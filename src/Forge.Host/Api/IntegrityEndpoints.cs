using Forge.Host.Integrity;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public static class IntegrityEndpoints
{
    public static void MapIntegrityEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/integrity").RequireAuthorization();
        api.MapGet("/exceptions", async (HttpContext http, IntegrityExceptionReadStore reads, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null) return NotRegistered();
            return Results.Ok(await reads.ListAsync(principal, ct));
        });
    }
}
