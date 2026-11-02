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

/// <summary>One part's forecast in a run, and whether a planner approved it into demand. A family's own line is named family:CODE and is not approved.</summary>
public sealed record ForecastLine(long RunId, PartForecast Forecast, bool Simulated, string? ApprovedBy, DateTimeOffset? ApprovedAt);

/// <summary>A new item with little history borrows a like item's pattern, scaled (owner decision 2026-11-02).</summary>
/// <param name="Scale">How much of the like item's demand the new item will see; null works it out from the months both have.</param>
/// <param name="LaunchMonth">Nothing is forecast before this month.</param>
public sealed record ForecastAnalog(string PartNumber, string LikePart, decimal? Scale, DateOnly? LaunchMonth, string SetBy, DateTimeOffset SetAt);

/// <param name="Share">A fixed share of the family (or, with a driver, units per driver unit: an attach rate); null takes the recent mix.</param>
public sealed record ForecastFamilyMember(string PartNumber, decimal? Share);

/// <summary>
/// Parts forecast together (owner decision 2026-11-02): variants of a product family, or the options of a configurable item. The family
/// is forecast as a whole, which is steadier, and split by each member's share. With a driver part (the configured item that sells), the
/// family follows the driver and each option is its attach rate.
/// </summary>
public sealed record ForecastFamily(string Code, string Name, string? DriverPart, IReadOnlyList<ForecastFamilyMember> Members, string SetBy, DateTimeOffset SetAt);

public enum ForecastAdjustmentKind
{
    /// <summary>Add (or take away) units in each month: a promotion, a known large order.</summary>
    AddUnits,

    /// <summary>Raise or lower each month by a percentage.</summary>
    Percent,

    /// <summary>The part is ending: nothing is forecast from the first month on.</summary>
    EndOfLife,
}

/// <summary>Something a planner knows that history does not: kept apart from the statistical forecast and shown on every month it changes.</summary>
public sealed record ForecastAdjustment(long AdjustmentId, string PartNumber, DateOnly FromMonth, DateOnly? ToMonth, ForecastAdjustmentKind Kind, decimal Amount, string Reason, string SetBy,
    DateTimeOffset SetAt, string? CancelledBy = null, DateTimeOffset? CancelledAt = null);

/// <summary>Like items, families and planner adjustments, for the Forecast page.</summary>
public sealed record ForecastSetup(IReadOnlyList<ForecastAnalog> Analogs, IReadOnlyList<ForecastFamily> Families, IReadOnlyList<ForecastAdjustment> Adjustments);

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

    Task<IReadOnlyList<ForecastAnalog>> ListAnalogsAsync(CancellationToken ct);

    Task UpsertAnalogAsync(ForecastAnalog analog, CancellationToken ct);

    Task<bool> DeleteAnalogAsync(string partNumber, CancellationToken ct);

    Task<IReadOnlyList<ForecastFamily>> ListFamiliesAsync(CancellationToken ct);

    Task ReplaceFamilyAsync(ForecastFamily family, CancellationToken ct);

    Task<bool> DeleteFamilyAsync(string code, CancellationToken ct);

    Task<IReadOnlyList<ForecastAdjustment>> ListAdjustmentsAsync(bool activeOnly, CancellationToken ct);

    Task<long> InsertAdjustmentAsync(ForecastAdjustment adjustment, CancellationToken ct);

    Task<bool> CancelAdjustmentAsync(long adjustmentId, string actor, DateTimeOffset at, CancellationToken ct);
}

public sealed record GenerateSimulatedHistory(int Months, int Seed);

public sealed record ClearSimulatedHistory;

public sealed record ImportDemandHistory(IReadOnlyList<(string PartNumber, DateOnly Month, decimal Quantity)> Rows);

public sealed record SetForecastPolicy(int Horizon, int Holdout, int IntervalPercent, bool UseSimulated);

public sealed record RunForecast;

public sealed record ApproveForecast(long RunId, string PartNumber);

/// <summary>No like part clears the analog.</summary>
public sealed record SetForecastAnalog(string PartNumber, string? LikePart, decimal? Scale, DateOnly? LaunchMonth);

/// <summary>No members removes the family.</summary>
public sealed record SetForecastFamily(string Code, string? Name, string? DriverPart, IReadOnlyList<ForecastFamilyMember> Members);

public sealed record AddForecastAdjustment(string PartNumber, DateOnly FromMonth, DateOnly? ToMonth, ForecastAdjustmentKind Kind, decimal Amount);

public sealed record CancelForecastAdjustment(long AdjustmentId);

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
/// imported months and (until real history exists) simulated months, each marked; data checked first; methods tested on held-out months.
/// More complex items: families and configurable items are forecast as a whole and split by share; new items borrow a like item's
/// pattern; planner adjustments sit on top, kept apart and explained. A forecast is only a suggestion until a planner approves it, which
/// records forecast demand net of customer orders already in that month.
/// </summary>
public sealed class ForecastService(CommandPipeline pipeline)
{
    private const string Entity = "Forecast";
    private const string FamilyPrefix = "family:";

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

    /// <summary>
    /// Builds every forecast line: families first (the family, then each member's share), then new items from their like item, then every
    /// other part on its own history; planner adjustments go on last.
    /// </summary>
    public static IReadOnlyList<ForecastLine> Build(IReadOnlyList<HistorySummary> history, ForecastPolicy policy, ForecastSetup setup, DateOnly nextMonth)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(setup);
        var byPart = history.ToDictionary(h => h.PartNumber, StringComparer.Ordinal);
        var lines = new List<ForecastLine>();
        var done = new HashSet<string>(StringComparer.Ordinal);
        PartForecast Own(string part, IReadOnlyList<MonthlyDemand> series, IReadOnlyList<HistoryFlag> flags) =>
            ForecastEngine.Forecast(part, series, policy.Horizon, policy.Holdout, policy.IntervalPercent, flags);
        PartForecast Empty(string part, string why) => new(part, ForecastMethod.Naive, [],
            Enumerable.Range(0, policy.Horizon).Select(i => new ForecastMonth(nextMonth.AddMonths(i), 0, 0, 0)).ToList(), [new HistoryFlag("no_history", why)], 0, 0);

        foreach (var family in setup.Families)
        {
            var members = family.Members.Where(m => !done.Contains(m.PartNumber)).ToList();
            if (members.Count == 0) continue;
            var driver = family.DriverPart is { } d ? byPart.GetValueOrDefault(d) : null;
            var sources = family.DriverPart is null ? members.Select(m => byPart.GetValueOrDefault(m.PartNumber)).Where(h => h is not null).Select(h => h!).ToList() : driver is null ? [] : [driver];
            var simulated = sources.Any(s => s.SimulatedMonths > 0);
            var series = ForecastEngine.Fill(sources.SelectMany(s => s.Series), sources.Count == 0 ? (DateOnly?)null : sources.Max(s => s.Series.Count == 0 ? DateOnly.MinValue : s.Series[^1].Month));
            var basis = family.DriverPart is null
                ? string.Create(CultureInfo.InvariantCulture, $"Family {family.Name}: the total of its {members.Count} parts")
                : $"Family {family.Name}: follows {family.DriverPart}";
            var whole = series.Count == 0
                ? Empty(FamilyPrefix + family.Code, family.DriverPart is null ? "No member of the family has history." : $"Driver {family.DriverPart} has no history.")
                : Own(FamilyPrefix + family.Code, series, ForecastEngine.Check(series, []));
            lines.Add(new ForecastLine(0, whole with { Basis = basis }, simulated, null, null));

            // Shares: fixed ones as set; the rest from the last twelve months (of the family, or per driver unit as an attach rate).
            static decimal Recent(HistorySummary? h) => h?.Series.TakeLast(12).Sum(s => s.Quantity) ?? 0;
            var fixedTotal = members.Where(m => m.Share is not null).Sum(m => m.Share!.Value);
            var unfixedRecent = members.Where(m => m.Share is null).Sum(m => Recent(byPart.GetValueOrDefault(m.PartNumber)));
            var unfixedCount = members.Count(m => m.Share is null);
            foreach (var member in members)
            {
                var own = byPart.GetValueOrDefault(member.PartNumber);
                decimal share;
                string how;
                if (member.Share is { } s)
                {
                    share = s;
                    how = family.DriverPart is null ? "fixed share" : "fixed attach rate";
                }
                else if (family.DriverPart is not null)
                {
                    var driverRecent = Recent(driver);
                    share = driverRecent > 0 ? Recent(own) / driverRecent : 0;
                    how = "attach rate over the last 12 months";
                }
                else
                {
                    var rest = Math.Max(0, 1 - fixedTotal);
                    share = unfixedRecent > 0 ? rest * Recent(own) / unfixedRecent : unfixedCount > 0 ? rest / unfixedCount : 0;
                    how = unfixedRecent > 0 ? "share of the family's last 12 months" : "even split (no recent history)";
                }

                var flags = new List<HistoryFlag>(own?.Flags ?? []);
                if (own is { Series.Count: > 0 })
                {
                    var alone = Own(member.PartNumber, own.Series, own.Flags);
                    if (alone.Months.Count > 0)
                        flags.Add(new HistoryFlag("own_history", string.Create(CultureInfo.InvariantCulture,
                            $"On its own history alone ({alone.Method}) the first month would be {alone.Months[0].Forecast:0.##}.")));
                }

                var months = whole.Months.Select(m => new ForecastMonth(m.Month, R(m.Forecast * share), R(m.Low * share), R(m.High * share))).ToList();
                var scores = whole.Scores.Select(x => x with { MeanAbsoluteError = R(x.MeanAbsoluteError * share), Bias = R(x.Bias * share) }).ToList();
                var forecast = new PartForecast(member.PartNumber, whole.Method, scores, months, flags, own?.Months ?? 0, own is null || own.Months == 0 ? 0 : R(own.Total / own.Months),
                    string.Create(CultureInfo.InvariantCulture, $"{share * 100m:0.#} % of family {family.Name} ({how})"));
                lines.Add(new ForecastLine(0, forecast, simulated || (own?.SimulatedMonths ?? 0) > 0, null, null));
                done.Add(member.PartNumber);
            }
        }

        foreach (var analog in setup.Analogs.Where(a => !done.Contains(a.PartNumber)))
        {
            var own = byPart.GetValueOrDefault(analog.PartNumber);
            var like = byPart.GetValueOrDefault(analog.LikePart);
            if (own is { Months: >= 13 } && (analog.LaunchMonth is null || analog.LaunchMonth < nextMonth))
            {
                var forecast = Own(analog.PartNumber, own.Series, [.. own.Flags, new HistoryFlag("analog_not_needed", $"{analog.PartNumber} has a year of its own history now; its like item {analog.LikePart} is no longer used.")]);
                lines.Add(new ForecastLine(0, forecast, own.SimulatedMonths > 0, null, null));
                done.Add(analog.PartNumber);
                continue;
            }

            if (like is null || like.Series.Count == 0)
            {
                lines.Add(new ForecastLine(0, own is { Series.Count: > 0 }
                    ? Own(analog.PartNumber, own.Series, [.. own.Flags, new HistoryFlag("analog_empty", $"Like item {analog.LikePart} has no history to borrow.")])
                    : Empty(analog.PartNumber, $"No history of its own, and like item {analog.LikePart} has none either."), own?.SimulatedMonths > 0, null, null));
                done.Add(analog.PartNumber);
                continue;
            }

            var flags = new List<HistoryFlag>(own?.Flags.Where(f => f.Code != "short_history") ?? []);
            var scale = analog.Scale;
            if (scale is null)
            {
                // Worked out from the months both have, once the new item has three.
                var ownMonths = own?.Series.Where(m => m.Quantity > 0).Select(m => m.Month).ToHashSet() ?? [];
                var likeThen = like.Series.Where(m => ownMonths.Contains(m.Month)).Sum(m => m.Quantity);
                if (ownMonths.Count >= 3 && likeThen > 0)
                {
                    scale = R(own!.Series.Where(m => ownMonths.Contains(m.Month)).Sum(m => m.Quantity) / likeThen);
                    flags.Add(new HistoryFlag("scale_from_history", string.Create(CultureInfo.InvariantCulture,
                        $"Scale {scale:0.##} worked out from the {ownMonths.Count} month(s) both have sold.")));
                }
                else
                {
                    scale = 1m;
                    flags.Add(new HistoryFlag("scale_assumed", $"No scale set and too little history to work one out: it is assumed to sell like {analog.LikePart}. Set a scale."));
                }
            }

            var borrowed = Own(analog.LikePart, like.Series, like.Flags);
            var months = borrowed.Months.Select(m => analog.LaunchMonth is { } launch && m.Month < ForecastEngine.MonthOf(launch)
                    ? new ForecastMonth(m.Month, 0, 0, 0, null, $"before launch in {launch:yyyy-MM}")
                    : new ForecastMonth(m.Month, R(m.Forecast * scale.Value), R(m.Low * scale.Value), R(m.High * scale.Value))).ToList();
            flags.Add(new HistoryFlag("analog", $"New item: the pattern of {analog.LikePart} ({borrowed.Method}) is borrowed. Its range is {analog.LikePart}'s, scaled; the new item's own will be wider."));
            var scores = borrowed.Scores.Select(x => x with { MeanAbsoluteError = R(x.MeanAbsoluteError * scale.Value), Bias = R(x.Bias * scale.Value) }).ToList();
            lines.Add(new ForecastLine(0, new PartForecast(analog.PartNumber, borrowed.Method, scores, months, flags, own?.Months ?? 0, own is null || own.Months == 0 ? 0 : R(own.Total / own.Months),
                string.Create(CultureInfo.InvariantCulture, $"Like {analog.LikePart} × {scale:0.##}{(analog.LaunchMonth is { } l ? $", from {l:yyyy-MM}" : string.Empty)}")),
                like.SimulatedMonths > 0 || (own?.SimulatedMonths ?? 0) > 0, null, null));
            done.Add(analog.PartNumber);
        }

        foreach (var h in history.Where(h => !done.Contains(h.PartNumber)))
            lines.Add(new ForecastLine(0, Own(h.PartNumber, h.Series, h.Flags), h.SimulatedMonths > 0, null, null));

        var active = setup.Adjustments.Where(a => a.CancelledAt is null).ToList();
        return lines.Select(l => l.Forecast.PartNumber.StartsWith(FamilyPrefix, StringComparison.Ordinal) ? l : l with { Forecast = Adjust(l.Forecast, active) })
            .OrderBy(l => l.Forecast.PartNumber, StringComparer.Ordinal).ToList();
    }

    /// <summary>Planner adjustments on top of the statistical forecast; each changed month keeps what the method said and why it changed.</summary>
    public static PartForecast Adjust(PartForecast forecast, IReadOnlyList<ForecastAdjustment> adjustments)
    {
        ArgumentNullException.ThrowIfNull(forecast);
        ArgumentNullException.ThrowIfNull(adjustments);
        var mine = adjustments.Where(a => a.PartNumber == forecast.PartNumber && a.CancelledAt is null).OrderBy(a => a.AdjustmentId).ToList();
        if (mine.Count == 0) return forecast;
        var changed = false;
        var months = forecast.Months.Select(m =>
        {
            var applies = mine.Where(a => a.FromMonth <= m.Month && (a.Kind == ForecastAdjustmentKind.EndOfLife || (a.ToMonth ?? a.FromMonth) >= m.Month)).ToList();
            if (applies.Count == 0) return m;
            changed = true;
            var (f, low, high) = (m.Forecast, m.Low, m.High);
            var notes = new List<string>();
            foreach (var a in applies)
            {
                switch (a.Kind)
                {
                    case ForecastAdjustmentKind.Percent:
                        var factor = 1 + a.Amount / 100m;
                        (f, low, high) = (f * factor, low * factor, high * factor);
                        notes.Add(string.Create(CultureInfo.InvariantCulture, $"{a.Amount:+0.##;-0.##} % ({a.Reason})"));
                        break;
                    case ForecastAdjustmentKind.AddUnits:
                        (f, low, high) = (f + a.Amount, low + a.Amount, high + a.Amount);
                        notes.Add(string.Create(CultureInfo.InvariantCulture, $"{a.Amount:+0.##;-0.##} units ({a.Reason})"));
                        break;
                    default:
                        (f, low, high) = (0, 0, 0);
                        notes.Add(string.Create(CultureInfo.InvariantCulture, $"end of life from {a.FromMonth:yyyy-MM} ({a.Reason})"));
                        break;
                }
            }

            return m with { Forecast = R(Math.Max(0, f)), Low = R(Math.Max(0, low)), High = R(Math.Max(0, high)), Statistical = m.Statistical ?? m.Forecast, Adjusted = string.Join("; ", notes) };
        }).ToList();
        return changed ? forecast with { Months = months, Flags = [.. forecast.Flags, new HistoryFlag("adjusted", "A planner adjustment changes some months; the method's own figure is kept beside it.")] } : forecast;
    }

    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

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
            var known = await KnownPartsAsync(uow, ct);
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

    public Task<CommandResult> SetAnalogAsync(SetForecastAnalog c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return Run(nameof(SetForecastAnalog), c, context, Permissions.ForecastManage, false, async (uow, now) =>
        {
            var part = BusinessCode.Require(c.PartNumber, "Part");
            var store = uow.Store<IForecastStore>();
            if (string.IsNullOrWhiteSpace(c.LikePart))
                return await store.DeleteAnalogAsync(part, ct) ? $"{part} is forecast on its own history again." : $"{part} had no like item.";
            var like = BusinessCode.Require(c.LikePart, "Part");
            var known = await KnownPartsAsync(uow, ct);
            foreach (var p in new[] { part, like })
                if (!known.Contains(p)) throw new CommandRejectedException("unknown_part", $"Part {p} does not exist.");
            if (part == like) throw new CommandRejectedException("same_part", "A part cannot be like itself.");
            if (c.Scale is <= 0 or > 100) throw new CommandRejectedException("invalid_scale", "The scale is how much of the like item's demand to expect: above 0, at most 100.");
            if ((await store.ListAnalogsAsync(ct)).Any(a => a.PartNumber == like && a.LikePart == part))
                throw new CommandRejectedException("circular_analog", $"{like} already borrows from {part}.");
            var launch = c.LaunchMonth is { } l ? ForecastEngine.MonthOf(l) : (DateOnly?)null;
            await store.UpsertAnalogAsync(new ForecastAnalog(part, like, c.Scale, launch, context.Actor, now), ct);
            return string.Create(CultureInfo.InvariantCulture,
                $"{part} borrows {like}'s demand pattern{(c.Scale is { } s ? $" at {s:0.##} times its level" : ", scaled from the months both have sold")}{(launch is { } m ? $", from {m:yyyy-MM}" : string.Empty)}, until it has a year of its own history.");
        }, ct);
    }

    public Task<CommandResult> SetFamilyAsync(SetForecastFamily c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return Run(nameof(SetForecastFamily), c, context, Permissions.ForecastManage, false, async (uow, now) =>
        {
            var code = BusinessCode.Require(c.Code, "Family");
            var store = uow.Store<IForecastStore>();
            if (c.Members is not { Count: > 0 })
                return await store.DeleteFamilyAsync(code, ct) ? $"Family {code} removed; its parts are forecast on their own." : $"There is no family {code}.";
            var known = await KnownPartsAsync(uow, ct);
            var driver = string.IsNullOrWhiteSpace(c.DriverPart) ? null : BusinessCode.Require(c.DriverPart, "Part");
            if (driver is not null && !known.Contains(driver)) throw new CommandRejectedException("unknown_part", $"Part {driver} does not exist.");
            var members = new List<ForecastFamilyMember>();
            foreach (var m in c.Members)
            {
                var part = BusinessCode.Require(m.PartNumber, "Part");
                if (!known.Contains(part)) throw new CommandRejectedException("unknown_part", $"Part {part} does not exist.");
                if (part == driver) throw new CommandRejectedException("driver_is_member", $"{part} drives the family; it cannot also be a member.");
                if (members.Any(x => x.PartNumber == part)) throw new CommandRejectedException("duplicate_member", $"{part} is listed twice.");
                if (m.Share is <= 0 or > 100) throw new CommandRejectedException("invalid_share", $"{part}: a share is above 0 (and an attach rate at most 100 per driver unit).");
                members.Add(new ForecastFamilyMember(part, m.Share));
            }

            if (driver is null && members.Where(m => m.Share is not null).Sum(m => m.Share!.Value) > 1)
                throw new CommandRejectedException("shares_over_whole", "Fixed shares of a family add up to more than 100 %.");
            var elsewhere = (await store.ListFamiliesAsync(ct)).Where(f => f.Code != code).SelectMany(f => f.Members.Select(m => (f.Code, m.PartNumber))).FirstOrDefault(x => members.Any(m => m.PartNumber == x.PartNumber));
            if (elsewhere.Code is not null) throw new CommandRejectedException("in_other_family", $"{elsewhere.PartNumber} is already in family {elsewhere.Code}.");
            var name = string.IsNullOrWhiteSpace(c.Name) ? code : c.Name.Trim();
            await store.ReplaceFamilyAsync(new ForecastFamily(code, name, driver, members, context.Actor, now), ct);
            return string.Create(CultureInfo.InvariantCulture,
                $"Family {name}: {members.Count} part(s) forecast together{(driver is null ? " and split by share" : $", following {driver} with each option's attach rate")}.");
        }, ct);
    }

    public Task<CommandResult> AddAdjustmentAsync(AddForecastAdjustment c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return Run(nameof(AddForecastAdjustment), c, context, Permissions.ForecastManage, true, async (uow, now) =>
        {
            var part = BusinessCode.Require(c.PartNumber, "Part");
            if (!(await KnownPartsAsync(uow, ct)).Contains(part)) throw new CommandRejectedException("unknown_part", $"Part {part} does not exist.");
            if (!Enum.IsDefined(c.Kind)) throw new CommandRejectedException("invalid_kind", "Add units, change by a percentage, or end the part.");
            var from = ForecastEngine.MonthOf(c.FromMonth);
            var to = c.Kind == ForecastAdjustmentKind.EndOfLife ? (DateOnly?)null : c.ToMonth is { } t ? ForecastEngine.MonthOf(t) : from;
            if (to < from) throw new CommandRejectedException("invalid_months", "The last month is before the first.");
            if (c.Kind == ForecastAdjustmentKind.AddUnits && c.Amount == 0) throw new CommandRejectedException("invalid_amount", "Add or take away some units.");
            if (c.Kind == ForecastAdjustmentKind.Percent && (c.Amount is <= -100 or > 1000 || c.Amount == 0))
                throw new CommandRejectedException("invalid_amount", "A percentage change is above -100 % and at most +1000 %.");
            var id = await uow.Store<IForecastStore>().InsertAdjustmentAsync(
                new ForecastAdjustment(0, part, from, to, c.Kind, c.Kind == ForecastAdjustmentKind.EndOfLife ? 0 : c.Amount, context.Reason!, context.Actor, now), ct);
            var what = c.Kind switch
            {
                ForecastAdjustmentKind.Percent => string.Create(CultureInfo.InvariantCulture, $"{c.Amount:+0.##;-0.##} %"),
                ForecastAdjustmentKind.AddUnits => string.Create(CultureInfo.InvariantCulture, $"{c.Amount:+0.##;-0.##} units a month"),
                _ => "nothing forecast",
            };
            return string.Create(CultureInfo.InvariantCulture,
                $"Adjustment {id}: {part} {what} {(to is { } last ? last == from ? $"in {from:yyyy-MM}" : $"from {from:yyyy-MM} to {last:yyyy-MM}" : $"from {from:yyyy-MM} on")}. It applies from the next forecast run; the method's own figure stays beside it.");
        }, ct);
    }

    public Task<CommandResult> CancelAdjustmentAsync(CancelForecastAdjustment c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return Run(nameof(CancelForecastAdjustment), c, context, Permissions.ForecastManage, true, async (uow, now) =>
            await uow.Store<IForecastStore>().CancelAdjustmentAsync(c.AdjustmentId, context.Actor, now, ct)
                ? string.Create(CultureInfo.InvariantCulture, $"Adjustment {c.AdjustmentId} cancelled; it stops applying from the next forecast run.")
                : throw new CommandRejectedException("adjustment_not_found", string.Create(CultureInfo.InvariantCulture, $"There is no open adjustment {c.AdjustmentId}.")), ct);
    }

    public Task<CommandResult> RunAsync(RunForecast c, CommandContext context, CancellationToken ct) =>
        Run(nameof(RunForecast), c, context, Permissions.ForecastManage, false, async (uow, now) =>
        {
            var store = uow.Store<IForecastStore>();
            var policy = await store.ForecastPolicyAsync(ct);
            var history = await HistoryAsync(uow, now, policy.UseSimulated, ct);
            var setup = new ForecastSetup(await store.ListAnalogsAsync(ct), await store.ListFamiliesAsync(ct), await store.ListAdjustmentsAsync(true, ct));
            if (history.Count == 0 && setup.Analogs.Count == 0)
                throw new CommandRejectedException("no_history", "There is no demand history yet: ship orders, import history, or generate simulated history.");
            var next = ForecastEngine.MonthOf(SiteTime.DateAtOrUtc(now, await uow.SiteTimeZoneAsync(ct)));
            var lines = Build(history, policy, setup, next);
            var simulated = lines.Any(l => l.Simulated);
            var first = lines.SelectMany(l => l.Forecast.Months).Select(m => m.Month).DefaultIfEmpty(next).Min();
            var id = await store.InsertRunAsync(new ForecastRun(0, now, context.Actor, simulated, policy.Horizon, policy.Holdout, policy.IntervalPercent, first), lines, ct);
            var parts = lines.Count(l => !l.Forecast.PartNumber.StartsWith(FamilyPrefix, StringComparison.Ordinal));
            var families = lines.Count - parts;
            return string.Create(CultureInfo.InvariantCulture,
                $"Forecast {id}: {parts} part(s){(families > 0 ? $" and {families} family(ies)" : string.Empty)}, {policy.Horizon} months from {first:yyyy-MM}{(simulated ? ", built on simulated history" : string.Empty)}. Nothing is planned until a planner approves a part.");
        }, ct);

    public Task<CommandResult> ApproveAsync(ApproveForecast c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (c.PartNumber?.StartsWith(FamilyPrefix, StringComparison.Ordinal) == true)
            throw new CommandRejectedException("approve_members", "A family is not demand itself; approve each of its parts.");
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

                said.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{month.Month:yyyy-MM} {net:0.##}{(ordered > 0 ? $" (forecast {month.Forecast:0.##} less {ordered:0.##} on order)" : string.Empty)}{(month.Adjusted is null ? string.Empty : $" [adjusted: {month.Adjusted}]")}"));
            }

            await store.MarkApprovedAsync(run.RunId, part, context.Actor, now, ct);
            var message = $"{part} forecast demand approved: {string.Join("; ", said)}.{(line.Forecast.Basis is null ? string.Empty : $" Based on: {line.Forecast.Basis}.")}{(line.Simulated ? " Built on simulated history." : string.Empty)}";
            events.Add(pipeline.Event(new ForecastChanged("Approved", message), context, DemandService.SourceContext, Entity, part));
            return new CommandOutcome(CommandResult.Ok(part, message), events, new AuditChange(Entity, string.Create(CultureInfo.InvariantCulture, $"{run.RunId}/{part}"), null, CanonicalJson.Serialize(line.Forecast.Months)));
        }, ct);
    }

    private static async Task<HashSet<string>> KnownPartsAsync(IKernelUnitOfWork uow, CancellationToken ct) =>
        (await uow.Store<IEngineeringLookup>().ListItemsAsync(ct)).Select(i => i.PartNumber).ToHashSet(StringComparer.Ordinal);

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
