using Forge.Execution.Contracts;
using Forge.Planning.Engines;

namespace Forge.Planning;

/// <summary>Costs a scenario may use: the costing policy's labor and overhead rate and each part's standard unit cost, in force now.</summary>
public sealed record ScenarioCosting(string Currency, decimal? LaborAndOverheadPerHour, IReadOnlyDictionary<string, decimal> UnitStandardCost);

/// <summary>One operation's place in the baseline and the alternative schedule, and what it produces and costs in the horizon.</summary>
public sealed record OperationOutputImpact(
    long OrderNumber,
    int Sequence,
    string WorkCenterCode,
    string? BaselineMachine,
    string? ProposedMachine,
    DateTimeOffset? BaselineEnd,
    DateTimeOffset? ProposedEnd,
    bool BaselineWithinHorizon,
    bool ProposedWithinHorizon,
    decimal Units,
    decimal BaselineLaborHours,
    decimal ProposedLaborHours,
    decimal BaselineSetupMinutes,
    decimal ProposedSetupMinutes);

/// <summary>Plant totals for one schedule over the horizon.</summary>
public sealed record PlantImpactTotals(
    decimal UnitsCompleted,
    int OperationsCompleted,
    decimal LaborHours,
    decimal SetupMinutes,
    int OrdersInProcess,
    decimal WipUnits,
    int OrdersNotOnTime,
    decimal? OutputValue,
    decimal? LaborCost);

/// <summary>An order whose delivery status changes between the two schedules, with the commitment it carries.</summary>
public sealed record CommitmentImpact(
    long OrderNumber,
    string PartNumber,
    DateOnly DueDate,
    string BaselineStatus,
    string ProposedStatus,
    DateTimeOffset? BaselineCompletion,
    DateTimeOffset? ProposedCompletion,
    long? ContractId,
    string? Clin,
    Guid? ProgramId);

/// <summary>
/// The whole-plant comparison of two finite-capacity schedules (owner request 2026-10-31): per-operation output, labor and set-up in the
/// horizon, work in process at its end, orders whose delivery status changes, and standard-cost value where costing is visible.
/// </summary>
public sealed record ScenarioPlantImpact(
    DateTimeOffset HorizonEnd,
    PlantImpactTotals Baseline,
    PlantImpactTotals Proposed,
    IReadOnlyList<OperationOutputImpact> Operations,
    int OperationsChanged,
    IReadOnlyList<CommitmentImpact> Commitments,
    string? Currency,
    IReadOnlyList<string> Notes);

public static class ScenarioPlantImpacts
{
    private const int MaximumOperations = 200;

    public static ScenarioPlantImpact Compare(ScheduleResult baseline, ScheduleResult proposed, IReadOnlyList<OrderInfo> orders, ScenarioCosting? costing)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(proposed);
        ArgumentNullException.ThrowIfNull(orders);
        var byNumber = orders.ToDictionary(o => o.OrderNumber);
        var before = baseline.Operations.ToDictionary(o => (o.OrderNumber, o.Sequence));
        var after = proposed.Operations.ToDictionary(o => (o.OrderNumber, o.Sequence));
        var operations = new List<OperationOutputImpact>();
        foreach (var key in before.Keys.Union(after.Keys).OrderBy(k => k.OrderNumber).ThenBy(k => k.Sequence))
        {
            before.TryGetValue(key, out var a);
            after.TryGetValue(key, out var b);
            var info = byNumber.GetValueOrDefault(key.OrderNumber);
            var op = info?.Operations.FirstOrDefault(o => o.Sequence == key.Sequence);
            operations.Add(new OperationOutputImpact(key.OrderNumber, key.Sequence, (b ?? a)!.WorkCenterCode, a?.MachineCode, b?.MachineCode, a?.End, b?.End,
                Within(a, baseline.To), Within(b, proposed.To), Units(info, op), LaborHours(a, baseline), LaborHours(b, proposed), SetupMinutes(a, op, baseline.To), SetupMinutes(b, op, proposed.To)));
        }

        var changed = operations.Where(o => o.BaselineEnd != o.ProposedEnd || o.BaselineMachine != o.ProposedMachine || o.BaselineWithinHorizon != o.ProposedWithinHorizon).ToList();
        var commitments = baseline.Orders.Join(proposed.Orders, x => x.OrderNumber, y => y.OrderNumber, (x, y) => (Before: x, After: y))
            .Where(p => p.Before.Status != p.After.Status)
            .Select(p =>
            {
                var info = byNumber.GetValueOrDefault(p.After.OrderNumber);
                return new CommitmentImpact(p.After.OrderNumber, p.After.PartNumber, p.After.DueDate, p.Before.Status.ToString(), p.After.Status.ToString(),
                    p.Before.ProjectedCompletion, p.After.ProjectedCompletion, info?.ContractId, info?.ContractClin, info?.ProgramId);
            })
            .OrderBy(c => c.DueDate).ThenBy(c => c.OrderNumber).ToList();

        var notes = new List<string>
        {
            "Units are counted when an operation's scheduled end falls inside the horizon; a schedule is a projection, not a production record.",
            "Labor hours are the scheduled operator time inside the horizon; set-up minutes follow the scheduler's same-family or full set-up choice.",
            costing is null ? "No cost figures: costing is not visible to this role or no costing policy is recorded."
                : $"Values use standard cost in {costing.Currency}: completed units at each part's standard unit cost, labor at the policy's labor plus overhead rate per direct labor hour.",
        };
        if (changed.Count > MaximumOperations) notes.Add($"{changed.Count} operations changed; the first {MaximumOperations} are listed. Totals include all of them.");
        return new ScenarioPlantImpact(proposed.To, Totals(operations, baseline, byNumber, costing, before: true), Totals(operations, proposed, byNumber, costing, before: false),
            changed.Take(MaximumOperations).ToList(), changed.Count, commitments, costing?.Currency, notes);
    }

    private static bool Within(ScheduledOperation? op, DateTimeOffset to) => op is { Scheduled: true, End: { } end } && end <= to;

    private static decimal Units(OrderInfo? order, OrderOperationInfo? op) =>
        order is null || op is null ? 0 : Math.Max(0, op.QuantityFor(order.Quantity) - op.GoodQuantity);

    private static decimal LaborHours(ScheduledOperation? op, ScheduleResult schedule)
    {
        if (op is null || !op.Scheduled) return 0;
        var hours = op.Segments.Sum(s =>
        {
            var start = s.Start < schedule.From ? schedule.From : s.Start;
            var end = s.End > schedule.To ? schedule.To : s.End;
            return end > start ? (decimal)(end - start).TotalHours * Math.Max(1, s.Operators.Count) : 0m;
        });
        return decimal.Round(hours, 2);
    }

    private static decimal SetupMinutes(ScheduledOperation? scheduled, OrderOperationInfo? op, DateTimeOffset to)
    {
        if (scheduled is not { Scheduled: true, Start: { } start, Setup: { } setup } || start > to || op is null) return 0;
        return setup.StartsWith("Same-family", StringComparison.Ordinal) ? op.FamilySetupMinutes ?? 0 : op.SetupMinutes ?? 0;
    }

    private static PlantImpactTotals Totals(List<OperationOutputImpact> operations, ScheduleResult schedule, Dictionary<long, OrderInfo> orders, ScenarioCosting? costing, bool before)
    {
        bool Done(OperationOutputImpact o) => before ? o.BaselineWithinHorizon : o.ProposedWithinHorizon;
        var unitsCompleted = 0m;
        decimal? value = costing is null ? null : 0m;
        var inProcess = 0;
        var wipUnits = 0m;
        foreach (var group in operations.GroupBy(o => o.OrderNumber))
        {
            if (!orders.TryGetValue(group.Key, out var order)) continue;
            var routed = order.Routed();
            var last = routed.Count == 0 ? (int?)null : routed[^1].Sequence;
            var final = group.FirstOrDefault(o => o.Sequence == last);
            var finished = final is not null && Done(final);
            if (finished)
            {
                unitsCompleted += final!.Units;
                if (value is not null && costing!.UnitStandardCost.TryGetValue(order.PartNumber, out var unit)) value += final.Units * unit;
            }
            else if (group.Any(Done) || order.Operations.Any(op => op.Status != OperationStatus.Pending))
            {
                inProcess++;
                wipUnits += final?.Units ?? 0;
            }
        }

        var labor = operations.Sum(o => before ? o.BaselineLaborHours : o.ProposedLaborHours);
        return new PlantImpactTotals(unitsCompleted, operations.Count(Done), labor, operations.Sum(o => before ? o.BaselineSetupMinutes : o.ProposedSetupMinutes),
            inProcess, wipUnits, schedule.Orders.Count(o => o.Status != DeliveryStatus.OnTime),
            value is null ? null : decimal.Round(value.Value, 2),
            costing?.LaborAndOverheadPerHour is { } rate ? decimal.Round(labor * rate, 2) : null);
    }
}
