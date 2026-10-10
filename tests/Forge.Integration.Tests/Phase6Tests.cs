using System.Globalization;
using System.Net;
using System.Text.Json;
using Npgsql;

namespace Forge.Integration.Tests;

/// <summary>
/// Phase 6 exit gate through the real API: a transactional inventory ledger with two-person
/// adjustments, counts and quality releases; purchase orders approved by a second person; material
/// readiness feeding order readiness and the schedule; demand, item planning and explainable MRP.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class Phase6Tests(PostgresFixture postgres)
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

    private static decimal Stock(JsonElement balances, string location, string status) =>
        balances.EnumerateArray().Where(b => b.GetProperty("locationCode").GetString() == location && b.GetProperty("status").GetString() == status).Sum(b => b.GetProperty("quantity").GetDecimal());

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    [Fact]
    public async Task Every_quantity_is_a_ledger_sum_and_changes_need_a_second_person()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("mike", "MaterialHandler");
        await plant.Person("ivy", "InventoryController");
        await plant.Person("bob", "Buyer");
        await plant.Person("pat", "Buyer", "PurchasingManager");
        var (mike, ivy, stores, bob, pat) = (plant.As("mike"), plant.As("ivy"), plant.As("stores"), plant.As("bob"), plant.As("pat"));
        await plant.Ok(ivy, "/api/inventory/locations", new { code = "RECV", siteCode = "PLANT1", name = "Receiving dock", kind = "Receiving" });

        // Purchase orders name a supplier from the supplier master (V2.1).
        Assert.Contains("unknown_supplier", (await Try(plant, bob, "/api/procurement/purchase-orders", new { supplier = "Alcoa", lines = new[] { new { partNumber = "COIL-AL", quantity = 1m, promisedDate = Day(7) } } })).Body, StringComparison.Ordinal);
        await plant.Ok(bob, "/api/procurement/suppliers", new { code = "ALCOA", name = "Alcoa" });

        // A purchase order counts only once someone other than the buyer approves it.
        var po = Id(await plant.Ok(bob, "/api/procurement/purchase-orders", new { supplier = "Alcoa", lines = new[] { new { partNumber = "COIL-AL", quantity = 500m, promisedDate = Day(7) } } }));
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, bob, $"/api/procurement/purchase-orders/{po}/approve", new { reason = "x" })).Status);
        var own = Id(await plant.Ok(pat, "/api/procurement/purchase-orders", new { supplier = "Alcoa", lines = new[] { new { partNumber = "COIL-AL", quantity = 5m, promisedDate = Day(7) } } }));
        var selfApproval = await Try(plant, pat, $"/api/procurement/purchase-orders/{own}/approve", new { reason = "x" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, selfApproval.Status);
        Assert.Contains("separation_of_duties", selfApproval.Body, StringComparison.Ordinal);
        var early = await Try(plant, mike, "/api/inventory/receipts", new { partNumber = "COIL-AL", quantity = 10m, locationCode = "RECV", poNumber = po, inspect = true });
        Assert.Contains("purchase_order_not_open", early.Body, StringComparison.Ordinal);
        await plant.Ok(pat, $"/api/procurement/purchase-orders/{po}/approve", new { reason = "Budget approved" });
        var buyOnly = await Try(plant, bob, "/api/procurement/purchase-orders", new { supplier = "ALCOA", lines = new[] { new { partNumber = "ROLL-100", quantity = 1m, promisedDate = Day(7) } } });
        Assert.Contains("not_purchased", buyOnly.Body, StringComparison.Ordinal);

        // Receive into inspection; never more than ordered.
        await plant.Ok(mike, "/api/inventory/receipts", new { partNumber = "COIL-AL", quantity = 200m, locationCode = "RECV", poNumber = po, inspect = true });
        var over = await Try(plant, mike, "/api/inventory/receipts", new { partNumber = "COIL-AL", quantity = 400m, locationCode = "RECV", poNumber = po, inspect = true });
        Assert.Contains("over_receipt", over.Body, StringComparison.Ordinal);
        var noReason = await Try(plant, mike, "/api/inventory/receipts", new { partNumber = "COIL-AL", quantity = 1m, locationCode = "RECV" });
        Assert.Contains("reason", noReason.Body, StringComparison.OrdinalIgnoreCase);

        var orders = await Get(bob, "/api/procurement/purchase-orders");
        var line = orders.EnumerateArray().Single(o => o.GetProperty("poNumber").GetInt64() == po).GetProperty("lines")[0];
        Assert.Equal(200m, line.GetProperty("received").GetDecimal());
        Assert.Equal(300m, line.GetProperty("open").GetDecimal());

        // Inspection stock is released by a controller, never by the person who received it.
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, mike, "/api/inventory/releases", new { partNumber = "COIL-AL", quantity = 200m, locationCode = "RECV", status = "Inspection", reason = "x" })).Status);
        await plant.Ok(ivy, "/api/inventory/releases", new { partNumber = "COIL-AL", quantity = 200m, locationCode = "RECV", status = "Inspection", reason = "Certificate of conformance checked" });
        await plant.Ok(mike, "/api/inventory/transfers", new { partNumber = "COIL-AL", quantity = 200m, fromLocation = "RECV", toLocation = "STORES" });
        var negative = await Try(plant, mike, "/api/inventory/scrap", new { partNumber = "COIL-AL", quantity = 1m, locationCode = "RECV", reason = "Damaged" });
        Assert.Contains("insufficient_stock", negative.Body, StringComparison.Ordinal);

        // Adjustments: requested by one person, approved by another.
        var adjustment = Id(await plant.Ok(stores, "/api/inventory/adjustments", new { partNumber = "COIL-AL", locationCode = "STORES", change = -50m, reason = "Water damage found" }));
        Assert.Contains("separation_of_duties", (await Try(plant, stores, $"/api/inventory/requests/{adjustment}/approve", new { reason = "x" })).Body, StringComparison.Ordinal);
        await plant.Ok(ivy, $"/api/inventory/requests/{adjustment}/approve", new { reason = "Photos reviewed" });

        // A count posts its variance only if nothing moved since the count.
        var count = await plant.Ok(mike, "/api/inventory/counts", new { partNumber = "COIL-AL", locationCode = "STORES", counted = 1_000_100m });
        Assert.Contains("variance -50", count.GetProperty("message").GetString(), StringComparison.Ordinal);
        await plant.Ok(ivy, $"/api/inventory/requests/{Id(count)}/approve", new { reason = "Recount agreed" });
        var stale = Id(await plant.Ok(mike, "/api/inventory/counts", new { partNumber = "COIL-AL", locationCode = "STORES", counted = 5m }));
        await plant.Ok(mike, "/api/inventory/transfers", new { partNumber = "COIL-AL", quantity = 10m, fromLocation = "STORES", toLocation = "RECV" });
        Assert.Contains("stale_count", (await Try(plant, ivy, $"/api/inventory/requests/{stale}/approve", new { reason = "x" })).Body, StringComparison.Ordinal);
        await plant.Ok(ivy, $"/api/inventory/requests/{stale}/reject", new { reason = "Stock moved; count again" });

        var balances = await Get(mike, "/api/inventory/stock?part=COIL-AL");
        Assert.Equal(1_000_090m, Stock(balances, "STORES", "Released"));
        Assert.Equal(10m, Stock(balances, "RECV", "Released"));

        // Every balance is the sum of its ledger entries, and the ledger cannot be edited.
        var ledger = await Get(mike, "/api/inventory/transactions?part=COIL-AL&take=1000");
        var sum = ledger.EnumerateArray().Sum(t =>
            (t.GetProperty("toLocation").ValueKind == JsonValueKind.String ? t.GetProperty("quantity").GetDecimal() : 0)
            - (t.GetProperty("fromLocation").ValueKind == JsonValueKind.String ? t.GetProperty("quantity").GetDecimal() : 0));
        Assert.Equal(balances.EnumerateArray().Sum(b => b.GetProperty("quantity").GetDecimal()), sum);
        Assert.Contains(ledger.EnumerateArray(), t => t.GetProperty("kind").GetString() == "Adjustment" && t.GetProperty("approvedBy").GetString() != t.GetProperty("actor").GetString());

        await using var db = new NpgsqlConnection(app.ConnectionString);
        await db.OpenAsync();
        await using var tamper = new NpgsqlCommand("UPDATE inventory.transaction SET quantity = quantity + 1", db);
        await Assert.ThrowsAsync<PostgresException>(() => tamper.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Material_drives_order_readiness_and_the_schedule()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var e = plant.Engineer;
        await plant.Person("mike", "MaterialHandler");
        await plant.Person("bob", "Buyer");
        await plant.Person("pat", "PurchasingManager");
        var (mike, bob, pat) = (plant.As("mike"), plant.As("bob"), plant.As("pat"));

        // A bundle of wire, assembled by hand. Nobody has any wire yet.
        await plant.Ok(e, "/api/engineering/items", new { partNumber = "WIRE", description = "Copper wire", unitOfMeasure = "M", makeBuy = "Buy", leadTimeDays = 5 });
        await plant.Ok(e, "/api/engineering/items", new { partNumber = "BUNDLE", description = "Wire bundle", unitOfMeasure = "EA", makeBuy = "Make", leadTimeDays = 1 });
        await plant.Ok(e, "/api/engineering/items/WIRE/revisions", new { revision = "A" });
        await plant.Ok(e, "/api/engineering/items/BUNDLE/revisions", new { revision = "A" });
        await plant.Ok(e, "/api/engineering/items/BUNDLE/revisions/A/bom", new { lineNumber = 1, componentPartNumber = "WIRE", quantityPer = 5m, scrapPercent = 0m });
        await plant.Ok(e, "/api/engineering/items/BUNDLE/revisions/A/routing", new { sequence = 10, description = "Bundle", workCenterCode = "ASSY-WC", activityCode = "ASSEMBLE", setupMinutes = 0m, runMinutesPerUnit = 5m, crewSize = 1 });
        await plant.Ok(plant.Approver, "/api/engineering/items/WIRE/revisions/A/release", new { reason = "ECN-6" });
        await plant.Ok(plant.Approver, "/api/engineering/items/BUNDLE/revisions/A/release", new { reason = "ECN-6" });
        await plant.Ok(e, "/api/resources/shifts", new { code = "ALL", siteCode = "PLANT1", name = "Around the clock", start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 } });
        await plant.Ok(e, "/api/resources/work-centers/ASSY-WC/shifts", new { shiftCode = "ALL" });
        await plant.Ok(e, $"/api/workforce/workers/{plant.People["dana"]}/shift", new { shiftCode = "ALL" });

        var bundle = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "BUNDLE", quantity = 4m, dueDate = Day(10), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{bundle}/release", new { reason = "Customer order" });
        var roll = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "ROLL-100", quantity = 10m, dueDate = Day(10), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{roll}/release", new { reason = "Customer order" });

        // No wire: the bundle order is short, the roll order is ready.
        var readiness = await Get(planner, $"/api/orders/{bundle}/readiness");
        var op = readiness.GetProperty("output").GetProperty("operations")[0];
        Assert.Equal("Material", op.GetProperty("primaryConstraint").GetString());
        Assert.DoesNotContain("Material", op.GetProperty("notEvaluated").EnumerateArray().Select(x => x.GetString()));
        var material = (await Get(planner, "/api/planning/material")).GetProperty("output");
        Assert.Equal("Shortfall", material.EnumerateArray().Single(m => m.GetProperty("orderNumber").GetInt64() == bundle).GetProperty("state").GetString());
        Assert.Equal("Ready", material.EnumerateArray().Single(m => m.GetProperty("orderNumber").GetInt64() == roll).GetProperty("state").GetString());

        var schedule = await Proposed(plant, planner);
        Assert.Equal("Unschedulable", schedule.GetProperty("orders").EnumerateArray().Single(o => o.GetProperty("orderNumber").GetInt64() == bundle).GetProperty("status").GetString());
        var constraints = await Get(planner, "/api/planning/constraints");
        Assert.Contains(constraints.EnumerateArray(), c => c.GetProperty("category").GetString() == "Material" && c.GetProperty("affectedOrders").EnumerateArray().Any(o => o.GetInt64() == bundle));

        // MRP recommends buying exactly the 20 m the order still needs, and says why.
        var mrp = (await Get(planner, "/api/planning/mrp?part=WIRE")).GetProperty("output");
        var planned = Assert.Single(mrp.GetProperty("plannedOrders").EnumerateArray());
        Assert.Equal(20m, planned.GetProperty("quantity").GetDecimal());
        Assert.Contains($"WO{bundle}/WIRE", planned.GetProperty("pegging").EnumerateArray().Select(p => p.GetProperty("requirementKey").GetString()));
        Assert.Contains("20 more is needed", planned.GetProperty("explanation").GetString(), StringComparison.Ordinal);

        // An approved purchase order makes it expected; the schedule waits for its date.
        await plant.Ok(bob, "/api/procurement/suppliers", new { code = "SOUTHWIRE", name = "Southwire" });
        var po = Id(await plant.Ok(bob, "/api/procurement/purchase-orders", new { supplier = "Southwire", lines = new[] { new { partNumber = "WIRE", quantity = 20m, promisedDate = Day(3) } } }));
        await plant.Ok(pat, $"/api/procurement/purchase-orders/{po}/approve", new { reason = "Approved" });
        material = (await Get(planner, "/api/planning/material")).GetProperty("output");
        var expected = material.EnumerateArray().Single(m => m.GetProperty("orderNumber").GetInt64() == bundle);
        Assert.Equal("Expected", expected.GetProperty("state").GetString());
        schedule = await Proposed(plant, planner);
        var bundleOp = schedule.GetProperty("operations").EnumerateArray().Single(o => o.GetProperty("orderNumber").GetInt64() == bundle);
        Assert.Equal("Material", bundleOp.GetProperty("bindingConstraint").GetString());
        Assert.True(bundleOp.GetProperty("start").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddDays(1));

        // Receive it and issue it: the order is ready, and issuing more than it needs is refused.
        await plant.Ok(mike, "/api/inventory/receipts", new { partNumber = "WIRE", quantity = 20m, locationCode = "STORES", poNumber = po });
        readiness = await Get(planner, $"/api/orders/{bundle}/readiness");
        Assert.Contains(readiness.GetProperty("output").GetProperty("operations")[0].GetProperty("dimensions").EnumerateArray(),
            d => d.GetProperty("name").GetString() == "Material" && d.GetProperty("outcome").GetString() == "Pass");
        await plant.Ok(mike, "/api/inventory/issues", new { orderNumber = bundle, partNumber = "WIRE", quantity = 20m, locationCode = "STORES" });
        var overIssue = await Try(plant, mike, "/api/inventory/issues", new { orderNumber = bundle, partNumber = "WIRE", quantity = 1m, locationCode = "STORES" });
        Assert.Contains("over_issue", overIssue.Body, StringComparison.Ordinal);
        await plant.Ok(mike, "/api/inventory/returns", new { orderNumber = bundle, partNumber = "WIRE", quantity = 2m, locationCode = "STORES", reason = "Left over" });
        var issued = await Get(mike, $"/api/inventory/transactions?order={bundle}");
        Assert.Equal(2, issued.GetArrayLength());

        // Nothing has been produced, so nothing can be received from production.
        var notMade = await Try(plant, mike, "/api/inventory/production-receipts", new { orderNumber = bundle, quantity = 1m, locationCode = "STORES" });
        Assert.Contains("over_receipt", notMade.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Demand_and_item_planning_drive_an_explainable_mrp()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var e = plant.Engineer;
        var roll = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "ROLL-100", quantity = 10m, dueDate = Day(10), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{roll}/release", new { reason = "Customer order" });

        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, e, "/api/planning/demand", new { partNumber = "ROLL-100", quantity = 120m, needDate = Day(20), kind = "CustomerOrder" })).Status);
        var demand = Id(await plant.Ok(planner, "/api/planning/demand", new { partNumber = "ROLL-100", quantity = 120m, needDate = Day(20), priority = 2, kind = "CustomerOrder", customer = "Acme", reference = "PO-778" }));
        await plant.Ok(planner, "/api/engineering/items/ROLL-100/planning", new { leadTimeDays = 2, safetyStock = 0m, lotPolicy = "FixedQuantity", orderQuantity = 50m, orderMultiple = 0m });
        var badPolicy = await Try(plant, planner, "/api/engineering/items/ROLL-100/planning", new { leadTimeDays = 2, safetyStock = 0m, lotPolicy = "FixedQuantity", orderQuantity = 0m, orderMultiple = 0m });
        Assert.Contains("order_quantity_required", badPolicy.Body, StringComparison.Ordinal);

        // 120 needed, 10 already on order: 110 net, in fixed lots of 50 -> 150.
        var mrp = (await Get(planner, "/api/planning/mrp?part=ROLL-100")).GetProperty("output");
        var planned = Assert.Single(mrp.GetProperty("plannedOrders").EnumerateArray());
        Assert.Equal(110m, planned.GetProperty("netRequirement").GetDecimal());
        Assert.Equal(150m, planned.GetProperty("quantity").GetDecimal());
        Assert.Contains("Fixed lots of 50", planned.GetProperty("explanation").GetString(), StringComparison.Ordinal);
        Assert.Contains(planned.GetProperty("pegging").EnumerateArray(), p => p.GetProperty("requirementKey").GetString() == $"D{demand}");
        var item = Assert.Single(mrp.GetProperty("items").EnumerateArray());
        Assert.Contains(item.GetProperty("receipts").EnumerateArray(), r => r.GetProperty("key").GetString() == $"WO{roll}");

        // The whole plan explodes ROLL-100 into coil, which stock covers.
        var all = (await Get(planner, "/api/planning/mrp")).GetProperty("output");
        Assert.DoesNotContain(all.GetProperty("plannedOrders").EnumerateArray(), p => p.GetProperty("partNumber").GetString() == "COIL-AL");
        Assert.Contains(all.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("partNumber").GetString() == "COIL-AL").GetProperty("requirements").EnumerateArray(),
            r => r.GetProperty("key").GetString()!.StartsWith("PLN-ROLL-100-", StringComparison.Ordinal));

        // Changing demand needs a reason; closing it removes it from MRP.
        Assert.Contains("reason", (await Try(plant, planner, $"/api/planning/demand/{demand}/change", new { quantity = 60m, needDate = Day(20), priority = 2 })).Body, StringComparison.OrdinalIgnoreCase);
        await plant.Ok(planner, $"/api/planning/demand/{demand}/close", new { reason = "Customer cancelled" });
        mrp = (await Get(planner, "/api/planning/mrp?part=ROLL-100")).GetProperty("output");
        Assert.Equal(0, mrp.GetProperty("plannedOrders").GetArrayLength());

        var summary = await Get(planner, "/api/inventory/summary");
        var coil = summary.EnumerateArray().Single(s => s.GetProperty("partNumber").GetString() == "COIL-AL");
        Assert.Equal(26m, coil.GetProperty("allocated").GetDecimal()); // 10 x 2.5 x 1.04, not yet issued
        Assert.Equal(1_000_000m - 26m, coil.GetProperty("availableForPlanning").GetDecimal());
    }

    private static async Task<JsonElement> Proposed(SlittingPlant.Plant plant, HttpClient planner)
    {
        await plant.Ok(planner, "/api/planning/schedules", new { horizonDays = 14 });
        return (await Get(planner, "/api/planning/schedules/proposed")).GetProperty("result");
    }
}
