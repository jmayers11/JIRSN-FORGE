using Forge.Host.Notifications;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public static class NotificationEndpoints
{
    public static void MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/notifications").RequireAuthorization();

        api.MapGet("/inbox", (HttpContext http, bool? openOnly, bool? unreadOnly, NotificationReadStore reads, CancellationToken ct) =>
            Read(http, Permissions.NotificationsRead, async principal => await reads.InboxAsync(principal, openOnly ?? false, unreadOnly ?? false, ct)));

        api.MapPost("/alerts/{alertId:guid}/acknowledge", async (HttpContext http, Guid alertId, ReasonRequest body, NotificationCommands commands, NotificationReadStore reads, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null) return NotRegistered();
            var (program, addressed) = await reads.AlertAccessAsync(alertId, principal, ct);
            if (!addressed) return Results.NotFound();
            var (context, error) = await ContextAsync(http, body.Reason, null, Forge.Kernel.Commands.CommandSource.Api);
            if (context is null) return error!;
            return Results.Ok(await commands.AcknowledgeAsync(alertId, program is { } id ? new ProgramId(id) : null, context, ct));
        });

        api.MapGet("/rules", (HttpContext http, NotificationReadStore reads, CancellationToken ct) =>
            Read(http, Permissions.NotificationRulesManage, async _ => await reads.RulesAsync(ct)));

        api.MapGet("/event-types", (HttpContext http, NotificationReadStore reads, CancellationToken ct) =>
            Read(http, Permissions.NotificationRulesManage, async _ => await reads.EventTypesAsync(ct)));

        api.MapPost("/rules", (HttpContext http, NotificationRuleDraft body, NotificationCommands commands, CancellationToken ct) =>
            Command(http, body.Reason, context => commands.SaveRuleAsync(body, context, ct)));
    }
}
