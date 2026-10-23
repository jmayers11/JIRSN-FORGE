namespace Forge.Inventory.Contracts;

/// <summary>
/// The quality status of stock (V3.1: renamed from "Available" to "Released", because quality release is not
/// availability for planning). Only Released stock can be issued, and only Released stock in a nettable
/// location counts for MRP and readiness. Location, custody, nettability and point of use are separate
/// dimensions, read from the location (<see cref="StockPosition"/>).
/// </summary>
public enum StockStatus
{
    /// <summary>Released by quality for use.</summary>
    Released,

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

    /// <summary>Material staged for a work center (V3.1): at the point of use.</summary>
    Staging,

    /// <summary>Beside one machine (V3.1): at the point of use.</summary>
    MachineSide,

    /// <summary>Work in progress waiting between operations, possibly for a long time (V3.1).</summary>
    WipStorage,

    /// <summary>At a supplier doing outside processing (V3.1): company-owned, in the supplier's custody, never nettable.</summary>
    ExternalProcessor,

    /// <summary>Between sites or on its way to or from a processor (V3.1): never nettable.</summary>
    InTransit,
}

/// <summary>A location's place in its site's hierarchy, from the widest to the narrowest (V3.1).</summary>
public enum LocationLevel
{
    Area,
    Zone,
    Rack,
    Shelf,
    Bin,
    Position,
}

/// <summary>Who physically holds stock (V3.1). Ownership is not custody: stock at an outside processor is still the company's.</summary>
public enum Custody
{
    Company,
    Supplier,
    InTransit,
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

    /// <summary>Stock leaving the plant on a customer shipment.</summary>
    Shipment,

    /// <summary>Spare parts issued to a maintenance work order.</summary>
    MaintenanceIssue,

    /// <summary>Unused spare parts returned from a maintenance work order.</summary>
    MaintenanceReturn,

    /// <summary>Material issued to a facility or capital project line (V2.10).</summary>
    ProjectIssue,

    /// <summary>Unused material returned from a project line (V2.10).</summary>
    ProjectReturn,

    /// <summary>Part of an instance divided off into a new instance (V3.2): one entry out of the source, one into each result.</summary>
    Split,

    /// <summary>Instances combined into one where policy allows (V3.2): one entry out of each source, one into the result.</summary>
    Merge,

    /// <summary>A new instance cut from material issued to a production order and put back into stock (V3.3).</summary>
    Remnant,

    /// <summary>A new instance of another part a production order made alongside its output (V3.3).</summary>
    Byproduct,

    /// <summary>Rejected material shipped back to its supplier (V3.11).</summary>
    ReturnToVendor,

    /// <summary>Material a customer sent back against a shipment, received onto hold (V3.11).</summary>
    CustomerReturn,
}

/// <summary>What kind of physical instance a lot row is (V3.2). The quantity is always the ledger sum; the kind never changes.</summary>
public enum IdentityKind
{
    Lot,
    Batch,
    Roll,
    Serial,
    Reel,
    Sheet,
    Piece,
}

public enum InstanceRelation
{
    /// <summary>The result was divided off the source.</summary>
    Split,

    /// <summary>The source was combined into the result.</summary>
    Merge,

    /// <summary>The result is replacement material a supplier sent for the returned source (V3.11).</summary>
    Replaces,
}

/// <summary>A recorded link between two physical instances (V3.2), never changed: what became what, how much, when, by whom.</summary>
public sealed record InstanceLink(
    long LinkId,
    InstanceRelation Relation,
    string SourcePart,
    string SourceLot,
    string ResultPart,
    string ResultLot,
    decimal Quantity,
    long OutTransactionId,
    long InTransactionId,
    string? Reason,
    DateTimeOffset At,
    string Actor);

/// <summary>
/// Where an instance came from and what came of it (V3.2): the instances it was split from or merged out of
/// (sources), the instances divided off or merged into (results), and the first instances its line goes back to
/// (origins, the ones nothing was split or merged into).
/// </summary>
public sealed record InstanceLineage(string PartNumber, string LotNumber, IReadOnlyList<InstanceLink> Sources, IReadOnlyList<InstanceLink> Results, IReadOnlyList<string> Origins)
{
    public static InstanceLineage From(string partNumber, string lotNumber, IReadOnlyList<InstanceLink> links)
    {
        ArgumentNullException.ThrowIfNull(links);
        var into = links.ToLookup(l => (l.ResultPart, l.ResultLot));
        var origins = new SortedSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<(string, string)>();
        var queue = new Queue<(string Part, string Lot)>([(partNumber, lotNumber)]);
        while (queue.Count > 0)
        {
            var at = queue.Dequeue();
            if (!seen.Add(at))
            {
                continue;
            }

            var parents = into[at].ToList();
            if (parents.Count == 0)
            {
                if (at != (partNumber, lotNumber))
                {
                    origins.Add(at.Part == partNumber ? at.Lot : $"{at.Part}/{at.Lot}");
                }

                continue;
            }

            foreach (var p in parents)
            {
                queue.Enqueue((p.SourcePart, p.SourceLot));
            }
        }

        return new InstanceLineage(
            partNumber,
            lotNumber,
            into[(partNumber, lotNumber)].ToList(),
            links.Where(l => l.SourcePart == partNumber && l.SourceLot == lotNumber).ToList(),
            origins.ToList());
    }
}

/// <summary>What a transformation line accounts for (V3.3).</summary>
public enum TransformationRole
{
    /// <summary>Issued material that went into the order's output.</summary>
    Consumed,

    /// <summary>Issued material scrapped at the work center.</summary>
    Scrap,

    /// <summary>Issued material lost in the process (evaporation, trim, swarf), not separately scrapped.</summary>
    Loss,

    /// <summary>Issued material still at the work center; the latest figure replaces earlier ones.</summary>
    Remaining,

    /// <summary>A new instance cut from an issued lot and put back into stock.</summary>
    Remnant,

    /// <summary>A new instance of another part the order made alongside its output.</summary>
    Byproduct,
}

/// <summary>An issued inventory instance and the quantity attributed to a byproduct.</summary>
public sealed record TransformationParent(string PartNumber, string LotNumber, decimal Quantity);

/// <param name="SourcePart">For a remnant, the issued part (the same part); for a byproduct, null.</param>
/// <param name="SourceLot">For a remnant, the issued lot it was cut from.</param>
/// <param name="Parents">Explicit issued lots attributed to a byproduct; these do not change material reconciliation.</param>
public sealed record TransformationLine(
    int LineNumber,
    TransformationRole Role,
    string PartNumber,
    string? LotNumber,
    decimal Quantity,
    string? SourcePart = null,
    string? SourceLot = null,
    string? LocationCode = null,
    long? TransactionId = null,
    IReadOnlyList<TransformationParent>? Parents = null);

/// <summary>What became of material issued to a production order, as recorded at one moment (V3.3). Never changed.</summary>
public sealed record TransformationInfo(long TransformationId, long OrderNumber, int? OperationSequence, string? Reason, DateTimeOffset RecordedAt, string Actor, IReadOnlyList<TransformationLine> Lines);

/// <summary>How far issued and accounted may differ, as a percent of what was issued (V3.3). Absent = 0.</summary>
public sealed record YieldTolerance(string PartNumber, decimal Percent, string Reason, string SetBy, DateTimeOffset SetAt);

public enum ReconciliationStatus
{
    /// <summary>Nothing has been recorded for this issued material yet.</summary>
    NotRecorded,

    /// <summary>Issued = consumed + scrap + loss + remnant + remaining, exactly.</summary>
    Balanced,

    /// <summary>The difference is within the part's yield tolerance.</summary>
    WithinTolerance,

    /// <summary>The difference is beyond tolerance: flagged until a later record explains it. Never scrapped automatically.</summary>
    Unexplained,
}

/// <summary>One issued part and lot on a production order, accounted for (V3.3).</summary>
public sealed record MaterialReconciliation(
    long OrderNumber,
    string PartNumber,
    string? LotNumber,
    decimal Issued,
    decimal Returned,
    decimal Consumed,
    decimal Scrap,
    decimal Loss,
    decimal Remnant,
    decimal Remaining,
    decimal TolerancePercent,
    ReconciliationStatus Status)
{
    public decimal NetIssued => Issued - Returned;

    public decimal Accounted => Consumed + Scrap + Loss + Remnant + Remaining;

    /// <summary>Net issued less everything accounted for: positive is material nobody has explained, negative is more accounted than issued.</summary>
    public decimal Unexplained => NetIssued - Accounted;

    /// <summary>
    /// Issued = consumed + returned + scrap + loss + remnant + remaining ± tolerance, per issued part and lot. Issues
    /// and returns come from the ledger; the rest from transformation records, where the latest remaining figure
    /// replaces earlier ones and everything else adds up.
    /// </summary>
    public static IReadOnlyList<MaterialReconciliation> For(
        long orderNumber,
        IEnumerable<InventoryTransaction> orderTransactions,
        IEnumerable<TransformationInfo> transformations,
        Func<string, decimal> tolerancePercent)
    {
        ArgumentNullException.ThrowIfNull(orderTransactions);
        ArgumentNullException.ThrowIfNull(transformations);
        ArgumentNullException.ThrowIfNull(tolerancePercent);
        var issued = new Dictionary<(string Part, string? Lot), (decimal Issued, decimal Returned)>();
        foreach (var t in orderTransactions.Where(t => t.OrderNumber == orderNumber && t.Kind is InventoryTransactionKind.Issue or InventoryTransactionKind.Return))
        {
            var key = (t.PartNumber, t.LotNumber);
            var (i, r) = issued.GetValueOrDefault(key);
            issued[key] = t.Kind == InventoryTransactionKind.Issue ? (i + t.Quantity, r) : (i, r + t.Quantity);
        }

        var records = transformations.Where(x => x.OrderNumber == orderNumber).OrderBy(x => x.TransformationId).ToList();
        var result = new List<MaterialReconciliation>();
        foreach (var ((part, lot), (i, r)) in issued.OrderBy(x => x.Key.Part, StringComparer.Ordinal).ThenBy(x => x.Key.Lot, StringComparer.Ordinal))
        {
            var lines = records.SelectMany(x => x.Lines.Select(l => (x.TransformationId, Line: l))).ToList();
            decimal Sum(TransformationRole role) => lines.Where(l => l.Line.Role == role && l.Line.PartNumber == part && l.Line.LotNumber == lot).Sum(l => l.Line.Quantity);
            var remnant = lines.Where(l => l.Line.Role == TransformationRole.Remnant && l.Line.SourcePart == part && l.Line.SourceLot == lot).Sum(l => l.Line.Quantity);
            var remaining = lines.LastOrDefault(l => l.Line.Role == TransformationRole.Remaining && l.Line.PartNumber == part && l.Line.LotNumber == lot).Line?.Quantity ?? 0;
            var recorded = lines.Any(l => (l.Line.PartNumber == part && l.Line.LotNumber == lot && l.Line.Role != TransformationRole.Remnant && l.Line.Role != TransformationRole.Byproduct)
                || (l.Line.Role == TransformationRole.Remnant && l.Line.SourcePart == part && l.Line.SourceLot == lot));
            var tolerance = tolerancePercent(part);
            var row = new MaterialReconciliation(orderNumber, part, lot, i, r, Sum(TransformationRole.Consumed), Sum(TransformationRole.Scrap), Sum(TransformationRole.Loss), remnant, remaining, tolerance, ReconciliationStatus.NotRecorded);
            var status = !recorded ? (row.NetIssued == 0 ? ReconciliationStatus.Balanced : ReconciliationStatus.NotRecorded)
                : row.Unexplained == 0 ? ReconciliationStatus.Balanced
                : Math.Abs(row.Unexplained) <= row.NetIssued * tolerance / 100m ? ReconciliationStatus.WithinTolerance
                : ReconciliationStatus.Unexplained;
            result.Add(row with { Status = status });
        }

        return result;
    }
}

/// <summary>Whether lots of a part may be merged, and what the sources must share (V3.2). Absent = never.</summary>
public sealed record MergePolicy(string PartNumber, bool Allowed, bool SameRevisionRequired, bool SameSupplierLotRequired, string Reason, string SetBy, DateTimeOffset SetAt);

/// <param name="ParentCode">The location this one sits in (V3.1); null for a top-level area.</param>
/// <param name="Nettable">Whether released stock here counts for planning (V3.1).</param>
/// <param name="WorkCenterCode">The work center this location serves; stock here is at the point of use for Production, Staging and MachineSide locations (V3.1).</param>
/// <param name="MachineCode">The machine a MachineSide location serves (V3.1).</param>
/// <param name="CustodianSupplier">The outside processor holding stock at an ExternalProcessor location (V3.1).</param>
public sealed record LocationInfo(
    string Code,
    string SiteCode,
    string Name,
    LocationKind Kind,
    bool Active,
    string? ParentCode = null,
    LocationLevel Level = LocationLevel.Area,
    bool Nettable = true,
    string? WorkCenterCode = null,
    string? MachineCode = null,
    string? CustodianSupplier = null)
{
    public Custody Custody => Kind switch
    {
        LocationKind.ExternalProcessor => Custody.Supplier,
        LocationKind.InTransit => Custody.InTransit,
        _ => Custody.Company,
    };

    /// <summary>Stock here is at the point of use of <see cref="WorkCenterCode"/>.</summary>
    public bool PointOfUse => WorkCenterCode is not null && Kind is LocationKind.Production or LocationKind.Staging or LocationKind.MachineSide;

    /// <summary>Stock here may be issued, shipped or counted by the company; not when a supplier or carrier holds it.</summary>
    public bool InCompanyCustody => Custody == Custody.Company;
}

/// <summary>
/// One balance with every dimension the V3 brief keeps apart (H): quantity, quality status, location and its
/// path, custody, whether it counts for planning, and the work center or machine it is at the point of use of.
/// Allocation to orders is computed by material planning; reservation of physical stock arrives in V3.4.
/// </summary>
public sealed record StockPosition(
    string PartNumber,
    string? LotNumber,
    string LocationCode,
    string LocationPath,
    LocationKind LocationKind,
    StockStatus QualityStatus,
    decimal Quantity,
    Custody Custody,
    string? CustodianSupplier,
    bool Nettable,
    string? PointOfUseWorkCenter,
    string? MachineCode,
    bool Usable = true)
{
    /// <summary>Counts for planning: released by quality, in a nettable location, and usable by shelf life (V3.6).</summary>
    public bool AvailableForPlanning => QualityStatus == StockStatus.Released && Nettable && Usable;

    /// <param name="notUsable">Lots that are not usable by shelf life (V3.6): on hand, never counted for planning.</param>
    public static IReadOnlyList<StockPosition> From(IEnumerable<StockBalance> balances, IReadOnlyCollection<LocationInfo> locations, IReadOnlySet<(string PartNumber, string LotNumber)>? notUsable = null)
    {
        ArgumentNullException.ThrowIfNull(balances);
        ArgumentNullException.ThrowIfNull(locations);
        var byCode = locations.ToDictionary(l => l.Code, StringComparer.Ordinal);
        return balances.Select(b =>
        {
            var l = byCode.GetValueOrDefault(b.LocationCode);
            return new StockPosition(
                b.PartNumber, b.LotNumber, b.LocationCode, Path(b.LocationCode, byCode), l?.Kind ?? LocationKind.Stores, b.Status, b.Quantity,
                l?.Custody ?? Custody.Company, l?.CustodianSupplier, l?.Nettable ?? true, l is { PointOfUse: true } ? l.WorkCenterCode : null, l?.MachineCode,
                notUsable is null || b.LotNumber is not { } lot || !notUsable.Contains((b.PartNumber, lot)));
        }).ToList();
    }

    /// <summary>"AREA-1 / RACK-4 / BIN-12": the location and the ones it sits in, widest first.</summary>
    public static string Path(string code, IReadOnlyDictionary<string, LocationInfo> byCode)
    {
        ArgumentNullException.ThrowIfNull(byCode);
        var parts = new List<string> { code };
        var seen = new HashSet<string>(StringComparer.Ordinal) { code };
        var current = byCode.GetValueOrDefault(code);
        while (current?.ParentCode is { } parent && seen.Add(parent) && parts.Count < 10)
        {
            parts.Insert(0, parent);
            current = byCode.GetValueOrDefault(parent);
        }

        return string.Join(" / ", parts);
    }
}

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
    long? RequestId,
    long? ShipmentId = null,
    long? WorkOrderId = null,
    string? PartRevision = null,
    string? SubstitutesFor = null,
    long? AlternateId = null,
    decimal? PrimaryQuantity = null,
    string? ProjectCode = null,
    int? ProjectLine = null);

/// <param name="PartRevision">The part revision received into the lot; null when it was not recorded (stock received before V2.3).</param>
/// <param name="IdentityKind">What the instance physically is (V3.2).</param>
/// <param name="ExpiresOn">The expiry the lot was given (V3.6); an approved extension is its own record and never changes it.</param>
/// <param name="RetestDue">The first retest date the lot was given (V3.6); retests are their own records.</param>
/// <param name="OpenedAt">When the container was first opened (V3.6), starting its open life.</param>
public sealed record LotInfo(
    string PartNumber,
    string LotNumber,
    string? SupplierLot,
    DateTimeOffset FirstReceivedAt,
    string? PartRevision = null,
    IdentityKind IdentityKind = IdentityKind.Lot,
    DateOnly? ManufacturedOn = null,
    DateOnly? ExpiresOn = null,
    DateOnly? RetestDue = null,
    DateTimeOffset? OpenedAt = null);

/// <summary>Net quantity of a part issued to a production order (issues less returns).</summary>
/// <summary>Net material issued to an order, in the terms of the part the order needs: approved alternates count at their converted quantity (V2.6).</summary>
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

public enum ShipmentStatus
{
    /// <summary>Being put together; lines can change.</summary>
    Open,

    /// <summary>Packed and checked against stock; waiting for a different person to ship it.</summary>
    Packed,

    Shipped,
    Cancelled,
}

public sealed record ShipmentLineInfo(int LineNumber, string PartNumber, decimal Quantity, string LocationCode, string? LotNumber);

public sealed record ShipmentInfo(
    long ShipmentId,
    string Customer,
    string? Reference,
    long? DemandId,
    string? ShipTo,
    ShipmentStatus Status,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string? PackedBy,
    DateTimeOffset? PackedAt,
    string? ShippedBy,
    DateTimeOffset? ShippedAt,
    string? Carrier,
    string? Tracking,
    string? CancelReason,
    IReadOnlyList<ShipmentLineInfo> Lines,
    bool PackagingRequired = false,
    string? ContainerCode = null);

public enum ContainerKind { Carton, Crate, Pallet }

public enum ContainerStatus { Open, Sealed, Shipped }

public sealed record ContainerContentInfo(int LineNumber, string PartNumber, decimal Quantity, string LocationCode, string? LotNumber, StockStatus Status);

/// <summary>A physical carton, crate or pallet and the stock identities inside it (V3.14).</summary>
public sealed record ContainerInfo(string Code, ContainerKind Kind, ContainerStatus Status, string CreatedBy, DateTimeOffset CreatedAt, IReadOnlyList<ContainerContentInfo> Contents, long? ShipmentId = null, DateTimeOffset? ShippedAt = null);

/// <summary>Packing source records for specified physical lots, without a screen's presentation limit.</summary>
public sealed record PackingRecords(IReadOnlyList<ContainerInfo> Containers, IReadOnlyList<ShipmentInfo> Shipments);
public sealed record TransformationOutputInfo(long TransformationId, long OrderNumber, int? OperationSequence, TransformationLine Line);

/// <summary>Spare parts used on a maintenance work order (issues less returns), by part.</summary>
/// <param name="SubstitutesFor">Issued in place of this part through an approved alternate (V2.9); <paramref name="PrimaryNetIssued"/> is how much of it that covers.</param>
/// <summary>Material for a project line (V2.10): received against purchases for it, and issued to it (issues less returns).</summary>
public sealed record ProjectMaterial(string ProjectCode, int LineNumber, string PartNumber, decimal Received, decimal NetIssued);

public sealed record WorkOrderPart(long WorkOrderId, string PartNumber, decimal NetIssued, string? SubstitutesFor = null, decimal? PrimaryNetIssued = null);

/// <summary>What has shipped against a demand, by part.</summary>
public sealed record DemandShipped(long DemandId, string PartNumber, decimal Shipped);

/// <summary>What other contexts may ask Inventory.</summary>
public interface IInventoryLookup
{
    Task<bool> IsRecordedRemnantAsync(string partNumber, string lotNumber, CancellationToken ct);
    Task<TransformationOutputInfo?> FindTransformationOutputAsync(long transformationId, int lineNumber, CancellationToken ct);
    Task<PackingRecords> PackingForLotsAsync(string partNumber, IReadOnlyCollection<string> lotNumbers, CancellationToken ct);

    Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken ct);

    Task<ContainerInfo?> FindContainerAsync(string code, CancellationToken ct);

    Task<IReadOnlyList<LocationInfo>> ListLocationsAsync(CancellationToken ct);

    /// <summary>One recorded location by its exact authoritative code.</summary>
    Task<LocationInfo?> FindLocationAsync(string code, CancellationToken ct);

    /// <summary>Non-zero balances, optionally for one part, by part, location, lot and status.</summary>
    Task<IReadOnlyList<StockBalance>> ListBalancesAsync(string? partNumber, CancellationToken ct);

    /// <summary>Latest entries first, optionally for one part or one production order.</summary>
    Task<IReadOnlyList<InventoryTransaction>> ListTransactionsAsync(string? partNumber, long? orderNumber, int take, CancellationToken ct);

    /// <summary>Every transaction recorded in [from, to), oldest first (for reports).</summary>
    Task<IReadOnlyList<InventoryTransaction>> ListTransactionsBetweenAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    /// <summary>Shipped shipments made against any of the given demands, with their lines.</summary>
    Task<IReadOnlyList<ShipmentInfo>> ListShippedForDemandsAsync(IReadOnlyCollection<long> demandIds, CancellationToken ct);

    Task<IReadOnlyList<LotInfo>> ListLotsAsync(string? partNumber, CancellationToken ct);

    /// <summary>One physical identity by its exact part and lot keys, including identities with no current stock.</summary>
    Task<LotInfo?> FindLotAsync(string partNumber, string lotNumber, CancellationToken ct);

    Task<IReadOnlyList<OrderIssue>> ListOrderIssuesAsync(CancellationToken ct);

    /// <summary>Material received for and issued to project lines, for one project or all (V2.10).</summary>
    Task<IReadOnlyList<ProjectMaterial>> ListProjectMaterialAsync(string? projectCode, CancellationToken ct);

    Task<IReadOnlyList<PurchaseReceipt>> ListPurchaseReceiptsAsync(CancellationToken ct);

    Task<IReadOnlyList<ProductionReceipt>> ListProductionReceiptsAsync(CancellationToken ct);

    Task<IReadOnlyList<AdjustmentRequest>> ListRequestsAsync(bool pendingOnly, CancellationToken ct);

    /// <summary>Shipments, newest first; shipped and cancelled ones only when asked.</summary>
    Task<IReadOnlyList<ShipmentInfo>> ListShipmentsAsync(bool includeClosed, CancellationToken ct);

    Task<ShipmentInfo?> FindShipmentAsync(long shipmentId, CancellationToken ct);

    Task<IReadOnlyList<DemandShipped>> ListShippedByDemandAsync(CancellationToken ct);

    /// <summary>Spare parts per work order; one order, or all.</summary>
    Task<IReadOnlyList<WorkOrderPart>> ListWorkOrderPartsAsync(long? workOrderId, CancellationToken ct);

    /// <summary>Links where the instance is the source or the result (V3.2); every link when no part is given.</summary>
    Task<IReadOnlyList<InstanceLink>> ListInstanceLinksAsync(string? partNumber, string? lotNumber, CancellationToken ct);

    /// <summary>The merge policy in force for a part, or null (never merged).</summary>
    Task<MergePolicy?> FindMergePolicyAsync(string partNumber, CancellationToken ct);

    /// <summary>Transformation records, oldest first: for one order, or touching one part (and lot), or all (V3.3).</summary>
    Task<IReadOnlyList<TransformationInfo>> ListTransformationsAsync(long? orderNumber, string? partNumber, string? lotNumber, CancellationToken ct);

    /// <summary>The yield tolerance in force for each part that has one (V3.3).</summary>
    Task<IReadOnlyList<YieldTolerance>> ListYieldTolerancesAsync(CancellationToken ct);

    /// <summary>The ledger entries genealogy reads (V3.3): receipts, issues and returns to production orders, production receipts, remnants, byproducts, shipments and maintenance issues (installs). Never moves.</summary>
    Task<IReadOnlyList<InventoryTransaction>> ListGenealogyTransactionsAsync(CancellationToken ct);

    /// <summary>Material tasks (V3.4): open ones (requested, allocated, picked) or all, optionally for one order.</summary>
    Task<IReadOnlyList<MaterialTask>> ListMaterialTasksAsync(bool openOnly, long? orderNumber, CancellationToken ct);

    Task<MaterialTask?> FindMaterialTaskAsync(long taskId, CancellationToken ct);

    Task<MaterialTask?> FindMaterialTaskBySuspectReportAsync(long reportId, CancellationToken ct);

    Task<IReadOnlyList<MaterialTaskCustodyEvent>> ListMaterialTaskCustodyEventsAsync(long taskId, CancellationToken ct);

    Task<IReadOnlyList<CycleCountProgram>> ListCountProgramsAsync(CancellationToken ct);

    /// <summary>The last count of every balance ever counted, with or without a variance (V3.4).</summary>
    Task<IReadOnlyList<LastCount>> ListLastCountsAsync(CancellationToken ct);
}
