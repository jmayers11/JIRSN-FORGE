using Forge.Engineering.Contracts;
using Forge.Kernel.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Engineering.Infrastructure;

internal sealed class PostgresObsolescenceStore(PostgresUnitOfWork uow) : IObsolescenceStore, IObsolescenceLookup, IMroLookup
{
    private const string PolicySql =
        "SELECT policy_id, part_number, min_quantity, reorder_point, max_quantity, critical_spare, shelf_life_days, cleared, set_by, set_at, reason FROM engineering.item_mro";

    private const string RecordSql =
        """
        SELECT record_id, part_number, status, last_time_buy, end_of_life, end_of_support, evidence, recorded_by, recorded_at, replacement_part, replacement_status,
               replacement_proposed_by, replacement_proposed_at, replacement_decided_by, replacement_decided_at, replacement_reason
        FROM engineering.obsolescence
        """;

    public async Task InsertMroPolicyAsync(MroPolicyInfo p, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(p);
        await using var cmd = uow.Command(
            """
            INSERT INTO engineering.item_mro (part_number, min_quantity, reorder_point, max_quantity, critical_spare, shelf_life_days, cleared, set_by, set_at, reason)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
            """,
            p.PartNumber, p.MinQuantity, p.ReorderPoint, p.MaxQuantity, p.CriticalSpare, Int(p.ShelfLifeDays), p.Cleared, p.SetBy, p.SetAt, p.Reason);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> InsertObsolescenceAsync(ObsolescenceInfo r, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        await using var cmd = uow.Command(
            """
            INSERT INTO engineering.obsolescence (part_number, status, last_time_buy, end_of_life, end_of_support, evidence, recorded_by, recorded_at,
                replacement_part, replacement_status, replacement_proposed_by, replacement_proposed_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)
            RETURNING record_id
            """,
            r.PartNumber, r.Status.ToString(), Date(r.LastTimeBuy), Date(r.EndOfLife), Date(r.EndOfSupport), r.Evidence, r.RecordedBy, r.RecordedAt,
            Text(r.ReplacementPart), r.Replacement.ToString(), Text(r.ReplacementProposedBy), Time(r.ReplacementProposedAt));
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task UpdateReplacementAsync(ObsolescenceInfo r, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        await using var cmd = uow.Command(
            """
            UPDATE engineering.obsolescence SET replacement_part = $2, replacement_status = $3, replacement_proposed_by = $4, replacement_proposed_at = $5,
                replacement_decided_by = $6, replacement_decided_at = $7, replacement_reason = $8
            WHERE record_id = $1
            """,
            r.RecordId, Text(r.ReplacementPart), r.Replacement.ToString(), Text(r.ReplacementProposedBy), Time(r.ReplacementProposedAt),
            Text(r.ReplacementDecidedBy), Time(r.ReplacementDecidedAt), Text(r.ReplacementReason));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task<IReadOnlyList<MroPolicyInfo>> ListMroPoliciesAsync(CancellationToken ct) =>
        Read($"SELECT * FROM ({PolicySql.Replace("SELECT ", "SELECT DISTINCT ON (part_number) ", StringComparison.Ordinal)} ORDER BY part_number, policy_id DESC) p WHERE NOT cleared ORDER BY part_number", ReadPolicy, ct);

    public Task<IReadOnlyList<MroPolicyInfo>> MroHistoryAsync(string partNumber, CancellationToken ct) =>
        Read(PolicySql + " WHERE part_number = $1 ORDER BY policy_id DESC", ReadPolicy, ct, partNumber);

    public Task<IReadOnlyList<ObsolescenceInfo>> ListObsolescenceAsync(bool currentOnly, CancellationToken ct) =>
        currentOnly
            ? Read($"SELECT * FROM ({RecordSql.Replace("SELECT ", "SELECT DISTINCT ON (part_number) ", StringComparison.Ordinal)} ORDER BY part_number, record_id DESC) o ORDER BY record_id DESC", ReadRecord, ct)
            : Read(RecordSql + " ORDER BY record_id DESC", ReadRecord, ct);

    public async Task<ObsolescenceInfo?> FindObsolescenceAsync(long recordId, CancellationToken ct) =>
        (await Read(RecordSql + " WHERE record_id = $1", ReadRecord, ct, recordId)).SingleOrDefault();

    private static MroPolicyInfo ReadPolicy(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetString(1), Dec(r, 2), Dec(r, 3), Dec(r, 4), r.GetBoolean(5), r.IsDBNull(6) ? null : r.GetInt32(6), r.GetBoolean(7),
        r.GetString(8), r.GetFieldValue<DateTimeOffset>(9), r.GetString(10));

    private static ObsolescenceInfo ReadRecord(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetString(1), Enum.Parse<ObsolescenceStatus>(r.GetString(2)), OptDate(r, 3), OptDate(r, 4), OptDate(r, 5), r.GetString(6), r.GetString(7),
        r.GetFieldValue<DateTimeOffset>(8), Opt(r, 9), Enum.Parse<ReplacementStatus>(r.GetString(10)), Opt(r, 11), OptTime(r, 12), Opt(r, 13), OptTime(r, 14), Opt(r, 15));

    // numeric(18,6) comes back with six decimals; normalise so 4.000000 reads as 4.
    private static decimal Dec(NpgsqlDataReader r, int i) => r.GetDecimal(i) / 1.000000000000000000000000000000000m;

    private static string? Opt(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static DateOnly? OptDate(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateOnly>(i);

    private static DateTimeOffset? OptTime(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);

    private static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };

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
