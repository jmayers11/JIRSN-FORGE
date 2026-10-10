using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Planning.Engines;

namespace Forge.Planning;

/// <summary>Where an order's material stands on its way to the machine (V3.4).</summary>
public enum MaterialFlowState
{
    /// <summary>Everything required is issued to the order.</summary>
    Issued,

    /// <summary>What is still to issue is released and at the work center's point of use (beside the machine or staged).</summary>
    AtPointOfUse,

    /// <summary>A material-flow constraint: the stock exists in the company's hands, but not at the machine. Not a shortage.</summary>
    InWarehouse,

    /// <summary>A shortage: not enough released stock anywhere the company holds it.</summary>
    Shortage,
}

/// <summary>
/// One component of a released order, through each separate state (V3.4, V3 brief I, BB): required, requested,
/// allocated, picked, delivered, issued and consumed. Each can be partial, and none implies the next.
/// </summary>
public sealed record MaterialFlowLine(
    long OrderNumber,
    string PartNumber,
    string? WorkCenterCode,
    decimal Required,
    decimal Requested,
    decimal Allocated,
    decimal Picked,
    decimal Delivered,
    decimal Issued,
    decimal Consumed,
    decimal AtPointOfUse,
    decimal Elsewhere,
    MaterialFlowState State,
    string Explanation)
{
    /// <summary>Still to issue.</summary>
    public decimal Remaining => Math.Max(0, Required - Issued);

    /// <summary>
    /// The flow of each component of an order. The work center is that of the first operation not yet complete
    /// (the last one when all are); its point of use is every location beside one of its machines or staged for it.
    /// Stock at the point of use of another work center, on hold, in inspection, at an outside processor or in
    /// transit does not count; other released stock in a nettable location is "elsewhere".
    /// </summary>
    public static IReadOnlyList<MaterialFlowLine> For(
        OrderInfo order,
        IEnumerable<MaterialTask> tasks,
        IEnumerable<InventoryTransaction> orderTransactions,
        IEnumerable<TransformationInfo> transformations,
        IEnumerable<StockPosition> positions)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(orderTransactions);
        ArgumentNullException.ThrowIfNull(transformations);
        ArgumentNullException.ThrowIfNull(positions);
        var ops = order.Operations.OrderBy(o => o.Sequence).ToList();
        var workCenter = (ops.FirstOrDefault(o => o.Status != OperationStatus.Complete) ?? ops.LastOrDefault())?.WorkCenterCode;
        var mine = tasks.Where(t => t.OrderNumber == order.OrderNumber && t.Status != MaterialTaskStatus.Cancelled).ToList();
        var ledger = orderTransactions.Where(t => t.OrderNumber == order.OrderNumber && t.SubstitutesFor is null && t.Kind is InventoryTransactionKind.Issue or InventoryTransactionKind.Return).ToList();
        var consumedLines = transformations.Where(x => x.OrderNumber == order.OrderNumber).SelectMany(x => x.Lines).Where(l => l.Role == TransformationRole.Consumed).ToList();
        var stock = positions.Where(p => p.QualityStatus == StockStatus.Released && p.Custody == Custody.Company).ToList();
        var result = new List<MaterialFlowLine>();
        foreach (var group in order.Materials.GroupBy(m => m.ComponentPartNumber, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var part = group.Key;
            var required = group.Sum(m => m.RequiredQuantity);
            var partTasks = mine.Where(t => t.PartNumber == part).ToList();
            var requested = partTasks.Sum(t => t.Quantity);
            var allocated = partTasks.Where(t => t.Status is MaterialTaskStatus.Allocated or MaterialTaskStatus.Picked or MaterialTaskStatus.Delivered).Sum(t => t.Quantity);
            var picked = partTasks.Where(t => t.Status is MaterialTaskStatus.Picked or MaterialTaskStatus.Delivered).Sum(t => t.PickedQuantity ?? 0);
            var delivered = partTasks.Where(t => t.Status == MaterialTaskStatus.Delivered).Sum(t => t.PickedQuantity ?? 0);
            var issued = ledger.Where(t => t.PartNumber == part).Sum(t => t.Kind == InventoryTransactionKind.Issue ? t.Quantity : -t.Quantity);
            var consumed = consumedLines.Where(l => l.PartNumber == part).Sum(l => l.Quantity);
            var atUse = stock.Where(p => p.PartNumber == part && workCenter is not null && p.PointOfUseWorkCenter == workCenter).Sum(p => p.Quantity);
            var elsewhere = stock.Where(p => p.PartNumber == part && p.Nettable && p.PointOfUseWorkCenter is null).Sum(p => p.Quantity);
            var remaining = Math.Max(0, required - issued);
            var open = partTasks.Where(t => t.Status is MaterialTaskStatus.Requested or MaterialTaskStatus.Allocated or MaterialTaskStatus.Picked).Sum(t => t.Quantity);
            var (state, why) = remaining == 0 ? (MaterialFlowState.Issued, "All issued.")
                : atUse >= remaining ? (MaterialFlowState.AtPointOfUse, $"{Quantities.Text(atUse)} at {workCenter}'s point of use; {Quantities.Text(remaining)} still to issue.")
                : atUse + elsewhere >= remaining ? (MaterialFlowState.InWarehouse,
                    $"Material flow, not a shortage: {Quantities.Text(atUse)} at {workCenter ?? "the work center"}, {Quantities.Text(elsewhere)} released elsewhere; {Quantities.Text(remaining)} still to issue"
                    + (open > 0 ? $", {Quantities.Text(open)} on open tasks." : ", and nothing requested yet."))
                : (MaterialFlowState.Shortage, $"Short: {Quantities.Text(atUse + elsewhere)} released in the company's hands for {Quantities.Text(remaining)} still to issue.");
            result.Add(new MaterialFlowLine(order.OrderNumber, part, workCenter, required, requested, allocated, picked, delivered, issued, consumed, atUse, elsewhere, state, why));
        }

        return result;
    }
}
