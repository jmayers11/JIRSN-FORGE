using System.Globalization;
using System.Net;
using System.Text.Json;
using Forge.Kernel.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Forge.Integration.Tests;

/// <summary>
/// V2.11 through the real API: serial numbers are assigned at production receipt and never reused; every issue
/// to an order writes the as-built record; genealogy of a shipped serial reads that record and the other
/// records as they were — after a new component revision, an expired alternate and a renamed supplier, it still
/// shows what was built (scenario 10).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class V211Tests(PostgresFixture postgres)
{
    private const string Sarah = "B-SARAH-0211";

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

    private static async Task ItemAsync(SlittingPlant.Plant plant, string part, string makeBuy = "Buy", bool lotTracked = false, bool serialized = false)
    {
        await plant.Ok(plant.Engineer, "/api/engineering/items", new { partNumber = part, description = part, unitOfMeasure = "EA", makeBuy, leadTimeDays = 5, lotTracked, serialized });
        await plant.Ok(plant.Engineer, $"/api/engineering/items/{part}/revisions", new { revision = "A" });
    }

    private static async Task ReleaseAsync(SlittingPlant.Plant plant, string part, string revision = "A") =>
        await plant.Ok(plant.Approver, $"/api/engineering/items/{part}/revisions/{revision}/release", new { reason = "Baseline" });

    private static async Task PropagateAsync(ForgeApp app)
    {
        var relay = app.Services.GetRequiredService<OutboxRelay>();
        while (await relay.RelayBatchAsync(100, CancellationToken.None) > 0)
        {
        }

        await app.Services.GetRequiredService<SubscriptionPump>().PumpOnceAsync(1_000_000, CancellationToken.None);
    }

    [Fact]
    public async Task Scenario_10_genealogy_of_a_shipped_serial_reads_the_as_built_record_not_current_data()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var (e, approver) = (plant.Engineer, plant.Approver);
        await plant.Person("bob", "Buyer");
        await plant.Person("pat", "Buyer", "PurchasingManager");
        await plant.Person("lead", "Buyer", "EngineeringApprover");
        await plant.Person("ship", "Shipper");
        await plant.Person("tc", "TrainingCoordinator");
        await plant.Person("kiosk", "ShopFloorTerminal");
        var (bob, pat, lead, stores, ship, kiosk) = (plant.As("bob"), plant.As("pat"), plant.As("lead"), plant.As("stores"), plant.As("ship"), plant.As("kiosk"));

        // A serialized controller built from a serialized board and a lot-tracked housing (or its approved equivalent).
        await ItemAsync(plant, "PCB-1", serialized: true);
        await ItemAsync(plant, "HOUS-1", lotTracked: true);
        await ItemAsync(plant, "HOUS-2", lotTracked: true);
        await ItemAsync(plant, "CTRL-UNIT", "Make", serialized: true);
        foreach (var part in new[] { "PCB-1", "HOUS-1", "HOUS-2" })
        {
            await ReleaseAsync(plant, part);
        }

        await plant.Ok(e, "/api/engineering/items/CTRL-UNIT/revisions/A/bom", new { lineNumber = 1, componentPartNumber = "PCB-1", quantityPer = 1m, scrapPercent = 0m });
        await plant.Ok(e, "/api/engineering/items/CTRL-UNIT/revisions/A/bom", new { lineNumber = 2, componentPartNumber = "HOUS-1", quantityPer = 1m, scrapPercent = 0m });
        await plant.Ok(e, "/api/engineering/items/CTRL-UNIT/revisions/A/routing", new { sequence = 10, description = "Assemble and test", workCenterCode = "SLIT-WC", activityCode = "RUN_SLITTER", eligibleMachines = new[] { "SLITTER-A" }, setupMinutes = 0m, runMinutesPerUnit = 1m, crewSize = 1 });
        await ReleaseAsync(plant, "CTRL-UNIT");

        // Bought: boards one serial at a time, housings by lot, on purchase orders from two suppliers.
        await plant.Ok(bob, "/api/procurement/suppliers", new { code = "ACME", name = "Acme Electronics" });
        await plant.Ok(bob, "/api/procurement/suppliers", new { code = "CASECO", name = "Case Company" });
        var poBoards = Id(await plant.Ok(bob, "/api/procurement/purchase-orders", new { supplier = "ACME", lines = new[] { new { partNumber = "PCB-1", quantity = 2m, promisedDate = Day(5) } } }));
        var poCases = Id(await plant.Ok(bob, "/api/procurement/purchase-orders", new { supplier = "CASECO", lines = new[] { new { partNumber = "HOUS-1", quantity = 1m, promisedDate = Day(5) }, new { partNumber = "HOUS-2", quantity = 5m, promisedDate = Day(5) } } }));
        await plant.Ok(pat, $"/api/procurement/purchase-orders/{poBoards}/approve", new { reason = "Build stock" });
        await plant.Ok(pat, $"/api/procurement/purchase-orders/{poCases}/approve", new { reason = "Build stock" });
        var receipts = "/api/inventory/receipts";
        await Rejected(plant, stores, receipts, new { partNumber = "PCB-1", quantity = 1m, locationCode = "STORES", poNumber = poBoards, poLine = 1 }, "serial_required");
        await Rejected(plant, stores, receipts, new { partNumber = "PCB-1", quantity = 2m, locationCode = "STORES", lotNumber = "SN-B1", poNumber = poBoards, poLine = 1 }, "serial_quantity");
        await plant.Ok(stores, receipts, new { partNumber = "PCB-1", quantity = 1m, locationCode = "STORES", lotNumber = "SN-B1", poNumber = poBoards, poLine = 1, revision = "A" });
        await Rejected(plant, stores, receipts, new { partNumber = "PCB-1", quantity = 1m, locationCode = "STORES", lotNumber = "SN-B1", poNumber = poBoards, poLine = 1 }, "serial_exists");
        await plant.Ok(stores, receipts, new { partNumber = "PCB-1", quantity = 1m, locationCode = "STORES", lotNumber = "SN-B2", poNumber = poBoards, poLine = 1, revision = "A" });
        await plant.Ok(stores, receipts, new { partNumber = "HOUS-1", quantity = 1m, locationCode = "STORES", lotNumber = "H1-7", poNumber = poCases, poLine = 1, revision = "A" });
        await plant.Ok(stores, receipts, new { partNumber = "HOUS-2", quantity = 5m, locationCode = "STORES", lotNumber = "H2-9", poNumber = poCases, poLine = 2, revision = "A" });

        // HOUS-2 is an approved equivalent of HOUS-1.
        var equivalent = Id(await plant.Ok(lead, "/api/engineering/alternates", new { primaryPart = "HOUS-1", alternatePart = "HOUS-2", kind = "Equivalent", factor = 1m, evidence = "Same casting, second source" }));
        await plant.Ok(approver, $"/api/engineering/alternates/{equivalent}/approve", new { reason = "First article FA-31 passed" });

        // Order for two units; components issued — the as-built record is written as they go.
        var order = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "CTRL-UNIT", quantity = 2m, dueDate = Day(10), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Customer order" });
        var issue = "/api/inventory/issues";
        await Rejected(plant, stores, issue, new { orderNumber = order, partNumber = "PCB-1", quantity = 2m, locationCode = "STORES", lotNumber = "SN-B1" }, "serial_quantity");
        await plant.Ok(stores, issue, new { orderNumber = order, partNumber = "PCB-1", quantity = 1m, locationCode = "STORES", lotNumber = "SN-B1" });
        await plant.Ok(stores, issue, new { orderNumber = order, partNumber = "PCB-1", quantity = 1m, locationCode = "STORES", lotNumber = "SN-B2" });
        await plant.Ok(stores, issue, new { orderNumber = order, partNumber = "HOUS-1", quantity = 1m, locationCode = "STORES", lotNumber = "H1-7" });
        await Rejected(plant, stores, issue, new { orderNumber = order, partNumber = "HOUS-2", quantity = 2m, locationCode = "STORES", lotNumber = "H2-9", alternateFor = "HOUS-1" }, "over_issue");
        await plant.Ok(stores, issue, new { orderNumber = order, partNumber = "HOUS-2", quantity = 1m, locationCode = "STORES", lotNumber = "H2-9", alternateFor = "HOUS-1" });
        await plant.Ok(stores, "/api/inventory/returns", new { orderNumber = order, partNumber = "HOUS-2", quantity = 1m, locationCode = "STORES", lotNumber = "H2-9", alternateFor = "HOUS-1", reason = "Scratched in handling" });
        await plant.Ok(stores, issue, new { orderNumber = order, partNumber = "HOUS-2", quantity = 1m, locationCode = "STORES", lotNumber = "H2-9", alternateFor = "HOUS-1" });

        // Built on the shop floor by a qualified operator.
        await plant.Ok(e, "/api/resources/shifts", new { code = "ALL", siteCode = "PLANT1", name = "Around the clock", start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 } });
        await plant.Ok(e, "/api/resources/work-centers/SLIT-WC/shifts", new { shiftCode = "ALL" });
        await plant.Ok(e, $"/api/workforce/workers/{plant.People["sarah"]}/shift", new { shiftCode = "ALL" });
        await plant.Ok(plant.Admin, $"/api/workforce/people/{plant.People["sarah"]}/badge", new { badge = Sarah });
        await plant.Ok(kiosk, "/api/shopfloor/clock-in", new { badge = Sarah });
        await plant.Ok(kiosk, "/api/shopfloor/start", new { badge = Sarah, orderNumber = order, sequence = 10, machineCode = "SLITTER-A" });
        await plant.Ok(kiosk, "/api/shopfloor/report", new { badge = Sarah, orderNumber = order, sequence = 10, good = 2m, scrap = 0m, complete = true });

        // Serial numbers are assigned at production receipt: one per unit, never reused.
        var receive = "/api/inventory/production-receipts";
        await Rejected(plant, stores, receive, new { orderNumber = order, quantity = 2m, locationCode = "STORES" }, "serials_required");
        await Rejected(plant, stores, receive, new { orderNumber = order, quantity = 2m, locationCode = "STORES", serials = new[] { "000481" } }, "serials_required");
        await Rejected(plant, stores, receive, new { orderNumber = order, quantity = 2m, locationCode = "STORES", serials = new[] { "000481", "000481" } }, "duplicate_serial");
        var received = await plant.Ok(stores, receive, new { orderNumber = order, quantity = 2m, locationCode = "STORES", serials = new[] { "000481", "000482" } });
        Assert.Contains("serial 000481, 000482", received.GetProperty("message").GetString(), StringComparison.Ordinal);

        // Serial 000481 ships.
        var shipment = Id(await plant.Ok(stores, "/api/inventory/shipments", new { customer = "Orbital Systems", shipTo = "Dock 2", lines = new[] { new { partNumber = "CTRL-UNIT", quantity = 1m, locationCode = "STORES", lotNumber = "000481" } } }));
        await plant.Ok(stores, $"/api/inventory/shipments/{shipment}/pack", new { });
        await plant.Ok(ship, $"/api/inventory/shipments/{shipment}/ship", new { carrier = "UPS", tracking = "1Z0481" });

        // Months later: the board has a new revision, the equivalent is expired, the supplier is renamed.
        await plant.Ok(e, "/api/engineering/items/PCB-1/revisions", new { revision = "B" });
        await ReleaseAsync(plant, "PCB-1", "B");
        await plant.Ok(approver, $"/api/engineering/alternates/{equivalent}/expire", new { reason = "Second source withdrawn" });
        await plant.Ok(bob, "/api/procurement/suppliers/ACME", new { name = "Acme Global Electronics", reason = "Rebranded" });

        // Genealogy of 000481: from the records as they were.
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.As("tc").GetAsync(new Uri("/api/execution/serials/000481/genealogy", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await planner.GetAsync(new Uri("/api/execution/serials/999999/genealogy", UriKind.Relative))).StatusCode);
        var g = await Get(planner, "/api/execution/serials/000481/genealogy?part=CTRL-UNIT");
        Assert.Equal(("CTRL-UNIT", "A", order), (g.GetProperty("partNumber").GetString(), g.GetProperty("revision").GetString(), g.GetProperty("orderNumber").GetInt64()));
        Assert.Contains("shipped on shipment", g.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.Equal("Orbital Systems", Assert.Single(g.GetProperty("shipments").EnumerateArray()).GetProperty("customer").GetString());
        var built = g.GetProperty("order");
        Assert.Equal(["000481", "000482"], built.GetProperty("produced").EnumerateArray().Select(x => x.GetString()!).ToArray());
        var components = built.GetProperty("components").EnumerateArray().ToList();
        var board = components.Single(c => c.GetProperty("lotNumber").GetString() == "SN-B1");
        Assert.Equal(("A", true, "Acme Electronics", poBoards), (board.GetProperty("revision").GetString(), board.GetProperty("serial").GetBoolean(), board.GetProperty("supplier").GetString(), board.GetProperty("poNumber").GetInt64()));
        var substitute = components.Single(c => c.GetProperty("partNumber").GetString() == "HOUS-2");
        Assert.Equal(("HOUS-1", 1m, equivalent, $"person:{plant.People["approver"]}"),
            (substitute.GetProperty("inPlaceOf").GetString(), substitute.GetProperty("quantity").GetDecimal(), substitute.GetProperty("alternateId").GetInt64(), substitute.GetProperty("alternateApprovedBy").GetString()));
        Assert.Equal(("Case Company", "H2-9"), (substitute.GetProperty("supplier").GetString(), substitute.GetProperty("lotNumber").GetString()));
        Assert.Equal(4, components.Count);
        var labor = Assert.Single(built.GetProperty("labor").EnumerateArray());
        Assert.Equal(($"person:{plant.People["sarah"]}", "Operator"), (labor.GetProperty("person").GetString(), labor.GetProperty("role").GetString()));
        Assert.Contains("SLIT-II", labor.GetProperty("qualifications").EnumerateArray().Select(q => q.GetString()));
        Assert.Equal(6, built.GetProperty("lines").GetArrayLength()); // five issues and one return

        // Where the board went, from its own record.
        var b1 = await Get(planner, "/api/execution/serials/SN-B1/genealogy");
        Assert.Equal(order, Assert.Single(b1.GetProperty("usedIn").EnumerateArray()).GetProperty("orderNumber").GetInt64());
        Assert.Equal("Acme Electronics", Assert.Single(b1.GetProperty("received").EnumerateArray()).GetProperty("supplier").GetString());

        // The as-built record and the labor qualifications are never rewritten.
        await using var db = new NpgsqlConnection(app.ConnectionString);
        await db.OpenAsync();
        foreach (var sql in new[]
        {
            "UPDATE execution.as_built SET supplier_name = 'Acme Global Electronics'",
            "DELETE FROM execution.as_built",
            "UPDATE execution.labor SET qualifications = ARRAY['NONE']",
        })
        {
            await using var tamper = new NpgsqlCommand(sql, db);
            await Assert.ThrowsAsync<PostgresException>(() => tamper.ExecuteNonQueryAsync());
        }

        // The order's own genealogy, and the serial that has not shipped.
        Assert.Equal(4, (await Get(planner, $"/api/orders/{order}/genealogy")).GetProperty("components").GetArrayLength());
        Assert.Contains("not shipped", (await Get(planner, "/api/execution/serials/000482/genealogy")).GetProperty("summary").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_asset_component_installed_from_stores_traces_to_its_lot_supplier_and_purchase_order()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("mp", "MaintenancePlanner");
        await plant.Person("tech", "MaintenanceTechnician");
        await plant.Person("bob", "Buyer");
        await plant.Person("pat", "Buyer", "PurchasingManager");
        var (mp, tech, bob, pat, stores) = (plant.As("mp"), plant.As("tech"), plant.As("bob"), plant.As("pat"), plant.As("stores"));
        await ItemAsync(plant, "MOTOR-5", lotTracked: true);
        await ReleaseAsync(plant, "MOTOR-5");
        await plant.Ok(bob, "/api/procurement/suppliers", new { code = "GRAINGER", name = "Grainger" });
        var po = Id(await plant.Ok(bob, "/api/procurement/purchase-orders", new { supplier = "GRAINGER", lines = new[] { new { partNumber = "MOTOR-5", quantity = 1m, promisedDate = Day(3) } } }));
        await plant.Ok(pat, $"/api/procurement/purchase-orders/{po}/approve", new { reason = "Spare motor" });
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "MOTOR-5", quantity = 1m, locationCode = "STORES", lotNumber = "M-55", poNumber = po, poLine = 1, revision = "A" });

        await plant.Ok(mp, "/api/maintenance/assets", new { assetCode = "SL-01", name = "Slitter 1", type = "Production", machineCode = "SLITTER-A", criticality = "High" });
        await plant.Ok(mp, "/api/maintenance/assets/SL-01/components", new { partNumber = "MOTOR-5", position = "Rewind drive", quantity = 1m });
        await plant.Ok(mp, "/api/maintenance/assets/SL-01/status", new { status = "Active", reason = "In service" });
        await plant.Ok(plant.Engineer, "/api/maintenance/machines/SLITTER-A/down", new { description = "Rewind drive overheating" });
        var repair = (await Get(mp, "/api/maintenance/work-orders")).EnumerateArray().Select(v => v.GetProperty("order")).Single(o => o.GetProperty("kind").GetString() == "Corrective").GetProperty("workOrderId").GetInt64();
        await plant.Ok(mp, $"/api/maintenance/work-orders/{repair}/requirements", new { assetCode = "SL-01", parts = new[] { new { partNumber = "MOTOR-5", quantity = 1m, position = "Rewind drive" } } });
        await plant.Ok(tech, $"/api/maintenance/work-orders/{repair}/parts", new { partNumber = "MOTOR-5", quantity = 1m, locationCode = "STORES", lotNumber = "M-55" });
        await PropagateAsync(app);

        var g = await Get(tech, "/api/maintenance/assets/SL-01/genealogy");
        var parts = g.GetProperty("components").EnumerateArray().ToList();
        Assert.Equal(2, parts.Count);
        var delivered = parts.Single(p => p.GetProperty("component").GetProperty("removedAt").ValueKind == JsonValueKind.String);
        Assert.Contains("delivered", delivered.GetProperty("source").GetString(), StringComparison.Ordinal);
        var fitted = parts.Single(p => p.GetProperty("component").GetProperty("removedAt").ValueKind == JsonValueKind.Null);
        Assert.Equal(("M-55", "Grainger", po, "A"), (fitted.GetProperty("lotNumber").GetString(), fitted.GetProperty("supplier").GetString(), fitted.GetProperty("poNumber").GetInt64(), fitted.GetProperty("revision").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await tech.GetAsync(new Uri("/api/maintenance/assets/NOPE/genealogy", UriKind.Relative))).StatusCode);
    }
}
