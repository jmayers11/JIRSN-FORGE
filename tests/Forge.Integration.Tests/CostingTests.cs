using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Forge.Integration.Tests;

/// <summary>
/// Sprint 10 WW-S10-24 through the real API (owner decision 2026-10-25: US GAAP practice, standard cost with full absorption, kept by a
/// Cost Accountant). Eight bad rolls are split into five scrapped and three reworked: the order's quantities, the NCRs and the standard
/// value all reconcile, the rework's added labor, overhead and material are costed, and a later cost change never rewrites the valuation.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CostingTests(PostgresFixture postgres)
{
    private const string Sarah = "B-SARAH-COST";

    private static string Day(int fromToday) => DateTime.UtcNow.AddDays(fromToday).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task Rejected(SlittingPlant.Plant plant, HttpClient client, string url, object body, string code)
    {
        var response = await plant.Try(client, url, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{url} -> {(int)response.StatusCode}: {text}");
        Assert.Contains(code, text, StringComparison.Ordinal);
    }

    private static decimal State(JsonElement states, string state) =>
        states.GetProperty("states").EnumerateArray().Single(s => s.GetProperty("state").GetString() == state).GetProperty("quantity").GetDecimal();

    [Fact]
    public async Task A_split_disposition_reconciles_quantities_and_standard_cost_and_costs_the_rework()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var e = plant.Engineer;
        foreach (var (subject, roles) in new[] { ("cost", new[] { "CostAccountant" }), ("sup", new[] { "Supervisor" }), ("kiosk", new[] { "ShopFloorTerminal" }),
            ("qe", new[] { "QualityEngineer" }), ("qm", new[] { "QualityManager" }), ("mq", new[] { "ManufacturingEngineer" }) })
            await plant.Person(subject, roles);
        var (cost, sup, kiosk, qe, qm, mq, stores) = (plant.As("cost"), plant.As("sup"), plant.As("kiosk"), plant.As("qe"), plant.As("qm"), plant.As("mq"), plant.As("stores"));

        // The plant starts on standard cost in US dollars; only the cost accountant changes it, and only to a method Forge can value.
        var policy = Assert.Single((await Get(planner, "/api/costing/policies")).EnumerateArray());
        Assert.Equal(("Standard", "USD"), (policy.GetProperty("method").GetString(), policy.GetProperty("currency").GetString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await kiosk.GetAsync(new Uri("/api/costing/policies", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(planner, "/api/costing/policies", new { method = "Standard", laborRatePerHour = 1m, reason = "x" })).StatusCode);
        await Rejected(plant, cost, "/api/costing/policies", new { method = "WeightedAverage", reason = "Try average" }, "method_not_available");
        await plant.Ok(cost, "/api/costing/policies", new { method = "Standard", currency = "USD", laborRatePerHour = 30m, overheadRatePerHour = 45m, reason = "FY2026 rates" });
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(planner, "/api/costing/standard-costs", new { partNumber = "ROLL-100", material = 1m, labor = 1m, overhead = 1m, reason = "x" })).StatusCode);
        await plant.Ok(e, "/api/engineering/items", new { partNumber = "CONN-2", description = "Two-pin connector", unitOfMeasure = "EA", makeBuy = "Buy", leadTimeDays = 4 });
        await plant.Ok(e, "/api/engineering/items/CONN-2/revisions", new { revision = "A" });
        await plant.Ok(plant.Approver, "/api/engineering/items/CONN-2/revisions/A/release", new { reason = "Baseline" });
        await plant.Ok(cost, "/api/costing/standard-costs", new { partNumber = "ROLL-100", material = 10m, labor = 4m, overhead = 6m, reason = "FY2026 standard" });
        await plant.Ok(cost, "/api/costing/standard-costs", new { partNumber = "CONN-2", material = 1.5m, labor = 0m, overhead = 0.25m, reason = "FY2026 standard" });

        // 100 rolls through both operations: 96 good.
        await plant.Ok(e, "/api/execution/reason-codes", new { code = "EDGE", kind = "Scrap", description = "Edge wave" });
        var order = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "ROLL-100", quantity = 100m, dueDate = Day(10), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Customer order" });
        await plant.Ok(stores, "/api/inventory/issues", new { orderNumber = order, partNumber = "COIL-AL", quantity = 260m, locationCode = "STORES" });
        await plant.Ok(e, "/api/resources/shifts", new { code = "ALL", siteCode = "PLANT1", name = "Around the clock", start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 } });
        await plant.Ok(e, "/api/resources/work-centers/SLIT-WC/shifts", new { shiftCode = "ALL" });
        await plant.Ok(e, $"/api/workforce/workers/{plant.People["sarah"]}/shift", new { shiftCode = "ALL" });
        await plant.Ok(plant.Admin, $"/api/workforce/people/{plant.People["sarah"]}/badge", new { badge = Sarah });
        await plant.Ok(kiosk, "/api/shopfloor/clock-in", new { badge = Sarah });
        await plant.Ok(kiosk, "/api/shopfloor/start", new { badge = Sarah, orderNumber = order, sequence = 10, machineCode = "SLITTER-A" });
        await plant.Ok(kiosk, "/api/shopfloor/report", new { badge = Sarah, orderNumber = order, sequence = 10, good = 96m, scrap = 4m, scrapReasonCode = "EDGE", complete = true });
        await plant.Ok(kiosk, "/api/shopfloor/start", new { badge = Sarah, orderNumber = order, sequence = 20 });
        await plant.Ok(kiosk, "/api/shopfloor/report", new { badge = Sarah, orderNumber = order, sequence = 20, good = 96m, scrap = 0m, complete = true });

        // Eight rolls fail; five cannot be saved and three can. The NCR is split so each part takes its own disposition.
        var ncr = Id(await plant.Ok(qe, "/api/quality/ncrs", new { partNumber = "ROLL-100", orderNumber = order, sequence = 20, quantity = 8m, defect = "Core crushed", description = "Crushed cores on 8 rolls" }));
        await Rejected(plant, qe, $"/api/quality/ncrs/{ncr}/split", new { quantity = 8m, reason = "All" }, "invalid_quantity");
        var scrapNcr = Id(await plant.Ok(qe, $"/api/quality/ncrs/{ncr}/split", new { quantity = 5m, reason = "Five are beyond repair" }));
        var ncrs = (await Get(qe, "/api/quality/ncrs")).EnumerateArray().ToDictionary(n => n.GetProperty("ncrId").GetInt64());
        Assert.Equal((3m, 5m, ncr), (ncrs[ncr].GetProperty("quantity").GetDecimal(), ncrs[scrapNcr].GetProperty("quantity").GetDecimal(), ncrs[scrapNcr].GetProperty("parentNcrId").GetInt64()));

        await plant.Ok(qe, $"/api/quality/ncrs/{scrapNcr}/disposition", new { disposition = "Scrap", note = "Cores crushed through", reason = "MRB" });
        await plant.Ok(qe, $"/api/quality/ncrs/{ncr}/disposition", new { disposition = "Rework", note = "Re-core and retest", reason = "MRB" });
        Assert.Contains("over_ncr", await (await plant.Try(sup, "/api/quality/rework", new { kind = "Scrap", orderNumber = order, sequence = 20, quantity = 6m, instructions = "Scrap", ncrId = scrapNcr, reason = "NCR scrap" })).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await plant.Ok(sup, "/api/quality/rework", new { kind = "Scrap", orderNumber = order, sequence = 20, quantity = 5m, instructions = "Scrap the crushed rolls", ncrId = scrapNcr, reason = "NCR scrap" });
        var rework = Id(await plant.Ok(sup, "/api/quality/rework", new { kind = "Rework", orderNumber = order, sequence = 20, quantity = 3m, instructions = "Re-core", ncrId = ncr, reason = "NCR rework" }));
        var plan = await plant.Ok(mq, $"/api/quality/rework/{rework}/plans", new
        {
            operations = new[] { new { sequence = 30, description = "Re-core", workCenterCode = "ASSY-WC", activityCode = "ASSEMBLE", eligibleMachines = Array.Empty<string>(),
                requiredQualifications = Array.Empty<string>(), setupMinutes = 15m, runMinutesPerUnit = 3m, crewSize = 1, inspection = false } },
            materials = new[] { new { partNumber = "CONN-2", quantity = 3m, consumedAtSequence = 30 } }, trainees = Array.Empty<Guid>(),
            rationale = "New core and connector", reason = "Rework plan",
        });
        await plant.Ok(qm, $"/api/quality/rework-plans/{plan.GetProperty("entityId").GetString()}/decide", new { approve = true, note = "Approved", reason = "MRB" });

        // Order quantities: 96 produced less the 8 taken back (3 in rework, 5 scrapped); scrap is the 4 slit edges plus the 5.
        var states = await Get(sup, $"/api/orders/{order}/quantity-states");
        Assert.Equal((88m, 3m, 9m), (State(states, "Produced"), State(states, "Rework"), State(states, "Scrapped")));

        // Cost: 8 x 20.00 = 160.00, split 100.00 lost to scrap and 60.00 retained in rework; the re-core adds 0.4 h at 75.00 and 3 connectors.
        var url = $"/api/costing/ncrs/{scrapNcr}/reconciliation";
        Assert.Equal(HttpStatusCode.Forbidden, (await qe.GetAsync(new Uri(url, UriKind.Relative))).StatusCode);
        var r = await Get(cost, url);
        Assert.Equal((ncr, 20m, 8m, 160m), (r.GetProperty("rootNcrId").GetInt64(), r.GetProperty("unitStandardCost").GetDecimal(), r.GetProperty("originalQuantity").GetDecimal(), r.GetProperty("originalValue").GetDecimal()));
        var portions = r.GetProperty("portions").EnumerateArray().ToDictionary(p => p.GetProperty("ncrId").GetInt64());
        Assert.Equal(("Retained (rework)", 60m), (portions[ncr].GetProperty("treatment").GetString(), portions[ncr].GetProperty("value").GetDecimal()));
        Assert.Equal(("Loss", 100m), (portions[scrapNcr].GetProperty("treatment").GetString(), portions[scrapNcr].GetProperty("value").GetDecimal()));
        Assert.Equal((100m, 5m, true, true), (r.GetProperty("scrapLoss").GetDecimal(), r.GetProperty("scrapQuantityRecorded").GetDecimal(),
            r.GetProperty("quantitiesReconcile").GetBoolean(), r.GetProperty("valuesReconcile").GetBoolean()));
        var added = Assert.Single(r.GetProperty("rework").EnumerateArray());
        Assert.Equal((0.4m, 30m, 5.25m), (added.GetProperty("laborHours").GetDecimal(), added.GetProperty("laborAndOverhead").GetDecimal(), added.GetProperty("material").GetDecimal()));

        // A later standard cost applies from then on; the NCR stays valued at the cost in force when it was raised.
        await plant.Ok(cost, "/api/costing/standard-costs", new { partNumber = "ROLL-100", material = 12m, labor = 5m, overhead = 8m, reason = "Mid-year revaluation" });
        Assert.Equal(160m, (await Get(cost, url)).GetProperty("originalValue").GetDecimal());
        Assert.Equal(2, (await Get(cost, "/api/costing/standard-costs?part=ROLL-100")).GetArrayLength());
    }
}
