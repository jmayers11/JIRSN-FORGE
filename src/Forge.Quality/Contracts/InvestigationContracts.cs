using Forge.Kernel.Access;
using Forge.Kernel.Holds;
using Forge.Kernel.Security;

namespace Forge.Quality.Contracts;

public enum MeasurementFindingState { Draft, Submitted, Approved, Withdrawn }
public enum TraceInvestigationState { Open, Closed }
public enum TraceTriggerKind { SupplierLot, MeasurementFinding }
public enum TraceImpactDecision { PotentiallyAffected, NotAffected, Undetermined }
public enum TraceAssessmentState { Proposed, Approved, Withdrawn }
public sealed record MeasurementAffectedRange(string? CharacteristicName,string? Unit,decimal? From,decimal? To,string Basis);
public sealed record MeasurementFindingContent(string ToolCode,long? CalibrationEventId,DateTimeOffset DiscoveredAt,
    string AsFoundObservation,MeasurementAffectedRange? AffectedRange,DateTimeOffset? SuspectedFrom,DateTimeOffset? SuspectedTo,
    string IntervalBasis,string Evidence);
public sealed record InvestigationTransition(string Action,string Actor,DateTimeOffset At,string Reason,int Version);
public sealed record MeasurementResourceFinding(long FindingId,int Version,MeasurementFindingState State,
    string OrganizationCode,MeasurementFindingContent Content,string CreatedBy,string? ApprovedBy,IReadOnlyList<QmsPolicyReadSource> Sources,
    IReadOnlyList<InvestigationTransition> History);
public sealed record TraceInvestigationRoot(TraceTriggerKind Kind,string? PartNumber,string? LotNumber,long? FindingId);
public sealed record TraceSourceReference(string Type,string Id,string ReadPermission,IReadOnlyList<ResourceRef> Resources,bool ActionScope=true);
public sealed record TraceEvidence(string Type,string Id,string Relationship,decimal? Quantity,string? Unit,DateTimeOffset? At,
    string Basis,IReadOnlyList<TraceSourceReference> Sources,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? CapturedCanonicalJson=null);
public sealed record TraceCandidate(string Key,string Category,string RecordType,string RecordId,string? PartNumber,string? LotNumber,
    long? OrderNumber,int? Sequence,long? ShipmentId,string? LocationCode,string? Status,decimal? Quantity,string? Unit,
    string? Customer,IReadOnlyList<TraceEvidence> Paths,IReadOnlyList<TraceSourceReference> Sources,
    IReadOnlyList<ResourceRef> ActionResources,IReadOnlyList<TraceHoldTarget> HoldTargets,string? Revision=null);
public sealed record TraceHoldTarget(HoldTargetType Type,string Id,string? Part);
public sealed record TraceGap(string Code,string Description);
/// <summary>Recorded traversal is separate from physical/equipment/interval knowledge and current access completeness.</summary>
public sealed record TraceCompleteness(bool RecordedTraversalComplete,bool IntervalKnown,bool PhysicalCoverageKnown,
    bool RestrictedSources,bool ConcurrentSnapshotUncertain,IReadOnlyList<TraceGap> Gaps);
public sealed record TraceSnapshotBoundary(string SnapshotIdentity,DateTimeOffset CapturedAt,DateTimeOffset TransactionStartedAt);
public sealed record TraceCapture(DateTimeOffset DataCutoff,string QueryVersion,string SourceTimeBasis,TraceSnapshotBoundary Boundary,
    IReadOnlyList<TraceCandidate> Candidates,TraceCompleteness Completeness,string SourceFingerprint,
    IReadOnlyList<TraceSourceReference> RootSources,IReadOnlyList<TraceGraphLink> Links);
public sealed record TraceGraphLink(string From,string To,TraceEvidence Evidence);
public sealed record TraceInvestigationSnapshot(long InvestigationId,int Number,string RecordedBy,DateTimeOffset RecordedAt,TraceCapture Capture);
public sealed record TraceabilityInvestigation(long InvestigationId,int Version,TraceInvestigationState State,TraceInvestigationRoot Root,
    string OrganizationCode,string Title,Guid OwnerPersonId,string CreatedBy,DateTimeOffset CreatedAt,string TriggerEvidence,int LatestSnapshot,
    IReadOnlyList<TraceSourceReference> RootSources,IReadOnlyList<InvestigationTransition> History);
public sealed record TraceCandidateAssessment(long AssessmentId,long InvestigationId,int SnapshotNumber,string CandidateKey,int Version,
    TraceAssessmentState State,TraceImpactDecision Decision,string Basis,string Evidence,string PlannedDisposition,string ProposedBy,
    DateTimeOffset ProposedAt,string? ApprovedBy,DateTimeOffset? ApprovedAt,IReadOnlyList<InvestigationTransition> History);
public sealed record InvestigationHoldLink(long InvestigationId,int SnapshotNumber,string CandidateKey,long AssessmentId,long HoldId,
    int RecordedHoldVersion,string Actor,DateTimeOffset At,string Basis);
public sealed record TraceInvestigationView(TraceabilityInvestigation Header,TraceInvestigationSnapshot Snapshot,
    IReadOnlyList<TraceCandidateAssessment> Assessments,IReadOnlyList<InvestigationHoldLink> HoldLinks,bool NarrativeRedacted);

/// <summary>Host composes authoritative owner views; no second genealogy or business writes in this port.</summary>
public interface ITraceInvestigationResolver
{
    Task<IReadOnlyList<TraceSourceReference>> FindingSourcesAsync(IKernelUnitOfWork uow,ForgePrincipal principal,MeasurementFindingContent content,CancellationToken ct);
    Task<IReadOnlyList<TraceSourceReference>> RootSourcesAsync(IKernelUnitOfWork uow,ForgePrincipal principal,TraceInvestigationRoot root,CancellationToken ct);
    Task<TraceCapture> CaptureAsync(IKernelUnitOfWork uow,ForgePrincipal principal,TraceInvestigationRoot root,TraceSnapshotBoundary boundary,CancellationToken ct);
    Task RequireCurrentReadsAsync(IKernelUnitOfWork uow,ForgePrincipal principal,IReadOnlyList<TraceSourceReference> sources,CancellationToken ct);
    Task<IReadOnlyList<TraceSourceReference>> CurrentContextsAsync(IKernelUnitOfWork uow,IReadOnlyList<TraceSourceReference> sources,CancellationToken ct);
    Task<bool> CanReadAsync(IKernelUnitOfWork uow,ForgePrincipal principal,IReadOnlyList<TraceSourceReference> sources,CancellationToken ct);
    Task RequireCandidateActionAsync(IKernelUnitOfWork uow,ForgePrincipal principal,TraceCandidate candidate,string permission,CancellationToken ct);
    Task<Hold> RequireMatchingHoldAsync(IKernelUnitOfWork uow,ForgePrincipal principal,TraceCandidate candidate,long holdId,int expectedVersion,CancellationToken ct);
}
public interface IQualityInvestigationLookup
{
    Task<TraceSnapshotBoundary> CaptureBoundaryAsync(CancellationToken ct);
    Task<MeasurementResourceFinding?> FindFindingAsync(long findingId,CancellationToken ct);
    Task<IReadOnlyList<MeasurementResourceFinding>> ListFindingsAsync(CancellationToken ct);
    Task<TraceabilityInvestigation?> FindInvestigationAsync(long investigationId,CancellationToken ct);
    Task<IReadOnlyList<TraceabilityInvestigation>> ListInvestigationsAsync(CancellationToken ct);
    Task<TraceInvestigationSnapshot?> FindSnapshotAsync(long investigationId,int number,CancellationToken ct);
    Task<IReadOnlyList<TraceCandidateAssessment>> AssessmentsAsync(long investigationId,int snapshotNumber,CancellationToken ct);
    Task<TraceCandidateAssessment?> FindAssessmentAsync(long assessmentId,CancellationToken ct);
    Task<IReadOnlyList<InvestigationHoldLink>> HoldLinksAsync(long investigationId,CancellationToken ct);
    Task<IReadOnlyList<long>> LegacyInspectionIdsForToolAsync(string tool,DateTimeOffset? from,DateTimeOffset? to,DateTimeOffset cutoff,long afterId,int pageSize,CancellationToken ct);
    Task<IReadOnlyList<AcceptanceInspection>> AcceptanceInspectionsForToolAsync(string tool,DateTimeOffset? from,DateTimeOffset? to,DateTimeOffset cutoff,long afterId,int pageSize,CancellationToken ct);
}
public interface IQualityInvestigationSourceLock
{
    Task LockFindingAsync(long findingId,CancellationToken ct);
    Task LockInvestigationAsync(long investigationId,CancellationToken ct);
}
internal interface IQualityInvestigationStore:IQualityInvestigationLookup,IQualityInvestigationSourceLock
{
    Task<long> InsertFindingAsync(MeasurementResourceFinding finding,CancellationToken ct);
    Task UpdateFindingAsync(MeasurementResourceFinding finding,int expectedVersion,CancellationToken ct);
    Task<long> InsertInvestigationAsync(TraceabilityInvestigation investigation,CancellationToken ct);
    Task UpdateInvestigationAsync(TraceabilityInvestigation investigation,int expectedVersion,CancellationToken ct);
    Task InsertSnapshotAsync(TraceInvestigationSnapshot snapshot,CancellationToken ct);
    Task<long> InsertAssessmentAsync(TraceCandidateAssessment assessment,CancellationToken ct);
    Task UpdateAssessmentAsync(TraceCandidateAssessment assessment,int expectedVersion,CancellationToken ct);
    Task InsertHoldLinkAsync(InvestigationHoldLink link,CancellationToken ct);
}
