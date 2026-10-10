using Forge.Planning;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

/// <summary>V2.12: "What changed?" for one subject, and Horizon's future events — both only from records.</summary>
public static class HistoryEndpoints
{
    public static void MapHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/history/{subject}", async (HttpContext http, string subject, string? kind, int? days, HistoryService s, TimeProvider time, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null)
            {
                return NotRegistered();
            }

            DateTimeOffset? since = days is { } d && d > 0 ? time.GetUtcNow().AddDays(-Math.Min(d, 3660)) : null;
            return Results.Ok(await s.WhatChangedAsync(principal, subject, kind, since, ct));
        }).RequireAuthorization();
        app.MapGet("/api/horizon/events", async (HttpContext http, int? days, HistoryService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await s.FutureAsync(principal, days ?? 90, ct));
        }).RequireAuthorization();
    }
}
