using Forge.Kernel.Access;
using Forge.Kernel.Security;

namespace Forge.Quality.Contracts;

public enum QmsPlanningKind { EvaluationMethod, RiskOpportunity, MetricDefinition, Objective, Change, RetentionSchedule, Improvement }
public enum QmsRiskKind { Risk, Opportunity }
public enum QmsEvaluationMethodKind { Qualitative, Ordinal, Numeric, OtherExplicit }
public enum QmsMetricSourceKind { RecordedProductionReports, AttributedHumanObservation }
public enum QmsRecordedProductionMeasure { GoodQuantity, ScrapQuantity, TotalReportedQuantity, ScrapFraction, ReportCount }
public enum QmsTargetComparison { AtLeast, AtMost, Equal, WithinRange }
public enum QmsBaselineStatus { Recorded, Unknown }
public enum QmsAssessmentKind { RiskEvaluation, ObjectiveEvaluation, ChangeImpact, ImplementationVerification, EffectivenessReview, ImprovementEvaluation }
public enum QmsAssessmentState { Draft, Submitted, Reviewed, Approved, Withdrawn }
public enum QmsTaskState { Open, Completed, Verified, Reopened, Withdrawn }
public enum QmsPlanningCaseState { Identified, AssessmentPending, ActionPlanned, Implementing, VerificationPending, EffectivenessPending, Accepted, Closed, Reopened, Withdrawn }
public enum QmsRetentionTrigger { SourceCreatedAt, SourceApprovedAt, SourceClosedAt, HumanVerifiedExternalOccurrence }
public enum QmsRetentionHoldKind { Legal, Quality }
public enum QmsRetentionBindingState { Proposed, Active, Superseded, Withdrawn }
public enum QmsRetentionHoldState { Active, ReleaseProposed, Released }
public enum QmsDispositionState { Proposed, DryRunFrozen, Approved, Recorded, Withdrawn }
public enum QmsDispositionObservation { AdministrativeRecordOnly, AttributedExternalDispositionObservation }
public enum QmsMetricAvailability { Complete, Unavailable, Partial }

/// <summary>A reference requests owner evidence. Scope, timestamps, hashes and actors come from its actual owner.</summary>
public sealed record QmsPlanningEvidenceReference(string Context, string Type, string Key, string? Revision = null,
    Guid? EventId = null, Guid? AuditId = null);
public sealed record QmsPlanningReadSource(string Permission, ResourceRef Resource);
public sealed record QmsPlanningSourceSnapshot(QmsPlanningEvidenceReference Reference, string OwnerContext,
    string OwnerType, string OwnerId, string? OwnerRevision, string Fingerprint,
    DateTimeOffset CapturedAt, DateTimeOffset? CreatedAt, DateTimeOffset? ApprovedAt, DateTimeOffset? ClosedAt,
    string? FileSha256, IReadOnlyList<string> Contributors, IReadOnlyList<QmsPlanningReadSource> Sources,
    bool RequiresLicensedVerification,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? CapturedCanonicalJson = null);

public sealed record QmsEvaluationScaleEntry(string Code, string Description, decimal? NumericValue);
public sealed record QmsEvaluationMethodContent(string Name, QmsEvaluationMethodKind Kind, string Method,
    string DecisionCriteria, IReadOnlyList<QmsEvaluationScaleEntry> Scale, string ReviewInterval);
public sealed record QmsRiskOpportunityContent(QmsRiskKind Kind, string Context, QmsRevisionReference Process,
    string Description, string ConsequenceOrBenefit, QmsRevisionReference EvaluationMethod, Guid OwnerPersonId,
    string PlannedAction, DateTimeOffset DueAt, string Resources, string ReviewInterval, string EffectivenessCriteria);

/// <summary>Explicit order cohort and half-open period; unsupported/incomplete owner facts never become a complete scalar.</summary>
public sealed record QmsMetricDefinitionContent(string Name, QmsMetricSourceKind SourceKind,
    QmsRecordedProductionMeasure? ProductionMeasure, string Unit, string NumeratorDefinition,
    string DenominatorDefinition, string MeasurementMethod, string CohortBasis,
    IReadOnlyList<long> OrderNumbers, IReadOnlyList<int> OperationSequences, bool? IncludeAllOperations,
    DateTimeOffset PeriodFrom, DateTimeOffset PeriodTo, int MaximumRows,
    string CompletenessCriteria, string ReviewInterval);
public sealed record QmsObjectiveContent(string Title, QmsRevisionReference Metric, Guid OwnerPersonId,
    QmsBaselineStatus BaselineStatus, decimal? Baseline, string BaselineBasis,
    IReadOnlyList<QmsPlanningEvidenceReference> BaselineEvidence, QmsTargetComparison Comparison,
    decimal Target, decimal? UpperTarget, decimal Tolerance, DateTimeOffset PeriodFrom, DateTimeOffset PeriodTo,
    string ReviewInterval, string PlannedActions, string Resources, DateTimeOffset DueAt);
public sealed record QmsChangeContent(string ProposedChange, string ReasonForChange, string IntendedResult,
    IReadOnlyList<QmsRevisionReference> AffectedProcesses, IReadOnlyList<QmsRevisionReference> RisksAndOpportunities,
    string ResourceImplications, string ResponsibilityChanges, string RequiredApprovals,
    string ImplementationPlan, string VerificationCriteria, string EffectivenessCriteria, Guid OwnerPersonId, DateTimeOffset DueAt,
    IReadOnlyList<QmsPlanningEvidenceReference> EngineeringChanges);
public sealed record QmsRetentionScheduleContent(string RecordCategory, string RetentionRequirement,
    QmsRetentionTrigger Trigger, int? DurationDays, bool? Indefinite, Guid OwnerPersonId,
    string ContractCustomerLegalBasis, string DispositionAuthority, string DispositionEvidenceCriteria,
    IReadOnlyList<string> AllowedOwnerTypes);
public sealed record QmsImprovementContent(string Title, string Context, string IntendedBenefit,
    Guid OwnerPersonId, string EvaluationMethod, string SelectionCriteria, string PlannedActions,
    string Resources, DateTimeOffset DueAt, string BeforeAfterMethod,
    IReadOnlyList<QmsPlanningEvidenceReference> Origins);

/// <summary>Exactly one typed section agrees with Kind. Engineering retains every linked document byte.</summary>
public sealed record QmsPlanningContent(QmsEvaluationMethodContent? EvaluationMethod = null,
    QmsRiskOpportunityContent? RiskOpportunity = null, QmsMetricDefinitionContent? MetricDefinition = null,
    QmsObjectiveContent? Objective = null, QmsChangeContent? Change = null,
    QmsRetentionScheduleContent? RetentionSchedule = null, QmsImprovementContent? Improvement = null,
    IReadOnlyList<QmsDocumentReference>? Documents = null, IReadOnlyList<QmsRevisionReference>? Requirements = null,
    IReadOnlyList<QmsPlanningEvidenceReference>? Evidence = null);
public sealed record QmsPlanningTransition(string Action, int Version, string Actor, DateTimeOffset At,
    string FromState, string ToState, string Note, string Reason);
public sealed record QmsPlanningRevision(string Code, int Revision, int Version, QmsState State,
    QmsPlanningContent Content, string CreatedBy, string LastEditedBy, string? ReviewedBy, string? ApprovedBy,
    DateTimeOffset? EffectiveFrom, DateTimeOffset? EffectiveTo, bool RequiresLicensedVerification,
    IReadOnlyList<QmsPlanningSourceSnapshot> Sources, IReadOnlyList<QmsPlanningTransition> History);
public sealed record QmsPlanningRecord(string Code, QmsPlanningKind Kind, string OrganizationCode,
    string FacilityCode, Guid? ProgramId, DataMarking Markings, string CreatedBy, DateTimeOffset CreatedAt,
    IReadOnlyList<QmsPlanningRevision> Revisions);
public sealed record QmsPlanningPage<T>(IReadOnlyList<T> Items, string? NextCursor);
public sealed record QmsPlanningCase(Guid Id, string Code, int DefinitionRevision, int Version,
    QmsPlanningCaseState State, string CreatedBy, DateTimeOffset CreatedAt, Guid? LastApprovedAssessmentId,
    IReadOnlyList<QmsPlanningTransition> History, int Generation = 1);

public sealed record QmsRiskEvaluation(string MethodCode, int MethodRevision, string Evaluation,
    decimal? NumericResult, string? ScaleCode, string Decision, string Basis,
    string ActionPlan, DateTimeOffset NextReviewAt, string EffectivenessCriteria);
public sealed record QmsObjectiveEvaluation(DateTimeOffset From, DateTimeOffset To, decimal? HumanActual,
    string HumanMeasurementBasis, string CompletenessAssessment, string ActionDecision);
public sealed record QmsChangeImpact(string ProcessImpact, string RiskOpportunityImpact,
    string ResourceImpact, string ResponsibilityImpact, string IntegrityAssessment, string ApprovalBasis);
public sealed record QmsImplementationVerification(IReadOnlyList<Guid> TaskIds, string Criteria,
    string Result, bool? Satisfied, string Limitations);
public sealed record QmsEffectivenessReview(DateTimeOffset From, DateTimeOffset To, string Method,
    string Criteria, string Results, bool? Effective, string Limitations, string FollowUpDecision);
public sealed record QmsImprovementEvaluation(string Decision, string Basis, decimal? Before,
    decimal? After, string Unit, string ResultInterpretation, string Limitations);
public sealed record QmsPlanningAssessmentContent(QmsRiskEvaluation? RiskEvaluation = null,
    QmsObjectiveEvaluation? ObjectiveEvaluation = null, QmsChangeImpact? ChangeImpact = null,
    QmsImplementationVerification? ImplementationVerification = null, QmsEffectivenessReview? EffectivenessReview = null,
    QmsImprovementEvaluation? ImprovementEvaluation = null);
public sealed record QmsMetricEvidenceRow(long ReportId, long OrderNumber, int Sequence, Guid PersonId,
    DateTimeOffset ReportedAt, decimal GoodQuantity, decimal ScrapQuantity, string Unit,
    QmsPlanningSourceSnapshot Source);
public sealed record QmsMetricMeasurement(QmsMetricAvailability Availability, string Basis,
    decimal? Actual, decimal? Numerator, decimal? Denominator, string Unit, DateTimeOffset From,
    DateTimeOffset To, IReadOnlyList<long> RequiredOrderIds, IReadOnlyList<QmsMetricEvidenceRow> Evidence,
    string ManifestFingerprint, DateTimeOffset CapturedAt);
public sealed record QmsMetricDashboardItem(string Code, int Revision, string Name,
    QmsMetricSourceKind SourceKind, string Unit, string NumeratorDefinition, string DenominatorDefinition,
    string MeasurementMethod, string CohortBasis, string Status, bool Calculated,
    QmsMetricMeasurement? Measurement,IReadOnlyList<QmsMetricDashboardObjective> Objectives);
public sealed record QmsMetricDashboardObjective(string Code,int Revision,string Title,
    QmsTargetComparison Comparison,decimal Target,decimal? UpperTarget,decimal Tolerance,
    DateTimeOffset PeriodFrom,DateTimeOffset PeriodTo,string Status);
public sealed record QmsMetricObjectiveAnalystResult(string ObjectiveCode,int ObjectiveRevision,string Title,
    string MetricCode,int MetricRevision,decimal Actual,string Unit,QmsTargetComparison Comparison,
    decimal Target,decimal? UpperTarget,decimal Tolerance,DateTimeOffset From,DateTimeOffset To,
    QmsMetricMeasurement Measurement);
public sealed record QmsMetricDashboardSnapshot(DateTimeOffset From, DateTimeOffset To,
    DateTimeOffset CapturedAt, IReadOnlyList<QmsMetricDashboardItem> Items, string? NextCursor,
    bool DefinitionsComplete, bool CalculatedMetricsComplete);
public sealed record QmsPlanningAssessment(Guid Id, string Code, int DefinitionRevision, int Version,
    QmsAssessmentKind Kind, QmsAssessmentState State, QmsPlanningAssessmentContent Content,
    QmsMetricMeasurement? Measurement, string CreatedBy, string LastEditedBy, string? ReviewedBy,
    string? ApprovedBy, DateTimeOffset CreatedAt, IReadOnlyList<QmsPlanningSourceSnapshot> Sources,
    IReadOnlyList<QmsPlanningTransition> History, Guid? CaseId = null, int? CaseGeneration = null, int? CaseVersion = null);
public sealed record QmsPlanningTask(Guid Id, string Code, int DefinitionRevision, int Version,
    QmsTaskState State, string Title, Guid OwnerPersonId, DateTimeOffset DueAt, string Resources,
    string CompletionCriteria, string CreatedBy, DateTimeOffset CreatedAt, string? CompletedBy,
    DateTimeOffset? CompletedAt, string? VerifiedBy, DateTimeOffset? VerifiedAt, string Result,
    IReadOnlyList<QmsPlanningSourceSnapshot> Evidence, IReadOnlyList<QmsPlanningTransition> History);

public sealed record QmsRetentionBinding(Guid Id, int Version, QmsRetentionBindingState State,
    string OrganizationCode, string FacilityCode, Guid? ProgramId, DataMarking Markings,
    QmsRevisionReference Schedule, QmsPlanningSourceSnapshot Subject, QmsRetentionTrigger Trigger,
    DateTimeOffset? TriggerAt, string TriggerBasis, IReadOnlyList<QmsPlanningSourceSnapshot> TriggerEvidence,
    string CreatedBy, DateTimeOffset CreatedAt, string? ApprovedBy, DateTimeOffset? ApprovedAt,
    IReadOnlyList<QmsPlanningTransition> History, Guid? PreviousBindingId = null, int? PreviousBindingVersion = null);
public sealed record QmsRetentionHold(Guid Id, int Version, QmsRetentionHoldKind Kind, QmsRetentionHoldState State,
    string OrganizationCode, string FacilityCode, Guid? ProgramId, DataMarking Markings,
    QmsPlanningSourceSnapshot Subject, string Basis, string AuthorityBasis, string PlacedBy, DateTimeOffset PlacedAt,
    string? ReleaseProposedBy, string? ReleasedBy, DateTimeOffset? ReleasedAt,
    IReadOnlyList<QmsPlanningSourceSnapshot> Evidence, IReadOnlyList<QmsPlanningTransition> History);
public sealed record QmsDispositionCandidate(Guid BindingId, int BindingVersion, QmsRevisionReference Schedule,
    int ScheduleVersion, QmsPlanningSourceSnapshot Subject, DateTimeOffset? RetainUntil,
    bool Eligible, IReadOnlyList<string> Blockers, IReadOnlyList<Guid> ActiveHoldIds,
    IReadOnlyList<string> SharedFileOwnerIds);
public sealed record QmsDispositionManifest(Guid Id, int Version, DateTimeOffset EvaluatedAt,
    IReadOnlyList<QmsDispositionCandidate> Candidates, bool Eligible, string Fingerprint);
public sealed record QmsDispositionRecord(Guid Id, int Version, QmsDispositionState State,
    string OrganizationCode, string FacilityCode, Guid? ProgramId, DataMarking Markings,
    IReadOnlyList<Guid> BindingIds, string Purpose, string ProposedBy, DateTimeOffset ProposedAt,
    QmsDispositionManifest? Manifest, string? ApprovedBy, DateTimeOffset? ApprovedAt,
    QmsDispositionObservation? ObservationKind, string Observation, string? ObservedBy,
    DateTimeOffset? ObservedAt, IReadOnlyList<QmsPlanningSourceSnapshot> Evidence,
    IReadOnlyList<QmsPlanningTransition> History);

/// <summary>Host read bridge. It resolves actual owners, mixed permissions, source versions and complete fingerprints in the caller's UOW.</summary>
public interface IQmsPlanningEvidenceReader
{
    Task<QmsPlanningSourceSnapshot> ResolveAsync(IKernelUnitOfWork uow, ForgePrincipal principal,
        QmsPlanningEvidenceReference reference, bool lockSource, CancellationToken ct);
    Task<IReadOnlyList<QmsPlanningSourceSnapshot>> SharedFileOwnersAsync(IKernelUnitOfWork uow, ForgePrincipal principal,
        string sha256, CancellationToken ct);
    Task<QmsMetricMeasurement> MeasureAsync(IKernelUnitOfWork uow, ForgePrincipal principal,
        QmsMetricDefinitionContent definition, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
    Task<QmsMetricMeasurement> MeasureSnapshotAsync(IKernelUnitOfWork uow, ForgePrincipal principal,
        QmsMetricDefinitionContent definition, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
}

/// <summary>Actual owner extensions register one exact context/type. The supplied callback shares the UOW and recursion guard; it is not an injected reader/service cycle.</summary>
public interface IQmsPlanningOwnerEvidenceResolver
{
    bool Owns(string context, string type);
    Task<QmsPlanningSourceSnapshot> ResolveAsync(IKernelUnitOfWork uow, ForgePrincipal principal,
        QmsPlanningEvidenceReference reference, bool lockSource,
        Func<QmsPlanningEvidenceReference, bool, Task<QmsPlanningSourceSnapshot>> resolveSource, CancellationToken ct);
}

public interface IQmsPlanningLookup
{
    Task<QmsPlanningRecord?> FindAsync(string code, CancellationToken ct);
    Task<QmsPlanningPage<QmsPlanningRecord>> ListAsync(QmsPlanningKind? kind, int take, string? after, CancellationToken ct);
    Task<QmsPlanningAssessment?> FindAssessmentAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<QmsPlanningAssessment>> AssessmentsAsync(string code, int revision, CancellationToken ct);
    Task<QmsPlanningTask?> FindTaskAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<QmsPlanningTask>> TasksAsync(string code, int revision, CancellationToken ct);
    Task<QmsPlanningCase?> FindCaseAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<QmsPlanningCase>> CasesAsync(string code, CancellationToken ct);
    Task<QmsRetentionBinding?> FindBindingAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<QmsRetentionBinding>> BindingsForSourceAsync(QmsPlanningEvidenceReference subject, CancellationToken ct);
    Task<QmsPlanningPage<QmsRetentionBinding>> BindingsAsync(int take, string? after, CancellationToken ct);
    Task<IReadOnlyList<QmsRetentionHold>> HoldsAsync(QmsPlanningEvidenceReference subject, CancellationToken ct);
    Task<QmsRetentionHold?> FindHoldAsync(Guid id, CancellationToken ct);
    Task<QmsDispositionRecord?> FindDispositionAsync(Guid id, CancellationToken ct);
    Task<QmsPlanningPage<QmsDispositionRecord>> DispositionsAsync(int take, string? after, CancellationToken ct);
}

internal interface IQmsPlanningStore : IQmsPlanningLookup
{
    Task LockAsync(string code, CancellationToken ct);
    Task LockEvidenceAsync(string key, CancellationToken ct);
    Task InsertAsync(QmsPlanningRecord record, CancellationToken ct);
    Task SaveRevisionAsync(QmsPlanningRevision revision, int? expectedVersion, CancellationToken ct);
    Task SaveAssessmentAsync(QmsPlanningAssessment assessment, int? expectedVersion, CancellationToken ct);
    Task SaveTaskAsync(QmsPlanningTask task, int? expectedVersion, CancellationToken ct);
    Task SaveCaseAsync(QmsPlanningCase planningCase, int? expectedVersion, CancellationToken ct);
    Task SaveBindingAsync(QmsRetentionBinding binding, int? expectedVersion, CancellationToken ct);
    Task SaveHoldAsync(QmsRetentionHold hold, int? expectedVersion, CancellationToken ct);
    Task SaveDispositionAsync(QmsDispositionRecord disposition, int? expectedVersion, CancellationToken ct);
    Task AppendManifestAsync(Guid dispositionId, QmsDispositionManifest manifest, CancellationToken ct);
}
