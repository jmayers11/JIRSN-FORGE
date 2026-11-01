using Forge.Procurement.Contracts;
using Forge.Quality.Contracts;

namespace Forge.Planning.Tests;

/// <summary>Owner decisions 2026-11-01: SPC arithmetic (I-MR limits, Cp/Cpk, Western Electric rules) and the supplier on-time rule.</summary>
public class SpcEngineTests
{
    private static readonly SpcCharacteristicKey Key = new("SHAFT-1", 20, 1);
    private static readonly CharacteristicInfo Width = new(1, "Width", CharacteristicKind.Variable, 10m, 9.9m, 10.1m, "mm");
    private static readonly SpcPolicy Policy = SpcPolicy.Default with { MinimumPoints = 5 };

    private static List<SpcPoint> Points(params decimal[] values) =>
        values.Select((v, i) => new SpcPoint(i + 1, 10002, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero).AddHours(i), v)).ToList();

    [Fact]
    public void Limits_come_from_the_average_moving_range_and_capability_from_the_drawing()
    {
        var chart = SpcEngine.Chart(Key, Width, Points(10.00m, 10.02m, 10.00m, 10.02m, 10.00m, 10.02m), false, Policy);
        Assert.Equal(10.01m, chart.Mean);
        Assert.Equal(0.02m, chart.AverageMovingRange);
        Assert.Equal(Math.Round(0.02m / 1.128m, 6), chart.Sigma);
        Assert.Equal(Math.Round(10.01m + (3 * 0.02m / 1.128m), 6), chart.UpperControlLimit);
        Assert.Equal(Math.Round(3.267m * 0.02m, 6), chart.MovingRangeUpperLimit);
        Assert.Equal(Math.Round(0.2m / (6 * 0.02m / 1.128m), 3), chart.Cp);
        Assert.Equal(Math.Round(0.09m / (3 * 0.02m / 1.128m), 3), chart.Cpk);
        Assert.True(chart.Capable);
        Assert.Equal(1.33m, chart.RequiredCpk);
        Assert.Equal(1.67m, SpcEngine.Chart(Key, Width, chart.Points, true, Policy).RequiredCpk);
    }

    [Fact]
    public void Too_few_points_or_no_spread_gives_no_capability_and_says_why()
    {
        var few = SpcEngine.Chart(Key, Width, Points(10m, 10.01m), false, Policy);
        Assert.Null(few.Cpk);
        Assert.Null(few.Capable);
        Assert.Contains("need at least 5", Assert.Single(few.Notes), StringComparison.Ordinal);
        var flat = SpcEngine.Chart(Key, Width, Points(10m, 10m, 10m, 10m, 10m), false, Policy);
        Assert.Null(flat.Cpk);
        Assert.Empty(flat.Violations);
        Assert.Contains("gauge resolution", Assert.Single(flat.Notes), StringComparison.Ordinal);
    }

    [Fact]
    public void A_one_sided_limit_uses_that_side_and_an_incapable_process_is_noted()
    {
        var oneSided = Width with { Lower = null };
        var chart = SpcEngine.Chart(Key, oneSided, Points(10.05m, 10.09m, 10.04m, 10.08m, 10.05m, 10.09m), false, Policy);
        Assert.Null(chart.Cp);
        Assert.False(chart.Capable);
        Assert.Contains(chart.Notes, n => n.StartsWith("One-sided limit", StringComparison.Ordinal));
        Assert.Contains(chart.Notes, n => n.Contains("below the required 1.33", StringComparison.Ordinal));
    }

    [Fact]
    public void Each_western_electric_rule_is_reported_once_at_the_point_that_completes_it()
    {
        // mean 0, sigma 1 — z values are the values themselves.
        var beyond = SpcEngine.Rules(Points(0m, 3.5m, 0m), 0m, 1m);
        Assert.Equal((1, SpcRule.BeyondThreeSigma), (Assert.Single(beyond).PointIndex, beyond[0].Rule));
        var twoOfThree = SpcEngine.Rules(Points(2.5m, 0m, 2.5m), 0m, 1m);
        Assert.Equal((2, SpcRule.TwoOfThreeBeyondTwoSigma), (Assert.Single(twoOfThree).PointIndex, twoOfThree[0].Rule));
        var fourOfFive = SpcEngine.Rules(Points(-1.5m, -1.5m, 0m, -1.5m, -1.5m), 0m, 1m);
        Assert.Equal((4, SpcRule.FourOfFiveBeyondOneSigma), (Assert.Single(fourOfFive).PointIndex, fourOfFive[0].Rule));
        var eight = SpcEngine.Rules(Points(0.5m, 0.5m, 0.5m, 0.5m, 0.5m, 0.5m, 0.5m, 0.5m), 0m, 1m);
        Assert.Equal((7, SpcRule.EightOnOneSide), (Assert.Single(eight).PointIndex, eight[0].Rule));
        Assert.Empty(SpcEngine.Rules(Points(0.5m, -0.5m, 0.5m, -0.5m, 0.5m, -0.5m, 0.5m, -0.5m), 0m, 1m));
    }

    [Fact]
    public void On_time_is_on_or_before_the_promise_unless_the_company_allows_lateness_or_limits_earliness()
    {
        var standard = ScorecardPolicy.Default;
        Assert.True(standard.OnTime(0));
        Assert.True(standard.OnTime(-30));
        Assert.False(standard.OnTime(1));
        var adjusted = standard with { LateToleranceDays = 2, EarlyLimitDays = 5 };
        Assert.True(adjusted.OnTime(2));
        Assert.False(adjusted.OnTime(3));
        Assert.True(adjusted.OnTime(-5));
        Assert.False(adjusted.OnTime(-6));
        Assert.Equal(("A", "B", "C", "C"), (standard.Grade(96m, 99.6m), standard.Grade(96m, 99m), standard.Grade(80m, 100m), standard.Grade(100m, 97m)));
    }
}
