using System.Globalization;
using Forge.Engineering;
using Forge.Engineering.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Planning;
using Microsoft.AspNetCore.Http.Features;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record DocumentRequest(string Number, string Title, DocumentType Type, DocumentAuthority Authority, string? ExternalAuthority, string? ExternalNumber, Guid? ProgramId, DataMarking? Markings, string? Reason);

public sealed record DocumentChangeRequest(string Title, DataMarking? Markings, string? Reason);

public sealed record DocumentRevisionRequest(string Revision, string? FileSha256, string? FileName, string? ChangeNote, DateOnly? ReviewBy, string? Reason);

public sealed record DocumentDraftRequest(string? FileSha256, string? FileName, string? ChangeNote, DateOnly? ReviewBy, string? Reason);

public sealed record DocumentReleaseRequest(DateOnly? EffectiveFrom, string? Reason);

public sealed record AttachDocumentRequest(string DocumentNumber, string? Revision, DocumentPurpose? Purpose, string? Note, string? Reason);

public sealed record AttachFileRequest(string FileSha256, string FileName, string? Title, DocumentType? Type, DataMarking? Markings, string? Note, string? Reason);

/// <summary>The revision a link shows: the pinned one, or the document's current (or, for reference documents, latest) revision.</summary>
public sealed record AttachmentView(
    long LinkId,
    string DocumentNumber,
    string Title,
    DocumentType Type,
    DocumentAuthority Authority,
    DataMarking Markings,
    DocumentPurpose Purpose,
    string? PinnedRevision,
    DocumentRevisionInfo? Revision,
    string State,
    string? Note,
    string LinkedBy,
    DateTimeOffset LinkedAt,
    string? DetachedBy,
    DateTimeOffset? DetachedAt,
    string? DetachReason);

public sealed record DocumentRequirementRequest(string DocumentNumber, string? DocumentRevision, int? OperationSequence, string? Note, string? Reason);

public sealed record RuleRequest(string Revision, ApplicabilityCondition Condition, Guid? ProgramId, string? Customer, DateOnly? DueFrom, DateOnly? DueTo, long? OrderNumber, string? Reference, string? Note, string? Reason);

public sealed record EnforcementRequest(bool Enforced, string? Reason);

public sealed record DocumentDetailView(DocumentInfo Document, IReadOnlyList<DocumentLinkInfo> Links);

public sealed record EvidenceVerificationRequest(string Revision, string SourceSigner, DateTimeOffset SourceSignedAt, string Meaning, string? Reason);

/// <summary>V2.2 endpoints: controlled documents, their files, and documents attached to records.</summary>
public static class DocumentEndpoints
{
    public const string FileNameHeader = "X-File-Name";

    public static void MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var eng = app.MapGroup("/api/engineering").RequireAuthorization();

        eng.MapPost("/documents/{number}/verify-signature", (HttpContext http, string number, EvidenceVerificationRequest b, EvidenceService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.VerifyAsync(new VerifySignedEvidence(number, b.Revision, b.SourceSigner, b.SourceSignedAt, b.Meaning), c, ct)));
        eng.MapGet("/documents/{number}/signature-verification/{revision}", (HttpContext http, string number, string revision, CancellationToken _) =>
            Read(http, Permissions.EngineeringRead, async principal =>
            {
                await using var uow = await Snapshot(http);
                var document = await VisibleDocumentAsync(uow, principal, number, http.RequestAborted);
                return await uow.Store<IEvidenceLookup>().FindVerificationAsync(document.Number, revision.ToUpperInvariant(), http.RequestAborted);
            }));

        eng.MapGet("/documents", (HttpContext http, CancellationToken _) =>
            Read(http, Permissions.EngineeringRead, async principal =>
            {
                await using var uow = await Snapshot(http);
                return (await uow.Store<IDocumentLookup>().ListDocumentsAsync(http.RequestAborted)).Where(d => DocumentService.CanSee(principal, d)).ToList();
            }));
        eng.MapGet("/documents/{number}", (HttpContext http, string number, CancellationToken _) =>
            Read(http, Permissions.EngineeringRead, async principal =>
            {
                await using var uow = await Snapshot(http);
                var ct = http.RequestAborted;
                var document = await VisibleDocumentAsync(uow, principal, number, ct);
                var links = (await uow.Store<IDocumentLookup>().ListLinksAsync(null, null, document.Number, true, ct)).Where(l => CanSeeLink(principal, l)).ToList();
                return new DocumentDetailView(document, links);
            }));
        eng.MapGet("/documents/{number}/revisions/{rev}/file", async (HttpContext http, string number, string rev, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null)
            {
                return NotRegistered();
            }

            var decision = http.RequestServices.GetRequiredService<IForgeAuthorizer>().Authorize(principal, Permissions.EngineeringRead, ResourceRef.Of("DocumentFile"));
            if (!decision.IsPermitted)
            {
                throw new ForgeAuthorizationException(decision);
            }

            await using var uow = await Snapshot(http);
            var document = await VisibleDocumentAsync(uow, principal, number, ct);
            var revision = document.Revisions.FirstOrDefault(r => string.Equals(r.Revision, rev, StringComparison.OrdinalIgnoreCase) && r.FileSha256 is not null)
                ?? throw new CommandRejectedException("file_not_found", $"{document.Number} revision {rev} has no file.");
            var bytes = await uow.Store<IDocumentLookup>().ReadFileAsync(revision.FileSha256!, ct)
                ?? throw new CommandRejectedException("file_not_found", "The file is missing.");
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            http.Response.Headers["Content-Security-Policy"] = "sandbox";
            return Results.File(bytes, "application/octet-stream", revision.FileName ?? "file");
        });

        eng.MapPost("/document-files", async (HttpContext http, DocumentService s, CancellationToken ct) =>
        {
            var size = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (size is { IsReadOnly: false })
            {
                size.MaxRequestBodySize = DocumentService.MaxFileBytes + 1024;
            }

            if (http.Request.ContentLength > DocumentService.MaxFileBytes)
            {
                throw new CommandRejectedException("file_too_large", $"Files are at most {DocumentService.MaxFileBytes / 1_048_576} MB.");
            }

            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await http.Request.Body.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > DocumentService.MaxFileBytes)
                {
                    throw new CommandRejectedException("file_too_large", $"Files are at most {DocumentService.MaxFileBytes / 1_048_576} MB.");
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
            }

            var name = Uri.UnescapeDataString(http.Request.Headers[FileNameHeader].ToString());
            return await Command(http, null, c => s.StoreFileAsync(new StoreDocumentFile(buffer.ToArray(), name), c, ct));
        });

        eng.MapPost("/documents", (HttpContext http, DocumentRequest b, DocumentService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CreateDocumentAsync(new CreateDocument(b.Number, b.Title, b.Type, b.Authority, b.ExternalAuthority, b.ExternalNumber, b.ProgramId, b.Markings ?? DataMarking.None), c, ct)));
        eng.MapPost("/documents/{number}", (HttpContext http, string number, DocumentChangeRequest b, DocumentService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ChangeDocumentAsync(new ChangeDocument(number, b.Title, b.Markings ?? DataMarking.None), c, ct)));
        eng.MapPost("/documents/{number}/revisions", (HttpContext http, string number, DocumentRevisionRequest b, DocumentService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.AddRevisionAsync(new AddDocumentRevision(number, b.Revision, b.FileSha256, b.FileName, b.ChangeNote, b.ReviewBy), c, ct)));
        eng.MapPost("/documents/{number}/revisions/{rev}", (HttpContext http, string number, string rev, DocumentDraftRequest b, DocumentService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.UpdateDraftAsync(new UpdateDocumentDraft(number, rev, b.FileSha256, b.FileName, b.ChangeNote, b.ReviewBy), c, ct)));
        eng.MapPost("/documents/{number}/revisions/{rev}/submit", (HttpContext http, string number, string rev, ReasonRequest b, DocumentService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SubmitAsync(new SubmitDocumentRevision(number, rev), c, ct)));
        eng.MapPost("/documents/{number}/revisions/{rev}/return", (HttpContext http, string number, string rev, ReasonRequest b, DocumentService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ReturnAsync(new ReturnDocumentRevision(number, rev), c, ct)));
        eng.MapPost("/documents/{number}/revisions/{rev}/approve", (HttpContext http, string number, string rev, ReasonRequest b, DocumentService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ApproveAsync(new ApproveDocumentRevision(number, rev), c, ct)));
        eng.MapPost("/documents/{number}/revisions/{rev}/release", (HttpContext http, string number, string rev, DocumentReleaseRequest b, DocumentService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ReleaseAsync(new ReleaseDocumentRevision(number, rev, b.EffectiveFrom), c, ct)));
        eng.MapPost("/documents/{number}/revisions/{rev}/obsolete", (HttpContext http, string number, string rev, ReasonRequest b, DocumentService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ObsoleteAsync(new ObsoleteDocumentRevision(number, rev), c, ct)));

        eng.MapGet("/attachments/{type}/{id}", (HttpContext http, string type, string id, bool? history, DocumentService s, CancellationToken _) =>
            Read(http, Permissions.EngineeringRead, async principal =>
            {
                await using var uow = await Snapshot(http);
                var ct = http.RequestAborted;
                var target = await s.VisibleTargetOrNullAsync(uow, principal, TargetType(type), id, ct)
                    ?? throw new CommandRejectedException("target_not_found", $"{type} {id} does not exist.");
                var lookup = uow.Store<IDocumentLookup>();
                var links = await lookup.ListLinksAsync(target.Type, target.Id, null, history == true, ct);
                var views = new List<AttachmentView>();
                foreach (var link in links)
                {
                    var document = await lookup.FindDocumentAsync(link.DocumentNumber, ct);
                    if (document is not null && DocumentService.CanSee(principal, document))
                    {
                        views.Add(View(link, document));
                    }
                }

                return views;
            }));
        eng.MapPost("/attachments/{type}/{id}", (HttpContext http, string type, string id, AttachDocumentRequest b, DocumentService s, CancellationToken ct) =>
            AttachmentCommand(http, b.Reason, type, id, s, c => s.AttachAsync(new AttachDocument(TargetType(type), id, b.DocumentNumber, b.Revision, b.Purpose ?? DocumentPurpose.Information, b.Note), c, ct), ct));
        eng.MapPost("/attachments/{type}/{id}/file", (HttpContext http, string type, string id, AttachFileRequest b, DocumentService s, CancellationToken ct) =>
            AttachmentCommand(http, b.Reason, type, id, s, c => s.AttachFileAsync(new AttachFile(TargetType(type), id, b.FileSha256, b.FileName, b.Title, b.Type ?? DocumentType.Other, b.Markings ?? DataMarking.None, b.Note), c, ct), ct));
        // V2.3: what item revisions are built to, applicability rules and the enforcement policy.
        eng.MapGet("/items/{part}/revisions/{rev}/documents", (HttpContext http, string part, string rev, bool? history, CancellationToken _) =>
            Read(http, Permissions.EngineeringRead, async principal =>
            {
                await using var uow = await Snapshot(http);
                var ct = http.RequestAborted;
                var lookup = uow.Store<IDocumentLookup>();
                var visible = new List<DocumentRequirementInfo>();
                foreach (var r in await lookup.ListRequirementsAsync(part.Trim().ToUpperInvariant(), rev.Trim().ToUpperInvariant(), history == true, ct))
                {
                    if (await lookup.FindDocumentAsync(r.DocumentNumber, ct) is { } d && DocumentService.CanSee(principal, d))
                    {
                        visible.Add(r);
                    }
                }

                return visible;
            }));
        eng.MapPost("/items/{part}/revisions/{rev}/documents", (HttpContext http, string part, string rev, DocumentRequirementRequest b, DocumentService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.AddRequirementAsync(new AddDocumentRequirement(part, rev, b.DocumentNumber, b.DocumentRevision, b.OperationSequence, b.Note), c, ct)));
        eng.MapPost("/document-requirements/{id:long}/remove", (HttpContext http, long id, ReasonRequest b, DocumentService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RemoveRequirementAsync(new RemoveDocumentRequirement(id), c, ct)));
        eng.MapGet("/documents/{number}/rules", (HttpContext http, string number, bool? history, CancellationToken _) =>
            Read(http, Permissions.EngineeringRead, async principal =>
            {
                await using var uow = await Snapshot(http);
                var ct = http.RequestAborted;
                var document = await VisibleDocumentAsync(uow, principal, number, ct);
                return (await uow.Store<IDocumentLookup>().ListRulesAsync(document.Number, history == true, ct))
                    .Where(r => r.ProgramId is not { } p || principal.Programs.Contains(new ProgramId(p))).ToList();
            }));
        eng.MapPost("/documents/{number}/rules", (HttpContext http, string number, RuleRequest b, DocumentService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.AddRuleAsync(new AddApplicabilityRule(number, b.Revision, b.Condition, b.ProgramId, b.Customer, b.DueFrom, b.DueTo, b.OrderNumber, b.Reference, b.Note), c, ct)));
        eng.MapPost("/applicability-rules/{id:long}/withdraw", (HttpContext http, long id, ReasonRequest b, DocumentService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.WithdrawRuleAsync(new WithdrawApplicabilityRule(id), c, ct)));
        eng.MapPost("/orders/{orderNumber:long}/reissue-documentation", (HttpContext http, long orderNumber, ReasonRequest b, DocumentService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ReissueAsync(new ReissueOrderDocumentation(orderNumber), c, ct)));
        eng.MapGet("/documentation-policy", (HttpContext http, CancellationToken _) =>
            ReadStore<IDocumentLookup>(http, Permissions.EngineeringRead, async (l, ct) => await l.ListPoliciesAsync(ct)));
        eng.MapPost("/documentation-policy/{itemClass}", (HttpContext http, ItemClass itemClass, EnforcementRequest b, DocumentService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetEnforcementAsync(new SetDocumentationEnforcement(itemClass, b.Enforced), c, ct)));

        app.MapGet("/api/planning/applicability", async (HttpContext http, long? order, long? po, DocumentationService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null)
            {
                return NotRegistered();
            }

            return order is { } o ? Results.Ok(await s.OrderAsync(principal, o, ct))
                : po is { } p ? Results.Ok(await s.PurchaseOrderAsync(principal, p, ct))
                : Results.Problem(title: "Order or purchase order required", detail: "Give ?order= or ?po=.", statusCode: StatusCodes.Status400BadRequest);
        }).RequireAuthorization();

        eng.MapPost("/attachment-links/{linkId:long}/detach", (HttpContext http, long linkId, ReasonRequest b, DocumentService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.DetachAsync(new DetachDocument(linkId), c, ct)));
    }

    /// <summary>What a link shows and whether it can be relied on.</summary>
    internal static AttachmentView View(DocumentLinkInfo link, DocumentInfo document)
    {
        DocumentRevisionInfo? revision;
        string state;
        if (link.Revision is { } pinned)
        {
            revision = document.Revisions.FirstOrDefault(r => r.Revision == pinned);
            state = revision?.Status switch
            {
                DocumentRevisionStatus.Released or DocumentRevisionStatus.Applicable => "Current",
                DocumentRevisionStatus.Superseded => "Superseded",
                DocumentRevisionStatus.Obsolete => "Obsolete",
                DocumentRevisionStatus.Uncontrolled => "Reference",
                _ => "Not released",
            };
        }
        else if (document.Authority == DocumentAuthority.Reference)
        {
            revision = document.Latest;
            state = revision is null ? "Obsolete" : "Reference";
        }
        else
        {
            revision = document.Current;
            state = revision is null ? "Not released" : "Current";
        }

        return new AttachmentView(
            link.LinkId, document.Number, document.Title, document.Type, document.Authority, document.Markings, link.Purpose, link.Revision, revision, state,
            link.Note, link.LinkedBy, link.LinkedAt, link.DetachedBy, link.DetachedAt, link.DetachReason);
    }

    /// <summary>A link is visible with its record's program and its record's read permission.</summary>
    internal static bool CanSeeLink(ForgePrincipal principal, DocumentLinkInfo link) => DocumentAccess.CanSeeLink(principal, link);

    internal static string ReadPermission(DocumentTargetType type) => DocumentAccess.ReadPermission(type);

    private static async Task<IResult> AttachmentCommand(HttpContext http, string? reason, string type, string id, DocumentService service,
        Func<CommandContext, Task<CommandResult>> execute, CancellationToken ct)
    {
        try { return await Command(http, reason, execute); }
        catch (ForgeAuthorizationException denied) when (SourceClassificationDenied(denied.Decision))
        {
            // The pipeline has already recorded the real source denial before any replay.
            // An ordinary missing attachment permission, including a caller-supplied marking,
            // retains its permission response. Reuse the source owner's visibility check.
            if (denied.Decision.Reasons.Contains($"No assigned role grants '{Permissions.DocumentsAttach}'.", StringComparer.Ordinal)) throw;
            var principal = await PrincipalAsync(http);
            if (principal is null) throw;
            await using var read = await Snapshot(http);
            if (await service.VisibleTargetOrNullAsync(read, principal, TargetType(type), id, ct) is null)
                throw new CommandRejectedException("target_not_found", $"{type} {id} does not exist.");
            throw;
        }
    }

    private static DocumentTargetType TargetType(string raw) =>
        Enum.TryParse<DocumentTargetType>(raw, ignoreCase: true, out var type) && Enum.IsDefined(type) && !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            ? type
            : throw new CommandRejectedException("target_not_found", $"Documents cannot be attached to '{raw}'.");

    private static async Task<DocumentInfo> VisibleDocumentAsync(IKernelUnitOfWork uow, ForgePrincipal principal, string number, CancellationToken ct)
    {
        var document = await uow.Store<IDocumentLookup>().FindDocumentAsync((number ?? string.Empty).Trim().ToUpperInvariant(), ct);
        return document is not null && DocumentService.CanSee(principal, document)
            ? document
            : throw new CommandRejectedException("document_not_found", $"Document {number} does not exist.");
    }

    private static Task<IKernelUnitOfWork> Snapshot(HttpContext http) =>
        http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
}

