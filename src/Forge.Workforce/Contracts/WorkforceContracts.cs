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
    string? ShiftCode = null,
    string? JobTitle = null,
    bool HasPhoto = false,
    string? Department = null);

public sealed record WorkerPhotoInfo(string Sha256, string ContentType);

/// <summary>A period a worker is not available. There is deliberately no reason.</summary>
public sealed record UnavailabilityWindow(Guid PersonId, UnavailabilityKind Kind, DateTimeOffset StartsAt, DateTimeOffset EndsAt);

/// <summary>The person a badge identifies. A badge says who someone is, never what they are doing.</summary>
public sealed record BadgeHolder(Guid PersonId, string DisplayName, bool IsWorker, bool HasPin);

/// <summary>Paid time on site (clock in to clock out). Not direct labor and not presence evidence on its own.</summary>
public sealed record AttendanceRecord(long AttendanceId, Guid PersonId, DateTimeOffset ClockInAt, DateTimeOffset? ClockOutAt);

/// <summary>How a temporary move stands (ADR 0057 §5).</summary>
public enum TemporaryMoveStatus
{
    /// <summary>In force until its estimated end date, unless extended or ended.</summary>
    Active,

    /// <summary>Ended before its estimated end date.</summary>
    EndedEarly,

    /// <summary>Nobody extended it by its end date: the worker is treated as back home from the day after.</summary>
    Returned,
}

/// <summary>An extension or renewal of a temporary move: the new end date and why.</summary>
/// <remarks>Why is in the audit trail with the command; workforce records keep no reasons (Spec 78).</remarks>
public sealed record TemporaryMoveChange(int ChangeNumber, string Kind, DateOnly PreviousEndsOn, DateOnly EndsOn, DateTimeOffset ChangedAt, string ChangedBy);

/// <summary>A temporary move (loan) of a worker to another work center, in site dates, end date inclusive.</summary>
public sealed record TemporaryMoveInfo(
    long MoveId,
    Guid PersonId,
    string DisplayName,
    string FromWorkCenter,
    string ToWorkCenter,
    DateOnly StartsOn,
    DateOnly EndsOn,
    TemporaryMoveStatus Status,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateOnly? RemindedFor,
    DateTimeOffset? ClosedAt,
    string? ClosedBy,
    IReadOnlyList<TemporaryMoveChange> Changes);

/// <summary>A transfer: the worker's home work center from a date (ADR 0057 §5). Earlier homes are kept.</summary>
public sealed record HomeTransfer(long TransferId, Guid PersonId, string FromWorkCenter, string WorkCenter, DateOnly EffectiveFrom, DateTimeOffset RecordedAt, string RecordedBy);

/// <summary>
/// Where planning counts a worker over site dates (ADR 0057 §5): their home, changed by transfers, with temporary
/// moves on top. <see cref="Through"/> is inclusive; null means open-ended.
/// </summary>
public sealed record WorkCenterPeriod(Guid PersonId, string WorkCenterCode, DateOnly From, DateOnly? Through, string Kind, long? MoveId = null)
{
    public bool Covers(DateOnly day) => From <= day && (Through is null || day <= Through);
}

public interface IWorkforceLookup
{
    /// <summary>The active badge's holder, or null for an unknown or revoked badge.</summary>
    Task<BadgeHolder?> FindBadgeHolderAsync(string badge, CancellationToken ct);

    /// <summary>The person's open attendance (clocked in, not yet out), if any.</summary>
    Task<AttendanceRecord?> FindOpenAttendanceAsync(Guid personId, CancellationToken ct);

    /// <summary>Attendance overlapping [from, to), sorted by clock-in.</summary>
    Task<IReadOnlyList<AttendanceRecord>> ListAttendanceAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    Task<bool> WorkerExistsAsync(Guid personId, CancellationToken ct);
    Task<WorkerPhotoInfo?> FindWorkerPhotoAsync(Guid personId, CancellationToken ct);

    /// <summary>Every worker's state at the instant, sorted by person id.</summary>
    Task<IReadOnlyList<WorkerState>> GetWorkerStatesAsync(DateTimeOffset at, CancellationToken ct);

    /// <summary>Unavailability overlapping [from, to), sorted by person then start.</summary>
    Task<IReadOnlyList<UnavailabilityWindow>> ListUnavailabilityAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    /// <summary>Temporary moves (of one person, or all), newest first; only active ones unless asked.</summary>
    Task<IReadOnlyList<TemporaryMoveInfo>> ListTemporaryMovesAsync(Guid? personId, bool includeClosed, CancellationToken ct);

    /// <summary>Transfers (of one person, or all), oldest first.</summary>
    Task<IReadOnlyList<HomeTransfer>> ListTransfersAsync(Guid? personId, CancellationToken ct);

    /// <summary>
    /// Every worker's home periods (registration home, then each transfer) and temporary moves that are not ended
    /// early, sorted by person then start. A move ended early counts through the day it was ended.
    /// </summary>
    Task<IReadOnlyList<WorkCenterPeriod>> ListWorkCenterPeriodsAsync(CancellationToken ct);
}
