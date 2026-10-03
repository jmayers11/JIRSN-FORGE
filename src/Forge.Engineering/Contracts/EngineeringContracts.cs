namespace Forge.Engineering.Contracts;

public enum MakeBuy
{
    Make,
    Buy,
}

public enum RevisionStatus
{
    /// <summary>Being defined. BOM and routing may change. Nothing can be built from it.</summary>
    Draft,

    /// <summary>Frozen. In force from <see cref="RevisionInfo.EffectiveFrom"/> until superseded.</summary>
    Released,
}

/// <summary>How MRP sizes a planned order once it knows the net requirement.</summary>
public enum LotPolicy
{
    /// <summary>Exactly the net requirement.</summary>
    LotForLot,

    /// <summary>Whole multiples of <see cref="ItemInfo.OrderQuantity"/> (for example a full coil or pallet).</summary>
    FixedQuantity,

    /// <summary>At least <see cref="ItemInfo.OrderQuantity"/>.</summary>
    MinimumQuantity,
}

/// <summary>
/// What kind of thing an item is (V2 §10). The class decides how it is bought, documented, inspected and
/// substituted. <see cref="Unclassified"/> is honest about not knowing: nothing is inferred from Make/Buy.
/// </summary>
public enum ItemClass
{
    Unclassified,

    /// <summary>Made here from our own design.</summary>
    Make,

    /// <summary>Bought, made by a supplier to our drawing.</summary>
    BuildToPrint,

    /// <summary>Commercial off-the-shelf: the manufacturer owns the design and its revisions.</summary>
    Cots,
    RawMaterial,
    StandardHardware,
    Consumable,

    /// <summary>Maintenance, repair and operations stock: spares, filters, belts, lubricants.</summary>
    Mro,
    Tooling,

    /// <summary>A service or special process bought from a supplier (plating, heat treat, calibration).</summary>
    Service,
    OtherControlled,
}

public static class ItemClasses
{
    /// <summary>Classes that are always bought.</summary>
    public static bool AlwaysBought(ItemClass c) => c is ItemClass.BuildToPrint or ItemClass.Cots or ItemClass.RawMaterial or ItemClass.StandardHardware or ItemClass.Consumable or ItemClass.Service;

    public static bool Allows(ItemClass c, MakeBuy makeBuy) => c switch
    {
        ItemClass.Make => makeBuy == MakeBuy.Make,
        _ when AlwaysBought(c) => makeBuy == MakeBuy.Buy,
        _ => true,
    };

    /// <summary>The class a new item gets when none is given: made items are Make; anything bought must be classified by a person.</summary>
    public static ItemClass DefaultFor(MakeBuy makeBuy) => makeBuy == MakeBuy.Make ? ItemClass.Make : ItemClass.Unclassified;
}

public enum ManufacturerPartStatus
{
    /// <summary>Recorded, not yet approved. Never a reason to accept a purchase.</summary>
    Proposed,
    Approved,
    Withdrawn,
}

/// <summary>The manufacturer's stated lifecycle, as last recorded; Unknown when nobody has checked.</summary>
public enum ManufacturerLifecycle
{
    Active,
    NotRecommended,
    LastTimeBuy,
    EndOfLife,
    Unknown,
}

/// <summary>A manufacturer's part that may be bought as the item (V2 §12). The manufacturer owns its revision.</summary>
public sealed record ManufacturerPartInfo(
    string PartNumber,
    string Manufacturer,
    string Mpn,
    string? ManufacturerRevision,
    ManufacturerLifecycle Lifecycle,
    ManufacturerPartStatus Status,
    string? Datasheet,
    string? Note,
    string ProposedBy,
    DateTimeOffset ProposedAt,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAt);

/// <summary>
/// An item and its planning parameters. Lead time is in calendar days. Safety stock is the quantity
/// MRP keeps on hand; <see cref="OrderMultiple"/> (when above zero) rounds every planned order up.
/// </summary>
public sealed record ItemInfo(
    string PartNumber,
    string Description,
    string UnitOfMeasure,
    MakeBuy MakeBuy,
    int LeadTimeDays,
    bool LotTracked,
    bool Serialized,
    decimal SafetyStock = 0,
    LotPolicy LotPolicy = LotPolicy.LotForLot,
    decimal OrderQuantity = 0,
    decimal OrderMultiple = 0,
    ItemClass Class = ItemClass.Unclassified);

/// <summary>
/// One revision of an item. A released revision is in force from <see cref="EffectiveFrom"/> until
/// <see cref="EffectiveTo"/> (set when a later revision takes over or engineering withdraws it).
/// </summary>
public sealed record RevisionInfo(string PartNumber, string Revision, RevisionStatus Status, DateTimeOffset? EffectiveFrom, DateTimeOffset? EffectiveTo)
{
    public bool IsEffectiveAt(DateTimeOffset at) =>
        Status == RevisionStatus.Released && EffectiveFrom is { } from && from <= at && (EffectiveTo is null || at < EffectiveTo);
}

/// <summary>A component and how much of it one unit of the parent consumes, before scrap.</summary>
public sealed record BomLineInfo(int LineNumber, string ComponentPartNumber, decimal QuantityPer, decimal ScrapPercent);

/// <summary>
/// One routing step. With eligible machines, the operator requirement comes from each machine's
/// profile for the activity; <see cref="RequiredQualifications"/> are additional and all required.
/// </summary>
public sealed record RoutingOperationInfo(
    int Sequence,
    string Description,
    string WorkCenterCode,
    string ActivityCode,
    IReadOnlyList<string> EligibleMachines,
    IReadOnlyList<string> RequiredQualifications,
    decimal SetupMinutes,
    decimal RunMinutesPerUnit,
    int CrewSize);

/// <summary>A released revision and its bill of materials, for MRP explosion.</summary>
public sealed record RevisionStructure(RevisionInfo Revision, IReadOnlyList<BomLineInfo> Bom);

public sealed record RevisionDetail(ItemInfo Item, RevisionInfo Revision, IReadOnlyList<BomLineInfo> Bom, IReadOnlyList<RoutingOperationInfo> Routing);

public interface IEngineeringLookup
{
    Task<ItemInfo?> FindItemAsync(string partNumber, CancellationToken ct);

    Task<IReadOnlyList<ItemInfo>> ListItemsAsync(CancellationToken ct);

    Task<IReadOnlyList<RevisionInfo>> ListRevisionsAsync(string partNumber, CancellationToken ct);

    Task<RevisionDetail?> GetRevisionDetailAsync(string partNumber, string revision, CancellationToken ct);

    /// <summary>The released revision in force at the instant, if any.</summary>
    Task<RevisionInfo?> GetEffectiveRevisionAsync(string partNumber, DateTimeOffset at, CancellationToken ct);

    /// <summary>Manufacturer parts of one item, or of every item, by part, manufacturer and MPN.</summary>
    Task<IReadOnlyList<ManufacturerPartInfo>> ListManufacturerPartsAsync(string? partNumber, CancellationToken ct);

    /// <summary>Every released revision (in force, future or ended) with its BOM, by part then effective date.</summary>
    Task<IReadOnlyList<RevisionStructure>> ListReleasedStructuresAsync(CancellationToken ct);
}
