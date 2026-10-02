namespace Forge.Inventory.Contracts;

/// <summary>Whether stock may be used. Only Available stock can be issued or counts for MRP and readiness.</summary>
public enum StockStatus
{
    Available,

    /// <summary>Received and waiting for incoming inspection.</summary>
    Inspection,

    /// <summary>On quality hold.</summary>
    Hold,
}

public enum LocationKind
{
    Stores,
    Receiving,
    Inspection,
    Quarantine,
    Production,
    Shipping,
}

public enum InventoryTransactionKind
{
    Receipt,
    Issue,
    Return,
    Transfer,
    Adjustment,
    ProductionReceipt,
    Scrap,
    CycleCount,
    QualityHold,
    QualityRelease,
}

public sealed record LocationInfo(string Code, string SiteCode, string Name, LocationKind Kind, bool Active);

/// <summary>Quantity of a part at a location, lot and status: always the sum of its ledger entries.</summary>
public sealed record StockBalance(string PartNumber, string LocationCode, string? LotNumber, StockStatus Status, decimal Quantity);

/// <summary>
/// One ledger entry. Stock leaves (From) and/or arrives (To); a receipt has only To, an issue or
/// scrap only From. Entries are never changed or deleted; mistakes are corrected by new entries.
/// </summary>
public sealed record InventoryTransaction(
    long TransactionId,
    InventoryTransactionKind Kind,
    string PartNumber,
    decimal Quantity,
    string? FromLocation,
    StockStatus? FromStatus,
    string? ToLocation,
    StockStatus? ToStatus,
    string? LotNumber,
    long? OrderNumber,
    long? PoNumber,
    int? PoLine,
    string? Reason,
    DateTimeOffset At,
    string Actor,
    string? ApprovedBy,
    long? RequestId);

public sealed record LotInfo(string PartNumber, string LotNumber, string? SupplierLot, DateTimeOffset FirstReceivedAt);

/// <summary>Net quantity of a part issued to a production order (issues less returns).</summary>
public sealed record OrderIssue(long OrderNumber, string PartNumber, decimal NetIssued);

public sealed record PurchaseReceipt(long PoNumber, int LineNumber, decimal Received);

public sealed record ProductionReceipt(long OrderNumber, decimal Received);

public enum AdjustmentKind
{
    /// <summary>A stated change in quantity, with a reason.</summary>
    Adjustment,

    /// <summary>A physical count; the variance against the system quantity is what gets approved.</summary>
    Count,
}

public enum AdjustmentState
{
    Pending,
    Approved,
    Rejected,

    /// <summary>A count that agreed with the system; nothing to approve.</summary>
    Matched,
}

/// <summary>
/// A change to stock that needs a second person: an adjustment, or a count whose variance must be
/// posted. <see cref="Quantity"/> is the change for an adjustment and the counted quantity for a count.
/// </summary>
public sealed record AdjustmentRequest(
    long RequestId,
    AdjustmentKind Kind,
    string PartNumber,
    string LocationCode,
    string? LotNumber,
    StockStatus Status,
    decimal Quantity,
    decimal SystemQuantity,
    string Reason,
    string RequestedBy,
    DateTimeOffset RequestedAt,
    AdjustmentState State,
    string? DecidedBy,
    DateTimeOffset? DecidedAt,
    long? TransactionId)
{
    /// <summary>What approving it would change the quantity by.</summary>
    public decimal Variance => Kind == AdjustmentKind.Count ? Quantity - SystemQuantity : Quantity;
}

/// <summary>What other contexts may ask Inventory.</summary>
public interface IInventoryLookup
{
    Task<IReadOnlyList<LocationInfo>> ListLocationsAsync(CancellationToken ct);

    /// <summary>Non-zero balances, optionally for one part, by part, location, lot and status.</summary>
    Task<IReadOnlyList<StockBalance>> ListBalancesAsync(string? partNumber, CancellationToken ct);

    /// <summary>Latest entries first, optionally for one part or one production order.</summary>
    Task<IReadOnlyList<InventoryTransaction>> ListTransactionsAsync(string? partNumber, long? orderNumber, int take, CancellationToken ct);

    Task<IReadOnlyList<LotInfo>> ListLotsAsync(string? partNumber, CancellationToken ct);

    Task<IReadOnlyList<OrderIssue>> ListOrderIssuesAsync(CancellationToken ct);

    Task<IReadOnlyList<PurchaseReceipt>> ListPurchaseReceiptsAsync(CancellationToken ct);

    Task<IReadOnlyList<ProductionReceipt>> ListProductionReceiptsAsync(CancellationToken ct);

    Task<IReadOnlyList<AdjustmentRequest>> ListRequestsAsync(bool pendingOnly, CancellationToken ct);
}
