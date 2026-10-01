namespace Forge.Workforce.Contracts;

/// <summary>How presence was established. A shift schedule is never presence.</summary>
public enum PresenceKind
{
    /// <summary>From a timekeeping clock-in feed.</summary>
    ClockedIn,

    /// <summary>A supervisor confirmed the person is on site.</summary>
    SupervisorConfirmed,

    /// <summary>
    /// Engine-only: expected from the shift schedule, used for forecasts. Never recorded and never
    /// treated as evidence for current safety decisions.
    /// </summary>
    Projected,
}

/// <summary>Operational unavailability. Deliberately has no reason field.</summary>
public enum UnavailabilityKind
{
    CallOut,
    Unavailable,
    ApprovedPto,
    Training,
}

/// <summary>What a person is doing and where. The safety area is always set; narrower scopes are optional.</summary>
public sealed record WorkAssignment(string SafetyAreaCode, string? WorkCenterCode, string? MachineCode, string ActivityCode, bool AsTrainee);

/// <summary>
/// A worker's operational state at one instant: whether presence evidence is current, whether they
/// are unavailable, and their assignment. Planning decides what that means; Workforce only reports it.
/// </summary>
public sealed record WorkerState(
    Guid PersonId,
    string DisplayName,
    string HomeWorkCenterCode,
    bool IsActive,
    PresenceKind? PresenceKind,
    DateTimeOffset? PresentUntil,
    UnavailabilityKind? Unavailability,
    WorkAssignment? Assignment,
    string? ShiftCode = null);

/// <summary>A period a worker is not available. There is deliberately no reason.</summary>
public sealed record UnavailabilityWindow(Guid PersonId, UnavailabilityKind Kind, DateTimeOffset StartsAt, DateTimeOffset EndsAt);

public interface IWorkforceLookup
{
    Task<bool> WorkerExistsAsync(Guid personId, CancellationToken ct);

    /// <summary>Every worker's state at the instant, sorted by person id.</summary>
    Task<IReadOnlyList<WorkerState>> GetWorkerStatesAsync(DateTimeOffset at, CancellationToken ct);

    /// <summary>Unavailability overlapping [from, to), sorted by person then start.</summary>
    Task<IReadOnlyList<UnavailabilityWindow>> ListUnavailabilityAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
}
