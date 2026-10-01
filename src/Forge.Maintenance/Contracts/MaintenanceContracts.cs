namespace Forge.Maintenance.Contracts;

public enum DowntimeKind
{
    /// <summary>The machine went down unexpectedly. Open until restored.</summary>
    Unplanned,

    /// <summary>Scheduled preventive or corrective maintenance with a known window.</summary>
    Planned,
}

/// <summary>A period a machine cannot run. <see cref="EndsAt"/> is null while an unplanned outage is open.</summary>
public sealed record DowntimeWindow(long Id, string MachineCode, DowntimeKind Kind, DateTimeOffset StartsAt, DateTimeOffset? EndsAt, string Reason)
{
    public bool Covers(DateTimeOffset at) => StartsAt <= at && (EndsAt is null || at < EndsAt);
}

public interface IMaintenanceLookup
{
    /// <summary>Downtime overlapping [from, to), including open outages, sorted by machine then start.</summary>
    Task<IReadOnlyList<DowntimeWindow>> ListDowntimeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
}
