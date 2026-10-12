using Forge.Host.WorkInbox;
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
        api.MapPost("/assignments", async (HttpContext http, AssignWorkRequest request, WorkInboxStore store, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null) return NotRegistered();
            try { return Results.Created($"/api/work-inbox/assignments/{await store.AssignAsync(principal, request, ct)}", new { }); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        });
        api.MapPost("/assignments/{id:guid}/status", async (HttpContext http, Guid id, AssignmentStatusRequest request, WorkInboxStore store, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null) return NotRegistered();
            try { await store.UpdateStatusAsync(id, principal, request, ct); return Results.Ok(new { assignmentId = id, status = request.Status }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
    }
}
