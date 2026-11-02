using System.Globalization;

namespace Forge.Planning;

/// <summary>A forecasting method Forge tries on each part (owner decisions 2026-11-02).</summary>
public enum ForecastMethod
{
    /// <summary>Next month is last month.</summary>
    Naive,

    /// <summary>Next month is the same month a year earlier (needs a year of history).</summary>
    SeasonalNaive,

    /// <summary>The average of the last three months.</summary>
    MovingAverage3,

    /// <summary>Simple exponential smoothing; the smoothing weight is fitted to the history.</summary>
    ExponentialSmoothing,

    /// <summary>Croston's method for parts that sell only now and then (many months with none).</summary>
    Croston,
}

/// <summary>One month of demand history.</summary>
public sealed record MonthlyDemand(DateOnly Month, decimal Quantity);

/// <summary>One forecast month with a likely range.</summary>
public sealed record ForecastMonth(DateOnly Month, decimal Forecast, decimal Low, decimal High);

/// <summary>How a method did when tested on the held-out months: mean absolute error, weighted percentage error and bias (+ over-forecasts).</summary>
public sealed record BacktestScore(ForecastMethod Method, decimal MeanAbsoluteError, decimal? WeightedPercentError, decimal Bias, int Months);

/// <summary>A warning about the history behind a forecast.</summary>
public sealed record HistoryFlag(string Code, string Message);

/// <summary>The forecast for one part: the method that tested best, every method's score, the months ahead and what to be careful of.</summary>
public sealed record PartForecast(string PartNumber, ForecastMethod Method, IReadOnlyList<BacktestScore> Scores, IReadOnlyList<ForecastMonth> Months, IReadOnlyList<HistoryFlag> Flags,
    int HistoryMonths, decimal AverageMonthly);

/// <summary>
/// Monthly demand forecasting (owner decisions 2026-11-02). Each part's history is checked (gaps, one-off spikes, short history, sparse
/// demand); every method is tested by forecasting the held-out last months one step at a time from what came before; the method with the
/// lowest mean absolute error wins, and a simple method is preferred on ties. The likely range comes from that method's test errors.
/// Forecasts are suggestions: a planner approves them before they become demand.
/// </summary>
public static class ForecastEngine
{
    public static DateOnly MonthOf(DateOnly day) => new(day.Year, day.Month, 1);

    /// <summary>Months from first to last with no gaps (missing months as zero).</summary>
    public static IReadOnlyList<MonthlyDemand> Fill(IEnumerable<MonthlyDemand> history, DateOnly? through = null)
    {
        var byMonth = history.GroupBy(h => MonthOf(h.Month)).ToDictionary(g => g.Key, g => g.Sum(h => h.Quantity));
        if (byMonth.Count == 0) return [];
        var first = byMonth.Keys.Min();
        var last = through is { } t ? MonthOf(t) : byMonth.Keys.Max();
        var list = new List<MonthlyDemand>();
        for (var m = first; m <= last; m = m.AddMonths(1)) list.Add(new MonthlyDemand(m, byMonth.GetValueOrDefault(m)));
        return list;
    }

    public static IReadOnlyList<HistoryFlag> Check(IReadOnlyList<MonthlyDemand> series, IReadOnlyCollection<DateOnly> monthsWithoutRecords)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(monthsWithoutRecords);
        var flags = new List<HistoryFlag>();
        if (series.Count < 12)
            flags.Add(new("short_history", string.Create(CultureInfo.InvariantCulture, $"Only {series.Count} months of history; seasonal methods need 13 or more and every forecast is less certain.")));
        if (monthsWithoutRecords.Count > 0)
            flags.Add(new("missing_months", string.Create(CultureInfo.InvariantCulture,
                $"{monthsWithoutRecords.Count} month(s) inside the history have no records at all ({string.Join(", ", monthsWithoutRecords.Order().Take(6).Select(m => m.ToString("yyyy-MM", CultureInfo.InvariantCulture)))}); they count as zero. Check they really had no demand.")));
        var zeros = series.Count(s => s.Quantity == 0);
        if (series.Count > 0 && zeros * 2 >= series.Count)
            flags.Add(new("intermittent", string.Create(CultureInfo.InvariantCulture, $"{zeros} of {series.Count} months had no demand; Croston's method is tried for sparse demand.")));
        var values = series.Select(s => s.Quantity).Where(v => v > 0).Order().ToList();
        if (values.Count >= 6)
        {
            var median = Median(values);
            var mad = Median(values.Select(v => Math.Abs(v - median)).Order().ToList());
            var limit = median + 5 * Math.Max(mad, median * 0.1m);
            foreach (var spike in series.Where(s => s.Quantity > limit))
                flags.Add(new("spike", string.Create(CultureInfo.InvariantCulture,
                    $"{spike.Month:yyyy-MM} had {spike.Quantity:0.##}, far above the usual {median:0.##}. If it was a one-off order, it inflates the forecast.")));
        }

        return flags;
    }

    /// <summary>Forecasts one part. <paramref name="holdout"/> months are held out to test each method; <paramref name="horizon"/> months are forecast.</summary>
    public static PartForecast Forecast(string partNumber, IReadOnlyList<MonthlyDemand> series, int horizon, int holdout, decimal intervalPercent, IReadOnlyList<HistoryFlag> flags)
    {
        ArgumentNullException.ThrowIfNull(series);
        var values = series.Select(s => s.Quantity).ToArray();
        var nextMonth = series.Count == 0 ? MonthOf(DateOnly.FromDayNumber(0)) : series[^1].Month.AddMonths(1);
        if (values.Length == 0)
            return new PartForecast(partNumber, ForecastMethod.Naive, [], [], [.. flags, new("no_history", "No demand history; nothing to forecast.")], 0, 0);

        var methods = new List<ForecastMethod> { ForecastMethod.Naive, ForecastMethod.MovingAverage3, ForecastMethod.ExponentialSmoothing };
        if (values.Length >= 13) methods.Add(ForecastMethod.SeasonalNaive);
        if (values.Count(v => v == 0) * 3 >= values.Length) methods.Add(ForecastMethod.Croston);
        var test = Math.Min(holdout, Math.Max(0, values.Length - 3));
        var scores = new List<(BacktestScore Score, decimal[] Errors)>();
        foreach (var method in methods)
        {
            var errors = new List<decimal>();
            var actual = 0m;
            for (var t = values.Length - test; t < values.Length; t++)
            {
                if (method == ForecastMethod.SeasonalNaive && t < 12) continue;
                var predicted = Next(method, values[..t]);
                errors.Add(predicted - values[t]);
                actual += values[t];
            }

            if (errors.Count == 0) continue;
            var mae = errors.Average(Math.Abs);
            scores.Add((new BacktestScore(method, Round(mae), actual > 0 ? Round(errors.Sum(Math.Abs) / actual * 100m) : null, Round(errors.Average()), errors.Count), errors.ToArray()));
        }

        var best = scores.Count == 0
            ? (Score: new BacktestScore(ForecastMethod.MovingAverage3, 0, null, 0, 0), Errors: Array.Empty<decimal>())
            : scores.OrderBy(s => s.Score.MeanAbsoluteError).ThenBy(s => (int)s.Score.Method).First();
        var spread = best.Errors.Length >= 2 ? StandardDeviation(best.Errors) : values.Length >= 2 ? StandardDeviation(values) : 0m;
        var z = intervalPercent switch { >= 95 => 1.96m, >= 90 => 1.645m, >= 80 => 1.2816m, _ => 1m };
        var months = new List<ForecastMonth>();
        var path = values.ToList();
        for (var h = 1; h <= horizon; h++)
        {
            var f = Math.Max(0, Next(best.Score.Method, [.. path]));
            var width = z * spread * (decimal)Math.Sqrt(h);
            months.Add(new ForecastMonth(nextMonth.AddMonths(h - 1), Round(f), Round(Math.Max(0, f - width)), Round(f + width)));
            path.Add(f);
        }

        return new PartForecast(partNumber, best.Score.Method, scores.Select(s => s.Score).OrderBy(s => s.MeanAbsoluteError).ToList(), months, flags, values.Length, Round(values.Average()));
    }

    /// <summary>The one-step forecast of a method from a history.</summary>
    public static decimal Next(ForecastMethod method, decimal[] history)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (history.Length == 0) return 0;
        return method switch
        {
            ForecastMethod.Naive => history[^1],
            ForecastMethod.SeasonalNaive => history.Length >= 12 ? history[^12] : history[^1],
            ForecastMethod.MovingAverage3 => history[^Math.Min(3, history.Length)..].Average(),
            ForecastMethod.ExponentialSmoothing => Smooth(history, BestAlpha(history)),
            ForecastMethod.Croston => Croston(history, 0.1m),
            _ => history[^1],
        };
    }

    private static readonly decimal[] Alphas = [0.1m, 0.2m, 0.3m, 0.5m, 0.7m, 0.9m];

    private static decimal BestAlpha(decimal[] history)
    {
        if (history.Length < 4) return 0.3m;
        return Alphas.OrderBy(a =>
        {
            var level = history[0];
            var error = 0m;
            for (var i = 1; i < history.Length; i++)
            {
                error += Math.Abs(history[i] - level);
                level = a * history[i] + (1 - a) * level;
            }

            return error;
        }).First();
    }

    private static decimal Smooth(decimal[] history, decimal alpha)
    {
        var level = history[0];
        for (var i = 1; i < history.Length; i++) level = alpha * history[i] + (1 - alpha) * level;
        return level;
    }

    private static decimal Croston(decimal[] history, decimal alpha)
    {
        decimal? size = null;
        decimal? interval = null;
        var since = 1;
        foreach (var v in history)
        {
            if (v > 0)
            {
                size = size is { } s ? alpha * v + (1 - alpha) * s : v;
                interval = interval is { } p ? alpha * since + (1 - alpha) * p : since;
                since = 1;
            }
            else
            {
                since++;
            }
        }

        return size is { } z && interval is { } q && q > 0 ? z / q : 0;
    }

    private static decimal Median(IReadOnlyList<decimal> sorted) =>
        sorted.Count == 0 ? 0 : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;

    private static decimal StandardDeviation(IReadOnlyCollection<decimal> values)
    {
        var mean = values.Average();
        var variance = values.Sum(v => (v - mean) * (v - mean)) / Math.Max(1, values.Count - 1);
        return (decimal)Math.Sqrt((double)variance);
    }

    private static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Simulated monthly history for a part (owner decision 2026-11-02: no real history yet), the same every time for the same part and
    /// seed: a base level, a trend, a seasonal peak, noise, and for some parts sparse demand. Marked simulated wherever it is shown.
    /// </summary>
    public static IReadOnlyList<MonthlyDemand> Simulate(string partNumber, DateOnly firstMonth, int months, int seed)
    {
        ArgumentNullException.ThrowIfNull(partNumber);
        var rng = new SplitMix(unchecked((ulong)seed * 0x9E3779B97F4A7C15UL ^ Hash(partNumber)));
        var level = 20 + (decimal)(rng.Next() * 180);
        var trend = -0.01m + (decimal)(rng.Next() * 0.04);
        var amplitude = (decimal)(rng.Next() * 0.35);
        var peak = 1 + (int)(rng.Next() * 12);
        var sparse = rng.Next() < 0.2;
        var list = new List<MonthlyDemand>();
        for (var i = 0; i < months; i++)
        {
            var month = MonthOf(firstMonth).AddMonths(i);
            var season = 1 + amplitude * (decimal)Math.Cos(2 * Math.PI * (month.Month - peak) / 12.0);
            var noise = 1 + ((decimal)rng.Next() - 0.5m) * 0.3m;
            var quantity = level * (decimal)Math.Pow((double)(1 + trend), i) * season * noise;
            if (sparse) quantity = rng.Next() < 0.6 ? 0 : quantity * 2.5m;
            list.Add(new MonthlyDemand(month, Math.Max(0, Math.Round(quantity, 0, MidpointRounding.AwayFromZero))));
        }

        return list;
    }

    private static ulong Hash(string s)
    {
        var h = 14695981039346656037UL;
        foreach (var c in s) h = (h ^ c) * 1099511628211UL;
        return h;
    }

    /// <summary>A small deterministic generator (the same numbers for the same seed).</summary>
    private sealed class SplitMix(ulong state)
    {
        public double Next()
        {
            state += 0x9E3779B97F4A7C15UL;
            var z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (z >> 11) * (1.0 / (1UL << 53));
        }
    }
}
