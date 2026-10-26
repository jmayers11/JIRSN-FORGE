using Forge.Execution;
using Forge.Execution.Contracts;
using Forge.Kernel.Security;
using Forge.Planning;
using Forge.Quality;
using Forge.Quality.Contracts;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record ReasonCodeRequest(string Code, ReasonKind Kind, StoppageCategory? Category, string Description, string? Reason);

public sealed record ReasonCodeChangeRequest(string Description, bool Active, string? Reason);

public sealed record StoppageRequest(string MachineCode, StoppageCategory Category, string ReasonCode, long? OrderNumber, int? Sequence, string? Note, DateTimeOffset? StartedAt, string? Reason);

public sealed record StoppageEndRequest(DateTimeOffset? EndedAt, string? Reason);

public sealed record RootCauseRequest(string Cause, string Evidence, string? Reason);

public sealed record ReworkRequest(ReworkKind Kind, long OrderNumber, int Sequence, decimal Quantity, string Instructions, long? NcrId, string? ReasonCode, string? Reason);

public sealed record ReworkCloseRequest(decimal GoodQuantity, decimal ScrapQuantity, string Note, string? Reason);

public sealed record ReworkPlanRequest(IReadOnlyList<ReworkPlanOperation> Operations, IReadOnlyList<ReworkPlanMaterial>? Materials, IReadOnlyList<Guid>? Trainees, string Rationale, string? Reason);

public sealed record ReworkPlanDecisionRequest(bool Approve, string Note, string? Reason);

/// <summary>V3.5: reason codes, stoppages and root causes; rework and repair; an order's quantity states.</summary>
internal static class ProductionActualsEndpoints
{
    public static void MapProductionActualsEndpoints(this IEndpointRouteBuilder app)
    {
        var execution = app.MapGroup("/api/execution").RequireAuthorization();
        execution.MapGet("/reason-codes", (HttpContext http, CancellationToken _) =>
            ReadStore<IProductionActualsLookup>(http, Permissions.ShopFloorRead, async (l, ct) => await l.ListReasonCodesAsync(ct)));
        execution.MapPost("/reason-codes", (HttpContext http, ReasonCodeRequest b, ProductionActualsService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.DefineReasonCodeAsync(new DefineReasonCode(b.Code, b.Kind, b.Category, b.Description), c, ct)));
        execution.MapPost("/reason-codes/{code}", (HttpContext http, string code, ReasonCodeChangeRequest b, ProductionActualsService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ChangeReasonCodeAsync(new ChangeReasonCode(code, b.Description, b.Active), c, ct)));

        execution.MapGet("/stoppages", async (HttpContext http, bool? open, string? machine, int? days, ProductionActualsQueries q, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await q.StoppagesAsync(principal, open ?? false, machine, days ?? 7, ct));
        });
        execution.MapPost("/stoppages", (HttpContext http, StoppageRequest b, ProductionActualsService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.StartStoppageAsync(new StartStoppage(b.MachineCode, b.Category, b.ReasonCode, b.OrderNumber, b.Sequence, b.Note, b.StartedAt), c, ct)));
        execution.MapPost("/stoppages/{id:long}/end", (HttpContext http, long id, StoppageEndRequest b, ProductionActualsService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.EndStoppageAsync(new EndStoppage(id, b.EndedAt), c, ct)));
        execution.MapPost("/stoppages/{id:long}/root-causes", (HttpContext http, long id, RootCauseRequest b, ProductionActualsService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ProposeRootCauseAsync(new ProposeRootCause(id, b.Cause, b.Evidence), c, ct)));
        execution.MapPost("/root-causes/{id:long}/confirm", (HttpContext http, long id, ReasonRequest b, ProductionActualsService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ConfirmRootCauseAsync(new ConfirmRootCause(id), c, ct)));

        var quality = app.MapGroup("/api/quality").RequireAuthorization();
        quality.MapGet("/rework", (HttpContext http, long? order, bool? open, CancellationToken _) =>
            Read(http, Permissions.ShopFloorRead, async principal =>
            {
                await using var uow = await http.RequestServices.GetRequiredService<Forge.Kernel.Access.IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                var orders = uow.Store<IExecutionLookup>();
                var list = new List<ReworkInfo>();
                foreach (var r in await uow.Store<IReworkLookup>().ListReworkAsync(order, open ?? false, http.RequestAborted))
                {
                    if (await orders.FindOrderAsync(r.OrderNumber, http.RequestAborted) is { } o && (o.ProgramId is not { } p || principal.Programs.Contains(new Forge.Kernel.Identity.ProgramId(p))))
                    {
                        list.Add(r);
                    }
                }

                return list;
            }));
        quality.MapPost("/rework", (HttpContext http, ReworkRequest b, ReworkService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.OpenAsync(new OpenRework(b.Kind, b.OrderNumber, b.Sequence, b.Quantity, b.Instructions, b.NcrId, b.ReasonCode), c, ct)));
        quality.MapPost("/rework/{id:long}/close", (HttpContext http, long id, ReworkCloseRequest b, ReworkService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CloseAsync(new CloseRework(id, b.GoodQuantity, b.ScrapQuantity, b.Note), c, ct)));

        quality.MapGet("/trends", (HttpContext http, string part, DateOnly? from, DateOnly? to, Forge.Host.Quality.QualityTrendService s, CancellationToken ct) =>
            Read(http, Permissions.QualityRead, async principal => await s.BuildAsync(principal, part, from, to, ct)));
        quality.MapGet("/rework-plans", (HttpContext http, long? order, CancellationToken _) =>
            Read(http, Permissions.ShopFloorRead, async principal =>
            {
                await using var uow = await http.RequestServices.GetRequiredService<Forge.Kernel.Access.IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                var orders = uow.Store<IExecutionLookup>();
                var visible = new Dictionary<long, bool>();
                var list = new List<ReworkPlan>();
                foreach (var plan in await uow.Store<IReworkPlanStore>().ListPlansAsync(order, http.RequestAborted))
                {
                    if (!visible.TryGetValue(plan.OrderNumber, out var ok))
                    {
                        var o = await orders.FindOrderAsync(plan.OrderNumber, http.RequestAborted);
                        visible[plan.OrderNumber] = ok = o is not null && (o.ProgramId is not { } p || principal.Programs.Contains(new Forge.Kernel.Identity.ProgramId(p)));
                    }

                    if (ok) list.Add(plan);
                }

                return list;
            }));
        quality.MapPost("/rework/{id:long}/plans", (HttpContext http, long id, ReworkPlanRequest b, Forge.Host.Quality.ReworkPlanService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ProposeAsync(new Forge.Host.Quality.ProposeReworkPlan(id, b.Operations ?? [], b.Materials, b.Trainees, b.Rationale), c, ct)));
        quality.MapPost("/rework-plans/{planId:guid}/decide", (HttpContext http, Guid planId, ReworkPlanDecisionRequest b, Forge.Host.Quality.ReworkPlanService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.DecideAsync(new Forge.Host.Quality.DecideReworkPlan(planId, b.Approve, b.Note), c, ct)));

        app.MapGet("/api/orders/{order:long}/quantity-states", async (HttpContext http, long order, ProductionActualsQueries q, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await q.OrderAsync(principal, order, ct));
        }).RequireAuthorization();
    }
}
