using Forge.Planning.Analyst;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record AskRequest(string Question, bool? Narrate);

public sealed record AnalystStatus(bool Narration, string Provider, IReadOnlyList<string> Suggestions);

public static class AnalystEndpoints
{
    public static void MapAnalystEndpoints(this IEndpointRouteBuilder app)
    {
        var analyst = app.MapGroup("/api/analyst").RequireAuthorization();

        analyst.MapGet("/status", (HttpContext http, AnalystService s) =>
            Read(http, Forge.Kernel.Security.Permissions.AnalystUse, _ => Task.FromResult<object?>(new AnalystStatus(s.NarrationAvailable, s.NarratorName, AnalystService.Suggestions))));

        // Questions are reads: they change nothing, so they are posted (for the text) but not run as commands.
        analyst.MapPost("/interpret", async (HttpContext http, AskRequest b, AnalystService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await s.InterpretAsync(principal, b.Question, ct));
        });

        analyst.MapPost("/ask", async (HttpContext http, AskRequest b, AnalystService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await s.AskAsync(principal, b.Question, b.Narrate ?? false, ct));
        });

        analyst.MapGet("/brief", async (HttpContext http, string? shift, BriefService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await s.BriefAsync(principal, string.IsNullOrWhiteSpace(shift) ? null : shift.Trim(), ct));
        });
    }
}
