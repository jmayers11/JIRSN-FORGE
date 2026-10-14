using Forge.Host.Identification;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record ResolveIdentificationRequest(string? Code);

/// <summary>Read-only identification; scanning a key supplies no manufacturing authority.</summary>
internal static class IdentificationEndpoints
{
    public static void MapIdentificationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/identification/config", async (HttpContext http, IdentificationResolver resolver) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null) return NotRegistered();
            try
            {
                return Results.Ok(resolver.GetConfiguration());
            }
            catch (IdentificationException error) when (error.Code == "identification_not_configured")
            {
                return Failure(error, StatusCodes.Status503ServiceUnavailable);
            }
        }).RequireAuthorization();

        app.MapPost("/api/identification/resolve", async (HttpContext http, ResolveIdentificationRequest request, IdentificationResolver resolver, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null) return NotRegistered();

            try
            {
                return Results.Ok(await resolver.ResolveAsync(principal, request.Code, ct));
            }
            catch (IdentificationException error) when (error.Code is "identification_not_configured" or "invalid_identification" or "foreign_namespace" or "identification_not_found")
            {
                var status = error.Code switch
                {
                    "identification_not_configured" => StatusCodes.Status503ServiceUnavailable,
                    "identification_not_found" => StatusCodes.Status404NotFound,
                    _ => StatusCodes.Status400BadRequest,
                };
                return Failure(error, status);
            }
        }).RequireAuthorization();
    }

    private static IResult Failure(IdentificationException error, int status) =>
        Results.Problem(title: "Identification unavailable", detail: error.Message, statusCode: status,
            extensions: new Dictionary<string, object?> { ["code"] = error.Code });
}
