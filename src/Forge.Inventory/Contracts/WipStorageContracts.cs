using Forge.Kernel.Access;

namespace Forge.Inventory.Contracts;

public enum StorageRuleState
{
    Proposed,
    Active,
    Rejected,
    Retired,
}

/// <summary>
/// The approved safe holding capacity of one location for one part (Sprint 10 §18), set by a site safety review, never derived from
/// floor area or volume. Utilization and dwell limits are optional; without a utilization limit the whole approved quantity counts.
/// </summary>
public sealed record StorageCapacityRule(
    long RuleId,
    string LocationCode,
    string PartNumber,
    decimal MaxUnits,
    decimal? MaxUtilizationPercent,
    int? MaxDwellHours,
    string SafetyReference,
    StorageRuleState State,
    string ProposedBy,
    DateTimeOffset ProposedAt,
    string? DecidedBy,
    DateTimeOffset? DecidedAt);

public enum PartialBuildState
{
    Active,
    Completed,
    Cancelled,
}

/// <param name="IssuedAtApproval">Net issues of the part to the order when the build was approved; later issues draw the reservation down.</param>
public sealed record PartialBuildMaterial(string PartNumber, decimal Quantity, decimal IssuedAtApproval);

/// <summary>
/// An approved partial build (Sprint 10 §17): build <see cref="Quantity"/> through operation <see cref="Sequence"/> now and hold the
/// work-in-process at <see cref="StorageLocation"/> until the missing material arrives. It reserves its material and storage space.
/// </summary>
public sealed record PartialBuild(
    long BuildId,
    long OrderNumber,
    int Sequence,
    decimal Quantity,
    string? StorageLocation,
    decimal StorageQuantity,
    string PartNumber,
    string Evidence,
    PartialBuildState State,
    string ApprovedBy,
    DateTimeOffset ApprovedAt,
    string? ClosedBy,
    DateTimeOffset? ClosedAt,
    IReadOnlyList<PartialBuildMaterial> Materials);

/// <summary>Material an approved partial build still holds for its order: its reservation less what has been issued to the order since approval.</summary>
public sealed record PartialBuildReservation(long BuildId, long OrderNumber, string PartNumber, decimal Outstanding);

/// <summary>
/// What a storage location holds now, from the ledger, plus the space approved partial builds have reserved there.
/// <see cref="OtherParts"/> lists other parts on hand there: their space cannot be expressed in this rule's units.
/// </summary>
public sealed record StorageOccupancy(string LocationCode, string PartNumber, decimal OnHand, decimal Reserved, IReadOnlyList<string> OtherParts)
{
    public bool Mixed => OtherParts.Count > 0;
}

/// <summary>The free operational space under one active rule: floor(max × utilization) less what is on hand and reserved.</summary>
public sealed record StorageAvailability(StorageCapacityRule Rule, StorageOccupancy Occupancy, decimal OperationalLimit, decimal? Free, string Status);

public interface IWipStorageLookup
{
    Task<IReadOnlyList<StorageCapacityRule>> ListStorageRulesAsync(CancellationToken ct);

    Task<IReadOnlyList<PartialBuild>> ListPartialBuildsAsync(bool activeOnly, long? orderNumber, CancellationToken ct);
}

/// <summary>Reads shared by Inventory commands and Planning previews, so both apply the same storage and reservation arithmetic.</summary>
public static class WipStorageQueries
{
    public static async Task<IReadOnlyList<PartialBuildReservation>> OutstandingReservationsAsync(IKernelUnitOfWork uow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var builds = await uow.Store<IWipStorageLookup>().ListPartialBuildsAsync(true, null, ct);
        if (builds.Count == 0)
        {
            return [];
        }

        var issued = (await uow.Store<IInventoryLookup>().ListOrderIssuesAsync(ct)).ToDictionary(i => (i.OrderNumber, i.PartNumber), i => i.NetIssued);
        return builds
            .SelectMany(b => b.Materials.Select(m => new PartialBuildReservation(b.BuildId, b.OrderNumber, m.PartNumber,
                Math.Max(0m, m.Quantity - Math.Max(0m, issued.GetValueOrDefault((b.OrderNumber, m.PartNumber)) - m.IssuedAtApproval)))))
            .Where(r => r.Outstanding > 0)
            .ToList();
    }

    public static async Task<StorageOccupancy> OccupancyAsync(IKernelUnitOfWork uow, string locationCode, string partNumber, DateTimeOffset now, long? excludeBuildId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var here = (await ShelfLifeQueries.PositionsAsync(uow, null, now, ct)).Where(p => p.LocationCode == locationCode && p.Quantity > 0).ToList();
        var reserved = (await uow.Store<IWipStorageLookup>().ListPartialBuildsAsync(true, null, ct))
            .Where(b => b.StorageLocation == locationCode && b.BuildId != excludeBuildId)
            .ToList();
        return new StorageOccupancy(locationCode, partNumber,
            here.Where(p => p.PartNumber == partNumber).Sum(p => p.Quantity),
            reserved.Where(b => b.PartNumber == partNumber).Sum(b => b.StorageQuantity),
            [.. here.Select(p => p.PartNumber).Concat(reserved.Select(b => b.PartNumber)).Where(p => p != partNumber).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
    }

    /// <summary>Free space at every active rule for the part. Mixed-part locations report no free space: the rule cannot measure them.</summary>
    public static async Task<IReadOnlyList<StorageAvailability>> AvailabilityAsync(IKernelUnitOfWork uow, string partNumber, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var result = new List<StorageAvailability>();
        foreach (var rule in (await uow.Store<IWipStorageLookup>().ListStorageRulesAsync(ct)).Where(r => r.State == StorageRuleState.Active && r.PartNumber == partNumber).OrderBy(r => r.LocationCode, StringComparer.Ordinal))
        {
            result.Add(Availability(rule, await OccupancyAsync(uow, rule.LocationCode, partNumber, now, null, ct)));
        }

        return result;
    }

    public static StorageAvailability Availability(StorageCapacityRule rule, StorageOccupancy occupancy)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(occupancy);
        var limit = decimal.Floor(rule.MaxUnits * (rule.MaxUtilizationPercent ?? 100m) / 100m);
        if (occupancy.Mixed)
        {
            return new StorageAvailability(rule, occupancy, limit, null,
                $"{rule.LocationCode} also holds {string.Join(", ", occupancy.OtherParts)}; its free space for {rule.PartNumber} is unknown until a safety review covers the mix.");
        }

        var free = Math.Max(0m, limit - occupancy.OnHand - occupancy.Reserved);
        return new StorageAvailability(rule, occupancy, limit, free,
            $"{Q(free)} of {Q(limit)} free at {rule.LocationCode} ({Q(occupancy.OnHand)} on hand, {Q(occupancy.Reserved)} reserved; approved {Q(rule.MaxUnits)}" +
            (rule.MaxUtilizationPercent is { } u ? $" at {Q(u)}% utilization" : string.Empty) + $", {rule.SafetyReference}).");
    }

    private static string Q(decimal value) => value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
}
