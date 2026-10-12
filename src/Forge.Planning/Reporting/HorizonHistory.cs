using System.Globalization;
using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Audit;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Maintenance.Contracts;
using Forge.Planning.Engines;
using Forge.Quality.Contracts;

namespace Forge.Planning.Reporting;

public sealed record HistoryVersion(long VersionId, DateTimeOffset? ApprovedAt, string? ApprovedBy, string ProposedBy, string Summary);

/// <summary>A recorded fact that happened between the two moments and bears on a change.</summary>
public sealed record HistoryEvidence(DateTimeOffset? At, string Kind, string Text, string Trace);

public sealed record CauseMapStep(Guid EventId, string EventType, string SourceContext, string EntityType, string EntityId, DateTimeOffset At, string? Reason, string Trace);
public sealed record CauseMapLink(Guid CauseEventId, Guid EffectEventId);
public sealed record CauseMapView(IReadOnlyList<CauseMapStep> Steps, IReadOnlyList<CauseMapLink> Links, IReadOnlyList<Guid> EventsWithoutVisibleCause);

public sealed record OperationChange(
    int Sequence,
    string WorkCenterCode,
    string? MachineBefore,
    string? MachineAfter,
    DateTimeOffset? StartBefore,
    DateTimeOffset? StartAfter,
    DateTimeOffset? EndBefore,
    DateTimeOffset? EndAfter,
    decimal? ShiftHours,
    string? BindingBefore,
    string? BindingAfter,
    string Explanation);

public sealed record OrderChange(
    long OrderNumber,
    string PartNumber,
    string Change,
    string? StatusBefore,
    string? StatusAfter,
    DateTimeOffset? ProjectedBefore,
    DateTimeOffset? ProjectedAfter,
    decimal? ShiftHours,
    DateOnly? DueBefore,
    DateOnly? DueAfter,
    int? PriorityBefore,
    int? PriorityAfter,
    string Explanation,
    IReadOnlyList<OperationChange> Operations,
    IReadOnlyList<HistoryEvidence> Evidence);

/// <summary>"What changed?" between two moments: the approved schedules in force at each, compared order by order, with the evidence recorded in between.</summary>
public sealed record HorizonComparison(
    DateTimeOffset At1,
    DateTimeOffset At2,
    HistoryVersion? Before,
    HistoryVersion? After,
    IReadOnlyList<HistoryVersion> ApprovedBetween,
    int Later,
    int Earlier,
    int Added,
    int Removed,
    int StatusChanged,
    int Unchanged,
    string Headline,
    IReadOnlyList<OrderChange> Orders,
    IReadOnlyList<HistoryEvidence> SiteEvidence,
    bool AuditIncluded,
    CauseMapView? CauseMap = null);

/// <summary>
/// Horizon history (spec §63–65): compares the approved schedules in force at two moments and explains
/// each order's change from what the schedules recorded (binding constraints, machines, start times)
/// and from records made in between: downtime, constraint findings, shop-floor reports, NCRs and,
/// for people who may read it, the audit trail. Nothing is inferred beyond what was recorded.
/// </summary>
public sealed class HorizonHistoryService(IKernelUnitOfWorkFactory unitOfWork, IForgeAuthorizer authorizer)
{
    /// <summary>Start shifts smaller than this are treated as unchanged.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(1);

    public async Task<HorizonComparison> CompareAsync(ForgePrincipal principal, DateTimeOffset at1, DateTimeOffset at2, bool includeUnchanged, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var decision = authorizer.Authorize(principal, Permissions.ScheduleRead, ResourceRef.Of("Schedule"));
        if (!decision.IsPermitted)
        {
            throw new ForgeAuthorizationException(decision);
        }

        if (at2 <= at1)
        {
            throw new CommandRejectedException("invalid_period", "The second moment must be after the first.");
        }

        if (at2 - at1 > TimeSpan.FromDays(366))
        {
            throw new CommandRejectedException("invalid_period", "Compare moments at most a year apart.");
        }

        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var planning = uow.Store<IPlanningStore>();
        var before = await planning.ApprovedAsOfAsync(at1, ct);
        var after = await planning.ApprovedAsOfAsync(at2, ct);
        var between = await planning.ListApprovedBetweenAsync(at1, at2, ct);
        bool Visible(Guid? program) => program is not { } p || principal.Programs.Contains(new ProgramId(p));

        var ordersBefore = (before?.Result?.Orders ?? []).Where(o => Visible(o.ProgramId)).ToDictionary(o => o.OrderNumber);
        var ordersAfter = (after?.Result?.Orders ?? []).Where(o => Visible(o.ProgramId)).ToDictionary(o => o.OrderNumber);
        var opsBefore = (before?.Result?.Operations ?? []).Where(o => Visible(o.ProgramId)).ToLookup(o => o.OrderNumber);
        var opsAfter = (after?.Result?.Operations ?? []).Where(o => Visible(o.ProgramId)).ToLookup(o => o.OrderNumber);

        // Records made between the two moments.
        var constraints = await planning.ListConstraintsAsync(true, ct);
        var reports = await uow.Store<IExecutionLookup>().ListReportsAsync(at1, at2, null, ct);
        var ncrs = await uow.Store<IQualityLookup>().ListNcrsRaisedBetweenAsync(at1, at2, ct);
        var horizonEnd = new[] { at2, before?.To ?? at2, after?.To ?? at2 }.Max();
        var downtime = await uow.Store<IMaintenanceLookup>().ListDowntimeAsync(at1, horizonEnd, ct);

        var changes = new List<OrderChange>();
        foreach (var number in ordersBefore.Keys.Union(ordersAfter.Keys).Order())
        {
            ordersBefore.TryGetValue(number, out var a);
            ordersAfter.TryGetValue(number, out var b);
            var ops = CompareOperations(opsBefore[number].ToList(), opsAfter[number].ToList());
            var evidence = Evidence(number, ops, opsBefore[number].Concat(opsAfter[number]).ToList(), constraints, reports, ncrs, downtime, at1, at2);
            changes.Add(Describe(number, a, b, ops, evidence));
        }

        var counts = changes.GroupBy(c => c.Change, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        int Count(string k) => counts.GetValueOrDefault(k);
        var shown = changes
            .Where(c => includeUnchanged || c.Change != "Unchanged")
            .OrderBy(c => c.Change switch { "Later" => 0, "Status changed" => 1, "Added" => 2, "Removed" => 3, "Earlier" => 4, _ => 5 })
            .ThenByDescending(c => Math.Abs(c.ShiftHours ?? 0))
            .ThenBy(c => c.OrderNumber)
            .ToList();

        var site = SiteEvidence(between, constraints, downtime, at1, at2, changes);
        var headline = before is null && after is null
            ? "No schedule was approved at either moment, so there is nothing to compare."
            : before?.VersionId == after?.VersionId
                ? $"The same schedule (version {Id(after!.VersionId)}) was in force at both moments; nothing in the plan changed."
                : $"Schedule {(before is null ? "none" : Id(before.VersionId))} → {(after is null ? "none" : Id(after.VersionId))}: "
                    + $"{Count("Later")} later, {Count("Earlier")} earlier, {Count("Status changed")} changed delivery status, {Count("Added")} added, {Count("Removed")} removed, {Count("Unchanged")} unchanged.";

        return new HorizonComparison(
            at1, at2, Version(before), Version(after), between.Select(v => Version(v)!).ToList(),
            Count("Later"), Count("Earlier"), Count("Added"), Count("Removed"), Count("Status changed"), Count("Unchanged"),
            headline, shown, site, false);
    }

    /// <summary>
    /// Adds audit entries recorded between the two moments to the orders they concern (production order
    /// changes, and changes to the machines of the order's operations). Call only for a reader allowed to
    /// read the audit trail; entries are already limited to the reader's programs.
    /// </summary>
    public static HorizonComparison WithAudit(HorizonComparison comparison, IReadOnlyList<ChainedAuditEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(entries);
        var inWindow = entries
            .Where(e => e.Entry.Outcome == "Succeeded" && e.Entry.RecordedAt > comparison.At1 && e.Entry.RecordedAt <= comparison.At2)
            .ToList();
        var orders = comparison.Orders.Select(o =>
        {
            var id = Id(o.OrderNumber);
            var machines = o.Operations.SelectMany(op => new[] { op.MachineBefore, op.MachineAfter }).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var mine = inWindow
                .Where(e => e.Entry.EntityType switch
                {
                    "ProductionOrder" => e.Entry.EntityId == id,
                    "OrderOperation" => e.Entry.EntityId.StartsWith(id + "/", StringComparison.Ordinal),
                    "Machine" => machines.Contains(e.Entry.EntityId),
                    _ => false,
                })
                .Select(e => new HistoryEvidence(e.Entry.RecordedAt, "Audit", $"{e.Entry.Action} on {e.Entry.EntityType} {e.Entry.EntityId}" + (e.Entry.Reason is { Length: > 0 } r ? $" — “{r}”" : string.Empty) + $" ({e.Entry.Actor})", "audit:" + e.Sequence.ToString(CultureInfo.InvariantCulture)))
                .ToList();
            return mine.Count == 0 ? o : o with { Evidence = o.Evidence.Concat(mine).OrderBy(x => x.At).ToList() };
        }).ToList();
        return comparison with { Orders = orders, AuditIncluded = true };
    }

    internal static List<OperationChange> CompareOperations(List<ScheduledOperation> before, List<ScheduledOperation> after)
    {
        var changes = new List<OperationChange>();
        var a = before.ToDictionary(o => o.Sequence);
        var b = after.ToDictionary(o => o.Sequence);
        foreach (var seq in a.Keys.Union(b.Keys).Order())
        {
            a.TryGetValue(seq, out var x);
            b.TryGetValue(seq, out var y);
            var startX = x is { Scheduled: true } ? x.Start : null;
            var startY = y is { Scheduled: true } ? y.Start : null;
            decimal? shift = startX is { } s1 && startY is { } s2 ? ReportRun.Hours(s2 - s1) : null;
            var moved = shift is { } h && Math.Abs(h) * 60 >= (decimal)Tolerance.TotalMinutes;
            var machineChanged = !string.Equals(x?.MachineCode, y?.MachineCode, StringComparison.Ordinal) && x is not null && y is not null;
            var bindingChanged = !string.Equals(x?.BindingConstraint, y?.BindingConstraint, StringComparison.Ordinal);
            if (x is not null && y is not null && !moved && !machineChanged && !bindingChanged && x.Scheduled == y.Scheduled)
            {
                continue;
            }

            string text;
            if (x is null)
            {
                text = y!.Scheduled ? $"Op {seq}: newly scheduled to start {y.Start:yyyy-MM-dd HH:mm} UTC ({Binding(y)})." : $"Op {seq}: added but could not be scheduled ({Binding(y)}).";
            }
            else if (y is null)
            {
                text = $"Op {seq}: no longer in the schedule (completed, cancelled or the order left the horizon).";
            }
            else if (x.Scheduled && !y.Scheduled)
            {
                text = $"Op {seq}: could no longer be scheduled — {Binding(y)}.";
            }
            else if (!x.Scheduled && y.Scheduled)
            {
                text = $"Op {seq}: now scheduled to start {y.Start:yyyy-MM-dd HH:mm} UTC ({Binding(y)}).";
            }
            else
            {
                var parts = new List<string>();
                if (moved)
                {
                    parts.Add($"starts {Fmt(Math.Abs(shift!.Value))} h {(shift > 0 ? "later" : "earlier")}");
                }

                if (machineChanged)
                {
                    parts.Add($"moved from {x.MachineCode ?? "no machine"} to {y.MachineCode ?? "no machine"}");
                }

                parts.Add(bindingChanged
                    ? $"start was fixed by {Binding(x)}, now by {Binding(y)}"
                    : $"start still fixed by {Binding(y)}");
                text = $"Op {seq}: " + string.Join("; ", parts) + ".";
            }

            changes.Add(new OperationChange(
                seq, (y ?? x)!.WorkCenterCode, x?.MachineCode, y?.MachineCode, startX, startY,
                x is { Scheduled: true } ? x.End : null, y is { Scheduled: true } ? y.End : null,
                shift, x?.BindingConstraint, y?.BindingConstraint, text));
        }

        return changes;
    }

    internal static OrderChange Describe(long number, ScheduledOrder? a, ScheduledOrder? b, List<OperationChange> ops, List<HistoryEvidence> evidence)
    {
        var part = (b ?? a)!.PartNumber;
        decimal? shift = a?.ProjectedCompletion is { } p1 && b?.ProjectedCompletion is { } p2 ? ReportRun.Hours(p2 - p1) : null;
        var moved = shift is { } h && Math.Abs(h) * 60 >= (decimal)Tolerance.TotalMinutes;
        var change = a is null ? "Added"
            : b is null ? "Removed"
            : a.Status != b.Status ? "Status changed"
            : moved ? (shift > 0 ? "Later" : "Earlier")
            : ops.Count > 0 || a.DueDate != b.DueDate || a.Priority != b.Priority ? "Rescheduled"
            : "Unchanged";

        var lines = new List<string>();
        switch (change)
        {
            case "Added":
                lines.Add($"Entered the schedule ({Status(b!)}" + (b!.ProjectedCompletion is { } pc ? $", projected to finish {pc:yyyy-MM-dd HH:mm} UTC" : string.Empty) + ").");
                break;
            case "Removed":
                lines.Add("Left the schedule: the order was completed, cancelled, put on hold or fell outside the horizon.");
                break;
            default:
                if (a!.Status != b!.Status)
                {
                    lines.Add($"Delivery went from {Status(a)} to {Status(b)}.");
                }

                if (moved)
                {
                    lines.Add($"Projected completion {Fmt(Math.Abs(shift!.Value))} h {(shift > 0 ? "later" : "earlier")} ({a.ProjectedCompletion:yyyy-MM-dd HH:mm} → {b.ProjectedCompletion:yyyy-MM-dd HH:mm} UTC).");
                }

                if (a.DueDate != b.DueDate)
                {
                    lines.Add($"Due date changed {a.DueDate:yyyy-MM-dd} → {b.DueDate:yyyy-MM-dd}.");
                }

                if (a.Priority != b.Priority)
                {
                    lines.Add($"Priority changed {a.Priority} → {b.Priority}.");
                }

                break;
        }

        // The operation that moved most, with what now fixes its start, is the schedule's own answer to "why".
        if (ops.Where(o => o.ShiftHours is not null).MaxBy(o => Math.Abs(o.ShiftHours!.Value)) is { ShiftHours: { } biggest } driver && Math.Abs(biggest) > 0)
        {
            lines.Add("Largest move: " + driver.Explanation);
        }
        else if (ops.Count > 0)
        {
            // Nothing moved in time, but machines or binding constraints did: say which.
            lines.AddRange(ops.Take(2).Select(o => o.Explanation));
        }

        if (evidence.Count > 0)
        {
            lines.Add($"{evidence.Count} recorded event(s) in between bear on it.");
        }

        if (change == "Unchanged")
        {
            lines.Add("No change.");
        }

        return new OrderChange(
            number, part, change, a?.Status.ToString(), b?.Status.ToString(), a?.ProjectedCompletion, b?.ProjectedCompletion, shift,
            a?.DueDate, b?.DueDate, a?.Priority, b?.Priority, string.Join(" ", lines), ops, evidence);
    }

    private static List<HistoryEvidence> Evidence(
        long number,
        List<OperationChange> ops,
        List<ScheduledOperation> scheduled,
        IReadOnlyList<ConstraintRecord> constraints,
        IReadOnlyList<ProductionReport> reports,
        IReadOnlyList<NcrInfo> ncrs,
        IReadOnlyList<DowntimeWindow> downtime,
        DateTimeOffset at1,
        DateTimeOffset at2)
    {
        var list = new List<HistoryEvidence>();
        foreach (var c in constraints.Where(c => c.AffectedOrders.Contains(number)))
        {
            if (c.FirstSeen > at1 && c.FirstSeen <= at2)
            {
                list.Add(new(c.FirstSeen, "Constraint found", $"{c.Category}: {c.Finding}", "constraint:" + Id(c.ConstraintId)));
            }

            if (c.ResolvedAt is { } r && r > at1 && r <= at2)
            {
                list.Add(new(r, "Constraint resolved", $"{c.Category}: {c.Subject}" + (c.Resolution is { Length: > 0 } how ? $" — {how}" : string.Empty), "constraint:" + Id(c.ConstraintId)));
            }
        }

        foreach (var g in reports.Where(r => r.OrderNumber == number).GroupBy(r => r.Sequence).OrderBy(g => g.Key))
        {
            var good = g.Sum(r => r.GoodQuantity);
            var scrap = g.Sum(r => r.ScrapQuantity);
            var done = g.Any(r => r.CompletedOperation);
            list.Add(new(g.Max(r => r.ReportedAt), "Shop floor", $"Op {g.Key}: {Fmt(good)} good" + (scrap > 0 ? $", {Fmt(scrap)} scrap" : string.Empty) + (done ? ", operation completed" : string.Empty) + ".", "order:" + Id(number)));
        }

        foreach (var n in ncrs.Where(n => n.OrderNumber == number))
        {
            list.Add(new(n.RaisedAt, "NCR raised", $"NCR {Id(n.NcrId)}: {n.Defect} ({Fmt(n.Quantity)} {n.PartNumber}); holds the order until dispositioned.", "ncr:" + Id(n.NcrId)));
        }

        // Downtime on the machines of operations that moved, overlapping where those operations were or now are.
        foreach (var op in ops.Where(o => o.ShiftHours is not null || o.MachineBefore != o.MachineAfter || o.BindingBefore != o.BindingAfter))
        {
            var machines = new[] { op.MachineBefore, op.MachineAfter }
                .Concat(scheduled.Where(s => s.Sequence == op.Sequence).SelectMany(s => s.Interruptions.Select(_ => s.MachineCode)))
                .OfType<string>().Distinct(StringComparer.Ordinal).ToList();
            var from = new[] { op.StartBefore, op.StartAfter, at1 }.OfType<DateTimeOffset>().Min();
            var until = new[] { op.EndBefore, op.EndAfter, at2 }.OfType<DateTimeOffset>().Max();
            foreach (var w in downtime.Where(w => machines.Contains(w.MachineCode) && w.StartsAt < until && (w.EndsAt ?? DateTimeOffset.MaxValue) > from))
            {
                if (list.Any(e => e.Trace == Trace(w) && e.Text.StartsWith(w.MachineCode, StringComparison.Ordinal)))
                {
                    continue;
                }

                list.Add(new(w.StartsAt, "Downtime", $"{w.MachineCode}: {Kind(w.Kind)} from {w.StartsAt:yyyy-MM-dd HH:mm}" + (w.EndsAt is { } e ? $" to {e:yyyy-MM-dd HH:mm}" : " (still open)") + $" UTC — {w.Reason}", Trace(w)));
            }
        }

        return list.OrderBy(e => e.At).ToList();
    }

    private static List<HistoryEvidence> SiteEvidence(
        IReadOnlyList<ScheduleVersion> between,
        IReadOnlyList<ConstraintRecord> constraints,
        IReadOnlyList<DowntimeWindow> downtime,
        DateTimeOffset at1,
        DateTimeOffset at2,
        List<OrderChange> changes)
    {
        var list = new List<HistoryEvidence>();
        foreach (var v in between)
        {
            list.Add(new(v.ApprovedAt, "Schedule approved", $"Version {Id(v.VersionId)} approved: {v.Summary}", "schedule:" + Id(v.VersionId)));
        }

        var visibleOrders = changes.Select(c => c.OrderNumber).ToHashSet();
        foreach (var c in constraints.Where(c => c.FirstSeen > at1 && c.FirstSeen <= at2 && (c.AffectedOrders.Count == 0 || c.AffectedOrders.Any(visibleOrders.Contains))))
        {
            list.Add(new(c.FirstSeen, "Constraint found", $"{c.Category} — {c.Subject}: {c.Finding}", "constraint:" + Id(c.ConstraintId)));
        }

        foreach (var w in downtime.Where(w => w.StartsAt > at1 && w.StartsAt <= at2 && w.Kind != DowntimeKind.Planned))
        {
            list.Add(new(w.StartsAt, "Downtime", $"{w.MachineCode}: {Kind(w.Kind)} — {w.Reason}", Trace(w)));
        }

        return list.OrderBy(e => e.At).ToList();
    }

    private static string Trace(DowntimeWindow w) => w.Kind == DowntimeKind.MaintenanceOverdue || w.Id <= 0 ? "machine:" + w.MachineCode : "downtime:" + Id(w.Id);

    private static string Kind(DowntimeKind kind) => kind switch
    {
        DowntimeKind.Unplanned => "unplanned outage",
        DowntimeKind.Planned => "planned maintenance",
        DowntimeKind.MaintenanceOverdue => "locked out, PM overdue",
        DowntimeKind.AssetOutOfService => "asset out of service",
        _ => kind.ToString(),
    };

    private static string Binding(ScheduledOperation op) =>
        string.IsNullOrWhiteSpace(op.BindingDetail) ? op.BindingConstraint : $"{op.BindingConstraint}: {op.BindingDetail.TrimEnd('.', ' ')}";

    private static string Status(ScheduledOrder o) => o.Status switch
    {
        DeliveryStatus.OnTime => "on time",
        DeliveryStatus.AtRisk => "at risk",
        DeliveryStatus.Late => "projected late",
        DeliveryStatus.Unschedulable => "cannot be scheduled",
        _ => o.Status.ToString(),
    };

    private static HistoryVersion? Version(ScheduleVersion? v) =>
        v is null ? null : new HistoryVersion(v.VersionId, v.ApprovedAt, v.ApprovedBy, v.ProposedBy, v.Summary);

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    private static string Fmt(decimal d) => d.ToString("0.##", CultureInfo.InvariantCulture);
}
