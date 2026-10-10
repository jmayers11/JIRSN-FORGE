using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Forge.Integration.Tests;

/// <summary>
/// V3.1 through the real API: locations form a hierarchy, serve a work center or machine, and say who has custody;
/// stock on hand, released, available for planning and at the point of use are separate figures; stock in a
/// supplier's custody counts for nothing here until it comes back, and MRP nets only what is available for planning.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class V31Tests(PostgresFixture postgres)
{
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

    private static JsonElement Location(JsonElement locations, string code) => locations.EnumerateArray().Single(l => l.GetProperty("code").GetString() == code);

    [Fact]
    public async Task Locations_form_a_hierarchy_serve_a_machine_and_say_who_has_custody()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("bob", "Buyer");
        var (stores, bob) = (plant.As("stores"), plant.As("bob"));
        await plant.Ok(bob, "/api/procurement/suppliers", new { code = "COATCO", name = "Coating Co." });
        const string url = "/api/inventory/locations";
        object Loc(string code, string kind, string? parent = null, string? level = null, string? machine = null, string? workCenter = null, string? custodian = null, bool? nettable = null) =>
            new { code, siteCode = "PLANT1", name = code, kind, parentCode = parent, level, machineCode = machine, workCenterCode = workCenter, custodianSupplier = custodian, nettable };

        // A hierarchy: area, rack, bin; each narrower than what it sits in, never inside itself.
        await plant.Ok(stores, url, Loc("WH1", "Stores"));
        await plant.Ok(stores, url, Loc("WH1-R4", "Stores", "WH1", "Rack"));
        await plant.Ok(stores, url, Loc("WH1-R4-B12", "Stores", "WH1-R4", "Bin"));
        await Rejected(plant, stores, url, Loc("WH1-Z", "Stores", "WH1-R4", "Zone"), "invalid_level");
        await Rejected(plant, stores, url, Loc("WH1-X", "Stores", "NOPE"), "unknown_parent");
        await Rejected(plant, stores, $"{url}/WH1", new { name = "WH1", parentCode = "WH1-R4-B12", level = "Position", nettable = true }, "reason_required");
        await Rejected(plant, stores, $"{url}/WH1", new { name = "WH1", parentCode = "WH1-R4-B12", level = "Position", nettable = true, reason = "Reorganise" }, "location_cycle");
        await Rejected(plant, stores, $"{url}/WH1/retire", new { reason = "Closing" }, "location_has_children");

        // Point of use: a machine-side location takes its work center from the machine; staging names one.
        await Rejected(plant, stores, url, Loc("SL-B-SIDE", "MachineSide"), "machine_required");
        await plant.Ok(stores, url, Loc("SL-B-SIDE", "MachineSide", machine: "SLITTER-B"));
        await Rejected(plant, stores, url, Loc("STAGE-1", "Staging"), "work_center_required");
        await Rejected(plant, stores, url, Loc("RACK-WC", "Stores", workCenter: "SLIT-WC"), "work_center_not_allowed");

        // Custody: an outside processor is a supplier holding company-owned material, never counted for planning.
        await Rejected(plant, stores, url, Loc("COAT", "ExternalProcessor"), "custodian_required");
        await Rejected(plant, stores, url, Loc("COAT", "ExternalProcessor", custodian: "COATCO", nettable: true), "away_not_nettable");
        await Rejected(plant, stores, url, Loc("COAT", "Stores", custodian: "COATCO"), "custodian_not_allowed");
        await plant.Ok(stores, url, Loc("COAT", "ExternalProcessor", custodian: "COATCO"));
        await plant.Ok(stores, url, Loc("QUAR-1", "Quarantine"));

        var locations = await Get(stores, url);
        var side = Location(locations, "SL-B-SIDE");
        Assert.Equal(("SLIT-WC", "SLITTER-B", true), (side.GetProperty("workCenterCode").GetString(), side.GetProperty("machineCode").GetString(), side.GetProperty("pointOfUse").GetBoolean()));
        var coat = Location(locations, "COAT");
        Assert.Equal(("Supplier", false, false), (coat.GetProperty("custody").GetString(), coat.GetProperty("nettable").GetBoolean(), coat.GetProperty("inCompanyCustody").GetBoolean()));
        Assert.False(Location(locations, "QUAR-1").GetProperty("nettable").GetBoolean());
        Assert.Equal(("Rack", "WH1"), (Location(locations, "WH1-R4").GetProperty("level").GetString(), Location(locations, "WH1-R4").GetProperty("parentCode").GetString()));

        // Changes need a reason, are audited, and the processor of a location holding stock never changes by edit.
        await plant.Ok(stores, $"{url}/QUAR-1", new { name = "Quarantine cage", parentCode = "WH1", level = "Zone", nettable = false, reason = "Cage moved into WH1" });
        Assert.Equal("WH1", Location(await Get(stores, url), "QUAR-1").GetProperty("parentCode").GetString());
    }

    [Fact]
    public async Task On_hand_released_available_for_planning_and_at_point_of_use_are_separate_and_custody_blocks_use()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("bob", "Buyer");
        await plant.Person("ivy", "InventoryController");
        var (stores, bob, ivy) = (plant.As("stores"), plant.As("bob"), plant.As("ivy"));
        await plant.Ok(plant.Engineer, "/api/engineering/items", new { partNumber = "TAPE-1", description = "Splice tape", unitOfMeasure = "EA", makeBuy = "Buy", leadTimeDays = 5, lotTracked = true });
        await plant.Ok(plant.Engineer, "/api/engineering/items/TAPE-1/revisions", new { revision = "A" });
        await plant.Ok(plant.Approver, "/api/engineering/items/TAPE-1/revisions/A/release", new { reason = "Baseline" });
        await plant.Ok(bob, "/api/procurement/suppliers", new { code = "COATCO", name = "Coating Co." });
        const string url = "/api/inventory/locations";
        await plant.Ok(stores, url, new { code = "WH1", siteCode = "PLANT1", name = "Warehouse 1", kind = "Stores" });
        await plant.Ok(stores, url, new { code = "WH1-B12", siteCode = "PLANT1", name = "Bin 12", kind = "Stores", parentCode = "WH1", level = "Bin" });
        await plant.Ok(stores, url, new { code = "SL-B-SIDE", siteCode = "PLANT1", name = "Beside slitter B", kind = "MachineSide", machineCode = "SLITTER-B" });
        await plant.Ok(stores, url, new { code = "COAT", siteCode = "PLANT1", name = "Coating Co.", kind = "ExternalProcessor", custodianSupplier = "COATCO" });

        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "TAPE-1", quantity = 100m, locationCode = "WH1-B12", lotNumber = "T-1", inspect = false, reason = "Opening balance" });
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "TAPE-1", quantity = 20m, locationCode = "WH1-B12", lotNumber = "T-2", inspect = true, reason = "Opening balance" });
        await plant.Ok(stores, "/api/inventory/transfers", new { partNumber = "TAPE-1", quantity = 30m, fromLocation = "WH1-B12", toLocation = "SL-B-SIDE", lotNumber = "T-1", status = "Released" });
        await plant.Ok(stores, "/api/inventory/transfers", new { partNumber = "TAPE-1", quantity = 25m, fromLocation = "WH1-B12", toLocation = "COAT", lotNumber = "T-1", status = "Released", reason = "Out for coating" });

        // Material in a supplier's custody is the company's, but nobody here can receive, count, scrap or ship it.
        await Rejected(plant, stores, "/api/inventory/scrap", new { partNumber = "TAPE-1", quantity = 1m, locationCode = "COAT", lotNumber = "T-1", status = "Released", reason = "Damaged" }, "not_in_custody");
        await Rejected(plant, stores, "/api/inventory/counts", new { partNumber = "TAPE-1", locationCode = "COAT", lotNumber = "T-1", status = "Released", counted = 24m }, "not_in_custody");
        await Rejected(plant, stores, "/api/inventory/receipts", new { partNumber = "TAPE-1", quantity = 1m, locationCode = "COAT", lotNumber = "T-9", reason = "x" }, "not_in_custody");

        // The figures, each its own.
        var tape = (await Get(planner, "/api/inventory/summary")).EnumerateArray().Single(s => s.GetProperty("partNumber").GetString() == "TAPE-1");
        decimal F(string name) => tape.GetProperty(name).GetDecimal();
        Assert.Equal((120m, 100m, 20m, 0m), (F("onHand"), F("released"), F("inspection"), F("hold")));
        Assert.Equal((25m, 30m, 0m, 75m), (F("notNettable"), F("atPointOfUse"), F("allocated"), F("availableForPlanning")));

        var mrpItem = (await Get(planner, "/api/planning/mrp?part=TAPE-1")).GetProperty("output").GetProperty("items").EnumerateArray().Single(i => i.GetProperty("partNumber").GetString() == "TAPE-1");
        Assert.Equal((75m, 20m), (mrpItem.GetProperty("available").GetDecimal(), mrpItem.GetProperty("inspection").GetDecimal()));

        // Positions: the lot's path, custody and point of use, by lot or by location (a parent finds what sits in it).
        var t1 = (await Get(stores, "/api/inventory/positions?lot=T-1")).EnumerateArray().ToDictionary(p => p.GetProperty("locationCode").GetString()!);
        Assert.Equal(3, t1.Count);
        Assert.Equal(("WH1 / WH1-B12", 45m), (t1["WH1-B12"].GetProperty("locationPath").GetString(), t1["WH1-B12"].GetProperty("quantity").GetDecimal()));
        Assert.Equal(("Supplier", "COATCO", false), (t1["COAT"].GetProperty("custody").GetString(), t1["COAT"].GetProperty("custodianSupplier").GetString(), t1["COAT"].GetProperty("availableForPlanning").GetBoolean()));
        Assert.Equal(("SLIT-WC", "SLITTER-B"), (t1["SL-B-SIDE"].GetProperty("pointOfUseWorkCenter").GetString(), t1["SL-B-SIDE"].GetProperty("machineCode").GetString()));
        Assert.Equal(2, (await Get(stores, "/api/inventory/positions?location=WH1")).GetArrayLength()); // T-1 and T-2 in WH1's bin

        // The same as a report, searched by custody; the Analyst asks it for the lot.
        var report = await Get(planner, "/api/reports/stock-positions?search=Outside%20processor");
        var row = Assert.Single(report.GetProperty("rows").EnumerateArray());
        Assert.Equal(25m, row.GetProperty("cells")[9].GetDecimal());
        var answer = await (await planner.PostAsJsonAsync(new Uri("/api/analyst/ask", UriKind.Relative), new { question = "Where is lot T-1?" })).Content.ReadFromJsonAsync<JsonElement>();
        var request = answer.GetProperty("interpretation").GetProperty("report");
        Assert.Equal(("stock-positions", "T-1"), (request.GetProperty("reportId").GetString(), request.GetProperty("parameters").GetProperty("search").GetString()));

        // Back from the processor by transfer: it counts again, with the same lot.
        await plant.Ok(stores, "/api/inventory/transfers", new { partNumber = "TAPE-1", quantity = 25m, fromLocation = "COAT", toLocation = "WH1-B12", lotNumber = "T-1", status = "Released", reason = "Back from coating" });
        tape = (await Get(planner, "/api/inventory/summary")).EnumerateArray().Single(s => s.GetProperty("partNumber").GetString() == "TAPE-1");
        Assert.Equal((0m, 100m), (tape.GetProperty("notNettable").GetDecimal(), tape.GetProperty("availableForPlanning").GetDecimal()));
        Assert.Contains((await Get(ivy, "/api/inventory/stock?part=TAPE-1")).EnumerateArray(), b => b.GetProperty("status").GetString() == "Released" && b.GetProperty("lotNumber").GetString() == "T-1");
    }
}
