using Forge.Kernel.Access;
using Forge.Kernel.Security;

namespace Forge.Inventory.Contracts;

/// <summary>Explicit stock issue inputs for a read-only source readiness check.</summary>
public sealed record StockIssueReadinessRequest(long OrderNumber, string PartNumber, decimal Quantity,
    string LocationCode, string? LotNumber, string? AlternateFor = null);

/// <summary>Explicit transfer inputs for a read-only source readiness check.</summary>
public sealed record StockTransferReadinessRequest(string PartNumber, decimal Quantity,
    string FromLocation, string ToLocation, string? LotNumber, StockStatus Status);

/// <summary>The controlled configuration and stock identities needed to evaluate a processor dispatch.</summary>
public sealed record OutsideDispatchReadinessRequest(string PartNumber, decimal Quantity, string FromLocation,
    string ProcessorLocation, string LotNumber, long PurchaseOrder, int PurchaseOrderLine,
    string? MaterialRevision = null, string? SpecificationDocument = null, string? SpecificationRevision = null,
    DateOnly? ExpectedReturnDate = null, bool RequireCertificate = true);

/// <summary>
/// Inventory owns the same source gates its real commands execute. Planning may request their
/// evaluation in its existing consistent snapshot; this port never locks, writes, or commits a command.
/// A failed source gate throws the existing nonsecret domain rejection with its recorded constraint.
/// </summary>
public interface IInventoryRecoveryReadiness
{
    Task RequireIssueAsync(IKernelUnitOfWork uow, StockIssueReadinessRequest request, ForgePrincipal principal,
        DateTimeOffset now, CancellationToken ct);

    Task RequireTransferAsync(IKernelUnitOfWork uow, StockTransferReadinessRequest request, CancellationToken ct);

    Task RequireOutsideDispatchAsync(IKernelUnitOfWork uow, OutsideDispatchReadinessRequest request, ForgePrincipal principal,
        DateTimeOffset now, CancellationToken ct);

    Task RequireShipmentReleaseAsync(IKernelUnitOfWork uow, ShipmentInfo shipment, string actor,
        DateTimeOffset now, CancellationToken ct);
}
