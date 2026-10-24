using Forge.Kernel.Postgres;
using Npgsql;

namespace Forge.Host.Demo;

public sealed record DemoTenant(string DemoCode, string SiteCode, string DatabaseName, long ClockOffsetSeconds, string DesignatedBy, DateTimeOffset DesignatedAt);

public sealed record DemoBaseline(long BaselineId, string DatabaseName, DateTimeOffset CapturedAt, DateTimeOffset SimulatedAt, long ClockOffsetSeconds,
    string CapturedBy, string Reason, string Kpis);

public interface IDemoStore
{
    Task<DemoTenant?> FindTenantAsync(CancellationToken ct);

    Task InsertTenantAsync(DemoTenant tenant, CancellationToken ct);

    /// <summary>Moves the clock forward; false when the offset changed since the caller read it.</summary>
    Task<bool> SetOffsetAsync(long expected, long offset, CancellationToken ct);

    Task<string> CurrentDatabaseAsync(CancellationToken ct);

    Task<IReadOnlyList<string>> SiteCodesAsync(CancellationToken ct);

    Task<long> InsertBaselineAsync(DemoBaseline baseline, CancellationToken ct);

    Task<DemoBaseline?> LatestBaselineAsync(CancellationToken ct);
}

internal sealed class DemoStore(PostgresUnitOfWork uow) : IDemoStore
{
    public async Task<DemoTenant?> FindTenantAsync(CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT demo_code, site_code, database_name, clock_offset_seconds, designated_by, designated_at FROM kernel.demo_tenant");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new DemoTenant(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetString(4), r.GetFieldValue<DateTimeOffset>(5))
            : null;
    }

    public async Task InsertTenantAsync(DemoTenant tenant, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        await using var cmd = uow.Command(
            "INSERT INTO kernel.demo_tenant (demo_code, site_code, database_name, clock_offset_seconds, designated_by, designated_at) VALUES ($1, $2, $3, $4, $5, $6)",
            tenant.DemoCode, tenant.SiteCode, tenant.DatabaseName, tenant.ClockOffsetSeconds, tenant.DesignatedBy, tenant.DesignatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> SetOffsetAsync(long expected, long offset, CancellationToken ct)
    {
        await using var cmd = uow.Command("UPDATE kernel.demo_tenant SET clock_offset_seconds = $2 WHERE clock_offset_seconds = $1", expected, offset);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<string> CurrentDatabaseAsync(CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT current_database()");
        return (string)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlyList<string>> SiteCodesAsync(CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT code FROM resources.site ORDER BY code");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var codes = new List<string>();
        while (await r.ReadAsync(ct)) codes.Add(r.GetString(0));
        return codes;
    }

    public async Task<long> InsertBaselineAsync(DemoBaseline baseline, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        await using var cmd = uow.Command(
            """
            INSERT INTO kernel.demo_baseline (database_name, captured_at, simulated_at, clock_offset_seconds, captured_by, reason, kpis)
            VALUES ($1, $2, $3, $4, $5, $6, $7) RETURNING baseline_id
            """,
            baseline.DatabaseName, baseline.CapturedAt, baseline.SimulatedAt, baseline.ClockOffsetSeconds, baseline.CapturedBy, baseline.Reason,
            new NpgsqlParameter { Value = baseline.Kpis, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb });
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<DemoBaseline?> LatestBaselineAsync(CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "SELECT baseline_id, database_name, captured_at, simulated_at, clock_offset_seconds, captured_by, reason, kpis::text FROM kernel.demo_baseline ORDER BY baseline_id DESC LIMIT 1");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new DemoBaseline(r.GetInt64(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2), r.GetFieldValue<DateTimeOffset>(3), r.GetInt64(4), r.GetString(5), r.GetString(6), r.GetString(7))
            : null;
    }
}
