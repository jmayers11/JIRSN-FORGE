using Forge.Kernel.Access;
using Forge.Kernel.Security;

namespace Forge.Quality.Contracts;

public enum QmsReviewDefinitionKind { ManagementReviewProgram, StandardEvidenceMapping }
public enum QmsReviewRunState { Draft, InputsFrozen, Submitted, Reviewed, Approved, Closed, Withdrawn }
public enum QmsEvidenceAdequacy { NotAssessed, Adequate, Inadequate, Unavailable }
public enum QmsConformityAssessment { NotAssessed, Supported, Gap, NotApplicable, RequiresLicensedVerification }
public enum QmsReviewActionState { Open, Completed, Verified, Reopened, Withdrawn }
public enum QmsMappingApplicability { Undetermined, Applicable, Conditional, NotApplicable }
public enum QmsEditionRelationship { Direct, Split, Combined, Changed, New, Removed, Unresolved }

/// <summary>An organization chooses its review agenda; a populated input is never an effectiveness conclusion.</summary>
public sealed record QmsReviewAgendaItem(string Code, string Topic, bool Required, string RequiredEvidence,
    string AdequacyCriteria, IReadOnlyList<QmsPlanningEvidenceReference> Sources,
    IReadOnlyList<QmsRevisionReference> Metrics);
public sealed record QmsManagementReviewProgramContent(string Name, Guid OwnerPersonId, string Frequency,
    string Scope, IReadOnlyList<QmsReviewAgendaItem> Agenda, string RequiredParticipants,
    string DecisionAuthority, string ActionVerificationCriteria, string ClosureCriteria,
    IReadOnlyList<Guid> RequiredParticipantPersonIds);
public sealed record QmsStandardEvidenceMappingContent(string Title, IReadOnlyList<QmsStandardReference> Criteria,
    QmsMappingApplicability Applicability, string ApplicabilityBasis, Guid ProcessOwnerPersonId,
    IReadOnlyList<QmsRevisionReference> Processes, IReadOnlyList<QmsRevisionReference> Requirements,
    IReadOnlyList<QmsDocumentReference> ImplementingProcedures, IReadOnlyList<string> ForgeCapabilities,
    IReadOnlyList<string> RequiredEvidenceTypes, string AssessmentMethod, string ReviewInterval);
public sealed record QmsReviewDefinitionContent(QmsManagementReviewProgramContent? Program = null,
    QmsStandardEvidenceMappingContent? Mapping = null, IReadOnlyList<QmsDocumentReference>? Documents = null);
public sealed record QmsReviewTransition(string Action, int Version, string Actor, DateTimeOffset At,
    string FromState, string ToState, string Basis, string Reason);
public sealed record QmsReviewDefinitionRevision(string Code, int Revision, int Version, QmsState State,
    QmsReviewDefinitionContent Content, string CreatedBy, string LastEditedBy, string? ReviewedBy,
    string? ApprovedBy, DateTimeOffset? EffectiveFrom, DateTimeOffset? EffectiveTo,
    bool RequiresLicensedVerification, IReadOnlyList<QmsPlanningSourceSnapshot> Sources,
    IReadOnlyList<QmsReviewTransition> History);
public sealed record QmsReviewDefinition(string Code, QmsReviewDefinitionKind Kind, string OrganizationCode,
    string FacilityCode, Guid? ProgramId, DataMarking Markings, string CreatedBy, DateTimeOffset CreatedAt,
    IReadOnlyList<QmsReviewDefinitionRevision> Revisions);

public sealed record QmsReviewMetricSnapshot(string Code, int Revision, int Version,
    string DefinitionFingerprint, QmsMetricMeasurement Measurement);
public sealed record QmsReviewFrozenInput(string AgendaCode, string Topic, bool Required,
    IReadOnlyList<QmsPlanningSourceSnapshot> Sources, IReadOnlyList<QmsReviewMetricSnapshot> Metrics,
    IReadOnlyList<string> Gaps, string Fingerprint);
/// <summary>Immutable captured inputs, definition versions, closed period and actual data cutoff. No later recalculation changes these bytes.</summary>
public sealed record QmsReviewInputManifest(Guid ManifestId, Guid ReviewId, string ProgramCode,
    int ProgramRevision, int ProgramVersion, string DefinitionFingerprint, DateTimeOffset PeriodFrom,
    DateTimeOffset PeriodTo, DateTimeOffset DataCutoff, string CapturedBy, DateTimeOffset CapturedAt,
    IReadOnlyList<QmsReviewFrozenInput> Inputs, IReadOnlyList<string> Contributors, string Fingerprint,
    string DatabaseSnapshotIdentity);
public sealed record QmsReviewInputAssessment(string AgendaCode, QmsEvidenceAdequacy Adequacy,
    string Assessment, string Limits, string Decision, IReadOnlyList<QmsPlanningEvidenceReference> AdditionalEvidence,
    IReadOnlyList<QmsPlanningSourceSnapshot>? CapturedEvidence = null);
public sealed record QmsManagementReviewDecision(string Code, string Decision, string Basis,
    string IntendedResult, string Resources, Guid? OwnerPersonId, DateTimeOffset? DueAt,
    string CompletionCriteria, IReadOnlyList<QmsPlanningEvidenceReference> SourceActionReferences,
    IReadOnlyList<QmsPlanningSourceSnapshot>? CapturedSources = null);
public sealed record QmsManagementReviewRun(Guid Id, int Version, QmsReviewRunState State,
    string ProgramCode, int ProgramRevision, DateTimeOffset PeriodFrom, DateTimeOffset PeriodTo,
    string CreatedBy, DateTimeOffset CreatedAt, QmsReviewInputManifest? Manifest,
    IReadOnlyList<QmsReviewInputAssessment> Assessments, IReadOnlyList<QmsManagementReviewDecision> Decisions,
    string MeetingRecord, IReadOnlyList<Guid> Participants, string? ReviewedBy, string? ApprovedBy,
    DateTimeOffset? ApprovedAt, string Limitations, IReadOnlyList<QmsReviewTransition> History);
public sealed record QmsReviewAction(Guid Id, Guid ReviewId, string DecisionCode, int Version,
    QmsReviewActionState State, Guid OwnerPersonId, DateTimeOffset DueAt, string Action,
    string CompletionCriteria, string Resources, string CreatedBy, DateTimeOffset CreatedAt,
    IReadOnlyList<QmsPlanningSourceSnapshot> Evidence, string? CompletedBy, string? VerifiedBy,
    DateTimeOffset? VerifiedAt, string Result, IReadOnlyList<QmsReviewTransition> History);

/// <summary>Qualified human assessment is distinct from source linkage and licensed normative verification.</summary>
public sealed record QmsMappingEvidenceAssessment(Guid Id, string MappingCode, int MappingRevision,
    int Version, QmsAssessmentState State, QmsEvidenceAdequacy Adequacy,
    QmsConformityAssessment Assessment, string Interpretation, string AssessmentMethod,
    string ApplicabilityBasis, string Gap, string AuditorNotes, string Limitations,
    QmsAuditorValidation AuditorValidation, QmsAuditorValidationResult Competence,
    IReadOnlyList<QmsAuditorValidationResult> CompetenceHistory,
    IReadOnlyList<QmsPlanningSourceSnapshot> Evidence, string CreatedBy, DateTimeOffset CreatedAt,
    string? ReviewedBy, string? ApprovedBy, DateTimeOffset? ApprovedAt,
    IReadOnlyList<QmsReviewTransition> History);
/// <summary>Many-to-many edition mapping; unresolved criteria retain explicit unresolved state.</summary>
public sealed record QmsEditionMappingAssessment(Guid Id, int Version, string OrganizationCode, string FacilityCode,
    Guid? ProgramId, DataMarking Markings, QmsAssessmentState State,
    string Standard, string FromEdition, string ToEdition, IReadOnlyList<QmsStandardReference> FromCriteria,
    IReadOnlyList<QmsStandardReference> ToCriteria, QmsEditionRelationship Relationship,
    string Basis, string Changes, string ApplicabilityImpact, string RequiredActions,
    bool RequiresLicensedVerification, QmsAuditorValidation AuditorValidation, QmsAuditorValidationResult Competence,
    IReadOnlyList<QmsAuditorValidationResult> CompetenceHistory,
    IReadOnlyList<QmsPlanningSourceSnapshot> Evidence,
    string CreatedBy, DateTimeOffset CreatedAt, string? ReviewedBy, string? ApprovedBy,
    DateTimeOffset? ApprovedAt, IReadOnlyList<QmsReviewTransition> History);

public sealed record QmsReviewPage<T>(IReadOnlyList<T> Items, string? NextCursor);
public interface IQmsReviewLookup
{
    Task<QmsReviewSnapshotIdentity> SnapshotIdentityAsync(CancellationToken ct);
    Task<QmsReviewDefinition?> FindDefinitionAsync(string code, CancellationToken ct);
    Task<QmsReviewPage<QmsReviewDefinition>> DefinitionsAsync(QmsReviewDefinitionKind? kind, int take, string? after, CancellationToken ct);
    Task<QmsManagementReviewRun?> FindRunAsync(Guid id, CancellationToken ct);
    Task<QmsReviewPage<QmsManagementReviewRun>> RunsAsync(string? programCode, int take, string? after, CancellationToken ct);
    Task<QmsReviewAction?> FindActionAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<QmsReviewAction>> ActionsAsync(Guid reviewId, CancellationToken ct);
    Task<QmsMappingEvidenceAssessment?> FindMappingAssessmentAsync(Guid id, CancellationToken ct);
    Task<QmsReviewPage<QmsMappingEvidenceAssessment>> MappingAssessmentsAsync(string? code, int take, string? after, CancellationToken ct);
    Task<QmsEditionMappingAssessment?> FindEditionAssessmentAsync(Guid id, CancellationToken ct);
    Task<QmsReviewPage<QmsEditionMappingAssessment>> EditionAssessmentsAsync(int take, string? after, CancellationToken ct);
}
public sealed record QmsReviewSnapshotIdentity(DateTimeOffset CapturedAt, string SnapshotIdentity);
internal interface IQmsReviewStore : IQmsReviewLookup
{
    Task LockAsync(string key, CancellationToken ct);
    Task InsertDefinitionAsync(QmsReviewDefinition definition, CancellationToken ct);
    Task SaveDefinitionRevisionAsync(QmsReviewDefinitionRevision revision, int? expectedVersion, CancellationToken ct);
    Task SaveRunAsync(QmsManagementReviewRun run, int? expectedVersion, CancellationToken ct);
    Task AppendManifestAsync(QmsReviewInputManifest manifest, CancellationToken ct);
    Task SaveActionAsync(QmsReviewAction action, int? expectedVersion, CancellationToken ct);
    Task SaveMappingAssessmentAsync(QmsMappingEvidenceAssessment assessment, int? expectedVersion, CancellationToken ct);
    Task SaveEditionAssessmentAsync(QmsEditionMappingAssessment assessment, int? expectedVersion, CancellationToken ct);
}
