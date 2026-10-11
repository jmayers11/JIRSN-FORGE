using Forge.Execution;
using Forge.Execution.Contracts;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record ShiftChecklistRequest(string WorkCenterCode, string Code, string Description, bool Required, int SortOrder, bool Active, string? Reason);
public sealed record ShiftHandoffRequest(string WorkCenterCode, string ShiftCode, DateTimeOffset ShiftFrom, DateTimeOffset ShiftUntil, string? Context, IReadOnlyList<ShiftChecklistResponse>? Checklist, string? Reason);
public sealed record ShiftReasonRequest(string? Reason);
public sealed record ShiftAcknowledgeRequest(string? Note, string? Reason);
public sealed record ShiftActionRequest(string Description, string? SourceKind, string? SourceReference, Guid Assignee, string? Reason);
public sealed record ShiftActionStatusRequest(ShiftActionStatus Expected, ShiftActionStatus Status, string? Reason);
public sealed record ShiftCloseoutRequest(string WorkCenterCode, string ShiftCode, DateTimeOffset ShiftFrom, DateTimeOffset ShiftUntil, string? Note, string? Reason);

internal static class ShiftContinuityEndpoints
{
    public static void MapShiftContinuityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/execution/shifts").RequireAuthorization();
        group.MapGet("/checklist", async (HttpContext http, string workCenter, ShiftContinuityService service, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await service.ChecklistAsync(workCenter, principal, ct));
        });
        group.MapPost("/checklist", (HttpContext http, ShiftChecklistRequest b, ShiftContinuityService service, CancellationToken ct) =>
            Command(http, b.Reason, c => service.ConfigureChecklistAsync(new ConfigureShiftChecklistItem(b.WorkCenterCode, b.Code, b.Description, b.Required, b.SortOrder, b.Active), c, ct)));
        group.MapGet("/handoffs", async (HttpContext http, string? workCenter, int? limit, ShiftContinuityService service, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await service.HandoffsAsync(workCenter, limit ?? 30, principal, ct));
        });
        group.MapPost("/handoffs", (HttpContext http, ShiftHandoffRequest b, ShiftContinuityService service, CancellationToken ct) =>
            Command(http, b.Reason, c => service.CreateHandoffAsync(new CreateShiftHandoff(b.WorkCenterCode, b.ShiftCode, b.ShiftFrom, b.ShiftUntil, b.Context, b.Checklist), c, ct)));
        group.MapGet("/handoffs/{id:long}", async (HttpContext http, long id, ShiftContinuityService service, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null) return NotRegistered();
            var handoff = await service.HandoffAsync(id, principal, ct);
            return handoff is null ? Results.NotFound() : Results.Ok(new { handoff, acknowledgments = await service.AcknowledgmentsAsync(id, principal, ct) });
        });
        group.MapPost("/handoffs/{id:long}/review", (HttpContext http, long id, ShiftReasonRequest b, ShiftContinuityService service, CancellationToken ct) =>
            Command(http, b.Reason, c => service.ReviewAsync(new ReviewShiftHandoff(id), c, ct)));
        group.MapPost("/handoffs/{id:long}/acknowledge", (HttpContext http, long id, ShiftAcknowledgeRequest b, ShiftContinuityService service, CancellationToken ct) =>
            Command(http, b.Reason, c => service.AcknowledgeAsync(new AcknowledgeShiftHandoff(id, b.Note), c, ct)));
        group.MapGet("/handoffs/{id:long}/actions", async (HttpContext http, long id, ShiftContinuityService service, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await service.ActionsAsync(id, principal, ct));
        });
        group.MapPost("/handoffs/{id:long}/actions", (HttpContext http, long id, ShiftActionRequest b, ShiftContinuityService service, CancellationToken ct) =>
            Command(http, b.Reason, c => service.AssignActionAsync(new AssignShiftAction(id, b.Description, b.SourceKind, b.SourceReference, b.Assignee), c, ct)));
        group.MapPost("/actions/{id:long}/status", (HttpContext http, long id, ShiftActionStatusRequest b, ShiftContinuityService service, CancellationToken ct) =>
            Command(http, b.Reason, c => service.TransitionActionAsync(new TransitionShiftAction(id, b.Expected, b.Status), c, ct)));
        group.MapGet("/closeouts", async (HttpContext http, string? workCenter, int? limit, ShiftContinuityService service, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await service.CloseoutsAsync(workCenter, limit ?? 30, principal, ct));
        });
        group.MapPost("/closeouts", (HttpContext http, ShiftCloseoutRequest b, ShiftContinuityService service, CancellationToken ct) =>
            Command(http, b.Reason, c => service.CloseShiftAsync(new CloseShift(b.WorkCenterCode, b.ShiftCode, b.ShiftFrom, b.ShiftUntil, b.Note), c, ct)));
    }
}
