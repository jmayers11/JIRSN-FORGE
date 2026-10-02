using System.Globalization;
using System.Net;
using System.Text.Json;
using Npgsql;

namespace Forge.Integration.Tests;

/// <summary>
/// Phase 7a exit gate through the real API: inspection plans on routing operations, independent
/// inspections with calibrated gauges, nonconformances that hold the order until dispositioned (with a
/// second person for use-as-is), corrective actions verified by someone else, production receipts that
/// wait for inspection, and shipments packed by one person and shipped by another.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class Phase7Tests(PostgresFixture postgres)
{
    private const string Sarah = "B-SARAH-0001";

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

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static JsonElement Dimension(JsonElement readiness, int sequence, string name) =>
        readiness.GetProperty("output").GetProperty("operations").EnumerateArray()
            .Single(o => o.GetProperty("sequence").GetInt32() == sequence)
            .GetProperty("dimensions").EnumerateArray()
            .Single(d => d.GetProperty("name").GetString() == name);

    [Fact]
    public async Task Inspections_and_nonconformances_hold_the_order_until_quality_decides()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var e = plant.Engineer;
        await plant.Ok(e, "/api/resources/shifts", new { code = "ALL", siteCode = "PLANT1", name = "Around the clock", start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 } });
        await plant.Ok(e, "/api/resources/work-centers/SLIT-WC/shifts", new { shiftCode = "ALL" });
        await plant.Ok(e, $"/api/workforce/workers/{plant.People["sarah"]}/shift", new { shiftCode = "ALL" });
        await plant.Ok(plant.Admin, $"/api/workforce/people/{plant.People["sarah"]}/badge", new { badge = Sarah });
        await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["sarah"]}/roles", new { role = "Inspector", reason = "Cross-trained inspector" });
        await plant.Person("kiosk", "ShopFloorTerminal");
        await plant.Person("qe", "QualityEngineer");
        await plant.Person("qm", "QualityManager");
        await plant.Person("insp", "Inspector");
        var (k, qe, qm, insp, stores) = (plant.As("kiosk"), plant.As("qe"), plant.As("qm"), plant.As("insp"), plant.As("stores"));

        // Gauges and inspection points. Inspection points belong to operations of the released routing.
        await plant.Ok(qe, "/api/quality/gauges", new { code = "MIC-1", description = "Micrometer 0-150 mm", calibrationDue = Day(30), certificate = "CAL-2231" });
        await plant.Ok(qe, "/api/quality/gauges", new { code = "MIC-OLD", description = "Old micrometer", calibrationDue = Day(-1) });
        var noOp = await Try(plant, qe, "/api/quality/plans", new { partNumber = "ROLL-100", sequence = 99, title = "Nope", characteristics = new[] { new { name = "Width", kind = "Attribute" } } });
        Assert.Contains("unknown_operation", noOp.Body, StringComparison.Ordinal);
        var noLimits = await Try(plant, qe, "/api/quality/plans", new { partNumber = "ROLL-100", sequence = 10, title = "Slit check", characteristics = new[] { new { name = "Width", kind = "Variable" } } });
        Assert.Contains("limits_required", noLimits.Body, StringComparison.Ordinal);
        await plant.Ok(qe, "/api/quality/plans", new
        {
            partNumber = "ROLL-100",
            sequence = 10,
            title = "Slit width and edge",
            characteristics = new object[]
            {
                new { name = "Width", kind = "Variable", nominal = 100m, lower = 99.5m, upper = 100.5m, unit = "mm" },
                new { name = "Edge free of burrs", kind = "Attribute" },
            },
        });
        await plant.Ok(qe, "/api/quality/plans", new { partNumber = "ROLL-100", sequence = 20, title = "Final count and label", characteristics = new[] { new { name = "Label matches order", kind = "Attribute" } } });

        var order = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "ROLL-100", quantity = 10m, dueDate = Day(10), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Customer order" });

        // Nothing to inspect before the operation has been worked.
        var early = await Try(plant, insp, "/api/quality/inspections", new { orderNumber = order, sequence = 10, quantity = 2m, gaugeCode = "MIC-1", findings = new object[] { new { number = 1, measured = 100m }, new { number = 2, accepted = true } } });
        Assert.Contains("not_started", early.Body, StringComparison.Ordinal);

        await plant.Ok(k, "/api/shopfloor/clock-in", new { badge = Sarah });
        await plant.Ok(k, "/api/shopfloor/start", new { badge = Sarah, orderNumber = order, sequence = 10, machineCode = "SLITTER-A" });
        await plant.Ok(k, "/api/shopfloor/report", new { badge = Sarah, orderNumber = order, sequence = 10, good = 10m, scrap = 0m, complete = true });

        // Operation 20 waits for the inspection after operation 10, at the kiosk as well as in readiness.
        var notInspected = await Try(plant, k, "/api/shopfloor/start", new { badge = Sarah, orderNumber = order, sequence = 20 });
        Assert.Contains("order_on_hold", notInspected.Body, StringComparison.Ordinal);
        Assert.Contains("inspection after operation 10", notInspected.Body, StringComparison.Ordinal);
        var queue = await plant.Ok(k, "/api/shopfloor/identify", new { badge = Sarah });
        Assert.Contains(queue.GetProperty("queue").EnumerateArray(), q => q.GetProperty("orderNumber").GetInt64() == order && q.GetProperty("hold").ValueKind == JsonValueKind.String);
        var readiness = await Get(planner, $"/api/orders/{order}/readiness");
        Assert.Equal("Fail", Dimension(readiness, 20, "Quality").GetProperty("outcome").GetString());
        Assert.Equal("Quality", readiness.GetProperty("output").GetProperty("operations")[1].GetProperty("primaryConstraint").GetString());
        var due = await Get(insp, "/api/quality/due");
        Assert.Contains(due.EnumerateArray(), d => d.GetProperty("orderNumber").GetInt64() == order && d.GetProperty("sequence").GetInt32() == 10);

        var findings = new object[] { new { number = 1, measured = 100.1m }, new { number = 2, accepted = true } };
        var ownWork = await Try(plant, plant.As("sarah"), "/api/quality/inspections", new { orderNumber = order, sequence = 10, quantity = 2m, gaugeCode = "MIC-1", findings });
        Assert.Contains("separation_of_duties", ownWork.Body, StringComparison.Ordinal);
        var stale = await Try(plant, insp, "/api/quality/inspections", new { orderNumber = order, sequence = 10, quantity = 2m, gaugeCode = "MIC-OLD", findings });
        Assert.Contains("gauge_out_of_calibration", stale.Body, StringComparison.Ordinal);
        var noGauge = await Try(plant, insp, "/api/quality/inspections", new { orderNumber = order, sequence = 10, quantity = 2m, findings });
        Assert.Contains("gauge_required", noGauge.Body, StringComparison.Ordinal);

        // A measurement outside the limits fails the inspection and raises an NCR.
        var failed = await plant.Ok(insp, "/api/quality/inspections", new { orderNumber = order, sequence = 10, quantity = 2m, gaugeCode = "MIC-1", rejectedQuantity = 1m, notes = "Second roll wide", findings = new object[] { new { number = 1, measured = 100.8m }, new { number = 2, accepted = true } } });
        Assert.Contains("failed inspection", failed.GetProperty("message").GetString(), StringComparison.Ordinal);
        var ncr = Assert.Single((await Get(qe, "/api/quality/ncrs")).EnumerateArray());
        var ncrId = ncr.GetProperty("ncrId").GetInt64();
        Assert.Equal(1m, ncr.GetProperty("quantity").GetDecimal());
        Assert.Contains("Width 100.8 outside 99.5-100.5", ncr.GetProperty("description").GetString(), StringComparison.Ordinal);
        readiness = await Get(planner, $"/api/orders/{order}/readiness");
        Assert.Contains($"NCR {ncrId}", Dimension(readiness, 20, "Quality").GetProperty("detail").GetString(), StringComparison.Ordinal);
        var held = await Try(plant, k, "/api/shopfloor/start", new { badge = Sarah, orderNumber = order, sequence = 20 });
        Assert.Contains($"NCR {ncrId}", held.Body, StringComparison.Ordinal);

        // Use-as-is needs a reason, the disposition authority, and a different person to approve it.
        Assert.Contains("reason", (await Try(plant, qe, $"/api/quality/ncrs/{ncrId}/disposition", new { disposition = "UseAsIs", note = "Within customer tolerance" })).Body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, insp, $"/api/quality/ncrs/{ncrId}/disposition", new { disposition = "UseAsIs", note = "x", reason = "x" })).Status);
        await plant.Ok(qe, $"/api/quality/ncrs/{ncrId}/disposition", new { disposition = "UseAsIs", note = "Customer drawing allows +1.0 mm", reason = "Customer tolerance" });
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, qe, $"/api/quality/ncrs/{ncrId}/approve", new { reason = "x" })).Status);
        await plant.Person("lead", "QualityEngineer", "QualityManager");
        var lead = plant.As("lead");
        var own = Id(await plant.Ok(lead, "/api/quality/ncrs", new { partNumber = "ROLL-100", orderNumber = order, sequence = 10, quantity = 1m, defect = "Scratch", description = "Light scratch on face" }));
        await plant.Ok(lead, $"/api/quality/ncrs/{own}/disposition", new { disposition = "UseAsIs", note = "Cosmetic", reason = "Cosmetic only" });
        Assert.Contains("separation_of_duties", (await Try(plant, lead, $"/api/quality/ncrs/{own}/approve", new { reason = "x" })).Body, StringComparison.Ordinal);
        await plant.Ok(qm, $"/api/quality/ncrs/{own}/approve", new { reason = "Cosmetic, customer agreed" });
        await plant.Ok(qm, $"/api/quality/ncrs/{ncrId}/approve", new { reason = "Customer concession CC-14" });

        // The roll is re-inspected and passes; operation 20 is clear.
        await plant.Ok(insp, "/api/quality/inspections", new { orderNumber = order, sequence = 10, quantity = 2m, gaugeCode = "MIC-1", findings });
        readiness = await Get(planner, $"/api/orders/{order}/readiness");
        Assert.Equal("Pass", Dimension(readiness, 20, "Quality").GetProperty("outcome").GetString());

        await plant.Ok(k, "/api/shopfloor/start", new { badge = Sarah, orderNumber = order, sequence = 20 });
        await plant.Ok(k, "/api/shopfloor/report", new { badge = Sarah, orderNumber = order, sequence = 20, good = 10m, scrap = 0m, complete = true });

        // Finished goods go into stock only after the final inspection.
        await plant.Ok(stores, "/api/inventory/locations", new { code = "FG", siteCode = "PLANT1", name = "Finished goods", kind = "Stores" });
        var uninspected = await Try(plant, stores, "/api/inventory/production-receipts", new { orderNumber = order, quantity = 10m, locationCode = "FG" });
        Assert.Contains("inspection_required", uninspected.Body, StringComparison.Ordinal);
        await plant.Ok(insp, "/api/quality/inspections", new { orderNumber = order, sequence = 20, quantity = 10m, findings = new[] { new { number = 1, accepted = true } } });
        await plant.Ok(stores, "/api/inventory/production-receipts", new { orderNumber = order, quantity = 10m, locationCode = "FG" });

        // Corrective action: implemented by one person, verified by another with authority.
        var capa = Id(await plant.Ok(qe, "/api/quality/capas", new { ncrId, title = "Slitter knife setting drift", rootCause = "Knife spacer worn", action = "Replace spacers; add spacer check to setup sheet", dueDate = Day(14) }));
        await plant.Ok(qe, $"/api/quality/capas/{capa}/implement", new { note = "Spacers replaced, setup sheet rev C" });
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, qe, $"/api/quality/capas/{capa}/verify", new { reason = "x" })).Status);
        await plant.Ok(qm, $"/api/quality/capas/{capa}/verify", new { reason = "Three setups checked, no drift" });
        Assert.Equal("Verified", (await Get(qm, "/api/quality/capas?includeVerified=true")).EnumerateArray().Single(c => c.GetProperty("capaId").GetInt64() == capa).GetProperty("status").GetString());

        await plant.Ok(qe, $"/api/quality/ncrs/{ncrId}/close", new { note = "Shipped under concession", reason = "Complete" });
        var history = await Get(qe, $"/api/quality/orders/{order}");
        Assert.Equal(3, history.GetProperty("inspections").GetArrayLength());
        Assert.Empty(history.GetProperty("quality").GetProperty("openNcrs").EnumerateArray());

        // Inspection records are evidence: they cannot be edited.
        await using var db = new NpgsqlConnection(app.ConnectionString);
        await db.OpenAsync();
        await using var tamper = new NpgsqlCommand("UPDATE quality.inspection SET result = 'Pass'", db);
        await Assert.ThrowsAsync<PostgresException>(() => tamper.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Shipments_are_packed_by_one_person_and_shipped_by_another()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("ship", "Shipper");
        await plant.Person("both", "MaterialHandler", "Shipper");
        var (stores, ship, both) = (plant.As("stores"), plant.As("ship"), plant.As("both"));
        await plant.Ok(stores, "/api/inventory/locations", new { code = "FG", siteCode = "PLANT1", name = "Finished goods", kind = "Stores" });
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "ROLL-100", quantity = 10m, locationCode = "FG", reason = "Opening balance" });
        var demand = Id(await plant.Ok(planner, "/api/planning/demand", new { partNumber = "ROLL-100", quantity = 6m, needDate = Day(5), priority = 2, kind = "CustomerOrder", customer = "Acme", reference = "PO-901" }));

        var wrongPart = await Try(plant, stores, "/api/inventory/shipments", new { demandId = demand, lines = new[] { new { partNumber = "COIL-AL", quantity = 1m, locationCode = "STORES" } } });
        Assert.Contains("wrong_part", wrongPart.Body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, ship, "/api/inventory/shipments", new { customer = "Acme", lines = new[] { new { partNumber = "ROLL-100", quantity = 1m, locationCode = "FG" } } })).Status);

        var shipment = Id(await plant.Ok(stores, "/api/inventory/shipments", new { demandId = demand, shipTo = "Acme, Dock 4", lines = new[] { new { partNumber = "ROLL-100", quantity = 6m, locationCode = "FG" } } }));
        var notPacked = await Try(plant, ship, $"/api/inventory/shipments/{shipment}/ship", new { carrier = "UPS" });
        Assert.Contains("not_packed", notPacked.Body, StringComparison.Ordinal);
        await plant.Ok(stores, $"/api/inventory/shipments/{shipment}/pack", new { });
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, stores, $"/api/inventory/shipments/{shipment}/ship", new { carrier = "UPS" })).Status);
        await plant.Ok(ship, $"/api/inventory/shipments/{shipment}/ship", new { carrier = "UPS", tracking = "1Z999" });

        // Someone allowed to do both still cannot ship what they packed.
        var mine = Id(await plant.Ok(both, "/api/inventory/shipments", new { customer = "Beta", lines = new[] { new { partNumber = "ROLL-100", quantity = 1m, locationCode = "FG" } } }));
        await plant.Ok(both, $"/api/inventory/shipments/{mine}/pack", new { });
        Assert.Contains("separation_of_duties", (await Try(plant, both, $"/api/inventory/shipments/{mine}/ship", new { })).Body, StringComparison.Ordinal);
        var tooMuch = await Try(plant, stores, "/api/inventory/shipments", new { customer = "Gamma", lines = new[] { new { partNumber = "ROLL-100", quantity = 50m, locationCode = "FG" } } });
        var big = Id(JsonDocument.Parse(tooMuch.Body).RootElement);
        Assert.Contains("insufficient_stock", (await Try(plant, stores, $"/api/inventory/shipments/{big}/pack", new { })).Body, StringComparison.Ordinal);
        Assert.Contains("reason", (await Try(plant, both, $"/api/inventory/shipments/{mine}/cancel", new { })).Body, StringComparison.OrdinalIgnoreCase);
        await plant.Ok(both, $"/api/inventory/shipments/{mine}/cancel", new { reason = "Customer changed the order" });

        // Shipping took the stock out through the ledger, and the demand is met.
        var balances = await Get(stores, "/api/inventory/stock?part=ROLL-100");
        Assert.Equal(4m, balances.EnumerateArray().Sum(b => b.GetProperty("quantity").GetDecimal()));
        var ledger = await Get(stores, "/api/inventory/transactions?part=ROLL-100");
        var shipped = Assert.Single(ledger.EnumerateArray(), t => t.GetProperty("kind").GetString() == "Shipment");
        Assert.Equal(shipment, shipped.GetProperty("shipmentId").GetInt64());
        var demandRow = (await Get(planner, "/api/planning/demand")).EnumerateArray().Single(d => d.GetProperty("demandId").GetInt64() == demand);
        Assert.Equal(6m, demandRow.GetProperty("shipped").GetDecimal());
        Assert.Equal(0m, demandRow.GetProperty("remaining").GetDecimal());
        var mrp = (await Get(planner, "/api/planning/mrp?part=ROLL-100")).GetProperty("output");
        Assert.DoesNotContain(mrp.GetProperty("items")[0].GetProperty("requirements").EnumerateArray(), r => r.GetProperty("key").GetString() == $"D{demand}");

        var list = await Get(ship, "/api/inventory/shipments?includeClosed=true");
        var done = list.EnumerateArray().Single(s => s.GetProperty("shipmentId").GetInt64() == shipment);
        Assert.Equal("Shipped", done.GetProperty("status").GetString());
        Assert.Equal("Acme", done.GetProperty("customer").GetString());
        Assert.NotEqual(done.GetProperty("packedBy").GetString(), done.GetProperty("shippedBy").GetString());
    }
}
