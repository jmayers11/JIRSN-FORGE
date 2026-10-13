using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Security;

namespace Forge.Inventory;

/// <summary>Contract implementation delegates to the preparation gates reused by the real Inventory commands.</summary>
public sealed class InventoryRecoveryReadiness : IInventoryRecoveryReadiness
{
    public Task RequireIssueAsync(IKernelUnitOfWork uow, StockIssueReadinessRequest request, ForgePrincipal principal,
        DateTimeOffset now, CancellationToken ct) =>
        InventoryService.CheckIssueToOrderAsync(uow,
            new IssueToOrder(request.OrderNumber, request.PartNumber, request.Quantity, request.LocationCode, request.LotNumber, request.AlternateFor),
            principal, now, ct);

    public Task RequireTransferAsync(IKernelUnitOfWork uow, StockTransferReadinessRequest request, CancellationToken ct) =>
        InventoryService.CheckTransferStockAsync(uow,
            new TransferStock(request.PartNumber, request.Quantity, request.FromLocation, request.ToLocation, request.LotNumber, request.Status), ct);

    public Task RequireOutsideDispatchAsync(IKernelUnitOfWork uow, OutsideDispatchReadinessRequest request, ForgePrincipal principal,
        DateTimeOffset now, CancellationToken ct) =>
        InventoryService.CheckOutsideProcessDispatchAsync(uow,
            new DispatchOutsideProcess(request.PartNumber, request.Quantity, request.FromLocation, request.ProcessorLocation,
                request.LotNumber, request.PurchaseOrder, request.PurchaseOrderLine, request.MaterialRevision, request.SpecificationDocument,
                request.SpecificationRevision, request.ExpectedReturnDate, request.RequireCertificate), principal, now, ct);

    public Task RequireShipmentReleaseAsync(IKernelUnitOfWork uow, ShipmentInfo shipment, string actor, DateTimeOffset now, CancellationToken ct) =>
        InventoryService.CheckShipmentReleaseAsync(uow, shipment, actor, now, ct);
}
