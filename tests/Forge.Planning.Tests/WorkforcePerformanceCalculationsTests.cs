using Forge.Execution;
using Forge.Execution.Contracts;

namespace Forge.Planning.Tests;

public sealed class WorkforcePerformanceCalculationsTests
{
    [Fact]
    public void Paid_time_reconciliation_counts_each_minute_once_and_flags_gaps_and_overlaps()
    {
        var employee = Guid.NewGuid();
        var shift = new PerformanceInterval(TestFactory.Now, TestFactory.Now.AddHours(10));
        var activities = new[]
        {
            Paid(employee, "PROD", TestFactory.Now, TestFactory.Now.AddHours(6.5)),
            Paid(employee, "MACHINE", TestFactory.Now.AddHours(6.5), TestFactory.Now.AddHours(8.5)),
            Paid(employee, "MATERIAL", TestFactory.Now.AddHours(8.5), TestFactory.Now.AddHours(9.5)),
            Paid(employee, "SETUP", TestFactory.Now.AddHours(9.5), TestFactory.Now.AddHours(10)),
        };

        var reconciled = PaidTimeReconciliationCalculator.Calculate(employee, "DAY", shift, 600m, activities);

        Assert.Equal(600m, reconciled.AccountedPaidMinutes);
        Assert.Equal(0m, reconciled.OverlapMinutes);
        Assert.Equal(0m, reconciled.GapMinutes);
        Assert.True(reconciled.IsComplete);

        var gap = PaidTimeReconciliationCalculator.Calculate(employee, "DAY", shift, 120m,
            [Paid(employee, "DAY", TestFactory.Now, TestFactory.Now.AddMinutes(90))]);
        Assert.Equal(30m, gap.GapMinutes);
        Assert.False(gap.IsComplete);

        var overlap = PaidTimeReconciliationCalculator.Calculate(employee, "DAY", shift, 90m,
        [Paid(employee, "DAY", TestFactory.Now, TestFactory.Now.AddMinutes(60)),
         Paid(employee, "DAY", TestFactory.Now.AddMinutes(30), TestFactory.Now.AddMinutes(90))]);
        Assert.Equal(90m, overlap.AccountedPaidMinutes);
        Assert.Equal(30m, overlap.OverlapMinutes);
        Assert.False(overlap.IsComplete);
        Assert.Contains(overlap.DataQualityFlags, flag => flag.Contains("overlap", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Machine_downtime_is_limited_to_assignment_and_paid_shift_overlap_after_prior_classification()
    {
        var downtime = new PerformanceInterval(TestFactory.Now.AddMinutes(-10), TestFactory.Now.AddMinutes(60));
        var assignment = new PerformanceInterval(TestFactory.Now, TestFactory.Now.AddMinutes(90));
        var shift = new PerformanceInterval(TestFactory.Now, TestFactory.Now.AddMinutes(45));

        var result = MachineDowntimeImpactCalculator.Calculate(downtime, assignment, shift,
            [new PerformanceInterval(TestFactory.Now.AddMinutes(10), TestFactory.Now.AddMinutes(30))]);

        Assert.Equal(70m, result.FullMachineEventMinutes);
        Assert.Equal(45m, result.AssignedEmployeeOverlapMinutes);
        Assert.Equal(20m, result.AlreadyClassifiedMinutes);
        Assert.Equal(25m, result.EmployeeMachineImpactMinutes);
    }

    [Fact]
    public void Controllable_efficiency_shows_raw_and_adjusted_hours_and_returns_na_for_zero_eligible_time()
    {
        var metric = ControllableLaborEfficiencyCalculator.Calculate(7.15m, 10m, 6.5m, 3m);

        Assert.Equal(71.5m, metric.UnadjustedEfficiencyPercent);
        Assert.Equal(110m, metric.ControllableEfficiencyPercent);
        Assert.Equal(3m, metric.VerifiedExternalDelayHours);
        Assert.Null(ControllableLaborEfficiencyCalculator.Calculate(7.15m, 10m, 0m, 10m).ControllableEfficiencyPercent);
        Assert.Null(ControllableLaborEfficiencyCalculator.Calculate(null, 10m, 6.5m, 3m).UnadjustedEfficiencyPercent);
    }

    [Fact]
    public void Official_and_employee_attributable_fpy_use_distinct_populations_and_show_coverage()
    {
        var inspections = new List<FirstPassInspectionResult>();
        for (var index = 0; index < 92; index++)
            inspections.Add(new FirstPassInspectionResult($"GOOD-{index}", 1m, true, FirstPassCause.EmployeeControlled, true, true));
        for (var index = 0; index < 3; index++)
            inspections.Add(new FirstPassInspectionResult($"EMP-FAIL-{index}", 1m, false, FirstPassCause.EmployeeControlled, true, true));
        for (var index = 0; index < 5; index++)
            inspections.Add(new FirstPassInspectionResult($"MACHINE-FAIL-{index}", 1m, false, FirstPassCause.Machine, true, false));

        var metric = FirstPassYieldCalculator.Calculate(inspections, 95m);

        Assert.Equal(100m, metric.InspectedQuantity);
        Assert.Equal(92m, metric.FirstPassGoodQuantity);
        Assert.Equal(92m, metric.OfficialProcessFpyPercent);
        Assert.Equal(95m, metric.EmployeeAttributionCoveragePercent);
        Assert.Equal(95m, metric.EmployeeEligibleQuantity);
        Assert.Equal(92m, metric.EmployeeEligibleGoodQuantity);
        Assert.Equal(96.84m, metric.EmployeeAttributableFpyPercent);
        Assert.Equal(5m, metric.ExternallyCausedQuantity);
        Assert.Null(FirstPassYieldCalculator.Calculate(inspections, 96m).EmployeeAttributableFpyPercent);
        Assert.Null(FirstPassYieldCalculator.Calculate([], 95m).OfficialProcessFpyPercent);
    }

    [Fact]
    public void Skill_capacity_forecast_uses_calibrated_setup_and_run_ranges_after_hard_gates()
    {
        var input = SkillEstimate() with
        {
            ConservativeSetupMinutes = 35m,
            ExpectedSetupMinutes = 30m,
            OptimisticSetupMinutes = 25m,
            ConservativeRunMinutesPerGoodUnit = 6m,
            ExpectedRunMinutesPerGoodUnit = 5m,
            OptimisticRunMinutesPerGoodUnit = 4m,
            ScheduledEligibleMinutes = 480,
            BreakMinutes = 30,
            PlannedNonProductionMinutes = 30,
        };

        var forecast = SkillCapacityForecastCalculator.Calculate(input);

        Assert.True(forecast.IsEligible);
        Assert.Equal(420, forecast.ProductiveMinutes);
        Assert.Equal(64m, forecast.ConservativeGoodUnits);
        Assert.Equal(78m, forecast.ExpectedGoodUnits);
        Assert.Equal(98m, forecast.OptimisticGoodUnits);
        Assert.Equal(124, forecast.DaysSinceLastVerifiedProductiveExecution);
        Assert.Equal(CapacityEstimateConfidence.High, forecast.Confidence);

        var expired = SkillCapacityForecastCalculator.Calculate(input with { HasCurrentQualification = false });
        Assert.False(expired.IsEligible);
        Assert.Null(expired.ExpectedGoodUnits);
        Assert.Contains("expired", expired.EligibilityStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Coverage_metrics_average_per_assignment_and_report_forecast_error_separately()
    {
        var employee = Guid.NewGuid();
        var assignments = new[]
        {
            Coverage(employee, "A", "CNC", 70m, 76m, 72m, TestFactory.Now, TestFactory.Now.AddHours(1)),
            Coverage(employee, "B", "WIND", 120m, 125m, 120m, TestFactory.Now.AddMinutes(30), TestFactory.Now.AddHours(1.5)),
            Coverage(employee, "C", "SLIT", 80m, 72m, 75m, TestFactory.Now.AddHours(2), TestFactory.Now.AddHours(3)),
        };

        var metrics = CrossFunctionalCoverageCalculator.Calculate(employee, assignments, minimumEligibleDurationMinutes: 15);

        Assert.Equal(3, metrics.GoalEligibleAssignmentCount);
        Assert.Equal(2, metrics.GoalSuccessCount);
        Assert.Equal(66.67m, metrics.CoverageGoalSuccessRatePercent);
        Assert.Equal(100m * ((76m / 70m) + (125m / 120m) + (72m / 80m)) / 3m,
            metrics.AverageCoverageGoalAttainmentPercent!.Value, 2);
        Assert.Equal(2.5m, metrics.VerifiedCoverageHours);
        Assert.Equal(3, metrics.DistinctQualifiedOperationsCovered);
        Assert.Equal(3, metrics.ForecastComparisonCount);
        Assert.Equal(4m, metrics.MeanAbsoluteForecastErrorGoodUnits);
        Assert.Equal(-2m, metrics.MeanForecastBiasGoodUnits);
    }

    [Fact]
    public void Coverage_goal_exclusions_and_no_data_are_explicit_and_never_become_zero_percent()
    {
        var employee = Guid.NewGuid();
        var assignments = new[]
        {
            Coverage(employee, "NO-GOAL", "CNC", null, 10m, null, TestFactory.Now, TestFactory.Now.AddHours(1)),
            Coverage(employee, "NOT-COMPARABLE", "WIND", 10m, 8m, 9m, TestFactory.Now.AddHours(1), TestFactory.Now.AddHours(2)) with
            { Comparable = false, ExclusionReason = "Machine breakdown; reviewed as not comparable." },
            Coverage(employee, "UNQUALIFIED", "SLIT", 10m, 12m, 11m, TestFactory.Now.AddHours(2), TestFactory.Now.AddHours(3)) with
            { HasCurrentQualification = false },
        };

        var metrics = CrossFunctionalCoverageCalculator.Calculate(employee, assignments, minimumEligibleDurationMinutes: 15);
        var empty = CrossFunctionalCoverageCalculator.Calculate(employee, [], minimumEligibleDurationMinutes: 15);

        Assert.Null(metrics.CoverageGoalSuccessRatePercent);
        Assert.Null(metrics.AverageCoverageGoalAttainmentPercent);
        Assert.Equal(3, metrics.ExcludedGoalAssignmentCount);
        Assert.Contains(metrics.GoalExclusions, exclusion => exclusion.Reason.Contains("Machine breakdown", StringComparison.Ordinal));
        Assert.Null(empty.CoverageGoalSuccessRatePercent);
        Assert.Null(empty.AverageCoverageGoalAttainmentPercent);
        Assert.Equal(0, empty.GoalEligibleAssignmentCount);
    }

    [Fact]
    public void Eight_of_eight_completed_eligible_assignments_with_seven_goals_met_is_87_point_5_percent()
    {
        var employee = Guid.NewGuid();
        var assignments = Enumerable.Range(0, 8).Select(index => Coverage(employee, $"A-{index}", $"OP-{index}",
            100m, index == 4 ? 99m : 100m, 100m, TestFactory.Now.AddHours(index), TestFactory.Now.AddHours(index + 1))).ToArray();

        var metrics = CrossFunctionalCoverageCalculator.Calculate(employee, assignments, minimumEligibleDurationMinutes: 15);

        Assert.Equal(8, metrics.GoalEligibleAssignmentCount);
        Assert.Equal(7, metrics.GoalSuccessCount);
        Assert.Equal(87.5m, metrics.CoverageGoalSuccessRatePercent);
    }

    private static PaidTimeActivityInterval Paid(Guid employee, string code, DateTimeOffset start, DateTimeOffset end) =>
        new(employee, "DAY", new PerformanceInterval(start, end), code);

    private static SkillCapacityForecastInput SkillEstimate() => new(
        true, true, true, true, true, 480, 30, 30, CapacityEstimateEvidence.VerifiedContextualHistory,
        "Context-matched CNC-2 setup/run observations with downtime excluded", 12, TestFactory.Now,
        TestFactory.Now.AddDays(-124), 40m, 35m, 30m, 8m, 7m, 6m, CapacityEstimateConfidence.High);

    private static CoverageGoalAssignment Coverage(Guid employee, string id, string operation, decimal? goal,
        decimal? actual, decimal? forecast, DateTimeOffset start, DateTimeOffset end) => new(
        employee, id, operation, start, end, true, true, true, true, false, true, true, true, true,
        goal, actual, forecast);
}
