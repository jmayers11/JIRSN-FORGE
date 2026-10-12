using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Holds;
using Forge.Kernel.Security;
using Forge.Planning;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record HoldRequest(
    HoldKind Kind,
    HoldTargetType TargetType,
    string TargetId,
    string? TargetPart,
    string? Source,
    string? Evidence,
    string[]? AffectedRecords,
    string? RequiredResolution,
    string? Reason);

public sealed record HoldKindRequest(HoldActivity[] Blocks, string? Reason, bool RequireFreshAssurance = false, bool RequireSignature = false,
    string? SignatureMeaning = null, bool PropagateDownstream = false);
public sealed record HoldReleaseRequest(string? Reason, int? ExpectedVersion = null, bool SignatureConfirmed = false, string? SignatureMeaning = null);

/// <summary>
/// V3.10: generalized holds. The register lists kernel holds with the holds Quality and Inventory keep (undecided NCRs,
/// inspections not passed, stock on quality hold). Placing needs <c>holds.place</c>; releasing needs the kind's
/// release authority; what each kind blocks is configured by a security administrator.
/// </summary>
internal static class HoldEndpoints
{
    public static void MapHoldEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/holds").RequireAuthorization();
        api.MapGet("/", (HttpContext http, bool? includeReleased, CancellationToken _) =>
            Read(http, Permissions.HoldsRead, async principal =>
            {
                await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                return await HoldRegister.ListAsync(uow, principal, includeReleased ?? false, http.RequestAborted);
            }));
        api.MapGet("/kinds", (HttpContext http, CancellationToken _) =>
            ReadStore<IHoldLookup>(http, Permissions.HoldsRead, async (l, ct) => await l.ListKindPoliciesAsync(ct)));
        api.MapPost("/", (HttpContext http, HoldRequest b, HoldService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.PlaceAsync(new PlaceHold(b.Kind, b.TargetType, b.TargetId, b.TargetPart, b.Source, b.Evidence, b.AffectedRecords, b.RequiredResolution), c, ct)));
        api.MapPost("/{id:long}/release", async (HttpContext http, long id, HoldReleaseRequest b, HoldService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null) return NotRegistered();
            var proof = StepUpMiddleware.TryGetEvidence(http, http.RequestServices.GetRequiredService<IConfiguration>(), http.RequestServices.GetRequiredService<TimeProvider>(), principal.PersonId.Value);
            try
            {
                return await Command(http, b.Reason, c => s.ReleaseAsync(new ReleaseHold(id, b.ExpectedVersion, b.SignatureConfirmed, b.SignatureMeaning), c, ct, proof));
            }
            catch (CommandRejectedException rejection) when (rejection.Code == "step_up_required")
            {
                // The normal command pipeline first records the rejected attempt, then this boundary sends the provider challenge.
                return StepUpMiddleware.Challenge(http);
            }
        });
        api.MapPost("/kinds/{kind}", (HttpContext http, HoldKind kind, HoldKindRequest b, HoldService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetKindPolicyAsync(new SetHoldKindPolicy(kind, b.Blocks, b.RequireFreshAssurance, b.RequireSignature, b.SignatureMeaning, b.PropagateDownstream), c, ct)));
    }
}
