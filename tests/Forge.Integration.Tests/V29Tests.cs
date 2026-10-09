using System.Globalization;
using System.Net;
using System.Text.Json;
using Forge.Kernel.Postgres;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Integration.Tests;

/// <summary>
/// V2.9 through the real API: maintenance work types, what a work order needs (asset, parts with positions,
/// procedure, qualifications), readiness dimension by dimension with MRO alternates (scenario 4), missing parts
/// requested from purchasing (scenario 5), and outages proposed by one person and approved by another after
/// the schedule preview (scenario 8). Work that is not ready is never planned as capacity loss.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class V29Tests(PostgresFixture postgres)
{
    private static string Day(int fromToday) => DateTime.UtcNow.AddDays(fromToday).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    private static async Task<(HttpStatusCode Status, string Body)> Try(SlittingPlant.Plant plant, HttpClient client, string url, object body)
    {
        var response = await plant.Try(client, url, body);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task Rejected(SlittingPlant.Plant plant, HttpClient client, string url, object body, string code, HttpStatusCode status = HttpStatusCode.UnprocessableEntity)
    {
        var (actual, text) = await Try(plant, client, url, body);
        Assert.True(actual == status, $"{url} -> {(int)actual}: {text}");
        Assert.Contains(code, text, StringComparison.Ordinal);
    }

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static async Task BoughtAsync(SlittingPlant.Plant plant, string part)
    {
        await plant.Ok(plant.Engineer, "/api/engineering/items", new { partNumber = part, description = part, unitOfMeasure = "EA", makeBuy = "Buy", leadTimeDays = 5 });
        await plant.Ok(plant.Engineer, $"/api/engineering/items/{part}/revisions", new { revision = "A" });
        await plant.Ok(plant.Approver, $"/api/engineering/items/{part}/revisions/A/release", new { reason = "Baseline" });
    }

    private static async Task<JsonElement> Readiness(HttpClient client, long workOrder) => await Get(client, $"/api/maintenance/work-orders/{workOrder}/readiness");

    private static string Outcome(JsonElement readiness, string dimension) =>
        readiness.GetProperty("dimensions").EnumerateArray().Single(d => d.GetProperty("name").GetString() == dimension).GetProperty("outcome").GetString()!;

    private static async Task<JsonElement> Order(HttpClient client, long workOrder) =>
        (await Get(client, "/api/maintenance/work-orders?includeClosed=true")).EnumerateArray().Select(v => v.GetProperty("order")).Single(o => o.GetProperty("workOrderId").GetInt64() == workOrder);

    /// <summary>Background services are off in tests: relay the outbox to the event log, then feed the subscribers.</summary>
    private static async Task PropagateAsync(ForgeApp app)
    {
        var relay = app.Services.GetRequiredService<OutboxRelay>();
        while (await relay.RelayBatchAsync(100, CancellationToken.None) > 0)
        {
        }

        await app.Services.GetRequiredService<SubscriptionPump>().PumpOnceAsync(1_000_000, CancellationToken.None);
    }

    [Fact]
    public async Task Scenarios_4_5_and_8_work_is_planned_only_when_ready_and_its_outage_is_approved_by_someone_else_after_the_preview()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("mp", "MaintenancePlanner");
        await plant.Person("mp2", "MaintenancePlanner");
        await plant.Person("tech", "MaintenanceTechnician");
        await plant.Person("bob", "Buyer");
        var (mp, mp2, tech, bob, stores, eng) = (plant.As("mp"), plant.As("mp2"), plant.As("tech"), plant.As("bob"), plant.As("stores"), plant.Engineer);
        await BoughtAsync(plant, "BRG-6205");
        await BoughtAsync(plant, "BRG-6205-Z");
        await plant.Ok(eng, "/api/competency/qualification-types", new { code = "ELEC-II", name = "Electrical maintenance II", kind = "Machine", validityDays = 365 });
        await plant.Ok(mp, "/api/maintenance/assets", new { assetCode = "SL-02", name = "Slitter 2", type = "Production", machineCode = "SLITTER-B", criticality = "Critical" });
        await plant.Ok(mp, "/api/maintenance/assets/SL-02/status", new { status = "Active", reason = "Site acceptance test passed" });

        // Work types: preventive work comes from plans; every work order is on a machine or an asset.
        var url = "/api/maintenance/work-orders";
        await Rejected(plant, mp, url, new { assetCode = "SL-02", title = "x", dueDate = Day(3), kind = "Preventive" }, "preventive_from_plan");
        await Rejected(plant, mp, url, new { title = "x", dueDate = Day(3), kind = "Inspection" }, "target_required");
        await Rejected(plant, mp, url, new { machineCode = "SLITTER-A", assetCode = "SL-02", title = "x", dueDate = Day(3), kind = "Inspection" }, "asset_machine_mismatch");
        var wo = Id(await plant.Ok(mp, url, new { assetCode = "SL-02", title = "Replace rewind motor bearings", dueDate = Day(5), kind = "Predictive" }));
        var order = await Order(mp, wo);
        Assert.Equal(("SLITTER-B", "SL-02", "Predictive"), (order.GetProperty("machineCode").GetString(), order.GetProperty("assetCode").GetString(), order.GetProperty("kind").GetString()));

        // What the work needs: validated, and planned only by maintenance planning.
        var requirements = $"{url}/{wo}/requirements";
        object Needs(object[] parts, string[]? qualifications = null, string? procedure = null) => new { parts, qualifications = qualifications ?? ["ELEC-II"], procedureDocument = procedure };
        var bearing = new { partNumber = "BRG-6205", quantity = 2m, position = "Rewind motor bearings" };
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, bob, requirements, Needs([bearing]))).Status);
        await Rejected(plant, mp, requirements, Needs([bearing], ["NOPE"]), "unknown_qualification");
        await Rejected(plant, mp, requirements, Needs([new { partNumber = "NOPE", quantity = 1m, position = (string?)null }]), "unknown_part");
        await Rejected(plant, mp, requirements, Needs([bearing, bearing]), "duplicate_part");
        await Rejected(plant, mp, requirements, Needs([bearing], procedure: "SOP-404"), "document_not_found", HttpStatusCode.NotFound);
        await plant.Ok(mp, requirements, Needs([bearing]));
        Assert.Equal("Rewind motor bearings", Assert.Single((await Get(tech, requirements)).EnumerateArray()).GetProperty("position").GetString());

        // Scenario 4: nothing on hand and nobody qualified — blocked on each, and not planned as capacity loss.
        var ready = await Readiness(tech, wo);
        Assert.Equal(("Blocked", "Material", "Pass", "Fail", "Fail"), (ready.GetProperty("status").GetString(), ready.GetProperty("blocking").GetString(), Outcome(ready, "Asset"), Outcome(ready, "Material"), Outcome(ready, "Technician")));
        var window = new { startsAt = DateTimeOffset.UtcNow.AddDays(1), endsAt = DateTimeOffset.UtcNow.AddDays(1).AddHours(3), reason = "Next planned stop" };
        await Rejected(plant, mp, $"{url}/{wo}/schedule", window, "not_ready");
        Assert.Empty((await Get(mp, "/api/maintenance/outages")).EnumerateArray());

        // Scenario 5: what is missing goes to purchasing, sourced to the work order, once.
        var asked = await plant.Ok(mp, $"{url}/{wo}/request-parts", new { reason = "Bearings for predictive replacement" });
        var line = Assert.Single(asked.GetProperty("lines").EnumerateArray());
        Assert.Equal(("BRG-6205", 2m, "Requested"), (line.GetProperty("partNumber").GetString(), line.GetProperty("quantity").GetDecimal(), line.GetProperty("outcome").GetString()));
        var requisition = (await Get(bob, "/api/procurement/requisitions")).EnumerateArray().Single();
        Assert.Equal(("Maintenance", $"work-order:{wo}"), (requisition.GetProperty("sourceKind").GetString(), requisition.GetProperty("sourceKey").GetString()));
        Assert.NotEqual("Requested", Assert.Single((await plant.Ok(mp, $"{url}/{wo}/request-parts", new { reason = "Again" })).GetProperty("lines").EnumerateArray()).GetProperty("outcome").GetString());

        // A proposed maintenance-only alternate never counts; approved, it covers the part for this asset.
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "BRG-6205-Z", quantity = 4m, locationCode = "STORES", reason = "Spares from the old slitter" });
        var alternate = Id(await plant.Ok(bob, "/api/engineering/alternates", new { primaryPart = "BRG-6205", alternatePart = "BRG-6205-Z", kind = "MaintenanceOnly", factor = 1m, qualityNotes = "Check seal type" }));
        Assert.Equal("Fail", Outcome(await Readiness(tech, wo), "Material"));
        await plant.Ok(mp, $"/api/engineering/alternates/{alternate}/approve", new { reason = "Same envelope and rating" });
        ready = await Readiness(tech, wo);
        var part = Assert.Single(ready.GetProperty("parts").EnumerateArray());
        Assert.Equal((true, "BRG-6205-Z", alternate), (part.GetProperty("covered").GetBoolean(), part.GetProperty("alternate").GetString(), part.GetProperty("alternateId").GetInt64()));
        Assert.Equal(("Pass", "Technician"), (Outcome(ready, "Material"), ready.GetProperty("blocking").GetString()));

        // A qualified, present technician: ready.
        await plant.Worker("sparky", "SLIT-WC", "ELEC-II");
        ready = await Readiness(tech, wo);
        Assert.Equal("Ready", ready.GetProperty("status").GetString());
        Assert.Contains("Sparky", ready.GetProperty("technicians").EnumerateArray().Select(t => t.GetString()!));
        Assert.Contains((await Get(mp, "/api/maintenance/readiness")).EnumerateArray(), r => r.GetProperty("workOrderId").GetInt64() == wo && r.GetProperty("status").GetString() == "Ready");

        // Scenario 8: scheduling on a running machine proposes an outage; it is capacity loss only once someone else approves it.
        await plant.Ok(mp, $"{url}/{wo}/schedule", window);
        var outage = Assert.Single((await Get(mp, "/api/maintenance/outages")).EnumerateArray());
        var outageId = outage.GetProperty("outageId").GetInt64();
        Assert.Equal(("SLITTER-B", wo, "Proposed"), (outage.GetProperty("machineCode").GetString(), outage.GetProperty("workOrderId").GetInt64(), outage.GetProperty("status").GetString()));
        Assert.Equal("Open", (await Order(mp, wo)).GetProperty("status").GetString());
        await Rejected(plant, mp, $"{url}/{wo}/schedule", window, "outage_pending");
        var view = await Get(mp2, $"/api/maintenance/outages/{outageId}");
        var preview = view.GetProperty("preview");
        Assert.Equal("SLITTER-B", preview.GetProperty("machineCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(preview.GetProperty("summary").GetString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, tech, $"/api/maintenance/outages/{outageId}/approve", new { reason = "x" })).Status);
        await Rejected(plant, mp, $"/api/maintenance/outages/{outageId}/approve", new { reason = "x" }, "separation_of_duties");
        await Rejected(plant, mp2, $"/api/maintenance/outages/{outageId}/approve", new { }, "reason_required");
        await plant.Ok(mp2, $"/api/maintenance/outages/{outageId}/approve", new { reason = "Night shift has spare capacity on SLITTER-A" });
        var decided = (await Get(mp, "/api/maintenance/outages?includeDecided=true")).EnumerateArray().Single(o => o.GetProperty("outageId").GetInt64() == outageId);
        Assert.Equal(("Approved", preview.GetProperty("summary").GetString()), (decided.GetProperty("status").GetString(), decided.GetProperty("impact").GetString()));
        Assert.Equal(JsonValueKind.Number, decided.GetProperty("downtimeId").ValueKind);
        Assert.Equal("Scheduled", (await Order(mp, wo)).GetProperty("status").GetString());
        await Rejected(plant, mp2, $"/api/maintenance/outages/{outageId}/reject", new { reason = "x" }, "outage_decided");

        // The alternate goes in at the required position: the asset records what was installed, and in place of what.
        await plant.Ok(tech, $"{url}/{wo}/parts", new { partNumber = "BRG-6205-Z", quantity = 2m, locationCode = "STORES", alternateFor = "BRG-6205" });
        await PropagateAsync(app);
        var installed = (await Get(tech, "/api/maintenance/assets/SL-02")).GetProperty("components").EnumerateArray().Single(c => c.GetProperty("partNumber").GetString() == "BRG-6205-Z");
        Assert.Equal(("Rewind motor bearings", 2m, wo), (installed.GetProperty("position").GetString(), installed.GetProperty("quantity").GetDecimal(), installed.GetProperty("installedByWorkOrder").GetInt64()));
        Assert.Contains("in place of BRG-6205", (await Get(tech, "/api/maintenance/assets/SL-02")).GetProperty("history").EnumerateArray().Last().GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.True(Assert.Single((await Readiness(tech, wo)).GetProperty("parts").EnumerateArray()).GetProperty("covered").GetBoolean());

        // Started work keeps what it needed.
        await plant.Ok(tech, $"{url}/{wo}/start", new { });
        await Rejected(plant, mp, requirements, Needs([bearing]), "work_started");
    }

    [Fact]
    public async Task Outages_are_withdrawn_by_their_proposer_or_rejected_and_work_off_the_production_floor_needs_none()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("mp", "MaintenancePlanner");
        await plant.Person("mp2", "MaintenancePlanner");
        await plant.Person("tech", "MaintenanceTechnician");
        var (mp, mp2, tech) = (plant.As("mp"), plant.As("mp2"), plant.As("tech"));
        var url = "/api/maintenance/work-orders";
        object Window(int days) => new { startsAt = DateTimeOffset.UtcNow.AddDays(days), endsAt = DateTimeOffset.UtcNow.AddDays(days).AddHours(2), reason = "Planned stop" };

        // Calibration on SLITTER-A: a proposal, withdrawn by its proposer only.
        var cal = Id(await plant.Ok(mp, url, new { machineCode = "SLITTER-A", title = "Calibrate tension load cells", dueDate = Day(10), kind = "Calibration" }));
        await Rejected(plant, mp, $"{url}/{cal}/schedule", new { startsAt = DateTimeOffset.UtcNow.AddDays(-2), endsAt = DateTimeOffset.UtcNow.AddDays(-1), reason = "x" }, "invalid_window");
        await plant.Ok(mp, $"{url}/{cal}/schedule", Window(2));
        var first = Assert.Single((await Get(mp, "/api/maintenance/outages")).EnumerateArray()).GetProperty("outageId").GetInt64();
        await Rejected(plant, tech, $"/api/maintenance/outages/{first}/withdraw", new { reason = "x" }, "not_yours");
        await Rejected(plant, mp, $"/api/maintenance/outages/{first}/withdraw", new { }, "reason_required");
        await plant.Ok(mp, $"/api/maintenance/outages/{first}/withdraw", new { reason = "Load cells arrive later" });

        // Proposed again, rejected by someone else with a reason; the work stays open and can be proposed again.
        await plant.Ok(mp, $"{url}/{cal}/schedule", Window(3));
        var second = Assert.Single((await Get(mp, "/api/maintenance/outages")).EnumerateArray()).GetProperty("outageId").GetInt64();
        await plant.Ok(mp2, $"/api/maintenance/outages/{second}/reject", new { reason = "Customer run that day" });
        var all = (await Get(mp, "/api/maintenance/outages?includeDecided=true")).EnumerateArray().ToDictionary(o => o.GetProperty("outageId").GetInt64(), o => o.GetProperty("status").GetString());
        Assert.Equal(("Withdrawn", "Rejected"), (all[first], all[second]));
        Assert.Equal("Open", (await Order(mp, cal)).GetProperty("status").GetString());
        await Rejected(plant, mp2, $"/api/maintenance/outages/{second}/approve", new { reason = "Changed my mind" }, "outage_decided");
        await plant.Ok(mp, $"{url}/{cal}/schedule", Window(4));

        // Cancelling work withdraws its pending outage.
        await plant.Ok(mp, $"{url}/{cal}/cancel", new { reason = "Calibration contractor will do it" });
        Assert.Empty((await Get(mp, "/api/maintenance/outages")).EnumerateArray());

        // Facility work on a building takes no production capacity: scheduled directly, no outage.
        await plant.Ok(mp, "/api/maintenance/assets", new { assetCode = "BLDG-1", name = "Main building", type = "Building", criticality = "High" });
        await plant.Ok(mp, "/api/maintenance/assets/BLDG-1/status", new { status = "Active", reason = "In use" });
        var roof = Id(await plant.Ok(mp, url, new { assetCode = "BLDG-1", title = "Roof drain inspection", dueDate = Day(10), kind = "Facility" }));
        await plant.Ok(mp, $"{url}/{roof}/schedule", Window(2));
        Assert.Equal("Scheduled", (await Order(mp, roof)).GetProperty("status").GetString());
        Assert.Empty((await Get(mp, "/api/maintenance/outages")).EnumerateArray());

        // Planned maintenance on a machine is a proposal too, and the preview is available before the decision.
        await plant.Ok(mp, "/api/maintenance/machines/SLITTER-B/planned", new { startsAt = DateTimeOffset.UtcNow.AddDays(5), endsAt = DateTimeOffset.UtcNow.AddDays(5).AddHours(4), description = "Guard rework" });
        var planned = Assert.Single((await Get(mp, "/api/maintenance/outages")).EnumerateArray());
        Assert.Equal(JsonValueKind.Null, planned.GetProperty("workOrderId").ValueKind);
        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(5).ToString("o", CultureInfo.InvariantCulture));
        var to = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(5).AddHours(4).ToString("o", CultureInfo.InvariantCulture));
        Assert.Equal("SLITTER-B", (await Get(tech, $"/api/maintenance/outages/preview?machine=slitter-b&from={from}&to={to}")).GetProperty("machineCode").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await tech.GetAsync(new Uri($"/api/maintenance/outages/preview?machine=NOPE&from={from}&to={to}", UriKind.Relative))).StatusCode);

        // Emergency work is raised for immediate action.
        var emergency = await plant.Ok(tech, url, new { machineCode = "SLITTER-A", title = "Hydraulic leak at the knife shaft", dueDate = Day(0), kind = "Emergency" });
        Assert.Contains("out of production at once", emergency.GetProperty("message").GetString(), StringComparison.Ordinal);
    }
}
