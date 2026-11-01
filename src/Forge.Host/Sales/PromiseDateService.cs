using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Procurement.Contracts;
using Forge.Resources.Contracts;

namespace Forge.Host.Sales;

/// <summary>A supply the promise counts on: free stock today, an approved purchase order line or an open production order.</summary>
public sealed record PromiseSupply(DateOnly Date, decimal Quantity, string Source);

/// <summary>
/// The earliest date a quantity can be promised (available to promise). Free stock is what is available for planning less every active
/// reservation; then open purchase order lines and open production orders of the part by their dates. When they do not cover the
/// quantity, the part's lead time from today is the earliest honest date. Other orders' unreserved demand is not netted, so the date is
/// optimistic where supply is contested; it is advice for the quote, never a commitment by itself.
/// </summary>
public sealed record PromiseDate(string PartNumber, decimal Quantity, DateOnly Earliest, string Basis, decimal FreeNow, IReadOnlyList<PromiseSupply> Supplies);

public sealed class PromiseDateService(IKernelUnitOfWorkFactory unitOfWork, TimeProvider time)
{
    public async Task<PromiseDate> PromiseAsync(string rawPart, decimal quantity, CancellationToken ct)
    {
        var part = BusinessCode.Require(rawPart, "Part");
        if (quantity <= 0) throw new CommandRejectedException("invalid_quantity", "Ask for a quantity above zero.");
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var item = await uow.Store<IEngineeringLookup>().FindItemAsync(part, ct) ?? throw new CommandRejectedException("unknown_part", $"Part {part} does not exist.");
        var now = time.GetUtcNow();
        var today = await uow.SiteDateAsync(now, ct);
        var reserved = (await StockReservationQueries.OutstandingAsync(uow, part, ct)).Sum(b => b.Outstanding);
        var free = Math.Max(0m, await StockReservationQueries.AvailableAsync(uow, part, now, ct) - reserved);
        var supplies = new List<PromiseSupply> { new(today, free, "Free stock (available less reservations)") };

        var received = (await uow.Store<IInventoryLookup>().ListPurchaseReceiptsAsync(ct)).ToDictionary(r => (r.PoNumber, r.LineNumber), r => r.Received);
        foreach (var po in (await uow.Store<IProcurementLookup>().ListPurchaseOrdersAsync(ct)).Where(p => p.Status == PurchaseOrderStatus.Approved))
            foreach (var line in po.Lines.Where(l => l.Status == PurchaseLineStatus.Open && l.PartNumber == part))
            {
                var open = line.Quantity - received.GetValueOrDefault((po.PoNumber, line.LineNumber));
                if (open > 0) supplies.Add(new PromiseSupply(line.PromisedDate < today ? today : line.PromisedDate, open, $"PO {po.PoNumber} line {line.LineNumber}"));
            }

        var execution = uow.Store<IExecutionLookup>();
        foreach (var header in (await execution.ListOrdersAsync(ct)).Where(o => o.PartNumber == part && o.Status is OrderStatus.FirmPlanned or OrderStatus.Released or OrderStatus.Held))
        {
            var order = await execution.FindOrderAsync(header.OrderNumber, ct);
            var routed = order?.Routed() ?? [];
            var produced = routed.Count == 0 ? 0 : routed[^1].GoodQuantity;
            var open = header.Quantity - produced;
            if (open > 0) supplies.Add(new PromiseSupply(header.DueDate < today ? today : header.DueDate, open, $"Production order {header.OrderNumber} ({header.Status})"));
        }

        supplies = supplies.Where(s => s.Quantity > 0).OrderBy(s => s.Date).ThenBy(s => s.Source, StringComparer.Ordinal).ToList();
        var cumulative = 0m;
        foreach (var supply in supplies)
        {
            cumulative += supply.Quantity;
            if (cumulative >= quantity)
                return new PromiseDate(part, quantity, supply.Date, supply.Date == today && supply.Source.StartsWith("Free", StringComparison.Ordinal) ? "In free stock today." : $"Covered once {supply.Source} arrives.", free, supplies);
        }

        var leadTime = today.AddDays(Math.Max(0, item.LeadTimeDays));
        if (supplies.Count > 0 && supplies[^1].Date > leadTime) leadTime = supplies[^1].Date;
        return new PromiseDate(part, quantity, leadTime,
            $"Open supply covers {cumulative} of {quantity}; the rest needs a new {(item.MakeBuy == MakeBuy.Make ? "production order" : "purchase")} at the part's {item.LeadTimeDays}-day lead time.", free, supplies);
    }
}
