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
/// <param name="RequisitionId">The requisition the line was bought for (V2.7), with its source: why this was purchased.</param>
public sealed record PurchaseLineInfo(
    int LineNumber,
    string PartNumber,
    decimal Quantity,
    string UnitOfMeasure,
    DateOnly PromisedDate,
    PurchaseLineStatus Status,
    long? RequisitionId = null,
    PurchaseSourceKind? SourceKind = null,
    string? SourceKey = null,
    string? SourceLabel = null);

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

/// <summary>One document in a technical package: the revision the supplier was sent, and its file.</summary>
public sealed record PackageDocument(string DocumentNumber, string Title, string? Revision, string State, string Source, string? FileSha256, string? FileName);

/// <summary>
/// What a purchase order line was bought to (V2.5): the item revision and document revisions in force when
/// the order was approved. Written once, never changed.
/// </summary>
public sealed record TechnicalPackage(long PoNumber, int LineNumber, string PartNumber, string? ItemRevision, string Summary, DateTimeOffset RecordedAt, string RecordedBy, IReadOnlyList<PackageDocument> Documents);

/// <summary>Works out each line's technical package inside the approval's transaction (implemented by the host, which can see documents).</summary>
public interface ITechnicalPackageResolver
{
    Task<IReadOnlyList<TechnicalPackage>> ResolveAsync(Kernel.Access.IKernelUnitOfWork uow, PurchaseOrderInfo po, DateTimeOffset now, string actor, CancellationToken ct);
}

public interface ITechnicalPackageLookup
{
    Task<TechnicalPackage?> FindPackageAsync(long poNumber, int lineNumber, CancellationToken ct);

    /// <summary>Every recorded package of the open lines of approved orders for these parts.</summary>
    Task<IReadOnlyList<TechnicalPackage>> ListPackagesAsync(IReadOnlyCollection<string> partNumbers, CancellationToken ct);
}

/// <summary>Why something is bought: the demand kinds, plus order material (Production), safety stock and a mixture.</summary>
public enum PurchaseSourceKind
{
    CustomerOrder,
    Forecast,
    Service,
    Internal,
    ContractDelivery,
    Maintenance,
    MroReplenishment,
    FacilityProject,
    CapitalProject,
    Quality,
    Tooling,
    Engineering,

    /// <summary>Material for production orders.</summary>
    Production,

    /// <summary>Kept on hand: safety stock and lot-size rounding.</summary>
    SafetyStock,

    /// <summary>Several sources at once (the label lists them).</summary>
    Mixed,
}

public enum RequisitionStatus
{
    Open,
    Ordered,
    Cancelled,
}

/// <summary>
/// A request to buy, from any source (V2.7): what, how many, by when, the configuration required, a
/// preferred supplier, and — always — what it is for. A buyer turns it into a purchase order line, which
/// keeps the source.
/// </summary>
public sealed record RequisitionInfo(
    long RequisitionId,
    string PartNumber,
    decimal Quantity,
    DateOnly NeedDate,
    PurchaseSourceKind SourceKind,
    string SourceKey,
    string SourceLabel,
    long? DemandId,
    string? PlannedOrder,
    string? RequiredRevision,
    string? SupplierCode,
    Guid? ProgramId,
    RequisitionStatus Status,
    string RequestedBy,
    DateTimeOffset RequestedAt,
    long? PoNumber,
    int? PoLine,
    string? OrderedBy,
    DateTimeOffset? OrderedAt,
    string? CancelledBy,
    DateTimeOffset? CancelledAt,
    string? CancelledReason);

public interface IRequisitionLookup
{
    Task<IReadOnlyList<RequisitionInfo>> ListRequisitionsAsync(bool openOnly, CancellationToken ct);

    Task<RequisitionInfo?> FindRequisitionAsync(long requisitionId, CancellationToken ct);
}
