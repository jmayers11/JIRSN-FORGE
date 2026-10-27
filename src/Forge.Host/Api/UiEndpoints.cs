namespace Forge.Host.Api;

/// <summary>What the web UI needs to know before anyone signs in: how sign-in works here.</summary>
internal static class UiEndpoints
{
    public static void MapUiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/ui/config", (IConfiguration config, IHostEnvironment env) => Results.Ok(new
        {
            authMode = config["Forge:Authentication:Mode"] ?? "Oidc",
            authority = config["Forge:Authentication:Authority"],
            clientId = config["Forge:Authentication:ClientId"],
            scope = config["Forge:Authentication:Scope"] ?? "openid profile",
            environment = env.EnvironmentName,
            operationalVisualizationsEnabled = !bool.TryParse(config["Forge:Features:OperationalVisualizations"], out var visualizationsEnabled) || visualizationsEnabled,
        })).AllowAnonymous();
    }
}
