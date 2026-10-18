using Forge.Quality.Contracts;

namespace Forge.Inventory.Contracts;

/// <summary>What happens to material a receiving inspection rejected (V3.11).</summary>
public enum SupplierDisposition
{
    Pending,
    ReturnToVendor,
    ReworkBySupplier,
    Scrap,

    /// <summary>Accepted despite the defect; a second person approves before it is released.</summary>
    UseAsIs,
}

public enum RtvStatus
{
    Open,
    Shipped,

    /// <summary>The supplier sent replacement material, received as a new instance.</summary>
    Replaced,

    /// <summary>The supplier credited it instead.</summary>
    Credited,
}

public enum CustomerReturnDisposition
{
    Restock,
    Rework,
    Scrap,
}

/// <summary>One receiving inspection of (part of) a receipt: accepted and rejected quantities, results, and the FAI status found.</summary>
public sealed record ReceivingInspection(
    long InspectionId,
    long ReceiptTransactionId,
    string PartNumber,
    string? LotNumber,
    string LocationCode,
    long? PoNumber,
    string? SupplierCode,
    decimal Accepted,
    decimal Rejected,
    IReadOnlyList<ReceivingCharacteristicResult> Results,
    FaiStatus FaiStatus,
    string Inspector,
    DateTimeOffset At);

public sealed record SupplierRejection(
    long RejectionId,
    long InspectionId,
    string PartNumber,
    string? LotNumber,
    string LocationCode,
    string? SupplierCode,
    long? PoNumber,
    decimal Quantity,
    string Defect,
    SupplierDisposition Disposition,
    string? DispositionedBy = null,
    DateTimeOffset? DispositionedAt = null,
    string? ApprovedBy = null,
    DateTimeOffset? ApprovedAt = null,
    long? SplitFrom = null);

/// <summary>A return to vendor (V3.11): opened from a rejection, shipped back, then replaced (a new instance) or credited.</summary>
public sealed record RtvInfo(
    long RtvId,
    long RejectionId,
    string? SupplierCode,
    string PartNumber,
    string? LotNumber,
    string LocationCode,
    decimal Quantity,
    bool ForRework,
    RtvStatus Status,
    long? ShippedTransactionId = null,
    string? ShippedBy = null,
    DateTimeOffset? ShippedAt = null,
    string? ReplacementLot = null,
    long? ReplacementTransactionId = null,
    string? ClosedBy = null,
    DateTimeOffset? ClosedAt = null,
    string? Note = null);

/// <summary>Material a customer sent back against a shipment (V3.11), received onto hold and then dispositioned.</summary>
public sealed record CustomerReturnInfo(
    long RmaId,
    long ShipmentId,
    string Customer,
    string PartNumber,
    string? LotNumber,
    decimal Quantity,
    string LocationCode,
    string Reason,
    long ReceiptTransactionId,
    string ReceivedBy,
    DateTimeOffset ReceivedAt,
    CustomerReturnDisposition? Disposition = null,
    string? DispositionedBy = null,
    DateTimeOffset? DispositionedAt = null,
    long? DispositionTransactionId = null);

/// <summary>Where a receipt into inspection stands: received, accepted, rejected, still to inspect.</summary>
public sealed record ReceiptInspectionState(InventoryTransaction Receipt, string? SupplierCode, decimal Accepted, decimal Rejected, IReadOnlyList<ReceivingInspection> Inspections)
{
    public decimal Remaining => Receipt.Quantity - Accepted - Rejected;
}

public interface IReceivingLookup
{
    Task<CustomerReturnInfo?> FindCustomerReturnAsync(long rmaId, CancellationToken ct);
    Task<InventoryTransaction?> FindTransactionAsync(long transactionId, CancellationToken ct);

    Task<IReadOnlyList<ReceivingInspection>> ListReceivingInspectionsAsync(long? receiptTransactionId, CancellationToken ct);

    /// <summary>Receipts made into inspection status, newest first.</summary>
    Task<IReadOnlyList<InventoryTransaction>> ListInspectionReceiptsAsync(int take, CancellationToken ct);

    Task<IReadOnlyList<SupplierRejection>> ListRejectionsAsync(bool openOnly, CancellationToken ct);

    Task<IReadOnlyList<RtvInfo>> ListRtvsAsync(bool openOnly, CancellationToken ct);

    Task<IReadOnlyList<CustomerReturnInfo>> ListCustomerReturnsAsync(long? shipmentId, CancellationToken ct);
}

public interface IReceivingStore
{
    Task LockCustomerReturnAsync(long rmaId, CancellationToken ct);
    Task<long> InsertReceivingInspectionAsync(ReceivingInspection inspection, CancellationToken ct);

    Task<long> InsertRejectionAsync(SupplierRejection rejection, CancellationToken ct);

    Task UpdateRejectionAsync(SupplierRejection rejection, CancellationToken ct);

    Task<long> InsertRtvAsync(RtvInfo rtv, CancellationToken ct);

    Task UpdateRtvAsync(RtvInfo rtv, CancellationToken ct);

    Task<long> InsertCustomerReturnAsync(CustomerReturnInfo customerReturn, CancellationToken ct);

    Task UpdateCustomerReturnAsync(CustomerReturnInfo customerReturn, CancellationToken ct);
}

