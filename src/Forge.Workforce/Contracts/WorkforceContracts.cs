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

/// <summary>The person a badge identifies. A badge says who someone is, never what they are doing.</summary>
public sealed record BadgeHolder(Guid PersonId, string DisplayName, bool IsWorker, bool HasPin);

/// <summary>Paid time on site (clock in to clock out). Not direct labor and not presence evidence on its own.</summary>
public sealed record AttendanceRecord(long AttendanceId, Guid PersonId, DateTimeOffset ClockInAt, DateTimeOffset? ClockOutAt);

public interface IWorkforceLookup
{
    /// <summary>The active badge's holder, or null for an unknown or revoked badge.</summary>
    Task<BadgeHolder?> FindBadgeHolderAsync(string badge, CancellationToken ct);

    /// <summary>True only if the badge is active, has a PIN and the PIN matches.</summary>
    Task<bool> VerifyBadgePinAsync(string badge, string pin, CancellationToken ct);

    /// <summary>The person's open attendance (clocked in, not yet out), if any.</summary>
    Task<AttendanceRecord?> FindOpenAttendanceAsync(Guid personId, CancellationToken ct);

    /// <summary>Attendance overlapping [from, to), sorted by clock-in.</summary>
    Task<IReadOnlyList<AttendanceRecord>> ListAttendanceAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    Task<bool> WorkerExistsAsync(Guid personId, CancellationToken ct);

    /// <summary>Every worker's state at the instant, sorted by person id.</summary>
    Task<IReadOnlyList<WorkerState>> GetWorkerStatesAsync(DateTimeOffset at, CancellationToken ct);

    /// <summary>Unavailability overlapping [from, to), sorted by person then start.</summary>
    Task<IReadOnlyList<UnavailabilityWindow>> ListUnavailabilityAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
}
