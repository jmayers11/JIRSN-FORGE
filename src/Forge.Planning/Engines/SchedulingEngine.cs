using Forge.Execution.Contracts;
using Forge.Kernel.Engines;
using Forge.Quality.Contracts;

namespace Forge.Planning.Engines;

/// <summary>
/// Orders to schedule over a horizon. When <paramref name="Material"/> is given, an order that has
/// not started waits for its material: short or overdue material leaves it unscheduled, expected
/// material holds its first operation until the receipt's date, and a part made by another released order until that
/// order's planned finish; an overdue purchase order is planned from the earliest date the part could still come
/// (today plus the part's lead time from <paramref name="LeadTimeDays"/>) and the order is marked as estimated (ADR 0057
/// §4). When <paramref name="Quality"/> is
/// given, an inspection that has not happened yet is a planned gap after the inspected operation;
/// only an undecided nonconformance stops the order (ADR 0057 §2).
/// </summary>
public sealed record ScheduleRequest(
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<OrderInfo> Orders,
    IReadOnlyList<OrderMaterialStatus>? Material = null,
    IReadOnlyDictionary<long, OrderQuality>? Quality = null,
    IReadOnlyDictionary<string, int>? LeadTimeDays = null);

public sealed record ScheduledSegment(DateTimeOffset Start, DateTimeOffset End, IReadOnlyList<Guid> Operators);

/// <summary>A pause inside an operation caused by something other than the normal end of a shift.</summary>
public sealed record Interruption(DateTimeOffset From, DateTimeOffset Until, string Limiting, string Detail);

public sealed record ScheduledOperation(
    long OrderNumber,
    int Sequence,
    string Description,
    string WorkCenterCode,
    string? MachineCode,
    string ActivityCode,
    bool Scheduled,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    decimal PlannedMinutes,
    string BindingConstraint,
    string BindingDetail,
    IReadOnlyList<ScheduledSegment> Segments,
    IReadOnlyList<Interruption> Interruptions,
    IReadOnlyList<string> Operators,
    Guid? ProgramId = null,
    string? SiteCode = null,
    string? Setup = null);

public enum DeliveryStatus
{
    OnTime,
    AtRisk,
    Late,
    Unschedulable,
}

public sealed record ScheduledOrder(
    long OrderNumber,
    string PartNumber,
    int Priority,
    DateOnly DueDate,
    DateTimeOffset DueAt,
    DateTimeOffset? ProjectedCompletion,
    DeliveryStatus Status,
    string Explanation,
    Guid? ProgramId = null);

public sealed record ScheduleResult(DateTimeOffset From, DateTimeOffset To, IReadOnlyList<ScheduledOrder> Orders, IReadOnlyList<ScheduledOperation> Operations);

/// <summary>
/// Deterministic finite-capacity scheduler. Released orders are taken in a documented order
/// (priority, due date, order number); each operation goes on the earliest executable window of an
/// eligible machine, after its predecessor, with a qualified crew that is not already booked. A
/// machine and a person are each booked once at a time. Operations may continue across shifts.
/// For every operation the scheduler records what fixed its start (the binding constraint) and any
/// interruption, so "why does op 20 start Thursday?" has a recorded answer.
/// </summary>
public sealed class SchedulingEngine : IEngine<SiteSnapshot, ScheduleRequest, ScheduleResult>
{
    /// <summary>An order finishing within this margin of its due time is at risk.</summary>
    public static readonly TimeSpan AtRiskMargin = TimeSpan.FromHours(8);

    public string Name => "finite-scheduler";

    public string Version => "4";

    public EngineEvaluation<ScheduleResult> Evaluate(SiteSnapshot snapshot, ScheduleRequest parameters, DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(parameters);
        var horizon = new Span(WorkCalendar.Max(parameters.From, asOf), parameters.To);
        if (horizon.End <= horizon.Start || horizon.Length > TimeSpan.FromDays(62))
        {
            throw new ArgumentException("The horizon must end after it starts (and after now) and span at most 62 days.", nameof(parameters));
        }

        var timeline = new CapacityTimeline(snapshot, horizon);
        var state = new Bookings();
        var operations = new List<ScheduledOperation>();
        var orders = new List<ScheduledOrder>();

        var queue = parameters.Orders
            .Where(o => o.Status == OrderStatus.Released)
            .OrderBy(o => o.Priority)
            .ThenBy(o => o.DueDate)
            .ThenBy(o => o.OrderNumber)
            .ToList();
        var released = queue.ToDictionary(o => o.OrderNumber);
        var material = (parameters.Material ?? []).ToDictionary(m => m.OrderNumber);
        var done = new Dictionary<long, ScheduledOrder>();
        var visiting = new HashSet<long>();

        // Components first (ADR 0057 §4.1): before an order is planned, the released orders making the parts it waits
        // for are planned, so it can start from their planned finish. They take their consumer's place in the queue.
        foreach (var order in queue)
        {
            Plan(order);
        }

        // A sub-assembly is late when the order waiting for it is late because of it (ADR 0057 §4.5).
        foreach (var (number, parent) in done.ToList())
        {
            foreach (var sub in SubOrders(material.GetValueOrDefault(number)).Where(done.ContainsKey))
            {
                var child = done[sub];
                if (parent.Status is DeliveryStatus.Late or DeliveryStatus.Unschedulable && child.Status is DeliveryStatus.OnTime or DeliveryStatus.AtRisk
                    && parent.Explanation.Contains($"(order {sub})", StringComparison.Ordinal))
                {
                    done[sub] = child with
                    {
                        Status = DeliveryStatus.Late,
                        Explanation = child.Explanation + $" Late for order {number} ({parent.PartNumber}), which waits for it and is projected {(parent.ProjectedCompletion is { } p ? $"to finish {p:u}" : "not to be schedulable")}, due {parent.DueAt:u}.",
                    };
                }
            }
        }

        orders.AddRange(queue.Select(o => done[o.OrderNumber]));

        void Plan(OrderInfo order)
        {
            if (done.ContainsKey(order.OrderNumber) || !visiting.Add(order.OrderNumber))
            {
                return; // planned already, or a loop in the supply chain (each order is planned once)
            }

            foreach (var sub in SubOrders(material.GetValueOrDefault(order.OrderNumber)))
            {
                if (released.TryGetValue(sub, out var component))
                {
                    Plan(component);
                }
            }

            DateTimeOffset? predecessorEnd = horizon.Start;
            var predecessorSequence = (int?)null;
            var orderOps = new List<ScheduledOperation>();
            var gate = MaterialGate(snapshot, horizon, order, material.GetValueOrDefault(order.OrderNumber), done, parameters.LeadTimeDays);
            var (materialStart, materialBlock) = (gate.Start, gate.Block);
            OrderQuality? quality = null;
            parameters.Quality?.TryGetValue(order.OrderNumber, out quality);
            foreach (var op in order.Operations.Where(o => o.Status != OperationStatus.Complete).OrderBy(o => o.Sequence))
            {
                (string Limiting, string Detail)? waited = null;
                if (predecessorSequence is null && materialStart is { } arrives && arrives > horizon.Start)
                {
                    predecessorEnd = arrives;
                    waited = ("Material", materialBlock ?? "Waiting for material.");
                }

                // Inspections after earlier operations that have not passed yet take their planned time; they do not stop the plan.
                if (quality is not null && predecessorEnd is { } flow)
                {
                    var (inspected, gap) = InspectionGap(quality, predecessorSequence, op.Sequence);
                    var ready = (predecessorSequence is null ? horizon.Start : flow) + gap;
                    if (inspected.Count > 0 && gap > TimeSpan.Zero && ready > flow)
                    {
                        predecessorEnd = ready;
                        waited = ("Inspection", InspectionDetail(quality, inspected, ready));
                    }
                }

                var blocking = quality is { OpenNcrs.Count: > 0 } ? OrderReadinessEngine.QualityCheck(quality with { PlannedSequences = [] }, op.Sequence) : null;
                var scheduled = (blocking is { Outcome: EvidenceOutcome.Fail }
                    ? Unscheduled(order, op, "Quality", blocking.Detail)
                    : predecessorSequence is null && materialStart is null && materialBlock is not null
                    ? Unscheduled(order, op, "Material", materialBlock)
                    : predecessorEnd is { } earliest
                        ? Schedule(timeline, state, order, op, earliest, predecessorSequence, waited)
                        : Unscheduled(order, op, "Predecessor", $"Operation {predecessorSequence} could not be scheduled.")) with { ProgramId = order.ProgramId,
                            SiteCode = snapshot.SafetyAreas.FirstOrDefault(area => area.Code == snapshot.WorkCenters.FirstOrDefault(center => center.Code == op.WorkCenterCode)?.SafetyAreaCode)?.SiteCode };
                orderOps.Add(scheduled);
                predecessorEnd = scheduled.End;
                predecessorSequence = op.Sequence;
            }

            // An inspection after the last operation is part of the order's flow too: the order is done when it has passed.
            (DateTimeOffset At, string Detail)? finalInspection = null;
            if (quality is not null && predecessorEnd is { } last)
            {
                var (inspected, gap) = InspectionGap(quality, predecessorSequence, int.MaxValue);
                var ready = (predecessorSequence is null ? horizon.Start : last) + gap;
                if (inspected.Count > 0 && gap > TimeSpan.Zero)
                {
                    finalInspection = (ready, InspectionDetail(quality, inspected, ready));
                }
            }

            operations.AddRange(orderOps);
            var delivered = Deliver(snapshot, order, orderOps, finalInspection) with { ProgramId = order.ProgramId };
            if (gate.Estimated is { } estimate)
            {
                delivered = delivered with { Explanation = delivered.Explanation + " " + estimate };
            }

            done[order.OrderNumber] = delivered;
            visiting.Remove(order.OrderNumber);
        }


        var late = orders.Where(o => o.Status != DeliveryStatus.OnTime).ToList();
        var root = new EvidenceNode(
            "Schedule",
            late.Count == 0 ? EvidenceOutcome.Pass : EvidenceOutcome.Fail,
            $"{orders.Count} released order(s) scheduled from {horizon.Start:u}; {late.Count} at risk, late or unschedulable.")
        {
            Children = orders.Select(o => new EvidenceNode(o.Status.ToString(), o.Status == DeliveryStatus.OnTime ? EvidenceOutcome.Pass : EvidenceOutcome.Fail, $"Order {o.OrderNumber}: {o.Explanation}")).ToList(),
        };
        return new EngineEvaluation<ScheduleResult>(new ScheduleResult(horizon.Start, horizon.End, orders, operations), root);
    }

    /// <summary>When an order that has not started may begin, given its material.</summary>
    /// <param name="Start">When it may start, if later than the start of the horizon.</param>
    /// <param name="Block">Why it waits (with a start) or cannot be planned (without one); null when nothing holds it.</param>
    /// <param name="Estimated">Set when the start rests on an estimate, such as an overdue purchase order with no new date.</param>
    private sealed record Gate(DateTimeOffset? Start, string? Block, string? Estimated = null);

    /// <summary>The released orders that make parts allocated to an order (its sub-assemblies), in allocation order.</summary>
    private static IEnumerable<long> SubOrders(OrderMaterialStatus? status) =>
        (status?.Lines ?? []).SelectMany(l => l.Allocations).Select(a => SubOrder(a.Source)).OfType<long>().Distinct();

    private static long? SubOrder(string source) =>
        source.StartsWith("WO", StringComparison.Ordinal) && long.TryParse(source.AsSpan(2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>
    /// When an order that has not started may begin, given its material (ADR 0057 §4). Stock is there now. A part made by
    /// a released order comes at that order's planned finish in this schedule; one that cannot be planned stops this
    /// order too, with the cause. A part made by an order not in the schedule, or bought, comes on its due or promised
    /// date; an overdue purchase order with no new date is planned from today plus the part's lead time and marked as
    /// estimated, "late, needing a supplier date". Shortfalls cannot be planned.
    /// </summary>
    private static Gate MaterialGate(SiteSnapshot snapshot, Span horizon, OrderInfo order, OrderMaterialStatus? status,
        IReadOnlyDictionary<long, ScheduledOrder> planned, IReadOnlyDictionary<string, int>? leadTimes)
    {
        if (status is null || status.State == MaterialState.Ready || order.Operations.Any(o => o.Status != OperationStatus.Pending))
        {
            return new Gate(null, null);
        }

        if (status.State == MaterialState.Shortfall || status.ReadyOn is null)
        {
            return new Gate(null, status.Summary);
        }

        var site = WorkCalendar.SiteOfWorkCenter(snapshot, order.Operations.OrderBy(o => o.Sequence).First().WorkCenterCode);
        var today = WorkCalendar.LocalDate(snapshot, site, horizon.Start);
        DateTimeOffset OnDay(DateOnly day) => WorkCalendar.EndOfDay(snapshot, site, day.AddDays(-1)).ToUniversalTime();
        var start = horizon.Start;
        var reasons = new List<string>();
        var estimates = new List<string>();
        foreach (var line in status.Lines)
        {
            foreach (var allocation in line.Allocations.Where(a => a.DueDate is not null))
            {
                var due = allocation.DueDate!.Value;
                DateTimeOffset arrives;
                if (SubOrder(allocation.Source) is { } sub && planned.TryGetValue(sub, out var component))
                {
                    if (component.ProjectedCompletion is not { } finish)
                    {
                        return new Gate(null, $"Waiting for {line.PartNumber} (order {sub}), which cannot be planned: {Cause(component.Explanation)}");
                    }

                    arrives = finish;
                    reasons.Add($"{line.PartNumber} (order {sub}), planned to finish {finish:u}" + (Cause(component.Explanation) is { Length: > 0 } why && component.Explanation.Contains("waited for Material", StringComparison.Ordinal) ? $", which {why}" : string.Empty));
                }
                else if (due < today)
                {
                    var lead = leadTimes?.GetValueOrDefault(line.PartNumber) ?? 0;
                    var earliest = today.AddDays(Math.Max(0, lead));
                    arrives = OnDay(earliest);
                    var what = SubOrder(allocation.Source) is { } other ? $"order {other}" : allocation.Description;
                    reasons.Add($"{line.PartNumber} from {what}, due {due:yyyy-MM-dd}, not received; estimated {earliest:yyyy-MM-dd}");
                    estimates.Add($"Late, needs a supplier date: {line.PartNumber} from {what} was due {due:yyyy-MM-dd} and has not been received; planned from {earliest:yyyy-MM-dd} (today plus {lead} day(s) lead time), an estimate.");
                }
                else
                {
                    arrives = OnDay(due);
                    reasons.Add($"{line.PartNumber} from {allocation.Description}, due {due:yyyy-MM-dd}");
                }

                start = WorkCalendar.Max(start, arrives);
            }
        }

        if (reasons.Count == 0)
        {
            // A status with no allocation lines carries only its ready date and summary.
            start = WorkCalendar.Max(start, OnDay(status.ReadyOn.Value < today ? today : status.ReadyOn.Value));
            return start >= horizon.End ? new Gate(null, $"{status.Summary} That is after the end of the planning horizon.")
                : start > horizon.Start ? new Gate(start, status.Summary) : new Gate(null, null);
        }

        var estimated = estimates.Count == 0 ? null : string.Join(" ", estimates);
        var block = "Waiting for " + string.Join("; ", reasons) + ".";
        if (start >= horizon.End)
        {
            return new Gate(null, $"{block} That is after the end of the planning horizon.", estimated);
        }

        return start > horizon.Start ? new Gate(start, block, estimated) : new Gate(null, null, estimated);
    }

    /// <summary>The cause in an order's explanation: what its first waiting operation waited for, in lower case.</summary>
    private static string Cause(string explanation)
    {
        var marker = explanation.IndexOf("waited for Material: ", StringComparison.Ordinal);
        if (marker >= 0)
        {
            var text = explanation[(marker + "waited for Material: ".Length)..];
            var ends = new[] { text.IndexOf("; operation ", StringComparison.Ordinal), text.IndexOf(". ", StringComparison.Ordinal) }.Where(i => i >= 0).ToList();
            text = (ends.Count > 0 ? text[..ends.Min()] : text).TrimEnd('.');
            return text.StartsWith("Waiting for ", StringComparison.Ordinal) ? "is waiting for " + text["Waiting for ".Length..] : text;
        }

        var cannot = explanation.IndexOf("cannot be scheduled: ", StringComparison.Ordinal);
        return cannot >= 0 ? explanation[(cannot + "cannot be scheduled: ".Length)..].TrimEnd('.') : explanation.TrimEnd('.');
    }

    /// <summary>
    /// The inspection points between the previous unfinished operation (or the start, when there is none) and this
    /// operation that have not passed, and their planned time together. An inspection after a finished operation is
    /// due now; one after an operation still to do follows it.
    /// </summary>
    private static (IReadOnlyList<int> Points, TimeSpan Gap) InspectionGap(OrderQuality quality, int? previous, int sequence)
    {
        var points = quality.PlannedSequences
            .Where(s => s < sequence && (previous is null || s >= previous) && !quality.PassedSequences.Contains(s))
            .Order()
            .ToList();
        return (points, TimeSpan.FromMinutes(points.Sum(quality.MinutesAfter)));
    }

    private static string InspectionDetail(OrderQuality quality, IReadOnlyList<int> points, DateTimeOffset until)
    {
        var parts = points.Select(s => (quality.FailedSequences?.Contains(s) ?? false ? "re-inspection" : "inspection") + $" after operation {s} ({quality.MinutesAfter(s)} min)");
        return $"Waiting for the {string.Join(" and ", parts)}, planned until {until:u}.";
    }

    private static ScheduledOperation Schedule(CapacityTimeline timeline, Bookings state, OrderInfo order, OrderOperationInfo op, DateTimeOffset earliest, int? predecessor, (string Limiting, string Detail)? waited = null)
    {
        var snapshot = timeline.Snapshot;
        if (!snapshot.WorkCenters.Any(w => w.Code == op.WorkCenterCode && w.Active))
            return Unscheduled(order, op, "Equipment", $"Work center {op.WorkCenterCode} is inactive or missing.");
        var minutes = TimeSpan.FromMinutes((double)RemainingMinutes(order, op));
        var options = new List<(WorkRequirement Requirement, Placement Placement, string? Setup, decimal Minutes)>();
        var holdBlocks = new List<string>();

        if (op.EligibleMachines.Count == 0)
        {
            var requirement = new WorkRequirement(op.WorkCenterCode, null, op.ActivityCode, [], op.RequiredQualifications, op.CrewSize);
            if (IsHeld(null))
            {
                return Unscheduled(order, op, "Holds", holdBlocks[^1]);
            }

            options.Add((requirement, Place(timeline, state, requirement, minutes, earliest), null, RemainingMinutes(order, op)));
        }
        else
        {
            foreach (var machine in op.EligibleMachines.Order(StringComparer.Ordinal))
            {
                var profile = snapshot.MachineActivities.FirstOrDefault(m => m.MachineCode == machine && m.ActivityCode == op.ActivityCode);
                if (profile is null)
                {
                    continue;
                }

                if (IsHeld(machine))
                {
                    continue;
                }

                var requirement = new WorkRequirement(op.WorkCenterCode, machine, op.ActivityCode, profile.OperatorQualifications, op.RequiredQualifications, op.CrewSize, profile.RequiresMachineAuthorization);
                options.Add(WithSetup(timeline, state, order, op, requirement, machine, minutes, earliest));
            }
        }

        if (options.Count == 0)
        {
            if (holdBlocks.Count > 0)
            {
                return Unscheduled(order, op, "Holds", string.Join(" ", holdBlocks.Distinct(StringComparer.Ordinal)));
            }

            return Unscheduled(order, op, "RequirementProfile", $"None of {string.Join(", ", op.EligibleMachines)} is defined for {op.ActivityCode}.");
        }

        var best = options
            .OrderBy(o => o.Placement.End ?? DateTimeOffset.MaxValue)
            .ThenBy(o => o.Requirement.MachineCode ?? string.Empty, StringComparer.Ordinal)
            .First();
        var placement = best.Placement;
        if (placement.End is null)
        {
            var blocker = placement.FirstBlocker ?? ("Horizon", "Not enough executable time before the end of the planning horizon.");
            return Unscheduled(order, op, blocker.Limiting, $"Cannot complete within the horizon. {blocker.Detail}") with { MachineCode = best.Requirement.MachineCode };
        }

        foreach (var segment in placement.Segments)
        {
            if (best.Requirement.MachineCode is { } m)
            {
                state.Book(m, segment.Start, segment.End, order.OrderNumber, op.Sequence, op.SetupFamily);
            }

            foreach (var person in segment.Operators)
            {
                state.BookPerson(person, segment.Start, segment.End, order.OrderNumber, op.Sequence);
            }
        }

        // Waiting for material explains the start even when the calendar also had a gap then.
        var (binding, detail) = waited
            ?? placement.FirstBlocker
            ?? (predecessor is { } p && earliest > timeline.Horizon.Start
                ? ("Predecessor", $"Operation {p} finishes at {earliest:u}.")
                : ("Now", "Starts at the earliest possible time."));
        var names = placement.Segments.SelectMany(s => s.Operators).Distinct()
            .Select(id => snapshot.Person(id)?.Name ?? id.ToString())
            .ToList();
        return new ScheduledOperation(
            order.OrderNumber,
            op.Sequence,
            op.Description,
            op.WorkCenterCode,
            best.Requirement.MachineCode,
            op.ActivityCode,
            true,
            placement.Segments[0].Start,
            placement.End,
            best.Minutes,
            binding,
            detail,
            placement.Segments,
            placement.Interruptions,
            names,
            Setup: best.Setup);

        bool IsHeld(string? machine)
        {
            var readiness = ExecutableChecks.Holds(snapshot, order.OrderNumber, order.PartNumber, op.Sequence, op.WorkCenterCode, machine, order.ProgramId);
            if (readiness.Outcome != EvidenceOutcome.Fail)
            {
                return false;
            }

            holdBlocks.Add(readiness.Detail);
            return true;
        }
    }

    /// <summary>
    /// Places an operation on one machine. When it names a set-up family and the machine's previous job in this schedule
    /// is the same family, it gets the reduced same-family set-up; otherwise the full one (ADR 0057 §3.2). Which one, and
    /// why, is recorded. Only work not yet started can save set-up, and only jobs in this schedule count as previous.
    /// </summary>
    private static (WorkRequirement Requirement, Placement Placement, string? Setup, decimal Minutes) WithSetup(
        CapacityTimeline timeline, Bookings state, OrderInfo order, OrderOperationInfo op, WorkRequirement requirement, string machine, TimeSpan minutes, DateTimeOffset earliest)
    {
        var full = Place(timeline, state, requirement, minutes, earliest);
        var remaining = RemainingMinutes(order, op);
        if (op is not { Status: OperationStatus.Pending, SetupFamily: { } family, SetupMinutes: { } setup, FamilySetupMinutes: { } same } || same >= setup)
        {
            return (requirement, full, null, remaining);
        }

        Booking? Previous(Placement p) => p.Segments.Count == 0 ? null : state.PreviousOnMachine(machine, p.Segments[0].Start);
        var saved = setup - same;
        var reduced = Place(timeline, state, requirement, minutes - TimeSpan.FromMinutes((double)saved), earliest);
        if (reduced.End is not null && Previous(reduced) is { Family: { } before } previous && before == family)
        {
            return (requirement, reduced, $"Same-family set-up: {Quantities.Text(same)} min instead of {Quantities.Text(setup)}, after order {previous.Order} operation {previous.Sequence} ({family}) on {machine}.", remaining - saved);
        }

        var after = Previous(full);
        var why = after is null ? $"no earlier job on {machine} in this schedule" : after.Family is null ? $"the previous job on {machine} (order {after.Order}) has no set-up family" : $"the previous job on {machine} (order {after.Order}) is {after.Family}, not {family}";
        return (requirement, full, $"Full set-up ({Quantities.Text(setup)} min): {why}.", remaining);
    }

    /// <summary>
    /// Work left on an operation. Started work keeps the share of its planned time that matches the
    /// quantity not yet reported good; untouched work keeps all of it.
    /// </summary>
    public static decimal RemainingMinutes(OrderInfo order, OrderOperationInfo op)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(op);
        if (op.Status != OperationStatus.InProgress || order.Quantity <= 0)
        {
            return op.PlannedMinutes;
        }

        var left = Math.Clamp(1 - (op.GoodQuantity / order.Quantity), 0m, 1m);
        return decimal.Round(op.PlannedMinutes * left, 4);
    }

    private sealed record Placement(IReadOnlyList<ScheduledSegment> Segments, IReadOnlyList<Interruption> Interruptions, (string Limiting, string Detail)? FirstBlocker, DateTimeOffset? End);

    /// <summary>Simulates placing work; does not change bookings.</summary>
    private static Placement Place(CapacityTimeline timeline, Bookings state, WorkRequirement requirement, TimeSpan minutes, DateTimeOffset earliest)
    {
        var intervals = timeline.For(requirement);
        var segments = new List<ScheduledSegment>();
        var interruptions = new List<Interruption>();
        (string Limiting, string Detail)? firstBlocker = null;
        (string Limiting, string Detail)? lastExecutionBlocker = null;
        var cursor = earliest;
        var remaining = minutes;

        void Block(string limiting, string detail, DateTimeOffset until)
        {
            if (limiting != "Calendar") lastExecutionBlocker = (limiting, detail);
            if (segments.Count == 0)
            {
                firstBlocker ??= (limiting, detail);
            }
            else if (limiting != "Calendar")
            {
                // Normal shift ends are not interruptions; anything else is.
                if (interruptions.Count > 0 && interruptions[^1].Limiting == limiting && interruptions[^1].Until == cursor)
                {
                    interruptions[^1] = interruptions[^1] with { Until = until };
                }
                else
                {
                    interruptions.Add(new Interruption(cursor, until, limiting, detail));
                }
            }
        }

        if (remaining <= TimeSpan.Zero)
        {
            // Zero-duration milestones still require an eligible resource and valid tooling;
            // they cannot bypass the same execution gates as a timed operation.
            var ready = intervals.FirstOrDefault(i => i.Executable && i.End > earliest);
            if (ready is null)
            {
                var blocked = intervals.FirstOrDefault(i => i.End > earliest);
                return new Placement([], [], blocked is null ? ("Calendar", $"{requirement.WorkCenterCode} has no working time left in the horizon.")
                    : (blocked.Limiting ?? "Unknown", blocked.Detail), null);
            }
            var start = WorkCalendar.Max(earliest, ready.Start);
            var first = intervals.FirstOrDefault(i => i.End > earliest && !i.Executable && i.Start < start);
            return new Placement([new ScheduledSegment(start, start, [])], [], first is null ? null : (first.Limiting ?? "Unknown", first.Detail), start);
        }

        while (remaining > TimeSpan.Zero)
        {
            var interval = intervals.FirstOrDefault(i => i.End > cursor);
            if (interval is null)
            {
                return new Placement(segments, interruptions, lastExecutionBlocker ?? firstBlocker ?? ("Calendar", $"{requirement.WorkCenterCode} has no working time left in the horizon."), null);
            }

            if (interval.Start > cursor)
            {
                Block("Calendar", $"{requirement.WorkCenterCode} is not working until {interval.Start:u}.", interval.Start);
                cursor = interval.Start;
                continue;
            }

            if (!interval.Executable)
            {
                Block(interval.Limiting ?? "Unknown", interval.Detail, interval.End);
                cursor = interval.End;
                continue;
            }

            if (requirement.MachineCode is { } machine && state.MachineBusyAt(machine, cursor) is { } busy)
            {
                var until = WorkCalendar.Min(busy.End, interval.End);
                Block("MachineBusy", $"{machine} is booked for order {busy.Order} operation {busy.Sequence} until {busy.End:u}.", until);
                cursor = until;
                continue;
            }

            var free = interval.Candidates.Where(p => state.PersonBusyAt(p, cursor) is null).ToList();
            if (free.Count < requirement.Crew)
            {
                var releases = interval.Candidates.Select(p => state.PersonBusyAt(p, cursor)).OfType<Booking>().Select(b => b.End).ToList();
                var until = WorkCalendar.Min(releases.Count == 0 ? interval.End : releases.Min(), interval.End);
                Block("OperatorsBusy", $"Qualified people for {requirement.ActivityCode} are booked on other work until {until:u}.", until);
                cursor = until;
                continue;
            }

            var crew = free.Take(requirement.Crew).ToList();
            var end = WorkCalendar.Min(interval.End, cursor + remaining);
            if (requirement.MachineCode is { } m && state.NextMachineBooking(m, cursor) is { } nextMachine)
            {
                end = WorkCalendar.Min(end, nextMachine);
            }

            foreach (var person in crew)
            {
                if (state.NextPersonBooking(person, cursor) is { } nextPerson)
                {
                    end = WorkCalendar.Min(end, nextPerson);
                }
            }

            segments.Add(new ScheduledSegment(cursor, end, crew));
            remaining -= end - cursor;
            cursor = end;
        }

        return new Placement(segments, interruptions, firstBlocker, cursor);
    }

    private static ScheduledOperation Unscheduled(OrderInfo order, OrderOperationInfo op, string limiting, string detail) => new(
        order.OrderNumber, op.Sequence, op.Description, op.WorkCenterCode, null, op.ActivityCode, false, null, null, op.PlannedMinutes, limiting, detail, [], [], []);

    private static ScheduledOrder Deliver(SiteSnapshot snapshot, OrderInfo order, IReadOnlyList<ScheduledOperation> ops, (DateTimeOffset At, string Detail)? finalInspection = null)
    {
        var site = WorkCalendar.SiteOfWorkCenter(snapshot, ops.Count > 0 ? ops[0].WorkCenterCode : string.Empty);
        var dueAt = WorkCalendar.EndOfDay(snapshot, site, order.DueDate);
        if (ops.Count == 0 && finalInspection is { } only)
        {
            var onlyStatus = only.At > dueAt ? DeliveryStatus.Late : dueAt - only.At < AtRiskMargin ? DeliveryStatus.AtRisk : DeliveryStatus.OnTime;
            return new ScheduledOrder(order.OrderNumber, order.PartNumber, order.Priority, order.DueDate, dueAt, only.At, onlyStatus, $"Projected completion {only.At:u}, due {dueAt:u}. {only.Detail}");
        }

        if (ops.Count == 0)
        {
            return new ScheduledOrder(order.OrderNumber, order.PartNumber, order.Priority, order.DueDate, dueAt, null, DeliveryStatus.OnTime, "No remaining operations.");
        }

        var blocked = ops.FirstOrDefault(o => !o.Scheduled);
        if (blocked is not null)
        {
            return new ScheduledOrder(order.OrderNumber, order.PartNumber, order.Priority, order.DueDate, dueAt, null, DeliveryStatus.Unschedulable, $"Operation {blocked.Sequence} cannot be scheduled: {blocked.BindingConstraint}. {blocked.BindingDetail}");
        }

        var completion = finalInspection is { } final && final.At > ops[^1].End!.Value ? final.At : ops[^1].End!.Value;
        var status = completion > dueAt ? DeliveryStatus.Late : dueAt - completion < AtRiskMargin ? DeliveryStatus.AtRisk : DeliveryStatus.OnTime;
        var cause = ops
            .Select(o => (Op: o, Delay: o.Interruptions.Aggregate(TimeSpan.Zero, (t, i) => t + (i.Until - i.From)), Binding: o.BindingConstraint is "Now" or "Predecessor" ? null : o))
            .Where(x => x.Binding is not null || x.Delay > TimeSpan.Zero)
            .Select(x => x.Binding is not null
                ? $"operation {x.Op.Sequence} waited for {x.Op.BindingConstraint}: {x.Op.BindingDetail}"
                : $"operation {x.Op.Sequence} was interrupted by {string.Join(", ", x.Op.Interruptions.Select(i => i.Limiting).Distinct())}")
            .ToList();
        if (finalInspection is { } f && completion == f.At)
        {
            cause.Add(char.ToLowerInvariant(f.Detail[0]) + f.Detail[1..].TrimEnd('.'));
        }

        var explanation = $"Projected completion {completion:u}, due {dueAt:u}." + (cause.Count == 0 ? string.Empty : " Delays: " + string.Join("; ", cause) + ".");
        return new ScheduledOrder(order.OrderNumber, order.PartNumber, order.Priority, order.DueDate, dueAt, completion, status, explanation);
    }

    private sealed record Booking(DateTimeOffset Start, DateTimeOffset End, long Order, int Sequence, string? Family = null);

    private sealed class Bookings
    {
        private readonly Dictionary<string, List<Booking>> machines = new(StringComparer.Ordinal);
        private readonly Dictionary<Guid, List<Booking>> people = [];

        public void Book(string machine, DateTimeOffset start, DateTimeOffset end, long order, int sequence, string? family = null) =>
            Get(machines, machine).Add(new Booking(start, end, order, sequence, family));

        /// <summary>The job booked on the machine just before an instant (the latest that ended at or before it).</summary>
        public Booking? PreviousOnMachine(string machine, DateTimeOffset at) =>
            machines.TryGetValue(machine, out var list) ? list.Where(b => b.End <= at).OrderByDescending(b => b.End).FirstOrDefault() : null;

        public void BookPerson(Guid person, DateTimeOffset start, DateTimeOffset end, long order, int sequence) =>
            Get(people, person).Add(new Booking(start, end, order, sequence));

        public Booking? MachineBusyAt(string machine, DateTimeOffset at) =>
            machines.TryGetValue(machine, out var list) ? list.FirstOrDefault(b => b.Start <= at && at < b.End) : null;

        public Booking? PersonBusyAt(Guid person, DateTimeOffset at) =>
            people.TryGetValue(person, out var list) ? list.FirstOrDefault(b => b.Start <= at && at < b.End) : null;

        public DateTimeOffset? NextMachineBooking(string machine, DateTimeOffset after) =>
            machines.TryGetValue(machine, out var list) ? list.Where(b => b.Start > after).Select(b => (DateTimeOffset?)b.Start).Min() : null;

        public DateTimeOffset? NextPersonBooking(Guid person, DateTimeOffset after) =>
            people.TryGetValue(person, out var list) ? list.Where(b => b.Start > after).Select(b => (DateTimeOffset?)b.Start).Min() : null;

        private static List<Booking> Get<TKey>(Dictionary<TKey, List<Booking>> map, TKey key)
            where TKey : notnull
        {
            if (!map.TryGetValue(key, out var list))
            {
                list = [];
                map[key] = list;
            }

            return list;
        }
    }
}
