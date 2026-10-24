namespace Forge.Host.Demo;

/// <summary>
/// The simulated demo clock (Sprint 9): real time plus a forward-only offset held by the designated demo database. Registered only
/// when the host runs with Forge:Demo:Enabled, so a production host always uses the system clock. Elapsed-time measurement
/// (<see cref="TimeProvider.GetTimestamp"/>) stays real.
/// </summary>
public sealed class DemoClock(TimeProvider inner) : TimeProvider
{
    private long offsetTicks;

    public TimeSpan Offset => TimeSpan.FromTicks(Volatile.Read(ref offsetTicks));

    public DateTimeOffset RealUtcNow => inner.GetUtcNow();

    public void SetOffset(TimeSpan offset) => Volatile.Write(ref offsetTicks, offset < TimeSpan.Zero ? 0 : offset.Ticks);

    public override DateTimeOffset GetUtcNow() => inner.GetUtcNow() + Offset;

    public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

    public override long TimestampFrequency => inner.TimestampFrequency;

    public override long GetTimestamp() => inner.GetTimestamp();
}

/// <summary>Host settings for the demo tenant. <see cref="ConnectionString"/> is the host's own; it is never returned by any API.</summary>
public sealed record DemoOptions(bool Enabled, string ConnectionString, string MaintenanceDatabase = "postgres");
