using Forge.Execution.Contracts;
using Forge.Kernel.Engines;

namespace Forge.Planning.Engines;

public sealed record ScheduleRequest(DateTimeOffset From, DateTimeOffset To, IReadOnlyList<OrderInfo> Orders);

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
    Guid? ProgramId = null);

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

    public string Version => "1";

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
            .ThenBy(o => o.OrderNumber);

        foreach (var order in queue)
        {
            DateTimeOffset? predecessorEnd = horizon.Start;
            var predecessorSequence = (int?)null;
            var orderOps = new List<ScheduledOperation>();
            foreach (var op in order.Operations.Where(o => o.Status != OperationStatus.Complete).OrderBy(o => o.Sequence))
            {
                var scheduled = (predecessorEnd is { } earliest
                    ? Schedule(timeline, state, order, op, earliest, predecessorSequence)
                    : Unscheduled(order, op, "Predecessor", $"Operation {predecessorSequence} could not be scheduled.")) with { ProgramId = order.ProgramId };
                orderOps.Add(scheduled);
                predecessorEnd = scheduled.End;
                predecessorSequence = op.Sequence;
            }

            operations.AddRange(orderOps);
            orders.Add(Deliver(snapshot, order, orderOps) with { ProgramId = order.ProgramId });
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

    private static ScheduledOperation Schedule(CapacityTimeline timeline, Bookings state, OrderInfo order, OrderOperationInfo op, DateTimeOffset earliest, int? predecessor)
    {
        var snapshot = timeline.Snapshot;
        var minutes = TimeSpan.FromMinutes((double)op.PlannedMinutes);
        var options = new List<(WorkRequirement Requirement, Placement Placement)>();

        if (op.EligibleMachines.Count == 0)
        {
            var requirement = new WorkRequirement(op.WorkCenterCode, null, op.ActivityCode, [], op.RequiredQualifications, op.CrewSize);
            options.Add((requirement, Place(timeline, state, requirement, minutes, earliest)));
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

                var requirement = new WorkRequirement(op.WorkCenterCode, machine, op.ActivityCode, profile.OperatorQualifications, op.RequiredQualifications, op.CrewSize);
                options.Add((requirement, Place(timeline, state, requirement, minutes, earliest)));
            }
        }

        if (options.Count == 0)
        {
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
                state.Book(m, segment.Start, segment.End, order.OrderNumber, op.Sequence);
            }

            foreach (var person in segment.Operators)
            {
                state.BookPerson(person, segment.Start, segment.End, order.OrderNumber, op.Sequence);
            }
        }

        var (binding, detail) = placement.FirstBlocker
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
            op.PlannedMinutes,
            binding,
            detail,
            placement.Segments,
            placement.Interruptions,
            names);
    }

    private sealed record Placement(IReadOnlyList<ScheduledSegment> Segments, IReadOnlyList<Interruption> Interruptions, (string Limiting, string Detail)? FirstBlocker, DateTimeOffset? End);

    /// <summary>Simulates placing work; does not change bookings.</summary>
    private static Placement Place(CapacityTimeline timeline, Bookings state, WorkRequirement requirement, TimeSpan minutes, DateTimeOffset earliest)
    {
        var intervals = timeline.For(requirement);
        var segments = new List<ScheduledSegment>();
        var interruptions = new List<Interruption>();
        (string Limiting, string Detail)? firstBlocker = null;
        var cursor = earliest;
        var remaining = minutes;

        void Block(string limiting, string detail, DateTimeOffset until)
        {
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
            return new Placement([new ScheduledSegment(earliest, earliest, [])], [], null, earliest);
        }

        while (remaining > TimeSpan.Zero)
        {
            var interval = intervals.FirstOrDefault(i => i.End > cursor);
            if (interval is null)
            {
                return new Placement(segments, interruptions, firstBlocker ?? ("Calendar", $"{requirement.WorkCenterCode} has no working time left in the horizon."), null);
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

    private static ScheduledOrder Deliver(SiteSnapshot snapshot, OrderInfo order, IReadOnlyList<ScheduledOperation> ops)
    {
        var site = WorkCalendar.SiteOfWorkCenter(snapshot, ops.Count > 0 ? ops[0].WorkCenterCode : string.Empty);
        var dueAt = WorkCalendar.EndOfDay(snapshot, site, order.DueDate);
        if (ops.Count == 0)
        {
            return new ScheduledOrder(order.OrderNumber, order.PartNumber, order.Priority, order.DueDate, dueAt, null, DeliveryStatus.OnTime, "No remaining operations.");
        }

        var blocked = ops.FirstOrDefault(o => !o.Scheduled);
        if (blocked is not null)
        {
            return new ScheduledOrder(order.OrderNumber, order.PartNumber, order.Priority, order.DueDate, dueAt, null, DeliveryStatus.Unschedulable, $"Operation {blocked.Sequence} cannot be scheduled: {blocked.BindingConstraint}. {blocked.BindingDetail}");
        }

        var completion = ops[^1].End!.Value;
        var status = completion > dueAt ? DeliveryStatus.Late : dueAt - completion < AtRiskMargin ? DeliveryStatus.AtRisk : DeliveryStatus.OnTime;
        var cause = ops
            .Select(o => (Op: o, Delay: o.Interruptions.Aggregate(TimeSpan.Zero, (t, i) => t + (i.Until - i.From)), Binding: o.BindingConstraint is "Now" or "Predecessor" ? null : o))
            .Where(x => x.Binding is not null || x.Delay > TimeSpan.Zero)
            .Select(x => x.Binding is not null
                ? $"operation {x.Op.Sequence} waited for {x.Op.BindingConstraint}: {x.Op.BindingDetail}"
                : $"operation {x.Op.Sequence} was interrupted by {string.Join(", ", x.Op.Interruptions.Select(i => i.Limiting).Distinct())}")
            .ToList();
        var explanation = $"Projected completion {completion:u}, due {dueAt:u}." + (cause.Count == 0 ? string.Empty : " Delays: " + string.Join("; ", cause) + ".");
        return new ScheduledOrder(order.OrderNumber, order.PartNumber, order.Priority, order.DueDate, dueAt, completion, status, explanation);
    }

    private sealed record Booking(DateTimeOffset Start, DateTimeOffset End, long Order, int Sequence);

    private sealed class Bookings
    {
        private readonly Dictionary<string, List<Booking>> machines = new(StringComparer.Ordinal);
        private readonly Dictionary<Guid, List<Booking>> people = [];

        public void Book(string machine, DateTimeOffset start, DateTimeOffset end, long order, int sequence) =>
            Get(machines, machine).Add(new Booking(start, end, order, sequence));

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
