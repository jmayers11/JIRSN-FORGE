using Forge.Kernel.Access;
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

public sealed record HoldKindRequest(HoldActivity[] Blocks, string? Reason);

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
        api.MapPost("/{id:long}/release", (HttpContext http, long id, ReasonRequest b, HoldService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ReleaseAsync(new ReleaseHold(id), c, ct)));
        api.MapPost("/kinds/{kind}", (HttpContext http, HoldKind kind, HoldKindRequest b, HoldService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.SetKindPolicyAsync(new SetHoldKindPolicy(kind, b.Blocks), c, ct)));
    }
}
