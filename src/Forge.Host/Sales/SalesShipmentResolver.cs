using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Planning;
using Forge.Sales.Contracts;

namespace Forge.Host.Sales;

/// <summary>
/// A sales order line's shipments: confirmed shipments made against the customer-order demand planned from that line. Shipments
/// belong to Inventory and the demand to Planning; the host can see both inside the caller's transaction.
/// </summary>
public sealed class SalesShipmentResolver : ISalesShipmentResolver
{
    public async Task<IReadOnlyList<SalesShipment>> ShippedAsync(IKernelUnitOfWork uow, long orderNumber, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var lines = (await uow.Store<IDemandStore>().ListAsync(true, ct))
            .Select(d => (Demand: d, Source: SalesOrderInfo.ParseDemandSource(d.SourceKey)))
            .Where(x => x.Source is { } s && s.OrderNumber == orderNumber)
            .ToDictionary(x => x.Demand.DemandId, x => (x.Source!.Value.LineNumber, x.Demand.PartNumber));
        if (lines.Count == 0) return [];
        var shipped = new List<SalesShipment>();
        foreach (var shipment in await uow.Store<IInventoryLookup>().ListShipmentsAsync(true, ct))
        {
            if (shipment.Status != ShipmentStatus.Shipped || shipment.DemandId is not { } demandId || !lines.TryGetValue(demandId, out var line)) continue;
            var quantity = shipment.Lines.Where(l => l.PartNumber == line.PartNumber).Sum(l => l.Quantity);
            if (quantity > 0)
                shipped.Add(new SalesShipment(shipment.ShipmentId, line.LineNumber, line.PartNumber, quantity, shipment.ShippedAt ?? shipment.CreatedAt, shipment.Carrier, shipment.Tracking));
        }

        return shipped.OrderBy(s => s.ShipmentId).ThenBy(s => s.OrderLine).ToList();
    }
}
