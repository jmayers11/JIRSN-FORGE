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
    decimal OrderMultiple = 0);

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

    /// <summary>Every released revision (in force, future or ended) with its BOM, by part then effective date.</summary>
    Task<IReadOnlyList<RevisionStructure>> ListReleasedStructuresAsync(CancellationToken ct);
}
