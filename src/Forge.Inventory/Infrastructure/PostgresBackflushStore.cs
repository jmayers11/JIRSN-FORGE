using Forge.Inventory.Contracts;
using Forge.Kernel.Postgres;
using Npgsql;

namespace Forge.Inventory.Infrastructure;

internal sealed class PostgresBackflushStore(PostgresUnitOfWork uow) : IBackflushLookup, IBackflushStore
{
    private const string ExceptionSql =
        """
        SELECT exception_id, order_number, sequence, report_id, part_number, quantity, deducted, location_code, problem, status, raised_at, resolved_by, resolved_at, resolution_note
        FROM inventory.backflush_exception
        """;

    public async Task<IReadOnlyList<BackflushSetting>> ListBackflushSettingsAsync(CancellationToken ct)
    {
        var list = new List<BackflushSetting>();
        await using var cmd = uow.Command("SELECT part_number, enabled, location_code, updated_by, updated_at FROM inventory.backflush_setting ORDER BY part_number");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new BackflushSetting(r.GetString(0), r.GetBoolean(1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateTimeOffset>(4)));
        return list;
    }

    public Task<IReadOnlyList<BackflushException>> ListBackflushExceptionsAsync(bool openOnly, CancellationToken ct) =>
        ReadAsync(ExceptionSql + " WHERE ($1 = false OR status = 'Open') ORDER BY exception_id DESC LIMIT 500", ct, openOnly);

    public async Task<BackflushException?> FindBackflushExceptionAsync(long exceptionId, CancellationToken ct) =>
        (await ReadAsync(ExceptionSql + " WHERE exception_id = $1", ct, exceptionId)).SingleOrDefault();

    public async Task UpsertBackflushSettingAsync(BackflushSetting s, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            """
            INSERT INTO inventory.backflush_setting (part_number, enabled, location_code, updated_by, updated_at) VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT (part_number) DO UPDATE SET enabled = EXCLUDED.enabled, location_code = EXCLUDED.location_code, updated_by = EXCLUDED.updated_by, updated_at = EXCLUDED.updated_at
            """,
            s.PartNumber, s.Enabled, s.LocationCode, s.UpdatedBy, s.UpdatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> InsertBackflushExceptionAsync(BackflushException e, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            """
            INSERT INTO inventory.backflush_exception (order_number, sequence, report_id, part_number, quantity, deducted, location_code, problem, status, raised_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, 'Open', $9)
            ON CONFLICT (report_id, part_number) DO UPDATE SET report_id = EXCLUDED.report_id
            RETURNING exception_id
            """,
            e.OrderNumber, e.Sequence, e.ReportId, e.PartNumber, e.Quantity, e.Deducted, e.LocationCode, e.Problem, e.RaisedAt);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task ResolveBackflushExceptionAsync(long exceptionId, string actor, DateTimeOffset at, string note, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE inventory.backflush_exception SET status = 'Resolved', resolved_by = $2, resolved_at = $3, resolution_note = $4 WHERE exception_id = $1 AND status = 'Open'",
            exceptionId, actor, at, note);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<IReadOnlyList<BackflushException>> ReadAsync(string sql, CancellationToken ct, params object?[] args)
    {
        var list = new List<BackflushException>();
        await using var cmd = uow.Command(sql, args);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    private static BackflushException Read(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetInt32(2), r.GetInt64(3), r.GetString(4), r.GetDecimal(5), r.GetDecimal(6), r.GetString(7), r.GetString(8),
        Enum.Parse<BackflushExceptionStatus>(r.GetString(9)), r.GetFieldValue<DateTimeOffset>(10), r.IsDBNull(11) ? null : r.GetString(11),
        r.IsDBNull(12) ? null : r.GetFieldValue<DateTimeOffset>(12), r.IsDBNull(13) ? null : r.GetString(13));
}
