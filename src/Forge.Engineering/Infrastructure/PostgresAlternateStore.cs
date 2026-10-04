using Forge.Engineering.Contracts;
using Forge.Kernel.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Engineering.Infrastructure;

internal sealed class PostgresAlternateStore(PostgresUnitOfWork uow) : IAlternateStore, IAlternateLookup
{
    private const string AlternateSql =
        """
        SELECT alternate_id, primary_part, alternate_part, kind, status, factor, program_id, customer, parent_part, asset_code, machine_code, operation_sequence,
               supplier_code, valid_from, valid_to, quality_notes, evidence, change_number, proposed_by, proposed_at, decided_by, decided_at, decision_reason,
               expired_by, expired_at, expired_reason
        FROM engineering.alternate
        """;

    public async Task<long> InsertAlternateAsync(AlternateInfo a, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(a);
        await using var cmd = uow.Command(
            """
            INSERT INTO engineering.alternate (primary_part, alternate_part, kind, status, factor, program_id, customer, parent_part, asset_code, machine_code,
                operation_sequence, supplier_code, valid_from, valid_to, quality_notes, evidence, change_number, proposed_by, proposed_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19)
            RETURNING alternate_id
            """,
            a.PrimaryPart, a.AlternatePart, a.Kind.ToString(), a.Status.ToString(), a.Factor, Uuid(a.ProgramId), Text(a.Customer), Text(a.ParentPart), Text(a.AssetCode),
            Text(a.MachineCode), Int(a.OperationSequence), Text(a.SupplierCode), Date(a.ValidFrom), Date(a.ValidTo), Text(a.QualityNotes), Text(a.Evidence),
            Text(a.ChangeNumber), a.ProposedBy, a.ProposedAt);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task UpdateAlternateAsync(AlternateInfo a, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(a);
        await using var cmd = uow.Command(
            """
            UPDATE engineering.alternate SET status = $2, decided_by = $3, decided_at = $4, decision_reason = $5, expired_by = $6, expired_at = $7, expired_reason = $8
            WHERE alternate_id = $1
            """,
            a.AlternateId, a.Status.ToString(), Text(a.DecidedBy), Time(a.DecidedAt), Text(a.DecisionReason), Text(a.ExpiredBy), Time(a.ExpiredAt), Text(a.ExpiredReason));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertLinePolicyAsync(string part, string revision, int line, SubstitutionPolicy policy, string actor, DateTimeOffset at, string reason, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "INSERT INTO engineering.bom_line_policy (part_number, revision, line_number, policy, set_by, set_at, reason) VALUES ($1, $2, $3, $4, $5, $6, $7)",
            part, revision, line, policy.ToString(), actor, at, reason);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task<IReadOnlyList<AlternateInfo>> ListAlternatesAsync(CancellationToken ct) => Read(AlternateSql + " ORDER BY alternate_id", ReadAlternate, ct);

    public async Task<AlternateInfo?> FindAlternateAsync(long alternateId, CancellationToken ct) =>
        (await Read(AlternateSql + " WHERE alternate_id = $1", ReadAlternate, ct, alternateId)).SingleOrDefault();

    private static AlternateInfo ReadAlternate(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), Enum.Parse<AlternateKind>(r.GetString(3)), Enum.Parse<AlternateStatus>(r.GetString(4)), r.GetDecimal(5) / 1.000000000000000000000000000000000m,
        r.IsDBNull(6) ? null : r.GetGuid(6), Opt(r, 7), Opt(r, 8), Opt(r, 9), Opt(r, 10), r.IsDBNull(11) ? null : r.GetInt32(11), Opt(r, 12),
        r.IsDBNull(13) ? null : r.GetFieldValue<DateOnly>(13), r.IsDBNull(14) ? null : r.GetFieldValue<DateOnly>(14), Opt(r, 15), Opt(r, 16), Opt(r, 17),
        r.GetString(18), r.GetFieldValue<DateTimeOffset>(19), Opt(r, 20), OptTime(r, 21), Opt(r, 22), Opt(r, 23), OptTime(r, 24), Opt(r, 25));

    private static string? Opt(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static DateTimeOffset? OptTime(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);

    private static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };

    private static NpgsqlParameter Uuid(Guid? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid };

    private static NpgsqlParameter Int(int? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer };

    private static NpgsqlParameter Date(DateOnly? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date };

    private static NpgsqlParameter Time(DateTimeOffset? value) =>
        new() { Value = (object?)value?.ToUniversalTime() ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz };

    private async Task<IReadOnlyList<T>> Read<T>(string sql, Func<NpgsqlDataReader, T> map, CancellationToken ct, params object?[] args)
    {
        var list = new List<T>();
        await using var cmd = uow.Command(sql, args);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(map(reader));
        }

        return list;
    }
}
