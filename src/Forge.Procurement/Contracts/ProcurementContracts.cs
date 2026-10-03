namespace Forge.Procurement.Contracts;

public enum PurchaseOrderStatus
{
    /// <summary>Being written. Not yet a commitment; MRP does not count it.</summary>
    Draft,

    /// <summary>Approved by someone other than the buyer. Open lines are scheduled receipts.</summary>
    Approved,

    /// <summary>Nothing more is expected, whatever was received.</summary>
    Closed,

    /// <summary>Withdrawn before approval.</summary>
    Cancelled,
}

public enum PurchaseLineStatus
{
    Open,
    Cancelled,
}

/// <summary>One purchased part on a purchase order, due on the supplier's promised date.</summary>
public sealed record PurchaseLineInfo(int LineNumber, string PartNumber, decimal Quantity, string UnitOfMeasure, DateOnly PromisedDate, PurchaseLineStatus Status);

/// <param name="Supplier">The supplier's name as written on the order.</param>
/// <param name="SupplierCode">The supplier record the order is placed with.</param>
public sealed record PurchaseOrderInfo(
    long PoNumber,
    string Supplier,
    PurchaseOrderStatus Status,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    IReadOnlyList<PurchaseLineInfo> Lines,
    string? SupplierCode = null);

public enum SupplierStatus
{
    Active,

    /// <summary>No new purchase orders until the hold is lifted; open orders stay open.</summary>
    OnHold,

    /// <summary>No longer used. Kept for history.</summary>
    Inactive,
}

public sealed record SupplierInfo(string Code, string Name, SupplierStatus Status, string? Notes, string CreatedBy, DateTimeOffset CreatedAt);

/// <summary>A supplier approved to supply an item. An item with any approved source is bought only from them.</summary>
public sealed record ApprovedSourceInfo(string PartNumber, string SupplierCode, string? Note, string ApprovedBy, DateTimeOffset ApprovedAt);

/// <summary>What other contexts may ask Procurement. Received quantities belong to Inventory.</summary>
public interface IProcurementLookup
{
    /// <summary>Every purchase order with its lines, newest first.</summary>
    Task<IReadOnlyList<PurchaseOrderInfo>> ListPurchaseOrdersAsync(CancellationToken ct);

    Task<PurchaseOrderInfo?> FindPurchaseOrderAsync(long poNumber, CancellationToken ct);

    Task<IReadOnlyList<SupplierInfo>> ListSuppliersAsync(CancellationToken ct);

    Task<SupplierInfo?> FindSupplierAsync(string code, CancellationToken ct);

    /// <summary>Approved sources of one item, or of every item.</summary>
    Task<IReadOnlyList<ApprovedSourceInfo>> ListApprovedSourcesAsync(string? partNumber, CancellationToken ct);
}
