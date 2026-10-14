using Forge.Kernel.Security;

namespace Forge.Quality.Contracts;

public enum QmsRecordKind { Profile, Process, Policy, Requirement }
public enum QmsState { Draft, Submitted, Reviewed, Approved, Effective, Superseded, Withdrawn }
public enum QmsPolicyKind { Quality, EthicalBehavior, Execution, ProductRelease, LabelCopy, AttemptRetention, OtherOrganization }
public enum QmsRequirementClass { ISO, Industry, Customer, Organization, Optional, Statutory }
public enum QmsApplicability { Applicable, NotApplicable, PendingDetermination }
public enum QmsStandardVerification { PendingAuthorizedVerification, VerifiedAuthorizedCopy }

public sealed record QmsRevisionReference(string Code, int Revision);
public sealed record QmsDocumentReference(string DocumentNumber, string Revision);
public sealed record QmsIssue(string Kind, string Description, string Basis);
public sealed record QmsInterestedParty(string Name, string Requirements, string Basis);
public sealed record QmsNonApplicability(string RequirementCode, int Revision, string Basis);
public sealed record QmsMonitoring(string Name, string Method, string DataSource, string ReviewInterval);
public sealed record QmsProfileContent(string OrganizationName, string Scope, IReadOnlyList<string> Products,
    IReadOnlyList<string> Services, IReadOnlyList<QmsIssue> Issues, IReadOnlyList<QmsInterestedParty> InterestedParties,
    IReadOnlyList<string> CustomerRequirements, IReadOnlyList<string> StatutoryRequirements, bool? ClimateRelevant,
    string ClimateBasis, IReadOnlyList<QmsNonApplicability> NonApplicability, IReadOnlyList<QmsRevisionReference> Processes,
    IReadOnlyList<QmsRevisionReference> Requirements);
public sealed record QmsProcessContent(string Name, IReadOnlyList<string> Inputs, IReadOnlyList<string> Outputs,
    Guid OwnerPersonId, IReadOnlyList<QmsRevisionReference> Dependencies, IReadOnlyList<string> Resources,
    IReadOnlyList<string> RiskConsiderations, IReadOnlyList<QmsMonitoring> Monitoring);
public sealed record QmsExecutionRules(bool? RequireQualification, bool? RequirePresentOperator, bool? RequireReleasedDocuments, bool? RevalidateAtCommit,
    QmsExecutionPresentationRules? Presentation = null);
public sealed record QmsProductReleaseRules(bool? RequireIndependentDecision, bool? RequireRequiredInspectionEvidence,
    bool? RequireDispositionedNonconformities, string ReleasePermission);
public sealed record QmsLabelCopyRules(bool? RequireCopyAuthorization, int? MaximumCopiesPerRequest, bool? RetainCopyReason,
    QmsLabelLifecycleRules? Lifecycle = null);
public sealed record QmsAttemptRetentionRules(int? MinimumRetentionDays, int? MaximumRawAttemptAgeDays,
    int? MaximumStoredRawAttempts, bool? PreserveDeniedAttempts, bool? PreserveSourceLink, bool? NeverRewriteAuditChain,
    QmsTelemetryRules? Telemetry = null);
/// <summary>Governed definitions for consumers; recording these does not disable existing manufacturing or audit guards.</summary>
public sealed record QmsPolicyRules(QmsExecutionRules? Execution = null, QmsProductReleaseRules? ProductRelease = null,
    QmsLabelCopyRules? LabelCopy = null, QmsAttemptRetentionRules? AttemptRetention = null);
public sealed record QmsPolicyContent(QmsPolicyKind PolicyKind, string Statement, Guid OwnerPersonId,
    string CommunicationMethod, string ReviewInterval, bool? AcknowledgmentRequired, QmsPolicyRules? Rules);
public sealed record QmsStandardReference(string Standard, string Edition, string Clause, QmsStandardVerification Verification,
    string VerificationBasis, QmsDocumentReference? VerificationDocument);
public sealed record QmsRequirementContent(string Title, QmsRequirementClass Classification, string RequirementText,
    QmsApplicability Applicability, string ApplicabilityBasis, Guid OwnerPersonId, IReadOnlyList<QmsStandardReference> StandardReferences);
/// <summary>Exactly one typed content section must agree with the record kind. Engineering owns document bytes and revision lifecycle.</summary>
public sealed record QmsContent(QmsProfileContent? Profile = null, QmsProcessContent? Process = null,
    QmsPolicyContent? Policy = null, QmsRequirementContent? Requirement = null, IReadOnlyList<QmsDocumentReference>? Documents = null);

public sealed record QmsSourceReference(string Kind, string Key, string Revision, Guid? ProgramId, DataMarking Markings, string? FacilityCode);
public sealed record QmsTransition(string Action, string Actor, DateTimeOffset At, string Note, string Reason,
    QmsState? FromState, QmsState ToState, int Version);
public sealed record QmsRevisionView(string Code, int Revision, int Version, QmsState State, QmsContent Content,
    string CreatedBy, string LastEditedBy, string? ReviewedBy, string? ApprovedBy, DateTimeOffset? EffectiveFrom,
    DateTimeOffset? EffectiveTo, bool RequiresLicensedVerification, IReadOnlyList<QmsSourceReference> Sources,
    IReadOnlyList<QmsTransition> History);
public sealed record QmsRecordView(string Code, QmsRecordKind Kind, string OrganizationCode, string FacilityCode,
    Guid? ProgramId, DataMarking Markings, string CreatedBy, DateTimeOffset CreatedAt, IReadOnlyList<QmsRevisionView> Revisions);

/// <summary>Quality owns QMS records. Readers must check current permissions for both the record and its recorded sources.</summary>
public interface IQmsLookup
{
    Task<QmsRecordView?> FindAsync(string code, CancellationToken ct);
    Task<IReadOnlyList<QmsRecordView>> ListAsync(QmsRecordKind? kind, CancellationToken ct);
}

internal interface IQmsStore : IQmsLookup
{
    Task LockAsync(string code, CancellationToken ct);
    Task LockSourceAsync(string kind, string code, string revision, CancellationToken ct);
    Task InsertRecordAsync(QmsRecordView record, CancellationToken ct);
    Task InsertRevisionAsync(QmsRevisionView revision, CancellationToken ct);
    Task UpdateRevisionAsync(QmsRevisionView revision, int expectedVersion, bool replaceSources, CancellationToken ct);
}
