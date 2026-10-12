using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ToolUseSegregationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Real_private_program_tool_and_gauge_references_follow_current_access_in_history_reports_and_analyst()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("tool-private-programs", "ProgramManager");
        await plant.Person("tool-private-quality", "QualityEngineer");
        await plant.Person("tool-private-inspector", "Inspector");
        await plant.Person("tool-private-reader", "QualityEngineer");
        await plant.Person("tool-private-kiosk", "ShopFloorTerminal");
        var quality = plant.As("tool-private-quality");
        var inspector = plant.As("tool-private-inspector");
        var reader = plant.As("tool-private-reader");
        var kiosk = plant.As("tool-private-kiosk");
        var program = Guid.Parse((await plant.Ok(plant.As("tool-private-programs"), "/api/programs/", new { code = "TOOL-PRIVATE", name = "Private operation evidence" })).GetProperty("entityId").GetString()!);
        foreach (var subject in new[] { "eng", "planner", "stores", "sarah", "tool-private-quality", "tool-private-inspector", "tool-private-reader", "tool-private-kiosk" })
            await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People[subject]}/programs", new { programId = program, reason = "Permit the real private reference operation" });
        await plant.Ok(plant.Engineer, "/api/resources/shifts", new { code = "TOOL-ALL", siteCode = "PLANT1", name = "Private tool reference shift", start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 } });
        await plant.Ok(plant.Engineer, "/api/resources/work-centers/SLIT-WC/shifts", new { shiftCode = "TOOL-ALL" });
        await plant.Ok(plant.Engineer, $"/api/workforce/workers/{plant.People["sarah"]}/shift", new { shiftCode = "TOOL-ALL" });
        const string badge = "TOOL-PRIVATE-SARAH";
        await plant.Ok(plant.Admin, $"/api/workforce/people/{plant.People["sarah"]}/badge", new { badge });
        await plant.Ok(quality, "/api/quality/gauges", new { code = "PRIV-GAUGE", description = "Private operation inspection gauge", calibrationDue = Day(30), certificate = "CAL-PRIVATE-01" });
        await plant.Ok(plant.Engineer, "/api/resources/tool-types", new { code = "PRIV-BLADE", name = "Slitting blade", kind = "Cutting", usageLimit = 100 });
        await plant.Ok(plant.Engineer, "/api/resources/tools", new { code = "PRIV-KNIFE", typeCode = "PRIV-BLADE", description = "Private order slitting knife", eligibleMachines = new[] { "SLITTER-A" }, eligibleActivities = new[] { "RUN_SLITTER" } });
        await plant.Ok(quality, "/api/quality/plans", new { partNumber = "ROLL-100", sequence = 10, title = "Private width acceptance", characteristics = new[] { new { name = "Width", kind = "Variable", nominal = 100m, lower = 99.5m, upper = 100.5m, unit = "mm" } } });
        var order = Id(await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = "ROLL-100", quantity = 10m, dueDate = Day(10), priority = 3, programId = program }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Release private operation" });
        await plant.Ok(plant.As("stores"), "/api/inventory/issues", new { orderNumber = order, partNumber = "COIL-AL", quantity = 26m, locationCode = "STORES" });
        await plant.Ok(kiosk, "/api/shopfloor/clock-in", new { badge });
        await plant.Ok(kiosk, "/api/shopfloor/start", new { badge, orderNumber = order, sequence = 10, machineCode = "SLITTER-A", toolCode = "PRIV-KNIFE" });
        await plant.Ok(kiosk, "/api/shopfloor/report", new { badge, orderNumber = order, sequence = 10, good = 10m, scrap = 0m, complete = true });
        await plant.Ok(inspector, "/api/quality/inspections", new { orderNumber = order, sequence = 10, quantity = 10m, gaugeCode = "PRIV-GAUGE", findings = new[] { new { number = 1, measured = 100m } } });
        const string sharedReference = "Tool crib comparison SAMPLE-01";
        await plant.Ok(quality, "/api/resources/tools/PRIV-GAUGE/uses", new { uses = 1, reference = sharedReference, reason = "Record ordinary shared tool metadata" });
        await plant.Ok(quality, "/api/resources/tools/PRIV-GAUGE/uses", new { uses = 1, reference = "inspection 999999999/10", reason = "No source order exists for this entered reference" });

        var gaugeHistory = await GetAsync(reader, "/api/resources/tools/PRIV-GAUGE/history");
        Assert.Contains(gaugeHistory.EnumerateArray(), item => item.GetProperty("reference").GetString() == $"inspection {order}/10");
        Assert.Contains(gaugeHistory.EnumerateArray(), item => item.GetProperty("reference").GetString() == sharedReference);
        Assert.DoesNotContain(gaugeHistory.EnumerateArray(), item => item.GetProperty("reference").GetString() == "inspection 999999999/10");
        var knifeHistory = await GetAsync(reader, "/api/resources/tools/PRIV-KNIFE/history");
        Assert.Contains(knifeHistory.EnumerateArray(), item => item.GetProperty("reference").GetString() == $"order {order}/10");
        var reportPath = $"/api/reports/tool-use-history?search=PRIV-GAUGE&from={Day(-1)}&to={Day(1)}";
        var report = await GetAsync(reader, reportPath);
        Assert.Equal(2, report.GetProperty("rows").GetArrayLength());
        Assert.Contains(report.GetProperty("rows").EnumerateArray(), row => row.GetProperty("cells")[6].GetString() == $"inspection {order}/10");
        var answer = await AskAsync(reader);
        Assert.Equal("tool-use-history", answer.GetProperty("interpretation").GetProperty("report").GetProperty("reportId").GetString());
        Assert.Equal(2, answer.GetProperty("rows").GetArrayLength());

        await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["tool-private-reader"]}/programs/{program}/revoke", new { reason = "Current revocation must remove private tool-use references" });
        var hiddenGauge = await GetAsync(reader, "/api/resources/tools/PRIV-GAUGE/history");
        var remainingUse = Assert.Single(hiddenGauge.EnumerateArray(), item => item.GetProperty("kind").GetString() == "Used");
        Assert.Equal(sharedReference, remainingUse.GetProperty("reference").GetString());
        var hiddenKnife = await GetAsync(reader, "/api/resources/tools/PRIV-KNIFE/history");
        Assert.DoesNotContain(hiddenKnife.EnumerateArray(), item => item.GetProperty("kind").GetString() == "Used");
        var hiddenReport = await GetAsync(reader, reportPath);
        Assert.Equal(sharedReference, Assert.Single(hiddenReport.GetProperty("rows").EnumerateArray()).GetProperty("cells")[6].GetString());
        var hiddenAnswer = await AskAsync(reader);
        Assert.Equal(sharedReference, Assert.Single(hiddenAnswer.GetProperty("rows").EnumerateArray()).GetProperty("cells")[6].GetString());
        Assert.DoesNotContain($"inspection {order}/10", hiddenReport.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain($"inspection {order}/10", hiddenAnswer.GetRawText(), StringComparison.Ordinal);
    }

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);
    private static string Day(int days) => DateTime.UtcNow.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static async Task<JsonElement> AskAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/analyst/ask", new { question = "Was PRIV-GAUGE calibrated when it was used?" });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement;
    }
    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }
}
