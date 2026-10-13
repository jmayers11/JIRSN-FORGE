using Forge.Kernel.Access;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Planning;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record GoalRequest(GoalKind Kind, string Title, long? ParentGoalId, long? OrderNumber, long? DemandId, DateOnly? RequiredBy, int? LeadDays, Guid? ProgramId, string? Reason);

public sealed record GoalChangeRequest(string Title, DateOnly? RequiredBy, int? LeadDays, string? Reason);

/// <summary>V3.7: goals, their breakdown, attainment and reforecast; goal risk per released order.</summary>
internal static class GoalEndpoints
{
    public static void MapGoalEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/planning").RequireAuthorization();
        api.MapGet("/goals", async (HttpContext http, bool? includeClosed, GoalService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await s.ListAsync(principal, includeClosed ?? false, ct));
        });
        api.MapPost("/goals", (HttpContext http, GoalRequest b, GoalService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CreateAsync(new CreateGoal(b.Kind, b.Title, b.ParentGoalId, b.OrderNumber, b.DemandId, b.RequiredBy, b.LeadDays, b.ProgramId), c, ct)));
        api.MapPost("/goals/{id:long}", (HttpContext http, long id, GoalChangeRequest b, GoalService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ChangeAsync(new ChangeGoal(id, b.Title, b.RequiredBy, b.LeadDays), c, ct)));
        api.MapPost("/goals/{id:long}/close", (HttpContext http, long id, ReasonRequest b, GoalService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.CloseAsync(new CloseGoal(id), c, ct)));
        api.MapPost("/goals/reforecast", (HttpContext http, ReasonRequest b, GoalService s, CancellationToken ct) =>
            Command(http, b.Reason, c => s.ReforecastAsync(new ReforecastGoals(), c, ct)));

        // The goal-risk engine's view of every released order: behind plan against the approved schedule, at risk against its due date.
        api.MapGet("/goal-risk", (HttpContext http, CancellationToken _) =>
            Read(http, Permissions.ScheduleRead, async principal =>
            {
                await using var uow = await http.RequestServices.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
                var lines = await GoalService.RiskAsync(uow, http.RequestServices.GetRequiredService<TimeProvider>(), http.RequestAborted, principal);
                return lines.Values.Where(l => l.ProgramId is not { } p || principal.Programs.Contains(new ProgramId(p))).OrderBy(l => l.OrderNumber).ToList();
            }));
    }
}
