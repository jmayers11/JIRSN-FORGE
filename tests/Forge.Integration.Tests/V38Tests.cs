using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Forge.Integration.Tests;

/// <summary>
/// V3.8 through the real API: tools are resources with identity, place, custodian, condition, eligibility and uses;
/// a blade's service cycle and life limit stop its use; calibration decides a gauge; validity is judged at the moment
/// of use (and can be asked as of an earlier moment); a shop-floor start can name the tool it uses; and gauges defined
/// in Quality are tools.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class V38Tests(PostgresFixture postgres)
{
    private const string Sarah = "B-SARAH-0308";

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

    private static async Task<JsonElement> Tool(HttpClient client, string code, string query = "") =>
        (await Get(client, "/api/resources/tools" + query)).EnumerateArray().Single(v => v.GetProperty("tool").GetProperty("code").GetString() == code);

    [Fact]
    public async Task A_blade_is_judged_at_the_moment_of_use_through_service_cycles_and_its_life_limit()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("qe", "QualityEngineer");
        var (e, stores, qe) = (plant.Engineer, plant.As("stores"), plant.As("qe"));

        // Types and tools: only those who manage tools register them.
        await plant.Ok(e, "/api/resources/tool-types", new { code = "SLIT-KNIFE", name = "Slitter knife", kind = "Cutting", usageLimit = 5, serviceUses = 2 });
        await Rejected(plant, e, "/api/resources/tool-types", new { code = "BAD", name = "x", kind = "Cutting", usageLimit = 2, serviceUses = 3 }, "invalid_limits");
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(stores, "/api/resources/tools", new { code = "K-9", typeCode = "SLIT-KNIFE", description = "x" })).StatusCode);
        await Rejected(plant, e, "/api/resources/tools", new { code = "K-9", typeCode = "SLIT-KNIFE", description = "x", eligibleMachines = new[] { "NOPE" } }, "unknown_machine");
        await plant.Ok(e, "/api/resources/tools", new { code = "K-1", typeCode = "SLIT-KNIFE", description = "Knife set 100 mm", serial = "KS-0001", location = "TOOL-CRIB", eligibleMachines = new[] { "SLITTER-A" }, eligibleActivities = new[] { "RUN_SLITTER" } });
        await Task.Delay(300);
        var beforeUse = DateTimeOffset.UtcNow;
        await Task.Delay(300);

        // Eligibility, service cycle, life limit.
        const string uses = "/api/resources/tools/K-1/uses";
        await Rejected(plant, e, uses, new { uses = 1, machineCode = "SLITTER-B", activityCode = "RUN_SLITTER" }, "tool_not_valid");
        await plant.Ok(e, uses, new { uses = 2, machineCode = "SLITTER-A", activityCode = "RUN_SLITTER", reference = "trial" });
        await Rejected(plant, e, uses, new { uses = 1, machineCode = "SLITTER-A" }, "due for service");
        await Rejected(plant, stores, "/api/resources/tools/K-1/service", new { vendor = "Sharp Co." }, "reason_required");
        await plant.Ok(stores, "/api/resources/tools/K-1/service", new { vendor = "Sharp Co.", reason = "Two runs since sharpening" });
        var away = await Tool(stores, "K-1");
        Assert.Equal(("OutForService", "AtVendor", "Sharp Co."), (away.GetProperty("tool").GetProperty("status").GetString(), away.GetProperty("tool").GetProperty("custody").GetString(), away.GetProperty("tool").GetProperty("custodian").GetString()));
        await Rejected(plant, e, uses, new { uses = 1, machineCode = "SLITTER-A" }, "out for service");
        await plant.Ok(stores, "/api/resources/tools/K-1/return", new { condition = "Good", location = "TOOL-CRIB" });
        await plant.Ok(e, uses, new { uses = 2, machineCode = "SLITTER-A" });
        await plant.Ok(stores, "/api/resources/tools/K-1/service", new { vendor = "Sharp Co.", reason = "Due" });
        await plant.Ok(stores, "/api/resources/tools/K-1/return", new { condition = "Worn" });
        await plant.Ok(e, uses, new { uses = 1, machineCode = "SLITTER-A" });
        await Rejected(plant, e, uses, new { uses = 1, machineCode = "SLITTER-A" }, "usage limit");

        // Judged as of a moment: before any use it was valid; now it is not.
        var then = await Tool(stores, "K-1", $"?at={Uri.EscapeDataString(beforeUse.ToString("O", CultureInfo.InvariantCulture))}");
        Assert.True(then.GetProperty("validity").GetProperty("valid").GetBoolean());
        var now = await Tool(stores, "K-1");
        Assert.Equal((false, 5), (now.GetProperty("validity").GetProperty("valid").GetBoolean(), now.GetProperty("tool").GetProperty("usageCount").GetInt32()));
        var history = (await Get(stores, "/api/resources/tools/K-1/history")).EnumerateArray().Select(h => h.GetProperty("kind").GetString()).ToList();
        Assert.Equal("Registered,Used,SentForService,Returned,Used,SentForService,Returned,Used", string.Join(",", history));

        // A gauge needs a calibration in force; a failed one stops it.
        await plant.Ok(qe, "/api/resources/tool-types", new { code = "CALIPER", name = "Caliper", kind = "Gauge", calibrationDays = 180 });
        await plant.Ok(qe, "/api/resources/tools", new { code = "CAL-1", typeCode = "CALIPER", description = "Digital caliper 150 mm", calibratedOn = Day(-10), certificate = "LAB-77" });
        Assert.Equal(Day(170), (await Tool(qe, "CAL-1")).GetProperty("tool").GetProperty("calibrationDue").GetString());
        await plant.Ok(qe, "/api/resources/tools/CAL-1/calibrations", new { calibratedOn = Day(0), passed = false, certificate = "LAB-78" });
        Assert.Contains("no calibration in force", (await Tool(qe, "CAL-1")).GetProperty("validity").GetProperty("problems")[0].GetString(), StringComparison.Ordinal);
        await plant.Ok(qe, "/api/resources/tools/CAL-1/calibrations", new { calibratedOn = Day(0), passed = true, certificate = "LAB-79" });
        Assert.True((await Tool(qe, "CAL-1")).GetProperty("validity").GetProperty("valid").GetBoolean());
        await plant.Ok(qe, "/api/resources/tools/CAL-1/uses", new { uses = 1, reference = "inspection" });
        var useHistory = await Get(qe, $"/api/reports/tool-use-history?search=CAL-1&from={Day(-1)}&to={Day(1)}");
        var useRow = Assert.Single(useHistory.GetProperty("rows").EnumerateArray());
        Assert.Equal("CAL-1", useRow.GetProperty("cells")[1].GetString());
        Assert.Equal("Yes", useRow.GetProperty("cells")[3].GetString());
        var historicalAnswer = await (await qe.PostAsJsonAsync(new Uri("/api/analyst/ask", UriKind.Relative), new { question = "Was CAL-1 calibrated when it was used?" })).Content.ReadFromJsonAsync<JsonElement>();
        var historicalRequest = historicalAnswer.GetProperty("interpretation").GetProperty("report");
        Assert.Equal(("tool-use-history", "CAL-1"), (historicalRequest.GetProperty("reportId").GetString(), historicalRequest.GetProperty("parameters").GetProperty("search").GetString()));

        // Gauges defined in Quality are tools of the GAUGE type.
        await plant.Ok(qe, "/api/quality/gauges", new { code = "MIC-9", description = "Micrometer", calibrationDue = Day(30), reason = "New gauge" });
        var mic = await Tool(qe, "MIC-9");
        Assert.Equal(("GAUGE", true), (mic.GetProperty("tool").GetProperty("typeCode").GetString(), mic.GetProperty("validity").GetProperty("valid").GetBoolean()));
        Assert.Contains((await Get(qe, "/api/quality/gauges")).EnumerateArray(), g => g.GetProperty("code").GetString() == "MIC-9");

        // The report and the Analyst.
        var report = await Get(stores, "/api/reports/tool-status?search=K-1");
        Assert.Equal("No", Assert.Single(report.GetProperty("rows").EnumerateArray()).GetProperty("cells")[11].GetString());
        var answer = await (await stores.PostAsJsonAsync(new Uri("/api/analyst/ask", UriKind.Relative), new { question = "Which tools are out for service?" })).Content.ReadFromJsonAsync<JsonElement>();
        var request = answer.GetProperty("interpretation").GetProperty("report");
        Assert.Equal(("tool-status", "OutForService"), (request.GetProperty("reportId").GetString(), request.GetProperty("parameters").GetProperty("status").GetString()));
    }

    [Fact]
    public async Task A_shop_floor_start_names_its_tool_and_a_damaged_tool_stops_it()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("kiosk", "ShopFloorTerminal");
        var (e, stores, kiosk) = (plant.Engineer, plant.As("stores"), plant.As("kiosk"));
        await plant.Ok(e, "/api/resources/tool-types", new { code = "SLIT-KNIFE", name = "Slitter knife", kind = "Cutting", serviceUses = 10 });
        await plant.Ok(e, "/api/resources/tools", new { code = "K-2", typeCode = "SLIT-KNIFE", description = "Knife set", eligibleMachines = new[] { "SLITTER-A" } });
        await plant.Ok(e, "/api/resources/tools", new { code = "K-3", typeCode = "SLIT-KNIFE", description = "Knife set", eligibleMachines = new[] { "SLITTER-A" } });
        await plant.Ok(stores, "/api/resources/tools/K-3/condition", new { condition = "Damaged", reason = "Chipped edge" });

        var order = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "ROLL-100", quantity = 10m, dueDate = Day(10), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Customer order" });
        await plant.Ok(stores, "/api/inventory/issues", new { orderNumber = order, partNumber = "COIL-AL", quantity = 26m, locationCode = "STORES" });
        await plant.Ok(e, "/api/resources/shifts", new { code = "ALL", siteCode = "PLANT1", name = "Around the clock", start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 } });
        await plant.Ok(e, "/api/resources/work-centers/SLIT-WC/shifts", new { shiftCode = "ALL" });
        await plant.Ok(e, $"/api/workforce/workers/{plant.People["sarah"]}/shift", new { shiftCode = "ALL" });
        await plant.Ok(plant.Admin, $"/api/workforce/people/{plant.People["sarah"]}/badge", new { badge = Sarah });
        await plant.Ok(kiosk, "/api/shopfloor/clock-in", new { badge = Sarah });

        await Rejected(plant, kiosk, "/api/shopfloor/start", new { badge = Sarah, orderNumber = order, sequence = 10, machineCode = "SLITTER-A", toolCode = "K-3" }, "damaged");
        await plant.Ok(kiosk, "/api/shopfloor/start", new { badge = Sarah, orderNumber = order, sequence = 10, machineCode = "SLITTER-A", toolCode = "K-2" });
        var used = (await Get(stores, "/api/resources/tools/K-2/history")).EnumerateArray().Last();
        Assert.Equal(("Used", $"order {order}/10"), (used.GetProperty("kind").GetString(), used.GetProperty("reference").GetString()));
    }
}
