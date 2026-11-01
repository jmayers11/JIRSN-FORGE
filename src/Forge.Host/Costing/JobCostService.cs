using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Workforce.Contracts;

namespace Forge.Host.Costing;

/// <summary>Material issued to or returned from a production order, at the part's standard cost when it moved.</summary>
public sealed record JobMaterialCost(string PartNumber, decimal Issued, decimal Returned, decimal Credited, decimal? Value, string? Note);

/// <summary>Direct labor on one operation: hours by role, costed at the policy's labor and overhead rates in force when the work started.</summary>
public sealed record JobLaborCost(int Sequence, string WorkCenterCode, decimal OperatorHours, decimal TrainerHours, decimal TraineeHours, decimal? Labor, decimal? Overhead, int OpenEntries);

/// <summary>Actual against standard for one cost element. A positive variance is unfavorable (actual above standard).</summary>
public sealed record JobCostElement(string Element, decimal? Actual, decimal? Standard, decimal? Variance);

/// <summary>
/// Actual job cost (owner decisions 2026-10-31): material is the quantity issued to the order (less returns and credited remnants and
/// by-products) at standard cost; labor and overhead are the recorded direct-labor hours at the costing policy's plant rates. The standard
/// is the part's standard material, labor and overhead for the good quantity received. The difference is the job's variance; what has not
/// yet produced output is work in process. Nothing is posted; the accounting export carries the inventory side.
/// </summary>
public sealed record JobCost(
    long OrderNumber,
    string PartNumber,
    decimal OrderQuantity,
    string Status,
    string Currency,
    decimal GoodReceived,
    decimal ScrapReported,
    decimal? UnitStandard,
    IReadOnlyList<JobCostElement> Elements,
    decimal? ActualTotal,
    decimal? StandardOfOutput,
    decimal? Variance,
    decimal? WorkInProcess,
    IReadOnlyList<JobMaterialCost> Material,
    IReadOnlyList<JobLaborCost> Labor,
    IReadOnlyList<string> Notes)
{
    /// <summary>Hours employees charged to the order on their timesheets: all active entries, and those on approved weeks.</summary>
    public decimal TimesheetHours { get; init; }

    public decimal TimesheetHoursApproved { get; init; }

    /// <summary>Shop-floor direct-labor hours (operators and trainers) the cost is built from.</summary>
    public decimal ShopFloorHours => Labor.Sum(l => l.OperatorHours + l.TrainerHours);
}

public sealed class JobCostService(IKernelUnitOfWorkFactory unitOfWork, TimeProvider time)
{
    public async Task<JobCost> CostAsync(ForgePrincipal principal, long orderNumber, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var order = await uow.Store<IExecutionLookup>().FindOrderAsync(orderNumber, ct);
        if (order is null || (order.ProgramId is { } program && !principal.Programs.Contains(new ProgramId(program))))
            throw new CommandRejectedException("order_not_found", $"Order {orderNumber} does not exist.");
        var costing = uow.Store<ICostingLookup>();
        var charged = await uow.Store<ITimesheetLookup>().ListOrderEntriesAsync(orderNumber, false, ct);
        var approved = await uow.Store<ITimesheetLookup>().ListOrderEntriesAsync(orderNumber, true, ct);
        var cost = Cost(order, await costing.ListPoliciesAsync(ct), await costing.ListStandardCostsAsync(null, ct),
            await uow.Store<IInventoryLookup>().ListTransactionsAsync(null, orderNumber, 100_000, ct),
            await uow.Store<IExecutionLookup>().ListLaborAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, orderNumber, ct),
            await uow.Store<IExecutionLookup>().ListReportsAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, orderNumber, ct),
            time.GetUtcNow());
        cost = cost with { TimesheetHours = charged.Sum(e => e.Hours), TimesheetHoursApproved = approved.Sum(e => e.Hours) };
        if (cost.TimesheetHours > 0 && Math.Abs(cost.TimesheetHours - cost.ShopFloorHours) >= 0.25m)
            cost = cost with { Notes = [.. cost.Notes, string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Timesheets charge {cost.TimesheetHours:0.##} h to this order and shop-floor labor records {cost.ShopFloorHours:0.##} h; review the difference with the employees' supervisors.")] };
        return cost;
    }

    /// <summary>The cost of one order from its recorded facts. Pure, so it is tested without a database.</summary>
    public static JobCost Cost(OrderInfo order, IReadOnlyList<CostingPolicy> policies, IReadOnlyList<StandardCost> costs, IReadOnlyList<InventoryTransaction> movements,
        IReadOnlyList<LaborEntry> labor, IReadOnlyList<ProductionReport> reports, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(order);
        var notes = new List<string>();
        var currency = Forge.Inventory.Contracts.Costing.PolicyAt(policies, now)?.Currency ?? "USD";

        // Material: issued less returned, at standard when it moved; remnants and by-products put back from the order are credits.
        var material = new List<JobMaterialCost>();
        var materialComplete = true;
        foreach (var part in movements.Where(m => m.OrderNumber == order.OrderNumber && m.Kind is InventoryTransactionKind.Issue or InventoryTransactionKind.Return
                     or InventoryTransactionKind.Remnant or InventoryTransactionKind.Byproduct)
                 .GroupBy(m => m.PartNumber).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            decimal issued = 0, returned = 0, credited = 0, value = 0;
            var valued = true;
            foreach (var m in part)
            {
                var standard = Forge.Inventory.Contracts.Costing.StandardAt(costs, m.PartNumber, m.At);
                var sign = m.Kind == InventoryTransactionKind.Issue ? 1m : -1m;
                if (m.Kind == InventoryTransactionKind.Issue) issued += m.Quantity;
                else if (m.Kind == InventoryTransactionKind.Return) returned += m.Quantity;
                else credited += m.Quantity;
                if (standard is null) valued = false;
                else value += sign * m.Quantity * standard.Total;
            }

            if (!valued) materialComplete = false;
            material.Add(new JobMaterialCost(part.Key, issued, returned, credited, valued ? Round(value) : null, valued ? null : $"{part.Key} has no standard cost for some movements."));
        }

        // Labor: person-hours of operators and trainers; trainee hours are training, not job cost. Open entries count to now.
        var laborLines = new List<JobLaborCost>();
        var laborComplete = true;
        foreach (var op in labor.Where(l => l.OrderNumber == order.OrderNumber).GroupBy(l => (l.Sequence, l.WorkCenterCode)).OrderBy(g => g.Key.Sequence))
        {
            decimal operatorHours = 0, trainerHours = 0, traineeHours = 0, laborCost = 0, overheadCost = 0;
            var valued = true;
            foreach (var entry in op)
            {
                var hours = (decimal)((entry.EndedAt ?? now) - entry.StartedAt).TotalHours;
                if (hours <= 0) continue;
                if (entry.Role == LaborRole.Trainee) { traineeHours += hours; continue; }
                if (entry.Role == LaborRole.Trainer) trainerHours += hours; else operatorHours += hours;
                var policy = Forge.Inventory.Contracts.Costing.PolicyAt(policies, entry.StartedAt);
                if (policy?.LaborRatePerHour is not { } rate || policy.OverheadRatePerHour is not { } overhead) { valued = false; continue; }
                laborCost += hours * rate;
                overheadCost += hours * overhead;
            }

            if (!valued) laborComplete = false;
            laborLines.Add(new JobLaborCost(op.Key.Sequence, op.Key.WorkCenterCode, Round(operatorHours, 4), Round(trainerHours, 4), Round(traineeHours, 4),
                valued ? Round(laborCost) : null, valued ? Round(overheadCost) : null, op.Count(e => e.EndedAt is null)));
        }

        if (labor.Any(l => l.OrderNumber == order.OrderNumber && l.EndedAt is null))
            notes.Add("Some labor is still clocked on; its hours count up to now.");
        if (!laborComplete) notes.Add("The costing policy has no labor or overhead rate for some of the labor; those hours are not costed.");
        if (!materialComplete) notes.Add("Some issued material has no standard cost; set it and the job cost completes.");

        // Output: good quantity of the order's part received to stock, valued at its standard.
        var good = movements.Where(m => m.OrderNumber == order.OrderNumber && m.Kind == InventoryTransactionKind.ProductionReceipt && m.PartNumber == order.PartNumber).Sum(m => m.Quantity);
        var scrap = reports.Where(r => r.OrderNumber == order.OrderNumber).Sum(r => r.ScrapQuantity);
        var unit = Forge.Inventory.Contracts.Costing.StandardAt(costs, order.PartNumber, now);
        if (unit is null) notes.Add($"{order.PartNumber} has no standard cost, so there is no standard to compare with.");

        decimal? actualMaterial = materialComplete ? material.Sum(m => m.Value ?? 0) : null;
        decimal? actualLabor = laborComplete ? laborLines.Sum(l => l.Labor ?? 0) : null;
        decimal? actualOverhead = laborComplete ? laborLines.Sum(l => l.Overhead ?? 0) : null;
        JobCostElement Element(string name, decimal? actual, decimal? perUnit) =>
            new(name, actual, perUnit is { } p ? Round(p * good) : null, actual is { } a && perUnit is { } q ? Round(a - q * good) : null);
        var elements = new[]
        {
            Element("Material", actualMaterial, unit?.Material),
            Element("Labor", actualLabor, unit?.Labor),
            Element("Overhead", actualOverhead, unit?.Overhead),
        };
        decimal? actualTotal = actualMaterial + actualLabor + actualOverhead;
        decimal? standardOfOutput = unit is null ? null : Round(unit.Total * good);
        var finished = order.Status is OrderStatus.Completed or OrderStatus.Cancelled;
        decimal? variance = finished && actualTotal is { } at && standardOfOutput is { } so ? at - so : null;
        decimal? wip = !finished && actualTotal is { } a2 && standardOfOutput is { } s2 ? a2 - s2 : null;
        notes.Add(finished
            ? "The order is finished: the difference between actual cost and the standard of what it produced is its variance."
            : "The order is open: actual cost not yet relieved by output is work in process; the variance is final when the order completes.");
        return new JobCost(order.OrderNumber, order.PartNumber, order.Quantity, order.Status.ToString(), currency, good, scrap, unit?.Total, elements,
            actualTotal is { } t ? Round(t) : null, standardOfOutput, variance is { } v ? Round(v) : null, wip is { } w ? Round(w) : null, material, laborLines, notes);
    }

    private static decimal Round(decimal amount, int digits = 2) => Math.Round(amount, digits, MidpointRounding.AwayFromZero);
}
