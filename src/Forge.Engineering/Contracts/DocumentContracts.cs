using Forge.Kernel.Access;
using Forge.Kernel.Security;

namespace Forge.Engineering.Contracts;

/// <summary>Who controls a document's content (V2 §2).</summary>
public enum DocumentAuthority
{
    /// <summary>We control it: drafted, reviewed, approved by someone other than the author, released.</summary>
    Internal,

    /// <summary>A customer, standards body or manufacturer controls it. Forge records which revision applies; it never releases it.</summary>
    External,

    /// <summary>An uncontrolled attachment (customer PO, photo, certificate, note). Never production authority.</summary>
    Reference,
}

public enum DocumentType
{
    Drawing,
    Specification,
    Procedure,
    WorkInstruction,
    InspectionPlan,
    Standard,
    Datasheet,
    Certificate,
    PurchaseDocument,
    Contract,
    Photo,
    Other,
}

public enum DocumentRevisionStatus
{
    Draft,
    InReview,
    Approved,
    Released,

    /// <summary>An external document's revision recorded as the one that applies.</summary>
    Applicable,
    Superseded,
    Obsolete,

    /// <summary>A reference attachment: kept, never controlled.</summary>
    Uncontrolled,
}

/// <summary>The kinds of record a document can be attached to.</summary>
public enum DocumentTargetType
{
    Item,
    ItemRevision,
    Supplier,
    Program,
    ProductionOrder,
    PurchaseOrder,
    WorkOrder,
    Ncr,
    WorkCenter,
}

public enum DocumentPurpose
{
    /// <summary>The record must also follow this document.</summary>
    Follow,

    /// <summary>For information only.</summary>
    Information,
}

public sealed record DocumentFileInfo(string Sha256, long SizeBytes, string StoredBy, DateTimeOffset StoredAt);

public sealed record DocumentRevisionInfo(
    string DocumentNumber,
    string Revision,
    DocumentRevisionStatus Status,
    string? FileSha256,
    string? FileName,
    long? FileSize,
    string? ChangeNote,
    DateOnly? ReviewBy,
    string Author,
    DateTimeOffset AuthoredAt,
    string? SubmittedBy,
    DateTimeOffset? SubmittedAt,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    string? ReleasedBy,
    DateTimeOffset? ReleasedAt,
    DateOnly? EffectiveFrom,
    DateOnly? EffectiveTo,
    string? ObsoletedBy,
    DateTimeOffset? ObsoletedAt);

public sealed record DocumentInfo(
    string Number,
    string Title,
    DocumentType Type,
    DocumentAuthority Authority,
    string? ExternalAuthority,
    string? ExternalNumber,
    Guid? ProgramId,
    DataMarking Markings,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    IReadOnlyList<DocumentRevisionInfo> Revisions)
{
    /// <summary>The released (internal) or applicable (external) revision, if any. Reference documents have none.</summary>
    public DocumentRevisionInfo? Current => Revisions.FirstOrDefault(r => r.Status is DocumentRevisionStatus.Released or DocumentRevisionStatus.Applicable);

    /// <summary>The newest revision of a reference document.</summary>
    public DocumentRevisionInfo? Latest => Revisions.Where(r => r.Status != DocumentRevisionStatus.Obsolete).OrderByDescending(r => r.AuthoredAt).FirstOrDefault();
}

public sealed record DocumentLinkInfo(
    long LinkId,
    string DocumentNumber,
    string? Revision,
    DocumentTargetType TargetType,
    string TargetId,
    string TargetLabel,
    Guid? ProgramId,
    DocumentPurpose Purpose,
    string? Note,
    string LinkedBy,
    DateTimeOffset LinkedAt,
    string? DetachedBy,
    DateTimeOffset? DetachedAt,
    string? DetachReason);

/// <summary>A record a document is attached to, as its owning context describes it.</summary>
/// <param name="ReadPermission">The permission needed to see the record (and so its attachments).</param>
public sealed record DocumentTarget(DocumentTargetType Type, string Id, string Label, Guid? ProgramId, string ReadPermission);

/// <summary>
/// Finds the record a document is attached to. Implemented by the host, which can see every context's
/// contracts; Engineering does not depend on the contexts that own orders, purchase orders or NCRs.
/// </summary>
public interface IDocumentTargetResolver
{
    Task<DocumentTarget?> ResolveAsync(IKernelUnitOfWork uow, DocumentTargetType type, string id, CancellationToken ct);
}

/// <summary>A document an item revision is built to. No revision pinned = the revision that applies; no operation = the whole item.</summary>
public sealed record DocumentRequirementInfo(
    long RequirementId,
    string PartNumber,
    string Revision,
    string DocumentNumber,
    string? DocumentRevision,
    int? OperationSequence,
    string? Note,
    string AddedBy,
    DateTimeOffset AddedAt,
    string? RemovedBy,
    DateTimeOffset? RemovedAt,
    string? RemovedReason)
{
    public bool IsActiveAt(DateTimeOffset at) => AddedAt <= at && (RemovedAt is null || at < RemovedAt);
}

/// <summary>What an applicability rule tests (V2 §7). Serial and lot conditions arrive with genealogy.</summary>
public enum ApplicabilityCondition
{
    /// <summary>Orders for one program.</summary>
    Program,

    /// <summary>Orders for one customer (the program's customer).</summary>
    Customer,

    /// <summary>Orders due within a date range.</summary>
    DueDate,

    /// <summary>One order, under a deviation or concession: the most specific rule.</summary>
    Order,
}

public sealed record ApplicabilityRuleInfo(
    long RuleId,
    string DocumentNumber,
    string Revision,
    ApplicabilityCondition Condition,
    Guid? ProgramId,
    string? Customer,
    DateOnly? DueFrom,
    DateOnly? DueTo,
    long? OrderNumber,
    string? Reference,
    string? Note,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string? WithdrawnBy,
    DateTimeOffset? WithdrawnAt,
    string? WithdrawnReason)
{
    public bool IsActiveAt(DateTimeOffset at) => CreatedAt <= at && (WithdrawnAt is null || at < WithdrawnAt);
}

/// <summary>An order's documentation reissued after release: it then references what applied at <see cref="IssuedAt"/>.</summary>
public sealed record OrderDocumentationIssue(long IssueId, long OrderNumber, DateTimeOffset IssuedAt, string IssuedBy, string Reason);

/// <summary>Whether documentation blocks readiness for an item class. A class with no policy is not enforced.</summary>
public sealed record DocumentationPolicyInfo(ItemClass ItemClass, bool Enforced, string ChangedBy, DateTimeOffset ChangedAt);

/// <summary>Who may see a document: its program and its markings. The same rule in screens, reports, exports and Analyst.</summary>
public static class DocumentAccess
{
    public static bool CanSee(ForgePrincipal principal, DocumentInfo document)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(document);
        return (document.ProgramId is not { } p || principal.Programs.Contains(new Forge.Kernel.Identity.ProgramId(p)))
            && (document.Markings & ~principal.Markings) == DataMarking.None;
    }
}

public interface IDocumentLookup
{
    Task<IReadOnlyList<DocumentInfo>> ListDocumentsAsync(CancellationToken ct);

    Task<DocumentInfo?> FindDocumentAsync(string number, CancellationToken ct);

    /// <summary>Links by target, by document, or both; detached links only when asked (history).</summary>
    Task<IReadOnlyList<DocumentLinkInfo>> ListLinksAsync(DocumentTargetType? targetType, string? targetId, string? documentNumber, bool includeDetached, CancellationToken ct);

    Task<DocumentLinkInfo?> FindLinkAsync(long linkId, CancellationToken ct);

    /// <summary>Document requirements of item revisions (removed ones only when asked: history).</summary>
    Task<IReadOnlyList<DocumentRequirementInfo>> ListRequirementsAsync(string? partNumber, string? revision, bool includeRemoved, CancellationToken ct);

    /// <summary>Applicability rules of one document or all (withdrawn ones only when asked: history).</summary>
    Task<IReadOnlyList<ApplicabilityRuleInfo>> ListRulesAsync(string? documentNumber, bool includeWithdrawn, CancellationToken ct);

    Task<IReadOnlyList<DocumentationPolicyInfo>> ListPoliciesAsync(CancellationToken ct);

    Task<IReadOnlyList<OrderDocumentationIssue>> ListIssuesAsync(long orderNumber, CancellationToken ct);

    Task<DocumentFileInfo?> FindFileAsync(string sha256, CancellationToken ct);

    Task<byte[]?> ReadFileAsync(string sha256, CancellationToken ct);
}
