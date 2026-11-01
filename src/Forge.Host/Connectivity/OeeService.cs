using Forge.Connectivity.Contracts;
using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Resources.Contracts;

namespace Forge.Host.Connectivity;

public sealed record OeeReason(string Category, string ReasonCode, decimal Minutes);

/// <summary>
/// One machine on one shift. Availability = run ÷ planned production time (the shift less planned stops); performance = the routing's
/// ideal minutes for what was made ÷ run time; quality = good ÷ good and scrap. OEE is their product. It describes machines, not people.
/// </summary>
public sealed record OeeShift(
    string MachineCode,
    string MachineName,
    string WorkCenterCode,
    string ShiftCode,
    string ShiftName,
    DateOnly Date,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool InProgress,
    decimal ScheduledMinutes,
    decimal PlannedStopMinutes,
    decimal DowntimeMinutes,
    decimal UnexplainedMinutes,
    decimal RunMinutes,
    decimal Good,
    decimal Scrap,
    decimal IdealMinutes,
    long? MachineCounted,
    decimal? Availability,
    decimal? Performance,
    decimal? Quality,
    decimal? Oee,
    IReadOnlyList<OeeReason> Reasons,
    IReadOnlyList<string> Notes);

public sealed record OeeReport(DateOnly From, DateOnly To, IReadOnlyList<OeeShift> Shifts, IReadOnlyList<string> Notes);

/// <summary>OEE per machine per shift (owner decision 2026-11-01), from stoppages, production reports and the routing standards.</summary>
public sealed class OeeService(IKernelUnitOfWorkFactory unitOfWork, ISiteClock siteClock, TimeProvider time)
{
    public async Task<OeeReport> ReportAsync(DateOnly? from, DateOnly? to, string? machine, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var zone = siteClock.HomeTimeZone;
        var last = to ?? SiteTime.DateAt(now, zone);
        var first = from ?? last.AddDays(-6);
        if (first > last || last.DayNumber - first.DayNumber > 62) throw new CommandRejectedException("invalid_range", "Choose up to 63 days, the start before the end.");
        var rangeStart = SiteTime.StartOf(first, zone).AddDays(-1);
        var rangeEnd = SiteTime.StartOf(last.AddDays(1), zone).AddDays(1);
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var resources = uow.Store<IResourcesLookup>();
        var machines = (await resources.ListMachinesAsync(ct)).Where(m => m.Active && (machine is null || m.Code == machine)).ToList();
        var shifts = (await resources.ListShiftsAsync(ct)).ToDictionary(s => s.Code, StringComparer.Ordinal);
        var assigned = (await resources.ListWorkCenterShiftsAsync(ct)).GroupBy(a => a.WorkCenterCode).ToDictionary(g => g.Key, g => g.Select(a => a.ShiftCode).ToList(), StringComparer.Ordinal);
        var stoppages = (await uow.Store<IProductionActualsLookup>().ListStoppagesAsync(rangeStart, rangeEnd, false, ct)).GroupBy(s => s.MachineCode).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var execution = uow.Store<IExecutionLookup>();
        var labor = (await execution.ListLaborAsync(rangeStart.AddDays(-2), rangeEnd, null, ct)).Where(l => l.MachineCode is not null).ToList();
        var reports = await execution.ListReportsAsync(rangeStart, rangeEnd, null, ct);
        var readings = (await uow.Store<IConnectivityLookup>().ListReadingsAsync(machine, rangeStart.AddDays(-7), rangeEnd, ct))
            .Where(r => r.Reading.PartCount is not null).GroupBy(r => r.MachineCode).ToDictionary(g => g.Key, g => g.Select(r => r.Reading).ToList(), StringComparer.Ordinal);
        var orders = new Dictionary<long, OrderInfo?>();

        // Which machine made each report: the reporter's labor on that operation at that time.
        var made = new List<(string Machine, ProductionReport Report, decimal IdealPerUnit)>();
        foreach (var r in reports)
        {
            var entry = labor.Where(l => l.PersonId == r.PersonId && l.OrderNumber == r.OrderNumber && l.Sequence == r.Sequence && l.StartedAt <= r.ReportedAt
                    && (l.EndedAt ?? now).AddMinutes(5) >= r.ReportedAt).OrderByDescending(l => l.StartedAt).FirstOrDefault();
            if (entry?.MachineCode is not { } code) continue;
            if (!orders.TryGetValue(r.OrderNumber, out var order)) orders[r.OrderNumber] = order = await execution.FindOrderAsync(r.OrderNumber, ct);
            var op = order?.Operations.FirstOrDefault(o => o.Sequence == r.Sequence);
            var quantity = op is null || order is null ? 0m : op.QuantityFor(order.Quantity);
            var ideal = op is null || quantity <= 0 ? 0m : Math.Max(0m, op.PlannedMinutes - (op.SetupMinutes ?? 0m)) / quantity;
            made.Add((code, r, ideal));
        }

        var rows = new List<OeeShift>();
        foreach (var m in machines)
        {
            foreach (var shiftCode in assigned.GetValueOrDefault(m.WorkCenterCode) ?? [])
            {
                if (!shifts.TryGetValue(shiftCode, out var shift)) continue;
                for (var day = first; day <= last; day = day.AddDays(1))
                {
                    var iso = day.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)day.DayOfWeek;
                    if (!shift.Days.Contains(iso)) continue;
                    var start = SiteTime.StartOf(day, zone) + shift.Start.ToTimeSpan();
                    if (start >= now) continue;
                    var end = start.AddMinutes(shift.DurationMinutes);
                    var inProgress = end > now;
                    var until = inProgress ? now : end;
                    rows.Add(Shift(m, shift, day, start, until, inProgress, stoppages.GetValueOrDefault(m.Code) ?? [],
                        made.Where(x => x.Machine == m.Code && x.Report.ReportedAt >= start && x.Report.ReportedAt < until).ToList(), readings.GetValueOrDefault(m.Code), now));
                }
            }
        }

        var notes = new List<string>
        {
            "Planned stops (breaks, meetings, no demand) are taken out of the time the machine was meant to run; every other stop is downtime.",
            "Performance compares what was made with the routing's run time per part; above 100 % usually means the standard needs a look.",
            "OEE describes machines. It does not measure or rank the people who run them.",
        };
        if (machines.Any(m => !assigned.ContainsKey(m.WorkCenterCode))) notes.Add("Machines whose work center has no shift are not shown; assign shifts to their work centers.");
        return new OeeReport(first, last, rows.OrderByDescending(r => r.Start).ThenBy(r => r.MachineCode, StringComparer.Ordinal).ToList(), notes);
    }

    private static decimal Overlap(DateTimeOffset a1, DateTimeOffset a2, DateTimeOffset b1, DateTimeOffset b2)
    {
        var start = a1 > b1 ? a1 : b1;
        var end = a2 < b2 ? a2 : b2;
        return end > start ? (decimal)(end - start).TotalMinutes : 0m;
    }

    internal static OeeShift Shift(MachineInfo m, ShiftInfo shift, DateOnly day, DateTimeOffset start, DateTimeOffset until, bool inProgress, IReadOnlyList<StoppageInfo> stoppages,
        IReadOnlyList<(string Machine, ProductionReport Report, decimal IdealPerUnit)> made, IReadOnlyList<MachineReading>? readings, DateTimeOffset now)
    {
        var scheduled = (decimal)(until - start).TotalMinutes;
        var parts = stoppages.Select(s => (Stop: s, Minutes: Overlap(s.StartedAt, s.EndedAt ?? now, start, until))).Where(x => x.Minutes > 0).ToList();
        var plannedStops = parts.Where(p => p.Stop.Category == StoppageCategory.Planned).Sum(p => p.Minutes);
        var downtime = parts.Where(p => p.Stop.Category != StoppageCategory.Planned).Sum(p => p.Minutes);
        var unexplained = parts.Where(p => p.Stop.Unexplained).Sum(p => p.Minutes);
        var planned = Math.Max(0m, scheduled - plannedStops);
        var run = Math.Max(0m, planned - downtime);
        var good = made.Sum(x => x.Report.GoodQuantity);
        var scrap = made.Sum(x => x.Report.ScrapQuantity);
        var ideal = made.Sum(x => x.IdealPerUnit * (x.Report.GoodQuantity + x.Report.ScrapQuantity));
        decimal? R(decimal? v) => v is { } x ? Math.Round(x, 4) : null;
        var availability = planned > 0 ? run / planned : (decimal?)null;
        var performance = run > 0 && good + scrap > 0 ? ideal / run : (decimal?)null;
        var quality = good + scrap > 0 ? good / (good + scrap) : (decimal?)null;
        var oee = availability is { } a && performance is { } p && quality is { } q ? a * Math.Min(p, 1m) * q : (decimal?)null;
        long? CountAt(DateTimeOffset t) => readings?.LastOrDefault(r => r.At <= t)?.PartCount;
        var counted = CountAt(until) is { } c2 && CountAt(start) is { } c1 && c2 >= c1 ? c2 - c1 : (long?)null;
        var notes = new List<string>();
        if (unexplained > 0) notes.Add($"{Math.Round(unexplained, 1)} min of stops detected from the machine still need a reason.");
        if (performance > 1m) notes.Add("Faster than the routing standard; OEE counts performance at 100 %.");
        if (counted is { } n && n != good + scrap) notes.Add($"The machine counted {n} parts; {good + scrap} were reported.");
        if (good + scrap == 0 && run > 0) notes.Add("Nothing was reported on this machine this shift.");
        var reasons = parts.Where(p => p.Stop.Category != StoppageCategory.Planned)
            .GroupBy(p => (Category: p.Stop.Category.ToString(), p.Stop.ReasonCode))
            .Select(g => new OeeReason(g.Key.Category, g.Key.ReasonCode, Math.Round(g.Sum(x => x.Minutes), 1))).OrderByDescending(r => r.Minutes).ToList();
        return new OeeShift(m.Code, m.Name, m.WorkCenterCode, shift.Code, shift.Name, day, start, until, inProgress, Math.Round(scheduled, 1), Math.Round(plannedStops, 1),
            Math.Round(downtime, 1), Math.Round(unexplained, 1), Math.Round(run, 1), good, scrap, Math.Round(ideal, 1), counted, R(availability), R(performance), R(quality), R(oee), reasons, notes);
    }
}
