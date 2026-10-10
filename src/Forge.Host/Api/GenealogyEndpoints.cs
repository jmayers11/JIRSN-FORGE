using Forge.Planning;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

/// <summary>V2.11: genealogy of a serial, an order and an asset, read from the as-built and other records.</summary>
public static class GenealogyEndpoints
{
    public static void MapGenealogyEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/execution/serials/{serial}/genealogy", async (HttpContext http, string serial, string? part, GenealogyService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await s.SerialAsync(principal, part, serial, ct));
        }).RequireAuthorization();
        app.MapGet("/api/orders/{order:long}/genealogy", async (HttpContext http, long order, GenealogyService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await s.OrderAsync(principal, order, ct));
        }).RequireAuthorization();
        app.MapGet("/api/maintenance/assets/{code}/genealogy", async (HttpContext http, string code, GenealogyService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await s.AssetAsync(principal, code, ct));
        }).RequireAuthorization();
    }
}
