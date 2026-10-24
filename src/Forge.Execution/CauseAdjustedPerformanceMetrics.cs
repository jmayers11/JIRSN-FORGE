namespace Forge.Execution;

/// <summary>A half-open interval used for paid-time and machine-impact calculations.</summary>
public sealed record PerformanceInterval(DateTimeOffset StartsAt, DateTimeOffset EndsAt)
{
    public decimal Minutes
    {
        get
        {
            if (EndsAt <= StartsAt) throw new ArgumentException("A performance interval must end after it starts.");
            return (decimal)(EndsAt - StartsAt).Ticks / TimeSpan.TicksPerMinute;
        }
    }
}

public sealed record PaidTimeActivityInterval(
    Guid EmployeeId,
    string ShiftCode,
    PerformanceInterval Interval,
    string ActivityCode,
    bool IsPaid = true);

public sealed record PaidTimeReconciliation(
    Guid EmployeeId,
    string ShiftCode,
    decimal ExpectedPaidMinutes,
    decimal AccountedPaidMinutes,
    decimal RawIntervalMinutes,
    decimal OverlapMinutes,
    decimal GapMinutes,
    decimal OveraccountedMinutes,
    bool IsComplete,
    IReadOnlyList<string> DataQualityFlags);

public static class PerformanceIntervalMath
{
    public static decimal UnionMinutes(IEnumerable<PerformanceInterval> intervals)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        var ordered = intervals.ToArray();
        foreach (var interval in ordered) _ = interval.Minutes;
        ordered = ordered.OrderBy(interval => interval.StartsAt).ThenBy(interval => interval.EndsAt).ToArray();
        if (ordered.Length == 0) return 0;
        var start = ordered[0].StartsAt;
        var end = ordered[0].EndsAt;
        var ticks = 0L;
        foreach (var interval in ordered.Skip(1))
        {
            if (interval.StartsAt <= end)
            {
                if (interval.EndsAt > end) end = interval.EndsAt;
            }
            else
            {
                ticks += (end - start).Ticks;
                start = interval.StartsAt;
                end = interval.EndsAt;
            }
        }
        ticks += (end - start).Ticks;
        return (decimal)ticks / TimeSpan.TicksPerMinute;
    }

    public static PerformanceInterval? Intersect(PerformanceInterval left, PerformanceInterval right)
    {
        _ = left.Minutes;
        _ = right.Minutes;
        var start = left.StartsAt > right.StartsAt ? left.StartsAt : right.StartsAt;
        var end = left.EndsAt < right.EndsAt ? left.EndsAt : right.EndsAt;
        return end > start ? new PerformanceInterval(start, end) : null;
    }
}

/// <summary>Reconciles paid activities to one employee's actual paid shift time without double-counting overlaps.</summary>
public static class PaidTimeReconciliationCalculator
{
    public static PaidTimeReconciliation Calculate(Guid employeeId, string shiftCode, PerformanceInterval paidShift,
        decimal expectedPaidMinutes, IReadOnlyCollection<PaidTimeActivityInterval> activities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shiftCode);
        ArgumentNullException.ThrowIfNull(paidShift);
        ArgumentNullException.ThrowIfNull(activities);
        if (expectedPaidMinutes < 0 || expectedPaidMinutes > paidShift.Minutes)
            throw new ArgumentOutOfRangeException(nameof(expectedPaidMinutes), "Expected paid minutes must fit within the paid shift window.");

        var selected = activities.Where(activity => activity.EmployeeId == employeeId
            && StringComparer.Ordinal.Equals(activity.ShiftCode, shiftCode) && activity.IsPaid).ToArray();
        var clipped = selected.Select(activity => PerformanceIntervalMath.Intersect(activity.Interval, paidShift)).Where(interval => interval is not null)
            .Select(interval => interval!).ToArray();
        var rawMinutes = clipped.Sum(interval => interval.Minutes);
        var accountedMinutes = PerformanceIntervalMath.UnionMinutes(clipped);
        var overlap = Math.Max(0m, rawMinutes - accountedMinutes);
        var gap = Math.Max(0m, expectedPaidMinutes - accountedMinutes);
        var overaccounted = Math.Max(0m, accountedMinutes - expectedPaidMinutes);
        var flags = new List<string>();
        if (overlap > 0) flags.Add("Paid activity intervals overlap; overlapping time was counted once.");
        if (gap > 0) flags.Add("Paid shift time contains unaccounted minutes.");
        if (overaccounted > 0) flags.Add("Accounted activity exceeds expected paid shift minutes.");
        if (selected.Any(activity => activity.Interval.StartsAt < paidShift.StartsAt || activity.Interval.EndsAt > paidShift.EndsAt))
            flags.Add("Activity intervals extend beyond the paid shift and were clipped to the shift window.");

        return new PaidTimeReconciliation(employeeId, shiftCode, expectedPaidMinutes, accountedMinutes,
            rawMinutes, overlap, gap, overaccounted,
            overlap == 0 && gap == 0 && overaccounted == 0, flags);
    }

}

public sealed record MachineDowntimeImpact(
    decimal FullMachineEventMinutes,
    decimal AssignedEmployeeOverlapMinutes,
    decimal AlreadyClassifiedMinutes,
    decimal EmployeeMachineImpactMinutes);

/// <summary>Attributes only the employee/assignment/paid-shift overlap with a machine event.</summary>
public static class MachineDowntimeImpactCalculator
{
    public static MachineDowntimeImpact Calculate(PerformanceInterval machineEvent, PerformanceInterval employeeAssignment,
        PerformanceInterval paidShift, IReadOnlyCollection<PerformanceInterval> alreadyClassifiedIntervals)
    {
        ArgumentNullException.ThrowIfNull(machineEvent);
        ArgumentNullException.ThrowIfNull(employeeAssignment);
        ArgumentNullException.ThrowIfNull(paidShift);
        ArgumentNullException.ThrowIfNull(alreadyClassifiedIntervals);

        var eventAndAssignment = PerformanceIntervalMath.Intersect(machineEvent, employeeAssignment);
        var impactWindow = eventAndAssignment is null
            ? null
            : PerformanceIntervalMath.Intersect(eventAndAssignment, paidShift);
        var assignedOverlap = impactWindow?.Minutes ?? 0m;
        var classified = impactWindow is null
            ? 0m
            : PerformanceIntervalMath.UnionMinutes(alreadyClassifiedIntervals
                .Select(interval => PerformanceIntervalMath.Intersect(interval, impactWindow))
                .Where(interval => interval is not null).Select(interval => interval!));
        return new MachineDowntimeImpact(machineEvent.Minutes, assignedOverlap, classified,
            Math.Max(0m, assignedOverlap - classified));
    }
}

public sealed record ControllableLaborEfficiency(
    decimal TotalDirectLaborHours,
    decimal EligibleDirectLaborHours,
    decimal VerifiedExternalDelayHours,
    decimal? UnadjustedEfficiencyPercent,
    decimal? ControllableEfficiencyPercent,
    string Status);

/// <summary>Calculates adjusted and unadjusted efficiency only from accepted standard hours and explicit labor denominators.</summary>
public static class ControllableLaborEfficiencyCalculator
{
    public static ControllableLaborEfficiency Calculate(decimal? earnedStandardHours, decimal totalDirectLaborHours,
        decimal eligibleDirectLaborHours, decimal verifiedExternalDelayHours)
    {
        if (totalDirectLaborHours < 0 || eligibleDirectLaborHours < 0 || verifiedExternalDelayHours < 0)
            throw new ArgumentOutOfRangeException(nameof(totalDirectLaborHours), "Labor hours cannot be negative.");
        if (eligibleDirectLaborHours > totalDirectLaborHours)
            throw new ArgumentException("Eligible direct labor cannot exceed total direct labor hours.", nameof(eligibleDirectLaborHours));
        if (verifiedExternalDelayHours > totalDirectLaborHours)
            throw new ArgumentException("Verified external delay cannot exceed total direct labor hours.", nameof(verifiedExternalDelayHours));
        if (earnedStandardHours is < 0)
            throw new ArgumentOutOfRangeException(nameof(earnedStandardHours), "Earned standard hours cannot be negative.");

        decimal? unadjusted = earnedStandardHours is { } earned && totalDirectLaborHours > 0
            ? decimal.Round(100m * earned / totalDirectLaborHours, 2, MidpointRounding.AwayFromZero)
            : null;
        decimal? controllable = earnedStandardHours is { } standard && eligibleDirectLaborHours > 0
            ? decimal.Round(100m * standard / eligibleDirectLaborHours, 2, MidpointRounding.AwayFromZero)
            : null;
        var status = earnedStandardHours is null
            ? "N/A: approved accepted-output labor standard is unavailable."
            : eligibleDirectLaborHours <= 0
                ? "N/A: no eligible direct-labor hours are available."
                : "Calculated from accepted-output standard hours; verified external delays remain visible but are excluded from controllable hours.";
        return new ControllableLaborEfficiency(totalDirectLaborHours, eligibleDirectLaborHours, verifiedExternalDelayHours,
            unadjusted, controllable, status);
    }
}

public enum FirstPassCause { EmployeeControlled, Machine, IncomingMaterial, UpstreamProcess, Unknown, Mixed }

/// <summary>One uniquely identified first-pass inspection cohort; quantity must use one consistent unit of measure.</summary>
public sealed record FirstPassInspectionResult(
    string UnitOrLotKey,
    decimal Quantity,
    bool PassedFirstPass,
    FirstPassCause Cause,
    bool CauseVerified,
    bool MeaningfulEmployeeControlledOpportunity);

public sealed record FirstPassYieldMetrics(
    decimal InspectedQuantity,
    decimal FirstPassGoodQuantity,
    decimal? OfficialProcessFpyPercent,
    decimal EmployeeEligibleQuantity,
    decimal EmployeeEligibleGoodQuantity,
    decimal? EmployeeAttributableFpyPercent,
    decimal EmployeeAttributionCoveragePercent,
    decimal MinimumRequiredAttributionCoveragePercent,
    decimal ExternallyCausedQuantity,
    decimal UnassignedQuantity,
    IReadOnlyDictionary<FirstPassCause, decimal> VerifiedExternalCauseQuantities,
    string EmployeeMetricStatus);

/// <summary>Preserves official process FPY and calculates a separate evidence-gated employee-attributable diagnostic.</summary>
public static class FirstPassYieldCalculator
{
    private static readonly FirstPassCause[] ExternalCauses = [FirstPassCause.Machine, FirstPassCause.IncomingMaterial, FirstPassCause.UpstreamProcess];

    public static FirstPassYieldMetrics Calculate(IReadOnlyCollection<FirstPassInspectionResult> inspections,
        decimal minimumRequiredAttributionCoveragePercent)
    {
        ArgumentNullException.ThrowIfNull(inspections);
        if (minimumRequiredAttributionCoveragePercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(minimumRequiredAttributionCoveragePercent));
        if (inspections.Any(item => string.IsNullOrWhiteSpace(item.UnitOrLotKey) || item.Quantity <= 0))
            throw new ArgumentException("Each first-pass inspection needs a unique key and a positive quantity.", nameof(inspections));
        if (inspections.GroupBy(item => item.UnitOrLotKey, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new ArgumentException("First-pass inspection cohorts must be unique to prevent duplicate quantity counting.", nameof(inspections));

        var inspected = inspections.Sum(item => item.Quantity);
        var good = inspections.Where(item => item.PassedFirstPass).Sum(item => item.Quantity);
        decimal? official = inspected > 0 ? decimal.Round(100m * good / inspected, 2, MidpointRounding.AwayFromZero) : null;
        var employeeEligible = inspections.Where(IsEmployeeEligible).ToArray();
        var employeeEligibleQuantity = employeeEligible.Sum(item => item.Quantity);
        var employeeGood = employeeEligible.Where(item => item.PassedFirstPass).Sum(item => item.Quantity);
        var coverage = inspected > 0 ? decimal.Round(100m * employeeEligibleQuantity / inspected, 2, MidpointRounding.AwayFromZero) : 0m;
        decimal? attributable = employeeEligibleQuantity > 0 && coverage >= minimumRequiredAttributionCoveragePercent
            ? decimal.Round(100m * employeeGood / employeeEligibleQuantity, 2, MidpointRounding.AwayFromZero)
            : null;
        var external = inspections.Where(item => item.CauseVerified && ExternalCauses.Contains(item.Cause)).ToArray();
        var externalByCause = ExternalCauses.ToDictionary(cause => cause,
            cause => external.Where(item => item.Cause == cause).Sum(item => item.Quantity));
        var unassigned = inspections.Where(item => !IsEmployeeEligible(item)
            && !(item.CauseVerified && ExternalCauses.Contains(item.Cause))).Sum(item => item.Quantity);
        var status = inspected == 0
            ? "N/A: no first-pass inspection quantity is recorded."
            : employeeEligibleQuantity == 0
                ? "N/A: no inspected units have verified employee-controlled attribution."
                : coverage < minimumRequiredAttributionCoveragePercent
                    ? $"N/A: attribution coverage {coverage:0.##}% is below the configured {minimumRequiredAttributionCoveragePercent:0.##}% threshold."
                    : "Employee-attributable FPY uses only verified units with a meaningful employee-controlled opportunity; external and unknown causes are reported separately.";
        return new FirstPassYieldMetrics(inspected, good, official, employeeEligibleQuantity, employeeGood, attributable,
            coverage, minimumRequiredAttributionCoveragePercent, external.Sum(item => item.Quantity), unassigned,
            externalByCause, status);
    }

    private static bool IsEmployeeEligible(FirstPassInspectionResult item) => item.CauseVerified
        && item.Cause == FirstPassCause.EmployeeControlled && item.MeaningfulEmployeeControlledOpportunity;
}
