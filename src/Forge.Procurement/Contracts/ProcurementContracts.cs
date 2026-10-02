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

public sealed record PurchaseOrderInfo(
    long PoNumber,
    string Supplier,
    PurchaseOrderStatus Status,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    IReadOnlyList<PurchaseLineInfo> Lines);

/// <summary>What other contexts may ask Procurement. Received quantities belong to Inventory.</summary>
public interface IProcurementLookup
{
    /// <summary>Every purchase order with its lines, newest first.</summary>
    Task<IReadOnlyList<PurchaseOrderInfo>> ListPurchaseOrdersAsync(CancellationToken ct);

    Task<PurchaseOrderInfo?> FindPurchaseOrderAsync(long poNumber, CancellationToken ct);
}
