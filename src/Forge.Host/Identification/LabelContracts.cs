using Forge.Engineering.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Events;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;

namespace Forge.Host.Identification;

public enum LabelSymbology { Code128, QR, DataMatrix }
public enum LabelTemplateState { Draft, Approved, Withdrawn }
public enum LabelCopyState { PendingApproval, Active, Replaced, Voided }
public enum LabelRelationship { Original, Reprint, Replacement }
public enum LabelPrintState { Requested, Sent, SpoolAccepted, Confirmed, Failed, Uncertain }
public enum IdentificationAliasState { Active, Voided }

public sealed record LabelTransition(string Action, int Version, string? FromState, string ToState, string Actor,
    DateTimeOffset At, string Reason, string? Observation = null);
public sealed record PresentationReadSource(string Permission, ResourceRef Resource);
public sealed record IdentificationSubject(ResolvedIdentification Display, string CanonicalSubjectCode, string SourceType,
    string SourceId, string ReadPermission, IReadOnlyList<ResourceRef> Resources, Guid? LabelId = null, Guid? AliasId = null,
    string? PresentationCode = null, string? PresentedKind = null, string? ActivityCode = null, string? MachineCode = null,
    string? PresentationFingerprint = null, IReadOnlyList<PresentationReadSource>? ReadSources = null);
public sealed record LabelSubjectSnapshot(ResolvedIdentification Display, string CanonicalSubjectCode,
    string ReadPermission, IReadOnlyList<ResourceRef> Resources, string? PresentationCode = null, string? PresentedKind = null);

public sealed record LabelTemplateView(string Code, int Revision, int Version, LabelTemplateState State, int Width, int Height,
    IReadOnlyList<string> AllowedKinds, IReadOnlyList<LabelSymbology> AllowedSymbologies, string FacilityCode,
    IReadOnlyList<QmsDocumentReference> Documents, string CreatedBy, string LastEditedBy, string? ApprovedBy,
    DateTimeOffset CreatedAt, DateTimeOffset? ApprovedAt, IReadOnlyList<PresentationReadSource> Sources);
public sealed record LabelIssuanceView(Guid IssuanceId, string SubjectCode, string TemplateCode, int TemplateVersion,
    LabelSymbology Symbology, string FacilityCode, int Copies, string CreatedBy, DateTimeOffset CreatedAt,
    string PolicyCode, int PolicyRevision, string PolicyFingerprint, Guid? PreviousLabelId = null,
    LabelRelationship Relationship = LabelRelationship.Original, LabelSubjectSnapshot? Snapshot = null,
    IReadOnlyList<PresentationReadSource>? Sources = null);
public sealed record LabelCopyView(Guid LabelId, Guid IssuanceId, string Code, string SubjectCode, ResolvedIdentification Subject,
    string TemplateCode, int TemplateVersion, LabelSymbology Symbology, string FacilityCode, LabelCopyState State, int Version,
    string CreatedBy, DateTimeOffset CreatedAt, string? ApprovedBy, DateTimeOffset? ApprovedAt, Guid? PreviousLabelId,
    LabelRelationship Relationship, string PolicyCode, int PolicyRevision, string PolicyFingerprint,
    LabelSubjectSnapshot Snapshot, IReadOnlyList<PresentationReadSource> Sources);
public sealed record LabelPrintAttemptView(Guid AttemptId, Guid LabelId, int Version, LabelPrintState State,
    string? PrinterCode, string RequestedBy, DateTimeOffset RequestedAt, string? Observation, string? OutcomeBy,
    DateTimeOffset? OutcomeAt, string ArtifactSha256, bool ObservedPrinted, bool ObservedAttached,
    string TemplateHash, string PolicyFingerprint, LabelSubjectSnapshot? Snapshot = null,
    IReadOnlyList<PresentationReadSource>? Sources = null);
public sealed record IdentificationAliasView(Guid AliasId, string Domain, string Alias, string Code, string SubjectCode,
    ResolvedIdentification Subject, string FacilityCode, IdentificationAliasState State, int Version, string CreatedBy,
    DateTimeOffset CreatedAt, Guid? PreviousAliasId, string? VoidedBy, DateTimeOffset? VoidedAt,
    LabelSubjectSnapshot Snapshot, IReadOnlyList<PresentationReadSource> Sources, string PolicyCode,
    int PolicyRevision, string PolicyFingerprint);
public sealed record PresentationEnvelope<T>(T Record, IReadOnlyList<LabelTransition> Transitions);
public sealed record IdentificationPage<T>(IReadOnlyList<T> Items, string? NextCursor);
public sealed record LabelPolicyCapabilities(bool Available, bool? RequireCopyAuthorization = null,
    bool? RequireIndependentCopyAuthorization = null, string? RequiredConfirmation = null,
    bool? AllowReprint = null, bool? AllowReplacement = null, bool? AllowVoid = null);
public sealed record LabelDetail(LabelCopyView Label, IReadOnlyList<LabelTransition> Transitions,
    IReadOnlyList<LabelPrintAttemptView> PrintAttempts, IReadOnlyList<string> AllowedActions, LabelPolicyCapabilities Policy,
    ResolvedIdentification? CurrentSubject = null);
public sealed record IdentificationPresentationChanged(string Kind, string Key, string Action, int Version) : IDomainEvent
{
    public static string EventType => nameof(IdentificationPresentationChanged);
    public static int SchemaVersion => 1;
}

public sealed record CreateLabelTemplate(string Code, string FacilityCode, int Width, int Height,
    IReadOnlyList<string> AllowedKinds, IReadOnlyList<LabelSymbology> AllowedSymbologies, IReadOnlyList<QmsDocumentReference> Documents);
public sealed record ReviseLabelTemplate(string Code, int ExpectedVersion, int Width, int Height,
    IReadOnlyList<string> AllowedKinds, IReadOnlyList<LabelSymbology> AllowedSymbologies, IReadOnlyList<QmsDocumentReference> Documents);
public sealed record TransitionLabelTemplate(string Code, int Revision, int ExpectedVersion, bool Withdraw);
public sealed record IssueLabels(string SubjectCode, string TemplateCode, int TemplateVersion, LabelSymbology Symbology,
    int Copies, string FacilityCode);
public sealed record ChangeLabelCopy(Guid LabelId, int ExpectedVersion, string Action);
public sealed record RequestLabelPrint(Guid LabelId, int ExpectedVersion, string? PrinterCode);
public sealed record ObserveLabelPrint(Guid AttemptId, int ExpectedVersion, LabelPrintState Outcome, string Observation,
    bool ObservedPrinted, bool ObservedAttached);
public sealed record CreateIdentificationAlias(string Domain, string Alias, string SubjectCode, string FacilityCode);
public sealed record ReplaceIdentificationAlias(Guid AliasId, int ExpectedVersion, string SubjectCode, string FacilityCode);
public sealed record VoidIdentificationAlias(Guid AliasId, int ExpectedVersion);

public interface IIdentificationPresentationStore
{
    Task LockAsync(string key, CancellationToken ct);
    Task<PresentationEnvelope<T>?> FindAsync<T>(string kind, string key, CancellationToken ct);
    Task<IReadOnlyList<T>> HistoryAsync<T>(string kind, string key, CancellationToken ct);
    Task<IReadOnlyList<T>> ListAsync<T>(string kind, string? subjectCode, int take, string? afterKey, CancellationToken ct);
    Task<IReadOnlyList<T>> SubjectRecordsAsync<T>(string kind, string subjectCode, CancellationToken ct);
    Task<IdentificationAliasView?> FindActiveAliasAsync(string ns, string domain, string alias, CancellationToken ct);
    Task<IReadOnlyList<LabelPrintAttemptView>> FindArtifactOwnersAsync(string sha256, CancellationToken ct);
    Task AppendAsync<T>(string kind, string key, string ns, string? subjectCode, int version, string state,
        T record, LabelTransition transition, CancellationToken ct, string? aliasDomain = null, string? aliasValue = null);
}
