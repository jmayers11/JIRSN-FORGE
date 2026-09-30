namespace Forge.Workforce.Contracts;

/// <summary>How presence was established. A shift schedule is never presence.</summary>
public enum PresenceKind
{
    /// <summary>From a timekeeping clock-in feed.</summary>
    ClockedIn,

    /// <summary>A supervisor confirmed the person is on site.</summary>
    SupervisorConfirmed,
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
    WorkAssignment? Assignment);

public interface IWorkforceLookup
{
    Task<bool> WorkerExistsAsync(Guid personId, CancellationToken ct);

    /// <summary>Every worker's state at the instant, sorted by person id.</summary>
    Task<IReadOnlyList<WorkerState>> GetWorkerStatesAsync(DateTimeOffset at, CancellationToken ct);
}
