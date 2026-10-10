using Forge.Inventory.Contracts;
using Forge.Kernel.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Inventory.Infrastructure;

/// <summary>Shelf-life rules, lot dates, extensions, retests and storage excursions (V3.6).</summary>
internal sealed class PostgresShelfLifeStore(PostgresUnitOfWork uow) : IShelfLifeLookup, IShelfLifeStore
{
    private const string RuleSql =
        """
        SELECT part_number, shelf_life_days, basis, retest_days, open_life_hours, storage_min_c, storage_max_c, storage_note, selection, reason, set_by, set_at
        FROM inventory.shelf_life_rule
        """;

    public async Task<ShelfLifeRule?> FindRuleAsync(string partNumber, CancellationToken ct) =>
        (await Read(RuleSql + " WHERE part_number = $1 ORDER BY rule_id DESC LIMIT 1", ReadRule, ct, partNumber)).SingleOrDefault();

    public Task<IReadOnlyList<ShelfLifeRule>> ListRulesAsync(CancellationToken ct) =>
        Read("SELECT DISTINCT ON (part_number) " + RuleSql["SELECT ".Length..] + " ORDER BY part_number, rule_id DESC", ReadRule, ct);

    public Task<IReadOnlyList<ShelfLifeExtension>> ListExtensionsAsync(string? partNumber, CancellationToken ct) =>
        Read(
            """
            SELECT extension_id, part_number, lot_number, previous_expires, new_expires, evidence, requested_by, requested_at, approved, decided_by, decided_at
            FROM inventory.shelf_life_extension WHERE ($1::text IS NULL OR part_number = $1) ORDER BY extension_id
            """,
            r => new ShelfLifeExtension(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetFieldValue<DateOnly>(3), r.GetFieldValue<DateOnly>(4), r.GetString(5), r.GetString(6),
                r.GetFieldValue<DateTimeOffset>(7), r.IsDBNull(8) ? null : r.GetBoolean(8), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10)),
            ct,
            Text(partNumber));

    public Task<IReadOnlyList<RetestInfo>> ListRetestsAsync(string? partNumber, CancellationToken ct) =>
        Read(
            "SELECT retest_id, part_number, lot_number, passed, next_due, evidence, tested_by, tested_at FROM inventory.retest WHERE ($1::text IS NULL OR part_number = $1) ORDER BY retest_id",
            r => new RetestInfo(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), r.IsDBNull(4) ? null : r.GetFieldValue<DateOnly>(4), r.GetString(5), r.GetString(6), r.GetFieldValue<DateTimeOffset>(7)),
            ct,
            Text(partNumber));

    public Task<IReadOnlyList<StorageExcursion>> ListExcursionsAsync(CancellationToken ct) =>
        Read(
            "SELECT excursion_id, location_code, condition, measured_c, started_at, ended_at, note, recorded_by, recorded_at FROM inventory.storage_excursion ORDER BY started_at, excursion_id",
            r => new StorageExcursion(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetDecimal(3), r.GetFieldValue<DateTimeOffset>(4), r.GetFieldValue<DateTimeOffset>(5),
                r.IsDBNull(6) ? null : r.GetString(6), r.GetString(7), r.GetFieldValue<DateTimeOffset>(8)),
            ct);

    public Task<IReadOnlyList<(string PartNumber, string LotNumber, string Location, DateTimeOffset From, DateTimeOffset? Until)>> ListLotResidenceAsync(string? partNumber, CancellationToken ct) =>
        Read(
            """
            WITH moves AS (
                SELECT part_number, lot_number, to_location AS loc, at, quantity AS q FROM inventory.transaction WHERE to_location IS NOT NULL AND lot_number IS NOT NULL
                UNION ALL
                SELECT part_number, lot_number, from_location, at, -quantity FROM inventory.transaction WHERE from_location IS NOT NULL AND lot_number IS NOT NULL)
            SELECT part_number, lot_number, loc, min(at) FILTER (WHERE q > 0), CASE WHEN sum(q) > 0 THEN NULL ELSE max(at) END
            FROM moves WHERE ($1::text IS NULL OR part_number = $1)
            GROUP BY 1, 2, 3 HAVING min(at) FILTER (WHERE q > 0) IS NOT NULL
            ORDER BY 1, 2, 3
            """,
            r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3), r.IsDBNull(4) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(4)),
            ct,
            Text(partNumber));

    public async Task InsertRuleAsync(ShelfLifeRule rule, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rule);
        await using var cmd = uow.Command(
            """
            INSERT INTO inventory.shelf_life_rule (part_number, shelf_life_days, basis, retest_days, open_life_hours, storage_min_c, storage_max_c, storage_note, selection, reason, set_by, set_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)
            """,
            rule.PartNumber, Int(rule.ShelfLifeDays), rule.Basis.ToString(), Int(rule.RetestDays), Int(rule.OpenLifeHours), Num(rule.StorageMinC), Num(rule.StorageMaxC), Text(rule.StorageNote),
            rule.Selection.ToString(), rule.Reason, rule.SetBy, rule.SetAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetLotDatesAsync(string partNumber, string lotNumber, DateOnly? manufacturedOn, DateOnly? expiresOn, DateOnly? retestDue, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            """
            UPDATE inventory.lot SET manufactured_on = COALESCE(manufactured_on, $3), expires_on = COALESCE(expires_on, $4), retest_due = COALESCE(retest_due, $5)
            WHERE part_number = $1 AND lot_number = $2
            """,
            partNumber, lotNumber, Date(manufacturedOn), Date(expiresOn), Date(retestDue));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InheritLotDatesAsync(string partNumber, IReadOnlyList<string> sourceLots, string lotNumber, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sourceLots);
        await using var cmd = uow.Command(
            """
            UPDATE inventory.lot t SET
                manufactured_on = COALESCE(t.manufactured_on, s.manufactured_on), expires_on = COALESCE(t.expires_on, s.expires_on),
                retest_due = COALESCE(t.retest_due, s.retest_due), opened_at = COALESCE(t.opened_at, s.opened_at), opened_by = COALESCE(t.opened_by, s.opened_by)
            FROM (SELECT min(manufactured_on) AS manufactured_on, min(expires_on) AS expires_on, min(retest_due) AS retest_due, min(opened_at) AS opened_at,
                         (array_agg(opened_by ORDER BY opened_at) FILTER (WHERE opened_at IS NOT NULL))[1] AS opened_by
                  FROM inventory.lot WHERE part_number = $1 AND lot_number = ANY($2)) s
            WHERE t.part_number = $1 AND t.lot_number = $3
            """,
            partNumber, sourceLots.ToArray(), lotNumber);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task OpenLotAsync(string partNumber, string lotNumber, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE inventory.lot SET opened_at = $3, opened_by = $4 WHERE part_number = $1 AND lot_number = $2 AND opened_at IS NULL", partNumber, lotNumber, at, actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> InsertExtensionAsync(ShelfLifeExtension extension, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(extension);
        await using var cmd = uow.Command(
            """
            INSERT INTO inventory.shelf_life_extension (part_number, lot_number, previous_expires, new_expires, evidence, requested_by, requested_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7) RETURNING extension_id
            """,
            extension.PartNumber, extension.LotNumber, extension.PreviousExpires, extension.NewExpires, extension.Evidence, extension.RequestedBy, extension.RequestedAt);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task DecideExtensionAsync(long extensionId, bool approved, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE inventory.shelf_life_extension SET approved = $2, decided_at = $3, decided_by = $4 WHERE extension_id = $1 AND approved IS NULL", extensionId, approved, at, actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> InsertRetestAsync(RetestInfo retest, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(retest);
        await using var cmd = uow.Command(
            "INSERT INTO inventory.retest (part_number, lot_number, passed, next_due, evidence, tested_by, tested_at) VALUES ($1, $2, $3, $4, $5, $6, $7) RETURNING retest_id",
            retest.PartNumber, retest.LotNumber, retest.Passed, Date(retest.NextDue), retest.Evidence, retest.TestedBy, retest.TestedAt);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<long> InsertExcursionAsync(StorageExcursion excursion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(excursion);
        await using var cmd = uow.Command(
            """
            INSERT INTO inventory.storage_excursion (location_code, condition, measured_c, started_at, ended_at, note, recorded_by, recorded_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8) RETURNING excursion_id
            """,
            excursion.LocationCode, excursion.Condition, Num(excursion.MeasuredC), excursion.StartedAt, excursion.EndedAt, Text(excursion.Note), excursion.RecordedBy, excursion.RecordedAt);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static ShelfLifeRule ReadRule(NpgsqlDataReader r) => new(
        r.GetString(0), r.IsDBNull(1) ? null : r.GetInt32(1), Enum.Parse<ShelfLifeBasis>(r.GetString(2)), r.IsDBNull(3) ? null : r.GetInt32(3), r.IsDBNull(4) ? null : r.GetInt32(4),
        r.IsDBNull(5) ? null : r.GetDecimal(5), r.IsDBNull(6) ? null : r.GetDecimal(6), r.IsDBNull(7) ? null : r.GetString(7), Enum.Parse<SelectionRule>(r.GetString(8)),
        r.GetString(9), r.GetString(10), r.GetFieldValue<DateTimeOffset>(11));

    private static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };

    private static NpgsqlParameter Int(int? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer };

    private static NpgsqlParameter Num(decimal? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Numeric };

    private static NpgsqlParameter Date(DateOnly? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date };

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
