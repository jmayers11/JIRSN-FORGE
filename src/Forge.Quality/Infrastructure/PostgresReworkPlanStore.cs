using System.Text.Json;
using Forge.Kernel.Postgres;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Quality.Infrastructure;

/// <summary>Rework plans (Sprint 10): proposed once, then approved or rejected once.</summary>
internal sealed class PostgresReworkPlanStore(PostgresUnitOfWork uow) : IReworkPlanStore
{
    private const string Sql =
        """
        SELECT plan_id, rework_id, order_number, status, operations::text, materials::text, trainees, rationale, proposed_by, proposed_by_person_id, proposed_at,
               decided_by, decided_by_person_id, decided_at, decision_note, version
        FROM quality.rework_plan
        """;

    public async Task<IReadOnlyList<ReworkPlan>> ListPlansAsync(long? orderNumber, CancellationToken ct) =>
        await ReadAsync(Sql + " WHERE ($1::bigint IS NULL OR order_number = $1) ORDER BY proposed_at, plan_id", ct,
            new NpgsqlParameter { Value = (object?)orderNumber ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });

    public async Task<ReworkPlan?> FindPlanAsync(Guid planId, CancellationToken ct) =>
        (await ReadAsync(Sql + " WHERE plan_id = $1", ct, planId)).SingleOrDefault();

    public async Task<bool> InsertPlanAsync(ReworkPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await using var cmd = uow.Command(
            """
            INSERT INTO quality.rework_plan (plan_id, rework_id, order_number, status, operations, materials, trainees, rationale, proposed_by, proposed_by_person_id, proposed_at, version)
            VALUES ($1, $2, $3, $4, $5::jsonb, $6::jsonb, $7, $8, $9, $10, $11, $12)
            ON CONFLICT (rework_id) WHERE status = 'Proposed' DO NOTHING
            """,
            plan.PlanId, plan.ReworkId, plan.OrderNumber, plan.Status.ToString(), CanonicalJson.Serialize(plan.Operations), CanonicalJson.Serialize(plan.Materials),
            plan.Trainees.ToArray(), plan.Rationale, plan.ProposedBy, Uuid(plan.ProposedByPersonId), plan.ProposedAt, plan.Version);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> DecidePlanAsync(ReworkPlan plan, int expectedVersion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await using var cmd = uow.Command(
            """
            UPDATE quality.rework_plan SET status = $2, decided_by = $3, decided_by_person_id = $4, decided_at = $5, decision_note = $6, version = $7
            WHERE plan_id = $1 AND status = 'Proposed' AND version = $8
            """,
            plan.PlanId, plan.Status.ToString(), (object?)plan.DecidedBy ?? DBNull.Value, Uuid(plan.DecidedByPersonId),
            new NpgsqlParameter { Value = (object?)plan.DecidedAt ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz },
            (object?)plan.DecisionNote ?? DBNull.Value, plan.Version, expectedVersion);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    private static NpgsqlParameter Uuid(Guid? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid };

    private async Task<IReadOnlyList<ReworkPlan>> ReadAsync(string sql, CancellationToken ct, params object[] args)
    {
        var list = new List<ReworkPlan>();
        await using var cmd = uow.Command(sql, args);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new ReworkPlan(
                r.GetGuid(0), r.GetInt64(1), r.GetInt64(2), Enum.Parse<ReworkPlanStatus>(r.GetString(3)),
                JsonSerializer.Deserialize<List<ReworkPlanOperation>>(r.GetString(4), CanonicalJson.Options)!,
                JsonSerializer.Deserialize<List<ReworkPlanMaterial>>(r.GetString(5), CanonicalJson.Options)!,
                r.GetFieldValue<Guid[]>(6), r.GetString(7), r.GetString(8), r.IsDBNull(9) ? null : r.GetGuid(9), r.GetFieldValue<DateTimeOffset>(10),
                r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetGuid(12), r.IsDBNull(13) ? null : r.GetFieldValue<DateTimeOffset>(13),
                r.IsDBNull(14) ? null : r.GetString(14), r.GetInt32(15)));
        }

        return list;
    }
}
