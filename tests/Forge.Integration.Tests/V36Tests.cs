using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Forge.Integration.Tests;

/// <summary>
/// V3.6 through the real API: a lot keeps the expiry it was given; an extension needs a second person and the
/// original date stays; a failed retest, an expiry or a storage excursion without a retest since stops a lot being
/// issued and counted for planning; FEFO orders only what the hard rules leave; split lots keep their age and a
/// merge never hides an earlier expiry.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class V36Tests(PostgresFixture postgres)
{
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

    private static async Task<Dictionary<string, JsonElement>> Lives(HttpClient client, string part) =>
        (await Get(client, $"/api/inventory/shelf-life?part={part}&all=true")).EnumerateArray().ToDictionary(v => v.GetProperty("shelfLife").GetProperty("lotNumber").GetString()!, v => v.GetProperty("shelfLife"));

    private static async Task ItemAsync(SlittingPlant.Plant plant, string part, string makeBuy = "Buy")
    {
        await plant.Ok(plant.Engineer, "/api/engineering/items", new { partNumber = part, description = part, unitOfMeasure = "EA", makeBuy, leadTimeDays = 5, lotTracked = makeBuy == "Buy" });
        await plant.Ok(plant.Engineer, $"/api/engineering/items/{part}/revisions", new { revision = "A" });
    }

    private static decimal ForPlanning(JsonElement summary, string part) =>
        summary.EnumerateArray().Single(s => s.GetProperty("partNumber").GetString() == part).GetProperty("availableForPlanning").GetDecimal();

    [Fact]
    public async Task Expiry_extension_retest_and_selection_follow_the_hard_rules_first()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("qe", "QualityEngineer");
        await plant.Person("qm", "QualityManager");
        await plant.Person("lead", "QualityEngineer", "QualityManager");
        await plant.Person("insp", "Inspector");
        var (stores, qe, qm, lead, insp) = (plant.As("stores"), plant.As("qe"), plant.As("qm"), plant.As("lead"), plant.As("insp"));

        // A kit made from an adhesive with a shelf life.
        await ItemAsync(plant, "ADH-1");
        await plant.Ok(plant.Approver, "/api/engineering/items/ADH-1/revisions/A/release", new { reason = "Baseline" });
        await ItemAsync(plant, "KIT-1", "Make");
        await plant.Ok(plant.Engineer, "/api/engineering/items/KIT-1/revisions/A/bom", new { lineNumber = 1, componentPartNumber = "ADH-1", quantityPer = 1m, scrapPercent = 0m });
        await plant.Ok(plant.Engineer, "/api/engineering/items/KIT-1/revisions/A/routing", new { sequence = 10, description = "Kit", workCenterCode = "SLIT-WC", activityCode = "KITTING", setupMinutes = 0m, runMinutesPerUnit = 1m, crewSize = 1 });
        await plant.Ok(plant.Approver, "/api/engineering/items/KIT-1/revisions/A/release", new { reason = "Baseline" });
        var order = Id(await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = "KIT-1", quantity = 50m, dueDate = Day(10), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Customer order" });

        // Quality sets the rule; it needs a reason and the authority.
        const string ruleUrl = "/api/inventory/shelf-life-rules/ADH-1";
        var rule = new { shelfLifeDays = 180, basis = "Manufacture", retestDays = 90, openLifeHours = 8, selection = "Fefo", reason = "Supplier datasheet TDS-12" };
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(stores, ruleUrl, rule)).StatusCode);
        await Rejected(plant, qe, ruleUrl, rule with { reason = "" }, "reason_required");
        await plant.Ok(qe, ruleUrl, rule);

        // Lots get their dates at receipt: 180 days from manufacture, or the earlier date on the paperwork.
        object Receipt(string lot, int madeDaysAgo, string? expiresOn = null) =>
            new { partNumber = "ADH-1", quantity = 20m, locationCode = "STORES", lotNumber = lot, revision = "A", manufacturedOn = Day(-madeDaysAgo), expiresOn, reason = "Opening balance" };
        await Rejected(plant, stores, "/api/inventory/receipts", new { partNumber = "ADH-1", quantity = 1m, locationCode = "STORES", lotNumber = "A-0", revision = "A", reason = "x" }, "manufactured_on_required");
        await plant.Ok(stores, "/api/inventory/receipts", Receipt("A-1", 200));
        await plant.Ok(stores, "/api/inventory/receipts", Receipt("A-2", 100));
        await plant.Ok(stores, "/api/inventory/receipts", Receipt("A-3", 170, Day(5)));
        var lives = await Lives(stores, "ADH-1");
        Assert.Equal(("Expired", Day(-20)), (lives["A-1"].GetProperty("status").GetString(), lives["A-1"].GetProperty("expires").GetString()));
        Assert.Equal(("Ok", Day(80), Day(90)), (lives["A-2"].GetProperty("status").GetString(), lives["A-2"].GetProperty("expires").GetString(), lives["A-2"].GetProperty("retestDue").GetString()));
        Assert.Equal(("Expiring", Day(5)), (lives["A-3"].GetProperty("status").GetString(), lives["A-3"].GetProperty("expires").GetString()));
        Assert.Equal(200, lives["A-1"].GetProperty("ageDays").GetInt32());

        // Expired stock is on hand but not available for planning, and is never issued.
        Assert.Equal(-10m, ForPlanning(await Get(planner, "/api/inventory/summary"), "ADH-1")); // 40 usable less the order's 50
        await Rejected(plant, stores, "/api/inventory/issues", new { orderNumber = order, partNumber = "ADH-1", quantity = 5m, locationCode = "STORES", lotNumber = "A-1" }, "shelf_life_blocked");

        // FEFO orders only what the hard rules leave: A-3 expires first, A-1 is out.
        var selection = await Get(stores, $"/api/inventory/selection?part=ADH-1&order={order}");
        var candidates = selection.GetProperty("candidates").EnumerateArray().ToList();
        Assert.Equal(("Fefo", "A"), (selection.GetProperty("rule").GetString(), selection.GetProperty("requiredRevision").GetString()));
        Assert.Equal(("A-3", 1), (candidates[0].GetProperty("lotNumber").GetString(), candidates[0].GetProperty("rank").GetInt32()));
        Assert.Equal(("A-2", 2), (candidates[1].GetProperty("lotNumber").GetString(), candidates[1].GetProperty("rank").GetInt32()));
        Assert.Contains("Expired", candidates.Single(c => c.GetProperty("lotNumber").GetString() == "A-1").GetProperty("excludedBecause").GetString(), StringComparison.Ordinal);

        // An extension: requested with evidence, decided by someone else; the original date stays.
        const string ext = "/api/inventory/lots/ADH-1/A-1/extensions";
        await Rejected(plant, qe, ext, new { newExpires = Day(-30), evidence = "x", reason = "x" }, "invalid_extension");
        var extension = Id(await plant.Ok(qe, ext, new { newExpires = Day(60), evidence = "Retest of viscosity and cure, report QR-88", reason = "Stock shortage" }));
        await Rejected(plant, qe, ext, new { newExpires = Day(40), evidence = "again", reason = "x" }, "extension_pending");
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(qe, $"/api/inventory/extensions/{extension}/approve", new { reason = "x" })).StatusCode);
        await plant.Ok(qm, $"/api/inventory/extensions/{extension}/approve", new { reason = "QR-88 within datasheet" });
        await Rejected(plant, qm, $"/api/inventory/extensions/{extension}/approve", new { reason = "again" }, "extension_decided");
        var a1 = (await Lives(stores, "ADH-1"))["A-1"];
        Assert.Equal(("Ok", Day(60), Day(-20)), (a1.GetProperty("status").GetString(), a1.GetProperty("expires").GetString(), a1.GetProperty("originalExpires").GetString()));
        await plant.Ok(stores, "/api/inventory/issues", new { orderNumber = order, partNumber = "ADH-1", quantity = 5m, locationCode = "STORES", lotNumber = "A-1" });

        var own = Id(await plant.Ok(lead, "/api/inventory/lots/ADH-1/A-3/extensions", new { newExpires = Day(20), evidence = "Supplier letter", reason = "x" }));
        await Rejected(plant, lead, $"/api/inventory/extensions/{own}/approve", new { reason = "x" }, "separation_of_duties");
        await plant.Ok(qm, $"/api/inventory/extensions/{own}/reject", new { reason = "Letter does not cover this batch" });

        // An open container starts its open life once.
        await plant.Ok(stores, "/api/inventory/lots/ADH-1/A-2/open", new { });
        await Rejected(plant, stores, "/api/inventory/lots/ADH-1/A-2/open", new { }, "already_open");
        Assert.NotEqual(JsonValueKind.Null, (await Lives(stores, "ADH-1"))["A-2"].GetProperty("openExpiresAt").ValueKind);

        // A failed retest stops the lot; a passing retest's next date is after today.
        await Rejected(plant, insp, "/api/inventory/lots/ADH-1/A-2/retests", new { passed = true, nextDue = Day(-1), evidence = "x" }, "invalid_retest_date");
        await plant.Ok(insp, "/api/inventory/lots/ADH-1/A-2/retests", new { passed = false, evidence = "Viscosity out of range" });
        Assert.Equal("RetestFailed", (await Lives(stores, "ADH-1"))["A-2"].GetProperty("status").GetString());
        await Rejected(plant, stores, "/api/inventory/issues", new { orderNumber = order, partNumber = "ADH-1", quantity = 5m, locationCode = "STORES", lotNumber = "A-2" }, "shelf_life_blocked");

        // Split instances keep their age and expiry; a merge never hides an earlier expiry.
        await plant.Ok(stores, "/api/inventory/splits", new { partNumber = "ADH-1", lotNumber = "A-3", locationCode = "STORES", status = "Released", results = new[] { new { lotNumber = "A-3S", quantity = 5m } }, reason = "Half tubes" });
        Assert.Equal(Day(5), (await Lives(stores, "ADH-1"))["A-3S"].GetProperty("expires").GetString());
        await plant.Ok(stores, "/api/inventory/merge-policy/ADH-1", new { allowed = true, sameRevisionRequired = false, sameSupplierLotRequired = false, reason = "Tubes may be combined" });
        await Rejected(plant, stores, "/api/inventory/merges", new { partNumber = "ADH-1", resultLot = "M-1", locationCode = "STORES", status = "Released", sources = new[] { new { lotNumber = "A-3S", quantity = 5m }, new { lotNumber = "A-1", quantity = 5m } }, reason = "Combine" }, "expiry_mismatch");

        // The report and the Analyst.
        var report = await Get(stores, "/api/reports/shelf-life?status=RetestFailed");
        Assert.Equal("A-2", Assert.Single(report.GetProperty("rows").EnumerateArray()).GetProperty("cells")[1].GetString());
        var answer = await (await stores.PostAsJsonAsync(new Uri("/api/analyst/ask", UriKind.Relative), new { question = "Which lots are expiring soon?" })).Content.ReadFromJsonAsync<JsonElement>();
        var request = answer.GetProperty("interpretation").GetProperty("report");
        Assert.Equal(("shelf-life", "Expiring"), (request.GetProperty("reportId").GetString(), request.GetProperty("parameters").GetProperty("status").GetString()));
    }

    [Fact]
    public async Task A_storage_excursion_stops_the_lots_that_were_there_until_retested()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("qe", "QualityEngineer");
        await plant.Person("insp", "Inspector");
        var (stores, qe, insp) = (plant.As("stores"), plant.As("qe"), plant.As("insp"));
        await ItemAsync(plant, "VAC-1");
        await plant.Ok(plant.Approver, "/api/engineering/items/VAC-1/revisions/A/release", new { reason = "Baseline" });
        await plant.Ok(qe, "/api/inventory/shelf-life-rules/VAC-1", new { basis = "Receipt", storageMinC = 2m, storageMaxC = 8m, storageNote = "Refrigerate", selection = "Fifo", reason = "Cold chain" });
        await plant.Ok(stores, "/api/inventory/locations", new { code = "FRIDGE", siteCode = "PLANT1", name = "Cold store", kind = "Stores" });

        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "VAC-1", quantity = 10m, locationCode = "FRIDGE", lotNumber = "V-1", revision = "A", reason = "Opening balance" });
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "VAC-1", quantity = 10m, locationCode = "FRIDGE", lotNumber = "V-2", revision = "A", reason = "Opening balance" });
        await plant.Ok(stores, "/api/inventory/transfers", new { partNumber = "VAC-1", quantity = 10m, fromLocation = "FRIDGE", toLocation = "STORES", lotNumber = "V-2", status = "Released", reason = "To the line" });
        await Task.Delay(300);
        var from = DateTimeOffset.UtcNow;
        await Task.Delay(300);
        var until = DateTimeOffset.UtcNow;
        await Task.Delay(100);

        // Within limits: nothing happens. Outside them: what was in the fridge then needs a retest; V-2 had left.
        await plant.Ok(stores, "/api/inventory/excursions", new { locationCode = "FRIDGE", condition = "Door ajar, logger at 6 °C", measuredC = 6m, startedAt = from, endedAt = until, reason = "Logger alarm" });
        Assert.Equal("NotTracked", (await Lives(stores, "VAC-1"))["V-1"].GetProperty("status").GetString());
        await Rejected(plant, stores, "/api/inventory/excursions", new { locationCode = "FRIDGE", condition = "x", startedAt = from, endedAt = DateTimeOffset.UtcNow.AddHours(1), reason = "x" }, "invalid_time");
        await plant.Ok(stores, "/api/inventory/excursions", new { locationCode = "FRIDGE", condition = "Power cut", measuredC = 15m, startedAt = from, endedAt = until, reason = "Logger alarm" });
        var lives = await Lives(stores, "VAC-1");
        Assert.Equal(("Excursion", "NotTracked"), (lives["V-1"].GetProperty("status").GetString(), lives["V-2"].GetProperty("status").GetString()));
        Assert.Equal(10m, ForPlanning(await Get(planner, "/api/inventory/summary"), "VAC-1"));
        var excluded = (await Get(stores, "/api/inventory/selection?part=VAC-1")).GetProperty("candidates").EnumerateArray().Single(c => c.GetProperty("lotNumber").GetString() == "V-1");
        Assert.Contains("excursion", excluded.GetProperty("excludedBecause").GetString(), StringComparison.Ordinal);

        // A passing retest after the excursion clears it.
        await plant.Ok(insp, "/api/inventory/lots/VAC-1/V-1/retests", new { passed = true, evidence = "Potency assay 98 %" });
        Assert.Equal("Ok", (await Lives(stores, "VAC-1"))["V-1"].GetProperty("status").GetString());
        Assert.Equal(20m, ForPlanning(await Get(planner, "/api/inventory/summary"), "VAC-1"));
    }
}
