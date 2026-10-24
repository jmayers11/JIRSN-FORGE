using Forge.Execution.Contracts;

namespace Forge.Planning;

public enum CapacityEstimateEvidence { None, VerifiedContextualHistory, CalibratedRoleBenchmark }
public enum CapacityEstimateConfidence { Low, Medium, High }

/// <summary>
/// Inputs are already-calibrated, operation/context-specific rates. This calculator applies the
/// approved estimate to available minutes; it does not invent a universal skill-decay penalty.
/// </summary>
public sealed record SkillCapacityForecastInput(
    bool HasCurrentQualification,
    bool RequiredRefresherSatisfied,
    bool HasRequiredMachineAuthorization,
    bool SafetyRequirementsSatisfied,
    bool TwoPersonRuleSatisfied,
    int ScheduledEligibleMinutes,
    int BreakMinutes,
    int PlannedNonProductionMinutes,
    CapacityEstimateEvidence Evidence,
    string EstimateBasis,
    int VerifiedObservationCount,
    DateTimeOffset AsOf,
    DateTimeOffset? LastVerifiedProductiveExecutionAt,
    decimal? ConservativeSetupMinutes,
    decimal? ExpectedSetupMinutes,
    decimal? OptimisticSetupMinutes,
    decimal? ConservativeRunMinutesPerGoodUnit,
    decimal? ExpectedRunMinutesPerGoodUnit,
    decimal? OptimisticRunMinutesPerGoodUnit,
    CapacityEstimateConfidence Confidence);

public sealed record SkillCapacityForecast(
    bool IsEligible,
    string EligibilityStatus,
    int ProductiveMinutes,
    decimal? ConservativeGoodUnits,
    decimal? ExpectedGoodUnits,
    decimal? OptimisticGoodUnits,
    int? DaysSinceLastVerifiedProductiveExecution,
    CapacityEstimateConfidence? Confidence,
    CapacityEstimateEvidence Evidence,
    int VerifiedObservationCount,
    string EstimateBasis,
    string Status);

/// <summary>Hard-gates staffing before applying transparent setup/run estimates to productive minutes.</summary>
public static class SkillCapacityForecastCalculator
{
    public static SkillCapacityForecast Calculate(SkillCapacityForecastInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.ScheduledEligibleMinutes < 0 || input.BreakMinutes < 0 || input.PlannedNonProductionMinutes < 0)
            throw new ArgumentOutOfRangeException(nameof(input), "Scheduled time, breaks, and planned nonproduction time cannot be negative.");
        if (input.VerifiedObservationCount < 0) throw new ArgumentOutOfRangeException(nameof(input), "Verified observation count cannot be negative.");
        var productive = Math.Max(0, input.ScheduledEligibleMinutes - input.BreakMinutes - input.PlannedNonProductionMinutes);
        int? daysSince = input.LastVerifiedProductiveExecutionAt is { } last && last <= input.AsOf
            ? (int)Math.Floor((input.AsOf - last).TotalDays)
            : null;

        var eligibilityFailure = !input.HasCurrentQualification ? "Current operation-specific qualification is missing or expired."
            : !input.RequiredRefresherSatisfied ? "A required refresher or supervised reacclimation is incomplete."
            : !input.HasRequiredMachineAuthorization ? "Required machine-specific authorization is missing."
            : !input.SafetyRequirementsSatisfied ? "Required safety authorization is missing."
            : !input.TwoPersonRuleSatisfied ? "The required two-person staffing rule is not met."
            : null;
        if (eligibilityFailure is not null)
            return new SkillCapacityForecast(false, eligibilityFailure, productive, null, null, null, daysSince,
                null, input.Evidence, input.VerifiedObservationCount, input.EstimateBasis,
                "Ineligible: qualification, refresher, authorization, safety, or minimum-staffing constraints are hard stops.");

        if (input.Evidence == CapacityEstimateEvidence.None)
            return new SkillCapacityForecast(true, "Eligible for assignment; no capacity estimate is available.", productive,
                null, null, null, daysSince, null, input.Evidence, input.VerifiedObservationCount, input.EstimateBasis,
                "N/A: no verified contextual history or calibrated role benchmark is available.");

        if (string.IsNullOrWhiteSpace(input.EstimateBasis))
            throw new ArgumentException("A capacity estimate must identify its evidence and calibration basis.", nameof(input));
        if (input.Evidence == CapacityEstimateEvidence.VerifiedContextualHistory && input.VerifiedObservationCount == 0)
            throw new ArgumentException("Verified contextual history requires at least one verified observation.", nameof(input));
        var setup = new[] { input.ConservativeSetupMinutes, input.ExpectedSetupMinutes, input.OptimisticSetupMinutes };
        var run = new[] { input.ConservativeRunMinutesPerGoodUnit, input.ExpectedRunMinutesPerGoodUnit, input.OptimisticRunMinutesPerGoodUnit };
        if (setup.Any(value => value is null or < 0) || run.Any(value => value is null or <= 0))
            throw new ArgumentException("Capacity estimates require nonnegative setup estimates and positive run time per good unit.", nameof(input));
        if (setup[0] < setup[1] || setup[1] < setup[2] || run[0] < run[1] || run[1] < run[2])
            throw new ArgumentException("Conservative, expected, and optimistic estimates must be ordered from slower to faster.", nameof(input));

        var confidence = input.Evidence == CapacityEstimateEvidence.CalibratedRoleBenchmark
            ? CapacityEstimateConfidence.Low
            : input.Confidence;
        decimal Units(decimal setupMinutes, decimal runMinutes) => Math.Max(0m,
            decimal.Floor((productive - setupMinutes) / runMinutes));
        var conservative = Units(input.ConservativeSetupMinutes!.Value, input.ConservativeRunMinutesPerGoodUnit!.Value);
        var expected = Units(input.ExpectedSetupMinutes!.Value, input.ExpectedRunMinutesPerGoodUnit!.Value);
        var optimistic = Units(input.OptimisticSetupMinutes!.Value, input.OptimisticRunMinutesPerGoodUnit!.Value);
        var status = input.Evidence == CapacityEstimateEvidence.CalibratedRoleBenchmark
            ? "Planning estimate uses a conservative calibrated role benchmark; employee-specific history is insufficient."
            : "Planning estimate uses verified, contextual operation history and is not an employee performance grade.";
        return new SkillCapacityForecast(true, "Eligible under the supplied qualification and safety checks.", productive,
            conservative, expected, optimistic, daysSince, confidence, input.Evidence, input.VerifiedObservationCount,
            input.EstimateBasis, status);
    }
}

public sealed record CoverageGoalAssignment(
    Guid EmployeeId,
    string AssignmentId,
    string OperationCode,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    bool SupervisorApproved,
    bool HasCurrentQualification,
    bool SafetyRequirementsSatisfied,
    bool TwoPersonRuleSatisfied,
    bool Cancelled,
    bool Started,
    bool Completed,
    bool HasValidOutputData,
    bool Comparable,
    decimal? ApprovedGoalGoodUnits,
    decimal? AcceptedGoodUnits,
    decimal? ForecastGoodUnits,
    string? ExclusionReason = null);

public sealed record CoverageGoalExclusion(string AssignmentId, string Reason);

public sealed record CrossFunctionalCoverageMetrics(
    int CompletedAssignmentCount,
    decimal VerifiedCoverageHours,
    int DistinctQualifiedOperationsCovered,
    int GoalEligibleAssignmentCount,
    int GoalSuccessCount,
    decimal? CoverageGoalSuccessRatePercent,
    decimal? AverageCoverageGoalAttainmentPercent,
    decimal? MedianCoverageGoalAttainmentPercent,
    int ForecastComparisonCount,
    decimal? MeanAbsoluteForecastErrorGoodUnits,
    decimal? MeanForecastBiasGoodUnits,
    int ExcludedGoalAssignmentCount,
    IReadOnlyList<CoverageGoalExclusion> GoalExclusions,
    string Status);

/// <summary>Calculates approved cross-functional coverage evidence without combining unlike unit goals.</summary>
public static class CrossFunctionalCoverageCalculator
{
    public static CrossFunctionalCoverageMetrics Calculate(Guid employeeId,
        IReadOnlyCollection<CoverageGoalAssignment> assignments, int minimumEligibleDurationMinutes)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumEligibleDurationMinutes);
        var selected = assignments.Where(item => item.EmployeeId == employeeId).ToArray();
        if (selected.GroupBy(item => item.AssignmentId, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new ArgumentException("Coverage assignment identifiers must be unique per employee.", nameof(assignments));
        if (selected.Any(item => string.IsNullOrWhiteSpace(item.AssignmentId) || string.IsNullOrWhiteSpace(item.OperationCode)))
            throw new ArgumentException("Coverage assignments require an identifier and operation code.", nameof(assignments));

        var validIntervals = selected.Where(IsCoverageCompleted).Select(Interval).Where(interval => interval is not null)
            .Select(interval => interval!).ToArray();
        var coverageMinutes = PerformanceIntervalMath.UnionMinutes(validIntervals);
        var completed = selected.Where(IsCoverageCompleted).ToArray();
        var qualifiedOperations = completed.Select(item => item.OperationCode).Distinct(StringComparer.Ordinal).Count();

        var eligible = new List<CoverageGoalAssignment>();
        var exclusions = new List<CoverageGoalExclusion>();
        foreach (var assignment in selected)
        {
            var reason = GoalExclusionReason(assignment, minimumEligibleDurationMinutes);
            if (reason is null) eligible.Add(assignment);
            else if (assignment.ApprovedGoalGoodUnits is > 0 || assignment.Completed || assignment.Started)
                exclusions.Add(new CoverageGoalExclusion(assignment.AssignmentId, assignment.ExclusionReason is { Length: > 0 } supplied ? supplied : reason));
        }

        var successes = eligible.Count(item => item.AcceptedGoodUnits >= item.ApprovedGoalGoodUnits);
        var attainment = eligible.Select(item => 100m * item.AcceptedGoodUnits!.Value / item.ApprovedGoalGoodUnits!.Value)
            .Order().ToArray();
        decimal? successRate = eligible.Count > 0
            ? decimal.Round(100m * successes / eligible.Count, 2, MidpointRounding.AwayFromZero)
            : null;
        decimal? average = attainment.Length > 0
            ? decimal.Round(attainment.Average(), 2, MidpointRounding.AwayFromZero)
            : null;
        decimal? median = attainment.Length switch
        {
            0 => null,
            var count when count % 2 == 1 => decimal.Round(attainment[count / 2], 2, MidpointRounding.AwayFromZero),
            var count => decimal.Round((attainment[count / 2 - 1] + attainment[count / 2]) / 2m, 2, MidpointRounding.AwayFromZero),
        };

        var forecastAssignments = selected.Where(item => IsCoverageCompleted(item)
            && item.HasValidOutputData && item.AcceptedGoodUnits is >= 0 && item.ForecastGoodUnits is >= 0).ToArray();
        var forecastErrors = forecastAssignments.Select(item => item.ForecastGoodUnits!.Value - item.AcceptedGoodUnits!.Value).ToArray();
        decimal? meanAbsoluteError = forecastErrors.Length > 0
            ? decimal.Round(forecastErrors.Average(error => Math.Abs(error)), 2, MidpointRounding.AwayFromZero)
            : null;
        decimal? meanBias = forecastErrors.Length > 0
            ? decimal.Round(forecastErrors.Average(), 2, MidpointRounding.AwayFromZero)
            : null;
        var status = eligible.Count == 0
            ? "N/A: no completed, approved, comparable coverage assignments have a valid positive goal and accepted-output evidence."
            : "Coverage goal results use accepted good output and per-assignment goals; forecast error is reported separately and is not an employee score.";

        return new CrossFunctionalCoverageMetrics(completed.Length,
            decimal.Round(coverageMinutes / 60m, 2, MidpointRounding.AwayFromZero), qualifiedOperations,
            eligible.Count, successes, successRate, average, median, forecastErrors.Length, meanAbsoluteError, meanBias,
            exclusions.Count, exclusions, status);
    }

    private static bool IsCoverageCompleted(CoverageGoalAssignment item) => item.SupervisorApproved && item.HasCurrentQualification
        && item.SafetyRequirementsSatisfied && item.TwoPersonRuleSatisfied && !item.Cancelled && item.Started && item.Completed
        && Interval(item) is not null;

    private static string? GoalExclusionReason(CoverageGoalAssignment item, int minimumDurationMinutes)
    {
        if (item.Cancelled) return "Cancelled assignment.";
        if (!item.Started) return "Assignment did not start.";
        if (!item.Completed) return "Assignment is not complete.";
        if (!item.SupervisorApproved) return "Assignment was not supervisor-approved.";
        if (!item.HasCurrentQualification) return "Required qualification was not current.";
        if (!item.SafetyRequirementsSatisfied || !item.TwoPersonRuleSatisfied) return "Required safety or minimum-staffing rule was not met.";
        if (!item.HasValidOutputData || item.AcceptedGoodUnits is null or < 0) return "Accepted good-output evidence is incomplete or invalid.";
        if (Interval(item) is not { } interval) return "Verified assignment start/end times are unavailable.";
        if (interval.Minutes < minimumDurationMinutes) return "Assignment duration is below the configured minimum.";
        if (!item.Comparable) return item.ExclusionReason is { Length: > 0 } reason ? reason : "Assignment is not comparable after reviewed disruptions or scope changes.";
        if (item.ApprovedGoalGoodUnits is not > 0) return "No valid positive supervisor-approved goal was recorded.";
        return null;
    }

    private static PerformanceInterval? Interval(CoverageGoalAssignment item) => item.StartedAt is { } start && item.EndedAt is { } end && end > start
        ? new PerformanceInterval(start, end)
        : null;

}
