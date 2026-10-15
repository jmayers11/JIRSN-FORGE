using Forge.Quality.Acceptance;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public static class ReturnAcceptanceEndpoints
{
 public static void MapReturnAcceptanceEndpoints(this IEndpointRouteBuilder app)
 {
  app.MapGet("/api/quality/acceptance/return-cohorts/{id:long}",async(HttpContext http,ReturnCohortReadService service,long id,CancellationToken ct)=>
  {var principal=await PrincipalAsync(http);return principal is null?NotRegistered():Results.Ok(await service.ReadAsync(principal,id,ct));}).RequireAuthorization();
 }
}
