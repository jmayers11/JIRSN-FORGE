using Forge.Engineering;
using Forge.Engineering.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Planning;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record ChangeRequest(string Title, ChangeClass Class, ChangeType Type, ChangePriority? Priority, string ChangeReason, string? Description, Guid? ProgramId, string? Customer, DateOnly? RequestedEffective, string? Reason);

public sealed record ChangeUpdateRequest(string Title, ChangeClass Class, ChangeType Type, ChangePriority? Priority, string ChangeReason, string? Description, string? Customer, DateOnly? RequestedEffective, string? Reason);

public sealed record ChangeLineRequest(ChangeLineKind Kind, string Subject, string ToRevision, string? Note, string? Reason);

public sealed record ChangeReviewRequest(ReviewKind Kind, ReviewDecision Decision, string? Notes, string? Reason);

public sealed record ChangeReleaseRequest(DateOnly? EffectiveFrom, string? Reason);

public sealed record ReviewPolicyRequest(string Name, ChangeClass? ChangeClass, ChangeType? ChangeType, ItemClass? ItemClass, Guid? ProgramId, string? Customer, ReviewKind[]? RequiredReviews, bool CombineApprovalRelease, string? Reason);

/// <summary>A change and the reviews it needs now (for a draft: what submitting would require).</summary>
public sealed record ChangeDetailView(ChangeInfo Change, RequiredReviews Required, IReadOnlyDictionary<ReviewKind, string[]> Reviewers);

/// <summary>V2.4 endpoints: engineering changes, reviews, approval, release, impact and review policies.</summary>
public static class ChangeEndpoints
{
    public static void MapChangeEndpoints(this IEndpointRouteBuilder app)
    {
        var eng = app.MapGroup("/api/engineering").RequireAuthorization();

        eng.MapGet("/changes", (HttpContext http, CancellationToken _) =>
            Read(http, Permissions.EngineeringRead, async principal =>
            {
                await using var uow = await Snapshot(http);
                return (await uow.Store<IChangeLookup>().ListChangesAsync(http.RequestAborted))
                    .Where(c => c.ProgramId is not { } p || principal.Programs.Contains(new ProgramId(p))).ToList();
            }));
        eng.MapGet("/changes/{number}", (HttpContext http, string number, CancellationToken _) =>
            Read(http, Permissions.EngineeringRead, async principal =>
            {
                await using var uow = await Snapshot(http);
                var ct = http.RequestAborted;
                var change = await uow.Store<IChangeLookup>().FindChangeAsync(number.Trim().ToUpperInvariant(), ct);
                if (change is null || (change.ProgramId is { } p && !principal.Programs.Contains(new ProgramId(p))))
                {
                    throw new CommandRejectedException("change_not_found", $"Engineering change {number} does not exist.");
                }

                return new ChangeDetailView(change, await ChangeService.RequiredAsync(uow, change, ct), ChangeService.Reviewers);
            }));
        eng.MapGet("/changes/{number}/impact", async (HttpContext http, string number, ChangeImpactService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await s.ImpactAsync(principal, number, ct));
        });

        eng.MapPost("/changes", (HttpContext http, ChangeRequest b, ChangeService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CreateAsync(new CreateChange(b.Title, b.Class, b.Type, b.Priority ?? ChangePriority.Routine, b.ChangeReason, b.Description, b.ProgramId, b.Customer, b.RequestedEffective), c, ct)));
        eng.MapPost("/changes/{number}", (HttpContext http, string number, ChangeUpdateRequest b, ChangeService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.UpdateAsync(new UpdateChange(number, b.Title, b.Class, b.Type, b.Priority ?? ChangePriority.Routine, b.ChangeReason, b.Description, b.Customer, b.RequestedEffective), c, ct)));
        eng.MapPost("/changes/{number}/lines", (HttpContext http, string number, ChangeLineRequest b, ChangeService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.AddLineAsync(new AddChangeLine(number, b.Kind, b.Subject, b.ToRevision, b.Note), c, ct)));
        eng.MapPost("/changes/{number}/lines/{line:int}/remove", (HttpContext http, string number, int line, ReasonRequest b, ChangeService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RemoveLineAsync(new RemoveChangeLine(number, line), c, ct)));
        eng.MapPost("/changes/{number}/submit", (HttpContext http, string number, ReasonRequest b, ChangeService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SubmitAsync(new SubmitChange(number), c, ct)));
        eng.MapPost("/changes/{number}/reviews", (HttpContext http, string number, ChangeReviewRequest b, ChangeService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RecordReviewAsync(new RecordChangeReview(number, b.Kind, b.Decision, b.Notes), c, ct)));
        eng.MapPost("/changes/{number}/approve", (HttpContext http, string number, ReasonRequest b, ChangeService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ApproveAsync(new ApproveChange(number), c, ct)));
        eng.MapPost("/changes/{number}/reject", (HttpContext http, string number, ReasonRequest b, ChangeService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.RejectAsync(new RejectChange(number), c, ct)));
        eng.MapPost("/changes/{number}/cancel", (HttpContext http, string number, ReasonRequest b, ChangeService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CancelAsync(new CancelChange(number), c, ct)));
        eng.MapPost("/changes/{number}/release", (HttpContext http, string number, ChangeReleaseRequest b, ChangeService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ReleaseAsync(new ReleaseChange(number, b.EffectiveFrom), c, ct)));

        eng.MapGet("/review-policies", (HttpContext http, bool? history, CancellationToken _) =>
            ReadStore<IChangeLookup>(http, Permissions.EngineeringRead, async (l, ct) => await l.ListReviewPoliciesAsync(history == true, ct)));
        eng.MapPost("/review-policies", (HttpContext http, ReviewPolicyRequest b, ChangeService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CreatePolicyAsync(new CreateReviewPolicy(b.Name, b.ChangeClass, b.ChangeType, b.ItemClass, b.ProgramId, b.Customer, b.RequiredReviews ?? [], b.CombineApprovalRelease), c, ct)));
        eng.MapPost("/review-policies/{id:long}/withdraw", (HttpContext http, long id, ReasonRequest b, ChangeService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.WithdrawPolicyAsync(new WithdrawReviewPolicy(id), c, ct)));
    }

    private static Task<IKernelUnitOfWork> Snapshot(HttpContext http) =>
        http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
}
