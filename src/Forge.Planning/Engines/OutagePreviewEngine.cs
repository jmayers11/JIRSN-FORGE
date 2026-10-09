using System.Globalization;

namespace Forge.Planning.Engines;

/// <summary>An operation the outage touches: where and when it ran in the plan without the outage, and with it.</summary>
/// <param name="Effect">Moves (to another machine), Delayed (same machine, later), CannotMove (no longer scheduled), or Unchanged.</param>
public sealed record OutageOperationEffect(
    long OrderNumber,
    int Sequence,
    string Description,
    string? MachineBefore,
    DateTimeOffset? StartBefore,
    DateTimeOffset? EndBefore,
    string? MachineAfter,
    DateTimeOffset? StartAfter,
    DateTimeOffset? EndAfter,
    string Effect,
    string Detail);

/// <summary>An order whose projection the outage changes.</summary>
public sealed record OutageOrderEffect(long OrderNumber, string PartNumber, DateOnly DueDate, string StatusBefore, string StatusAfter, DateTimeOffset? CompletionBefore, DateTimeOffset? CompletionAfter, bool BecomesLate);

/// <summary>What a proposed outage does to the schedule (V2.9 scenario 8): the scheduler run with and without it.</summary>
public sealed record OutagePreview(
    string MachineCode,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    IReadOnlyList<OutageOperationEffect> Operations,
    IReadOnlyList<OutageOrderEffect> Orders,
    int Affected,
    int Moved,
    int Delayed,
    int CannotMove,
    int OrdersLate,
    string Summary);

/// <summary>
/// Compares two runs of the finite scheduler over the same orders — without and with a proposed outage — and
/// says which operations it touches (those planned on the machine during the window), which move to another
/// machine, which are only delayed, which cannot be scheduled any more, and which orders become late. Pure.
/// </summary>
public static class OutagePreviewEngine
{
    public static OutagePreview Compare(string machine, DateTimeOffset start, DateTimeOffset end, ScheduleResult without, ScheduleResult with)
    {
        ArgumentNullException.ThrowIfNull(without);
        ArgumentNullException.ThrowIfNull(with);
        var after = with.Operations.ToDictionary(o => (o.OrderNumber, o.Sequence));
        var effects = new List<OutageOperationEffect>();
        foreach (var op in without.Operations.Where(o => o.Scheduled && o.MachineCode == machine && o.Start < end && o.End > start).OrderBy(o => o.Start).ThenBy(o => o.OrderNumber).ThenBy(o => o.Sequence))
        {
            after.TryGetValue((op.OrderNumber, op.Sequence), out var then);
            string effect;
            string detail;
            if (then is not { Scheduled: true })
            {
                effect = "CannotMove";
                detail = $"Order {op.OrderNumber} op {op.Sequence} can no longer be scheduled{(then is null ? string.Empty : ": " + then.BindingDetail)}.";
            }
            else if (then.MachineCode != machine)
            {
                effect = "Moves";
                detail = $"Order {op.OrderNumber} op {op.Sequence} moves to {then.MachineCode ?? then.WorkCenterCode}, starting {T(then.Start)}.";
            }
            else if (then.Start > op.Start)
            {
                effect = "Delayed";
                detail = $"Order {op.OrderNumber} op {op.Sequence} stays on {machine} and starts {T(then.Start)} instead of {T(op.Start)}.";
            }
            else
            {
                effect = "Unchanged";
                detail = $"Order {op.OrderNumber} op {op.Sequence} is not affected.";
            }

            effects.Add(new OutageOperationEffect(op.OrderNumber, op.Sequence, op.Description, op.MachineCode, op.Start, op.End, then?.MachineCode, then?.Start, then?.End, effect, detail));
        }

        var before = without.Orders.ToDictionary(o => o.OrderNumber);
        var orders = new List<OutageOrderEffect>();
        foreach (var o in with.Orders.OrderBy(o => o.OrderNumber))
        {
            if (!before.TryGetValue(o.OrderNumber, out var b))
            {
                continue;
            }

            var worse = Rank(o.Status) > Rank(b.Status);
            var later = o.ProjectedCompletion is { } c && (b.ProjectedCompletion is not { } bc || c > bc);
            if (worse || later || o.Status != b.Status)
            {
                var becomesLate = o.Status is DeliveryStatus.Late or DeliveryStatus.Unschedulable && b.Status is not (DeliveryStatus.Late or DeliveryStatus.Unschedulable);
                orders.Add(new OutageOrderEffect(o.OrderNumber, o.PartNumber, o.DueDate, b.Status.ToString(), o.Status.ToString(), b.ProjectedCompletion, o.ProjectedCompletion, becomesLate));
            }
        }

        var touched = effects.Where(e => e.Effect != "Unchanged").ToList();
        var moved = touched.Count(e => e.Effect == "Moves");
        var delayed = touched.Count(e => e.Effect == "Delayed");
        var cannot = touched.Count(e => e.Effect == "CannotMove");
        var late = orders.Where(o => o.BecomesLate).Select(o => o.OrderNumber).ToList();
        var summary = $"Outage on {machine} {T(start)} to {T(end)}: "
            + (touched.Count == 0 ? "no planned operation is affected" : $"{touched.Count} operation(s) affected — {moved} move to another machine, {delayed} delayed, {cannot} cannot move")
            + (late.Count == 0 ? "; no order becomes late" : $"; {late.Count} order(s) projected late ({string.Join(", ", late)})")
            + (orders.Count > late.Count ? $"; {orders.Count - late.Count} other order(s) finish later" : string.Empty)
            + ".";
        return new OutagePreview(machine, start, end, effects, orders, touched.Count, moved, delayed, cannot, late.Count, summary);
    }

    private static int Rank(DeliveryStatus status) => status switch
    {
        DeliveryStatus.OnTime => 0,
        DeliveryStatus.AtRisk => 1,
        DeliveryStatus.Late => 2,
        _ => 3,
    };

    private static string T(DateTimeOffset? at) => at is { } t ? t.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) : "(unscheduled)";
}
