namespace Forge.Planning;

/// <summary>Canonical, already-netted released material available to one routing operation.</summary>
public sealed record OperationComponentAvailability(
    string PartNumber,
    decimal NetUsableQuantity,
    decimal RequiredQuantityPerGoodUnit,
    decimal FixedSetupQuantity,
    decimal GoodYieldFactor);

public enum PartialBuildCapacityKind { Operation, QualifiedLabor, Machine, SafeStorage, DownstreamAcceptance }

public sealed record PartialBuildLimit(PartialBuildCapacityKind Kind, string Name, decimal? MaximumUnits, bool Required = true);

public sealed record PartialBuildInput(
    decimal OrderRemainingUnits,
    decimal OperationRemainingUnits,
    IReadOnlyCollection<OperationComponentAvailability> OperationMaterials,
    IReadOnlyCollection<PartialBuildLimit> CapacityLimits,
    decimal MinimumBatchUnits,
    decimal OutputPackMultiple,
    bool OperationPrerequisitesReady,
    bool RequiredToolingReady,
    bool MaterialsReleasedAndTraceable,
    bool CustomerOrSiteAllowsPartialBuild);

public sealed record ComponentBuildLimit(string PartNumber, decimal BuildableGoodUnits);

public sealed record PartialBuildFeasibility(
    decimal OrderRemainingUnits,
    decimal OperationRemainingUnits,
    decimal MaterialFeasibleUnits,
    decimal CapacityFeasibleUnits,
    decimal RecommendedQuantity,
    IReadOnlyList<ComponentBuildLimit> ComponentLimits,
    IReadOnlyList<string> LimitingConstraints,
    IReadOnlyList<string> Blockers,
    bool IsRecommendationFeasible,
    string Status);

/// <summary>
/// Calculates an operation-scoped partial quantity from canonical net usable supply and known hard
/// capacity limits. It is a deterministic what-if calculation; approval and atomic reservation remain external.
/// </summary>
public static class PartialBuildFeasibilityCalculator
{
    public static PartialBuildFeasibility Calculate(PartialBuildInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.OperationMaterials);
        ArgumentNullException.ThrowIfNull(input.CapacityLimits);
        if (input.OrderRemainingUnits < 0 || input.OperationRemainingUnits < 0 || input.MinimumBatchUnits < 0
            || input.OutputPackMultiple <= 0)
            throw new ArgumentOutOfRangeException(nameof(input), "Remaining quantities and minimum batch must be nonnegative; pack multiple must be positive.");
        if (input.OperationMaterials.Any(item => string.IsNullOrWhiteSpace(item.PartNumber) || item.NetUsableQuantity < 0
            || item.RequiredQuantityPerGoodUnit <= 0 || item.FixedSetupQuantity < 0 || item.GoodYieldFactor is <= 0 or > 1))
            throw new ArgumentException("Each operation material needs a part, nonnegative net supply/setup quantity, positive per-unit demand, and a yield factor in (0, 1].", nameof(input));
        if (input.OperationMaterials.GroupBy(item => item.PartNumber, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new ArgumentException("Combine repeated component demand into one operation-level requirement before calculating feasibility.", nameof(input));
        if (input.CapacityLimits.GroupBy(limit => limit.Kind).Any(group => group.Count() > 1)
            || input.CapacityLimits.GroupBy(limit => limit.Name, StringComparer.Ordinal).Any(group => group.Count() > 1)
            || input.CapacityLimits.Any(limit => string.IsNullOrWhiteSpace(limit.Name) || limit.MaximumUnits is < 0))
            throw new ArgumentException("Capacity limits need unique names and nonnegative known quantities.", nameof(input));

        var blockers = new List<string>();
        if (!input.OperationPrerequisitesReady) blockers.Add("Operation prerequisite or routing dependency is not ready.");
        if (!input.RequiredToolingReady) blockers.Add("Required tooling is unavailable.");
        if (!input.MaterialsReleasedAndTraceable) blockers.Add("Material is not physically released, correctly revised, and traceable.");
        var requiredKinds = new[] { PartialBuildCapacityKind.Operation, PartialBuildCapacityKind.QualifiedLabor,
            PartialBuildCapacityKind.Machine, PartialBuildCapacityKind.SafeStorage };
        var missingKinds = requiredKinds.Where(kind => input.CapacityLimits.SingleOrDefault(limit => limit.Kind == kind)
            is not { Required: true, MaximumUnits: not null }).ToArray();
        var missingLimits = input.CapacityLimits.Where(limit => limit.Required && limit.MaximumUnits is null).Select(limit => limit.Name)
            .Concat(missingKinds.Select(kind => kind.ToString())).ToArray();
        if (missingLimits.Length > 0) blockers.Add("Required capacity evidence is unknown: " + string.Join(", ", missingLimits) + ".");

        var componentLimits = input.OperationMaterials.Select(item => new ComponentBuildLimit(item.PartNumber,
            decimal.Floor(Math.Max(0m, item.NetUsableQuantity - item.FixedSetupQuantity) * item.GoodYieldFactor
                / item.RequiredQuantityPerGoodUnit))).OrderBy(item => item.PartNumber, StringComparer.Ordinal).ToArray();
        var materialFeasible = Math.Min(input.OperationRemainingUnits,
            componentLimits.Length == 0 ? input.OperationRemainingUnits : componentLimits.Min(item => item.BuildableGoodUnits));
        var knownLimits = input.CapacityLimits.Where(limit => limit.MaximumUnits is not null).ToArray();
        var capacityFeasible = Math.Min(input.OrderRemainingUnits, input.OperationRemainingUnits);
        if (knownLimits.Length > 0) capacityFeasible = Math.Min(capacityFeasible, knownLimits.Min(limit => limit.MaximumUnits!.Value));
        var rawQuantity = Math.Min(materialFeasible, capacityFeasible);
        var packedQuantity = decimal.Floor(rawQuantity / input.OutputPackMultiple) * input.OutputPackMultiple;

        var hardGateFailed = blockers.Count > 0;
        if (!input.CustomerOrSiteAllowsPartialBuild && packedQuantity < input.OperationRemainingUnits)
            blockers.Add("Customer or site policy prohibits a partial operation release.");
        if (packedQuantity > 0 && packedQuantity < input.MinimumBatchUnits)
            blockers.Add($"Feasible quantity {packedQuantity:0.###} is below the minimum batch of {input.MinimumBatchUnits:0.###}.");
        var recommendation = blockers.Count == 0 ? packedQuantity : 0m;
        var limiting = new List<string>();
        if (materialFeasible == rawQuantity)
        {
            if (componentLimits.Length == 0) limiting.Add("Operation remaining quantity");
            else limiting.AddRange(componentLimits.Where(item => item.BuildableGoodUnits == materialFeasible).Select(item => $"Material {item.PartNumber}"));
        }
        limiting.AddRange(knownLimits.Where(limit => limit.MaximumUnits == rawQuantity).Select(limit => limit.Name));
        if (packedQuantity < rawQuantity) limiting.Add($"Output pack multiple {input.OutputPackMultiple:0.###}");
        if (hardGateFailed) limiting.Add("Readiness evidence");
        if (!input.CustomerOrSiteAllowsPartialBuild && packedQuantity < input.OperationRemainingUnits) limiting.Add("No-partial policy");
        if (packedQuantity > 0 && packedQuantity < input.MinimumBatchUnits) limiting.Add("Minimum batch");

        var status = recommendation > 0
            ? $"A feasible operation-level quantity of {recommendation:0.###} may be proposed for approval; no inventory or work order was changed."
            : blockers.Count > 0
                ? "No partial release recommendation is available until the listed hard constraints are resolved."
                : "No positive quantity satisfies material, pack, minimum-batch, operation, and capacity limits.";
        return new PartialBuildFeasibility(input.OrderRemainingUnits, input.OperationRemainingUnits, materialFeasible,
            capacityFeasible, recommendation, componentLimits, limiting.Distinct(StringComparer.Ordinal).ToArray(),
            blockers, recommendation > 0, status);
    }
}

/// <summary>Approved slot-level capacity expressed in units of one identified part/container profile.</summary>
public sealed record WipStorageSlotCapacity(
    string SlotCode,
    string StorageProfileCode,
    bool ApprovedLocation,
    bool CompatiblePartAndContainer,
    bool GeometryHandlingAndSafetyApproved,
    decimal? ApprovedCapacityUnits,
    decimal? PhysicallyOccupiedUnits,
    decimal? CommittedReservedUnits,
    decimal? BlockedOrQuarantineCapacityUnits,
    bool QuarantineAlreadyCountedInOccupied,
    decimal? MaximumOperationalUtilization);

public sealed record WipSlotAllocation(string SlotCode, decimal Quantity);

public sealed record WipStorageCapacityResult(
    string StorageProfileCode,
    decimal ApprovedCapacityUnits,
    decimal PhysicallyFreeUnits,
    decimal OperationalFreeUnits,
    decimal RequestedQuantity,
    decimal AllocatableQuantity,
    IReadOnlyList<WipSlotAllocation> ProposedSlotAllocations,
    int ExcludedUnknownSlotCount,
    IReadOnlyList<string> Blockers,
    string Status);

/// <summary>Applies section 18's capacity equations independently to each approved physical slot.</summary>
public static class WipStorageCapacityCalculator
{
    public static WipStorageCapacityResult Calculate(string storageProfileCode,
        IReadOnlyCollection<WipStorageSlotCapacity> slots, decimal requestedQuantity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageProfileCode);
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentOutOfRangeException.ThrowIfNegative(requestedQuantity);
        var relevant = slots.Where(slot => StringComparer.Ordinal.Equals(slot.StorageProfileCode, storageProfileCode)
            && slot.ApprovedLocation && slot.CompatiblePartAndContainer).ToArray();
        if (relevant.GroupBy(slot => slot.SlotCode, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new ArgumentException("A physical slot may be counted once per storage profile.", nameof(slots));

        var unknown = 0;
        var blockers = new List<string>();
        var freeBySlot = new List<(string Slot, decimal Free)>();
        decimal capacity = 0;
        decimal physicalFreeTotal = 0;
        foreach (var slot in relevant)
        {
            if (!slot.GeometryHandlingAndSafetyApproved || slot.ApprovedCapacityUnits is not { } approvedCapacity
                || slot.PhysicallyOccupiedUnits is not { } occupied || slot.CommittedReservedUnits is not { } reserved
                || (!slot.QuarantineAlreadyCountedInOccupied && slot.BlockedOrQuarantineCapacityUnits is null)
                || slot.BlockedOrQuarantineCapacityUnits is < 0 || slot.MaximumOperationalUtilization is < 0 or > 1)
            {
                unknown++;
                blockers.Add($"Slot {slot.SlotCode} has incomplete approved geometry, handling, safety, occupancy, reservation, or utilization evidence and is unavailable pending authorized review.");
                continue;
            }
            if (approvedCapacity < 0 || occupied < 0 || reserved < 0)
                throw new ArgumentException($"Slot {slot.SlotCode} has a negative capacity, occupancy, or reservation.", nameof(slots));

            var blockedSeparate = slot.QuarantineAlreadyCountedInOccupied ? 0m : slot.BlockedOrQuarantineCapacityUnits!.Value;
            var physicalFree = Math.Max(0m, approvedCapacity - occupied - reserved - blockedSeparate);
            var utilization = slot.MaximumOperationalUtilization ?? 1m;
            var operationalLimit = decimal.Floor(approvedCapacity * utilization);
            var operationalFree = Math.Max(0m, Math.Min(physicalFree, operationalLimit - occupied - reserved));
            capacity += approvedCapacity;
            physicalFreeTotal += physicalFree;
            freeBySlot.Add((slot.SlotCode, operationalFree));
        }

        var remaining = requestedQuantity;
        var allocations = new List<WipSlotAllocation>();
        foreach (var slot in freeBySlot.OrderBy(item => item.Slot, StringComparer.Ordinal))
        {
            if (remaining <= 0) break;
            var quantity = Math.Min(remaining, slot.Free);
            if (quantity <= 0) continue;
            allocations.Add(new WipSlotAllocation(slot.Slot, quantity));
            remaining -= quantity;
        }
        var operationalFreeTotal = freeBySlot.Sum(slot => slot.Free);
        var allocatable = allocations.Sum(item => item.Quantity);
        var status = unknown > 0
            ? $"{allocatable:0.###} units fit known approved slots; {unknown} compatible slot(s) are excluded pending authorized review."
            : $"{allocatable:0.###} units fit the approved operational limit; allocations are a preview and require an atomic reservation at approval.";
        return new WipStorageCapacityResult(storageProfileCode, capacity, physicalFreeTotal, operationalFreeTotal,
            requestedQuantity, allocatable, allocations, unknown, blockers, status);
    }
}

public interface IWorkTimeCalendar
{
    DateTimeOffset AddWorkingTime(DateTimeOffset start, TimeSpan workingDuration);
}

public sealed record MaterialUsableDateInput(
    string PartNumber,
    decimal RequiredQuantity,
    decimal? CoveredQuantityAtEarliestArrival,
    decimal? CoveredQuantityAtExpectedArrival,
    decimal? CoveredQuantityAtRiskAdjustedArrival,
    DateTimeOffset? SupplierPromisedArrival,
    DateTimeOffset? EarliestPlausiblePhysicalArrival,
    DateTimeOffset? ExpectedPhysicalArrival,
    DateTimeOffset? RiskAdjustedPhysicalArrival,
    TimeSpan? ReceivingLeadTime,
    TimeSpan? InspectionLeadTime,
    TimeSpan? PutAwayLeadTime,
    string ArrivalConfidence,
    string Source);

public sealed record MaterialUsableDateForecast(
    string PartNumber,
    DateTimeOffset? SupplierPromisedArrival,
    DateTimeOffset? EarliestPlausibleUsableAt,
    DateTimeOffset? ExpectedUsableAt,
    DateTimeOffset? RiskAdjustedUsableAt,
    string ArrivalConfidence,
    string Source,
    string Status);

/// <summary>Applies source-backed receiving/inspection/put-away lead time using the site's approved working calendar.</summary>
public enum MaterialDateScenario { EarliestPlausible, Expected, RiskAdjusted }

public static class MaterialUsableDateCalculator
{
    public static MaterialUsableDateForecast Calculate(MaterialUsableDateInput input, IWorkTimeCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.PartNumber);
        if (input.RequiredQuantity <= 0 || input.CoveredQuantityAtEarliestArrival is < 0
            || input.CoveredQuantityAtExpectedArrival is < 0 || input.CoveredQuantityAtRiskAdjustedArrival is < 0)
            throw new ArgumentOutOfRangeException(nameof(input), "Required and covered material quantities must be positive or nonnegative as appropriate.");
        var missing = new[] { input.ReceivingLeadTime, input.InspectionLeadTime, input.PutAwayLeadTime }.Any(value => value is null);
        var negative = new[] { input.ReceivingLeadTime, input.InspectionLeadTime, input.PutAwayLeadTime }
            .Any(value => value is { } duration && duration < TimeSpan.Zero);
        if (negative) throw new ArgumentException("Receiving, inspection, and put-away durations cannot be negative.", nameof(input));
        DateTimeOffset? expected = missing || input.ExpectedPhysicalArrival is null
                || input.CoveredQuantityAtExpectedArrival is not { } expectedCoverage || expectedCoverage < input.RequiredQuantity
            ? null
            : AddLeadTimes(calendar, input.ExpectedPhysicalArrival.Value, input.ReceivingLeadTime!.Value,
                input.InspectionLeadTime!.Value, input.PutAwayLeadTime!.Value);
        DateTimeOffset? earliest = missing || input.EarliestPlausiblePhysicalArrival is null
                || input.CoveredQuantityAtEarliestArrival is not { } earliestCoverage || earliestCoverage < input.RequiredQuantity
            ? null
            : AddLeadTimes(calendar, input.EarliestPlausiblePhysicalArrival.Value, input.ReceivingLeadTime!.Value,
                input.InspectionLeadTime!.Value, input.PutAwayLeadTime!.Value);
        DateTimeOffset? riskAdjusted = missing || input.RiskAdjustedPhysicalArrival is null
                || input.CoveredQuantityAtRiskAdjustedArrival is not { } riskCoverage || riskCoverage < input.RequiredQuantity
            ? null
            : AddLeadTimes(calendar, input.RiskAdjustedPhysicalArrival.Value, input.ReceivingLeadTime!.Value,
                input.InspectionLeadTime!.Value, input.PutAwayLeadTime!.Value);
        var status = expected is null
            ? "Usable date unknown: source-backed arrival, enough pegged quantity, or approved lead-time evidence is missing."
            : "Forecast usable date includes the approved receiving, inspection, and put-away calendar; it is not a confirmed receipt or quality release.";
        return new MaterialUsableDateForecast(input.PartNumber, input.SupplierPromisedArrival, earliest, expected,
            riskAdjusted, input.ArrivalConfidence, input.Source, status);
    }

    public static DateTimeOffset? EarliestOperationRestart(IReadOnlyCollection<MaterialUsableDateForecast> criticalMaterials,
        MaterialDateScenario scenario, DateTimeOffset asOf, TimeSpan stagingLeadTime, TimeSpan setupLeadTime, IWorkTimeCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(criticalMaterials);
        ArgumentNullException.ThrowIfNull(calendar);
        if (stagingLeadTime < TimeSpan.Zero) throw new ArgumentException("Staging lead time cannot be negative.", nameof(stagingLeadTime));
        if (setupLeadTime < TimeSpan.Zero) throw new ArgumentException("Setup lead time cannot be negative.", nameof(setupLeadTime));
        DateTimeOffset? ReadyAt(MaterialUsableDateForecast material) => scenario switch
        {
            MaterialDateScenario.EarliestPlausible => material.EarliestPlausibleUsableAt,
            MaterialDateScenario.Expected => material.ExpectedUsableAt,
            MaterialDateScenario.RiskAdjusted => material.RiskAdjustedUsableAt,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        if (criticalMaterials.Any(material => ReadyAt(material) is null)) return null;
        var ready = criticalMaterials.Count == 0 ? asOf : criticalMaterials.Max(material => ReadyAt(material)!.Value);
        return calendar.AddWorkingTime(calendar.AddWorkingTime(ready, stagingLeadTime), setupLeadTime);
    }

    private static DateTimeOffset AddLeadTimes(IWorkTimeCalendar calendar, DateTimeOffset arrival,
        TimeSpan receiving, TimeSpan inspection, TimeSpan putAway) =>
        calendar.AddWorkingTime(calendar.AddWorkingTime(calendar.AddWorkingTime(arrival, receiving), inspection), putAway);
}
