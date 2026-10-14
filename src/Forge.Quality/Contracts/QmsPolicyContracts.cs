using Forge.Kernel.Access;
using Forge.Kernel.Security;

namespace Forge.Quality.Contracts;

public enum QmsPolicyPurpose { Execution, ProductRelease, LabelCopy, AttemptRetention }
public enum QmsRequiredScanKind { Order, Operation, Material, Location, Machine, WorkCenter, Tool, Gauge, ControlledInstruction, Container }
public enum QmsProviderAssurance { Authenticated, StepUp }
public enum QmsActiveCopyMode { SingleActive, MultipleActive }
public enum QmsLabelSymbology { Code128, QR, DataMatrix, GS1DataMatrix }
public enum QmsLabelConfirmation { None, OperatorObservedPrinting, OperatorObservedPrintingAndAttachment }
public enum QmsTelemetryPayloadMode { NoPayload, NormalizedReferencesOnly }

/// <summary>Owner-resolved facts. Process/risk and policy choice deliberately cannot be supplied by an execution client.</summary>
public sealed record QmsPolicyContext(string FacilityCode, Guid? ProgramId = null, string? WorkCenterCode = null,
    string? ActivityCode = null, string? MachineCode = null, int? OperationSequence = null,
    string? PartNumber = null, string? PartRevision = null, string? SubjectKind = null);
public sealed record QmsOperationSelector(string? WorkCenterCode = null, string? ActivityCode = null,
    string? MachineCode = null, int? OperationSequence = null, string? PartNumber = null, string? PartRevision = null);
public sealed record QmsOperationScanRule(string Code, QmsOperationSelector Selector, QmsRevisionReference? Process,
    string? RiskClassCode, IReadOnlyList<QmsRequiredScanKind> RequiredScans, bool? AllowManualSelection,
    bool? RequireInstructionRevision, bool? RequireTrustedTerminalContext);
public sealed record QmsExecutionPresentationRules(int? SessionExpirySeconds, bool? RequireServerSession,
    bool? RequireCurrentAuthenticatedOperator, bool? RequireTrustedWorkstation, QmsProviderAssurance? ProviderAssurance,
    IReadOnlyList<QmsOperationScanRule> OperationRules);
public sealed record QmsLabelTemplateReference(string Code, int Version);
public sealed record QmsLabelLifecycleRules(QmsActiveCopyMode? ActiveCopyMode, int? MaximumActiveCopies,
    bool? AllowReprint, bool? AllowReplacement, bool? AllowVoid, bool? ReplacementVoidsPriorCopy,
    bool? RequireIndependentCopyAuthorization, QmsLabelConfirmation? RequiredConfirmation,
    IReadOnlyList<string>? AllowedSubjectKinds, IReadOnlyList<QmsLabelTemplateReference>? AllowedTemplates,
    IReadOnlyList<QmsLabelSymbology>? AllowedSymbologies, bool? AllowAliasCreate, bool? AllowAliasReplacement, bool? AllowAliasVoid);
/// <summary>Optional telemetry never stores unknown raw payloads, badge/PIN/token values, or provider claims.</summary>
public sealed record QmsTelemetryRules(QmsTelemetryPayloadMode? PayloadMode, int? MaximumCapturedReferenceCharacters,
    bool? RetainDeclaredAdapter, bool? RetainDeclaredDevice, bool? RetainCaptureMethod);

/// <summary>Human governance binds an exact policy to actual manufacturing selectors and declares process/risk with a basis.</summary>
public sealed record QmsPolicyAdoptionContent(QmsPolicyPurpose Purpose, string PolicyCode, int PolicyRevision,
    QmsOperationSelector Selector, QmsRevisionReference? Process, string? RiskClassCode, string? RiskBasis, string Basis);
public sealed record QmsPolicyAdoptionRevision(string Code, int Revision, int Version, QmsState State,
    QmsPolicyAdoptionContent Content, string CreatedBy, string LastEditedBy, string? ReviewedBy, string? ApprovedBy,
    DateTimeOffset? EffectiveFrom, DateTimeOffset? EffectiveTo, IReadOnlyList<QmsSourceReference> Sources,
    IReadOnlyList<QmsTransition> History);
public sealed record QmsPolicyAdoptionView(string Code, string OrganizationCode, string FacilityCode, Guid? ProgramId,
    DataMarking Markings, string CreatedBy, DateTimeOffset CreatedAt, IReadOnlyList<QmsPolicyAdoptionRevision> Revisions);
public sealed record QmsPolicyReadSource(string Permission, ResourceRef Resource);
public sealed record QmsAdoptedPolicy(string AdoptionCode, int AdoptionRevision, int AdoptionVersion, string PolicyCode,
    int PolicyRevision, int PolicyVersion, string OrganizationCode, QmsPolicyContext Context, QmsOperationSelector Selector,
    QmsRevisionReference? Process, string? RiskClassCode, string? RiskBasis, DateTimeOffset EffectiveFrom,
    DateTimeOffset? EffectiveTo, DataMarking Markings, QmsPolicyRules Rules, QmsOperationScanRule? OperationRule,
    string Fingerprint, IReadOnlyList<QmsPolicyReadSource> Sources)
{
    public string Code => PolicyCode;
    public int Revision => PolicyRevision;
    public int Version => PolicyVersion;
    public string FacilityCode => Context.FacilityCode;
    public Guid? ProgramId => Context.ProgramId;
}

public interface IQmsPolicyAdoptionLookup
{
    Task<QmsPolicyAdoptionView?> FindAsync(string code, CancellationToken ct);
    Task<IReadOnlyList<QmsPolicyAdoptionView>> ListAsync(CancellationToken ct);
}

/// <summary>Same-UOW current governed controls. Callers must supply actual owner context and separately authorize their business action.</summary>
public interface IQmsPolicyReader
{
    Task<QmsAdoptedPolicy> RequireAsync(IKernelUnitOfWork uow, ForgePrincipal principal, QmsPolicyPurpose purpose,
        QmsPolicyContext context, DateTimeOffset at, CancellationToken ct);
    /// <summary>Null only until the actual context has ever had an Effective adoption. Unapproved drafts create no operational authority.</summary>
    Task<QmsAdoptedPolicy?> TryAsync(IKernelUnitOfWork uow, ForgePrincipal principal, QmsPolicyPurpose purpose,
        QmsPolicyContext context, DateTimeOffset at, CancellationToken ct);
}

internal interface IQmsPolicyAdoptionStore : IQmsPolicyAdoptionLookup
{
    Task LockAsync(string code, CancellationToken ct);
    Task LockScopeAsync(string organization, string facility, QmsPolicyPurpose purpose, bool shared, CancellationToken ct);
    Task LockReadAsync(string code, int revision, CancellationToken ct);
    Task InsertAsync(QmsPolicyAdoptionView adoption, CancellationToken ct);
    Task InsertRevisionAsync(QmsPolicyAdoptionRevision revision, CancellationToken ct);
    Task UpdateAsync(QmsPolicyAdoptionRevision revision, int expectedVersion, bool replaceSources, CancellationToken ct);
}
