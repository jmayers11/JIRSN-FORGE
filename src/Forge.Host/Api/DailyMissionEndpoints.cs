using Forge.Host.DailyMission;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

internal static class DailyMissionEndpoints
{
    public static void MapDailyMissionEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/daily-mission").RequireAuthorization();
        api.MapGet("", async (HttpContext http, DailyMissionService mission, DailyMissionStateStore state, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null) return NotRegistered();
            var preferences = await state.GetPreferencesAsync(principal, ct);
            return Results.Ok(await mission.GetAsync(DailyMissionStateStore.ApplyProgramScope(principal, preferences.ProgramScope), ct));
        });
        api.MapGet("/recovery/{orderNumber:long}/{sequence:int}", async (HttpContext http, long orderNumber, int sequence, DailyMissionService mission, DailyMissionStateStore state, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null) return NotRegistered();
            var preferences = await state.GetPreferencesAsync(principal, ct);
            return Results.Ok(await mission.WorkforceRecoveryAsync(
                DailyMissionStateStore.ApplyProgramScope(principal, preferences.ProgramScope), orderNumber, sequence, ct));
        });
        api.MapPost("/session", async (HttpContext http, DailyMissionService mission, DailyMissionStateStore state, TimeProvider time, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null) return NotRegistered();
            var context = await mission.GetOperationalContextAsync(principal, ct);
            var briefing = context is { Relation: "Current" or "Upcoming" }
                ? await state.OpenBriefingAsync(principal.PersonId.Value, context.OperationalDate, context.OperationalDayId, DailyMissionStateStore.ScopeKey(principal), time.GetUtcNow(), ct)
                : new DailyMissionBriefingState(false, null, null);
            var preferences = briefing.ShouldShow ? await state.GetPreferencesAsync(principal, ct) : null;
            var scopedPrincipal = preferences is null ? principal : DailyMissionStateStore.ApplyProgramScope(principal, preferences.ProgramScope);
            var projection = briefing.ShouldShow ? await mission.GetAsync(scopedPrincipal, ct) : null;
            return Results.Ok(new DailyMissionSession(projection, briefing));
        });
        api.MapPost("/briefing/dismiss", async (HttpContext http, DailyMissionService mission, DailyMissionStateStore state, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null) return NotRegistered();
            var context = await mission.GetOperationalContextAsync(principal, ct);
            if (context is null) return Results.BadRequest(new { title = "Briefing unavailable", detail = "A current or upcoming assigned shift is needed to save this acknowledgement." });
            var result = await state.DismissBriefingAsync(principal.PersonId.Value, context.OperationalDate, context.OperationalDayId, DailyMissionStateStore.ScopeKey(principal), ct);
            return Results.Ok(result);
        });
        api.MapGet("/preferences", async (HttpContext http, DailyMissionService mission, DailyMissionStateStore state, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null) return NotRegistered();
            var preferences = await state.GetPreferencesAsync(principal, ct);
            return Results.Ok(preferences with { ProgramOptions = await mission.ProgramOptionsAsync(principal, ct) });
        });
        api.MapPut("/preferences", async (HttpContext http, SaveDailyMissionWorkspaceRequest request, DailyMissionService mission, DailyMissionStateStore state, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null) return NotRegistered();
            try
            {
                var preferences = await state.SavePreferencesAsync(principal, request, ct);
                return Results.Ok(preferences with { ProgramOptions = await mission.ProgramOptionsAsync(principal, ct) });
            }
            catch (ArgumentException exception) { return Results.BadRequest(new { title = "Invalid workspace preference", detail = exception.Message }); }
        });
    }
}
