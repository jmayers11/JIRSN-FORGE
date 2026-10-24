using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Host.Identification;
using Forge.Kernel.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class V41MaterialIdentificationTests(PostgresFixture postgres)
{
    private const string Namespace = "V41.Material";
    private const string Endpoint = "/api/identification/resolve";
    private static string Material(string part, string lot) => $"forge:v1:{Namespace}:material:{CanonicalIdentification.EncodeKey(part)}:{CanonicalIdentification.EncodeKey(lot)}";
    private static string Location(string location) => $"forge:v1:{Namespace}:location:{CanonicalIdentification.EncodeKey(location)}";
    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static async Task<JsonElement> ResolveAsync(HttpClient client, string code)
    {
        using var response = await client.PostAsJsonAsync(Endpoint, new { code });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static async Task<(int Status, string? Title, string? Detail, string? Code)> FailureAsync(HttpClient client, string code, HttpStatusCode status = HttpStatusCode.NotFound, string outcome = "identification_not_found")
    {
        using var response = await client.PostAsJsonAsync(Endpoint, new { code });
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(status, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        Assert.Equal(outcome, document.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain(code, body, StringComparison.Ordinal);
        Assert.DoesNotContain("IDENT-", body, StringComparison.Ordinal);
        return (document.RootElement.GetProperty("status").GetInt32(), document.RootElement.GetProperty("title").GetString(),
            document.RootElement.GetProperty("detail").GetString(), document.RootElement.GetProperty("code").GetString());
    }

    private static async Task<string[]> StateAsync(ForgeApp app, bool sourceOnly = false)
    {
        string[] sourceTables = ["inventory.lot", "inventory.location", "inventory.balance", "inventory.transaction", "inventory.material_task"];
        string[] auditTables = ["kernel.audit_log", "kernel.event_log", "kernel.outbox", "kernel.idempotency"];
        var result = new List<string>();
        await using var connection = await app.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        foreach (var table in sourceOnly ? sourceTables : sourceTables.Concat(auditTables))
        {
            await using var command = new NpgsqlCommand($"SELECT COALESCE(jsonb_agg(to_jsonb(material_snapshot_row) ORDER BY to_jsonb(material_snapshot_row)::text)::text,'[]') FROM {table} material_snapshot_row", connection);
            result.Add(table + ":" + (string)(await command.ExecuteScalarAsync())!);
        }
        return result.ToArray();
    }

    private static async Task<SlittingPlant.Plant> StockAsync(ForgeApp app)
    {
        var plant = await SlittingPlant.BuildAsync(app);
        await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["approver"]}/roles", new { role = "EngineeringApprover", reason = "Independent engineering release authority for the real fixture revisions" });
        await plant.Person("id-stores", "InventoryController");
        await plant.Person("id-handler", "MaterialHandler");
        var stores = plant.As("id-stores");
        foreach (var part in new[] { "IDENT-A", "IDENT-B" })
        {
            await plant.Ok(plant.Engineer, "/api/engineering/items", new { partNumber = part, description = part, unitOfMeasure = "M", makeBuy = "Buy", lotTracked = true });
            await plant.Ok(plant.Engineer, $"/api/engineering/items/{part}/revisions", new { revision = "A" });
            await plant.Ok(plant.Approver, $"/api/engineering/items/{part}/revisions/A/release", new { reason = "Released scan subject configuration" });
        }
        await plant.Ok(plant.Engineer, "/api/resources/sites", new { code = "PLANT2", name = "Another facility", timeZone = TestSite.TimeZone });
        foreach (var (code, site) in new[] { ("IDENT-WH", "PLANT1"), ("IDENT-OTHER", "PLANT2") })
            await plant.Ok(stores, "/api/inventory/locations", new { code, siteCode = site, name = code, kind = "Stores" });
        await plant.Ok(stores, "/api/inventory/locations", new { code = "IDENT-SIDE", siteCode = "PLANT1", name = "Machine side", kind = "MachineSide", machineCode = "SLITTER-A" });
        foreach (var (part, kind) in new[] { ("IDENT-A", "Roll"), ("IDENT-B", "Batch") })
            await plant.Ok(plant.As("id-handler"), "/api/inventory/receipts", new { partNumber = part, quantity = 20m, locationCode = "IDENT-WH", lotNumber = "SAME/LOT", supplierLot = "PRIVATE-SUPPLIER-REF", revision = "A", identityKind = kind, reason = "Recorded physical identity" });
        return plant;
    }

    [Fact]
    public async Task Material_keys_are_composite_and_current_location_source_scope_is_authoritative_without_read_side_writes()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), configureBuilder: builder => builder.UseSetting(CanonicalIdentification.NamespaceConfigurationKey, Namespace));
        var plant = await StockAsync(app);
        const string role = "IdentificationFacilityReader";
        await plant.Ok(plant.Admin, "/api/kernel/roles", new { role, displayName = "Actual facility inventory read", permissions = new[] { Permissions.InventoryRead }, reason = "Configure source read authority" });
        await plant.Ok(plant.Admin, $"/api/kernel/roles/{role}/permission-scopes", new { permission = Permissions.InventoryRead, scopeType = "facility", scopeId = "PLANT1", reason = "Only recorded source facility" });
        await plant.Person("id-facility-reader", role);
        const string workCenterRole = "IdentificationWorkCenterReader";
        await plant.Ok(plant.Admin, "/api/kernel/roles", new { role = workCenterRole, displayName = "Actual work-center inventory read", permissions = new[] { Permissions.InventoryRead }, reason = "Configure source work-center read authority" });
        await plant.Ok(plant.Admin, $"/api/kernel/roles/{workCenterRole}/permission-scopes", new { permission = Permissions.InventoryRead, scopeType = "work_center", scopeId = "SLIT-WC", reason = "Only the owning machine work center" });
        await plant.Person("id-work-center-reader", workCenterRole);
        await plant.Person("id-no-read", "Auditor");
        var reader = plant.As("id-handler");
        var scoped = plant.As("id-facility-reader");
        var before = await StateAsync(app);
        using (var metadataResponse = await plant.As("id-no-read").GetAsync("/api/identification/config"))
        {
            Assert.Equal(HttpStatusCode.OK, metadataResponse.StatusCode);
            using var metadata = JsonDocument.Parse(await metadataResponse.Content.ReadAsStringAsync());
            Assert.Equal(Namespace, metadata.RootElement.GetProperty("namespace").GetString());
            foreach (var retainedKind in new[] { "order", "operation", "material", "location" })
                Assert.Contains(retainedKind, metadata.RootElement.GetProperty("supportedKinds").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(1024, metadata.RootElement.GetProperty("maxCodeLength").GetInt32());
            Assert.Equal(40, metadata.RootElement.GetProperty("maxKeyLength").GetInt32());
        }
        foreach (var (part, kind) in new[] { ("IDENT-A", "Roll"), ("IDENT-B", "Batch") })
        {
            var identity = await ResolveAsync(reader, Material(part, "SAME/LOT"));
            Assert.Equal((part, "SAME/LOT", kind, "A", "Recorded"), (identity.GetProperty("partNumber").GetString(), identity.GetProperty("lotNumber").GetString(), identity.GetProperty("identityKind").GetString(), identity.GetProperty("revision").GetString(), identity.GetProperty("status").GetString()));
            Assert.Equal(JsonValueKind.Null, identity.GetProperty("orderNumber").ValueKind);
            Assert.Equal(JsonValueKind.Null, identity.GetProperty("locationCode").ValueKind);
            Assert.DoesNotContain("PRIVATE-SUPPLIER-REF", identity.GetRawText(), StringComparison.Ordinal);
            Assert.False(identity.TryGetProperty("quantity", out _));
        }
        Assert.Equal("IDENT-WH", (await ResolveAsync(scoped, Location("IDENT-WH"))).GetProperty("locationCode").GetString());
        Assert.Equal("IDENT-SIDE", (await ResolveAsync(scoped, Location("IDENT-SIDE"))).GetProperty("locationCode").GetString());
        Assert.Equal("IDENT-SIDE", (await ResolveAsync(plant.As("id-work-center-reader"), Location("IDENT-SIDE"))).GetProperty("locationCode").GetString());
        var concealed = await FailureAsync(reader, Material("MISSING", "SAME/LOT"));
        Assert.Equal(concealed, await FailureAsync(plant.As("id-no-read"), Material("IDENT-A", "SAME/LOT")));
        Assert.Equal(concealed, await FailureAsync(scoped, Location("IDENT-OTHER")));
        Assert.Equal(concealed, await FailureAsync(plant.As("id-work-center-reader"), Location("IDENT-WH")));
        Assert.Equal(concealed, await FailureAsync(scoped, Material("IDENT-A", "SAME/LOT"))); // Facility scope cannot invent a location on the owner identity.
        await FailureAsync(reader, $"forge:v1:{Namespace}:material:SAME%2FLOT", HttpStatusCode.BadRequest, "invalid_identification");
        Assert.Equal(before, await StateAsync(app));

        await plant.Ok(plant.Admin, $"/api/kernel/roles/{role}/permissions/{Permissions.InventoryRead}/revoke", new { reason = "Withdraw source read authority" });
        before = await StateAsync(app);
        Assert.Equal(concealed, await FailureAsync(scoped, Location("IDENT-WH")));
        Assert.Equal(before, await StateAsync(app));
        await plant.Ok(plant.Admin, $"/api/kernel/roles/{role}/permission-scopes", new { permission = Permissions.InventoryRead, scopeType = "facility", scopeId = "PLANT1", reason = "Restore actual source authority" });
        before = await StateAsync(app);
        Assert.Equal("Active", (await ResolveAsync(scoped, Location("IDENT-WH"))).GetProperty("status").GetString());
        Assert.Equal(before, await StateAsync(app));
    }

    [Fact]
    public async Task Typed_pick_checks_real_task_part_and_lot_and_replay_keeps_current_program_authority()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), configureBuilder: builder => builder.UseSetting(CanonicalIdentification.NamespaceConfigurationKey, Namespace));
        var plant = await StockAsync(app);
        await plant.Person("id-planner", "Planner");
        await plant.Person("id-programs", "ProgramManager");
        var program = Guid.Parse((await plant.Ok(plant.As("id-programs"), "/api/programs/", new { code = "IDENT-PRIVATE", name = "Private material task" })).GetProperty("entityId").GetString()!);
        foreach (var subject in new[] { "eng", "id-planner", "id-handler" })
            await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People[subject]}/programs", new { programId = program, reason = "Actual task source membership" });
        await plant.Ok(plant.Engineer, "/api/engineering/items", new { partNumber = "IDENT-MAKE", description = "Private manufacturing source", unitOfMeasure = "EA", makeBuy = "Make" });
        await plant.Ok(plant.Engineer, "/api/engineering/items/IDENT-MAKE/revisions", new { revision = "A" });
        await plant.Ok(plant.Engineer, "/api/engineering/items/IDENT-MAKE/revisions/A/bom", new { lineNumber = 1, componentPartNumber = "IDENT-A", quantityPer = 1m, scrapPercent = 0m });
        await plant.Ok(plant.Engineer, "/api/engineering/items/IDENT-MAKE/revisions/A/routing", new { sequence = 10, description = "Slit", workCenterCode = "SLIT-WC", activityCode = "RUN_SLITTER", eligibleMachines = new[] { "SLITTER-A" }, setupMinutes = 0m, runMinutesPerUnit = 0.01m, crewSize = 1 });
        await plant.Ok(plant.Approver, "/api/engineering/items/IDENT-MAKE/revisions/A/release", new { reason = "Approved typed task configuration" });
        var order = Id(await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = "IDENT-MAKE", quantity = 10m, dueDate = DateTime.UtcNow.AddDays(10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), priority = 3, programId = program }));
        await plant.Ok(plant.As("id-planner"), $"/api/orders/{order}/release", new { reason = "Release actual private task source" });
        var handler = plant.As("id-handler");
        var task = Id(await plant.Ok(handler, "/api/inventory/tasks", new { kind = "Stage", orderNumber = order, partNumber = "IDENT-A", quantity = 10m, toLocation = "IDENT-SIDE", fromLocation = "IDENT-WH", lotNumber = "SAME/LOT" }));
        var pickUrl = $"/api/inventory/tasks/{task}/pick";
        var before = await StateAsync(app, true);
        foreach (var (part, lot) in new[] { ("IDENT-B", "SAME/LOT"), ("IDENT-A", "WRONG"), ("ident-a", "SAME/LOT") })
        {
            using var denied = await plant.Try(handler, pickUrl, new { scannedLocation = "IDENT-WH", scannedLot = "SAME/LOT", scannedMaterial = new { partNumber = part, lotNumber = lot } });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, denied.StatusCode);
            Assert.Contains("scan_mismatch", await denied.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        Assert.Equal(before, await StateAsync(app, true));
        var body = new { scannedLocation = "IDENT-WH", scannedLot = "SAME/LOT", quantity = 8m, scannedMaterial = new { partNumber = "IDENT-A", lotNumber = "SAME/LOT" } };
        // A successful identity preview cannot turn current held stock into released stock.
        await plant.Ok(handler, "/api/inventory/holds", new { partNumber = "IDENT-A", quantity = 20m, locationCode = "IDENT-WH", lotNumber = "SAME/LOT", reason = "Hold after the scan preview" });
        before = await StateAsync(app);
        Assert.Equal("Recorded", (await ResolveAsync(handler, Material("IDENT-A", "SAME/LOT"))).GetProperty("status").GetString());
        Assert.Equal(before, await StateAsync(app));
        before = await StateAsync(app, true);
        using (var held = await plant.Try(handler, pickUrl, body))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, held.StatusCode);
            Assert.Contains("insufficient_stock", await held.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        Assert.Equal(before, await StateAsync(app, true));
        await plant.Person("id-independent-release", "InventoryController");
        await plant.Ok(plant.As("id-independent-release"), "/api/inventory/releases", new { partNumber = "IDENT-A", quantity = 20m, locationCode = "IDENT-WH", lotNumber = "SAME/LOT", status = "Hold", reason = "Independent release of the actual stock" });
        const string key = "typed-material-pick-once";
        using var accepted = await handler.CommandAsync(pickUrl, body, key);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var acceptedBody = await accepted.Content.ReadAsStringAsync();
        var pickedState = await StateAsync(app, true);
        using (var replay = await handler.CommandAsync(pickUrl, body, key))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.Equal(acceptedBody, await replay.Content.ReadAsStringAsync());
        }
        using (var conflict = await handler.CommandAsync(pickUrl, new { scannedLocation = "IDENT-WH", scannedLot = "SAME/LOT", quantity = 8m, scannedMaterial = new { partNumber = "IDENT-B", lotNumber = "SAME/LOT" } }, key))
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(pickedState, await StateAsync(app, true));
        await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["id-handler"]}/programs/{program}/revoke", new { reason = "Withdraw current private task source access" });
        using (var revoked = await handler.CommandAsync(pickUrl, body, key))
            Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        Assert.Equal(pickedState, await StateAsync(app, true));
        await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["id-handler"]}/programs", new { programId = program, reason = "Restore independently approved task membership" });
        using (var restored = await handler.CommandAsync(pickUrl, body, key))
        {
            Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
            Assert.Equal(acceptedBody, await restored.Content.ReadAsStringAsync());
        }
        await plant.EnsureCustodyAsync();
        await plant.Ok(handler, $"/api/inventory/tasks/{task}/deliver", new { scannedLocation = "IDENT-SIDE" });
        await using var connection = await app.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        await using var transaction = new NpgsqlCommand("SELECT count(*), sum(quantity) FROM inventory.transaction WHERE order_number=$1 AND kind='Transfer'", connection);
        transaction.Parameters.AddWithValue(order);
        await using (var transactions = await transaction.ExecuteReaderAsync())
        {
            Assert.True(await transactions.ReadAsync());
            Assert.Equal(1L, transactions.GetInt64(0));
            Assert.Equal(8m, transactions.GetDecimal(1));
        }
        await using var audit = new NpgsqlCommand("SELECT count(*) FILTER (WHERE outcome='Succeeded'), count(*) FILTER (WHERE outcome='Denied' AND program_id=$2) FROM kernel.audit_log WHERE entity_type='MaterialTask' AND entity_id=$1 AND action='PickMaterial'", connection);
        audit.Parameters.AddWithValue(task.ToString(CultureInfo.InvariantCulture));
        audit.Parameters.AddWithValue(program);
        await using var audits = await audit.ExecuteReaderAsync();
        Assert.True(await audits.ReadAsync());
        Assert.Equal(1L, audits.GetInt64(0));
        Assert.Equal(1L, audits.GetInt64(1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid namespace")]
    public async Task Configuration_metadata_is_authenticated_and_fails_closed_without_a_valid_namespace(string? configuredNamespace)
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), configureBuilder: builder => builder.UseSetting(CanonicalIdentification.NamespaceConfigurationKey, configuredNamespace));
        var before = await StateAsync(app);
        using (var unauthenticated = await app.CreateClient().GetAsync("/api/identification/config"))
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        using (var unavailable = await app.As(ForgeApp.AdminSubject).GetAsync("/api/identification/config"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
            Assert.Contains("identification_not_configured", await unavailable.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        Assert.Equal(before, await StateAsync(app));
    }
}
