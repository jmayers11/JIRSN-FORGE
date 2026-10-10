using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Forge.Integration.Tests;

/// <summary>
/// V3.5 through the real API: a stoppage has one category and one reason code; setups and changeovers carry the
/// minutes the routing expected; a reason code is never a proven root cause, which needs a second person; scrap
/// can name a scrap reason code; rework and repair take good output back without rewriting the production
/// reports, repair only on an approved NCR; and an order's quantity states follow all of it.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class V35Tests(PostgresFixture postgres)
{
    private const string Sarah = "B-SARAH-0305";

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

    private static async Task<long> ReleasedOrderAsync(SlittingPlant.Plant plant, HttpClient planner)
    {
        var order = Id(await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = "ROLL-100", quantity = 100m, dueDate = Day(10), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Customer order" });
        return order;
    }

    private static async Task ReasonCodesAsync(SlittingPlant.Plant plant)
    {
        foreach (var (code, kind, category, description) in new[]
        {
            ("NO-COIL", "Downtime", "Starved", "No coil staged"), ("KNIFE", "Downtime", "Changeover", "Knife change for new width"), ("JAM", "Downtime", "Unplanned", "Web jam"),
            ("EDGE", "Scrap", (string?)null, "Edge wave"), ("RESLIT", "Rework", null, "Re-slit to width"),
        })
        {
            await plant.Ok(plant.Engineer, "/api/execution/reason-codes", new { code, kind, category, description });
        }
    }

    [Fact]
    public async Task Stoppages_have_one_category_and_reason_and_a_root_cause_is_proven_only_by_someone_else()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("sup", "Supervisor");
        await plant.Person("mp", "MaintenancePlanner");
        var (e, sup, mp) = (plant.Engineer, plant.As("sup"), plant.As("mp"));
        var order = await ReleasedOrderAsync(plant, planner);

        // Reason codes: the plant's own words; downtime reasons belong to one category; only engineering defines them.
        const string codes = "/api/execution/reason-codes";
        await Rejected(plant, e, codes, new { code = "X", kind = "Downtime", description = "x" }, "invalid_category");
        await Rejected(plant, e, codes, new { code = "X", kind = "Scrap", category = "Unplanned", description = "x" }, "invalid_category");
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(sup, codes, new { code = "X", kind = "Scrap", description = "x" })).StatusCode);
        await ReasonCodesAsync(plant);
        await Rejected(plant, e, codes, new { code = "JAM", kind = "Scrap", description = "dup" }, "code_exists");

        // One category and one reason that belongs to it; setups and changeovers name the operation they prepare.
        const string url = "/api/execution/stoppages";
        await plant.Ok(sup, url, new { machineCode = "SLITTER-B", category = "Starved", reasonCode = "NO-COIL", note = "Coil still at receiving" });
        await Rejected(plant, sup, url, new { machineCode = "SLITTER-B", category = "Starved", reasonCode = "NO-COIL" }, "machine_already_stopped");
        await Rejected(plant, sup, url, new { machineCode = "SLITTER-A", category = "Unplanned", reasonCode = "NO-COIL" }, "category_mismatch");
        await Rejected(plant, sup, url, new { machineCode = "SLITTER-A", category = "Unplanned", reasonCode = "EDGE" }, "wrong_reason_kind");
        await Rejected(plant, sup, url, new { machineCode = "SLITTER-A", category = "Changeover", reasonCode = "KNIFE" }, "operation_required");
        await Rejected(plant, sup, url, new { machineCode = "PAINT-BOOTH", category = "Changeover", reasonCode = "KNIFE", orderNumber = order, sequence = 10 }, "machine_not_eligible");
        await Rejected(plant, sup, url, new { machineCode = "SLITTER-A", category = "Unplanned", reasonCode = "JAM", startedAt = DateTimeOffset.UtcNow.AddDays(-9) }, "invalid_time");

        // A jam reported after the fact, two hours back; then the changeover, against the routing's 30 minutes of setup.
        var jam = Id(await plant.Ok(sup, url, new { machineCode = "SLITTER-A", category = "Unplanned", reasonCode = "JAM", startedAt = DateTimeOffset.UtcNow.AddHours(-2) }));
        await plant.Ok(sup, $"{url}/{jam}/end", new { endedAt = DateTimeOffset.UtcNow.AddMinutes(-50) });
        await Rejected(plant, sup, $"{url}/{jam}/end", new { }, "stoppage_ended");
        var changeover = await plant.Ok(sup, url, new { machineCode = "SLITTER-A", category = "Changeover", reasonCode = "KNIFE", orderNumber = order, sequence = 10 });
        Assert.Contains("expects 30 min", changeover.GetProperty("message").GetString(), StringComparison.Ordinal);
        await plant.Ok(sup, $"{url}/{Id(changeover)}/end", new { });

        var list = (await Get(sup, $"{url}?days=1")).EnumerateArray().ToDictionary(v => v.GetProperty("stoppage").GetProperty("stoppageId").GetInt64());
        Assert.InRange(list[jam].GetProperty("minutes").GetDecimal(), 69.5m, 70.5m);
        var co = list[Id(changeover)];
        Assert.Equal((30m, "Changeover", order), (co.GetProperty("stoppage").GetProperty("expectedMinutes").GetDecimal(), co.GetProperty("stoppage").GetProperty("category").GetString(), co.GetProperty("stoppage").GetProperty("orderNumber").GetInt64()));
        Assert.True(co.GetProperty("variance").GetDecimal() < 0); // done in less than the routing's 30 minutes
        Assert.Single((await Get(sup, $"{url}?open=true")).EnumerateArray()); // slitter B is still starved

        // A reason code is what was reported. A root cause is proposed with evidence, and proven only by someone else.
        var starved = list.Values.Single(v => v.GetProperty("stoppage").GetProperty("category").GetString() == "Starved").GetProperty("stoppage").GetProperty("stoppageId").GetInt64();
        var cause = Id(await plant.Ok(e, $"{url}/{starved}/root-causes", new { cause = "Receiving inspection backlog held the coil", evidence = "Coil C-88 sat in Inspection 3 h; inspector on another line" }));
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(sup, $"/api/execution/root-causes/{cause}/confirm", new { reason = "x" })).StatusCode);
        await Rejected(plant, e, $"/api/execution/root-causes/{cause}/confirm", new { reason = "Seen it" }, "separation_of_duties");
        await Rejected(plant, mp, $"/api/execution/root-causes/{cause}/confirm", new { }, "reason_required");
        await plant.Ok(mp, $"/api/execution/root-causes/{cause}/confirm", new { reason = "Inspection log and receiving timestamps agree" });
        await Rejected(plant, sup, $"{url}/{starved}/root-causes", new { cause = "Other", evidence = "x" }, "root_cause_proven");
        await plant.Ok(sup, $"{url}/{jam}/root-causes", new { cause = "Splice tape lifted", evidence = "Tape found in nip" });

        // The report keeps reported reasons and proven causes apart; the Analyst asks for it by category.
        var report = await Get(sup, "/api/reports/stoppages?status=Starved");
        var row = Assert.Single(report.GetProperty("rows").EnumerateArray());
        Assert.StartsWith("Proven:", row.GetProperty("cells")[9].GetString(), StringComparison.Ordinal);
        var unplanned = Assert.Single((await Get(sup, "/api/reports/stoppages?status=Unplanned")).GetProperty("rows").EnumerateArray());
        Assert.StartsWith("Proposed:", unplanned.GetProperty("cells")[9].GetString(), StringComparison.Ordinal);
        var answer = await (await sup.PostAsJsonAsync(new Uri("/api/analyst/ask", UriKind.Relative), new { question = "Which machines were starved this week?" })).Content.ReadFromJsonAsync<JsonElement>();
        var request = answer.GetProperty("interpretation").GetProperty("report");
        Assert.Equal(("stoppages", "Starved"), (request.GetProperty("reportId").GetString(), request.GetProperty("parameters").GetProperty("status").GetString()));

        // Retiring a reason keeps what was recorded with it.
        await plant.Ok(e, $"{codes}/JAM", new { description = "Web jam", active = false, reason = "Split into JAM-ENTRY and JAM-EXIT" });
        await Rejected(plant, sup, url, new { machineCode = "SLITTER-A", category = "Unplanned", reasonCode = "JAM" }, "reason_code_retired");
        Assert.Equal("JAM", (await Get(sup, $"{url}?days=1")).EnumerateArray().Single(v => v.GetProperty("stoppage").GetProperty("stoppageId").GetInt64() == jam).GetProperty("stoppage").GetProperty("reasonCode").GetString());
    }

    [Fact]
    public async Task Rework_and_repair_take_output_back_without_rewriting_history_and_quantity_states_follow()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("sup", "Supervisor");
        await plant.Person("kiosk", "ShopFloorTerminal");
        await plant.Person("qe", "QualityEngineer");
        await plant.Person("qm", "QualityManager");
        var (e, sup, kiosk, qe, qm, stores) = (plant.Engineer, plant.As("sup"), plant.As("kiosk"), plant.As("qe"), plant.As("qm"), plant.As("stores"));
        await ReasonCodesAsync(plant);
        var order = await ReleasedOrderAsync(plant, planner);
        await plant.Ok(stores, "/api/inventory/issues", new { orderNumber = order, partNumber = "COIL-AL", quantity = 260m, locationCode = "STORES" });

        await plant.Ok(e, "/api/resources/shifts", new { code = "ALL", siteCode = "PLANT1", name = "Around the clock", start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 } });
        await plant.Ok(e, "/api/resources/work-centers/SLIT-WC/shifts", new { shiftCode = "ALL" });
        await plant.Ok(e, $"/api/workforce/workers/{plant.People["sarah"]}/shift", new { shiftCode = "ALL" });
        await plant.Ok(plant.Admin, $"/api/workforce/people/{plant.People["sarah"]}/badge", new { badge = Sarah });
        await plant.Ok(kiosk, "/api/shopfloor/clock-in", new { badge = Sarah });
        await plant.Ok(kiosk, "/api/shopfloor/start", new { badge = Sarah, orderNumber = order, sequence = 10, machineCode = "SLITTER-A" });

        // Scrap may name a scrap reason code; a rework code is not a scrap reason.
        await Rejected(plant, kiosk, "/api/shopfloor/report", new { badge = Sarah, orderNumber = order, sequence = 10, good = 0m, scrap = 4m, scrapReasonCode = "RESLIT", complete = false }, "wrong_reason_kind");
        await plant.Ok(kiosk, "/api/shopfloor/report", new { badge = Sarah, orderNumber = order, sequence = 10, good = 96m, scrap = 4m, scrapReasonCode = "EDGE", complete = true });
        await plant.Ok(kiosk, "/api/shopfloor/start", new { badge = Sarah, orderNumber = order, sequence = 20 });
        await plant.Ok(kiosk, "/api/shopfloor/report", new { badge = Sarah, orderNumber = order, sequence = 20, good = 96m, scrap = 0m, complete = true });
        await plant.Ok(stores, "/api/inventory/production-receipts", new { orderNumber = order, quantity = 50m, locationCode = "STORES" });

        var states = await Get(sup, $"/api/orders/{order}/quantity-states");
        decimal S(JsonElement q, string state) => q.GetProperty("states").EnumerateArray().Single(s => s.GetProperty("state").GetString() == state).GetProperty("quantity").GetDecimal();
        Assert.Equal((100m, 100m, 96m, 4m, 50m, 46m), (S(states, "Ordered"), S(states, "Started"), S(states, "Produced"), S(states, "Scrapped"), S(states, "Received"), S(states, "Produced, not received")));

        // Rework: why (a rework reason or an NCR), from which operation, never more than its good output.
        const string url = "/api/quality/rework";
        await Rejected(plant, sup, url, new { kind = "Rework", orderNumber = order, sequence = 20, quantity = 10m, instructions = "Re-slit", reasonCode = "RESLIT" }, "reason_required");
        await Rejected(plant, sup, url, new { kind = "Rework", orderNumber = order, sequence = 20, quantity = 10m, instructions = "Re-slit", reason = "Width" }, "reason_required");
        await Rejected(plant, sup, url, new { kind = "Rework", orderNumber = order, sequence = 20, quantity = 200m, instructions = "Re-slit", reasonCode = "RESLIT", reason = "Width" }, "over_rework");
        await Rejected(plant, sup, url, new { kind = "Rework", orderNumber = order, sequence = 20, quantity = 10m, instructions = "Re-slit", reasonCode = "EDGE", reason = "Width" }, "wrong_reason_kind");
        await Rejected(plant, sup, url, new { kind = "Repair", orderNumber = order, sequence = 20, quantity = 4m, instructions = "Patch", reasonCode = "RESLIT", reason = "Width" }, "ncr_required");
        var rework = Id(await plant.Ok(sup, url, new { kind = "Rework", orderNumber = order, sequence = 20, quantity = 10m, instructions = "Re-slit to 100 mm", reasonCode = "RESLIT", reason = "Width over on 10 rolls" }));

        // Repair needs an NCR whose repair disposition a second person approved.
        var ncr = Id(await plant.Ok(qe, "/api/quality/ncrs", new { partNumber = "ROLL-100", orderNumber = order, sequence = 20, quantity = 4m, defect = "Crushed core", description = "Core crushed on 4 rolls" }));
        await plant.Ok(qe, $"/api/quality/ncrs/{ncr}/disposition", new { disposition = "Repair", note = "Re-core; usable, core not to drawing", reason = "Customer accepts re-cored rolls" });
        var ncrRow = (await Get(qe, "/api/quality/ncrs")).EnumerateArray().Single(n => n.GetProperty("ncrId").GetInt64() == ncr);
        Assert.Equal(("AwaitingApproval", "Repair"), (ncrRow.GetProperty("status").GetString(), ncrRow.GetProperty("disposition").GetString()));
        object Repair(decimal quantity) => new { kind = "Repair", orderNumber = order, sequence = 20, quantity, instructions = "Re-core", ncrId = ncr, reason = "NCR repair" };
        await Rejected(plant, sup, url, Repair(4m), "ncr_not_dispositioned");
        await Rejected(plant, qe, $"/api/quality/ncrs/{ncr}/approve", new { reason = "x" }, "", HttpStatusCode.Forbidden);
        await plant.Ok(qm, $"/api/quality/ncrs/{ncr}/approve", new { reason = "Concession RC-7" });
        await Rejected(plant, sup, url, Repair(5m), "over_ncr");
        await plant.Ok(sup, url, Repair(4m));

        states = await Get(sup, $"/api/orders/{order}/quantity-states");
        Assert.Equal((82m, 10m, 4m, 4m, 0m), (S(states, "Produced"), S(states, "Rework"), S(states, "Repair"), S(states, "Scrapped"), S(states, "In process")));

        // Closing says what came out good and what was scrapped; the production report stays as recorded.
        await Rejected(plant, sup, $"{url}/{rework}/close", new { goodQuantity = 8m, scrapQuantity = 1m, note = "x" }, "quantity_mismatch");
        await plant.Ok(sup, $"{url}/{rework}/close", new { goodQuantity = 8m, scrapQuantity = 2m, note = "Two rolls too narrow after re-slit" });
        await Rejected(plant, sup, $"{url}/{rework}/close", new { goodQuantity = 10m, scrapQuantity = 0m, note = "x" }, "rework_closed");
        states = await Get(sup, $"/api/orders/{order}/quantity-states");
        Assert.Equal((90m, 0m, 4m, 6m), (S(states, "Produced"), S(states, "Rework"), S(states, "Repair"), S(states, "Scrapped")));
        var ops = (await Get(sup, $"/api/orders/{order}")).GetProperty("operations").EnumerateArray().ToList();
        Assert.Equal(96m, ops.Single(o => o.GetProperty("sequence").GetInt32() == 20).GetProperty("goodQuantity").GetDecimal());

        // What is in repair is not there to receive.
        await Rejected(plant, stores, "/api/inventory/production-receipts", new { orderNumber = order, quantity = 41m, locationCode = "STORES" }, "over_receipt");

        // The report and the Analyst.
        var report = await Get(sup, $"/api/reports/quantity-states?search={order}");
        var row = Assert.Single(report.GetProperty("rows").EnumerateArray());
        Assert.Equal((90m, 4m, 6m), (row.GetProperty("cells")[5].GetDecimal(), row.GetProperty("cells")[9].GetDecimal(), row.GetProperty("cells")[15].GetDecimal()));
        var answer = await (await sup.PostAsJsonAsync(new Uri("/api/analyst/ask", UriKind.Relative), new { question = $"How much of order {order} is in rework?" })).Content.ReadFromJsonAsync<JsonElement>();
        var request = answer.GetProperty("interpretation").GetProperty("report");
        Assert.Equal(("quantity-states", order.ToString(CultureInfo.InvariantCulture)), (request.GetProperty("reportId").GetString(), request.GetProperty("parameters").GetProperty("search").GetString()));
    }
}
