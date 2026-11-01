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
using Forge.Quality.Qms;
using Forge.Host.Api;
using Forge.Host.Security;
using Forge.Host.Notifications;
using Forge.Host.WorkInbox;
using Forge.Host.Integrity;
using Forge.Host.History;
using Forge.Host.Identification;
using Forge.Host.Qms;
using Forge.Host.Commercial;
using Forge.Host.Quality;
using Forge.Kernel.Access;
using Forge.Kernel.Postgres;
using Forge.Quality.Planning;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Forge.Planning.Infrastructure;
using Forge.Resources.Infrastructure;
using Forge.SafetyRules.Infrastructure;
using Forge.Sales.Infrastructure;
using Forge.Workforce.Infrastructure;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Forge")
    ?? throw new InvalidOperationException("Connection string 'Forge' is not configured.");

// Bounded contexts in dependency order (migrations run in this order).
// Sprint 9 demo tenant: only a host started with Forge:Demo:Enabled has a simulated clock, and even then only a designated demo database moves it.
var demoEnabled = builder.Configuration.GetValue("Forge:Demo:Enabled", false);
builder.Services.AddSingleton(new Forge.Host.Demo.DemoOptions(demoEnabled, connectionString));
builder.Services.AddSingleton(_ => new Forge.Host.Demo.DemoClock(TimeProvider.System));
if (demoEnabled)
{
    builder.Services.AddSingleton<TimeProvider>(sp => sp.GetRequiredService<Forge.Host.Demo.DemoClock>());
}

builder.Services.AddForgeKernel(connectionString);
builder.Services.AddForgeStore<Forge.Host.Demo.IDemoStore>(uow => new Forge.Host.Demo.DemoStore(uow));
builder.Services.AddSingleton<Forge.Host.Demo.DemoService>();
builder.Services.AddForgeStore<INotificationStore>(uow => new NotificationStore(uow));
builder.Services.AddForgeResources();
builder.Services.AddForgeCompetency();
builder.Services.AddForgeWorkforce();
builder.Services.AddForgeSafetyRules();
builder.Services.AddForgeEngineering();
builder.Services.AddSingleton<Forge.Engineering.Contracts.IDocumentTargetResolver, Forge.Host.Documents.DocumentTargetResolver>();
builder.Services.AddSingleton<Forge.Kernel.Holds.IHoldTargetResolver, Forge.Host.Documents.HoldTargetResolver>();
builder.Services.AddSingleton<Forge.Procurement.Contracts.ITechnicalPackageResolver, Forge.Host.Documents.TechnicalPackageResolver>();
builder.Services.AddSingleton<Forge.Procurement.Contracts.ISourcingPackageResolver, Forge.Host.Documents.SourcingPackageResolver>();
builder.Services.AddForgePrograms();
builder.Services.AddForgeProjects();
builder.Services.AddForgeExecution();
builder.Services.AddForgeMaintenance();
builder.Services.AddForgeQuality();
builder.Services.AddForgeQms(builder.Configuration["Forge:Qms:Organization"]);
builder.Services.AddForgeQmsPlanning();
builder.Services.AddForgeAuditCapa();
builder.Services.AddForgeQmsReview();
builder.Services.AddSingleton<Forge.Quality.Contracts.IQmsPlanningEvidenceReader, QmsPlanningEvidenceReader>();
builder.Services.AddSingleton<Forge.Quality.Contracts.IInspectionSubjectResolver, Forge.Host.Quality.InspectionSubjectResolver>();
builder.Services.AddSingleton<Forge.Quality.Contracts.ITraceInvestigationResolver, Forge.Host.Quality.TraceInvestigationResolver>();
builder.Services.AddSingleton<Forge.Quality.Contracts.IQmsPlanningOwnerEvidenceResolver, Forge.Host.Quality.InvestigationPlanningEvidenceResolver>();
builder.Services.AddSingleton<Forge.Quality.Contracts.IAuditorQualificationReader, AuditorQualificationReader>();
builder.Services.AddSingleton<Forge.Quality.Contracts.IQmsPlanningOwnerEvidenceResolver, AuditCapaOwnerEvidenceResolver>();
builder.Services.AddSingleton<Forge.Quality.Contracts.IQmsPlanningOwnerEvidenceResolver, QmsReviewOwnerEvidenceResolver>();
builder.Services.AddSingleton<Forge.Quality.Contracts.ICommercialQualitySourceResolver, CommercialQualitySourceResolver>();
builder.Services.AddSingleton<Forge.Inventory.Contracts.IReturnAcceptanceSourceResolver, ReturnAcceptanceSourceResolver>();
builder.Services.AddForgeProcurement();
builder.Services.AddForgeInventory();
// Owner decisions 2026-10-31: quote to cash. Sales migrations follow inventory (invoice lines name shipments).
builder.Services.AddForgeSales();
builder.Services.AddSingleton<Forge.Sales.Contracts.ISalesShipmentResolver, Forge.Host.Sales.SalesShipmentResolver>();
builder.Services.AddSingleton<Forge.Host.Sales.AccountingExportService>();
builder.Services.AddSingleton<Forge.Workforce.Contracts.ITimeChargeResolver, Forge.Host.Workforce.TimeChargeResolver>();
builder.Services.AddSingleton<Forge.Host.Costing.JobCostService>();
builder.Services.AddSingleton<Forge.Execution.IOrderIssueLookup, Forge.Host.Api.OrderIssueLookup>();
builder.Services.AddSingleton<Forge.Host.Sales.PromiseDateService>();
// Owner decisions 2026-11-01: statistical process control and supplier scorecards.
builder.Services.AddSingleton<Forge.Host.Quality.SpcChartService>();
builder.Services.AddSingleton<Forge.Host.Procurement.SupplierScorecardService>();
// Owner decisions 2026-11-01: API and EDI (ANSI X12 through a folder, partner API keys, signed webhooks).
Forge.Interchange.Infrastructure.InterchangeRegistration.AddForgeInterchange(builder.Services);
builder.Services.AddSingleton<Forge.Interchange.IInterchangePartyResolver, Forge.Host.Interchange.InterchangePartyResolver>();
builder.Services.AddSingleton<Forge.Host.Interchange.FolderExchange>();
builder.Services.AddSingleton<Forge.Host.Interchange.WebhookDispatcher>();
builder.Services.AddHttpClient("forge-webhooks");
builder.Services.AddSingleton<Forge.Host.Interchange.IWebhookTransport, Forge.Host.Interchange.HttpWebhookTransport>();
// Owner decisions 2026-11-01: machine connections a plant picks per machine, downtime at the terminal, OEE per shift.
Forge.Connectivity.Infrastructure.ConnectivityRegistration.AddForgeConnectivity(builder.Services);
builder.Services.AddHttpClient("forge-machines");
builder.Services.AddSingleton<Forge.Host.Connectivity.IMachineConnector, Forge.Host.Connectivity.MTConnectConnector>();
builder.Services.AddSingleton<Forge.Host.Connectivity.IMachineConnector, Forge.Host.Connectivity.OpcUaConnector>();
builder.Services.AddSingleton<Forge.Host.Connectivity.IMachineConnector, Forge.Host.Connectivity.MqttConnector>();
builder.Services.AddSingleton<Forge.Host.Connectivity.IMachineConnector, Forge.Host.Connectivity.ModbusTcpConnector>();
builder.Services.AddSingleton<Forge.Host.Connectivity.IMachineConnector, Forge.Host.Connectivity.FileDropConnector>();
builder.Services.AddSingleton<Forge.Host.Connectivity.IMachineConnector, Forge.Host.Connectivity.SimulatorConnector>();
builder.Services.AddSingleton<Forge.Host.Connectivity.MachinePoller>();
builder.Services.AddSingleton<Forge.Host.Connectivity.OeeService>();
builder.Services.AddForgePlanning();
builder.Services.AddSingleton<Forge.Host.Analyst.QmsAnalystService>();

builder.Services.AddSingleton<NotificationCommands>();
builder.Services.AddSingleton<NotificationReadStore>();
builder.Services.AddSingleton<WorkInboxStore>();
builder.Services.AddSingleton<Forge.Host.DailyMission.DailyMissionService>();
builder.Services.AddSingleton<Forge.Host.DailyMission.DailyMissionStateStore>();
builder.Services.AddSingleton<WorkSourceResolver>();
builder.Services.AddSingleton<Forge.Kernel.WorkAssignments.IWorkSourceResolver>(sp => sp.GetRequiredService<WorkSourceResolver>());
builder.Services.AddSingleton<Forge.Kernel.Commands.ICommandSourceResolver>(sp => sp.GetRequiredService<WorkSourceResolver>());
builder.Services.AddSingleton<IdentificationResolver>();
builder.Services.AddForgeStore<IIdentificationPresentationStore>(uow => new LabelStore(uow));
builder.Services.AddForgeStore<Forge.Engineering.Contracts.IGeneratedDocumentContentStore>(uow => new GeneratedDocumentContentStore(uow));
builder.Services.AddScoped<Forge.Host.Workforce.PlantVisualService>();
builder.Services.AddScoped<Forge.Host.Quality.ReworkPlanService>();
builder.Services.AddSingleton<Forge.Host.Costing.DispositionCostService>();
builder.Services.AddSingleton<Forge.Host.Workforce.DevelopmentReadinessService>();
builder.Services.AddScoped<Forge.Host.Planning.ReplacementOrderService>();
builder.Services.AddSingleton<Forge.Host.Quality.QualityTrendService>();
builder.Services.AddSingleton<Forge.Host.Workforce.CauseAdjustedPerformanceService>();
builder.Services.AddSingleton<Forge.Host.Workforce.CoverageContributionService>();
builder.Services.AddSingleton<Forge.Host.Workforce.EmployeePerformanceCardService>();
builder.Services.AddForgeStore<IScanSessionStore>(uow => new PostgresScanSessionStore(uow));
builder.Services.AddSingleton<PresentationAccess>();
builder.Services.AddSingleton<LabelRendering>();
builder.Services.AddSingleton<LabelService>();
builder.Services.AddSingleton<Forge.Engineering.Contracts.IGeneratedContentOwnerAccess, LabelFileOwnerAccess>();
builder.Services.AddSingleton<ScannerOptions>();
builder.Services.AddSingleton<ScanAttemptRetentionOptions>();
builder.Services.AddSingleton<ScanSessionAuthority>();
builder.Services.AddSingleton<ScanSessionService>();
builder.Services.AddSingleton<ScanCommandParticipant>();
builder.Services.AddSingleton<CommercialCommandGuard>();
builder.Services.AddSingleton<HostCommandParticipant>();
builder.Services.AddSingleton<Forge.Kernel.Commands.ICommandTransactionParticipantFactory, HostCommandParticipantFactory>();
builder.Services.AddSingleton<OwnedSourceAccess>();
builder.Services.AddSingleton<AcceptanceNcrSourceAccess>();
builder.Services.AddSingleton<AcceptanceSourceAccess>();
builder.Services.AddSingleton<IntegrityExceptionReadStore>();
builder.Services.AddSingleton<CauseMapReadStore>();
builder.Services.AddSingleton<NotificationDeliveryService>();
builder.Services.AddSingleton<NotificationEventSubscriber>();
builder.Services.AddSingleton<Forge.Kernel.Postgres.IEventSubscriber>(sp => sp.GetRequiredService<NotificationEventSubscriber>());
var notificationEmailEndpoint = builder.Configuration["Forge:Notifications:EmailEndpoint"];
if (!string.IsNullOrWhiteSpace(notificationEmailEndpoint))
{
    var emailBearerToken = builder.Configuration["Forge:Notifications:EmailBearerToken"];
    if (!Uri.TryCreate(notificationEmailEndpoint, UriKind.Absolute, out var emailUri)
        || (emailUri.Scheme != Uri.UriSchemeHttps && !(builder.Environment.IsDevelopment() && emailUri.IsLoopback && emailUri.Scheme == Uri.UriSchemeHttp)))
        throw new InvalidOperationException("Forge:Notifications:EmailEndpoint must be HTTPS (HTTP loopback is allowed only in Development).");
    if (!string.IsNullOrWhiteSpace(emailBearerToken))
        builder.Services.AddHttpClient("forge-notification-email", client => client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", emailBearerToken));
    else
        builder.Services.AddHttpClient("forge-notification-email");
    builder.Services.AddSingleton<INotificationEmailProvider>(sp => new ConfiguredEmailProvider(sp.GetRequiredService<IHttpClientFactory>().CreateClient("forge-notification-email"), emailUri));
}
else
{
    builder.Services.AddSingleton<INotificationEmailProvider, UnconfiguredEmailProvider>();
}

// V2.5: propagation of released engineering changes (a subscriber, with retries of incomplete propagations).
builder.Services.TryAddSingleton<Forge.Host.Propagation.IPropagationFaults, Forge.Host.Propagation.NoPropagationFaults>();
builder.Services.AddSingleton<Forge.Host.Propagation.ChangePropagationRunner>();
builder.Services.AddSingleton<Forge.Host.Propagation.PropagationRetry>();
builder.Services.AddSingleton<Forge.Kernel.Postgres.IEventSubscriber, Forge.Host.Propagation.ChangePropagationSubscriber>();
builder.Services.AddSingleton<Forge.Kernel.Postgres.IEventSubscriber, Forge.Host.Installation.InstallationSubscriber>();
builder.Services.AddSingleton<Forge.Kernel.Postgres.IEventSubscriber, Forge.Host.Replanning.QualityReplanSubscriber>();
builder.Services.AddSingleton<Forge.Kernel.Postgres.IEventSubscriber, Forge.Host.Sales.SalesDemandSubscriber>();
builder.Services.AddSingleton<Forge.Kernel.Postgres.IEventSubscriber, Forge.Host.Sales.StockReservationSubscriber>();
builder.Services.AddSingleton<Forge.Kernel.Postgres.IEventSubscriber, Forge.Host.Quality.SpcSubscriber>();
builder.Services.AddSingleton<Forge.Kernel.Postgres.IEventSubscriber, Forge.Host.Interchange.InterchangeProcessor>();
builder.Services.AddSingleton<Forge.Kernel.Postgres.IEventSubscriber, Forge.Host.Interchange.WebhookSubscriber>();
builder.Services.AddSingleton<Forge.Kernel.Postgres.IEventSubscriber, Forge.Host.Connectivity.MachineDowntimeSubscriber>();
builder.Services.AddSingleton<Forge.Host.Moves.TemporaryMoveReconciler>();
builder.Services.AddSingleton<Forge.Host.Training.TrainingExpiryScanner>();
builder.Services.AddSingleton<Forge.Host.Planning.MaterialRiskScanner>();
// One site per database for now (owner decision 2026-10-25); raise Forge:Sites:MaxSites (0 = no limit) when multi-site operation starts.
builder.Services.AddSingleton(new Forge.Resources.Contracts.SitePolicy(builder.Configuration.GetValue<int?>("Forge:Sites:MaxSites") ?? 1));
// Risk Radar bands (owner decision 2026-10-27): High = today up to 6 days out, Medium = 7 to 29, Low = 30 or more.
builder.Services.AddSingleton(new Forge.Planning.RiskRadarOptions(
    builder.Configuration.GetValue<int?>("Forge:RiskRadar:HighMaxDays") ?? 6, builder.Configuration.GetValue<int?>("Forge:RiskRadar:MediumMaxDays") ?? 29));

var sentinelEventsEndpoint = builder.Configuration["Forge:Sentinel:SecurityEventsEndpoint"];
if (!string.IsNullOrWhiteSpace(sentinelEventsEndpoint))
{
    var sentinelBearerToken = builder.Configuration["Forge:Sentinel:SecurityEventsBearerToken"];
    if (string.IsNullOrWhiteSpace(sentinelBearerToken))
    {
        throw new InvalidOperationException("Forge:Sentinel:SecurityEventsBearerToken is required when a Sentinel security-event endpoint is configured.");
    }

    if (!Uri.TryCreate(sentinelEventsEndpoint, UriKind.Absolute, out var sentinelUri)
        || (sentinelUri.Scheme != Uri.UriSchemeHttps && !(builder.Environment.IsDevelopment() && sentinelUri.IsLoopback && sentinelUri.Scheme == Uri.UriSchemeHttp)))
    {
        throw new InvalidOperationException("Forge:Sentinel:SecurityEventsEndpoint must be HTTPS (HTTP loopback is allowed only in Development).");
    }

    builder.Services.AddHttpClient("forge-sentinel-security-events", client =>
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sentinelBearerToken));
    builder.Services.AddSingleton<Forge.Kernel.Postgres.IEventSubscriber>(sp => new Forge.Host.Security.SentinelSecurityEventSubscriber(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("forge-sentinel-security-events"),
        sentinelUri));
}

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
    builder.Services.AddHostedService<NotificationDeliveryWorker>();
    builder.Services.AddHostedService<Forge.Host.Interchange.InterchangeWorker>();
    builder.Services.AddHostedService<Forge.Host.Connectivity.MachinePollingWorker>();
    builder.Services.AddHostedService<Forge.Host.Moves.TemporaryMoveMonitor>();
    builder.Services.AddHostedService<Forge.Host.Training.TrainingExpiryMonitor>();
    builder.Services.AddHostedService<Forge.Host.Planning.MaterialRiskMonitor>();
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

// Each site's own clock decides "today" (ADR 0057 §1): read the site zones once the schema is in place.
await app.Services.GetRequiredService<SiteClockLoader>().LoadAsync(CancellationToken.None);
await app.Services.GetRequiredService<Forge.Host.Demo.DemoService>().LoadClockAsync(CancellationToken.None);

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
app.UseMiddleware<StepUpMiddleware>();
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
app.MapIdentificationEndpoints();
app.MapIdentificationPresentationEndpoints();
app.MapScanSessionEndpoints();
app.MapKernelEndpoints();
app.MapScimEndpoints();
app.MapOperationsEndpoints();
app.MapPlantVisualEndpoints();
app.MapScrap();
app.MapPartialBuilds();
app.MapDemo();
app.MapPerformanceAttribution();
app.MapSkillCapacity();
app.MapEngineeringEndpoints();
app.MapDocumentEndpoints();
app.MapChangeEndpoints();
app.MapAlternateEndpoints();
app.MapAssetEndpoints();
app.MapMaintenanceReadinessEndpoints();
app.MapPlanningEndpoints();
app.MapShopFloorEndpoints();
app.MapProductionActualsEndpoints();
app.MapShiftContinuityEndpoints();
app.MapNotificationEndpoints();
app.MapWorkInboxEndpoints();
app.MapDailyMissionEndpoints();
app.MapIntegrityEndpoints();
app.MapShelfLifeEndpoints();
app.MapGoalEndpoints();
app.MapPlanningScenarioEndpoints();
app.MapToolEndpoints();
app.MapHoldEndpoints();
app.MapReceivingEndpoints();
app.MapSourcingEndpoints();
app.MapExecutableEndpoints();
app.MapMaterialsEndpoints();
app.MapQualityEndpoints();
app.MapQmsEndpoints();
app.MapQmsPolicyAdoptionEndpoints();
app.MapQmsPlanningEndpoints();
app.MapAuditCapaEndpoints();
app.MapQmsReviewEndpoints();
app.MapCommercialQualityEndpoints();
app.MapReturnAcceptanceEndpoints();
app.MapProductAcceptanceEndpoints();
app.MapCostingEndpoints();
app.MapSalesEndpoints();
app.MapTimekeepingEndpoints();
app.MapAmendmentEndpoints();
app.MapReservationEndpoints();
app.MapSpcEndpoints();
app.MapInterchangeEndpoints();
app.MapConnectivityEndpoints();
app.MapInvestigationEndpoints();
app.MapProgramsEndpoints();
app.MapProjectEndpoints();
app.MapGenealogyEndpoints();
app.MapHistoryEndpoints();
app.MapHorizonLensEndpoints();
app.MapReportsEndpoints();
app.MapAnalystEndpoints();

await app.RunAsync();

/// <summary>Entry point; public so integration tests can host the app.</summary>
public partial class Program;
