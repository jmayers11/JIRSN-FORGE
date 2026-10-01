using Forge.Execution.Contracts;
using Forge.Kernel.Engines;

namespace Forge.Planning.Engines;

/// <summary>A purchased component and its supplier lead time in calendar days.</summary>
public sealed record PurchasedComponent(string PartNumber, int LeadTimeDays);

public sealed record DropDeadRequest(OrderInfo Order, int ShipDays, IReadOnlyList<PurchasedComponent> PurchasedComponents);

public sealed record DropDeadStep(string Label, DateTimeOffset LatestStart, DateTimeOffset LatestFinish, string Basis);

public sealed record DropDeadResult(
    long OrderNumber,
    DateTimeOffset NeedBy,
    DateTimeOffset ShipBy,
    IReadOnlyList<DropDeadStep> Operations,
    DateTimeOffset? MaterialRequiredBy,
    IReadOnlyList<DropDeadStep> PurchaseBy,
    bool AlreadyMissed,
    string Summary);

/// <summary>
/// Latest dates that still meet the customer need date (spec §42), by backward scheduling from the
/// need date through shipping, each operation in its work center's working time (shift calendars,
/// closures), material availability and supplier lead times. Capacity is not considered here: these
/// are the dates that must not be missed, not a plan.
/// </summary>
public sealed class DropDeadDateEngine : IEngine<SiteSnapshot, DropDeadRequest, DropDeadResult>
{
    private static readonly TimeSpan LookBack = TimeSpan.FromDays(180);

    public string Name => "drop-dead-dates";

    public string Version => "1";

    public EngineEvaluation<DropDeadResult> Evaluate(SiteSnapshot snapshot, DropDeadRequest parameters, DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(parameters);
        var order = parameters.Order;
        var ops = order.Operations.OrderBy(o => o.Sequence).ToList();
        var site = WorkCalendar.SiteOfWorkCenter(snapshot, ops.FirstOrDefault()?.WorkCenterCode ?? string.Empty);
        var needBy = WorkCalendar.EndOfDay(snapshot, site, order.DueDate);
        var shipBy = needBy.AddDays(-Math.Max(0, parameters.ShipDays));

        var steps = new List<DropDeadStep>();
        var finish = shipBy;
        var feasible = true;
        foreach (var op in Enumerable.Reverse(ops))
        {
            var minutes = TimeSpan.FromMinutes((double)op.PlannedMinutes);
            var start = Backward(snapshot, op.WorkCenterCode, finish, minutes);
            if (start is null)
            {
                feasible = false;
                steps.Insert(0, new DropDeadStep($"Op {op.Sequence} {op.Description}", finish, finish, $"No working time in {op.WorkCenterCode} within {LookBack.TotalDays:0} days before {finish:u}."));
                continue;
            }

            steps.Insert(0, new DropDeadStep(
                $"Op {op.Sequence} {op.Description}",
                start.Value,
                finish,
                $"{op.PlannedMinutes:0.##} working minutes in {op.WorkCenterCode}, finishing before the next step."));
            finish = start.Value;
        }

        DateTimeOffset? materialBy = ops.Count == 0 ? null : finish;
        var purchase = materialBy is { } mb
            ? parameters.PurchasedComponents
                .OrderBy(c => c.PartNumber, StringComparer.Ordinal)
                .Select(c => new DropDeadStep($"Order {c.PartNumber}", mb.AddDays(-c.LeadTimeDays), mb, $"{c.LeadTimeDays} day supplier lead time."))
                .ToList()
            : [];

        var earliestDeadline = new[] { materialBy }.Concat(purchase.Select(p => (DateTimeOffset?)p.LatestStart)).OfType<DateTimeOffset>().DefaultIfEmpty(shipBy).Min();
        var missed = !feasible || earliestDeadline < asOf;
        var summary = missed
            ? $"Order {order.OrderNumber} is already past at least one drop-dead date (earliest {earliestDeadline:u})."
            : $"Order {order.OrderNumber}: material needed by {materialBy:u}; ship by {shipBy:u} for need date {needBy:u}.";

        var root = new EvidenceNode("DropDeadDates", missed ? EvidenceOutcome.Fail : EvidenceOutcome.Pass, summary)
        {
            Children = steps.Concat(purchase).Select(s => new EvidenceNode(s.Label, EvidenceOutcome.Info, $"Start by {s.LatestStart:u}, finish by {s.LatestFinish:u}. {s.Basis}")).ToList(),
        };
        return new EngineEvaluation<DropDeadResult>(new DropDeadResult(order.OrderNumber, needBy, shipBy, steps, materialBy, purchase, missed, summary), root);
    }

    /// <summary>The latest start such that <paramref name="work"/> of working time fits before <paramref name="finish"/>.</summary>
    private static DateTimeOffset? Backward(SiteSnapshot snapshot, string workCenterCode, DateTimeOffset finish, TimeSpan work)
    {
        if (work <= TimeSpan.Zero)
        {
            return finish;
        }

        var open = WorkCalendar.WorkCenterOpen(snapshot, workCenterCode, finish - LookBack, finish);
        var remaining = work;
        foreach (var span in open.Reverse())
        {
            var length = span.End - span.Start;
            if (length >= remaining)
            {
                return span.End - remaining;
            }

            remaining -= length;
        }

        return null;
    }
}
