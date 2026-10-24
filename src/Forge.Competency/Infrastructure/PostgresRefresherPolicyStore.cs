using Forge.Competency.Contracts;
using Forge.Kernel.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Competency.Infrastructure;

internal sealed class PostgresRefresherPolicyStore(PostgresUnitOfWork uow) : IRefresherPolicyStore, IRefresherPolicyLookup
{
    public async Task InsertRefresherPolicyAsync(RefresherPolicy policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);
        await using var cmd = uow.Command(
            "INSERT INTO competency.refresher_policy (code, refresher_after_days, reacclimation_minutes, set_by, set_at) VALUES ($1, $2, $3, $4, $5)",
            policy.Code,
            new NpgsqlParameter { Value = (object?)policy.RefresherAfterDays ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer },
            new NpgsqlParameter { Value = (object?)policy.ReacclimationMinutes ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer },
            policy.SetBy, policy.SetAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<RefresherPolicy>> ListRefresherPoliciesAsync(CancellationToken ct)
    {
        var rows = new List<RefresherPolicy>();
        await using var cmd = uow.Command(
            """
            SELECT DISTINCT ON (code) code, refresher_after_days, reacclimation_minutes, set_by, set_at
            FROM competency.refresher_policy ORDER BY code, policy_id DESC
            """);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(1) && reader.IsDBNull(2)) continue;
            rows.Add(new RefresherPolicy(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return rows;
    }
}
