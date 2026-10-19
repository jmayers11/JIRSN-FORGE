namespace Forge.Simulation.Tests.Engine;

/// <summary>
/// The simulation's only source of time. Injected as the host's <see cref="TimeProvider"/> so every service sees
/// simulated time. It starts at a fixed hour and only moves forward when the simulation advances it, so a run never
/// depends on how long the machine takes. Event logs record offsets from the start, never wall-clock times.
/// </summary>
public sealed class SimClock(DateTimeOffset start) : TimeProvider
{
    // Always UTC: Forge stores what TimeProvider.GetUtcNow() answers, and the database accepts only offset 0.
    private DateTimeOffset now = start.ToUniversalTime();

    public DateTimeOffset Start { get; } = start.ToUniversalTime();

    public TimeSpan Elapsed => now - Start;

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan by)
    {
        if (by < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(by), "Simulated time only moves forward.");
        }

        now += by;
    }

    /// <summary>The next Monday at 00:00 UTC, so a factory run's weeks line up with its Monday-to-Friday shifts.</summary>
    public static SimClock NextMonday()
    {
        var today = DateTimeOffset.UtcNow.Date;
        var days = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
        return new SimClock(new DateTimeOffset(today.AddDays(days == 0 ? 7 : days), TimeSpan.Zero));
    }

    /// <summary>A start anchored to the current UTC hour, so dated rules (due dates, expiry) behave as in production.</summary>
    public static SimClock AtCurrentHour()
    {
        var utc = DateTimeOffset.UtcNow;
        return new SimClock(new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero));
    }
}
