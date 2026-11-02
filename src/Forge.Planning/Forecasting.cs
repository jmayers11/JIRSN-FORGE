using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Resources.Contracts;

namespace Forge.Planning;

public enum DemandHistorySource
{
    /// <summary>Made up by Forge to try forecasting before real history exists (owner decision 2026-11-02). Never real demand.</summary>
    Simulated,

    /// <summary>Typed or pasted in from another system.</summary>
    Imported,
}

public sealed record DemandHistoryRow(string PartNumber, DateOnly Month, decimal Quantity, DemandHistorySource Source, string RecordedBy, DateTimeOffset RecordedAt);

/// <param name="Horizon">Months forecast ahead.</param>
/// <param name="Holdout">Most recent months held out to test each method.</param>
/// <param name="IntervalPercent">How sure the likely range is (80, 90 or 95).</param>
/// <param name="UseSimulated">Whether simulated history counts. Turn it off once real history exists.</param>
public sealed record ForecastPolicy(int Horizon, int Holdout, int IntervalPercent, bool UseSimulated, string SetBy, DateTimeOffset SetAt)
{
    public static ForecastPolicy Default { get; } = new(6, 6, 80, true, "system:default", DateTimeOffset.UnixEpoch);
}

/// <param name="Simulated">Some of the history behind this run was simulated.</param>
public sealed record ForecastRun(long RunId, DateTimeOffset RunAt, string RunBy, bool Simulated, int Horizon, int Holdout, int IntervalPercent, DateOnly FirstMonth);

/// <summary>One part's forecast in a run, and whether a planner approved it into demand.</summary>
public sealed record ForecastLine(long RunId, PartForecast Forecast, bool Simulated, string? ApprovedBy, DateTimeOffset? ApprovedAt);

public interface IForecastStore
{
    Task<IReadOnlyList<DemandHistoryRow>> ListHistoryAsync(bool includeSimulated, CancellationToken ct);

    Task UpsertHistoryAsync(IReadOnlyList<DemandHistoryRow> rows, CancellationToken ct);

    Task<int> DeleteHistoryAsync(DemandHistorySource source, CancellationToken ct);

    Task<ForecastPolicy> ForecastPolicyAsync(CancellationToken ct);

    Task InsertForecastPolicyAsync(ForecastPolicy policy, CancellationToken ct);

    Task<long> InsertRunAsync(ForecastRun run, IReadOnlyList<ForecastLine> lines, CancellationToken ct);

    Task<ForecastRun?> LatestRunAsync(CancellationToken ct);

    Task<ForecastRun?> FindRunAsync(long runId, CancellationToken ct);

    Task<IReadOnlyList<ForecastLine>> ListLinesAsync(long runId, CancellationToken ct);

    Task MarkApprovedAsync(long runId, string partNumber, string actor, DateTimeOffset at, CancellationToken ct);
}

public sealed record GenerateSimulatedHistory(int Months, int Seed);

public sealed record ClearSimulatedHistory;

public sealed record ImportDemandHistory(IReadOnlyList<(string PartNumber, DateOnly Month, decimal Quantity)> Rows);

public sealed record SetForecastPolicy(int Horizon, int Holdout, int IntervalPercent, bool UseSimulated);

public sealed record RunForecast;

public sealed record ApproveForecast(long RunId, string PartNumber);

public sealed record ForecastChanged(string Change, string Message) : IDomainEvent
{
    public static string EventType => nameof(ForecastChanged);

    public static int SchemaVersion => 1;
}

/// <summary>What a part's history looks like before forecasting: months, total and where it came from.</summary>
public sealed record HistorySummary(string PartNumber, string? Description, int Months, decimal Total, int SimulatedMonths, int ImportedMonths, int ShippedMonths, IReadOnlyList<MonthlyDemand> Series,
    IReadOnlyList<HistoryFlag> Flags);

/// <summary>
/// Demand forecasting (owner decisions 2026-11-02): monthly buckets, six months ahead, every part with history; history from shipments,
/// imported months and (until real history exists) simulated months, each marked; data checked first; methods tested on held-out months;
/// a forecast is only a suggestion until a planner approves it, which records forecast demand net of customer orders already in that month.
/// </summary>
public sealed class ForecastService(CommandPipeline pipeline)
{
    private const string Entity = "Forecast";

    public static async Task<IReadOnlyList<HistorySummary>> HistoryAsync(IKernelUnitOfWork uow, DateTimeOffset now, bool includeSimulated, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var zone = await uow.SiteTimeZoneAsync(ct);
        var through = ForecastEngine.MonthOf(SiteTime.DateAtOrUtc(now, zone)).AddMonths(-1); // complete months only
        var rows = await uow.Store<IForecastStore>().ListHistoryAsync(includeSimulated, ct);
        var shipped = (await uow.Store<IInventoryLookup>().ListShipmentsAsync(true, ct)).Where(s => s.Status == ShipmentStatus.Shipped && s.ShippedAt is not null)
            .SelectMany(s => s.Lines.Select(l => (l.PartNumber, Month: ForecastEngine.MonthOf(SiteTime.DateAtOrUtc(s.ShippedAt!.Value, zone)), l.Quantity))).ToList();
        var parts = rows.Select(r => r.PartNumber).Concat(shipped.Select(s => s.PartNumber)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var engineering = uow.Store<IEngineeringLookup>();
        var list = new List<HistorySummary>();
        foreach (var part in parts)
        {
            var mine = rows.Where(r => r.PartNumber == part && r.Month <= through).ToList();
            var ships = shipped.Where(s => s.PartNumber == part && s.Month <= through).ToList();
            var raw = mine.Select(r => new MonthlyDemand(r.Month, r.Quantity)).Concat(ships.Select(s => new MonthlyDemand(s.Month, s.Quantity))).ToList();
            var series = ForecastEngine.Fill(raw, through);
            var recorded = mine.Select(r => r.Month).Concat(ships.Select(s => s.Month)).ToHashSet();
            var gaps = series.Where(s => !recorded.Contains(s.Month)).Select(s => s.Month).ToList();
            var flags = ForecastEngine.Check(series, gaps).ToList();
            var simulated = mine.Where(r => r.Source == DemandHistorySource.Simulated).Select(r => r.Month).Distinct().Count();
            if (simulated > 0) flags.Insert(0, new HistoryFlag("simulated", string.Create(CultureInfo.InvariantCulture, $"{simulated} month(s) are simulated history, not real demand.")));
            list.Add(new HistorySummary(part, (await engineering.FindItemAsync(part, ct))?.Description, series.Count, series.Sum(s => s.Quantity), simulated,
                mine.Where(r => r.Source == DemandHistorySource.Imported).Select(r => r.Month).Distinct().Count(), ships.Select(s => s.Month).Distinct().Count(), series, flags));
        }

        return list;
    }

    public Task<CommandResult> GenerateSimulatedAsync(GenerateSimulatedHistory c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return Run(nameof(GenerateSimulatedHistory), c, context, Permissions.ForecastManage, false, async (uow, now) =>
        {
            if (c.Months is < 12 or > 60) throw new CommandRejectedException("invalid_months", "Simulate 12 to 60 months.");
            var items = await uow.Store<IEngineeringLookup>().ListItemsAsync(ct);
            var structures = await uow.Store<IEngineeringLookup>().ListReleasedStructuresAsync(ct);
            var components = structures.SelectMany(s => s.Bom).Select(b => b.ComponentPartNumber).ToHashSet(StringComparer.Ordinal);
            // Finished goods: made parts nothing else uses. Without released structures, every made part.
            var parts = items.Where(i => i.MakeBuy == MakeBuy.Make && !components.Contains(i.PartNumber)).Select(i => i.PartNumber).ToList();
            if (parts.Count == 0) parts = items.Where(i => i.MakeBuy == MakeBuy.Make).Select(i => i.PartNumber).ToList();
            if (parts.Count == 0) throw new CommandRejectedException("no_parts", "There are no made parts to simulate demand for.");
            var zone = await uow.SiteTimeZoneAsync(ct);
            var first = ForecastEngine.MonthOf(SiteTime.DateAtOrUtc(now, zone)).AddMonths(-c.Months);
            var rows = parts.SelectMany(p => ForecastEngine.Simulate(p, first, c.Months, c.Seed)
                .Select(m => new DemandHistoryRow(p, m.Month, m.Quantity, DemandHistorySource.Simulated, context.Actor, now))).ToList();
            var store = uow.Store<IForecastStore>();
            await store.DeleteHistoryAsync(DemandHistorySource.Simulated, ct);
            await store.UpsertHistoryAsync(rows, ct);
            return string.Create(CultureInfo.InvariantCulture,
                $"Simulated {c.Months} months of demand for {parts.Count} part(s) ({first:yyyy-MM} to {first.AddMonths(c.Months - 1):yyyy-MM}). It is marked simulated everywhere; clear it once real history exists.");
        }, ct);
    }

    public Task<CommandResult> ClearSimulatedAsync(ClearSimulatedHistory c, CommandContext context, CancellationToken ct) =>
        Run(nameof(ClearSimulatedHistory), c, context, Permissions.ForecastManage, true, async (uow, _) =>
        {
            var removed = await uow.Store<IForecastStore>().DeleteHistoryAsync(DemandHistorySource.Simulated, ct);
            return string.Create(CultureInfo.InvariantCulture, $"Removed {removed} simulated month(s). Forecast demand already approved stays until a planner changes it.");
        }, ct);

    public Task<CommandResult> ImportAsync(ImportDemandHistory c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return Run(nameof(ImportDemandHistory), c, context, Permissions.ForecastManage, true, async (uow, now) =>
        {
            if (c.Rows is not { Count: > 0 } || c.Rows.Count > 50_000) throw new CommandRejectedException("rows_required", "Give 1 to 50,000 rows of part, month and quantity.");
            var engineering = uow.Store<IEngineeringLookup>();
            var known = (await engineering.ListItemsAsync(ct)).Select(i => i.PartNumber).ToHashSet(StringComparer.Ordinal);
            var rows = new List<DemandHistoryRow>();
            foreach (var (raw, month, quantity) in c.Rows)
            {
                var part = BusinessCode.Require(raw, "Part");
                if (!known.Contains(part)) throw new CommandRejectedException("unknown_part", $"Part {part} does not exist.");
                if (quantity < 0) throw new CommandRejectedException("invalid_quantity", $"{part} {month:yyyy-MM}: quantity cannot be negative.");
                rows.Add(new DemandHistoryRow(part, ForecastEngine.MonthOf(month), quantity, DemandHistorySource.Imported, context.Actor, now));
            }

            var merged = rows.GroupBy(r => (r.PartNumber, r.Month)).Select(g => g.First() with { Quantity = g.Sum(r => r.Quantity) }).ToList();
            await uow.Store<IForecastStore>().UpsertHistoryAsync(merged, ct);
            return string.Create(CultureInfo.InvariantCulture, $"Imported {merged.Count} month(s) of demand history for {merged.Select(r => r.PartNumber).Distinct().Count()} part(s).");
        }, ct);
    }

    public Task<CommandResult> SetPolicyAsync(SetForecastPolicy c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return Run(nameof(SetForecastPolicy), c, context, Permissions.ForecastManage, true, async (uow, now) =>
        {
            if (c.Horizon is < 1 or > 24 || c.Holdout is < 1 or > 24 || c.IntervalPercent is not (80 or 90 or 95))
                throw new CommandRejectedException("invalid_policy", "Forecast 1 to 24 months ahead, test on 1 to 24 months, with an 80, 90 or 95 % range.");
            await uow.Store<IForecastStore>().InsertForecastPolicyAsync(new ForecastPolicy(c.Horizon, c.Holdout, c.IntervalPercent, c.UseSimulated, context.Actor, now), ct);
            return string.Create(CultureInfo.InvariantCulture,
                $"Forecasts look {c.Horizon} months ahead, tested on the last {c.Holdout} months, with an {c.IntervalPercent} % range{(c.UseSimulated ? "; simulated history counts" : "; simulated history is ignored")}.");
        }, ct);
    }

    public Task<CommandResult> RunAsync(RunForecast c, CommandContext context, CancellationToken ct) =>
        Run(nameof(RunForecast), c, context, Permissions.ForecastManage, false, async (uow, now) =>
        {
            var store = uow.Store<IForecastStore>();
            var policy = await store.ForecastPolicyAsync(ct);
            var history = await HistoryAsync(uow, now, policy.UseSimulated, ct);
            if (history.Count == 0) throw new CommandRejectedException("no_history", "There is no demand history yet: ship orders, import history, or generate simulated history.");
            var lines = history.Select(h => new ForecastLine(0,
                ForecastEngine.Forecast(h.PartNumber, h.Series, policy.Horizon, policy.Holdout, policy.IntervalPercent, h.Flags), h.SimulatedMonths > 0, null, null)).ToList();
            var simulated = lines.Any(l => l.Simulated);
            var first = lines.SelectMany(l => l.Forecast.Months).Select(m => m.Month).DefaultIfEmpty(ForecastEngine.MonthOf(SiteTime.DateAtOrUtc(now, await uow.SiteTimeZoneAsync(ct)))).Min();
            var id = await store.InsertRunAsync(new ForecastRun(0, now, context.Actor, simulated, policy.Horizon, policy.Holdout, policy.IntervalPercent, first), lines, ct);
            return string.Create(CultureInfo.InvariantCulture,
                $"Forecast {id}: {lines.Count} part(s), {policy.Horizon} months from {first:yyyy-MM}{(simulated ? ", built on simulated history" : string.Empty)}. Nothing is planned until a planner approves a part.");
        }, ct);

    public Task<CommandResult> ApproveAsync(ApproveForecast c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var spec = new CommandSpec(DemandService.SourceContext, nameof(ApproveForecast), Permissions.DemandManage, ResourceRef.Of(Entity, string.Create(CultureInfo.InvariantCulture, $"{c.RunId}/{part}")));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var store = uow.Store<IForecastStore>();
            var run = await store.FindRunAsync(c.RunId, ct) ?? throw new CommandRejectedException("run_not_found", $"Forecast {c.RunId} does not exist.");
            var latest = await store.LatestRunAsync(ct);
            if (latest?.RunId != run.RunId) throw new CommandRejectedException("stale_forecast", $"Forecast {run.RunId} is not the latest; approve from forecast {latest?.RunId}.");
            var line = (await store.ListLinesAsync(run.RunId, ct)).FirstOrDefault(l => l.Forecast.PartNumber == part) ?? throw new CommandRejectedException("part_not_forecast", $"Forecast {run.RunId} has no {part}.");
            if (line.ApprovedAt is not null) return CommandOutcome.NoChange(CommandResult.NoChange(part, $"{part} is already approved from forecast {run.RunId}."));
            var demandStore = uow.Store<IDemandStore>();
            var open = (await demandStore.ListAsync(false, ct)).Where(d => d.PartNumber == part && d.Status == DemandStatus.Open).ToList();
            var events = new List<EventEnvelope>();
            var said = new List<string>();
            foreach (var month in line.Forecast.Months)
            {
                var ordered = open.Where(d => d.Kind == DemandKind.CustomerOrder && ForecastEngine.MonthOf(d.NeedDate) == month.Month).Sum(d => d.Quantity);
                var net = Math.Max(0, Math.Round(month.Forecast - ordered, 2, MidpointRounding.AwayFromZero));
                var source = string.Create(CultureInfo.InvariantCulture, $"forecast:{part}/{month.Month:yyyy-MM}");
                var existing = await demandStore.FindBySourceAsync(source, ct);
                if (existing is { Status: DemandStatus.Open })
                {
                    var changed = net > 0 ? existing with { Quantity = net } : existing with { Status = DemandStatus.Cancelled };
                    if (changed != existing)
                    {
                        await demandStore.UpdateAsync(changed, now, context.Actor, ct);
                        events.Add(pipeline.Event(new DemandChanged(existing.DemandId, part, net > 0 ? "Changed" : "Cancelled"), context, DemandService.SourceContext, "Demand",
                            existing.DemandId.ToString(CultureInfo.InvariantCulture)));
                    }
                }
                else if (net > 0 && existing is null)
                {
                    var demand = new DemandInfo(0, part, net, month.Month, 5, DemandKind.Forecast, null, string.Create(CultureInfo.InvariantCulture, $"Forecast {run.RunId}{(line.Simulated ? " (simulated history)" : string.Empty)}"),
                        null, DemandStatus.Open, context.Actor, now, source);
                    var id = await demandStore.InsertAsync(demand, ct);
                    events.Add(pipeline.Event(new DemandChanged(id, part, "Recorded"), context, DemandService.SourceContext, "Demand", id.ToString(CultureInfo.InvariantCulture)));
                }

                said.Add(string.Create(CultureInfo.InvariantCulture, $"{month.Month:yyyy-MM} {net:0.##}{(ordered > 0 ? $" (forecast {month.Forecast:0.##} less {ordered:0.##} on order)" : string.Empty)}"));
            }

            await store.MarkApprovedAsync(run.RunId, part, context.Actor, now, ct);
            var message = $"{part} forecast demand approved: {string.Join("; ", said)}.{(line.Simulated ? " Built on simulated history." : string.Empty)}";
            events.Add(pipeline.Event(new ForecastChanged("Approved", message), context, DemandService.SourceContext, Entity, part));
            return new CommandOutcome(CommandResult.Ok(part, message), events, new AuditChange(Entity, string.Create(CultureInfo.InvariantCulture, $"{run.RunId}/{part}"), null, CanonicalJson.Serialize(line.Forecast.Months)));
        }, ct);
    }

    private Task<CommandResult> Run<TCommand>(string type, TCommand command, CommandContext context, string permission, bool reasonRequired,
        Func<IKernelUnitOfWork, DateTimeOffset, Task<string>> body, CancellationToken ct)
    {
        var spec = new CommandSpec(DemandService.SourceContext, type, permission, ResourceRef.Of(Entity, "site"), reasonRequired);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var message = await body(uow, now);
            return new CommandOutcome(CommandResult.Ok("site", message), [pipeline.Event(new ForecastChanged(type, message), context, DemandService.SourceContext, Entity, "site")],
                new AuditChange(Entity, "site", null, CanonicalJson.Serialize(new { type, message })));
        }, ct);
    }
}
