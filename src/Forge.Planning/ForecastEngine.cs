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

    /// <summary>Level and a growth (or decline) trend that flattens out over time; weights fitted to the history.</summary>
    DampedTrend,

    /// <summary>Holt-Winters: level, a damped trend and a seasonal pattern together (needs two years of history).</summary>
    HoltWinters,

    /// <summary>Croston's method with the Syntetos-Boylan correction, which removes Croston's tendency to run high.</summary>
    SyntetosBoylan,
}

/// <summary>One month of demand history.</summary>
public sealed record MonthlyDemand(DateOnly Month, decimal Quantity);

/// <summary>One forecast month with a likely range.</summary>
/// <param name="Statistical">What the method forecast before planner adjustments; null when nothing was adjusted.</param>
/// <param name="Adjusted">What planner adjustments changed this month, and why.</param>
public sealed record ForecastMonth(DateOnly Month, decimal Forecast, decimal Low, decimal High, decimal? Statistical = null, string? Adjusted = null);

/// <summary>How a method did when tested on the held-out months: mean absolute error, weighted percentage error and bias (+ over-forecasts).</summary>
public sealed record BacktestScore(ForecastMethod Method, decimal MeanAbsoluteError, decimal? WeightedPercentError, decimal Bias, int Months);

/// <summary>A warning about the history behind a forecast.</summary>
public sealed record HistoryFlag(string Code, string Message);

/// <summary>The forecast for one part: the method that tested best, every method's score, the months ahead and what to be careful of.</summary>
/// <param name="Basis">What the forecast rests on when it is not the part's own history: a like item, or its family.</param>
public sealed record PartForecast(string PartNumber, ForecastMethod Method, IReadOnlyList<BacktestScore> Scores, IReadOnlyList<ForecastMonth> Months, IReadOnlyList<HistoryFlag> Flags,
    int HistoryMonths, decimal AverageMonthly, string? Basis = null);

/// <summary>
/// Monthly demand forecasting (owner decisions 2026-11-02). Each part's history is checked (gaps, one-off spikes, short history, sparse
/// demand); every method is tested by forecasting the held-out last months one step at a time from what came before; the method with the
/// lowest mean absolute error wins, and a simpler method is preferred on ties. The likely range comes from that method's test errors.
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
            flags.Add(new("intermittent", string.Create(CultureInfo.InvariantCulture, $"{zeros} of {series.Count} months had no demand; sparse-demand methods (Croston, Syntetos-Boylan) are tried.")));
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

        if (series.Count >= 12)
        {
            // A clear, steady change between the last two halves of the history: say so, since naive methods lag it.
            var half = Math.Min(12, series.Count / 2);
            var recent = series.TakeLast(half).Average(s => s.Quantity);
            var before = series.SkipLast(half).TakeLast(half).Average(s => s.Quantity);
            if (before > 0 && Math.Abs(recent - before) / before >= 0.25m)
                flags.Add(new("trend", string.Create(CultureInfo.InvariantCulture,
                    $"Demand {(recent > before ? "grew" : "fell")} {Math.Abs(recent - before) / before * 100m:0} % between the last two {half}-month spans; trend methods are tried.")));
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
        if (values.Length >= 8) methods.Add(ForecastMethod.DampedTrend);
        if (values.Length >= 26) methods.Add(ForecastMethod.HoltWinters);
        if (values.Count(v => v == 0) * 3 >= values.Length)
        {
            methods.Add(ForecastMethod.Croston);
            methods.Add(ForecastMethod.SyntetosBoylan);
        }

        var test = Math.Min(holdout, Math.Max(0, values.Length - 3));
        var scores = new List<(BacktestScore Score, decimal[] Errors)>();
        foreach (var method in methods)
        {
            var errors = new List<decimal>();
            var actual = 0m;
            for (var t = values.Length - test; t < values.Length; t++)
            {
                if (method == ForecastMethod.SeasonalNaive && t < 12) continue;
                if (method == ForecastMethod.HoltWinters && t < 24) continue;
                var predicted = Math.Max(0, Next(method, values[..t]));
                errors.Add(predicted - values[t]);
                actual += values[t];
            }

            if (errors.Count == 0) continue;
            var mae = errors.Average(Math.Abs);
            scores.Add((new BacktestScore(method, Round(mae), actual > 0 ? Round(errors.Sum(Math.Abs) / actual * 100m) : null, Round(errors.Average()), errors.Count), errors.ToArray()));
        }

        // Ties go to the simpler method.
        var best = scores.Count == 0
            ? (Score: new BacktestScore(ForecastMethod.MovingAverage3, 0, null, 0, 0), Errors: Array.Empty<decimal>())
            : scores.OrderBy(s => s.Score.MeanAbsoluteError).ThenBy(s => Complexity(s.Score.Method)).First();
        var spread = best.Errors.Length >= 2 ? StandardDeviation(best.Errors) : values.Length >= 2 ? StandardDeviation(values) : 0m;
        var z = intervalPercent switch { >= 95 => 1.96m, >= 90 => 1.645m, >= 80 => 1.2816m, _ => 1m };
        var months = new List<ForecastMonth>();
        var path = Ahead(best.Score.Method, values, horizon);
        for (var h = 1; h <= horizon; h++)
        {
            var f = Math.Max(0, path[h - 1]);
            var width = z * spread * (decimal)Math.Sqrt(h);
            months.Add(new ForecastMonth(nextMonth.AddMonths(h - 1), Round(f), Round(Math.Max(0, f - width)), Round(f + width)));
        }

        return new PartForecast(partNumber, best.Score.Method, scores.Select(s => s.Score).OrderBy(s => s.MeanAbsoluteError).ToList(), months, flags, values.Length, Round(values.Average()));
    }

    private static int Complexity(ForecastMethod method) => method switch
    {
        ForecastMethod.Naive => 0,
        ForecastMethod.SeasonalNaive => 1,
        ForecastMethod.MovingAverage3 => 2,
        ForecastMethod.ExponentialSmoothing => 3,
        ForecastMethod.Croston => 4,
        ForecastMethod.SyntetosBoylan => 5,
        ForecastMethod.DampedTrend => 6,
        _ => 7,
    };

    /// <summary>The forecasts for the next <paramref name="horizon"/> months. Trend and seasonal methods project directly; the others repeat their one-step forecast.</summary>
    public static decimal[] Ahead(ForecastMethod method, decimal[] history, int horizon)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (method is ForecastMethod.DampedTrend or ForecastMethod.HoltWinters && history.Length > 0)
        {
            var fit = method == ForecastMethod.HoltWinters ? FitHoltWinters(history) : FitDamped(history);
            return Enumerable.Range(1, horizon).Select(h => fit.Project(h)).ToArray();
        }

        var path = history.ToList();
        var list = new decimal[horizon];
        for (var h = 0; h < horizon; h++)
        {
            list[h] = Math.Max(0, Next(method, [.. path]));
            path.Add(list[h]);
        }

        return list;
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
            ForecastMethod.SyntetosBoylan => Croston(history, 0.1m) * (1 - 0.1m / 2),
            ForecastMethod.DampedTrend => FitDamped(history).Project(1),
            ForecastMethod.HoltWinters => FitHoltWinters(history).Project(1),
            _ => history[^1],
        };
    }

    /// <summary>A fitted trend (and season) model: level, trend, damping and the seasonal offsets, ready to project.</summary>
    private sealed record Fitted(decimal Level, decimal Trend, decimal Phi, decimal[] Season, int Next)
    {
        public decimal Project(int h)
        {
            var damp = 0m;
            var p = 1m;
            for (var i = 1; i <= h; i++)
            {
                p *= Phi;
                damp += p;
            }

            var season = Season.Length == 0 ? 0 : Season[(Next + h - 1) % Season.Length];
            return Level + damp * Trend + season;
        }
    }

    private static readonly decimal[] Alphas = [0.1m, 0.2m, 0.3m, 0.5m, 0.7m, 0.9m];
    private static readonly decimal[] TrendAlphas = [0.2m, 0.4m, 0.6m, 0.8m];
    private static readonly decimal[] Betas = [0.05m, 0.1m, 0.2m];
    private static readonly decimal[] Phis = [0.8m, 0.9m, 0.98m];
    private static readonly decimal[] SeasonAlphas = [0.1m, 0.3m, 0.5m];
    private static readonly decimal[] Gammas = [0.05m, 0.2m, 0.4m];

    /// <summary>Holt's linear method with a damped trend; the weights that forecast the history best one step ahead are kept.</summary>
    private static Fitted FitDamped(decimal[] y)
    {
        if (y.Length < 3) return new Fitted(y[^1], 0, 0.9m, [], 0);
        Fitted? best = null;
        var bestError = decimal.MaxValue;
        foreach (var a in TrendAlphas)
            foreach (var b in Betas)
                foreach (var phi in Phis)
                {
                    var level = y[0];
                    var trend = y[1] - y[0];
                    var error = 0m;
                    for (var t = 1; t < y.Length; t++)
                    {
                        var forecast = level + phi * trend;
                        error += Math.Abs(y[t] - forecast);
                        var next = a * y[t] + (1 - a) * forecast;
                        trend = b * (next - level) + (1 - b) * phi * trend;
                        level = next;
                    }

                    if (error < bestError)
                    {
                        bestError = error;
                        best = new Fitted(level, trend, phi, [], 0);
                    }
                }

        return best!;
    }

    /// <summary>Additive Holt-Winters with a damped trend over a 12-month season (additive, so months with no demand are fine).</summary>
    private static Fitted FitHoltWinters(decimal[] y)
    {
        const int m = 12;
        if (y.Length < 2 * m) return FitDamped(y);
        var first = y[..m].Average();
        var second = y[m..(2 * m)].Average();
        Fitted? best = null;
        var bestError = decimal.MaxValue;
        foreach (var a in SeasonAlphas)
            foreach (var b in Betas)
                foreach (var g in Gammas)
                    foreach (var phi in Phis)
                    {
                        // Start from the first year with its trend taken out, so the seasonal offsets are not mistaken for growth.
                        var trend = (second - first) / m;
                        var level = first + trend * (m - 1) / 2m;
                        var season = y[..m].Select((v, i) => v - (first + trend * (i - (m - 1) / 2m))).ToArray();
                        var error = 0m;
                        for (var t = m; t < y.Length; t++)
                        {
                            var s = season[t % m];
                            var forecast = level + phi * trend + s;
                            error += Math.Abs(y[t] - forecast);
                            var next = a * (y[t] - s) + (1 - a) * (level + phi * trend);
                            trend = b * (next - level) + (1 - b) * phi * trend;
                            season[t % m] = g * (y[t] - next) + (1 - g) * s;
                            level = next;
                        }

                        if (error < bestError)
                        {
                            bestError = error;
                            best = new Fitted(level, trend, phi, season, y.Length % m);
                        }
                    }

        return best!;
    }

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
