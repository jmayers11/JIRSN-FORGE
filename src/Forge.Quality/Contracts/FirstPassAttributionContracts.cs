namespace Forge.Quality.Contracts;

/// <summary>What caused a first-pass result, as Quality decided it (Sprint 10 §16.2). Unknown and mixed causes never default to the operator.</summary>
public enum DefectCause
{
    EmployeeControlled,
    Machine,
    IncomingMaterial,
    UpstreamProcess,
    Unknown,
    Mixed,
}

/// <summary>An attribution counts once a different, authorized person verified it; a correction supersedes it and both stay in history.</summary>
public enum AttributionState
{
    Proposed,
    Verified,
    Rejected,
    Superseded,
}

/// <param name="InspectionId">The first-pass inspection the attribution explains. The inspection itself is never changed.</param>
/// <param name="EmployeeOpportunity">Whether the operator had a meaningful, controllable opportunity to affect the result.</param>
/// <param name="FailedQuantity">For a failed inspection, how many of the inspected units failed; null means all of them.</param>
public sealed record FirstPassAttribution(
    long AttributionId,
    long InspectionId,
    DefectCause Cause,
    bool EmployeeOpportunity,
    decimal? FailedQuantity,
    string Evidence,
    AttributionState State,
    string ProposedBy,
    DateTimeOffset ProposedAt,
    string? ReviewedBy,
    DateTimeOffset? ReviewedAt,
    long? SupersedesAttributionId);

public interface IFirstPassAttributionLookup
{
    /// <summary>All attributions of the inspections, every state, oldest first.</summary>
    Task<IReadOnlyList<FirstPassAttribution>> ListAttributionsAsync(IReadOnlyCollection<long> inspectionIds, CancellationToken ct);

    Task<FirstPassAttribution?> FindAttributionAsync(long attributionId, CancellationToken ct);
}
