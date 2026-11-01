namespace Forge.Workforce.Contracts;

// Timekeeping (owner decision 2026-10-31: DCAA-style timesheets). Every employee records every hour they work, each day, against a
// production order operation or a charge code; signs the week; a supervisor who is not the employee approves it. Entries are never
// rewritten: a correction is a new entry that supersedes the old one and carries its reason, and correcting a signed or approved week
// sends it back for signature and approval.

/// <summary>What a charge code collects. Direct production work is charged to the order itself, not to a code.</summary>
public enum ChargeCodeKind
{
    /// <summary>Direct work on a contract or CLIN that is not a production order (engineering, test, program support).</summary>
    Contract,

    /// <summary>Indirect manufacturing support (material handling, cleaning, shop meetings).</summary>
    Indirect,

    Overhead,

    GeneralAndAdministrative,

    /// <summary>Paid leave and holidays.</summary>
    Leave,

    /// <summary>Costs that may not be charged to government contracts.</summary>
    Unallowable,
}

/// <param name="ContractNumber">For a <see cref="ChargeCodeKind.Contract"/> code: the contract the hours are charged to.</param>
public sealed record ChargeCodeInfo(string Code, string Name, ChargeCodeKind Kind, string? ContractNumber, string? Clin, bool Active, string CreatedBy, DateTimeOffset CreatedAt);

public enum TimesheetStatus
{
    /// <summary>The employee is recording time.</summary>
    Open,

    /// <summary>The employee certified the week; waiting for approval.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720", Justification = "'Signed' is the timesheet term people use, not a type name.")]
    Signed,

    Approved,

    /// <summary>The approver sent it back with a reason; the employee corrects and signs again.</summary>
    Returned,
}

public enum TimeEntryState
{
    Active,

    /// <summary>Replaced by a correction; kept as recorded.</summary>
    Superseded,

    /// <summary>Corrected to no time; kept as recorded.</summary>
    Withdrawn,
}

/// <param name="OrderNumber">Direct production work: the order (and <paramref name="Sequence"/>, its operation).</param>
/// <param name="ChargeCode">Any other work: the charge code.</param>
/// <param name="LateReason">Why the time was not recorded on the day it was worked (or the next day).</param>
/// <param name="CorrectionReason">Why this entry corrects <paramref name="SupersedesEntryId"/>, or why it was withdrawn.</param>
public sealed record TimeEntryInfo(
    Guid EntryId,
    Guid TimesheetId,
    DateOnly WorkDate,
    long? OrderNumber,
    int? Sequence,
    string? ChargeCode,
    decimal Hours,
    string? Note,
    TimeEntryState State,
    string RecordedBy,
    DateTimeOffset RecordedAt,
    string? LateReason,
    Guid? SupersedesEntryId,
    string? CorrectionReason)
{
    public string Charge => OrderNumber is { } order
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Order {order} op {Sequence}")
        : ChargeCode ?? string.Empty;
}

public sealed record TimesheetInfo(
    Guid TimesheetId,
    Guid PersonId,
    DateOnly WeekStart,
    TimesheetStatus Status,
    string? SignedBy,
    DateTimeOffset? SignedAt,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    string? ReturnedBy,
    DateTimeOffset? ReturnedAt,
    string? ReturnReason,
    IReadOnlyList<TimeEntryInfo> Entries)
{
    public decimal Hours => Entries.Where(e => e.State == TimeEntryState.Active).Sum(e => e.Hours);

    /// <summary>The Monday that starts the week of a date.</summary>
    public static DateOnly WeekOf(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
}

public interface ITimesheetLookup
{
    Task<IReadOnlyList<ChargeCodeInfo>> ListChargeCodesAsync(CancellationToken ct);

    Task<ChargeCodeInfo?> FindChargeCodeAsync(string code, CancellationToken ct);

    Task<TimesheetInfo?> FindTimesheetAsync(Guid timesheetId, CancellationToken ct);

    Task<TimesheetInfo?> FindTimesheetAsync(Guid personId, DateOnly weekStart, CancellationToken ct);

    /// <summary>Timesheets of a week (every person), or of a person (every week), newest first.</summary>
    Task<IReadOnlyList<TimesheetInfo>> ListTimesheetsAsync(DateOnly? weekStart, Guid? personId, CancellationToken ct);

    /// <summary>Active entries charged to one order, on approved timesheets only when asked.</summary>
    Task<IReadOnlyList<TimeEntryInfo>> ListOrderEntriesAsync(long orderNumber, bool approvedOnly, CancellationToken ct);
}

/// <summary>
/// Checks that a production order operation can take time charges, inside the caller's transaction. Orders belong to Execution, which
/// Workforce does not depend on, so the host implements this. Returns null when the charge is valid, otherwise why it is not.
/// </summary>
public interface ITimeChargeResolver
{
    Task<string?> OrderChargeProblemAsync(Kernel.Access.IKernelUnitOfWork uow, Kernel.Security.ForgePrincipal principal, long orderNumber, int sequence, CancellationToken ct);
}
