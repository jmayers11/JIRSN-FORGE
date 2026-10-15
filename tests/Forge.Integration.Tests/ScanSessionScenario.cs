using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Forge.Host.Identification;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;
using Forge.Quality.Qms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Forge.Integration.Tests;

/// <summary>Real-command setup. Configuration declares development-only provider claims; no request supplies trust.</summary>
internal static class ScanSessionScenario
{
    public const string Namespace = "SESSION.Plant_A";
    public const string PolicyCode = "EXECUTION-SCAN";
    public const string AdoptionCode = "EXECUTION-SLIT";
    public const string PolicyDocument = "EXECUTION.SCAN.PROCEDURE";
    public const string Instruction = "SESSION-WI-SLIT";
    public const string SarahBadge = "SCAN-SARAH-PRIVATE-BADGE";
    public const string MichaelBadge = "SCAN-MICHAEL-PRIVATE-BADGE";
    public const string AdapterId = "configured-reader";
    public const string ProviderSessionSetting = "Forge:Authentication:DevelopmentProviderSessionId";
    public const string WorkstationSetting = "Forge:Authentication:DevelopmentWorkstationId";
    public const string AdapterEnabledSetting = "Forge:Identification:ScannerAdapters:0:Enabled";
    public static readonly string[] BusinessTables = ["execution.production_order", "execution.order_operation", "execution.order_material", "execution.labor", "execution.training_session", "execution.production_report"];
    public static readonly string[] EvidenceTables = ["kernel.identification_scan_session", "kernel.identification_scan_channel", "kernel.identification_scan_acceptance", "kernel.identification_scan_attempt", "kernel.audit_log", "kernel.outbox", "kernel.event_log", "kernel.idempotency"];

    internal sealed class Clock(DateTimeOffset at) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = at;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    internal sealed class Flow(ForgeApp app, SlittingPlant.Plant plant, HttpClient terminal, Clock clock, long orderA, long orderB) : IAsyncDisposable
    {
        public ForgeApp App { get; } = app;
        public SlittingPlant.Plant Plant { get; } = plant;
        public HttpClient Terminal { get; } = terminal;
        public Clock Time { get; } = clock;
        public long OrderA { get; } = orderA;
        public long OrderB { get; } = orderB;
        public IConfiguration Configuration => App.Services.GetRequiredService<IConfiguration>();
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    public static string Operation(long order) => $"forge:v1:{Namespace}:operation:{order}:10";
    public static string InstructionCode(string revision) => $"forge:v1:{Namespace}:instruction:{Instruction}:{revision}";

    public static async Task<Flow> CreateAsync(PostgresFixture postgres, bool instruction = false,
        bool trustedWorkstation = true, bool currentAuthenticatedOperator = false, bool providerClaims = true, bool requireOperationScan = true,
        bool attemptRetentionEnabled = false, int attemptRetentionDays = 30, int attemptMaxPerSession = 100)
    {
        var clock = new Clock(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        var app = new ForgeApp(await postgres.CreateDatabaseAsync(), services => services.AddSingleton<TimeProvider>(clock), builder =>
        {
            builder.UseSetting(CanonicalIdentification.NamespaceConfigurationKey, Namespace);
            builder.UseSetting(QmsService.ConfigurationKey, "SESSION-EXAMPLE-ORG");
            builder.UseSetting("Forge:Identification:ScannerAdapters:0:Id", AdapterId);
            builder.UseSetting("Forge:Identification:ScannerAdapters:0:DisplayName", "Explicitly configured example reader");
            builder.UseSetting("Forge:Identification:ScannerAdapters:0:InputMode", "ConfiguredAdapter");
            builder.UseSetting("Forge:Identification:ScannerAdapters:0:Symbologies:0", "QR");
            builder.UseSetting(AdapterEnabledSetting, "true");
            builder.UseSetting("Forge:Identification:ScanAttemptRetention:Enabled", attemptRetentionEnabled.ToString(CultureInfo.InvariantCulture));
            builder.UseSetting("Forge:Identification:ScanAttemptRetention:RetentionDays", attemptRetentionDays.ToString(CultureInfo.InvariantCulture));
            builder.UseSetting("Forge:Identification:ScanAttemptRetention:MaxPerSession", attemptMaxPerSession.ToString(CultureInfo.InvariantCulture));
            if (providerClaims)
            {
                builder.UseSetting(ProviderSessionSetting, "development-session-declared-by-server");
                builder.UseSetting(WorkstationSetting, "development-workstation-declared-by-server");
            }
        });
        try
        {
            var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
            const string role = "ExplicitScanExecution";
            await plant.Ok(plant.Admin, "/api/kernel/roles", new { role, displayName = "Explicit current scan execution authority", permissions = new[]
                { Permissions.ShopFloorRead, Permissions.ShopFloorRecord, Permissions.OrdersRead, Permissions.ResourcesRead,
                  Permissions.InventoryRead, Permissions.EngineeringRead, Permissions.QmsRead, "identification.read", "identification.scan.stage" },
                reason = "Assign actual source and stage permissions for this acceptance scenario" });
            await plant.Person("scan-terminal", Roles.ShopFloorTerminal, role);
            foreach (var worker in new[] { "sarah", "michael" })
                await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People[worker]}/roles", new { role, reason = "Explicit same-person authentication scenario authority" });
            await plant.Ok(plant.Admin, $"/api/workforce/people/{plant.People["sarah"]}/badge", new { badge = SarahBadge });
            await plant.Ok(plant.Admin, $"/api/workforce/people/{plant.People["michael"]}/badge", new { badge = MichaelBadge });
            var terminal = plant.As("scan-terminal");
            await plant.Ok(terminal, "/api/shopfloor/clock-in", new { badge = SarahBadge });
            await plant.Ok(terminal, "/api/shopfloor/clock-in", new { badge = MichaelBadge });
            if (instruction)
            {
                await plant.Ok(plant.Engineer, "/api/engineering/documents", new { number = Instruction, title = "Actual slitting work instruction", type = "WorkInstruction", authority = "Internal" });
                await ReleaseDocumentAsync(plant, Instruction, "A");
                await plant.Ok(plant.Engineer, "/api/engineering/items/ROLL-100/revisions/A/documents", new { documentNumber = Instruction, operationSequence = 10, reason = "Bind the actual instruction to this operation" });
                await plant.Ok(plant.Approver, "/api/engineering/documentation-policy/Make", new { enforced = true, reason = "Enforce the released operation instruction" });
            }
            var selector = new QmsOperationSelector(WorkCenterCode: "SLIT-WC", ActivityCode: "RUN_SLITTER", OperationSequence: 10, PartNumber: "ROLL-100", PartRevision: "A");
            QmsRequiredScanKind[] scans = instruction ? [QmsRequiredScanKind.Operation, QmsRequiredScanKind.ControlledInstruction] : requireOperationScan ? [QmsRequiredScanKind.Operation] : [];
            var presentation = new QmsExecutionPresentationRules(120, true, currentAuthenticatedOperator, trustedWorkstation,
                QmsProviderAssurance.Authenticated, [new("SLIT-CURRENT", selector, null, null, scans, true, instruction, trustedWorkstation)]);
            var policy = new QmsPolicyContent(QmsPolicyKind.Execution, "The example organization explicitly requires current server-owned operation staging", plant.People["eng"],
                "Released procedure and supervisor briefing", "On process or source change", true,
                new(Execution: new(true, true, true, true, presentation)));
            await QmsPolicyScenario.AdoptAsync(plant, PolicyCode, AdoptionCode, PolicyDocument, policy, QmsPolicyPurpose.Execution, selector);
            async Task<long> Order()
            {
                var result = await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = "ROLL-100", quantity = 10m, dueDate = clock.UtcNow.AddDays(10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), priority = 3 });
                var order = long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);
                await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Release the real operation configuration for scan acceptance" });
                await plant.Ok(plant.As("stores"), "/api/inventory/issues", new { orderNumber = order, partNumber = "COIL-AL", quantity = 26m, locationCode = "STORES", reason = "Actual material readiness before production" });
                return order;
            }
            return new(app, plant, terminal, clock, await Order(), await Order());
        }
        catch { await app.DisposeAsync(); throw; }
    }

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == status, $"Expected {(int)status}, got {(int)response.StatusCode}: {text}");
            using var parsed = JsonDocument.Parse(text); return parsed.RootElement.Clone();
        }
    }

    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, string path, object body, string key,
        JsonElement? session = null, Guid? correlation = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key);
        if (correlation is { } trace) request.Headers.Add("X-Correlation-Id", trace.ToString("D"));
        if (session is { } selected)
        {
            request.Headers.Add("X-Forge-Scan-Session", selected.GetProperty("id").GetGuid().ToString("D"));
            request.Headers.Add("X-Forge-Scan-Version", selected.GetProperty("version").GetInt32().ToString(CultureInfo.InvariantCulture));
        }
        return await client.SendAsync(request);
    }

    public static object Start(Flow flow, bool workerB = false) => new
        { badge = workerB ? MichaelBadge : SarahBadge, orderNumber = workerB ? flow.OrderB : flow.OrderA, sequence = 10, machineCode = workerB ? "SLITTER-B" : "SLITTER-A" };
    public static object Report(Flow flow, decimal good = 1m, bool complete = false) => new
        { badge = SarahBadge, orderNumber = flow.OrderA, sequence = 10, good, scrap = 0m, complete };

    public static async Task<JsonElement> ReadAsync(HttpClient client, Guid id) =>
        await JsonAsync(await client.GetAsync($"/api/identification/scan-sessions/{id:D}"));

    public static async Task<JsonElement> CreateSessionAsync(Flow flow, bool workerB = false, HttpClient? client = null, Guid? priorOverride = null, bool useOverride = false)
    {
        client ??= flow.Terminal;
        var pointer = await JsonAsync(await client.GetAsync("/api/identification/scan-sessions/current"));
        var previous = useOverride ? priorOverride : pointer.GetProperty("id").ValueKind == JsonValueKind.Null ? (Guid?)null : pointer.GetProperty("id").GetGuid();
        var result = await JsonAsync(await SendAsync(client, "/api/identification/scan-sessions", new
        {
            operationCode = Operation(workerB ? flow.OrderB : flow.OrderA), badge = workerB ? MichaelBadge : SarahBadge,
            machineCode = workerB ? "SLITTER-B" : "SLITTER-A", previousSessionId = previous, reason = "Stage this actual verified worker and selected operation"
        }, "session-create-" + Guid.NewGuid().ToString("N")));
        return await ReadAsync(client, Guid.Parse(result.GetProperty("entityId").GetString()!));
    }

    public static async Task<JsonElement> StageAsync(Flow flow, JsonElement session, string? code = null,
        string mode = "KeyboardWedge", HttpClient? client = null)
    {
        client ??= flow.Terminal;
        var id = session.GetProperty("id").GetGuid();
        await JsonAsync(await SendAsync(client, $"/api/identification/scan-sessions/{id:D}/stages", new
        {
            expectedVersion = session.GetProperty("version").GetInt32(), code = code ?? session.GetProperty("operationCode").GetString(),
            inputMode = mode, adapterId = mode == "ConfiguredAdapter" ? AdapterId : null, symbology = mode == "ConfiguredAdapter" ? "QR" : null,
            reason = "Review and record the exact current source reference"
        }, "session-stage-" + Guid.NewGuid().ToString("N")));
        return await ReadAsync(client, id);
    }

    public static async Task RejectedAsync(HttpResponseMessage response, string code, HttpStatusCode status = HttpStatusCode.UnprocessableEntity)
    {
        var error = await JsonAsync(response, status);
        if (code == "idempotency_conflict") Assert.Equal("Idempotency key conflict", error.GetProperty("title").GetString());
        else Assert.Equal(code, error.GetProperty("code").GetString());
    }

    public static async Task<string[]> RowsAsync(Flow flow, params string[] tables)
    {
        var rows = new List<string>();
        await using var connection = new NpgsqlConnection(flow.App.ConnectionString); await connection.OpenAsync();
        foreach (var table in tables)
        {
            Assert.Contains(table, BusinessTables.Concat(EvidenceTables)); // only fixed, test-owned identifiers
            await using var command = new NpgsqlCommand($"SELECT COALESCE(jsonb_agg(to_jsonb(scan_acceptance_test_row) ORDER BY to_jsonb(scan_acceptance_test_row)::text)::text,'[]') FROM {table} scan_acceptance_test_row", connection);
            rows.Add(table + ":" + (string)(await command.ExecuteScalarAsync())!);
        }
        return rows.ToArray();
    }

    public static async Task ReleaseDocumentAsync(SlittingPlant.Plant plant, string number, string revision)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/engineering/document-files")
            { Content = new ByteArrayContent(Encoding.UTF8.GetBytes($"Actual governed source {number} revision {revision}; no physical occurrence inferred.")) };
        request.Headers.Add("Idempotency-Key", "scan-doc-file-" + Guid.NewGuid().ToString("N")); request.Headers.Add("X-File-Name", "scan-instruction.txt");
        var uploaded = await JsonAsync(await plant.Engineer.SendAsync(request));
        await plant.Ok(plant.Engineer, $"/api/engineering/documents/{number}/revisions", new { revision, fileSha256 = uploaded.GetProperty("entityId").GetString(), fileName = "scan-instruction.txt" });
        await plant.Ok(plant.Engineer, $"/api/engineering/documents/{number}/revisions/{revision}/submit", new { });
        await plant.Ok(plant.Approver, $"/api/engineering/documents/{number}/revisions/{revision}/approve", new { });
        await plant.Ok(plant.Approver, $"/api/engineering/documents/{number}/revisions/{revision}/release", new { reason = "Independent release of this exact controlled source" });
    }
}
