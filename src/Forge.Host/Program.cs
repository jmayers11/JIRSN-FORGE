using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Text.Json.Serialization;
using Forge.Competency.Infrastructure;
using Forge.Engineering.Infrastructure;
using Forge.Execution.Infrastructure;
using Forge.Maintenance.Infrastructure;
using Forge.Inventory.Infrastructure;
using Forge.Procurement.Infrastructure;
using Forge.Programs.Infrastructure;
using Forge.Projects.Infrastructure;
using Forge.Quality.Infrastructure;
using Forge.Host.Api;
using Forge.Host.Security;
using Forge.Kernel.Access;
using Forge.Kernel.Postgres;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Forge.Planning.Infrastructure;
using Forge.Resources.Infrastructure;
using Forge.SafetyRules.Infrastructure;
using Forge.Workforce.Infrastructure;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Forge")
    ?? throw new InvalidOperationException("Connection string 'Forge' is not configured.");

// Bounded contexts in dependency order (migrations run in this order).
builder.Services.AddForgeKernel(connectionString);
builder.Services.AddForgeCompetency();
builder.Services.AddForgeResources();
builder.Services.AddForgeWorkforce();
builder.Services.AddForgeSafetyRules();
builder.Services.AddForgeEngineering();
builder.Services.AddSingleton<Forge.Engineering.Contracts.IDocumentTargetResolver, Forge.Host.Documents.DocumentTargetResolver>();
builder.Services.AddSingleton<Forge.Procurement.Contracts.ITechnicalPackageResolver, Forge.Host.Documents.TechnicalPackageResolver>();
builder.Services.AddForgePrograms();
builder.Services.AddForgeProjects();
builder.Services.AddForgeExecution();
builder.Services.AddForgeMaintenance();
builder.Services.AddForgeQuality();
builder.Services.AddForgeProcurement();
builder.Services.AddForgeInventory();
builder.Services.AddForgePlanning();

// V2.5: propagation of released engineering changes (a subscriber, with retries of incomplete propagations).
builder.Services.TryAddSingleton<Forge.Host.Propagation.IPropagationFaults, Forge.Host.Propagation.NoPropagationFaults>();
builder.Services.AddSingleton<Forge.Host.Propagation.ChangePropagationRunner>();
builder.Services.AddSingleton<Forge.Host.Propagation.PropagationRetry>();
builder.Services.AddSingleton<Forge.Kernel.Postgres.IEventSubscriber, Forge.Host.Propagation.ChangePropagationSubscriber>();
builder.Services.AddSingleton<Forge.Kernel.Postgres.IEventSubscriber, Forge.Host.Installation.InstallationSubscriber>();

// Optional generative AI for Forge Analyst (off by default; Forge works fully without it).
var narratorOptions = builder.Configuration.GetSection("Forge:Analyst").Get<Forge.Host.Api.AnalystNarratorOptions>() ?? new();
if (string.Equals(narratorOptions.Provider, "anthropic", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(narratorOptions.ApiKey))
{
    builder.Services.AddHttpClient("forge-analyst");
    builder.Services.AddSingleton<Forge.Planning.Analyst.IAnalystNarrator>(sp => new Forge.Host.Api.AnthropicNarrator(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("forge-analyst"), narratorOptions, sp.GetRequiredService<ILogger<Forge.Host.Api.AnthropicNarrator>>()));
}
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
if (builder.Configuration.GetValue("Forge:BackgroundServices:Enabled", true))
{
    builder.Services.AddForgeKernelBackgroundServices();
    builder.Services.AddHostedService<Forge.Host.Propagation.PropagationRetryService>();
}

builder.Services.AddSingleton<RequestPrincipal>();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemMapping>();

var authMode = builder.Configuration["Forge:Authentication:Mode"] ?? "Oidc";
if (string.Equals(authMode, "Development", StringComparison.OrdinalIgnoreCase))
{
    if (!builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException("Development authentication is only allowed in the Development environment.");
    }

    builder.Services.AddAuthentication(DevelopmentAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(DevelopmentAuthenticationHandler.SchemeName, null);
}
else if (string.Equals(authMode, "Oidc", StringComparison.OrdinalIgnoreCase))
{
    // Any OIDC provider (Nexus, Entra ID, Okta, Keycloak): Forge validates its access tokens.
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = builder.Configuration["Forge:Authentication:Authority"];
            options.Audience = builder.Configuration["Forge:Authentication:Audience"];
            options.MapInboundClaims = false;
        });
}
else if (!string.Equals(authMode, "External", StringComparison.OrdinalIgnoreCase))
{
    // "External" means the caller (for example a test host) registers authentication itself.
    throw new InvalidOperationException($"Unknown authentication mode '{authMode}'.");
}

builder.Services.AddAuthorization();

var app = builder.Build();

if (app.Configuration.GetValue("Forge:Database:MigrateOnStartup", false))
{
    if (app.Environment.IsDevelopment() && app.Configuration.GetValue("Forge:Database:CreateIfMissing", false))
    {
        await DevelopmentDatabase.EnsureExistsAsync(connectionString, app.Logger, CancellationToken.None);
    }

    await app.Services.GetRequiredService<MigrationRunner>().MigrateAsync(CancellationToken.None);
}

var bootstrapSubject = app.Configuration["Forge:Bootstrap:AdminSubject"];
if (!string.IsNullOrWhiteSpace(bootstrapSubject))
{
    var created = await app.Services.GetRequiredService<AccessService>().BootstrapAdministratorAsync(
        bootstrapSubject,
        app.Configuration["Forge:Bootstrap:AdminDisplayName"] ?? bootstrapSubject,
        CancellationToken.None);
    if (created)
    {
        app.Logger.LogWarning("Bootstrapped the first security administrator for subject {Subject}.", bootstrapSubject);
    }
}

app.UseExceptionHandler();

// The Forge web UI: static files only. Every number it shows comes from the API below.
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    try
    {
        await using var cmd = db.CreateCommand("SELECT 1");
        await cmd.ExecuteScalarAsync(ct);
        return Results.Ok(new { status = "ready" });
    }
    catch (NpgsqlException)
    {
        return Results.Problem(title: "Database unavailable", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapUiEndpoints();
app.MapKernelEndpoints();
app.MapOperationsEndpoints();
app.MapEngineeringEndpoints();
app.MapDocumentEndpoints();
app.MapChangeEndpoints();
app.MapAlternateEndpoints();
app.MapAssetEndpoints();
app.MapMaintenanceReadinessEndpoints();
app.MapPlanningEndpoints();
app.MapShopFloorEndpoints();
app.MapProductionActualsEndpoints();
app.MapMaterialsEndpoints();
app.MapQualityEndpoints();
app.MapProgramsEndpoints();
app.MapProjectEndpoints();
app.MapGenealogyEndpoints();
app.MapHistoryEndpoints();
app.MapReportsEndpoints();
app.MapAnalystEndpoints();

await app.RunAsync();

/// <summary>Entry point; public so integration tests can host the app.</summary>
public partial class Program;
