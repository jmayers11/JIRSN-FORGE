using Forge.Kernel.Postgres;
using Forge.Workforce.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Workforce.Infrastructure;

internal sealed class PostgresTimesheetStore(PostgresUnitOfWork uow) : ITimesheetStore, ITimesheetLookup
{
    private const string CodeSql = "SELECT code, name, kind, contract_number, clin, active, created_by, created_at FROM workforce.charge_code";

    private const string SheetSql =
        "SELECT timesheet_id, person_id, week_start, status, signed_by, signed_at, approved_by, approved_at, returned_by, returned_at, return_reason FROM workforce.timesheet";

    private const string EntrySql =
        """
        SELECT entry_id, timesheet_id, work_date, order_number, sequence, charge_code, hours, note, state, recorded_by, recorded_at, late_reason,
               supersedes_entry_id, correction_reason
        FROM workforce.time_entry
        """;

    public async Task<bool> InsertChargeCodeAsync(ChargeCodeInfo code, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "INSERT INTO workforce.charge_code (code, name, kind, contract_number, clin, active, created_at, created_by) VALUES ($1, $2, $3, $4, $5, $6, $7, $8) ON CONFLICT DO NOTHING",
            code.Code, code.Name, code.Kind.ToString(), Text(code.ContractNumber), Text(code.Clin), code.Active, code.CreatedAt, code.CreatedBy);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task SetChargeCodeActiveAsync(string code, bool active, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command("UPDATE workforce.charge_code SET active = $2, changed_at = $3, changed_by = $4 WHERE code = $1", code, active, at, actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<TimesheetInfo> LockOrCreateTimesheetAsync(Guid timesheetId, Guid personId, DateOnly weekStart, CancellationToken ct)
    {
        await using (var insert = uow.Command(
            "INSERT INTO workforce.timesheet (timesheet_id, person_id, week_start, status) VALUES ($1, $2, $3, 'Open') ON CONFLICT (person_id, week_start) DO NOTHING",
            timesheetId, personId, weekStart))
        {
            await insert.ExecuteNonQueryAsync(ct);
        }

        Guid id;
        await using (var select = uow.Command("SELECT timesheet_id FROM workforce.timesheet WHERE person_id = $1 AND week_start = $2 FOR UPDATE", personId, weekStart))
        {
            id = (Guid)(await select.ExecuteScalarAsync(ct))!;
        }

        return (await FindTimesheetAsync(id, ct))!;
    }

    public async Task LockTimesheetAsync(Guid timesheetId, CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT timesheet_id FROM workforce.timesheet WHERE timesheet_id = $1 FOR UPDATE", timesheetId);
        await cmd.ExecuteScalarAsync(ct);
    }

    public async Task InsertEntryAsync(TimeEntryInfo entry, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            """
            INSERT INTO workforce.time_entry (entry_id, timesheet_id, work_date, order_number, sequence, charge_code, hours, note, state, recorded_at, recorded_by,
                                              late_reason, supersedes_entry_id, correction_reason)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, 'Active', $9, $10, $11, $12, $13)
            """,
            entry.EntryId, entry.TimesheetId, entry.WorkDate,
            new NpgsqlParameter { Value = (object?)entry.OrderNumber ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint },
            new NpgsqlParameter { Value = (object?)entry.Sequence ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer },
            Text(entry.ChargeCode), entry.Hours, Text(entry.Note), entry.RecordedAt, entry.RecordedBy, Text(entry.LateReason),
            new NpgsqlParameter { Value = (object?)entry.SupersedesEntryId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid }, Text(entry.CorrectionReason));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> RetireEntryAsync(Guid entryId, TimeEntryState state, string? correctionReason, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE workforce.time_entry SET state = $2, correction_reason = COALESCE($3, correction_reason) WHERE entry_id = $1 AND state = 'Active'",
            entryId, state.ToString(), Text(correctionReason));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task SetTimesheetStatusAsync(Guid timesheetId, TimesheetStatus status, DateTimeOffset at, string actor, string? reason, CancellationToken ct)
    {
        var sql = status switch
        {
            TimesheetStatus.Signed => "UPDATE workforce.timesheet SET status = 'Signed', signed_at = $2, signed_by = $3 WHERE timesheet_id = $1",
            TimesheetStatus.Approved => "UPDATE workforce.timesheet SET status = 'Approved', approved_at = $2, approved_by = $3 WHERE timesheet_id = $1",
            TimesheetStatus.Returned => "UPDATE workforce.timesheet SET status = 'Returned', returned_at = $2, returned_by = $3, return_reason = $4 WHERE timesheet_id = $1",
            _ => "UPDATE workforce.timesheet SET status = 'Open' WHERE timesheet_id = $1 AND $2::timestamptz IS NOT NULL AND $3::text IS NOT NULL",
        };
        await using var cmd = status == TimesheetStatus.Returned ? uow.Command(sql, timesheetId, at, actor, Text(reason)) : uow.Command(sql, timesheetId, at, actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task<IReadOnlyList<ChargeCodeInfo>> ListChargeCodesAsync(CancellationToken ct) => ReadAsync(CodeSql + " ORDER BY code", ReadCode, ct);

    public async Task<ChargeCodeInfo?> FindChargeCodeAsync(string code, CancellationToken ct) => (await ReadAsync(CodeSql + " WHERE code = $1", ReadCode, ct, code)).SingleOrDefault();

    public async Task<TimesheetInfo?> FindTimesheetAsync(Guid timesheetId, CancellationToken ct) =>
        (await WithEntriesAsync(await ReadAsync(SheetSql + " WHERE timesheet_id = $1", ReadSheet, ct, timesheetId), ct)).SingleOrDefault();

    public async Task<TimesheetInfo?> FindTimesheetAsync(Guid personId, DateOnly weekStart, CancellationToken ct) =>
        (await WithEntriesAsync(await ReadAsync(SheetSql + " WHERE person_id = $1 AND week_start = $2", ReadSheet, ct, personId, weekStart), ct)).SingleOrDefault();

    public async Task<IReadOnlyList<TimesheetInfo>> ListTimesheetsAsync(DateOnly? weekStart, Guid? personId, CancellationToken ct)
    {
        var sheets = (weekStart, personId) switch
        {
            ({ } w, { } p) => await ReadAsync(SheetSql + " WHERE week_start = $1 AND person_id = $2 ORDER BY week_start DESC", ReadSheet, ct, w, p),
            ({ } w, null) => await ReadAsync(SheetSql + " WHERE week_start = $1 ORDER BY person_id", ReadSheet, ct, w),
            (null, { } p) => await ReadAsync(SheetSql + " WHERE person_id = $1 ORDER BY week_start DESC", ReadSheet, ct, p),
            _ => await ReadAsync(SheetSql + " ORDER BY week_start DESC, person_id LIMIT 500", ReadSheet, ct),
        };
        return await WithEntriesAsync(sheets, ct);
    }

    public Task<IReadOnlyList<TimeEntryInfo>> ListOrderEntriesAsync(long orderNumber, bool approvedOnly, CancellationToken ct) =>
        ReadAsync(EntrySql + " WHERE order_number = $1 AND state = 'Active'"
            + (approvedOnly ? " AND timesheet_id IN (SELECT timesheet_id FROM workforce.timesheet WHERE status = 'Approved')" : string.Empty)
            + " ORDER BY work_date, recorded_at", ReadEntry, ct, orderNumber);

    private async Task<IReadOnlyList<TimesheetInfo>> WithEntriesAsync(IReadOnlyList<TimesheetInfo> sheets, CancellationToken ct)
    {
        if (sheets.Count == 0) return sheets;
        var ids = sheets.Select(s => s.TimesheetId).ToArray();
        var entries = (await ReadAsync(EntrySql + " WHERE timesheet_id = ANY($1) ORDER BY work_date, recorded_at", ReadEntry, ct, ids)).ToLookup(e => e.TimesheetId);
        return sheets.Select(s => s with { Entries = entries[s.TimesheetId].ToList() }).ToList();
    }

    private static ChargeCodeInfo ReadCode(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetString(1), Enum.Parse<ChargeCodeKind>(r.GetString(2)), Str(r, 3), Str(r, 4), r.GetBoolean(5), r.GetString(6), r.GetFieldValue<DateTimeOffset>(7));

    private static TimesheetInfo ReadSheet(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.GetFieldValue<DateOnly>(2), Enum.Parse<TimesheetStatus>(r.GetString(3)), Str(r, 4), At(r, 5), Str(r, 6), At(r, 7), Str(r, 8), At(r, 9), Str(r, 10), []);

    private static TimeEntryInfo ReadEntry(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.GetFieldValue<DateOnly>(2), r.IsDBNull(3) ? null : r.GetInt64(3), r.IsDBNull(4) ? null : r.GetInt32(4), Str(r, 5),
        r.GetDecimal(6) / 1.000000000000000000000000000000000m,Str(r, 7), Enum.Parse<TimeEntryState>(r.GetString(8)), r.GetString(9), r.GetFieldValue<DateTimeOffset>(10), Str(r, 11),
        r.IsDBNull(12) ? null : r.GetGuid(12), Str(r, 13));

    private static string? Str(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static DateTimeOffset? At(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);

    private static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };

    private async Task<IReadOnlyList<T>> ReadAsync<T>(string sql, Func<NpgsqlDataReader, T> map, CancellationToken ct, params object?[] args)
    {
        var list = new List<T>();
        await using var cmd = uow.Command(sql, args);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(map(reader));
        return list;
    }
}
