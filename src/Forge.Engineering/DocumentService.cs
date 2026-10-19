using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Forge.Engineering.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Resources.Contracts;

namespace Forge.Engineering;

public sealed record StoreDocumentFile(byte[] Content, string? FileName);

public sealed record CreateDocument(string Number, string Title, DocumentType Type, DocumentAuthority Authority, string? ExternalAuthority, string? ExternalNumber, Guid? ProgramId, DataMarking Markings);

/// <summary>Changes a document's title or markings. A reason is required; markings decide who may see it.</summary>
public sealed record ChangeDocument(string Number, string Title, DataMarking Markings);

public sealed record AddDocumentRevision(string Number, string Revision, string? FileSha256, string? FileName, string? ChangeNote, DateOnly? ReviewBy);

/// <summary>Replaces the file or notes of a draft revision. Nothing else can be edited.</summary>
public sealed record UpdateDocumentDraft(string Number, string Revision, string? FileSha256, string? FileName, string? ChangeNote, DateOnly? ReviewBy);

public sealed record SubmitDocumentRevision(string Number, string Revision);

public sealed record ApproveDocumentRevision(string Number, string Revision);

public sealed record ReturnDocumentRevision(string Number, string Revision);

public sealed record ReleaseDocumentRevision(string Number, string Revision, DateOnly? EffectiveFrom);

public sealed record ObsoleteDocumentRevision(string Number, string Revision);

public sealed record AttachDocument(DocumentTargetType TargetType, string TargetId, string DocumentNumber, string? Revision, DocumentPurpose Purpose, string? Note);

/// <summary>Attaches a file to a record as a new reference document (uncontrolled, never production authority).</summary>
public sealed record AttachFile(DocumentTargetType TargetType, string TargetId, string FileSha256, string FileName, string? Title, DocumentType Type, DataMarking Markings, string? Note);

public sealed record DetachDocument(long LinkId);

public sealed record DocumentFileStored(string Sha256, long SizeBytes) : IDomainEvent
{
    public static string EventType => nameof(DocumentFileStored);

    public static int SchemaVersion => 1;
}

public sealed record DocumentCreated(string Number, string Authority, string Type) : IDomainEvent
{
    public static string EventType => nameof(DocumentCreated);

    public static int SchemaVersion => 1;
}

public sealed record DocumentChanged(string Number) : IDomainEvent
{
    public static string EventType => nameof(DocumentChanged);

    public static int SchemaVersion => 1;
}

/// <summary>A document revision moved through its lifecycle: Proposed, Submitted, Returned, Approved, Released, Applicable, Superseded or Obsoleted.</summary>
public sealed record DocumentRevisionChanged(string Number, string Revision, string Change, string Status) : IDomainEvent
{
    public static string EventType => nameof(DocumentRevisionChanged);

    public static int SchemaVersion => 1;
}

public sealed record DocumentAttached(long LinkId, string Number, string? Revision, string TargetType, string TargetId, string Purpose) : IDomainEvent
{
    public static string EventType => nameof(DocumentAttached);

    public static int SchemaVersion => 1;
}

public sealed record DocumentDetached(long LinkId, string Number, string TargetType, string TargetId) : IDomainEvent
{
    public static string EventType => nameof(DocumentDetached);

    public static int SchemaVersion => 1;
}

/// <summary>Where documents are written and changed in the same transaction as the command.</summary>
public interface IDocumentStore
{
    Task<bool> InsertFileAsync(string sha256, byte[] content, DateTimeOffset at, string actor, CancellationToken ct);

    Task<bool> InsertDocumentAsync(DocumentInfo document, CancellationToken ct);

    Task UpdateDocumentAsync(string number, string title, DataMarking markings, DateTimeOffset at, string actor, CancellationToken ct);

    Task<long> NextReferenceNumberAsync(CancellationToken ct);

    Task<bool> InsertRevisionAsync(DocumentRevisionInfo revision, CancellationToken ct);

    Task UpdateRevisionAsync(DocumentRevisionInfo revision, CancellationToken ct);

    Task<long?> InsertLinkAsync(DocumentLinkInfo link, CancellationToken ct);

    Task DetachLinkAsync(long linkId, DateTimeOffset at, string actor, string reason, CancellationToken ct);

    Task<long> InsertRequirementAsync(DocumentRequirementInfo requirement, CancellationToken ct);

    Task RemoveRequirementAsync(long requirementId, DateTimeOffset at, string actor, string reason, CancellationToken ct);

    Task<long> InsertRuleAsync(ApplicabilityRuleInfo rule, CancellationToken ct);

    Task WithdrawRuleAsync(long ruleId, DateTimeOffset at, string actor, string reason, CancellationToken ct);

    Task SetPolicyAsync(ItemClass itemClass, bool enforced, DateTimeOffset at, string actor, CancellationToken ct);

    Task<long> InsertIssueAsync(long orderNumber, DateTimeOffset at, string actor, string reason, CancellationToken ct);
}

/// <summary>
/// Controlled documents and attachments (V2.2). Internal revisions are drafted, reviewed, approved by
/// someone other than the author and released; external revisions are recorded as applicable, never
/// released; reference documents are uncontrolled attachments and never production authority. Documents
/// are invisible to people without their program or markings, everywhere.
/// </summary>
public sealed partial class DocumentService(CommandPipeline pipeline, IDocumentTargetResolver targets, ISiteClock siteClock, IGeneratedContentOwnerAccess? generatedContent = null)
{
    public const string SourceContext = EngineeringService.SourceContext;

    /// <summary>The largest file Forge stores (50 MB).</summary>
    public const int MaxFileBytes = 52_428_800;

    private const string DocumentEntity = "Document";
    private const string RevisionEntity = "DocumentRevision";
    private const string LinkEntity = "DocumentLink";
    private const string FileEntity = "DocumentFile";

    public Task<CommandResult> StoreFileAsync(StoreDocumentFile c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var content = c.Content ?? [];
        if (content.Length == 0)
        {
            throw new CommandRejectedException("empty_file", "The file is empty.");
        }

        if (content.Length > MaxFileBytes)
        {
            throw new CommandRejectedException("file_too_large", $"Files are at most {MaxFileBytes / 1_048_576} MB.");
        }

        var sha = Convert.ToHexStringLower(SHA256.HashData(content));
        var name = FileName(c.FileName);
        var spec = new CommandSpec(SourceContext, nameof(StoreDocumentFile), Permissions.DocumentsAttach, ResourceRef.Of(FileEntity, sha));
        return pipeline.ExecuteAsync(spec, new { Sha256 = sha, content.Length, FileName = name }, context, async (uow, now) =>
        {
            await RequireGeneratedContentAsync(uow, context.Principal, sha, ct);
            if (!await uow.Store<IDocumentStore>().InsertFileAsync(sha, content, now, context.Actor, ct))
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(sha, $"{name} is already stored."));
            }

            return new CommandOutcome(
                CommandResult.Ok(sha, $"{name} stored ({content.Length:N0} bytes)."),
                [pipeline.Event(new DocumentFileStored(sha, content.Length), context, SourceContext, FileEntity, sha)],
                new AuditChange(FileEntity, sha, null, CanonicalJson.Serialize(new { Sha256 = sha, SizeBytes = content.Length, FileName = name })));
        }, ct);
    }

    public Task<CommandResult> CreateDocumentAsync(CreateDocument c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var number = Number(c.Number);
        var program = c.ProgramId is { } p ? new ProgramId(p) : (ProgramId?)null;
        var spec = new CommandSpec(SourceContext, nameof(CreateDocument), Permissions.DocumentsManage, new ResourceRef(DocumentEntity, number, program, c.Markings));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            RequireKnownMarkings(c.Markings);
            var (externalAuthority, externalNumber) = c.Authority == DocumentAuthority.External
                ? (Optional(c.ExternalAuthority, 100) ?? throw new CommandRejectedException("external_authority_required", "Name who controls an external document (customer, standards body or manufacturer)."), Optional(c.ExternalNumber, 100))
                : (null, null);
            var document = new DocumentInfo(number, Title(c.Title), c.Type, c.Authority, externalAuthority, externalNumber, c.ProgramId, c.Markings, context.Actor, now, []);
            if (!await uow.Store<IDocumentStore>().InsertDocumentAsync(document, ct))
            {
                throw new CommandRejectedException("document_exists", $"Document {number} already exists.");
            }

            return new CommandOutcome(
                CommandResult.Ok(number, $"{c.Authority} document {number} created. Add a revision with its file."),
                [pipeline.Event(new DocumentCreated(number, c.Authority.ToString(), c.Type.ToString()), context, SourceContext, DocumentEntity, number, program, c.Markings)],
                new AuditChange(DocumentEntity, number, null, CanonicalJson.Serialize(document), program));
        }, ct);
    }

    public Task<CommandResult> ChangeDocumentAsync(ChangeDocument c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var number = Number(c.Number);
        var spec = new CommandSpec(SourceContext, nameof(ChangeDocument), Permissions.DocumentsManage, new ResourceRef(DocumentEntity, number, null, c.Markings), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            RequireKnownMarkings(c.Markings);
            var document = await VisibleDocumentAsync(uow, number, context, ct);
            var title = Title(c.Title);
            if (title == document.Title && c.Markings == document.Markings)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(number, "Nothing changed."));
            }

            await uow.Store<IDocumentStore>().UpdateDocumentAsync(number, title, c.Markings, now, context.Actor, ct);
            return new CommandOutcome(
                CommandResult.Ok(number, $"{number} changed."),
                [pipeline.Event(new DocumentChanged(number), context, SourceContext, DocumentEntity, number, Program(document), c.Markings | document.Markings)],
                new AuditChange(DocumentEntity, number, CanonicalJson.Serialize(new { document.Title, Markings = document.Markings.ToString() }), CanonicalJson.Serialize(new { Title = title, Markings = c.Markings.ToString() }), Program(document)));
        }, ct);
    }

    public Task<CommandResult> AddRevisionAsync(AddDocumentRevision c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var number = Number(c.Number);
        var revision = Revision(c.Revision);
        var spec = new CommandSpec(SourceContext, nameof(AddDocumentRevision), Permissions.DocumentsManage, ResourceRef.Of(RevisionEntity, $"{number}/{revision}"));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var document = await VisibleDocumentAsync(uow, number, context, ct);
            if (document.Revisions.Any(r => r.Revision == revision))
            {
                throw new CommandRejectedException("revision_exists", $"{number} already has revision {revision}.");
            }

            if (document.Authority != DocumentAuthority.Reference
                && document.Revisions.FirstOrDefault(r => r.Status is DocumentRevisionStatus.Draft or DocumentRevisionStatus.InReview or DocumentRevisionStatus.Approved) is { } open)
            {
                throw new CommandRejectedException("open_revision", $"Revision {open.Revision} is still {open.Status}; release or obsolete it before starting another.");
            }

            var (sha, name, size) = await FileAsync(uow, context.Principal, c.FileSha256, c.FileName, document.Authority == DocumentAuthority.Reference, ct);
            var status = document.Authority == DocumentAuthority.Reference ? DocumentRevisionStatus.Uncontrolled : DocumentRevisionStatus.Draft;
            var record = new DocumentRevisionInfo(number, revision, status, sha, name, size, Optional(c.ChangeNote, 2000), c.ReviewBy, context.Actor, now,
                null, null, null, null, null, null, null, null, null, null);
            await uow.Store<IDocumentStore>().InsertRevisionAsync(record, ct);
            var next = status == DocumentRevisionStatus.Draft
                ? (document.Authority == DocumentAuthority.Internal ? "Submit it for review when it is ready." : "Record it as applicable when it applies.")
                : "It is kept as reference, never as production authority.";
            return new CommandOutcome(
                CommandResult.Ok($"{number}/{revision}", $"{number} revision {revision} added ({status}). {next}"),
                [RevisionEvent(document, record, "Proposed", context)],
                new AuditChange(RevisionEntity, $"{number}/{revision}", null, CanonicalJson.Serialize(record), Program(document)));
        }, ct);
    }

    public Task<CommandResult> UpdateDraftAsync(UpdateDocumentDraft c, CommandContext context, CancellationToken ct) =>
        ChangeRevisionAsync(nameof(UpdateDocumentDraft), c.Number, c.Revision, c, context, Permissions.DocumentsManage, false, async (uow, document, current, now) =>
        {
            if (current.Status != DocumentRevisionStatus.Draft)
            {
                throw new CommandRejectedException("not_draft", $"Revision {current.Revision} is {current.Status}; only a draft is edited.");
            }

            var (sha, name, size) = await FileAsync(uow, context.Principal, c.FileSha256, c.FileName, false, ct);
            var updated = current with { FileSha256 = sha, FileName = name, FileSize = size, ChangeNote = Optional(c.ChangeNote, 2000), ReviewBy = c.ReviewBy };
            return updated == current ? null : (updated, "Edited", "draft updated");
        }, ct);

    public Task<CommandResult> SubmitAsync(SubmitDocumentRevision c, CommandContext context, CancellationToken ct) =>
        ChangeRevisionAsync(nameof(SubmitDocumentRevision), c.Number, c.Revision, c, context, Permissions.DocumentsManage, false, (_, document, current, now) =>
        {
            RequireAuthority(document, DocumentAuthority.Internal, "submitted for review");
            RequireStatus(current, DocumentRevisionStatus.Draft, "submitted");
            if (current.FileSha256 is null)
            {
                throw new CommandRejectedException("no_file", "Add the revision's file before submitting it for review.");
            }

            return Changed(current with { Status = DocumentRevisionStatus.InReview, SubmittedBy = context.Actor, SubmittedAt = now }, "Submitted", "submitted for review; someone other than the author approves it");
        }, ct);

    public Task<CommandResult> ReturnAsync(ReturnDocumentRevision c, CommandContext context, CancellationToken ct) =>
        ChangeRevisionAsync(nameof(ReturnDocumentRevision), c.Number, c.Revision, c, context, Permissions.DocumentsApprove, true, (_, document, current, _) =>
        {
            RequireStatus(current, DocumentRevisionStatus.InReview, "returned");
            return Changed(current with { Status = DocumentRevisionStatus.Draft, SubmittedBy = null, SubmittedAt = null }, "Returned", "returned to the author as a draft");
        }, ct);

    public Task<CommandResult> ApproveAsync(ApproveDocumentRevision c, CommandContext context, CancellationToken ct) =>
        ChangeRevisionAsync(nameof(ApproveDocumentRevision), c.Number, c.Revision, c, context, Permissions.DocumentsApprove, false, async (uow, document, current, now) =>
        {
            await RequireNotUnderChangeAsync(uow, document, current, ct);
            RequireStatus(current, DocumentRevisionStatus.InReview, "approved");
            RequireNotAuthor(current, context, "approve");
            return (current with { Status = DocumentRevisionStatus.Approved, ApprovedBy = context.Actor, ApprovedAt = now }, "Approved", "approved; release it to make it the revision in force");
        }, ct);

    public Task<CommandResult> ReleaseAsync(ReleaseDocumentRevision c, CommandContext context, CancellationToken ct) =>
        ChangeRevisionAsync(nameof(ReleaseDocumentRevision), c.Number, c.Revision, c, context, Permissions.DocumentsRelease, true, async (uow, document, current, now) =>
        {
            await RequireNotUnderChangeAsync(uow, document, current, ct);
            if (document.Authority == DocumentAuthority.Reference)
            {
                throw new CommandRejectedException("reference_not_controlled", $"{document.Number} is a reference document; it is never released.");
            }

            var external = document.Authority == DocumentAuthority.External;
            RequireStatus(current, external ? DocumentRevisionStatus.Draft : DocumentRevisionStatus.Approved, external ? "recorded as applicable" : "released");
            if (current.FileSha256 is null)
            {
                throw new CommandRejectedException("no_file", "Add the revision's file first.");
            }

            RequireNotAuthor(current, context, external ? "record as applicable" : "release");
            var today = siteClock.DateAt(now);
            var from = c.EffectiveFrom ?? today;
            if (from < today)
            {
                throw new CommandRejectedException("effective_in_past", "A revision cannot take effect before today.");
            }

            if (document.Current is { } previous)
            {
                if (from < previous.EffectiveFrom)
                {
                    throw new CommandRejectedException("effectivity_overlap", $"Revision {previous.Revision} is in force from {previous.EffectiveFrom:yyyy-MM-dd}; the new revision cannot start earlier.");
                }

                var superseded = previous with { Status = DocumentRevisionStatus.Superseded, EffectiveTo = from };
                await uow.Store<IDocumentStore>().UpdateRevisionAsync(superseded, ct);
                await uow.Outbox.EnqueueAsync(RevisionEvent(document, superseded, "Superseded", context), ct);
            }

            var status = external ? DocumentRevisionStatus.Applicable : DocumentRevisionStatus.Released;
            return ((DocumentRevisionInfo, string, string)?)(
                current with { Status = status, ReleasedBy = context.Actor, ReleasedAt = now, EffectiveFrom = from },
                status.ToString(),
                external ? $"recorded as the applicable {document.ExternalAuthority} revision from {from:yyyy-MM-dd}" : $"released, in force from {from:yyyy-MM-dd}");
        }, ct);

    public Task<CommandResult> ObsoleteAsync(ObsoleteDocumentRevision c, CommandContext context, CancellationToken ct) =>
        ChangeRevisionAsync(nameof(ObsoleteDocumentRevision), c.Number, c.Revision, c, context, Permissions.DocumentsRelease, true, (_, _, current, now) =>
            current.Status is DocumentRevisionStatus.Superseded or DocumentRevisionStatus.Obsolete
                ? throw new CommandRejectedException("not_current", $"Revision {current.Revision} is already {current.Status}.")
                : Changed(
                    current with
                    {
                        Status = DocumentRevisionStatus.Obsolete,
                        ObsoletedBy = context.Actor,
                        ObsoletedAt = now,
                        EffectiveTo = current.EffectiveFrom is { } f ? Max(f, siteClock.DateAt(now)) : null,
                    },
                    "Obsoleted",
                    "obsolete; it no longer applies anywhere"),
            ct);

    public Task<CommandResult> AttachAsync(AttachDocument c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var number = Number(c.DocumentNumber);
        var spec = new CommandSpec(SourceContext, nameof(AttachDocument), Permissions.DocumentsAttach, ResourceRef.Of(LinkEntity, $"{c.TargetType}:{c.TargetId}"));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var target = await VisibleTargetAsync(uow, c.TargetType, c.TargetId, context, ct);
            var document = await VisibleDocumentAsync(uow, number, context, ct);
            RequireSameProgram(document, target);
            if (target.Type is DocumentTargetType.Receipt or DocumentTargetType.ReceivingInspection or DocumentTargetType.Inspection or DocumentTargetType.Shipment or DocumentTargetType.OutsideProcess or DocumentTargetType.Fai or DocumentTargetType.SupplierQuote
                && document.Authority != DocumentAuthority.Reference)
            {
                throw new CommandRejectedException("business_evidence_reference_only", "Attach business evidence as reference files; applicable engineering instructions remain controlled separately.");
            }
            string? revision = null;
            if (!string.IsNullOrWhiteSpace(c.Revision))
            {
                revision = Revision(c.Revision);
                var pinned = document.Revisions.FirstOrDefault(r => r.Revision == revision)
                    ?? throw new CommandRejectedException("revision_not_found", $"{number} has no revision {revision}.");
                if (c.Purpose == DocumentPurpose.Follow && pinned.Status is not (DocumentRevisionStatus.Released or DocumentRevisionStatus.Applicable))
                {
                    throw new CommandRejectedException("revision_not_released", $"Revision {revision} is {pinned.Status}; only a released or applicable revision can be followed.");
                }
            }

            if (document.Authority == DocumentAuthority.Reference && c.Purpose == DocumentPurpose.Follow)
            {
                throw new CommandRejectedException("reference_not_authority", $"{number} is a reference document; attach it for information, never as something to follow.");
            }

            return await LinkAsync(uow, document, revision, target, c.Purpose, Optional(c.Note, 500), context, now, ct);
        }, ct);
    }

    public Task<CommandResult> AttachFileAsync(AttachFile c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(SourceContext, nameof(AttachFile), Permissions.DocumentsAttach, new ResourceRef(LinkEntity, $"{c.TargetType}:{c.TargetId}", null, c.Markings));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            RequireKnownMarkings(c.Markings);
            var target = await VisibleTargetAsync(uow, c.TargetType, c.TargetId, context, ct);
            var (sha, name, size) = await FileAsync(uow, context.Principal, c.FileSha256, c.FileName, true, ct);
            var store = uow.Store<IDocumentStore>();
            var number = $"REF-{await store.NextReferenceNumberAsync(ct):D6}";
            var document = new DocumentInfo(number, Title(string.IsNullOrWhiteSpace(c.Title) ? name! : c.Title), c.Type, DocumentAuthority.Reference, null, null, target.ProgramId, c.Markings | target.Markings, context.Actor, now, []);
            await store.InsertDocumentAsync(document, ct);
            var revision = new DocumentRevisionInfo(number, "1", DocumentRevisionStatus.Uncontrolled, sha, name, size, null, null, context.Actor, now,
                null, null, null, null, null, null, null, null, null, null);
            await store.InsertRevisionAsync(revision, ct);
            await uow.Outbox.EnqueueAsync(pipeline.Event(new DocumentCreated(number, nameof(DocumentAuthority.Reference), c.Type.ToString()), context, SourceContext, DocumentEntity, number, Program(document), document.Markings), ct);
            await uow.Outbox.EnqueueAsync(RevisionEvent(document, revision, "Proposed", context), ct);
            return await LinkAsync(uow, document with { Revisions = [revision] }, null, target, DocumentPurpose.Information, Optional(c.Note, 500), context, now, ct);
        }, ct);
    }

    public Task<CommandResult> DetachAsync(DetachDocument c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(SourceContext, nameof(DetachDocument), Permissions.DocumentsAttach, ResourceRef.Of(LinkEntity, c.LinkId), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var link = await uow.Store<IDocumentLookup>().FindLinkAsync(c.LinkId, ct)
                ?? throw new CommandRejectedException("attachment_not_found", $"Attachment {c.LinkId} does not exist.");
            await VisibleTargetAsync(uow, link.TargetType, link.TargetId, context, ct);
            var document = await VisibleDocumentAsync(uow, link.DocumentNumber, context, ct);
            if (link.DetachedAt is not null)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(c.LinkId.ToString(System.Globalization.CultureInfo.InvariantCulture), "Already detached."));
            }

            await uow.Store<IDocumentStore>().DetachLinkAsync(link.LinkId, now, context.Actor, context.Reason!, ct);
            var id = link.LinkId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new CommandOutcome(
                CommandResult.Ok(id, $"{link.DocumentNumber} detached from {link.TargetLabel}. It stays in that record's history."),
                [pipeline.Event(new DocumentDetached(link.LinkId, link.DocumentNumber, link.TargetType.ToString(), link.TargetId), context, SourceContext, LinkEntity, id, Program(link.ProgramId), document.Markings)],
                new AuditChange(LinkEntity, id, CanonicalJson.Serialize(link), CanonicalJson.Serialize(link with { DetachedBy = context.Actor, DetachedAt = now, DetachReason = context.Reason }), Program(link.ProgramId)));
        }, ct);
    }

    /// <summary>Whether a person may see a document: its program and markings, the same rule everywhere.</summary>
    public static bool CanSee(ForgePrincipal principal, DocumentInfo document) => DocumentAccess.CanSee(principal, document);

    /// <summary>The record a person may see, or null: it must exist, be in a program they hold, and they must hold its read permission.</summary>
    public async Task<DocumentTarget?> VisibleTargetOrNullAsync(IKernelUnitOfWork uow, ForgePrincipal principal, DocumentTargetType type, string id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var target = string.IsNullOrWhiteSpace(id) ? null : await targets.ResolveAsync(uow, type, id.Trim(), ct);
        return target is null || !principal.HasPermission(target.ReadPermission) || (target.ProgramId is { } p && !principal.Programs.Contains(new ProgramId(p))) || (target.Markings & ~principal.Markings) != DataMarking.None
            ? null
            : target;
    }

    private async Task<CommandOutcome> LinkAsync(IKernelUnitOfWork uow, DocumentInfo document, string? revision, DocumentTarget target, DocumentPurpose purpose, string? note, CommandContext context, DateTimeOffset now, CancellationToken ct)
    {
        var link = new DocumentLinkInfo(0, document.Number, revision, target.Type, target.Id, target.Label, target.ProgramId, purpose, note, context.Actor, now, null, null, null, target.Markings);
        if (await uow.Store<IDocumentStore>().InsertLinkAsync(link, ct) is not { } linkId)
        {
            return CommandOutcome.NoChange(CommandResult.NoChange(document.Number, $"{document.Number} is already attached to {target.Label}."));
        }

        var id = linkId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var what = purpose == DocumentPurpose.Follow ? "to follow" : "for information";
        return new CommandOutcome(
            CommandResult.Ok(id, $"{document.Number}{(revision is null ? string.Empty : " revision " + revision)} attached to {target.Label} {what}."),
            [pipeline.Event(new DocumentAttached(linkId, document.Number, revision, target.Type.ToString(), target.Id, purpose.ToString()), context, SourceContext, LinkEntity, id, Program(target.ProgramId), document.Markings)],
            new AuditChange(LinkEntity, id, null, CanonicalJson.Serialize(link with { LinkId = linkId }), Program(target.ProgramId)));
    }

    private Task<CommandResult> ChangeRevisionAsync<TCommand>(
        string commandType,
        string rawNumber,
        string rawRevision,
        TCommand command,
        CommandContext context,
        string permission,
        bool reasonRequired,
        Func<IKernelUnitOfWork, DocumentInfo, DocumentRevisionInfo, DateTimeOffset, Task<(DocumentRevisionInfo Updated, string Change, string Message)?>> change,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var number = Number(rawNumber);
        var revision = Revision(rawRevision);
        var id = $"{number}/{revision}";
        var spec = new CommandSpec(SourceContext, commandType, permission, ResourceRef.Of(RevisionEntity, id), reasonRequired);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var document = await VisibleDocumentAsync(uow, number, context, ct);
            var current = document.Revisions.FirstOrDefault(r => r.Revision == revision)
                ?? throw new CommandRejectedException("revision_not_found", $"{number} has no revision {revision}.");
            if (await change(uow, document, current, now) is not { } r)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(id, "Nothing changed."));
            }

            await uow.Store<IDocumentStore>().UpdateRevisionAsync(r.Updated, ct);
            return new CommandOutcome(
                CommandResult.Ok(id, $"{number} revision {revision} {r.Message}."),
                [RevisionEvent(document, r.Updated, r.Change, context)],
                new AuditChange(RevisionEntity, id, CanonicalJson.Serialize(current), CanonicalJson.Serialize(r.Updated), Program(document)));
        }, ct);
    }

    private Task<CommandResult> ChangeRevisionAsync<TCommand>(
        string commandType,
        string rawNumber,
        string rawRevision,
        TCommand command,
        CommandContext context,
        string permission,
        bool reasonRequired,
        Func<IKernelUnitOfWork, DocumentInfo, DocumentRevisionInfo, DateTimeOffset, (DocumentRevisionInfo Updated, string Change, string Message)?> change,
        CancellationToken ct) =>
        ChangeRevisionAsync(commandType, rawNumber, rawRevision, command, context, permission, reasonRequired, (uow, d, r, now) => Task.FromResult(change(uow, d, r, now)), ct);

    private EventEnvelope RevisionEvent(DocumentInfo document, DocumentRevisionInfo revision, string change, CommandContext context) =>
        pipeline.Event(
            new DocumentRevisionChanged(document.Number, revision.Revision, change, revision.Status.ToString()),
            context,
            SourceContext,
            RevisionEntity,
            $"{document.Number}/{revision.Revision}",
            Program(document),
            document.Markings);

    private static (DocumentRevisionInfo, string, string)? Changed(DocumentRevisionInfo updated, string change, string message) => (updated, change, message);

    private static async Task<DocumentInfo> VisibleDocumentAsync(IKernelUnitOfWork uow, string number, CommandContext context, CancellationToken ct)
    {
        var document = await uow.Store<IDocumentLookup>().FindDocumentAsync(number, ct);
        return document is not null && CanSee(context.Principal, document)
            ? document
            : throw new CommandRejectedException("document_not_found", $"Document {number} does not exist.");
    }

    private async Task<DocumentTarget> VisibleTargetAsync(IKernelUnitOfWork uow, DocumentTargetType type, string id, CommandContext context, CancellationToken ct) =>
        await VisibleTargetOrNullAsync(uow, context.Principal, type, id, ct)
            ?? throw new CommandRejectedException("target_not_found", $"{Describe(type)} {id} does not exist.");

    private async Task<(string? Sha, string? Name, long? Size)> FileAsync(IKernelUnitOfWork uow, ForgePrincipal principal, string? rawSha, string? rawName, bool required, CancellationToken ct)
    {
        var sha = rawSha?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(sha))
        {
            return required ? throw new CommandRejectedException("file_required", "Upload the file first.") : (null, null, null);
        }

        if (!ShaPattern().IsMatch(sha))
        {
            throw new CommandRejectedException("invalid_file", "That is not a stored file reference.");
        }

        await RequireGeneratedContentAsync(uow, principal, sha, ct);
        var file = await uow.Store<IDocumentLookup>().FindFileAsync(sha, ct)
            ?? throw new CommandRejectedException("file_not_found", "That file has not been uploaded.");
        return (sha, FileName(rawName), file.SizeBytes);
    }

    private static async Task RequireNotUnderChangeAsync(IKernelUnitOfWork uow, DocumentInfo document, DocumentRevisionInfo revision, CancellationToken ct)
    {
        if (await ChangeService.OpenChangeForAsync(uow, ChangeLineKind.DocumentRevision, document.Number, revision.Revision, ct) is { } change)
        {
            throw new CommandRejectedException("under_change", $"{document.Number} rev {revision.Revision} is part of {change}; that change approves and releases it.");
        }
    }

    private static void RequireSameProgram(DocumentInfo document, DocumentTarget target)
    {
        if (document.ProgramId is { } program && program != target.ProgramId)
        {
            throw new CommandRejectedException("program_mismatch", $"{document.Number} belongs to a program; it can only be attached to that program's records.");
        }
    }

    private static void RequireAuthority(DocumentInfo document, DocumentAuthority authority, string action)
    {
        if (document.Authority != authority)
        {
            throw new CommandRejectedException("wrong_authority", $"{document.Number} is {document.Authority}; only {authority} documents are {action}.");
        }
    }

    private static void RequireStatus(DocumentRevisionInfo revision, DocumentRevisionStatus status, string action)
    {
        if (revision.Status != status)
        {
            throw new CommandRejectedException("invalid_status", $"Revision {revision.Revision} is {revision.Status}; only a {status} revision is {action}.");
        }
    }

    private static void RequireNotAuthor(DocumentRevisionInfo revision, CommandContext context, string action)
    {
        if (revision.Author == context.Actor)
        {
            throw new CommandRejectedException("separation_of_duties", $"You wrote revision {revision.Revision}; a different person must {action} it.");
        }
    }

    private static void RequireKnownMarkings(DataMarking markings)
    {
        if ((markings & ~(DataMarking.ControlledUnclassified | DataMarking.ExportControlled | DataMarking.Proprietary)) != DataMarking.None)
        {
            throw new CommandRejectedException("invalid_markings", "Unknown data marking.");
        }
    }

    private static ProgramId? Program(DocumentInfo document) => Program(document.ProgramId);

    private static ProgramId? Program(Guid? id) => id is { } p ? new ProgramId(p) : null;

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;

    private static string Describe(DocumentTargetType type) => type switch
    {
        DocumentTargetType.ProductionOrder => "Order",
        DocumentTargetType.PurchaseOrder => "Purchase order",
        DocumentTargetType.WorkOrder => "Work order",
        DocumentTargetType.Ncr => "NCR",
        DocumentTargetType.ItemRevision => "Item revision",
        DocumentTargetType.WorkCenter => "Work center",
        _ => type.ToString(),
    };

    private static string Number(string? raw)
    {
        var number = (raw ?? string.Empty).Trim().ToUpperInvariant();
        return NumberPattern().IsMatch(number)
            ? number
            : throw new CommandRejectedException("invalid_document_number", $"Document number '{raw}' is invalid. Use 1-60 characters: A-Z, 0-9, '.', '-' or '_'.");
    }

    private static string Revision(string? raw)
    {
        var revision = (raw ?? string.Empty).Trim().ToUpperInvariant();
        return RevisionPattern().IsMatch(revision)
            ? revision
            : throw new CommandRejectedException("invalid_revision", $"Revision '{raw}' is invalid. Use 1-20 characters: A-Z, 0-9, '.', '-' or '_'.");
    }

    private static string Title(string? raw)
    {
        var title = (raw ?? string.Empty).Trim();
        return title.Length is 0 or > 200 || title.Any(char.IsControl)
            ? throw new CommandRejectedException("invalid_title", "A title is required (at most 200 characters).")
            : title;
    }

    /// <summary>A file name as shown and offered for download: no path, no control characters, at most 200 characters.</summary>
    private static string FileName(string? raw)
    {
        var name = Path.GetFileName((raw ?? string.Empty).Replace('\\', '/')).Trim();
        name = new string(name.Where(ch => !char.IsControl(ch) && ch is not ('"' or '/' or ':' or '*' or '?' or '<' or '>' or '|')).ToArray());
        return name.Length == 0 ? "file" : name.Length > 200 ? name[..200] : name;
    }

    private static string? Optional(string? value, int max)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > max ? text[..max] : text;
    }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9._-]{0,59}$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();

    [GeneratedRegex("^[A-Z0-9][A-Z0-9._-]{0,19}$", RegexOptions.CultureInvariant)]
    private static partial Regex RevisionPattern();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ShaPattern();
}
