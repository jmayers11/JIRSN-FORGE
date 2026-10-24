namespace Forge.Execution.Contracts;

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

/// <summary>Union and intersection of performance intervals; shared with Planning's workforce calculations.</summary>
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
