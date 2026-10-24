namespace Forge.Workforce.Contracts;

public enum CoverageState
{
    Planned,
    Started,
    Completed,
    Cancelled,
}

public enum CoverageGoalKind
{
    Original,
    Revision,
    TimeAdjusted,
}

/// <summary>A goal version (Sprint 10 §22.1). Goals are never rewritten; a revision after work began is marked as such.</summary>
public sealed record CoverageGoal(long GoalId, Guid AssignmentId, int Version, CoverageGoalKind Kind, decimal GoodUnits, int PlannedProductiveMinutes,
    string Rationale, bool AfterStart, string SetBy, DateTimeOffset SetAt);

/// <summary>
/// A temporary assignment outside the worker's usual work (§22.1), with the capacity forecast shown when it was approved. Forecast, goal
/// and actual output are three different things; actual output is read from production reports, never stored here.
/// </summary>
public sealed record CoverageAssignment(
    Guid AssignmentId,
    Guid PersonId,
    string? OriginWorkCenter,
    string DestinationWorkCenter,
    string? MachineCode,
    string ActivityCode,
    long? OrderNumber,
    int? Sequence,
    DateTimeOffset PlannedStart,
    DateTimeOffset PlannedEnd,
    DateTimeOffset? ActualStart,
    DateTimeOffset? ActualEnd,
    string Reason,
    decimal? ForecastConservative,
    decimal? ForecastExpected,
    decimal? ForecastOptimistic,
    string? ForecastConfidence,
    string? ForecastBasis,
    CoverageState State,
    bool Comparable,
    string? ExclusionReason,
    string? ReviewedBy,
    DateTimeOffset? ReviewedAt,
    string ApprovedBy,
    DateTimeOffset ApprovedAt);

public interface ICoverageLookup
{
    Task<IReadOnlyList<CoverageAssignment>> ListCoverageAsync(Guid? personId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    Task<CoverageAssignment?> FindCoverageAsync(Guid assignmentId, CancellationToken ct);

    Task<IReadOnlyList<CoverageGoal>> ListCoverageGoalsAsync(IReadOnlyCollection<Guid> assignmentIds, CancellationToken ct);
}
