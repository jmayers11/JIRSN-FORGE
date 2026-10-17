namespace Forge.Simulation.Tests.Engine;

/// <summary>
/// The simulation's only source of time. Injected as the host's <see cref="TimeProvider"/> so every service sees
/// simulated time. It starts at a fixed hour and only moves forward when the simulation advances it, so a run never
/// depends on how long the machine takes. Event logs record offsets from the start, never wall-clock times.
/// </summary>
public sealed class SimClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset now = start;

    public DateTimeOffset Start { get; } = start;

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

    /// <summary>A start anchored to the current UTC hour, so dated rules (due dates, expiry) behave as in production.</summary>
    public static SimClock AtCurrentHour()
    {
        var utc = DateTimeOffset.UtcNow;
        return new SimClock(new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero));
    }
}
