using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Forge.Integration.Tests;

/// <summary>
/// V3.3 through the real API: what became of material issued to an order is recorded (consumed, scrap, loss,
/// remnant, byproduct, remaining); issued = consumed + returned + scrap + loss + remnant + remaining within the
/// yield tolerance, and anything else is flagged, never scrapped; a remnant is an ordinary new instance that can
/// be moved and issued again; and the genealogy graph traces a supplier lot through the master roll, a remnant
/// and a second order to what was made, and back. Moves never appear in it.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class V33Tests(PostgresFixture postgres)
{
    private const string Sarah = "B-SARAH-0303";

    private static string Day(int fromToday) => DateTime.UtcNow.AddDays(fromToday).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    private static async Task Rejected(SlittingPlant.Plant plant, HttpClient client, string url, object body, string code, HttpStatusCode status = HttpStatusCode.UnprocessableEntity)
    {
        var response = await plant.Try(client, url, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"{url} -> {(int)response.StatusCode}: {text}");
        Assert.Contains(code, text, StringComparison.Ordinal);
    }

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static async Task<JsonElement> Row(HttpClient client, long order, string lot) =>
        (await Get(client, $"/api/inventory/orders/{order}/reconciliation")).GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("lotNumber").GetString() == lot);

    [Fact]
    public async Task Material_is_accounted_for_and_genealogy_traces_a_supplier_lot_through_a_remnant_and_back()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var e = plant.Engineer;
        await plant.Person("bob", "Buyer");
        await plant.Person("pat", "Buyer", "PurchasingManager");
        await plant.Person("mo", "MaterialHandler");
        await plant.Person("kiosk", "ShopFloorTerminal");
        var (bob, pat, stores, mo, kiosk) = (plant.As("bob"), plant.As("pat"), plant.As("stores"), plant.As("mo"), plant.As("kiosk"));

        // Copper foil bought by the roll; strip made from it; trim is a byproduct.
        foreach (var (part, makeBuy) in new[] { ("FOIL-9", "Buy"), ("STRIP-9", "Make"), ("TRIM-9", "Buy") })
        {
            await plant.Ok(e, "/api/engineering/items", new { partNumber = part, description = part, unitOfMeasure = "M", makeBuy, leadTimeDays = 5, lotTracked = true });
            await plant.Ok(e, $"/api/engineering/items/{part}/revisions", new { revision = "A" });
        }

        await plant.Ok(e, "/api/engineering/items/STRIP-9/revisions/A/bom", new { lineNumber = 1, componentPartNumber = "FOIL-9", quantityPer = 1m, scrapPercent = 0m });
        await plant.Ok(e, "/api/engineering/items/STRIP-9/revisions/A/routing", new { sequence = 10, description = "Slit", workCenterCode = "SLIT-WC", activityCode = "RUN_SLITTER", eligibleMachines = new[] { "SLITTER-A" }, setupMinutes = 0m, runMinutesPerUnit = 0.01m, crewSize = 1 });
        foreach (var part in new[] { "FOIL-9", "STRIP-9", "TRIM-9" })
        {
            await plant.Ok(plant.Approver, $"/api/engineering/items/{part}/revisions/A/release", new { reason = "Baseline" });
        }

        await plant.Ok(bob, "/api/procurement/suppliers", new { code = "MILLCO", name = "Mill Company" });
        var po = Id(await plant.Ok(bob, "/api/procurement/purchase-orders", new { supplier = "MILLCO", lines = new[] { new { partNumber = "FOIL-9", quantity = 1000m, promisedDate = Day(5) } } }));
        await plant.Ok(pat, $"/api/procurement/purchase-orders/{po}/approve", new { reason = "Stock" });
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "FOIL-9", quantity = 1000m, locationCode = "STORES", lotNumber = "M-1", supplierLot = "HEAT-77", poNumber = po, poLine = 1, revision = "A", identityKind = "Roll" });

        // Order 1 slits the master roll.
        var first = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "STRIP-9", quantity = 1000m, dueDate = Day(10), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{first}/release", new { reason = "Customer order" });
        await plant.Ok(stores, "/api/inventory/issues", new { orderNumber = first, partNumber = "FOIL-9", quantity = 1000m, locationCode = "STORES", lotNumber = "M-1" });
        Assert.Equal("NotRecorded", (await Row(mo, first, "M-1")).GetProperty("status").GetString());

        const string url = "/api/inventory/transformations";
        object Record(long order, string? reason, params object[] entries) => new { orderNumber = order, entries, reason };
        await Rejected(plant, mo, url, Record(first, null, new { role = "Scrap", partNumber = "FOIL-9", lotNumber = "M-1", quantity = 5m }), "reason_required");
        await Rejected(plant, mo, url, Record(first, null, new { role = "Consumed", partNumber = "FOIL-9", lotNumber = "M-9", quantity = 5m }), "not_issued");
        await Rejected(plant, mo, url, Record(first, null, new { role = "Consumed", partNumber = "FOIL-9", lotNumber = "M-1", quantity = 1001m }), "over_accounted");
        await Rejected(plant, mo, url, Record(first, null, new { role = "Remnant", partNumber = "FOIL-9", lotNumber = "M-1", quantity = 1m, sourceLot = "M-1", locationCode = "STORES" }), "lot_exists");
        await Rejected(plant, mo, url, Record(first, null, new { role = "Byproduct", partNumber = "STRIP-9", lotNumber = "X-1", quantity = 1m, locationCode = "STORES" }), "use_production_receipt");

        // Consumed 830, a 150 remnant back to stores, 30 of trim as a byproduct (made, not issued); 20 nobody has explained yet.
        await plant.Ok(mo, url, Record(first, null,
            new { role = "Consumed", partNumber = "FOIL-9", lotNumber = "M-1", quantity = 830m },
            new { role = "Remnant", partNumber = "FOIL-9", lotNumber = "M-1-R", quantity = 150m, sourceLot = "M-1", locationCode = "STORES" },
            new { role = "Byproduct", partNumber = "TRIM-9", lotNumber = "T-1", quantity = 30m, locationCode = "STORES" }));
        var m1 = await Row(mo, first, "M-1");
        Assert.Equal(("Unexplained", 20m), (m1.GetProperty("status").GetString(), m1.GetProperty("unexplained").GetDecimal()));
        var flagged = await Get(planner, "/api/reports/material-reconciliation?status=Unexplained");
        Assert.Equal("M-1", Assert.Single(flagged.GetProperty("rows").EnumerateArray()).GetProperty("cells")[2].GetString());

        // The remnant is an ordinary instance: same supplier lot and revision, in stock, and it moves like any other.
        var remnant = (await Get(mo, "/api/inventory/lots?part=FOIL-9")).EnumerateArray().Single(l => l.GetProperty("lotNumber").GetString() == "M-1-R");
        Assert.Equal(("Roll", "A", "HEAT-77"), (remnant.GetProperty("identityKind").GetString(), remnant.GetProperty("partRevision").GetString(), remnant.GetProperty("supplierLot").GetString()));
        await plant.Ok(stores, "/api/inventory/locations", new { code = "RACK-R", siteCode = "PLANT1", name = "Remnant rack", kind = "Stores" });
        await plant.Ok(mo, "/api/inventory/transfers", new { partNumber = "FOIL-9", quantity = 150m, fromLocation = "STORES", toLocation = "RACK-R", lotNumber = "M-1-R", status = "Released", reason = "To the remnant rack" });

        // A tolerance of 1.5% would not cover 20 of 1000; the loss is recorded instead (with a reason), and it balances.
        await plant.Ok(stores, "/api/inventory/yield-tolerance/FOIL-9", new { percent = 1.5m, reason = "Edge trim varies" });
        Assert.Equal("Unexplained", (await Row(mo, first, "M-1")).GetProperty("status").GetString());
        await plant.Ok(mo, url, Record(first, "Edge trim to the chopper", new { role = "Loss", partNumber = "FOIL-9", lotNumber = "M-1", quantity = 20m }));
        Assert.Equal(("Balanced", 0m), ((await Row(mo, first, "M-1")).GetProperty("status").GetString(), (await Row(mo, first, "M-1")).GetProperty("unexplained").GetDecimal()));

        // Order 2 runs the remnant; its strip is received from production.
        var second = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "STRIP-9", quantity = 150m, dueDate = Day(12), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{second}/release", new { reason = "Remnant use" });
        await plant.Ok(stores, "/api/inventory/issues", new { orderNumber = second, partNumber = "FOIL-9", quantity = 150m, locationCode = "RACK-R", lotNumber = "M-1-R" });
        await plant.Ok(mo, url, Record(second, null, new { role = "Consumed", partNumber = "FOIL-9", lotNumber = "M-1-R", quantity = 148m }, new { role = "Remaining", partNumber = "FOIL-9", lotNumber = "M-1-R", quantity = 0m }));
        Assert.Equal("WithinTolerance", (await Row(mo, second, "M-1-R")).GetProperty("status").GetString()); // 2 of 150 within 1.5%

        await plant.Ok(e, "/api/resources/shifts", new { code = "ALL", siteCode = "PLANT1", name = "Around the clock", start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 } });
        await plant.Ok(e, "/api/resources/work-centers/SLIT-WC/shifts", new { shiftCode = "ALL" });
        await plant.Ok(e, $"/api/workforce/workers/{plant.People["sarah"]}/shift", new { shiftCode = "ALL" });
        await plant.Ok(plant.Admin, $"/api/workforce/people/{plant.People["sarah"]}/badge", new { badge = Sarah });
        await plant.Ok(kiosk, "/api/shopfloor/clock-in", new { badge = Sarah });
        await plant.Ok(kiosk, "/api/shopfloor/start", new { badge = Sarah, orderNumber = second, sequence = 10, machineCode = "SLITTER-A" });
        await plant.Ok(kiosk, "/api/shopfloor/report", new { badge = Sarah, orderNumber = second, sequence = 10, good = 148m, scrap = 0m, complete = true });
        await plant.Ok(stores, "/api/inventory/production-receipts", new { orderNumber = second, quantity = 148m, locationCode = "STORES", lotNumber = "S-2" });

        // Back from the strip: order 2, the remnant, the master roll, the receipt from Mill Company with its heat.
        var back = await Get(planner, "/api/genealogy/trace?part=STRIP-9&lot=S-2&direction=Back");
        var labels = back.GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("label").GetString()!).ToList();
        Assert.Contains(labels, l => l.Contains("M-1-R", StringComparison.Ordinal));
        Assert.Contains(labels, l => l.Contains("HEAT-77", StringComparison.Ordinal) && (l.Contains("Mill Company", StringComparison.Ordinal) || l.Contains("MILLCO", StringComparison.Ordinal)));
        var relations = back.GetProperty("edges").EnumerateArray().Select(x => x.GetProperty("relation").GetString()).ToHashSet();
        Assert.Superset(new HashSet<string?> { "Received", "Issued", "Consumed", "Remnant", "Produced" }, relations);
        Assert.DoesNotContain(labels, l => l.Contains("TRIM-9", StringComparison.Ordinal)); // a sibling of the strip's line, not an ancestor

        // Forward from the master roll: both orders, the trim byproduct and the strip; the move to the rack is not there.
        var forward = await Get(planner, "/api/genealogy/trace?part=FOIL-9&lot=M-1&direction=Forward");
        var to = forward.GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("id").GetString()).ToHashSet();
        Assert.Superset(new HashSet<string?> { $"order:{first}", $"order:{second}", "lot:TRIM-9/T-1", "lot:STRIP-9/S-2", "lot:FOIL-9/M-1-R" }, to);
        Assert.Contains(forward.GetProperty("edges").EnumerateArray(), x => x.GetProperty("relation").GetString() == "Lost");
        Assert.DoesNotContain(forward.GetProperty("edges").EnumerateArray(), x => x.GetProperty("relation").GetString() is "Transfer" or "Received");
        var missing = await planner.GetAsync(new Uri("/api/genealogy/trace?lot=NOPE-1", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        // The same as a report, and the Analyst asks it.
        var report = await Get(planner, "/api/reports/genealogy-trace?search=M-1-R&status=Came%20from");
        Assert.Contains(report.GetProperty("rows").EnumerateArray(), r => r.GetProperty("cells")[1].GetString() == "Remnant");
        var answer = await (await planner.PostAsJsonAsync(new Uri("/api/analyst/ask", UriKind.Relative), new { question = "Trace roll M-1-R" })).Content.ReadFromJsonAsync<JsonElement>();
        var request = answer.GetProperty("interpretation").GetProperty("report");
        Assert.Equal(("genealogy-trace", "M-1-R"), (request.GetProperty("reportId").GetString(), request.GetProperty("parameters").GetProperty("search").GetString()));
    }
}
