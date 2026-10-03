using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;

namespace Forge.Engineering;

public sealed record CreateChange(string Title, ChangeClass Class, ChangeType Type, ChangePriority Priority, string Reason, string? Description, Guid? ProgramId, string? Customer, DateOnly? RequestedEffective);

public sealed record UpdateChange(string Number, string Title, ChangeClass Class, ChangeType Type, ChangePriority Priority, string Reason, string? Description, string? Customer, DateOnly? RequestedEffective);

/// <summary>Adds an affected revision: <paramref name="Subject"/> is the document number or part number; the change moves it to <paramref name="ToRevision"/>.</summary>
public sealed record AddChangeLine(string Number, ChangeLineKind Kind, string Subject, string ToRevision, string? Note);

public sealed record RemoveChangeLine(string Number, int LineNumber);

public sealed record SubmitChange(string Number);

public sealed record RecordChangeReview(string Number, ReviewKind Kind, ReviewDecision Decision, string? Notes);

public sealed record ApproveChange(string Number);

public sealed record RejectChange(string Number);

public sealed record CancelChange(string Number);

public sealed record ReleaseChange(string Number, DateOnly? EffectiveFrom);

public sealed record CreateReviewPolicy(string Name, ChangeClass? ChangeClass, ChangeType? ChangeType, ItemClass? ItemClass, Guid? ProgramId, string? Customer, IReadOnlyList<ReviewKind> RequiredReviews, bool CombineApprovalRelease);

public sealed record WithdrawReviewPolicy(long PolicyId);

public sealed record EngineeringChangeChanged(string Number, string Change, string Status) : IDomainEvent
{
    public static string EventType => nameof(EngineeringChangeChanged);

    public static int SchemaVersion => 1;
}

public sealed record ChangeReviewRecorded(string Number, int Round, string Kind, string Decision) : IDomainEvent
{
    public static string EventType => nameof(ChangeReviewRecorded);

    public static int SchemaVersion => 1;
}

public sealed record ReviewPolicyChanged(long PolicyId, string Change) : IDomainEvent
{
    public static string EventType => nameof(ReviewPolicyChanged);

    public static int SchemaVersion => 1;
}

public interface IChangeStore
{
    Task<string> NextNumberAsync(CancellationToken ct);

    Task InsertChangeAsync(ChangeInfo change, CancellationToken ct);

    Task UpdateChangeAsync(ChangeInfo change, CancellationToken ct);

    Task InsertLineAsync(ChangeLineInfo line, CancellationToken ct);

    Task RemoveLineAsync(string number, int line, DateTimeOffset at, string actor, CancellationToken ct);

    Task InsertReviewAsync(ChangeReviewInfo review, CancellationToken ct);

    Task DecideReviewAsync(ChangeReviewInfo review, CancellationToken ct);

    Task<long> InsertPolicyAsync(ReviewPolicyInfo policy, CancellationToken ct);

    Task WithdrawPolicyAsync(long policyId, DateTimeOffset at, string actor, string reason, CancellationToken ct);
}

/// <summary>
/// Engineering change control (V2.4). A change lists the document and item revisions it moves; it is
/// submitted for the reviews its policy requires, decided by people other than the originator; a change
/// board approves it; release — by someone other than the originator and, unless a policy combines them,
/// other than the approver — releases every listed revision in one transaction. Revisions listed on an
/// open change can only be released by it.
/// </summary>
public sealed class ChangeService(CommandPipeline pipeline)
{
    public const string SourceContext = EngineeringService.SourceContext;
    private const string ChangeEntity = "EngineeringChange";
    private const string PolicyEntity = "ReviewPolicy";

    /// <summary>Roles whose holders may decide each kind of review.</summary>
    public static readonly IReadOnlyDictionary<ReviewKind, string[]> Reviewers = new Dictionary<ReviewKind, string[]>
    {
        [ReviewKind.Technical] = [Roles.ManufacturingEngineer, Roles.EngineeringApprover, Roles.DocumentController],
        [ReviewKind.Quality] = [Roles.QualityEngineer, Roles.QualityManager],
        [ReviewKind.Manufacturing] = [Roles.ManufacturingEngineer, Roles.Planner, Roles.Supervisor],
        [ReviewKind.Procurement] = [Roles.Buyer, Roles.PurchasingManager],
        [ReviewKind.Program] = [Roles.ProgramManager],
        [ReviewKind.Maintenance] = [Roles.MaintenancePlanner],
    };

    /// <summary>The open change that lists this revision, if any.</summary>
    public static async Task<string?> OpenChangeForAsync(IKernelUnitOfWork uow, ChangeLineKind kind, string subject, string revision, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        return (await uow.Store<IChangeLookup>().ListChangesAsync(ct))
            .FirstOrDefault(c => c.IsOpen && c.ActiveLines.Any(l => l.Kind == kind && l.Subject == subject && l.ToRevision == revision))?.Number;
    }

    public Task<CommandResult> CreateAsync(CreateChange c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var program = c.ProgramId is { } p ? new ProgramId(p) : (ProgramId?)null;
        var spec = new CommandSpec(SourceContext, nameof(CreateChange), Permissions.ChangesManage, new ResourceRef(ChangeEntity, null, program));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var store = uow.Store<IChangeStore>();
            var number = await store.NextNumberAsync(ct);
            var change = new ChangeInfo(number, Text(c.Title, 200, "title"), c.Class, c.Type, c.Priority, Text(c.Reason, 2000, "reason for the change"), Optional(c.Description, 4000),
                c.ProgramId, Optional(c.Customer, 200), c.RequestedEffective, ChangeStatus.Draft, 0, context.Actor, now, null, null, null, null, null, null, null, null, null, [], []);
            await store.InsertChangeAsync(change, ct);
            return Outcome(context, change, null, "Created", $"{number} created as a draft. List the revisions it changes, then submit it for review.");
        }, ct);
    }

    public Task<CommandResult> UpdateAsync(UpdateChange c, CommandContext context, CancellationToken ct) =>
        ChangeAsync(nameof(UpdateChange), c.Number, c, context, Permissions.ChangesManage, false, (uow, change, now) =>
        {
            RequireStatus(change, ChangeStatus.Draft, "edited");
            var updated = change with
            {
                Title = Text(c.Title, 200, "title"), Class = c.Class, Type = c.Type, Priority = c.Priority, Reason = Text(c.Reason, 2000, "reason for the change"),
                Description = Optional(c.Description, 4000), Customer = Optional(c.Customer, 200), RequestedEffective = c.RequestedEffective,
            };
            return Task.FromResult<(ChangeInfo, string, string)?>(updated == change ? null : (updated, "Edited", $"{change.Number} updated."));
        }, ct);

    public Task<CommandResult> AddLineAsync(AddChangeLine c, CommandContext context, CancellationToken ct) =>
        ChangeAsync(nameof(AddChangeLine), c.Number, c, context, Permissions.ChangesManage, false, async (uow, change, now) =>
        {
            RequireStatus(change, ChangeStatus.Draft, "changed");
            var to = (c.ToRevision ?? string.Empty).Trim().ToUpperInvariant();
            if (to.Length == 0)
            {
                throw new CommandRejectedException("revision_required", "Name the revision the change moves to.");
            }

            string subject;
            string? from;
            if (c.Kind == ChangeLineKind.DocumentRevision)
            {
                subject = (c.Subject ?? string.Empty).Trim().ToUpperInvariant();
                var document = await uow.Store<IDocumentLookup>().FindDocumentAsync(subject, ct);
                if (document is null || !DocumentAccess.CanSee(context.Principal, document))
                {
                    throw new CommandRejectedException("document_not_found", $"Document {subject} does not exist.");
                }

                if (document.Authority == DocumentAuthority.Reference)
                {
                    throw new CommandRejectedException("reference_not_controlled", $"{subject} is a reference document; it is never changed under change control.");
                }

                var target = document.Revisions.FirstOrDefault(r => r.Revision == to)
                    ?? throw new CommandRejectedException("revision_not_found", $"{subject} has no revision {to}. Add it as a draft first.");
                if (target.Status is not (DocumentRevisionStatus.Draft or DocumentRevisionStatus.InReview or DocumentRevisionStatus.Approved))
                {
                    throw new CommandRejectedException("revision_released", $"{subject} rev {to} is {target.Status}; a change lists a revision that is not yet released.");
                }

                from = document.Current?.Revision;
            }
            else
            {
                subject = BusinessCode.Require(c.Subject, "Part");
                var revisions = await uow.Store<IEngineeringLookup>().ListRevisionsAsync(subject, ct);
                var target = revisions.FirstOrDefault(r => r.Revision == to)
                    ?? throw new CommandRejectedException("revision_not_found", $"{subject} has no revision {to}. Create it as a draft first.");
                if (target.Status != RevisionStatus.Draft)
                {
                    throw new CommandRejectedException("revision_released", $"{subject} rev {to} is already released.");
                }

                from = revisions.FirstOrDefault(r => r.IsEffectiveAt(now))?.Revision;
            }

            if (change.ActiveLines.Any(l => l.Kind == c.Kind && l.Subject == subject))
            {
                throw new CommandRejectedException("line_exists", $"{change.Number} already changes {subject}.");
            }

            if (await OpenChangeForAsync(uow, c.Kind, subject, to, ct) is { } other)
            {
                throw new CommandRejectedException("on_other_change", $"{subject} rev {to} is already listed on {other}.");
            }

            var line = new ChangeLineInfo(change.Number, change.Lines.Count == 0 ? 1 : change.Lines.Max(l => l.LineNumber) + 1, c.Kind, c.Kind == ChangeLineKind.DocumentRevision ? subject : null,
                c.Kind == ChangeLineKind.ItemRevision ? subject : null, from, to, Optional(c.Note, 500), context.Actor, now, null, null);
            await uow.Store<IChangeStore>().InsertLineAsync(line, ct);
            return ((ChangeInfo, string, string)?)(change with { Lines = [.. change.Lines, line] }, "LineAdded", $"{change.Number} now changes {subject} {(from is null ? "(new)" : "rev " + from)} → rev {to}.");
        }, ct);

    public Task<CommandResult> RemoveLineAsync(RemoveChangeLine c, CommandContext context, CancellationToken ct) =>
        ChangeAsync(nameof(RemoveChangeLine), c.Number, c, context, Permissions.ChangesManage, true, async (uow, change, now) =>
        {
            RequireStatus(change, ChangeStatus.Draft, "changed");
            var line = change.ActiveLines.FirstOrDefault(l => l.LineNumber == c.LineNumber)
                ?? throw new CommandRejectedException("line_not_found", $"{change.Number} has no line {c.LineNumber}.");
            await uow.Store<IChangeStore>().RemoveLineAsync(change.Number, line.LineNumber, now, context.Actor, ct);
            return ((ChangeInfo, string, string)?)(change, "LineRemoved", $"{change.Number} no longer changes {line.Subject}. The line stays in its history.");
        }, ct);

    public Task<CommandResult> SubmitAsync(SubmitChange c, CommandContext context, CancellationToken ct) =>
        ChangeAsync(nameof(SubmitChange), c.Number, c, context, Permissions.ChangesManage, false, async (uow, change, now) =>
        {
            RequireStatus(change, ChangeStatus.Draft, "submitted");
            if (!change.ActiveLines.Any())
            {
                throw new CommandRejectedException("no_lines", "List at least one document or item revision the change moves.");
            }

            var required = await RequiredAsync(uow, change, ct);
            var round = change.ReviewRound + 1;
            var store = uow.Store<IChangeStore>();
            foreach (var kind in required.Kinds)
            {
                await store.InsertReviewAsync(new ChangeReviewInfo(change.Number, round, kind, string.Join("; ", required.Basis), null, null, null, null), ct);
            }

            var updated = change with { Status = ChangeStatus.InReview, ReviewRound = round, SubmittedAt = now };
            return ((ChangeInfo, string, string)?)(updated, "Submitted", $"{change.Number} submitted for review: {string.Join(", ", required.Kinds)} ({string.Join("; ", required.Basis)}).");
        }, ct);

    public Task<CommandResult> RecordReviewAsync(RecordChangeReview c, CommandContext context, CancellationToken ct) =>
        ChangeAsync(nameof(RecordChangeReview), c.Number, c, context, Permissions.ChangesReview, false, async (uow, change, now) =>
        {
            RequireStatus(change, ChangeStatus.InReview, "reviewed");
            var review = change.CurrentReviews.FirstOrDefault(r => r.Kind == c.Kind)
                ?? throw new CommandRejectedException("review_not_required", $"{change.Number} does not need a {c.Kind} review.");
            if (review.Decision is not null)
            {
                throw new CommandRejectedException("review_decided", $"The {c.Kind} review of {change.Number} was already decided by {review.Reviewer}.");
            }

            if (change.Originator == context.Actor)
            {
                throw new CommandRejectedException("separation_of_duties", "You originated this change; reviews are made by other people.");
            }

            if (!Reviewers[c.Kind].Any(context.Principal.Roles.Contains))
            {
                throw new CommandRejectedException("not_a_reviewer", $"A {c.Kind} review is made by a {string.Join(" or ", Reviewers[c.Kind])}.");
            }

            var notes = Optional(c.Notes, 2000);
            if (c.Decision == ReviewDecision.Reject && notes is null)
            {
                throw new CommandRejectedException("notes_required", "Say what must change when you reject.");
            }

            var decided = review with { Reviewer = context.Actor, Decision = c.Decision, Notes = notes, DecidedAt = now };
            await uow.Store<IChangeStore>().DecideReviewAsync(decided, ct);
            await uow.Outbox.EnqueueAsync(pipeline.Event(new ChangeReviewRecorded(change.Number, review.Round, c.Kind.ToString(), c.Decision.ToString()), context, SourceContext, ChangeEntity, change.Number, Program(change)), ct);
            if (c.Decision == ReviewDecision.Reject)
            {
                return ((ChangeInfo, string, string)?)(change with { Status = ChangeStatus.Draft }, "Returned", $"{c.Kind} review rejected {change.Number}; it is back with the originator as a draft.");
            }

            var pending = change.CurrentReviews.Count(r => r.Decision is null && r.Kind != c.Kind);
            return ((ChangeInfo, string, string)?)(change, "Reviewed", pending == 0
                ? $"{c.Kind} review recorded. All reviews are in; {change.Number} is ready for change board approval."
                : $"{c.Kind} review recorded; {pending} review(s) still open.");
        }, ct);

    public Task<CommandResult> ApproveAsync(ApproveChange c, CommandContext context, CancellationToken ct) =>
        ChangeAsync(nameof(ApproveChange), c.Number, c, context, Permissions.ChangesApprove, false, async (uow, change, now) =>
        {
            RequireStatus(change, ChangeStatus.InReview, "approved");
            var open = change.CurrentReviews.Where(r => r.Decision is null).Select(r => r.Kind.ToString()).ToList();
            if (open.Count > 0)
            {
                throw new CommandRejectedException("reviews_open", $"Reviews still open: {string.Join(", ", open)}.");
            }

            if (change.Originator == context.Actor)
            {
                throw new CommandRejectedException("separation_of_duties", "You originated this change; the change board approval is someone else's.");
            }

            if ((await AuthorsAsync(uow, change, ct)).Contains(context.Actor))
            {
                throw new CommandRejectedException("separation_of_duties", "You wrote one of the revisions this change releases; someone else approves it.");
            }

            return ((ChangeInfo, string, string)?)(change with { Status = ChangeStatus.Approved, ApprovedBy = context.Actor, ApprovedAt = now }, "Approved", $"{change.Number} approved by the change board. Release it to put its revisions in force.");
        }, ct);

    public Task<CommandResult> RejectAsync(RejectChange c, CommandContext context, CancellationToken ct) =>
        ChangeAsync(nameof(RejectChange), c.Number, c, context, Permissions.ChangesApprove, true, (uow, change, now) =>
        {
            if (change.Status is not (ChangeStatus.InReview or ChangeStatus.Approved))
            {
                throw new CommandRejectedException("invalid_status", $"{change.Number} is {change.Status}; only a change in review or approved is rejected.");
            }

            return Task.FromResult<(ChangeInfo, string, string)?>((change with { Status = ChangeStatus.Rejected, ClosedBy = context.Actor, ClosedAt = now, ClosedReason = context.Reason }, "Rejected", $"{change.Number} rejected. Its draft revisions stay drafts."));
        }, ct);

    public Task<CommandResult> CancelAsync(CancelChange c, CommandContext context, CancellationToken ct) =>
        ChangeAsync(nameof(CancelChange), c.Number, c, context, Permissions.ChangesManage, true, (uow, change, now) =>
            !change.IsOpen
                ? throw new CommandRejectedException("invalid_status", $"{change.Number} is {change.Status}.")
                : Task.FromResult<(ChangeInfo, string, string)?>((change with { Status = ChangeStatus.Cancelled, ClosedBy = context.Actor, ClosedAt = now, ClosedReason = context.Reason }, "Cancelled", $"{change.Number} cancelled.")),
            ct);

    public Task<CommandResult> ReleaseAsync(ReleaseChange c, CommandContext context, CancellationToken ct) =>
        ChangeAsync(nameof(ReleaseChange), c.Number, c, context, Permissions.EngineeringRelease, true, async (uow, change, now) =>
        {
            RequireStatus(change, ChangeStatus.Approved, "released");
            if (change.Originator == context.Actor)
            {
                throw new CommandRejectedException("separation_of_duties", "You originated this change; someone else releases it.");
            }

            var required = await RequiredAsync(uow, change, ct);
            if (change.ApprovedBy == context.Actor && !required.CombineApprovalRelease)
            {
                throw new CommandRejectedException("separation_of_duties", "You approved this change; a different person releases it (no review policy combines approval and release for it).");
            }

            var today = DateOnly.FromDateTime(now.UtcDateTime);
            var from = c.EffectiveFrom ?? change.RequestedEffective ?? today;
            if (from < today)
            {
                throw new CommandRejectedException("effective_in_past", "A change cannot take effect before today.");
            }

            var itemFrom = from == today ? now : new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var released = new List<string>();
            foreach (var line in change.ActiveLines.OrderBy(l => l.LineNumber))
            {
                if (line.Kind == ChangeLineKind.ItemRevision)
                {
                    var (effective, superseded, _) = await EngineeringService.ReleaseRevisionCoreAsync(uow, line.PartNumber!, line.ToRevision, itemFrom, now, context.Actor, ct);
                    await uow.Outbox.EnqueueAsync(pipeline.Event(new RevisionReleased(line.PartNumber!, line.ToRevision, effective, superseded?.Revision), context, SourceContext, "ItemRevision", $"{line.PartNumber}:{line.ToRevision}"), ct);
                    released.Add($"{line.PartNumber} rev {line.ToRevision}");
                }
                else
                {
                    released.Add(await ReleaseDocumentAsync(uow, change, line, from, context, now, ct));
                }
            }

            var updated = change with { Status = ChangeStatus.Released, ReleasedBy = context.Actor, ReleasedAt = now, EffectiveFrom = from };
            return ((ChangeInfo, string, string)?)(updated, "Released", $"{change.Number} released, effective {from:yyyy-MM-dd}: {string.Join(", ", released)}.");
        }, ct);

    public Task<CommandResult> CreatePolicyAsync(CreateReviewPolicy c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var program = c.ProgramId is { } p ? new ProgramId(p) : (ProgramId?)null;
        var spec = new CommandSpec(SourceContext, nameof(CreateReviewPolicy), Permissions.ChangesApprove, new ResourceRef(PolicyEntity, null, program), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var kinds = (c.RequiredReviews ?? []).Distinct().ToList();
            if (kinds.Count == 0)
            {
                throw new CommandRejectedException("reviews_required", "A policy requires at least one review.");
            }

            var policy = new ReviewPolicyInfo(0, Text(c.Name, 100, "policy name"), c.ChangeClass, c.ChangeType, c.ItemClass, c.ProgramId, Optional(c.Customer, 200), kinds, c.CombineApprovalRelease, context.Actor, now, null, null, null);
            var id = await uow.Store<IChangeStore>().InsertPolicyAsync(policy, ct);
            var key = id.ToString(CultureInfo.InvariantCulture);
            return new CommandOutcome(
                CommandResult.Ok(key, $"Review policy {id} ({policy.Name}) requires {string.Join(", ", kinds)}{(c.CombineApprovalRelease ? "; the approver may also release" : string.Empty)}."),
                [pipeline.Event(new ReviewPolicyChanged(id, "Created"), context, SourceContext, PolicyEntity, key, program)],
                new AuditChange(PolicyEntity, key, null, CanonicalJson.Serialize(policy with { PolicyId = id }), program));
        }, ct);
    }

    public Task<CommandResult> WithdrawPolicyAsync(WithdrawReviewPolicy c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var key = c.PolicyId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, nameof(WithdrawReviewPolicy), Permissions.ChangesApprove, ResourceRef.Of(PolicyEntity, key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var policy = (await uow.Store<IChangeLookup>().ListReviewPoliciesAsync(true, ct)).FirstOrDefault(p => p.PolicyId == c.PolicyId)
                ?? throw new CommandRejectedException("policy_not_found", $"Review policy {c.PolicyId} does not exist.");
            if (policy.WithdrawnAt is not null)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(key, "Already withdrawn."));
            }

            await uow.Store<IChangeStore>().WithdrawPolicyAsync(policy.PolicyId, now, context.Actor, context.Reason!, ct);
            return new CommandOutcome(
                CommandResult.Ok(key, $"Review policy {policy.PolicyId} withdrawn. Changes submitted from now on no longer need its reviews."),
                [pipeline.Event(new ReviewPolicyChanged(policy.PolicyId, "Withdrawn"), context, SourceContext, PolicyEntity, key)],
                new AuditChange(PolicyEntity, key, CanonicalJson.Serialize(policy), CanonicalJson.Serialize(policy with { WithdrawnBy = context.Actor, WithdrawnAt = now, WithdrawnReason = context.Reason })));
        }, ct);
    }

    /// <summary>The reviews a change needs now, from the policies and the classes of the items it affects.</summary>
    public static async Task<RequiredReviews> RequiredAsync(IKernelUnitOfWork uow, ChangeInfo change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(change);
        var engineering = uow.Store<IEngineeringLookup>();
        var parts = change.ActiveLines.Where(l => l.Kind == ChangeLineKind.ItemRevision).Select(l => l.PartNumber!).ToHashSet(StringComparer.Ordinal);
        var documents = change.ActiveLines.Where(l => l.Kind == ChangeLineKind.DocumentRevision).Select(l => l.DocumentNumber!).ToHashSet(StringComparer.Ordinal);
        if (documents.Count > 0)
        {
            foreach (var r in await uow.Store<IDocumentLookup>().ListRequirementsAsync(null, null, false, ct))
            {
                if (documents.Contains(r.DocumentNumber))
                {
                    parts.Add(r.PartNumber);
                }
            }
        }

        var classes = new HashSet<ItemClass>();
        foreach (var part in parts)
        {
            if (await engineering.FindItemAsync(part, ct) is { } item)
            {
                classes.Add(item.Class);
            }
        }

        return ReviewPolicies.For(await uow.Store<IChangeLookup>().ListReviewPoliciesAsync(false, ct), change, classes);
    }

    private async Task<string> ReleaseDocumentAsync(IKernelUnitOfWork uow, ChangeInfo change, ChangeLineInfo line, DateOnly from, CommandContext context, DateTimeOffset now, CancellationToken ct)
    {
        var document = await uow.Store<IDocumentLookup>().FindDocumentAsync(line.DocumentNumber!, ct)
            ?? throw new CommandRejectedException("document_not_found", $"Document {line.DocumentNumber} does not exist.");
        var revision = document.Revisions.FirstOrDefault(r => r.Revision == line.ToRevision)
            ?? throw new CommandRejectedException("revision_not_found", $"{document.Number} has no revision {line.ToRevision}.");
        if (revision.Status is not (DocumentRevisionStatus.Draft or DocumentRevisionStatus.InReview or DocumentRevisionStatus.Approved))
        {
            throw new CommandRejectedException("revision_released", $"{document.Number} rev {revision.Revision} is already {revision.Status}.");
        }

        if (revision.FileSha256 is null)
        {
            throw new CommandRejectedException("no_file", $"{document.Number} rev {revision.Revision} has no file.");
        }

        if (revision.Author == context.Actor)
        {
            throw new CommandRejectedException("separation_of_duties", $"You wrote {document.Number} rev {revision.Revision}; someone else releases it.");
        }

        var store = uow.Store<IDocumentStore>();
        if (document.Current is { } previous)
        {
            if (from < previous.EffectiveFrom)
            {
                throw new CommandRejectedException("effectivity_overlap", $"{document.Number} rev {previous.Revision} is in force from {previous.EffectiveFrom:yyyy-MM-dd}; the change cannot take effect earlier.");
            }

            await store.UpdateRevisionAsync(previous with { Status = DocumentRevisionStatus.Superseded, EffectiveTo = from }, ct);
            await uow.Outbox.EnqueueAsync(pipeline.Event(new DocumentRevisionChanged(document.Number, previous.Revision, "Superseded", nameof(DocumentRevisionStatus.Superseded)), context, SourceContext, "DocumentRevision", $"{document.Number}/{previous.Revision}", Program(document.ProgramId), document.Markings), ct);
        }

        var external = document.Authority == DocumentAuthority.External;
        var status = external ? DocumentRevisionStatus.Applicable : DocumentRevisionStatus.Released;
        await store.UpdateRevisionAsync(revision with
        {
            Status = status,
            SubmittedBy = revision.SubmittedBy ?? change.Originator,
            SubmittedAt = revision.SubmittedAt ?? change.SubmittedAt,
            ApprovedBy = external ? revision.ApprovedBy : change.ApprovedBy,
            ApprovedAt = external ? revision.ApprovedAt : change.ApprovedAt,
            ReleasedBy = context.Actor,
            ReleasedAt = now,
            EffectiveFrom = from,
        }, ct);
        await uow.Outbox.EnqueueAsync(pipeline.Event(new DocumentRevisionChanged(document.Number, revision.Revision, status.ToString(), status.ToString()), context, SourceContext, "DocumentRevision", $"{document.Number}/{revision.Revision}", Program(document.ProgramId), document.Markings), ct);
        return $"{document.Number} rev {revision.Revision}";
    }

    /// <summary>Everyone who wrote a revision the change releases: document authors and item revision contributors.</summary>
    private static async Task<HashSet<string>> AuthorsAsync(IKernelUnitOfWork uow, ChangeInfo change, CancellationToken ct)
    {
        var authors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in change.ActiveLines)
        {
            if (line.Kind == ChangeLineKind.DocumentRevision)
            {
                var document = await uow.Store<IDocumentLookup>().FindDocumentAsync(line.DocumentNumber!, ct);
                if (document?.Revisions.FirstOrDefault(r => r.Revision == line.ToRevision) is { } revision)
                {
                    authors.Add(revision.Author);
                }
            }
            else
            {
                authors.UnionWith(await uow.Store<IEngineeringStore>().GetContributorsAsync(line.PartNumber!, line.ToRevision, ct));
            }
        }

        return authors;
    }

    private Task<CommandResult> ChangeAsync<TCommand>(
        string commandType,
        string rawNumber,
        TCommand command,
        CommandContext context,
        string permission,
        bool reasonRequired,
        Func<IKernelUnitOfWork, ChangeInfo, DateTimeOffset, Task<(ChangeInfo Updated, string Change, string Message)?>> change,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var number = (rawNumber ?? string.Empty).Trim().ToUpperInvariant();
        var spec = new CommandSpec(SourceContext, commandType, permission, ResourceRef.Of(ChangeEntity, number), reasonRequired);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var current = await uow.Store<IChangeLookup>().FindChangeAsync(number, ct);
            if (current is null || (current.ProgramId is { } p && !context.Principal.Programs.Contains(new ProgramId(p))))
            {
                throw new CommandRejectedException("change_not_found", $"Engineering change {number} does not exist.");
            }

            if (await change(uow, current, now) is not { } r)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(number, "Nothing changed."));
            }

            if (!ReferenceEquals(r.Updated, current))
            {
                await uow.Store<IChangeStore>().UpdateChangeAsync(r.Updated, ct);
            }

            return Outcome(context, r.Updated, current, r.Change, r.Message);
        }, ct);
    }

    private CommandOutcome Outcome(CommandContext context, ChangeInfo change, ChangeInfo? before, string what, string message) => new(
        CommandResult.Ok(change.Number, message),
        [pipeline.Event(new EngineeringChangeChanged(change.Number, what, change.Status.ToString()), context, SourceContext, ChangeEntity, change.Number, Program(change))],
        new AuditChange(ChangeEntity, change.Number, before is null ? null : CanonicalJson.Serialize(Summary(before)), CanonicalJson.Serialize(Summary(change)), Program(change)));

    private static object Summary(ChangeInfo c) => new
    {
        c.Number, c.Title, Class = c.Class.ToString(), Type = c.Type.ToString(), Priority = c.Priority.ToString(), c.Reason, Status = c.Status.ToString(), c.ReviewRound,
        c.ApprovedBy, c.ReleasedBy, c.EffectiveFrom, c.ClosedReason,
        Lines = c.ActiveLines.Select(l => $"{l.Kind}:{l.Subject}:{l.FromRevision}->{l.ToRevision}").ToList(),
    };

    private static void RequireStatus(ChangeInfo change, ChangeStatus status, string action)
    {
        if (change.Status != status)
        {
            throw new CommandRejectedException("invalid_status", $"{change.Number} is {change.Status}; only a {status} change is {action}.");
        }
    }

    private static ProgramId? Program(ChangeInfo change) => Program(change.ProgramId);

    private static ProgramId? Program(Guid? id) => id is { } p ? new ProgramId(p) : null;

    private static string Text(string? raw, int max, string what)
    {
        var text = (raw ?? string.Empty).Trim();
        return text.Length == 0 || text.Length > max
            ? throw new CommandRejectedException("invalid_text", $"A {what} is required (at most {max} characters).")
            : text;
    }

    private static string? Optional(string? value, int max)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > max ? text[..max] : text;
    }
}
