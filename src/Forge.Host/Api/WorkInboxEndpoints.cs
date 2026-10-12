using Forge.Host.WorkInbox;
using Forge.Kernel.Identity;
using Forge.Kernel.Commands;
using Forge.Kernel.WorkAssignments;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public static class WorkInboxEndpoints
{
    public static void MapWorkInboxEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/work-inbox").RequireAuthorization();
        api.MapGet("", async (HttpContext http, WorkInboxStore store, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null) return NotRegistered();
            return Results.Ok(new { approvals = await store.ApprovalsAsync(principal, ct), assignments = await store.AssignmentsAsync(principal, ct) });
        });
        api.MapPost("/assignments", (HttpContext http, AssignWorkRequest request, WorkAssignmentService service, CancellationToken ct) =>
            Command(http, request.Reason, context => service.AssignAsync(new AssignWork(request.WorkType, request.WorkId, request.Title, request.DeepLink, new PersonId(request.AssigneePersonId)), context, ct)));
        api.MapPost("/assignments/{id:guid}/status", async (HttpContext http, Guid id, AssignmentStatusRequest request, WorkAssignmentService service, CancellationToken ct) =>
        {
            try { return await Command(http, request.Reason, context => service.SetStatusAsync(new SetWorkAssignmentStatus(id, request.Status), context, ct)); }
            catch (CommandRejectedException rejection) when (rejection.Code == "invalid_assignment_transition")
            { return Results.Conflict(new { error = rejection.Message }); }
        });
    }
}
