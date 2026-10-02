using Forge.Planning.Engines;

namespace Forge.Planning;

public enum ScheduleStatus
{
    Proposed,
    Approved,
    Superseded,
    Discarded,
}

public sealed record ScheduleVersion(
    long VersionId,
    ScheduleStatus Status,
    DateTimeOffset From,
    DateTimeOffset To,
    string EngineVersion,
    string InputFingerprint,
    string OutputFingerprint,
    string Summary,
    string ProposedBy,
    DateTimeOffset ProposedAt,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    ScheduleResult? Result);

public enum ConstraintStatus
{
    /// <summary>Found by a schedule run; nobody has picked it up yet.</summary>
    Open,

    /// <summary>Someone owns it and is investigating.</summary>
    Investigating,

    /// <summary>No longer present (by a later run) or resolved by a person; awaiting verification.</summary>
    Resolved,

    /// <summary>A person confirmed the resolution.</summary>
    Verified,
}

public sealed record ConstraintRecord(
    long ConstraintId,
    string Key,
    string Category,
    string Subject,
    ConstraintStatus Status,
    string Finding,
    string Evidence,
    IReadOnlyList<long> AffectedOrders,
    IReadOnlyList<string> AffectedOperations,
    IReadOnlyList<string> Alternatives,
    DateTimeOffset WindowFrom,
    DateTimeOffset WindowUntil,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    string? Owner,
    string? Resolution,
    string? ResolvedBy,
    string? VerifiedBy,
    int ReopenedCount,
    DateTimeOffset? ResolvedAt = null);

public interface IPlanningStore
{
    Task<ScheduleVersion?> FindVersionAsync(long versionId, bool includeResult, CancellationToken ct);

    Task<ScheduleVersion?> LatestAsync(ScheduleStatus status, bool includeResult, CancellationToken ct);

    /// <summary>The schedule that was approved and in force at an instant (approved at or before it, latest first).</summary>
    Task<ScheduleVersion?> ApprovedAsOfAsync(DateTimeOffset at, CancellationToken ct);

    /// <summary>Schedules approved after <paramref name="after"/> and at or before <paramref name="upTo"/>, oldest first, without results.</summary>
    Task<IReadOnlyList<ScheduleVersion>> ListApprovedBetweenAsync(DateTimeOffset after, DateTimeOffset upTo, CancellationToken ct);

    Task<long> InsertProposalAsync(ScheduleVersion version, string resultJson, CancellationToken ct);

    Task SetStatusAsync(long versionId, ScheduleStatus status, string? approvedBy, DateTimeOffset? approvedAt, CancellationToken ct);

    Task<IReadOnlyList<ConstraintRecord>> ListConstraintsAsync(bool includeClosed, CancellationToken ct);

    Task<ConstraintRecord?> FindConstraintAsync(long constraintId, CancellationToken ct);

    Task UpsertFindingAsync(ConstraintFinding finding, long versionId, DateTimeOffset at, CancellationToken ct);

    /// <returns>Findings closed because a run no longer found them.</returns>
    Task<int> ResolveMissingAsync(IReadOnlyCollection<string> presentKeys, long versionId, DateTimeOffset at, CancellationToken ct);

    Task UpdateLifecycleAsync(long constraintId, ConstraintStatus status, string? owner, string? resolution, string? resolvedBy, DateTimeOffset? resolvedAt, string? verifiedBy, CancellationToken ct);
}
