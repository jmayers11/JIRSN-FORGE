using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Planning;
using Forge.Resources.Contracts;
using Forge.Sales.Contracts;

namespace Forge.Host.Sales;

/// <summary>
/// Owner decision 2026-11-01: nothing ships to a customer on credit hold unless that shipment, or the sales order it ships, was
/// released. A shipment belongs to a customer through the sales order its demand was planned from, or by naming the customer's code.
/// </summary>
public sealed class CreditShipmentCheck : IShipmentReleaseCheck
{
    public async Task CheckAsync(IKernelUnitOfWork uow, ShipmentInfo shipment, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(shipment);
        if (!await uow.IsOnAsync(SiteFeatures.Receivables, ct)) return;
        var sales = uow.Store<ISalesLookup>();
        string? customer = null;
        long? salesOrder = null;
        if (shipment.DemandId is { } demandId)
        {
            var demand = (await uow.Store<IDemandStore>().ListAsync(true, ct)).FirstOrDefault(d => d.DemandId == demandId);
            if (SalesOrderInfo.ParseDemandSource(demand?.SourceKey) is { } source && await sales.FindOrderAsync(source.OrderNumber, ct) is { } order)
                (customer, salesOrder) = (order.CustomerCode, order.OrderNumber);
        }

        customer ??= (await sales.ListCustomersAsync(ct)).FirstOrDefault(c => string.Equals(c.Code, shipment.Customer.Trim(), StringComparison.OrdinalIgnoreCase))?.Code;
        if (customer is null) return;
        await CreditGate.RequireShipmentAsync(uow, customer, salesOrder, shipment.ShipmentId, await uow.SiteDateAsync(now, ct), ct);
    }
}
