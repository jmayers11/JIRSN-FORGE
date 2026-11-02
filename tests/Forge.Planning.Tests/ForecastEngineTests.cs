namespace Forge.Planning.Tests;

/// <summary>Owner decisions 2026-11-02: forecasting methods are tested on held-out months and the history is checked first.</summary>
public sealed class ForecastEngineTests
{
    private static readonly DateOnly Start = new(2024, 1, 1);

    private static IReadOnlyList<MonthlyDemand> Series(params decimal[] values) => values.Select((v, i) => new MonthlyDemand(Start.AddMonths(i), v)).ToList();

    [Fact]
    public void A_seasonal_part_is_forecast_from_the_same_month_last_year()
    {
        var year = new[] { 10m, 12m, 20m, 40m, 60m, 80m, 80m, 60m, 40m, 20m, 12m, 10m };
        var series = Series([.. year, .. year, .. year]);
        var forecast = ForecastEngine.Forecast("P", series, 6, 6, 80, []);
        Assert.Equal(ForecastMethod.SeasonalNaive, forecast.Method);
        Assert.Equal(0m, forecast.Scores.Single(s => s.Method == ForecastMethod.SeasonalNaive).MeanAbsoluteError);
        Assert.Equal(new[] { 10m, 12m, 20m, 40m, 60m, 80m }, forecast.Months.Select(m => m.Forecast).ToArray());
        Assert.Equal(new DateOnly(2027, 1, 1), forecast.Months[0].Month);
    }

    [Fact]
    public void A_steady_part_gets_a_simple_method_and_a_tight_range()
    {
        var forecast = ForecastEngine.Forecast("P", Series(50, 50, 50, 50, 50, 50, 50, 50, 50, 50), 3, 4, 80, []);
        Assert.Equal(ForecastMethod.Naive, forecast.Method); // ties go to the simplest method
        Assert.All(forecast.Months, m => Assert.Equal((50m, 50m, 50m), (m.Forecast, m.Low, m.High)));
        Assert.DoesNotContain(ForecastMethod.SeasonalNaive, forecast.Scores.Select(s => s.Method)); // under 13 months
    }

    [Fact]
    public void History_checks_flag_gaps_spikes_short_and_sparse_history()
    {
        var series = Series(10, 0, 0, 12, 0, 0, 400, 0, 0, 11);
        var flags = ForecastEngine.Check(series, [Start.AddMonths(1)]);
        Assert.Contains(flags, f => f.Code == "short_history");
        Assert.Contains(flags, f => f.Code == "missing_months");
        Assert.Contains(flags, f => f.Code == "intermittent");
        var spiky = Series(10, 12, 11, 9, 10, 300, 11, 10, 12, 10, 11, 9, 10);
        Assert.Contains(ForecastEngine.Check(spiky, []), f => f.Code == "spike" && f.Message.Contains("2024-06", StringComparison.Ordinal));
        Assert.Contains(ForecastMethod.Croston, ForecastEngine.Forecast("P", series, 2, 4, 80, flags).Scores.Select(s => s.Method));
    }

    [Fact]
    public void Gaps_inside_the_history_count_as_zero_months()
    {
        var filled = ForecastEngine.Fill([new MonthlyDemand(new DateOnly(2024, 1, 15), 5), new MonthlyDemand(new DateOnly(2024, 4, 2), 7)], new DateOnly(2024, 5, 1));
        Assert.Equal(new[] { 5m, 0m, 0m, 7m, 0m }, filled.Select(f => f.Quantity).ToArray());
    }

    [Fact]
    public void Simulated_history_is_the_same_for_the_same_part_and_seed()
    {
        var a = ForecastEngine.Simulate("ROLL-100", Start, 24, 2026);
        var b = ForecastEngine.Simulate("ROLL-100", Start, 24, 2026);
        var c = ForecastEngine.Simulate("ROLL-100", Start, 24, 7);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(24, a.Count);
        Assert.All(a, m => Assert.True(m.Quantity >= 0));
    }
}
