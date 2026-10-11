using Forge.Execution;
using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record HoldRequest(HoldScope Scope, long? OrderNumber, int? Sequence, string? PartNumber, string? WorkCenterCode, string? MachineCode, string? Reason);

/// <summary>V3.9: production holds — place one with a reason, list them, release one (a different person, with a reason).</summary>
internal static class HoldEndpoints
{
    public static void MapHoldEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/holds").RequireAuthorization();
        api.MapGet("/", (HttpContext http, bool? includeReleased, CancellationToken _) =>
            Read(http, Permissions.OrdersRead, async principal =>
            {
                await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                var holds = await uow.Store<IProductionHoldLookup>().ListProductionHoldsAsync(includeReleased ?? false, http.RequestAborted);
                var orders = uow.Store<IExecutionLookup>();
                var visible = new List<ProductionHold>();
                foreach (var hold in holds)
                {
                    // A hold on an order of a program the caller cannot see is left out, like the order.
                    if (hold.OrderNumber is { } n && (await orders.FindOrderAsync(n, http.RequestAborted))?.ProgramId is { } p && !principal.Programs.Contains(new ProgramId(p)))
                    {
                        continue;
                    }

                    visible.Add(hold);
                }

                return visible;
            }));
        api.MapPost("/", (HttpContext http, HoldRequest b, HoldService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.PlaceAsync(new PlaceHold(b.Scope, b.OrderNumber, b.Sequence, b.PartNumber, b.WorkCenterCode, b.MachineCode), c, ct)));
        api.MapPost("/{id:long}/release", (HttpContext http, long id, ReasonRequest b, HoldService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ReleaseAsync(new ReleaseHold(id), c, ct)));
    }
}
