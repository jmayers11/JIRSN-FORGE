namespace Forge.Planning.Tests;

/// <summary>Owner decisions 2026-11-02: forecasting for more complex items: trend and season together, families and options, new items, planner adjustments.</summary>
public sealed class ForecastComplexItemTests
{
    private static readonly DateOnly Start = new(2024, 1, 1);

    private static IReadOnlyList<MonthlyDemand> Series(params decimal[] values) => values.Select((v, i) => new MonthlyDemand(Start.AddMonths(i), v)).ToList();

    private static HistorySummary History(string part, IReadOnlyList<MonthlyDemand> series, int simulated = 0) =>
        new(part, null, series.Count, series.Sum(s => s.Quantity), simulated, series.Count - simulated, 0, series, []);

    private static readonly ForecastPolicy Policy = ForecastPolicy.Default with { Horizon = 6, Holdout = 6 };

    [Fact]
    public void A_growing_seasonal_part_is_forecast_with_trend_and_season_together()
    {
        var season = new[] { -30m, -20m, 0m, 20m, 30m, 20m, 0m, -20m, -30m, -20m, 0m, 10m };
        var values = Enumerable.Range(0, 36).Select(t => 200m + 4m * t + season[t % 12]).ToArray();
        var forecast = ForecastEngine.Forecast("P", Series(values), 6, 6, 80, ForecastEngine.Check(Series(values), []));
        Assert.Equal(ForecastMethod.HoltWinters, forecast.Method);
        var hw = forecast.Scores.Single(s => s.Method == ForecastMethod.HoltWinters).MeanAbsoluteError;
        Assert.True(hw < forecast.Scores.Single(s => s.Method == ForecastMethod.SeasonalNaive).MeanAbsoluteError);
        // Next January: the level has kept growing; same-month-last-year would be 48 short.
        var truth = 200m + 4m * 36 + season[0];
        Assert.InRange(forecast.Months[0].Forecast, truth - 20, truth + 20);
    }

    [Fact]
    public void A_steadily_growing_part_gets_a_trend_method()
    {
        var values = Enumerable.Range(0, 20).Select(t => 50m + 5m * t).ToArray();
        var forecast = ForecastEngine.Forecast("P", Series(values), 3, 6, 80, ForecastEngine.Check(Series(values), []));
        Assert.Equal(ForecastMethod.DampedTrend, forecast.Method);
        Assert.True(forecast.Months[0].Forecast > values[^1]);
        Assert.Contains(ForecastEngine.Check(Series(values), []), f => f.Code == "trend");
    }

    [Fact]
    public void Sparse_demand_also_tries_the_corrected_croston()
    {
        var forecast = ForecastEngine.Forecast("P", Series(10, 0, 0, 12, 0, 0, 9, 0, 0, 11, 0, 0), 2, 4, 80, []);
        Assert.Contains(ForecastMethod.SyntetosBoylan, forecast.Scores.Select(s => s.Method));
        Assert.True(ForecastEngine.Next(ForecastMethod.SyntetosBoylan, [10, 0, 0, 12]) < ForecastEngine.Next(ForecastMethod.Croston, [10, 0, 0, 12]));
    }

    [Fact]
    public void A_family_is_forecast_as_a_whole_and_split_by_recent_mix_or_fixed_share()
    {
        var a = History("VAR-A", Series(Enumerable.Repeat(30m, 24).ToArray()));
        var b = History("VAR-B", Series(Enumerable.Repeat(10m, 24).ToArray()));
        var c = History("VAR-C", Series(Enumerable.Repeat(5m, 24).ToArray()));
        var family = new ForecastFamily("FAM", "Brackets", null, [new("VAR-A", null), new("VAR-B", null), new("VAR-C", 0.2m)], "p", DateTimeOffset.UnixEpoch);
        var lines = ForecastService.Build([a, b, c], Policy, new ForecastSetup([], [family], []), Start.AddMonths(24));
        var whole = lines.Single(l => l.Forecast.PartNumber == "family:FAM").Forecast;
        Assert.Equal(45m, whole.Months[0].Forecast);
        // C is fixed at 20 %; A and B split the other 80 % by their 3:1 mix.
        Assert.Equal(9m, lines.Single(l => l.Forecast.PartNumber == "VAR-C").Forecast.Months[0].Forecast);
        Assert.Equal(27m, lines.Single(l => l.Forecast.PartNumber == "VAR-A").Forecast.Months[0].Forecast);
        Assert.Equal(9m, lines.Single(l => l.Forecast.PartNumber == "VAR-B").Forecast.Months[0].Forecast);
        Assert.Contains("of family Brackets", lines.Single(l => l.Forecast.PartNumber == "VAR-A").Forecast.Basis, StringComparison.Ordinal);
        Assert.Contains(lines.Single(l => l.Forecast.PartNumber == "VAR-A").Forecast.Flags, f => f.Code == "own_history");
    }

    [Fact]
    public void Options_of_a_configurable_item_follow_the_driver_by_attach_rate()
    {
        var driver = History("CONFIG", Series(Enumerable.Repeat(100m, 24).ToArray()));
        var option = History("OPT-HEATER", Series(Enumerable.Repeat(40m, 24).ToArray()));
        var family = new ForecastFamily("CFG", "Configured unit", "CONFIG", [new("OPT-HEATER", null), new("OPT-KIT", 2m)], "p", DateTimeOffset.UnixEpoch);
        var lines = ForecastService.Build([driver, option], Policy, new ForecastSetup([], [family], []), Start.AddMonths(24));
        Assert.Equal(40m, lines.Single(l => l.Forecast.PartNumber == "OPT-HEATER").Forecast.Months[0].Forecast); // 40 % attach
        Assert.Equal(200m, lines.Single(l => l.Forecast.PartNumber == "OPT-KIT").Forecast.Months[0].Forecast); // two per unit, set
        Assert.Contains(lines, l => l.Forecast.PartNumber == "CONFIG"); // the driver is still forecast on its own
    }

    [Fact]
    public void A_new_item_borrows_a_like_items_pattern_scaled_from_its_launch()
    {
        var like = History("OLD", Series(Enumerable.Repeat(40m, 24).ToArray()));
        var next = Start.AddMonths(24);
        var analog = new ForecastAnalog("NEW", "OLD", 0.5m, next.AddMonths(2), "p", DateTimeOffset.UnixEpoch);
        var line = ForecastService.Build([like], Policy, new ForecastSetup([analog], [], []), next).Single(l => l.Forecast.PartNumber == "NEW").Forecast;
        Assert.Equal(new[] { 0m, 0m, 20m, 20m, 20m, 20m }, line.Months.Select(m => m.Forecast).ToArray());
        Assert.StartsWith("Like OLD × 0.5", line.Basis, StringComparison.Ordinal);
        Assert.Contains(line.Flags, f => f.Code == "analog");

        // With no scale set, three shared months work it out.
        var own = History("NEW", Enumerable.Range(21, 3).Select(i => new MonthlyDemand(Start.AddMonths(i), 10m)).ToList());
        var scaled = ForecastService.Build([like, own], Policy, new ForecastSetup([analog with { Scale = null, LaunchMonth = null }], [], []), next).Single(l => l.Forecast.PartNumber == "NEW").Forecast;
        Assert.Equal(10m, scaled.Months[0].Forecast);
        Assert.Contains(scaled.Flags, f => f.Code == "scale_from_history");
    }

    [Fact]
    public void Planner_adjustments_sit_on_top_and_keep_the_methods_figure()
    {
        var part = History("P", Series(Enumerable.Repeat(100m, 24).ToArray()));
        var next = Start.AddMonths(24);
        var adjustments = new[]
        {
            new ForecastAdjustment(1, "P", next, next, ForecastAdjustmentKind.AddUnits, 50, "Spring promotion", "p", DateTimeOffset.UnixEpoch),
            new ForecastAdjustment(2, "P", next.AddMonths(1), next.AddMonths(2), ForecastAdjustmentKind.Percent, -25, "Customer de-stocking", "p", DateTimeOffset.UnixEpoch),
            new ForecastAdjustment(3, "P", next.AddMonths(4), null, ForecastAdjustmentKind.EndOfLife, 0, "Replaced by P2", "p", DateTimeOffset.UnixEpoch),
            new ForecastAdjustment(4, "P", next, next, ForecastAdjustmentKind.AddUnits, 999, "Cancelled", "p", DateTimeOffset.UnixEpoch, "p", DateTimeOffset.UnixEpoch),
        };
        var line = ForecastService.Build([part], Policy, new ForecastSetup([], [], adjustments), next).Single().Forecast;
        Assert.Equal(new[] { 150m, 75m, 75m, 100m, 0m, 0m }, line.Months.Select(m => m.Forecast).ToArray());
        Assert.Equal(100m, line.Months[0].Statistical);
        Assert.Contains("Spring promotion", line.Months[0].Adjusted, StringComparison.Ordinal);
        Assert.Null(line.Months[3].Adjusted);
        Assert.Contains(line.Flags, f => f.Code == "adjusted");
    }
}
