using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;

namespace Forge.Engineering;

public sealed record RaiseLaborStandardReview(string PartNumber, string Revision, int Sequence, string Reason);

public sealed record DecideLaborStandardReview(long ReviewId, LaborStandardReviewStatus Status, string Resolution);

public sealed record LaborStandardReviewChanged(long ReviewId, string PartNumber, string Revision, int Sequence, string Status, string Message) : IDomainEvent
{
    public static string EventType => nameof(LaborStandardReviewChanged);

    public static int SchemaVersion => 1;
}

/// <summary>
/// Labor standard reviews (Sprint 10 WW-S10-10). A supervisor who runs the reviews, or an engineer, marks a routing operation's
/// standard as suspect; while the review is open no one's efficiency is judged against it. Only an engineer decides it: confirmed
/// (it counts again) or corrected (work done against it stays out; the fix is a new routing revision). The standard itself is never
/// edited here.
/// </summary>
public sealed class LaborStandardReviewService(CommandPipeline pipeline)
{
    public const string SourceContext = "Engineering";
    private const string Entity = "LaborStandardReview";

    public Task<CommandResult> RaiseAsync(RaiseLaborStandardReview c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var revision = BusinessCode.Require(c.Revision, "Revision");
        var key = $"{part}/{revision}/{c.Sequence.ToString(CultureInfo.InvariantCulture)}";
        var spec = new CommandSpec(SourceContext, nameof(RaiseLaborStandardReview), Permissions.WorkforceSupervise, ResourceRef.Of("RoutingOperation", key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var detail = await uow.Store<IEngineeringLookup>().GetRevisionDetailAsync(part, revision, ct);
            if (detail is null || detail.Routing.All(o => o.Sequence != c.Sequence))
                throw new CommandRejectedException("unknown_operation", $"{part} rev {revision} has no routing operation {c.Sequence}.");
            var reason = BusinessCode.RequireName(c.Reason, "Reason");
            if (reason.Length > 800) throw new CommandRejectedException("reason_too_long", "Keep the reason to 800 characters.");
            var store = uow.Store<ILaborStandardReviewStore>();
            if (LaborStandardReviews.Latest(await store.ListReviewsAsync(ct), part, revision, c.Sequence) is { Status: LaborStandardReviewStatus.Open } open)
                throw new CommandRejectedException("review_open", $"Review {open.ReviewId} of this standard is already open.");
            var review = new LaborStandardReview(0, part, revision, c.Sequence, LaborStandardReviewStatus.Open, reason, context.Actor, now);
            var id = await store.InsertReviewAsync(review, ct) ?? throw new CommandRejectedException("review_open", "A review of this standard is already open.");
            var message = $"The labor standard of {part} rev {revision} operation {c.Sequence} is under review; no one's efficiency is judged against it until an engineer decides.";
            var idText = id.ToString(CultureInfo.InvariantCulture);
            return new CommandOutcome(CommandResult.Ok(idText, message),
                [pipeline.Event(new LaborStandardReviewChanged(id, part, revision, c.Sequence, nameof(LaborStandardReviewStatus.Open), message), context, SourceContext, Entity, idText)],
                new AuditChange(Entity, idText, null, CanonicalJson.Serialize(review with { ReviewId = id })));
        }, ct);
    }

    public Task<CommandResult> DecideAsync(DecideLaborStandardReview c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var idText = c.ReviewId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, nameof(DecideLaborStandardReview), Permissions.EngineeringManage, ResourceRef.Of(Entity, idText), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            if (c.Status == LaborStandardReviewStatus.Open || !Enum.IsDefined(c.Status))
                throw new CommandRejectedException("invalid_decision", "Decide a review as confirmed or corrected.");
            var store = uow.Store<ILaborStandardReviewStore>();
            var prior = (await store.ListReviewsAsync(ct)).FirstOrDefault(r => r.ReviewId == c.ReviewId)
                ?? throw new CommandRejectedException("review_not_found", $"Review {idText} does not exist.");
            if (prior.Status != LaborStandardReviewStatus.Open)
                throw new CommandRejectedException("review_decided", $"Review {idText} was already {prior.Status.ToString().ToLowerInvariant()}.");
            var resolution = BusinessCode.RequireName(c.Resolution, "Resolution");
            if (resolution.Length > 800) throw new CommandRejectedException("resolution_too_long", "Keep the resolution to 800 characters.");
            var decided = prior with { Status = c.Status, DecidedBy = context.Actor, DecidedAt = now, Resolution = resolution };
            if (!await store.DecideReviewAsync(decided, ct))
                throw new CommandRejectedException("review_decided", "Someone else decided this review first.");
            var message = c.Status == LaborStandardReviewStatus.Confirmed
                ? $"The labor standard of {prior.PartNumber} rev {prior.Revision} operation {prior.Sequence} was confirmed and counts in efficiency again."
                : $"The labor standard of {prior.PartNumber} rev {prior.Revision} operation {prior.Sequence} was corrected; work done against it stays out of efficiency.";
            return new CommandOutcome(CommandResult.Ok(idText, message),
                [pipeline.Event(new LaborStandardReviewChanged(prior.ReviewId, prior.PartNumber, prior.Revision, prior.Sequence, c.Status.ToString(), message), context, SourceContext, Entity, idText)],
                new AuditChange(Entity, idText, CanonicalJson.Serialize(prior), CanonicalJson.Serialize(decided)));
        }, ct);
    }
}
