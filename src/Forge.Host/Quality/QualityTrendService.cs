using System.Globalization;
using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Maintenance.Contracts;
using Forge.Quality.Contracts;
using Forge.Resources.Contracts;

namespace Forge.Host.Quality;

/// <summary>A machine that ran the part in the week, and what was recorded against it then.</summary>
public sealed record TrendMachineContext(string MachineCode, int Orders, int Stoppages, decimal StoppageMinutes, IReadOnlyList<string> StoppageCategories, int MaintenanceWindows);

/// <summary>A component (and lot, where tracked) that went into the part's orders in the week.</summary>
public sealed record TrendMaterialContext(string ComponentPart, string? LotNumber, string? SupplierCode, int Orders, int OrdersWithNonconformance);

public sealed record QualityTrendWeek(
    DateOnly WeekStart,
    decimal Good,
    decimal Scrap,
    decimal? YieldPercent,
    decimal Inspected,
    int FailedInspections,
    decimal NonconformingQuantity,
    IReadOnlyList<string> ScrapReasons,
    IReadOnlyList<TrendMachineContext> Machines,
    IReadOnlyList<TrendMaterialContext> Materials,
    IReadOnlyList<string> Gauges);

/// <summary>
/// A part's quality trend by week with the equipment and material that coincided with it (Sprint 10 WW-S10-09). It has no person
/// dimension at all: operator fault is never assigned here. Context lines say what occurred together, never what caused what.
/// </summary>
public sealed record QualityTrend(
    string PartNumber,
    DateOnly From,
    DateOnly To,
    IReadOnlyList<QualityTrendWeek> Weeks,
    IReadOnlyList<string> Context,
    string Attribution,
    IReadOnlyList<string> Limitations);

public sealed class QualityTrendService(IKernelUnitOfWorkFactory unitOfWork, IForgeAuthorizer authorizer, TimeProvider time)
{
    public const string Attribution =
        "Operator fault is not assigned here. This trend has no person dimension; employee-attributable first-pass quality is shown only from a Quality-verified attribution in cause-adjusted performance.";

    private const int MaximumOrders = 200;

    private static readonly string[] Limitations =
    [
        "Equipment and material context shows what coincided with each week, not a confirmed cause. Causes are confirmed in NCR investigations and stoppage root causes.",
        "Weeks start on Monday in the site's time zone. Quantities come from production reports, inspections and NCRs raised in the week.",
    ];

    public async Task<QualityTrend> BuildAsync(ForgePrincipal principal, string partNumber, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var decision = authorizer.Authorize(principal, Permissions.QualityRead, ResourceRef.Of("QualityTrend"));
        if (!decision.IsPermitted) throw new ForgeAuthorizationException(decision);
        var part = BusinessCode.Require(partNumber, "Part");
        var now = time.GetUtcNow();
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var zone = await uow.SiteTimeZoneAsync(ct);
        var last = to ?? SiteTime.DateAtOrUtc(now, zone);
        var first = Monday(from ?? last.AddDays(-83));
        if (last < first || last.DayNumber - first.DayNumber > 366)
            throw new CommandRejectedException("invalid_period", "Choose a period of up to a year, with the start before the end.");
        var start = SiteTime.StartOfOrUtc(first, zone);
        var end = SiteTime.StartOfOrUtc(last.AddDays(1), zone);
        DateOnly Week(DateTimeOffset at) => Monday(SiteTime.DateAtOrUtc(at, zone));

        var execution = uow.Store<IExecutionLookup>();
        var orders = (await execution.ListOrdersAsync(ct))
            .Where(o => o.PartNumber == part && (o.ProgramId is not { } p || principal.Programs.Contains(new ProgramId(p))))
            .ToDictionary(o => o.OrderNumber);
        var reports = (await execution.ListReportsAsync(start, end, null, ct)).Where(r => orders.ContainsKey(r.OrderNumber)).ToList();
        var labor = (await execution.ListLaborAsync(start, end, null, ct)).Where(l => orders.ContainsKey(l.OrderNumber) && l.MachineCode is not null).ToList();
        var quality = uow.Store<IQualityLookup>();
        var inspections = (await quality.ListInspectionsBetweenAsync(start, end, ct)).Where(i => orders.ContainsKey(i.OrderNumber)).ToList();
        var ncrs = (await quality.ListNcrsRaisedBetweenAsync(start, end, ct))
            .Where(n => n.PartNumber == part && (n.OrderNumber is not { } o || orders.ContainsKey(o))).ToList();

        // The orders active in the window, and which of them had any nonconformance (scrap, a failed inspection or an NCR).
        var active = reports.Select(r => r.OrderNumber).Concat(labor.Select(l => l.OrderNumber)).Concat(inspections.Select(i => i.OrderNumber))
            .Concat(ncrs.Where(n => n.OrderNumber is not null).Select(n => n.OrderNumber!.Value)).Distinct().Order().ToList();
        if (active.Count > MaximumOrders)
            throw new CommandRejectedException("too_many_orders", $"{active.Count} orders of {part} were active; choose a shorter period (at most {MaximumOrders} orders).");
        var nonconforming = reports.Where(r => r.ScrapQuantity > 0).Select(r => r.OrderNumber)
            .Concat(inspections.Where(i => i.Result == InspectionResult.Fail).Select(i => i.OrderNumber))
            .Concat(ncrs.Where(n => n.OrderNumber is not null).Select(n => n.OrderNumber!.Value)).ToHashSet();

        var machinesRun = labor.Select(l => l.MachineCode!).ToHashSet(StringComparer.Ordinal);
        var stoppages = (await uow.Store<IProductionActualsLookup>().ListStoppagesAsync(start, end, false, ct)).Where(s => machinesRun.Contains(s.MachineCode)).ToList();
        var downtime = (await uow.Store<IMaintenanceLookup>().ListDowntimeAsync(start, end, ct)).Where(d => !d.IsHypothetical && machinesRun.Contains(d.MachineCode)).ToList();
        var asBuilt = uow.Store<IAsBuiltRecord>();
        var material = new List<AsBuiltLine>();
        foreach (var order in active)
            material.AddRange((await asBuilt.ListAsync(order, null, null, ct)).Where(l => l.RecordedAt >= start && l.RecordedAt < end));

        var weeks = new List<QualityTrendWeek>();
        for (var week = first; week <= last; week = week.AddDays(7))
        {
            var w = week;
            var r = reports.Where(x => Week(x.ReportedAt) == w).ToList();
            var good = r.Sum(x => x.GoodQuantity);
            var scrap = r.Sum(x => x.ScrapQuantity);
            var reasons = r.Where(x => x.ScrapQuantity > 0).GroupBy(x => x.ScrapReasonCode ?? x.ScrapReason ?? "Unstated")
                .Select(g => $"{g.Key}: {Qty(g.Sum(x => x.ScrapQuantity))}").Order(StringComparer.Ordinal).ToList();
            var ins = inspections.Where(x => Week(x.At) == w).ToList();
            var machines = labor.Where(l => Week(l.StartedAt) == w).GroupBy(l => l.MachineCode!).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
            {
                var stops = stoppages.Where(s => s.MachineCode == g.Key && Week(s.StartedAt) == w).ToList();
                return new TrendMachineContext(g.Key, g.Select(l => l.OrderNumber).Distinct().Count(), stops.Count, stops.Sum(s => s.Minutes(now)),
                    stops.Select(s => s.Category.ToString()).Distinct().Order(StringComparer.Ordinal).ToList(),
                    downtime.Count(d => d.MachineCode == g.Key && Week(d.StartsAt) == w));
            }).ToList();
            var materials = material.Where(l => Week(l.RecordedAt) == w).GroupBy(l => (l.ComponentPart, l.LotNumber, l.SupplierCode))
                .OrderBy(g => g.Key.ComponentPart, StringComparer.Ordinal).ThenBy(g => g.Key.LotNumber ?? string.Empty, StringComparer.Ordinal)
                .Select(g => new TrendMaterialContext(g.Key.ComponentPart, g.Key.LotNumber, g.Key.SupplierCode, g.Select(l => l.OrderNumber).Distinct().Count(),
                    g.Select(l => l.OrderNumber).Distinct().Count(nonconforming.Contains))).ToList();
            weeks.Add(new QualityTrendWeek(w, good, scrap, good + scrap > 0 ? decimal.Round(100m * good / (good + scrap), 1) : null,
                ins.Sum(x => x.QuantityInspected), ins.Count(x => x.Result == InspectionResult.Fail), ncrs.Where(n => Week(n.RaisedAt) == w).Sum(n => n.Quantity),
                reasons, machines, materials, ins.Where(x => x.GaugeCode is not null).Select(x => x.GaugeCode!).Distinct().Order(StringComparer.Ordinal).ToList()));
        }

        return new QualityTrend(part, first, last, weeks, Context(weeks, machinesRun, material, nonconforming), Attribution, Limitations);
    }

    private static List<string> Context(List<QualityTrendWeek> weeks, HashSet<string> machines, List<AsBuiltLine> material, HashSet<long> nonconforming)
    {
        var lines = new List<string>();
        var poor = weeks.Where(w => w.Scrap > 0 || w.FailedInspections > 0 || w.NonconformingQuantity > 0).Select(w => w.WeekStart).ToHashSet();
        foreach (var machine in machines.Order(StringComparer.Ordinal))
        {
            var inPoor = weeks.Where(w => poor.Contains(w.WeekStart)).SelectMany(w => w.Machines).Where(m => m.MachineCode == machine).ToList();
            var stops = inPoor.Sum(m => m.Stoppages);
            if (stops > 0)
                lines.Add($"{machine} had {stops} recorded stoppage(s) ({Qty(inPoor.Sum(m => m.StoppageMinutes))} min) in weeks with nonconformance on this part; check whether they are related before drawing a conclusion.");
            else if (inPoor.Count > 0)
                lines.Add($"{machine} ran this part in {inPoor.Count} week(s) with nonconformance and has no stoppage recorded then.");
        }

        if (nonconforming.Count > 0)
            foreach (var lot in material.GroupBy(l => (l.ComponentPart, l.LotNumber)).OrderBy(g => g.Key.ComponentPart, StringComparer.Ordinal).ThenBy(g => g.Key.LotNumber ?? string.Empty, StringComparer.Ordinal))
            {
                var used = lot.Select(l => l.OrderNumber).Distinct().ToList();
                var hit = used.Count(nonconforming.Contains);
                if (hit > 0)
                    lines.Add($"{lot.Key.ComponentPart} {(lot.Key.LotNumber is { } n ? $"lot {n}" : "(lot not tracked)")} went into {hit} of the {nonconforming.Count} order(s) with nonconformance ({used.Count} order(s) used it in all).");
            }

        if (lines.Count == 0) lines.Add(poor.Count == 0 ? "No nonconformance was recorded on this part in the period." : "No equipment or material record coincides with the nonconformance in this period.");
        return lines;
    }

    private static DateOnly Monday(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    private static string Qty(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
