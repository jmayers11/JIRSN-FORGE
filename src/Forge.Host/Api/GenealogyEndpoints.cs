using Forge.Planning;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

/// <summary>V2.11: genealogy of a serial, an order and an asset, read from the as-built and other records. V3.3: the genealogy graph.</summary>
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

        // V3.3: the genealogy graph, traced back, forward or both from an instance, an order or a shipment.
        app.MapGet("/api/genealogy/trace", async (HttpContext http, string? part, string? lot, long? order, long? shipment, TraceDirection? direction, GenealogyGraphService s, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null ? NotRegistered() : Results.Ok(await s.TraceAsync(principal, part, lot, order, shipment, direction ?? TraceDirection.Both, ct));
        }).RequireAuthorization();
    }
}
