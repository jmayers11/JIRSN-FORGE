namespace Forge.Execution.Contracts;

/// <summary>What a reason code explains (V3.5).</summary>
public enum ReasonKind
{
    Downtime,
    Scrap,
    Rework,
}

/// <summary>Why a machine was not running (V3.5, V3 brief L). Each stoppage has exactly one.</summary>
public enum StoppageCategory
{
    /// <summary>Scheduled not to run: breaks, meetings, no shift demand.</summary>
    Planned,

    /// <summary>Stopped when it was meant to run, for a reason not covered below.</summary>
    Unplanned,

    /// <summary>First preparation of the machine for an operation.</summary>
    Setup,

    /// <summary>Switching the machine from one product or operation to the next.</summary>
    Changeover,

    /// <summary>Being maintained or repaired.</summary>
    Maintenance,

    /// <summary>Ready but waiting: for an operator, an instruction, an inspection, a decision.</summary>
    Waiting,

    /// <summary>Nothing to run: upstream material or work did not arrive.</summary>
    Starved,

    /// <summary>Cannot hand work on: downstream is full or not taking it.</summary>
    Blocked,
}

/// <summary>A reason in the plant's own words. A reason code says what was reported, never a proven root cause.</summary>
/// <param name="Category">For downtime reasons, the stoppage category the reason belongs to; null otherwise.</param>
public sealed record ReasonCodeInfo(string Code, ReasonKind Kind, StoppageCategory? Category, string Description, bool Active, DateTimeOffset ChangedAt, string ChangedBy);

/// <summary>A root cause proposed for a stoppage with its evidence; proven only once a different person confirms it.</summary>
public sealed record RootCauseInfo(long RootCauseId, long StoppageId, string Cause, string Evidence, DateTimeOffset ProposedAt, string ProposedBy, DateTimeOffset? ConfirmedAt, string? ConfirmedBy)
{
    public bool Proven => ConfirmedAt is not null;
}

/// <summary>
/// A period a machine did not run (V3.5). Setup and changeover stoppages name the order operation they prepare and
/// carry the minutes the routing expected, so the actual changeover can be compared with it.
/// </summary>
public sealed record StoppageInfo(
    long StoppageId,
    string MachineCode,
    string WorkCenterCode,
    StoppageCategory Category,
    string ReasonCode,
    long? OrderNumber,
    int? Sequence,
    decimal? ExpectedMinutes,
    string? Note,
    DateTimeOffset StartedAt,
    string StartedBy,
    DateTimeOffset? EndedAt,
    string? EndedBy)
{
    public bool Open => EndedAt is null;

    /// <summary>Minutes stopped, up to <paramref name="now"/> while still open.</summary>
    public decimal Minutes(DateTimeOffset now) => Math.Round((decimal)((EndedAt ?? now) - StartedAt).TotalMinutes, 1);

    /// <summary>Actual minus expected, for setups and changeovers that have ended.</summary>
    public decimal? Variance(DateTimeOffset now) => ExpectedMinutes is { } expected && EndedAt is not null ? Minutes(now) - expected : null;
}

public interface IProductionActualsLookup
{
    Task<IReadOnlyList<ReasonCodeInfo>> ListReasonCodesAsync(CancellationToken ct);

    /// <summary>Stoppages overlapping [from, to), oldest first; or every open one when <paramref name="openOnly"/>.</summary>
    Task<IReadOnlyList<StoppageInfo>> ListStoppagesAsync(DateTimeOffset from, DateTimeOffset to, bool openOnly, CancellationToken ct);

    Task<StoppageInfo?> FindStoppageAsync(long stoppageId, CancellationToken ct);

    /// <summary>Root causes of the given stoppages (all when null), oldest first.</summary>
    Task<IReadOnlyList<RootCauseInfo>> ListRootCausesAsync(IReadOnlyCollection<long>? stoppageIds, CancellationToken ct);
}

public interface IProductionActualsStore
{
    Task<bool> InsertReasonCodeAsync(ReasonCodeInfo code, CancellationToken ct);

    Task UpdateReasonCodeAsync(ReasonCodeInfo code, CancellationToken ct);

    /// <returns>The new stoppage's id, or null when the machine already has an open stoppage.</returns>
    Task<long?> InsertStoppageAsync(StoppageInfo stoppage, CancellationToken ct);

    Task EndStoppageAsync(long stoppageId, DateTimeOffset at, string actor, CancellationToken ct);

    Task<long> InsertRootCauseAsync(RootCauseInfo cause, CancellationToken ct);

    Task ConfirmRootCauseAsync(long rootCauseId, DateTimeOffset at, string actor, CancellationToken ct);
}
