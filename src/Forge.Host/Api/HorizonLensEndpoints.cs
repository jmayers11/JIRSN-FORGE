using Forge.Planning.Reporting;
using Forge.Kernel.Commands;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public static class HorizonLensEndpoints
{
    public static void MapHorizonLensEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/horizon/lenses", async (HttpContext http, HorizonLens? lens, DateTimeOffset? at,
            DateTimeOffset? from, DateTimeOffset? to, Guid? programId, long? contractId, string? workCenter,
            string? machine, long? orderNumber, Guid? personId, bool? onlyCommitted, HorizonLensService service, TimeProvider time, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null) return NotRegistered();
            var selected = lens ?? HorizonLens.Factory;
            if (!Enum.IsDefined(selected)) throw new CommandRejectedException("unknown_horizon_lens", "Select one of the seven Horizon lenses.");
            var instant = at ?? time.GetUtcNow();
            return Results.Ok(await service.ReadAsync(principal, new HorizonLensRequest(selected, instant, from ?? instant, to ?? instant.AddDays(14),
                programId, contractId, string.IsNullOrWhiteSpace(workCenter) ? null : workCenter.Trim(),
                string.IsNullOrWhiteSpace(machine) ? null : machine.Trim(), orderNumber, personId, onlyCommitted ?? false), ct));
        }).RequireAuthorization();
    }
}
