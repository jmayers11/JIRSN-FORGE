namespace Forge.Maintenance.Contracts;

public enum DowntimeKind
{
    /// <summary>The machine went down unexpectedly. Open until restored.</summary>
    Unplanned,

    /// <summary>Scheduled preventive or corrective maintenance with a known window.</summary>
    Planned,

    /// <summary>
    /// A lock-out preventive-maintenance plan is overdue past its grace days. Open until the work is
    /// completed. Not recorded; derived from the work order, so it ends the moment the work is done.
    /// </summary>
    MaintenanceOverdue,
}

/// <summary>A period a machine cannot run. <see cref="EndsAt"/> is null while an unplanned outage is open.</summary>
public sealed record DowntimeWindow(long Id, string MachineCode, DowntimeKind Kind, DateTimeOffset StartsAt, DateTimeOffset? EndsAt, string Reason)
{
    public bool Covers(DateTimeOffset at) => StartsAt <= at && (EndsAt is null || at < EndsAt);
}

/// <summary>
/// A preventive-maintenance plan, due every <see cref="IntervalDays"/> calendar days and/or every
/// <see cref="IntervalRunHours"/> hours the machine actually ran, whichever comes first.
/// </summary>
public sealed record PmPlanInfo(
    long PlanId,
    string MachineCode,
    string Title,
    int? IntervalDays,
    int DurationMinutes,
    IReadOnlyList<string> Tasks,
    bool LockOutWhenOverdue,
    int GraceDays,
    bool Active,
    decimal? IntervalRunHours = null,
    decimal GraceRunHours = 0);

public enum WorkOrderKind
{
    Preventive,
    Corrective,
}

public enum WorkOrderStatus
{
    Open,
    Scheduled,
    InProgress,

    /// <summary>Done by a technician; waiting for a different person to sign it off.</summary>
    Completed,

    Closed,
    Cancelled,
}

public sealed record WorkOrderInfo(
    long WorkOrderId,
    string MachineCode,
    WorkOrderKind Kind,
    long? PlanId,
    string Title,
    string? Description,
    IReadOnlyList<string> Tasks,
    DateOnly? DueDate,
    WorkOrderStatus Status,
    DateTimeOffset? ScheduledStart,
    DateTimeOffset? ScheduledEnd,
    long? DowntimeId,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string? StartedBy,
    DateTimeOffset? StartedAt,
    string? CompletedBy,
    DateTimeOffset? CompletedAt,
    string? Findings,
    string? ClosedBy,
    DateTimeOffset? ClosedAt,
    string? CancelReason,
    decimal? DueRunHours = null,
    DateTimeOffset? RunHoursFrom = null)
{
    public bool IsOpen => Status is WorkOrderStatus.Open or WorkOrderStatus.Scheduled or WorkOrderStatus.InProgress or WorkOrderStatus.Completed;
}

/// <summary>
/// How long a machine actually ran, from shop-floor labor recorded on it. Maintenance owns this port so it
/// never depends on Execution; Execution implements it. Overlapping labor (a crew) counts once.
/// </summary>
public interface IMachineUsageLookup
{
    Task<decimal> RunHoursAsync(string machineCode, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    /// <summary>The instant the machine's run hours since <paramref name="from"/> reached <paramref name="hours"/>, or null if not yet (as of <paramref name="asOf"/>).</summary>
    Task<DateTimeOffset?> ReachedAsync(string machineCode, DateTimeOffset from, decimal hours, DateTimeOffset asOf, CancellationToken ct);
}

public interface IMaintenanceLookup
{
    /// <summary>Downtime overlapping [from, to), including open outages, sorted by machine then start.</summary>
    Task<IReadOnlyList<DowntimeWindow>> ListDowntimeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    Task<IReadOnlyList<PmPlanInfo>> ListPlansAsync(CancellationToken ct);

    /// <summary>Work orders, open ones first by due date; closed and cancelled only when asked.</summary>
    Task<IReadOnlyList<WorkOrderInfo>> ListWorkOrdersAsync(bool includeClosed, CancellationToken ct);

    Task<WorkOrderInfo?> FindWorkOrderAsync(long workOrderId, CancellationToken ct);
}
