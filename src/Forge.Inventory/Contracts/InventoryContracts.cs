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
}

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
    string? MachineCode)
{
    /// <summary>Counts for planning: released by quality, in a nettable location.</summary>
    public bool AvailableForPlanning => QualityStatus == StockStatus.Released && Nettable;

    public static IReadOnlyList<StockPosition> From(IEnumerable<StockBalance> balances, IReadOnlyCollection<LocationInfo> locations)
    {
        ArgumentNullException.ThrowIfNull(balances);
        ArgumentNullException.ThrowIfNull(locations);
        var byCode = locations.ToDictionary(l => l.Code, StringComparer.Ordinal);
        return balances.Select(b =>
        {
            var l = byCode.GetValueOrDefault(b.LocationCode);
            return new StockPosition(
                b.PartNumber, b.LotNumber, b.LocationCode, Path(b.LocationCode, byCode), l?.Kind ?? LocationKind.Stores, b.Status, b.Quantity,
                l?.Custody ?? Custody.Company, l?.CustodianSupplier, l?.Nettable ?? true, l is { PointOfUse: true } ? l.WorkCenterCode : null, l?.MachineCode);
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
public sealed record LotInfo(string PartNumber, string LotNumber, string? SupplierLot, DateTimeOffset FirstReceivedAt, string? PartRevision = null);

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
    IReadOnlyList<ShipmentLineInfo> Lines);

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
    Task<IReadOnlyList<LocationInfo>> ListLocationsAsync(CancellationToken ct);

    /// <summary>Non-zero balances, optionally for one part, by part, location, lot and status.</summary>
    Task<IReadOnlyList<StockBalance>> ListBalancesAsync(string? partNumber, CancellationToken ct);

    /// <summary>Latest entries first, optionally for one part or one production order.</summary>
    Task<IReadOnlyList<InventoryTransaction>> ListTransactionsAsync(string? partNumber, long? orderNumber, int take, CancellationToken ct);

    /// <summary>Every transaction recorded in [from, to), oldest first (for reports).</summary>
    Task<IReadOnlyList<InventoryTransaction>> ListTransactionsBetweenAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    /// <summary>Shipped shipments made against any of the given demands, with their lines.</summary>
    Task<IReadOnlyList<ShipmentInfo>> ListShippedForDemandsAsync(IReadOnlyCollection<long> demandIds, CancellationToken ct);

    Task<IReadOnlyList<LotInfo>> ListLotsAsync(string? partNumber, CancellationToken ct);

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
}
