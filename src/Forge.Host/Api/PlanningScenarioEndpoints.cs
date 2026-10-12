using Forge.Planning;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record PlanningScenarioRequest(string Name, ScenarioChange Change, string? Reason);

internal static class PlanningScenarioEndpoints
{
    public static void MapPlanningScenarioEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/planning/scenarios").RequireAuthorization();
        api.MapGet("", async (HttpContext http, PlanningScenarioService service, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await service.ListAsync(principal, ct));
        });
        api.MapPost("", (HttpContext http, PlanningScenarioRequest request, CancellationToken ct) =>
            Command(http, request.Reason, context => http.RequestServices.GetRequiredService<PlanningScenarioService>()
                .CreateAsync(new CreatePlanningScenario(request.Name, request.Change), context, ct)));
        api.MapGet("/{id:guid}", async (HttpContext http, Guid id, PlanningScenarioService service, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await service.FindAsync(id, principal, ct));
        });
        api.MapPost("/{id:guid}/run", async (HttpContext http, Guid id, PlanningScenarioService service, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await service.RunAsync(id, principal, ct));
        });
        api.MapPost("/{id:guid}/delete", (HttpContext http, Guid id, ReasonRequest request, PlanningScenarioService service, CancellationToken ct) =>
            Command(http, request.Reason, context => service.DeleteAsync(new DeletePlanningScenario(id), context, ct)));
    }
}
