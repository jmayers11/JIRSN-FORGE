using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Quality.Contracts;

namespace Forge.Planning;

/// <summary>Whether a quantity state counts what has passed through it so far, or what sits in it now.</summary>
public enum QuantityStateKind
{
    SoFar,
    Now,
}

/// <summary>One quantity state of an order (V3.5) with the records it comes from.</summary>
public sealed record QuantityState(string State, QuantityStateKind Kind, decimal Quantity, string Explanation);

/// <summary>
/// An order's quantity through its states (V3.5, V3 brief K): ordered, started, produced, accepted, received and
/// shipped so far; in process, in rework, in repair, pending inspection, released, on hold, staged and packed now;
/// and scrapped. Every figure is derived from records that are never rewritten: production reports, rework records,
/// inspections, NCRs and the inventory ledger.
/// </summary>
public sealed record OrderQuantityStates(long OrderNumber, string PartNumber, string Status, IReadOnlyList<QuantityState> States, IReadOnlyList<string> Notes)
{
    public decimal Of(string state) => States.FirstOrDefault(s => s.State == state)?.Quantity ?? 0;

    public static OrderQuantityStates For(
        OrderInfo order,
        IReadOnlyList<ReworkInfo> rework,
        IReadOnlyList<InspectionInfo> inspections,
        IReadOnlyList<NcrInfo> ncrs,
        IReadOnlyList<InventoryTransaction> orderLedger,
        IReadOnlyList<InventoryTransaction> partLedger,
        IReadOnlyList<StockPosition> partPositions)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(rework);
        ArgumentNullException.ThrowIfNull(inspections);
        ArgumentNullException.ThrowIfNull(ncrs);
        ArgumentNullException.ThrowIfNull(orderLedger);
        ArgumentNullException.ThrowIfNull(partLedger);
        ArgumentNullException.ThrowIfNull(partPositions);
        var notes = new List<string>();
        var ops = order.Operations.OrderBy(o => o.Sequence).ToList();
        var mine = rework.Where(r => r.OrderNumber == order.OrderNumber).ToList();
        decimal Taken(int sequence) => mine.Where(r => r.Sequence == sequence).Sum(r => r.Open ? r.Quantity : r.ScrapQuantity ?? 0);

        var first = ops.FirstOrDefault();
        var last = ops.LastOrDefault();
        var started = first is null ? 0 : first.GoodQuantity + first.ScrapQuantity;
        var produced = last is null ? 0 : Math.Max(0, last.GoodQuantity - Taken(last.Sequence));
        var openRework = mine.Where(r => r.Open && r.Kind == ReworkKind.Rework).Sum(r => r.Quantity);
        var openRepair = mine.Where(r => r.Open && r.Kind == ReworkKind.Repair).Sum(r => r.Quantity);
        var scrap = ops.Sum(o => o.ScrapQuantity) + mine.Where(r => !r.Open).Sum(r => r.ScrapQuantity ?? 0);
        var repaired = mine.Where(r => !r.Open && r.Kind == ReworkKind.Repair).Sum(r => r.GoodQuantity ?? 0);

        // In process: everything that entered and is not yet produced, scrapped or taken back for rework or repair.
        var inProcess = Math.Max(0, started - produced - scrap - openRework - openRepair);

        // Accepted: passed at the order's last inspection point, plus what an approved NCR accepted as it is.
        var orderInspections = inspections.Where(i => i.OrderNumber == order.OrderNumber).ToList();
        var lastPoint = orderInspections.Count == 0 ? (int?)null : orderInspections.Max(i => i.Sequence);
        var passed = orderInspections.Where(i => i.Sequence == lastPoint && i.Result == InspectionResult.Pass).Sum(i => i.QuantityInspected);
        var usedAsIs = ncrs.Where(n => n.OrderNumber == order.OrderNumber && n.Disposition == Disposition.UseAsIs && n.Status is NcrStatus.Dispositioned or NcrStatus.Closed).Sum(n => n.Quantity);
        var accepted = Math.Min(produced, passed + usedAsIs);
        if (lastPoint is null && usedAsIs == 0)
        {
            notes.Add("No inspection recorded on this order; accepted counts inspections passed and NCR quantities accepted as they are.");
        }

        // Into stock: production receipts of this order, then where those lots are now.
        var receipts = orderLedger.Where(t => t.Kind == InventoryTransactionKind.ProductionReceipt && t.OrderNumber == order.OrderNumber && t.PartNumber == order.PartNumber).ToList();
        var received = receipts.Sum(t => t.Quantity);
        var lots = receipts.Where(t => t.LotNumber is not null).Select(t => t.LotNumber!).ToHashSet(StringComparer.Ordinal);
        decimal pending = 0, released = 0, hold = 0, staged = 0, shipped = 0;
        if (receipts.Count > 0 && lots.Count == 0)
        {
            notes.Add($"{order.PartNumber} is not lot-tracked, so its stock is not told apart by order; only what was received is shown.");
        }
        else
        {
            foreach (var p in partPositions.Where(p => p.PartNumber == order.PartNumber && p.LotNumber is { } lot && lots.Contains(lot)))
            {
                if (p.QualityStatus == StockStatus.Inspection)
                {
                    pending += p.Quantity;
                }
                else if (p.QualityStatus == StockStatus.Hold)
                {
                    hold += p.Quantity;
                }
                else if (p.LocationKind == LocationKind.Shipping)
                {
                    staged += p.Quantity;
                }
                else
                {
                    released += p.Quantity;
                }
            }

            shipped = partLedger.Where(t => t.Kind == InventoryTransactionKind.Shipment && t.PartNumber == order.PartNumber && t.LotNumber is { } lot && lots.Contains(lot)).Sum(t => t.Quantity);
        }

        var notReceived = Math.Max(0, produced - received);
        if (repaired > 0)
        {
            notes.Add($"{repaired:0.###} of what was produced was repaired: usable, not to the drawing.");
        }

        notes.Add("Packed is recorded from V3.14 (packaging and shipping); until then it is always 0.");
        QuantityState S(string state, QuantityStateKind kind, decimal q, string why) => new(state, kind, q, why);
        return new OrderQuantityStates(order.OrderNumber, order.PartNumber, order.Status.ToString(),
        [
            S("Ordered", QuantityStateKind.SoFar, order.Quantity, "The order quantity."),
            S("Started", QuantityStateKind.SoFar, started, $"Good and scrap reported at the first operation{(first is null ? string.Empty : $" ({first.Sequence})")}."),
            S("Produced", QuantityStateKind.SoFar, produced, $"Good reported at the last operation{(last is null ? string.Empty : $" ({last.Sequence})")}, less what was taken back for rework or repair and not returned."),
            S("Accepted", QuantityStateKind.SoFar, accepted, "Passed at the last inspection point, plus NCR quantities approved for use as is."),
            S("Received", QuantityStateKind.SoFar, received, "Received into stock from production."),
            S("Shipped", QuantityStateKind.SoFar, shipped, "Shipped from the lots this order produced."),
            S("Scrapped", QuantityStateKind.SoFar, scrap, "Scrap reported at any operation, plus scrap from rework and repair."),
            S("In process", QuantityStateKind.Now, inProcess, "Started and not yet produced, scrapped or taken back."),
            S("Rework", QuantityStateKind.Now, openRework, "Taken back for rework, not yet closed."),
            S("Repair", QuantityStateKind.Now, openRepair, "Taken back for an approved repair, not yet closed."),
            S("Produced, not received", QuantityStateKind.Now, notReceived, "Produced and not yet received into stock."),
            S("Pending inspection", QuantityStateKind.Now, pending, "In stock from this order, waiting for inspection."),
            S("Released", QuantityStateKind.Now, released, "In stock from this order, released by quality, not at shipping."),
            S("Hold", QuantityStateKind.Now, hold, "In stock from this order, on quality hold."),
            S("Packed", QuantityStateKind.Now, 0, "Packed into containers (V3.14)."),
            S("Staged", QuantityStateKind.Now, staged, "Released and at a shipping location, ready to ship."),
        ], notes);
    }
}
