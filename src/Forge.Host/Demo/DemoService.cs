using System.Globalization;
using System.Text.Json;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Planning;
using Forge.Resources.Contracts;
using Forge.Resources.Infrastructure;
using Npgsql;

namespace Forge.Host.Demo;

public sealed record DemoDesignated(string DemoCode, string SiteCode, string DatabaseName) : IDomainEvent
{
    public static string EventType => nameof(DemoDesignated);

    public static int SchemaVersion => 1;
}

public sealed record DemoClockAdvanced(string DemoCode, long FromOffsetSeconds, long ToOffsetSeconds) : IDomainEvent
{
    public static string EventType => nameof(DemoClockAdvanced);

    public static int SchemaVersion => 1;
}

public sealed record DemoBaselineCaptured(string DemoCode, long BaselineId, string BaselineDatabase) : IDomainEvent
{
    public static string EventType => nameof(DemoBaselineCaptured);

    public static int SchemaVersion => 1;
}

public sealed record DemoReset(string DemoCode, string BaselineDatabase, string ArchivedAs, string Stage) : IDomainEvent
{
    public static string EventType => nameof(DemoReset);

    public static int SchemaVersion => 1;
}

/// <summary>Plant KPIs that a scenario is expected to move, read with the caller's own permissions.</summary>
public sealed record DemoKpis(
    DateTimeOffset SimulatedAt,
    int ReleasedOrders,
    int HeldOrders,
    int CompletedOrders,
    int LateReleasedOrders,
    int? OpenOperations,
    int? ExecutableOperations,
    IReadOnlyDictionary<string, int>? BlockedByConstraint,
    decimal StockReleased,
    decimal StockHold,
    decimal StockInspection);

public sealed record DemoKpiDelta(string Metric, decimal? Baseline, decimal? Current, decimal? Delta);

public sealed record DemoStatus(
    bool Enabled,
    bool Designated,
    string? DemoCode,
    string? SiteCode,
    long OffsetSeconds,
    DateTimeOffset RealNow,
    DateTimeOffset SimulatedNow,
    DemoBaseline? Baseline,
    string Label);

/// <summary>
/// The demo tenant boundary (Sprint 9): designation, the simulated clock, a saved whole-database baseline and a reset that swaps the
/// database for a fresh copy of the baseline. Nothing is deleted: the replaced database is renamed and kept with connections disabled.
/// Every operation is refused unless the host runs with Forge:Demo:Enabled and the database is designated and holds only the demo site.
/// </summary>
public sealed class DemoService(
    DemoOptions options,
    DemoClock clock,
    CommandPipeline pipeline,
    IKernelUnitOfWorkFactory unitOfWork,
    NpgsqlDataSource dataSource,
    SiteClockLoader siteClock,
    ReadinessService readiness,
    IForgeAuthorizer authorizer)
{
    private const string Source = "Demo";
    private const string Entity = "DemoTenant";
    public const int MaxAdvanceMinutes = 7 * 24 * 60;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool Enabled => options.Enabled;

    /// <summary>Reads the designated offset into the clock at startup and after a reset. A missing or undesignated database runs on real time.</summary>
    public async Task LoadClockAsync(CancellationToken ct)
    {
        if (!options.Enabled)
            return;
        try
        {
            await using var cmd = dataSource.CreateCommand("SELECT clock_offset_seconds FROM kernel.demo_tenant");
            clock.SetOffset(TimeSpan.FromSeconds(await cmd.ExecuteScalarAsync(ct) is long seconds ? seconds : 0));
        }
        catch (PostgresException)
        {
            clock.SetOffset(TimeSpan.Zero);
        }
    }

    public async Task<DemoStatus> StatusAsync(CancellationToken ct)
    {
        DemoTenant? tenant = null;
        DemoBaseline? baseline = null;
        if (options.Enabled)
        {
            await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
            tenant = await uow.Store<IDemoStore>().FindTenantAsync(ct);
            baseline = tenant is null ? null : await uow.Store<IDemoStore>().LatestBaselineAsync(ct);
        }

        var designated = options.Enabled && tenant is not null;
        return new DemoStatus(options.Enabled, designated, tenant?.DemoCode, tenant?.SiteCode, designated ? (long)clock.Offset.TotalSeconds : 0, clock.RealUtcNow,
            designated ? clock.GetUtcNow() : clock.RealUtcNow, baseline,
            designated ? "SIMULATED DEMO CLOCK" : options.Enabled ? "Demo mode is on, but this database is not designated a demo tenant." : "Real time.");
    }

    public Task<CommandResult> DesignateAsync(string demoCode, string siteCode, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var code = BusinessCode.Require(demoCode, "Demo code");
        var site = BusinessCode.Require(siteCode, "Site");
        var spec = new CommandSpec(Source, "DesignateDemoTenant", Permissions.DemoReset, ResourceRef.Of(Entity, code), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, new { code, site }, context, async (uow, now) =>
        {
            RequireEnabled();
            var store = uow.Store<IDemoStore>();
            if (await store.FindTenantAsync(ct) is { } existing)
                throw new CommandRejectedException("demo_already_designated", $"This database is already the {existing.DemoCode} demo tenant.");
            if (site == "PLANT1")
                throw new CommandRejectedException("live_site", "PLANT1 is the live sample plant; it is never a demo tenant.");
            var others = (await store.SiteCodesAsync(ct)).Where(s => s != site).ToList();
            if (others.Count > 0)
                throw new CommandRejectedException("not_demo_only", $"This database holds other sites ({string.Join(", ", others)}); only an empty or demo-only database can be designated.");
            var database = await store.CurrentDatabaseAsync(ct);
            await store.InsertTenantAsync(new DemoTenant(code, site, database, 0, context.Actor, now), ct);
            return new CommandOutcome(
                CommandResult.Ok(code, $"This database is now the {code} demo tenant for site {site}. Its clock can be advanced and it can be reset to a saved baseline."),
                [pipeline.Event(new DemoDesignated(code, site, database), context, Source, Entity, code)],
                new AuditChange(Entity, code, null, JsonSerializer.Serialize(new { code, site, database }, Json)));
        }, ct);
    }

    public async Task<CommandResult> AdvanceAsync(int minutes, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var spec = new CommandSpec(Source, "AdvanceDemoClock", Permissions.DemoControl, ResourceRef.Of(Entity, "clock"), ReasonRequired: true);
        long? applied = null;
        var result = await pipeline.ExecuteAsync(spec, new { minutes }, context, async (uow, now) =>
        {
            RequireEnabled();
            if (minutes is < 1 or > MaxAdvanceMinutes)
                throw new CommandRejectedException("invalid_advance", $"Advance the simulated clock by 1 to {MaxAdvanceMinutes} minutes; it never moves backward.");
            var tenant = await RequireDemoAsync(uow, ct);
            var to = tenant.ClockOffsetSeconds + minutes * 60L;
            if (!await uow.Store<IDemoStore>().SetOffsetAsync(tenant.ClockOffsetSeconds, to, ct))
                throw new CommandRejectedException("demo_clock_changed", "Someone else moved the simulated clock meanwhile; reload and try again.");
            applied = to;
            var at = now.AddMinutes(minutes);
            return new CommandOutcome(
                CommandResult.Ok(tenant.DemoCode, $"Simulated clock advanced {minutes} minutes to {at.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)}."),
                [pipeline.Event(new DemoClockAdvanced(tenant.DemoCode, tenant.ClockOffsetSeconds, to), context, Source, Entity, tenant.DemoCode)],
                new AuditChange(Entity, tenant.DemoCode, tenant.ClockOffsetSeconds.ToString(CultureInfo.InvariantCulture), to.ToString(CultureInfo.InvariantCulture)));
        }, ct);
        if (applied is { } offset)
            clock.SetOffset(TimeSpan.FromSeconds(offset));
        return result;
    }

    public async Task<DemoKpis> KpisAsync(ForgePrincipal principal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var now = clock.GetUtcNow();
        int released, held, completed, late;
        decimal stockReleased, stockHold, stockInspection;
        await using (var uow = await unitOfWork.BeginSnapshotAsync(ct))
        {
            var orders = (await uow.Store<IExecutionLookup>().ListOrdersAsync(ct))
                .Where(o => o.ProgramId is not { } program || principal.Programs.Contains(new ProgramId(program))).ToList();
            var today = await uow.SiteDateAsync(now, ct);
            released = orders.Count(o => o.Status == OrderStatus.Released);
            held = orders.Count(o => o.Status == OrderStatus.Held);
            completed = orders.Count(o => o.Status == OrderStatus.Completed);
            late = orders.Count(o => o.Status is OrderStatus.Released or OrderStatus.Held && o.DueDate < today);
            var positions = await ShelfLifeQueries.PositionsAsync(uow, null, now, ct);
            stockReleased = positions.Where(p => p.QualityStatus == StockStatus.Released).Sum(p => p.Quantity);
            stockHold = positions.Where(p => p.QualityStatus == StockStatus.Hold).Sum(p => p.Quantity);
            stockInspection = positions.Where(p => p.QualityStatus == StockStatus.Inspection).Sum(p => p.Quantity);
        }

        IReadOnlyList<OperationAnswer>? operations = null;
        if (principal.HasPermission(Permissions.ReadinessRead))
            operations = await readiness.EvaluateOperationsAsync(principal, null, ct);
        return new DemoKpis(now, released, held, completed, late, operations?.Count, operations?.Count(o => o.Executable),
            operations?.Where(o => !o.Executable).GroupBy(o => o.PrimaryConstraint ?? "Unknown", StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            stockReleased, stockHold, stockInspection);
    }

    public async Task<object> CompareAsync(ForgePrincipal principal, CancellationToken ct)
    {
        var status = await StatusAsync(ct);
        if (!status.Designated)
            throw new CommandRejectedException("not_demo", "This database is not a designated demo tenant.");
        var baseline = status.Baseline ?? throw new CommandRejectedException("no_baseline", "No baseline has been saved yet.");
        var before = JsonSerializer.Deserialize<DemoKpis>(baseline.Kpis, Json)!;
        var now = await KpisAsync(principal, ct);
        static DemoKpiDelta Row(string metric, decimal? a, decimal? b) => new(metric, a, b, a is { } x && b is { } y ? y - x : null);
        var rows = new List<DemoKpiDelta>
        {
            Row("Released orders", before.ReleasedOrders, now.ReleasedOrders),
            Row("Held orders", before.HeldOrders, now.HeldOrders),
            Row("Completed orders", before.CompletedOrders, now.CompletedOrders),
            Row("Late released or held orders", before.LateReleasedOrders, now.LateReleasedOrders),
            Row("Open operations", before.OpenOperations, now.OpenOperations),
            Row("Executable operations", before.ExecutableOperations, now.ExecutableOperations),
            Row("Released stock", before.StockReleased, now.StockReleased),
            Row("Stock on hold", before.StockHold, now.StockHold),
            Row("Stock in inspection", before.StockInspection, now.StockInspection),
        };
        foreach (var constraint in (before.BlockedByConstraint?.Keys ?? []).Union(now.BlockedByConstraint?.Keys ?? [], StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            rows.Add(Row($"Blocked by {constraint}", before.BlockedByConstraint?.GetValueOrDefault(constraint) ?? (before.BlockedByConstraint is null ? null : 0),
                now.BlockedByConstraint?.GetValueOrDefault(constraint) ?? (now.BlockedByConstraint is null ? null : 0)));
        }

        return new { baseline = new { baseline.BaselineId, baseline.CapturedAt, baseline.SimulatedAt, baseline.Reason }, current = now.SimulatedAt, rows,
            note = "Counts use your own read permissions; a metric you cannot read is blank, not zero." };
    }

    /// <summary>Copies the whole database as the baseline. Connections are paused for the copy; an older baseline is renamed, not dropped.</summary>
    public async Task<CommandResult> CaptureBaselineAsync(CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        RequireEnabled();
        var kpis = JsonSerializer.Serialize(await KpisAsync(context.Principal, ct), Json);
        DemoTenant tenant;
        string database;
        await using (var uow = await unitOfWork.BeginSnapshotAsync(ct))
        {
            tenant = await RequireDemoAsync(uow, ct);
            database = await uow.Store<IDemoStore>().CurrentDatabaseAsync(ct);
        }

        var decision = authorizer.Authorize(context.Principal, Permissions.DemoControl, ResourceRef.Of(Entity, tenant.DemoCode));
        if (!decision.IsPermitted)
            throw new ForgeAuthorizationException(decision);
        if (string.IsNullOrWhiteSpace(context.Reason))
            throw new CommandRejectedException("reason_required", "Say why this is the baseline.");

        var baselineName = Name(database, "_baseline");
        var simulatedAt = clock.GetUtcNow();
        await using (var admin = await MaintenanceAsync(ct))
        {
            var exists = await ExistsAsync(admin, baselineName, ct);
            if (exists)
                await ExecAsync(admin, $"ALTER DATABASE {Quote(baselineName)} RENAME TO {Quote(Name(database, "_baseline_" + Stamp()))}", ct);
            await PauseAsync(admin, database, ct);
            try
            {
                await ExecAsync(admin, $"CREATE DATABASE {Quote(baselineName)} TEMPLATE {Quote(database)}", ct);
                await ExecAsync(admin, $"ALTER DATABASE {Quote(baselineName)} ALLOW_CONNECTIONS false", ct);
            }
            finally
            {
                await ExecAsync(admin, $"ALTER DATABASE {Quote(database)} ALLOW_CONNECTIONS true", CancellationToken.None);
            }
        }

        var spec = new CommandSpec(Source, "CaptureDemoBaseline", Permissions.DemoControl, ResourceRef.Of(Entity, tenant.DemoCode), ReasonRequired: true);
        return await pipeline.ExecuteAsync(spec, new { baselineName, simulatedAt }, context, async (uow, now) =>
        {
            var id = await uow.Store<IDemoStore>().InsertBaselineAsync(
                new DemoBaseline(0, baselineName, now, simulatedAt, (long)clock.Offset.TotalSeconds, context.Actor, context.Reason!, kpis), ct);
            return new CommandOutcome(
                CommandResult.Ok(id.ToString(CultureInfo.InvariantCulture), $"Baseline {id} saved as database {baselineName}. Reset returns the demo to exactly this state."),
                [pipeline.Event(new DemoBaselineCaptured(tenant.DemoCode, id, baselineName), context, Source, Entity, tenant.DemoCode)],
                new AuditChange("DemoBaseline", id.ToString(CultureInfo.InvariantCulture), null, kpis));
        }, ct);
    }

    /// <summary>
    /// Replaces the demo database with a fresh copy of its baseline. The current database is renamed to an archive (connections disabled),
    /// never dropped, so its audit trail survives. Both databases record the reset.
    /// </summary>
    public async Task<CommandResult> ResetAsync(CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        RequireEnabled();
        DemoTenant tenant;
        DemoBaseline baseline;
        string database;
        await using (var uow = await unitOfWork.BeginSnapshotAsync(ct))
        {
            tenant = await RequireDemoAsync(uow, ct);
            database = await uow.Store<IDemoStore>().CurrentDatabaseAsync(ct);
            baseline = await uow.Store<IDemoStore>().LatestBaselineAsync(ct)
                ?? throw new CommandRejectedException("no_baseline", "Save a baseline before resetting the demo.");
        }

        if (tenant.DatabaseName != database)
            throw new CommandRejectedException("not_demo", $"This database ({database}) is a copy, not the designated demo database {tenant.DatabaseName}.");
        var archive = Name(database, "_archive_" + Stamp());
        await using (var admin = await MaintenanceAsync(ct))
        {
            if (!await ExistsAsync(admin, baseline.DatabaseName, ct))
                throw new CommandRejectedException("no_baseline", $"The baseline database {baseline.DatabaseName} no longer exists; save a new baseline.");
        }

        // Recorded in the database being archived first, so its own trail says where it went.
        var spec = new CommandSpec(Source, "ResetDemo", Permissions.DemoReset, ResourceRef.Of(Entity, tenant.DemoCode), ReasonRequired: true);
        var started = await pipeline.ExecuteAsync(spec, new { baseline = baseline.DatabaseName, archive, stage = "archive" }, context, (_, _) =>
            Task.FromResult(new CommandOutcome(
                CommandResult.Ok(tenant.DemoCode, $"Demo reset started; this database is archived as {archive}."),
                [pipeline.Event(new DemoReset(tenant.DemoCode, baseline.DatabaseName, archive, "Archived"), context, Source, Entity, tenant.DemoCode)],
                new AuditChange(Entity, tenant.DemoCode, database, archive))), ct);
        if (!started.Succeeded)
            return started;

        await using (var admin = await MaintenanceAsync(ct))
        {
            await PauseAsync(admin, database, ct);
            var renamed = false;
            try
            {
                await ExecAsync(admin, $"ALTER DATABASE {Quote(database)} RENAME TO {Quote(archive)}", ct);
                renamed = true;
                await ExecAsync(admin, $"CREATE DATABASE {Quote(database)} TEMPLATE {Quote(baseline.DatabaseName)}", ct);
            }
            catch
            {
                if (renamed)
                    await ExecAsync(admin, $"ALTER DATABASE {Quote(archive)} RENAME TO {Quote(database)}", CancellationToken.None);
                await ExecAsync(admin, $"ALTER DATABASE {Quote(database)} ALLOW_CONNECTIONS true", CancellationToken.None);
                throw;
            }
        }

        dataSource.Clear();
        await LoadClockAsync(ct);
        await siteClock.LoadAsync(ct);

        // The restored copy predates the baseline record and the reset: record both in it.
        var finish = context with { IdempotencyKey = context.IdempotencyKey + ":restored" };
        return await pipeline.ExecuteAsync(spec, new { baseline = baseline.DatabaseName, archive, stage = "restored" }, finish, async (uow, now) =>
        {
            await uow.Store<IDemoStore>().InsertBaselineAsync(baseline with { BaselineId = 0 }, ct);
            return new CommandOutcome(
                CommandResult.Ok(tenant.DemoCode, $"Demo reset to baseline {baseline.BaselineId} ({baseline.CapturedAt.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)}). The previous state is kept as database {archive}."),
                [pipeline.Event(new DemoReset(tenant.DemoCode, baseline.DatabaseName, archive, "Restored"), context, Source, Entity, tenant.DemoCode)],
                new AuditChange(Entity, tenant.DemoCode, archive, baseline.DatabaseName));
        }, ct);
    }

    private void RequireEnabled()
    {
        if (!options.Enabled)
            throw new CommandRejectedException("demo_disabled", "Demo controls are off on this server. They run only on a demo host against a designated demo database.");
    }

    private static async Task<DemoTenant> RequireDemoAsync(IKernelUnitOfWork uow, CancellationToken ct)
    {
        var store = uow.Store<IDemoStore>();
        var tenant = await store.FindTenantAsync(ct)
            ?? throw new CommandRejectedException("not_demo", "This database is not a designated demo tenant; its clock and data are never simulated or reset.");
        var others = (await store.SiteCodesAsync(ct)).Where(s => s != tenant.SiteCode).ToList();
        if (others.Count > 0)
            throw new CommandRejectedException("not_demo_only", $"The demo database now also holds {string.Join(", ", others)}; demo controls are refused.");
        return tenant;
    }

    private async Task<NpgsqlConnection> MaintenanceAsync(CancellationToken ct)
    {
        var builder = new NpgsqlConnectionStringBuilder(options.ConnectionString) { Database = options.MaintenanceDatabase, Pooling = false };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    /// <summary>Stops new connections to the database and ends the existing ones, including this host's pooled ones.</summary>
    private async Task PauseAsync(NpgsqlConnection admin, string database, CancellationToken ct)
    {
        await ExecAsync(admin, $"ALTER DATABASE {Quote(database)} ALLOW_CONNECTIONS false", ct);
        await using (var terminate = new NpgsqlCommand("SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE datname = $1 AND pid <> pg_backend_pid()", admin))
        {
            terminate.Parameters.Add(new NpgsqlParameter { Value = database });
            await terminate.ExecuteScalarAsync(ct);
        }

        dataSource.Clear();
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await using var left = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = $1 AND pid <> pg_backend_pid()", admin);
            left.Parameters.Add(new NpgsqlParameter { Value = database });
            if ((long)(await left.ExecuteScalarAsync(ct))! == 0)
                return;
            await Task.Delay(100, ct);
        }
    }

    private static async Task<bool> ExistsAsync(NpgsqlConnection admin, string name, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = $1)", admin);
        cmd.Parameters.Add(new NpgsqlParameter { Value = name });
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task ExecAsync(NpgsqlConnection admin, string sql, CancellationToken ct)
    {
#pragma warning disable CA2100 // Identifiers are quoted by Quote(); no value is concatenated.
        await using var cmd = new NpgsqlCommand(sql, admin);
#pragma warning restore CA2100
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private string Stamp() => clock.RealUtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    private static string Name(string database, string suffix) => (database.Length + suffix.Length > 63 ? database[..(63 - suffix.Length)] : database) + suffix;

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
