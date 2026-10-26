using System.Text.Json;
using Forge.Kernel.Postgres;
using Forge.Kernel.Serialization;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Planning.Infrastructure;

/// <summary>Replacement order proposals (Sprint 10 WW-S10-35): proposed once, then approved or rejected once.</summary>
internal sealed class PostgresReplacementOrderStore(PostgresUnitOfWork uow) : IReplacementOrderStore
{
    private const string Sql =
        """
        SELECT proposal_id, part_number, revision, quantity, due_date, priority, ncr_id, replaces_order_number, program_id, status, checks::text, checked_at,
               rationale, proposed_by, proposed_by_person_id, proposed_at, decided_by, decided_by_person_id, decided_at, decision_note, order_number, version
        FROM planning.replacement_order_proposal
        """;

    public async Task<IReadOnlyList<ReplacementOrderProposal>> ListAsync(bool openOnly, CancellationToken ct) =>
        await ReadAsync(Sql + " WHERE (NOT $1 OR status = 'Proposed') ORDER BY proposed_at, proposal_id", ct, openOnly);

    public async Task<ReplacementOrderProposal?> FindAsync(Guid proposalId, CancellationToken ct) =>
        (await ReadAsync(Sql + " WHERE proposal_id = $1", ct, proposalId)).SingleOrDefault();

    public async Task InsertAsync(ReplacementOrderProposal p, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(p);
        await using var cmd = uow.Command(
            """
            INSERT INTO planning.replacement_order_proposal (proposal_id, part_number, revision, quantity, due_date, priority, ncr_id, replaces_order_number, program_id,
                status, checks, checked_at, rationale, proposed_by, proposed_by_person_id, proposed_at, version)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11::jsonb, $12, $13, $14, $15, $16, $17)
            """,
            p.ProposalId, p.PartNumber, p.Revision, p.Quantity, p.DueDate, p.Priority, Bigint(p.NcrId), Bigint(p.ReplacesOrderNumber), Uuid(p.ProgramId),
            p.Status.ToString(), CanonicalJson.Serialize(p.Checks), p.CheckedAt, p.Rationale, p.ProposedBy, Uuid(p.ProposedByPersonId), p.ProposedAt, p.Version);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> DecideAsync(ReplacementOrderProposal p, int expectedVersion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(p);
        await using var cmd = uow.Command(
            """
            UPDATE planning.replacement_order_proposal SET status = $2, decided_by = $3, decided_by_person_id = $4, decided_at = $5, decision_note = $6, order_number = $7, version = $8
            WHERE proposal_id = $1 AND status = 'Proposed' AND version = $9
            """,
            p.ProposalId, p.Status.ToString(), (object?)p.DecidedBy ?? DBNull.Value, Uuid(p.DecidedByPersonId),
            new NpgsqlParameter { Value = (object?)p.DecidedAt ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz },
            (object?)p.DecisionNote ?? DBNull.Value, Bigint(p.OrderNumber), p.Version, expectedVersion);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    private static NpgsqlParameter Uuid(Guid? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid };

    private static NpgsqlParameter Bigint(long? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint };

    private async Task<IReadOnlyList<ReplacementOrderProposal>> ReadAsync(string sql, CancellationToken ct, params object[] args)
    {
        var list = new List<ReplacementOrderProposal>();
        await using var cmd = uow.Command(sql, args);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new ReplacementOrderProposal(
                r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetDecimal(3), r.GetFieldValue<DateOnly>(4), r.GetInt32(5),
                r.IsDBNull(6) ? null : r.GetInt64(6), r.IsDBNull(7) ? null : r.GetInt64(7), r.IsDBNull(8) ? null : r.GetGuid(8),
                Enum.Parse<ReplacementOrderStatus>(r.GetString(9)),
                JsonSerializer.Deserialize<List<ReplacementOperationCheck>>(r.GetString(10), CanonicalJson.Options)!,
                r.GetFieldValue<DateTimeOffset>(11), r.GetString(12), r.GetString(13), r.IsDBNull(14) ? null : r.GetGuid(14), r.GetFieldValue<DateTimeOffset>(15),
                r.IsDBNull(16) ? null : r.GetString(16), r.IsDBNull(17) ? null : r.GetGuid(17), r.IsDBNull(18) ? null : r.GetFieldValue<DateTimeOffset>(18),
                r.IsDBNull(19) ? null : r.GetString(19), r.IsDBNull(20) ? null : r.GetInt64(20), r.GetInt32(21)));
        }

        return list;
    }
}
