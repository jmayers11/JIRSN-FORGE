using Forge.Execution.Contracts;
using Forge.Maintenance.Contracts;

namespace Forge.Planning.Tests;

/// <summary>Sprint 10 §21: verified observations and transparent capacity estimates, deterministic and evidence-labeled.</summary>
public sealed class SkillEvidenceTests
{
    private static readonly Guid Dana = Guid.Parse("00000000-0000-0000-0000-00000000d001");
    private static readonly Guid Eli = Guid.Parse("00000000-0000-0000-0000-00000000e001");
    private static readonly Dictionary<(long Order, int Sequence), (string Activity, string Part)> Ops = new()
    {
        [(1, 10)] = ("CNC_RUN", "WW-HOUSING"), [(2, 10)] = ("CNC_RUN", "WW-HOUSING"), [(3, 10)] = ("CNC_RUN", "WW-HOUSING"),
        [(4, 10)] = ("CNC_RUN", "WW-HOUSING"), [(5, 10)] = ("CNC_RUN", "WW-HOUSING"), [(6, 10)] = ("CNC_RUN", "WW-BRACKET"),
    };

    private static readonly OperationStandard Standard = new("WW-HOUSING", 30m, 4m, 9, 10);

    private static LaborEntry Run(Guid person, long order, DateTimeOffset start, int minutes, LaborPhase phase = LaborPhase.Run, string machine = "CNC-02") =>
        new(order * 100 + (int)phase, person, order, 10, "CNC-WC", machine, LaborRole.Operator, start, start.AddMinutes(minutes), null, null, phase);

    private static ProductionReport Good(Guid person, long order, decimal good) =>
        new(order, order, 10, "CNC-WC", person, good, 0m, null, true, false, TestFactory.Now);

    [Fact]
    public void Two_qualified_operators_with_different_history_get_distinct_evidence_based_ranges()
    {
        var day = TestFactory.Now.AddDays(-20);
        var labor = new List<LaborEntry>();
        var reports = new List<ProductionReport>();
        for (var i = 1; i <= 5; i++)
        {
            labor.Add(Run(Dana, i, day.AddDays(i), 100));
            reports.Add(Good(Dana, i, 25m));     // 4 min per good unit
        }

        for (var i = 1; i <= 3; i++)
        {
            labor.Add(Run(Eli, i, day.AddDays(i).AddHours(3), 120));
            reports.Add(Good(Eli, i, 20m));      // 6 min per good unit
        }

        var observations = SkillEvidence.Observations(labor, reports, [], Ops);
        var (dana, _, _) = SkillEvidence.Estimate(Dana, "CNC-02", "CNC_RUN", Standard, observations, TestFactory.Now, 480, 30, true, true, true, true, true, null, null);
        var (eli, _, _) = SkillEvidence.Estimate(Eli, "CNC-02", "CNC_RUN", Standard, observations, TestFactory.Now, 480, 30, true, true, true, true, true, null, null);
        var a = SkillCapacityForecastCalculator.Calculate(dana);
        var b = SkillCapacityForecastCalculator.Calculate(eli);

        Assert.Equal(CapacityEstimateEvidence.VerifiedContextualHistory, a.Evidence);
        Assert.Equal(CapacityEstimateConfidence.High, a.Confidence);
        Assert.Equal(CapacityEstimateConfidence.Medium, b.Confidence);
        Assert.True(a.ExpectedGoodUnits > b.ExpectedGoodUnits);
        Assert.Contains("5 verified runs", a.EstimateBasis, StringComparison.Ordinal);
    }

    [Fact]
    public void Machine_downtime_is_excluded_from_the_run_rate_and_overlapping_labor_counts_once()
    {
        var start = TestFactory.Now.AddDays(-2);
        // 120 minutes on the job, recorded twice (a duplicate scan), with a 40-minute verified breakdown inside it.
        var labor = new[] { Run(Dana, 1, start, 120), Run(Dana, 1, start.AddMinutes(10), 100) with { LaborId = 999 } };
        var downtime = new[] { new DowntimeWindow(1, "CNC-02", DowntimeKind.Unplanned, start.AddMinutes(40), start.AddMinutes(80), "Spindle fault") };
        var observation = Assert.Single(SkillEvidence.Observations(labor, [Good(Dana, 1, 20m)], downtime, Ops));

        Assert.Equal(80m, observation.RunMinutes);
        Assert.Equal(40m, observation.ExcludedDowntimeMinutes);
        Assert.Equal(4m, observation.RunMinutesPerGoodUnit);
    }

    [Fact]
    public void Without_reliable_history_the_estimate_is_a_labeled_conservative_benchmark()
    {
        var (input, _, _) = SkillEvidence.Estimate(Eli, "CNC-02", "CNC_RUN", Standard, [], TestFactory.Now, 480, 30, true, true, true, true, true, null, null);
        var forecast = SkillCapacityForecastCalculator.Calculate(input);

        Assert.Equal(CapacityEstimateEvidence.CalibratedRoleBenchmark, forecast.Evidence);
        Assert.Equal(CapacityEstimateConfidence.Low, forecast.Confidence);
        Assert.Equal(6m, input.ConservativeRunMinutesPerGoodUnit);   // 4 × 1.5
        Assert.Equal(4m, input.OptimisticRunMinutesPerGoodUnit);     // the standard itself, never better
        Assert.Contains("conservative", forecast.EstimateBasis, StringComparison.Ordinal);

        var none = SkillEvidence.Estimate(Eli, "CNC-02", "CNC_RUN", null, [], TestFactory.Now, 480, 30, true, true, true, true, true, null, null).Input;
        Assert.Null(SkillCapacityForecastCalculator.Calculate(none).ExpectedGoodUnits);
    }

    [Fact]
    public void An_expired_qualification_or_missing_refresher_is_a_hard_stop_whatever_the_history()
    {
        var labor = Enumerable.Range(1, 5).Select(i => Run(Dana, i, TestFactory.Now.AddDays(-200 - i), 100)).ToList();
        var observations = SkillEvidence.Observations(labor, [.. Enumerable.Range(1, 5).Select(i => Good(Dana, i, 50m))], [], Ops);

        var expired = SkillCapacityForecastCalculator.Calculate(SkillEvidence.Estimate(Dana, "CNC-02", "CNC_RUN", Standard, observations, TestFactory.Now, 480, 30,
            false, true, true, true, true, null, null).Input);
        Assert.False(expired.IsEligible);
        Assert.Null(expired.ExpectedGoodUnits);

        var noRefresher = SkillCapacityForecastCalculator.Calculate(SkillEvidence.Estimate(Dana, "CNC-02", "CNC_RUN", Standard, observations, TestFactory.Now, 480, 30,
            true, false, true, true, true, 35, 180).Input);
        Assert.False(noRefresher.IsEligible);
        Assert.Contains("refresher", noRefresher.EligibilityStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void Reacclimation_is_extra_setup_not_a_second_penalty_on_the_run_rate()
    {
        var labor = Enumerable.Range(1, 5).Select(i => Run(Dana, i, TestFactory.Now.AddDays(-200 - i), 100)).ToList();
        var observations = SkillEvidence.Observations(labor, [.. Enumerable.Range(1, 5).Select(i => Good(Dana, i, 25m))], [], Ops);
        var fresh = SkillEvidence.Estimate(Dana, "CNC-02", "CNC_RUN", Standard, observations, TestFactory.Now, 480, 30, true, true, true, true, true, null, null);
        var away = SkillEvidence.Estimate(Dana, "CNC-02", "CNC_RUN", Standard, observations, TestFactory.Now, 480, 30, true, true, true, true, true, 35, 180);

        Assert.Equal(fresh.Input.ExpectedRunMinutesPerGoodUnit, away.Input.ExpectedRunMinutesPerGoodUnit);
        Assert.Equal(fresh.Input.ExpectedSetupMinutes + 35m, away.Input.ExpectedSetupMinutes);
        Assert.Contains(away.Notes, n => n.Contains("reacclimation", StringComparison.Ordinal));
    }

    [Fact]
    public void A_different_part_is_not_compared_directly_when_enough_same_part_history_exists()
    {
        var labor = Enumerable.Range(1, 6).Select(i => Run(Dana, i, TestFactory.Now.AddDays(-10 - i), 100)).ToList();
        var reports = Enumerable.Range(1, 5).Select(i => Good(Dana, i, 25m)).Append(Good(Dana, 6, 5m)).ToList(); // the bracket is much slower
        var observations = SkillEvidence.Observations(labor, reports, [], Ops);
        var (input, _, notes) = SkillEvidence.Estimate(Dana, "CNC-02", "CNC_RUN", Standard, observations, TestFactory.Now, 480, 30, true, true, true, true, true, null, null);

        Assert.Equal(4m, input.ExpectedRunMinutesPerGoodUnit);
        Assert.Contains("(WW-HOUSING)", input.EstimateBasis, StringComparison.Ordinal);
        Assert.DoesNotContain(notes, n => n.Contains("other parts", StringComparison.Ordinal));
    }
}
