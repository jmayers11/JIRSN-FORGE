using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;

namespace Forge.Quality.Contracts;

public static class AuditPermissions
{
    public const string Manage="quality.audit.manage";
    public const string Conduct="quality.audit.conduct";
    public const string Approve="quality.audit.approve";
}
public enum QmsAuditState { Scheduled, InProgress, ReportDraft, ReportSubmitted, ReportApproved, FollowUp, Closed, Reopened, Cancelled }
public enum QmsAuditFindingKind { Nonconformity, Observation, OpportunityForImprovement }
public enum QmsAuditAdequacy { NotEvaluated, Adequate, Inadequate, Partial, Unavailable }
public enum QmsAuditFindingState { Recorded, FollowUpRequired, FollowUpVerified, Closed }
public enum QmsAuditCompetenceDecision { Competent, NotCompetent, Pending }
public enum QmsCauseKnowledge { Unknown, Proposed, Confirmed }
public enum QmsCapaState { Investigating, InvestigationSubmitted, InvestigationReviewed, PlanApproved, Implementing, EffectivenessPending, EffectivenessSubmitted, Closed, Reopened, Withdrawn }
public enum QmsCorrectiveActionKind { Correction, Containment, CorrectiveAction }
public enum QmsCorrectiveActionState { Open, Completed, Verified, Reopened, Withdrawn }
public enum QmsEffectivenessResult { Effective, Ineffective, Inconclusive }

public sealed record QmsAuditCriterion(string Code,string Name,string EvaluationMethod,
    IReadOnlyList<QmsRevisionReference> Requirements,IReadOnlyList<QmsDocumentReference> Documents,
    IReadOnlyList<string> RequiredEvidenceKinds,string EvidenceAdequacyCriteria);
public sealed record QmsAuditChecklistEntry(string Code,string CriterionCode,string Question,string RequiredEvidenceBasis);
public sealed record QmsAuditCompetenceRule(bool? RequireFormalQualifications,IReadOnlyList<string> QualificationCodes,
    bool? RequireIndependentCompetenceAssessment,string CompetenceCriteria,string ReassessmentInterval);
public sealed record QmsAuditImpartialityRule(bool? ProhibitAuditingOwnedProcesses,bool? ProhibitAuditingContributedEvidence,
    bool? RequireIndependentReportApproval,string ConflictAssessmentCriteria);
public sealed record QmsAuditProgramContent(string Title,string Scope,string ScheduleBasis,string Frequency,
    Guid OwnerPersonId,IReadOnlyList<QmsRevisionReference> Processes,IReadOnlyList<QmsAuditCriterion> Criteria,
    IReadOnlyList<QmsAuditChecklistEntry> Checklist,QmsAuditCompetenceRule Competence,
    QmsAuditImpartialityRule Impartiality,string FindingFollowUpCriteria,string ClosureCriteria,
    IReadOnlyList<QmsDocumentReference> Documents,IReadOnlyList<QmsPlanningEvidenceReference> Evidence);
public sealed record QmsAuditProgramRevision(string Code,int Revision,int Version,QmsState State,
    QmsAuditProgramContent Content,string CreatedBy,string LastEditedBy,string? ReviewedBy,string? ApprovedBy,
    DateTimeOffset? EffectiveFrom,DateTimeOffset? EffectiveTo,bool RequiresLicensedVerification,
    IReadOnlyList<QmsPlanningSourceSnapshot> Sources,IReadOnlyList<QmsPlanningTransition> History);
public sealed record QmsAuditProgram(string Code,string OrganizationCode,string FacilityCode,Guid? ProgramId,
    DataMarking Markings,string CreatedBy,DateTimeOffset CreatedAt,IReadOnlyList<QmsAuditProgramRevision> Revisions);
public sealed record QmsAuditQualificationFact(string Code,string Status,DateTimeOffset? CertifiedAt,
    DateTimeOffset? ExpiresAt,bool QualifiedAsOf,QmsPlanningSourceSnapshot Source);
public sealed record QmsAuditorCompetenceAssessment(Guid Id,int Version,Guid PersonId,QmsRevisionReference AuditProgram,
    QmsAuditCompetenceDecision Decision,DateTimeOffset ValidFrom,DateTimeOffset ValidTo,string Criteria,
    string ActualAssessment,string Limitations,string AssessedBy,string? ApprovedBy,DateTimeOffset? ApprovedAt,
    IReadOnlyList<QmsPlanningSourceSnapshot> Evidence,IReadOnlyList<QmsPlanningTransition> History);
public sealed record QmsAuditCompetenceSnapshot(Guid PersonId,DateTimeOffset AsOf,
    IReadOnlyList<QmsAuditQualificationFact> Qualifications,Guid? ApprovedAssessmentId,
    string ImpartialityBasis,IReadOnlyList<string> ExcludedActors,string Fingerprint);
public sealed record QmsAuditorValidation(Guid PersonId,DateTimeOffset AsOf,QmsRevisionReference AuditProgram,
    Guid? CompetenceAssessmentId,string ImpartialityBasis,IReadOnlyList<QmsRevisionReference>? RequiredCriteria=null,
    IReadOnlyList<QmsDocumentReference>? RequiredStandardReferences=null);
public sealed record QmsAuditorValidationResult(QmsRevisionReference AuditProgram,string ApprovedScope,
    QmsAuditCompetenceSnapshot Competence,IReadOnlyList<QmsPlanningSourceSnapshot> Sources);
/// <summary>Same-UOW actual approved criteria and as-of competence; a role is never competence evidence.</summary>
public interface IAuditorCompetenceValidator
{
    Task<QmsAuditorValidationResult> ValidateAsync(IKernelUnitOfWork uow,ForgePrincipal principal,
        QmsAuditorValidation query,bool lockSources,CancellationToken ct);
}
public interface IAuditorQualificationReader
{
    Task ValidateCodesAsync(IKernelUnitOfWork uow,IReadOnlyList<string> codes,bool lockSources,CancellationToken ct);
    Task<IReadOnlyList<QmsAuditQualificationFact>> QualificationsAsync(IKernelUnitOfWork uow,
        ForgePrincipal principal,Guid personId,IReadOnlyList<string> codes,DateTimeOffset asOf,
        bool lockSources,CancellationToken ct);
}
public sealed record QmsAuditAssignment(Guid PersonId,string Responsibility);
public sealed record QmsAuditChecklistAnswer(string ChecklistCode,string ActualObservation,QmsAuditAdequacy Adequacy,
    string AdequacyBasis,string Limitations,IReadOnlyList<QmsPlanningEvidenceReference> Evidence);
public sealed record QmsAuditChecklistAnswerView(string ChecklistCode,string ActualObservation,QmsAuditAdequacy Adequacy,
    string AdequacyBasis,string Limitations,IReadOnlyList<QmsPlanningSourceSnapshot> Evidence);
public sealed record QmsAuditReport(string Summary,string ScopePerformed,string EvidenceLimitations,
    string HumanAssessment,IReadOnlyList<Guid> FindingIds,IReadOnlyList<QmsPlanningSourceSnapshot> Evidence,
    string AuthoredBy,DateTimeOffset AuthoredAt,string? ApprovedBy,DateTimeOffset? ApprovedAt,
    IReadOnlyList<QmsAuditChecklistAnswerView> ChecklistAnswers);
public sealed record QmsAuditRun(Guid Id,int Version,int Generation,QmsAuditState State,string OrganizationCode,
    string FacilityCode,Guid? ProgramId,DataMarking Markings,QmsRevisionReference AuditProgram,
    int ProgramVersion,string Scope,DateTimeOffset ScheduledFrom,DateTimeOffset ScheduledTo,
    IReadOnlyList<QmsAuditAssignment> Auditors,IReadOnlyList<QmsAuditCompetenceSnapshot> Competence,
    string CreatedBy,DateTimeOffset CreatedAt,DateTimeOffset? StartedAt,DateTimeOffset? CompletedAt,
    QmsAuditReport? Report,IReadOnlyList<QmsPlanningSourceSnapshot> Sources,IReadOnlyList<QmsPlanningTransition> History);
public sealed record QmsAuditFinding(Guid Id,Guid AuditId,int AuditGeneration,int Version,QmsAuditFindingState State,
    string CriterionCode,QmsAuditFindingKind Kind,string Statement,string ActualObservation,
    QmsAuditAdequacy Adequacy,string AdequacyBasis,string Limitations,bool? FollowUpRequired,
    IReadOnlyList<QmsPlanningSourceSnapshot> Evidence,IReadOnlyList<long> CapaIds,
    IReadOnlyList<Guid> PlanningTaskIds,string RecordedBy,DateTimeOffset RecordedAt,string? VerifiedBy,
    DateTimeOffset? VerifiedAt,string FollowUpResult,IReadOnlyList<QmsPlanningTransition> History);

/// <summary>A current-generation audit finding that passed the audit's existing source-access checks.</summary>
public sealed record QmsAuditAnalystFinding(Guid AuditId,int AuditGeneration,string AuditState,
    Guid FindingId,string CriterionCode,QmsAuditFindingKind Kind,QmsAuditFindingState State,
    string Statement,bool? FollowUpRequired,DateTimeOffset RecordedAt);

/// <summary>An overdue, unverified corrective-action record that passed current owner/source access checks.</summary>
public sealed record QmsCapaAnalystOverdueAction(long CapaId,int CapaGeneration,QmsCapaState CapaState,
    Guid ActionId,QmsCorrectiveActionKind Kind,QmsCorrectiveActionState State,string Description,
    DateTimeOffset DueAt,Guid OwnerPersonId);

/// <summary>One governed extension of the existing quality.capa ID. Correction and containment never stand in for cause/action/effectiveness.</summary>
public sealed record QmsCapaCase(long CapaId,int Version,int Generation,QmsCapaState State,string OrganizationCode,
    string FacilityCode,Guid? ProgramId,DataMarking Markings,QmsRevisionReference Process,string ProblemStatement,Guid OwnerPersonId,
    DateOnly DueDate,QmsCauseKnowledge CauseKnowledge,string? ConfirmedCause,string? ApprovedPlan,
    string InvestigationMethod,string InvestigationCriteria,string EffectivenessCriteria,string RecurrenceMonitoring,
    IReadOnlyList<long> RelatedNcrIds,IReadOnlyList<QmsPlanningSourceSnapshot> Sources,string CreatedBy,
    DateTimeOffset CreatedAt,int? ApprovedInvestigationRevision,Guid? LatestEffectivenessId,
    IReadOnlyList<QmsPlanningTransition> History,CapaInfo? LegacySnapshot=null);
public sealed record QmsCapaCauseProposal(string Description,string SupportingBasis,string ContraryEvidence,
    IReadOnlyList<QmsPlanningEvidenceReference> Evidence);
public sealed record QmsCapaInvestigationContent(string ActualInvestigation,string Method,
    IReadOnlyList<QmsCapaCauseProposal> Proposals,string? HumanDeterminedCause,string CauseDecisionBasis,
    string ActionPlan,string SystemicScope,string RiskOpportunityImpact,string VerificationCriteria,
    IReadOnlyList<QmsPlanningEvidenceReference> Evidence);
public sealed record QmsCapaInvestigation(long CapaId,int Generation,int Revision,int Version,QmsState State,
    QmsCapaInvestigationContent Content,string CreatedBy,string LastEditedBy,string? ReviewedBy,string? ApprovedBy,
    IReadOnlyList<QmsPlanningSourceSnapshot> Sources,IReadOnlyList<QmsPlanningTransition> History);
public sealed record QmsCapaAction(Guid Id,long CapaId,int Generation,int Version,QmsCorrectiveActionKind Kind,
    QmsCorrectiveActionState State,string Description,Guid OwnerPersonId,DateTimeOffset DueAt,
    string Resources,string CompletionCriteria,string Result,string CreatedBy,DateTimeOffset CreatedAt,
    string? CompletedBy,DateTimeOffset? CompletedAt,string? VerifiedBy,DateTimeOffset? VerifiedAt,
    IReadOnlyList<QmsPlanningSourceSnapshot> Evidence,IReadOnlyList<QmsPlanningTransition> History,int ActionGeneration=1);
public sealed record QmsCapaActionEvidence(Guid Id,int Version,string Fingerprint);
public sealed record QmsCapaEffectiveness(Guid Id,long CapaId,int Generation,int Version,QmsState State,
    DateTimeOffset From,DateTimeOffset To,string Method,string Criteria,string ActualResults,
    QmsEffectivenessResult Result,string Limitations,string RecurrenceReview,string FollowUpDecision,
    IReadOnlyList<QmsPlanningSourceSnapshot> Evidence,IReadOnlyList<Guid> VerifiedActionIds,
    string CreatedBy,string? ReviewedBy,string? ApprovedBy,IReadOnlyList<QmsPlanningTransition> History,
    IReadOnlyList<QmsCapaActionEvidence> ActionManifest);

public interface IAuditCapaLookup
{
    Task<QmsAuditProgram?> FindProgramAsync(string code,CancellationToken ct);
    Task<QmsPlanningPage<QmsAuditProgram>> ProgramsAsync(int take,string? after,CancellationToken ct);
    Task<QmsAuditorCompetenceAssessment?> FindCompetenceAsync(Guid id,CancellationToken ct);
    Task<QmsAuditRun?> FindAuditAsync(Guid id,CancellationToken ct);
    Task<IReadOnlyList<QmsAuditRun>> AuditVersionsAsync(Guid id,CancellationToken ct);
    Task<QmsPlanningPage<QmsAuditRun>> AuditsAsync(int take,string? after,CancellationToken ct);
    Task<QmsAuditFinding?> FindFindingAsync(Guid id,CancellationToken ct);
    Task<QmsAuditFinding?> FindFindingOriginAsync(Guid id,CancellationToken ct);
    Task<IReadOnlyList<QmsAuditFinding>> FindingsAsync(Guid audit,CancellationToken ct);
    Task<QmsCapaCase?> FindCapaAsync(long id,CancellationToken ct);
    Task<QmsPlanningPage<QmsCapaCase>> CapasAsync(int take,string? after,CancellationToken ct);
    Task<IReadOnlyList<QmsCapaInvestigation>> InvestigationsAsync(long id,int generation,CancellationToken ct);
    Task<QmsCapaAction?> FindActionAsync(Guid id,CancellationToken ct);
    Task<IReadOnlyList<QmsCapaAction>> ActionsAsync(long id,int generation,CancellationToken ct);
    Task<QmsCapaEffectiveness?> FindEffectivenessAsync(Guid id,CancellationToken ct);
    Task<IReadOnlyList<QmsCapaEffectiveness>> EffectivenessAsync(long id,int generation,CancellationToken ct);
}
internal interface IAuditCapaStore : IAuditCapaLookup
{
    Task LockAsync(string type,string key,CancellationToken ct);
    Task LockCapaAsync(long id,CancellationToken ct);
    Task SaveProgramAsync(QmsAuditProgram program,CancellationToken ct);
    Task SaveProgramRevisionAsync(QmsAuditProgramRevision revision,int? expected,CancellationToken ct);
    Task SaveCompetenceAsync(QmsAuditorCompetenceAssessment assessment,int? expected,CancellationToken ct);
    Task SaveAuditAsync(QmsAuditRun audit,int? expected,CancellationToken ct);
    Task SaveFindingAsync(QmsAuditFinding finding,int? expected,CancellationToken ct);
    Task SaveCapaAsync(QmsCapaCase capa,int? expected,CancellationToken ct);
    Task SaveInvestigationAsync(QmsCapaInvestigation investigation,int? expected,CancellationToken ct);
    Task SaveActionAsync(QmsCapaAction action,int? expected,CancellationToken ct);
    Task SaveEffectivenessAsync(QmsCapaEffectiveness effectiveness,int? expected,CancellationToken ct);
    Task<long> InsertUnknownCapaAsync(long? ncrId,string title,DateOnly dueDate,string actor,DateTimeOffset at,CancellationToken ct);
    Task UpdateCoreCapaAsync(long id,string? cause,string? action,string status,string? implementedBy,
        string? implementationNote,string? verifiedBy,DateTimeOffset at,CancellationToken ct);
}
/// <summary>Legacy APIs consult this owner guard in the same command UOW before idempotent replay.</summary>
public interface ICapaLegacyGovernanceGuard
{
    Task AssertLegacyAsync(IKernelUnitOfWork uow,CommandContext context,long capaId,CancellationToken ct);
    Task<bool> CanReadAsync(IKernelUnitOfWork uow,ForgePrincipal principal,long capaId,CancellationToken ct);
}
