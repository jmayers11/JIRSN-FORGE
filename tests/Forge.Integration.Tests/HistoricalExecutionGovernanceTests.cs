using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Integration.Tests;

/// <summary>Governance B/E through the real API, with time advanced independently of identity and authority.</summary>
[Collection(PostgresCollection.Name)]
public sealed class HistoricalExecutionGovernanceTests(PostgresFixture postgres)
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private static long Id(JsonElement value) => long.Parse(value.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);
    private static string Day(DateTimeOffset at, int days = 0) => at.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private static async Task ConfigureFloorAsync(SlittingPlant.Plant plant, string badge)
    {
        await plant.Ok(plant.Engineer, "/api/resources/shifts", new { code = "ALL", siteCode = "PLANT1", name = "Around the clock", start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 } });
        await plant.Ok(plant.Engineer, "/api/resources/work-centers/SLIT-WC/shifts", new { shiftCode = "ALL" });
        await plant.Ok(plant.Engineer, $"/api/workforce/workers/{plant.People["sarah"]}/shift", new { shiftCode = "ALL" });
        await plant.Ok(plant.Admin, $"/api/workforce/people/{plant.People["sarah"]}/badge", new { badge });
    }

    [Fact]
    public async Task Historical_inspection_retains_calibration_valid_at_its_timestamp_after_the_gauge_expires()
    {
        var clock = new Clock(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        var baseline = clock.UtcNow;
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), services => services.AddSingleton<TimeProvider>(clock));
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        const string badge = "B-HISTORICAL-GAUGE";
        await ConfigureFloorAsync(plant, badge);
        await plant.Person("historical-kiosk", "ShopFloorTerminal");
        await plant.Person("historical-quality", "QualityEngineer");
        await plant.Person("historical-inspector", "Inspector");
        var kiosk = plant.As("historical-kiosk");
        var quality = plant.As("historical-quality");
        var inspector = plant.As("historical-inspector");
        await plant.Ok(quality, "/api/quality/gauges", new { code = "HIST-GAUGE", description = "Inspection gauge with one day left", calibrationDue = Day(baseline, 1), certificate = "CAL-HIST-ORIGINAL" });
        await plant.Ok(quality, "/api/quality/plans", new
        {
            partNumber = "ROLL-100", sequence = 10, title = "Historical width inspection",
            characteristics = new[] { new { name = "Width", kind = "Variable", nominal = 100m, lower = 99.5m, upper = 100.5m, unit = "mm" } },
        });
        var order = Id(await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = "ROLL-100", quantity = 10m, dueDate = Day(baseline, 10), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Historical inspection acceptance" });
        await plant.Ok(plant.As("stores"), "/api/inventory/issues", new { orderNumber = order, partNumber = "COIL-AL", quantity = 26m, locationCode = "STORES" });
        await plant.Ok(kiosk, "/api/shopfloor/clock-in", new { badge });
        await plant.Ok(kiosk, "/api/shopfloor/start", new { badge, orderNumber = order, sequence = 10, machineCode = "SLITTER-A" });
        await plant.Ok(kiosk, "/api/shopfloor/report", new { badge, orderNumber = order, sequence = 10, good = 10m, scrap = 0m, complete = true });
        clock.UtcNow = baseline.AddMinutes(1);
        object inspection = new { orderNumber = order, sequence = 10, quantity = 10m, gaugeCode = "HIST-GAUGE", findings = new[] { new { number = 1, measured = 100m } } };
        var inspectionId = Id(await plant.Ok(inspector, "/api/quality/inspections", inspection));
        var original = Assert.Single((await Get(inspector, $"/api/quality/inspections?order={order}")).EnumerateArray());
        Assert.Equal(inspectionId, original.GetProperty("inspectionId").GetInt64());
        Assert.Equal("Pass", original.GetProperty("result").GetString());
        Assert.Equal("HIST-GAUGE", original.GetProperty("gaugeCode").GetString());
        Assert.Equal(clock.UtcNow, original.GetProperty("at").GetDateTimeOffset());
        var used = Assert.Single((await Get(inspector, "/api/resources/tools/HIST-GAUGE/history")).EnumerateArray(), e => e.GetProperty("kind").GetString() == "Used");
        Assert.Equal($"inspection {order}/10", used.GetProperty("reference").GetString());
        Assert.Equal(original.GetProperty("at").GetDateTimeOffset(), used.GetProperty("at").GetDateTimeOffset());

        clock.UtcNow = baseline.AddDays(3);
        var current = (await Get(inspector, "/api/resources/tools")).EnumerateArray().Single(t => t.GetProperty("tool").GetProperty("code").GetString() == "HIST-GAUGE");
        Assert.False(current.GetProperty("validity").GetProperty("valid").GetBoolean());
        Assert.Contains(current.GetProperty("validity").GetProperty("problems").EnumerateArray(), p => p.GetString()!.Contains("due for calibration", StringComparison.Ordinal));
        var at = Uri.EscapeDataString(original.GetProperty("at").GetDateTimeOffset().ToString("O", CultureInfo.InvariantCulture));
        var historical = (await Get(inspector, $"/api/resources/tools?at={at}")).EnumerateArray().Single(t => t.GetProperty("tool").GetProperty("code").GetString() == "HIST-GAUGE");
        Assert.True(historical.GetProperty("validity").GetProperty("valid").GetBoolean());
        Assert.Equal(Day(baseline, 1), historical.GetProperty("validity").GetProperty("calibrationDue").GetString());
        var historyReport = await Get(quality, $"/api/reports/tool-use-history?search=HIST-GAUGE&from={Day(baseline, -1)}&to={Day(baseline, 4)}");
        var row = Assert.Single(historyReport.GetProperty("rows").EnumerateArray()).GetProperty("cells");
        Assert.Equal(("HIST-GAUGE", "Yes", $"inspection {order}/10"), (row[1].GetString(), row[3].GetString(), row[6].GetString()));
        Assert.Equal(original.GetProperty("at").GetDateTimeOffset(), row[0].GetDateTimeOffset());
        Assert.Equal(Day(baseline, 1), row[2].GetString());
        Assert.Equal(original.GetRawText(), Assert.Single((await Get(inspector, $"/api/quality/inspections?order={order}")).EnumerateArray()).GetRawText());

        using var rejected = await plant.Try(inspector, "/api/quality/inspections", inspection);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        Assert.Contains("gauge_out_of_calibration", await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(original.GetRawText(), Assert.Single((await Get(inspector, $"/api/quality/inspections?order={order}")).EnumerateArray()).GetRawText());
        Assert.Single((await Get(inspector, "/api/resources/tools/HIST-GAUGE/history")).EnumerateArray(), e => e.GetProperty("kind").GetString() == "Used");
    }

    [Fact]
    public async Task An_authenticated_supervisor_cannot_override_their_expired_operator_qualification()
    {
        var clock = new Clock(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        var baseline = clock.UtcNow;
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), services => services.AddSingleton<TimeProvider>(clock));
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        const string badge = "B-EXPIRING-OPERATOR";
        await ConfigureFloorAsync(plant, badge);
        await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["sarah"]}/roles", new { role = "Supervisor", reason = "Manufacturing authority remains independent of operation qualification" });
        await plant.Ok(plant.Engineer, "/api/competency/qualification-types", new { code = "SHORT-QUAL", name = "One-day process qualification", kind = "Machine", validityDays = 1 });
        await plant.Ok(plant.Engineer, $"/api/competency/people/{plant.People["sarah"]}/training", new { code = "SHORT-QUAL" });
        await plant.Ok(plant.Approver, $"/api/competency/people/{plant.People["sarah"]}/certifications", new { code = "SHORT-QUAL", evidence = "Observed practical assessment", reason = "One-day qualification granted" });
        await plant.Ok(plant.Engineer, "/api/resources/activity-types", new { code = "SHORT-RUN", name = "Short qualification process" });
        await plant.Ok(plant.Engineer, "/api/resources/machines/SLITTER-A/activities", new { activityCode = "SHORT-RUN", operatorQualifications = new[] { "SHORT-QUAL" } });
        await plant.Ok(plant.Engineer, "/api/engineering/items", new { partNumber = "SHORT-OUTPUT", description = "Output requiring a current process qualification", unitOfMeasure = "EA", makeBuy = "Make", leadTimeDays = 1 });
        await plant.Ok(plant.Engineer, "/api/engineering/items/SHORT-OUTPUT/revisions", new { revision = "A" });
        await plant.Ok(plant.Engineer, "/api/engineering/items/SHORT-OUTPUT/revisions/A/bom", new { lineNumber = 1, componentPartNumber = "COIL-AL", quantityPer = 1m, scrapPercent = 0m });
        await plant.Ok(plant.Engineer, "/api/engineering/items/SHORT-OUTPUT/revisions/A/routing", new { sequence = 10, description = "Run the qualified process", workCenterCode = "SLIT-WC", activityCode = "SHORT-RUN", eligibleMachines = new[] { "SLITTER-A" }, setupMinutes = 0m, runMinutesPerUnit = 1m, crewSize = 1 });
        await plant.Ok(plant.Approver, "/api/engineering/items/SHORT-OUTPUT/revisions/A/release", new { reason = "Qualified process baseline" });
        var order = Id(await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = "SHORT-OUTPUT", quantity = 5m, dueDate = Day(baseline, 10), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Expired qualification acceptance" });
        await plant.Ok(plant.As("stores"), "/api/inventory/issues", new { orderNumber = order, partNumber = "COIL-AL", quantity = 5m, locationCode = "STORES" });
        var supervisor = plant.As("sarah");
        var identity = await Get(supervisor, "/api/kernel/me");
        Assert.Equal(plant.People["sarah"], identity.GetProperty("personId").GetGuid());
        Assert.Contains(identity.GetProperty("roles").EnumerateArray(), role => role.GetString() == "Supervisor");
        await plant.Ok(supervisor, "/api/shopfloor/clock-in", new { badge });
        object start = new { badge, orderNumber = order, sequence = 10, machineCode = "SLITTER-A" };
        await plant.Ok(supervisor, "/api/shopfloor/start", start);
        await plant.Ok(supervisor, "/api/shopfloor/stop", new { badge });
        await plant.Ok(supervisor, "/api/shopfloor/clock-out", new { badge });
        var validLabor = Assert.Single((await Get(supervisor, $"/api/orders/{order}/shopfloor")).GetProperty("labor").EnumerateArray());
        Assert.Equal(plant.People["sarah"], validLabor.GetProperty("personId").GetGuid());
        Assert.NotEqual(JsonValueKind.Null, validLabor.GetProperty("endedAt").ValueKind);

        clock.UtcNow = baseline.AddDays(2);
        var stillAuthenticated = await Get(supervisor, "/api/kernel/me");
        Assert.Equal(identity.GetProperty("personId").GetGuid(), stillAuthenticated.GetProperty("personId").GetGuid());
        Assert.Equal(identity.GetProperty("roles").GetRawText(), stillAuthenticated.GetProperty("roles").GetRawText());
        await plant.Ok(supervisor, "/api/shopfloor/clock-in", new { badge });
        var expired = (await Get(plant.Engineer, $"/api/competency/people/{plant.People["sarah"]}")).EnumerateArray().Single(q => q.GetProperty("code").GetString() == "SHORT-QUAL");
        Assert.Equal("Qualified", expired.GetProperty("status").GetString());
        Assert.Equal(baseline.AddDays(1), expired.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.True(expired.GetProperty("expiresAt").GetDateTimeOffset() < clock.UtcNow);
        using var rejected = await plant.Try(supervisor, "/api/shopfloor/start", start);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        var problem = await rejected.Content.ReadAsStringAsync();
        Assert.Contains("not_qualified", problem, StringComparison.Ordinal);
        Assert.Contains("SHORT-QUAL", problem, StringComparison.Ordinal);
        var after = Assert.Single((await Get(supervisor, $"/api/orders/{order}/shopfloor")).GetProperty("labor").EnumerateArray());
        Assert.Equal(validLabor.GetRawText(), after.GetRawText());

        await plant.Ok(plant.Approver, $"/api/competency/people/{plant.People["sarah"]}/certifications", new { code = "SHORT-QUAL", evidence = "A new practical assessment", reason = "Authorized recertification after expiry" });
        await plant.Ok(supervisor, "/api/shopfloor/start", start);
        var resumed = (await Get(supervisor, $"/api/orders/{order}/shopfloor")).GetProperty("labor").EnumerateArray().ToList();
        Assert.Equal(2, resumed.Count);
        Assert.Contains(resumed, labor => labor.GetProperty("personId").GetGuid() == plant.People["sarah"] && labor.GetProperty("endedAt").ValueKind == JsonValueKind.Null);
    }
}
