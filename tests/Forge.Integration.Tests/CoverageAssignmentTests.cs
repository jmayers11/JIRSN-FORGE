using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Integration.Tests;

/// <summary>
/// Sprint 10 §21–22 through the real API: refresher rules, the capacity forecast on the recovery ranking, and a coverage assignment whose
/// goal is versioned and judged against the good output actually reported, with reviewed exclusions instead of unfair failures.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CoverageAssignmentTests(PostgresFixture postgres)
{
    private const string Sarah = "B-SARAH-0022";

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Set(DateTimeOffset at) => now = at;
    }

    private static string Day(int fromToday) => DateTime.UtcNow.AddDays(fromToday).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task Coverage_goals_are_versioned_and_judged_against_reported_good_output()
    {
        var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-1).AddHours(13), TimeSpan.Zero);
        var clock = new Clock(start.AddHours(-3));
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), services => services.AddSingleton<TimeProvider>(clock));
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var e = plant.Engineer;
        await plant.Ok(e, "/api/resources/shifts", new { code = "ALL", siteCode = "PLANT1", name = "Around the clock", start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 } });
        await plant.Ok(e, "/api/resources/work-centers/PAINT-WC/shifts", new { shiftCode = "ALL" });
        await plant.Ok(e, $"/api/workforce/workers/{plant.People["sarah"]}/shift", new { shiftCode = "ALL" });
        await plant.Ok(plant.Admin, $"/api/workforce/people/{plant.People["sarah"]}/badge", new { badge = Sarah });
        await plant.Person("kiosk", "ShopFloorTerminal");
        await plant.Person("sup", "Supervisor");
        await plant.Person("sup2", "Supervisor");
        var (k, sup, sup2) = (plant.As("kiosk"), plant.As("sup"), plant.As("sup2"));
        var sarah = plant.People["sarah"];

        // A painted panel: one paint operation at 1 minute per unit.
        await plant.Ok(e, "/api/engineering/items", new { partNumber = "PANEL-1", description = "Painted panel", unitOfMeasure = "EA", makeBuy = "Make", leadTimeDays = 2 });
        await plant.Ok(e, "/api/engineering/items/PANEL-1/revisions", new { revision = "A" });
        await plant.Ok(e, "/api/engineering/items/PANEL-1/revisions/A/bom", new { lineNumber = 1, componentPartNumber = "COIL-AL", quantityPer = 0.1m, scrapPercent = 0m });
        await plant.Ok(e, "/api/engineering/items/PANEL-1/revisions/A/routing", new { sequence = 10, description = "Paint", workCenterCode = "PAINT-WC", activityCode = "PAINT", eligibleMachines = new[] { "PAINT-BOOTH" }, setupMinutes = 10m, runMinutesPerUnit = 1m, crewSize = 1 });
        await plant.Ok(plant.Approver, "/api/engineering/items/PANEL-1/revisions/A/release", new { reason = "ECN-22" });
        var order = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "PANEL-1", quantity = 80m, dueDate = Day(10), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Customer order" });

        var plan = new
        {
            personId = sarah, destinationWorkCenter = "PAINT-WC", machineCode = "PAINT-BOOTH", activityCode = "PAINT", plannedStart = start, plannedEnd = start.AddHours(8),
            coverageReason = "Paint is short a painter today", goalGoodUnits = 70m, goalProductiveMinutes = 420, goalRationale = "Forecast 72; first day back on paint",
            orderNumber = order, sequence = 10, forecastConservative = 60m, forecastExpected = 72m, forecastOptimistic = 80m, forecastConfidence = "Medium", forecastBasis = "4 verified runs",
            reason = "Coverage plan",
        };

        // A lapsed (here: never held) qualification blocks coverage, whatever the worker achieved elsewhere; workers cannot plan coverage.
        Assert.Contains("qualification_not_current", await (await plant.Try(sup, "/api/workforce/coverage", plan)).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(plant.As("sarah"), "/api/workforce/coverage", plan)).StatusCode);
        await plant.Ok(e, $"/api/competency/people/{sarah}/training", new { code = "PAINT-I" });
        await plant.Ok(plant.Approver, $"/api/competency/people/{sarah}/certifications", new { code = "PAINT-I", evidence = "Observed paint practical", reason = "Cross-training complete" });

        // A refresher rule is site policy, set by competency management and audited.
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(planner, "/api/competency/qualification-types/PAINT-I/refresher-policy", new { refresherAfterDays = 180, reacclimationMinutes = 30, reason = "x" })).StatusCode);
        await plant.Ok(e, "/api/competency/qualification-types/PAINT-I/refresher-policy", new { refresherAfterDays = 180, reacclimationMinutes = 30, reason = "Paint SOP 7.2" });
        Assert.Contains((await Get(e, "/api/competency/refresher-policies")).EnumerateArray(), p => p.GetProperty("code").GetString() == "PAINT-I" && p.GetProperty("refresherAfterDays").GetInt32() == 180);

        // The recovery ranking carries a capacity forecast for each candidate; it never writes an assignment.
        var capacity = await plant.Ok(sup, "/api/readiness/workforce-recovery-capacity", new { machineCode = "PAINT-BOOTH", activityCode = "PAINT", scheduledMinutes = 480 });
        Assert.Equal("skill-capacity-heuristic-v1", capacity.GetProperty("ruleVersion").GetString());
        Assert.Equal(JsonValueKind.Array, capacity.GetProperty("candidates").ValueKind);

        var coverage = (await plant.Ok(sup, "/api/workforce/coverage", plan)).GetProperty("entityId").GetString()!;

        clock.Set(start);
        await plant.Ok(k, "/api/shopfloor/clock-in", new { badge = Sarah });
        await plant.Ok(sup, $"/api/workforce/coverage/{coverage}/start", new { reason = "Started in paint" });
        await plant.Ok(k, "/api/shopfloor/start", new { badge = Sarah, orderNumber = order, sequence = 10, machineCode = "PAINT-BOOTH" });
        clock.Set(start.AddMinutes(90));
        await plant.Ok(k, "/api/shopfloor/report", new { badge = Sarah, orderNumber = order, sequence = 10, good = 76m, scrap = 0m, complete = false });
        clock.Set(start.AddMinutes(91));
        await plant.Ok(sup, $"/api/workforce/coverage/{coverage}/complete", new { reason = "Back to slitting" });
        clock.Set(start.AddHours(2));

        var url = $"/api/workforce/coverage?personId={sarah}&from={Uri.EscapeDataString(start.AddDays(-1).ToString("O"))}&to={Uri.EscapeDataString(start.AddHours(3).ToString("O"))}";
        var report = await Get(sup, url);
        var metrics = report.GetProperty("metrics");
        Assert.Equal(100m, metrics.GetProperty("coverageGoalSuccessRatePercent").GetDecimal());
        Assert.Equal(108.57m, metrics.GetProperty("averageCoverageGoalAttainmentPercent").GetDecimal());
        Assert.Equal(4m, metrics.GetProperty("meanAbsoluteForecastErrorGoodUnits").GetDecimal());
        Assert.Equal(76m, report.GetProperty("assignments")[0].GetProperty("actualGoodUnits").GetDecimal());

        // A completed assignment's goal is not rewritten; a different supervisor may review a time-adjusted goal or exclude it, with the original kept.
        Assert.Contains("coverage_goal_state", await (await plant.Try(sup, $"/api/workforce/coverage/{coverage}/goal", new { goodUnits = 50m, productiveMinutes = 300, rationale = "Lower it", reason = "x" })).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("independent_review_required", await (await plant.Try(sup, $"/api/workforce/coverage/{coverage}/goal", new { goodUnits = 60m, productiveMinutes = 300, rationale = "x", timeAdjusted = true, reason = "x" })).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await plant.Ok(sup2, $"/api/workforce/coverage/{coverage}/goal", new { goodUnits = 80m, productiveMinutes = 420, rationale = "Same standard, full time available", timeAdjusted = true, reason = "Review" });
        var adjusted = await Get(sup, url);
        Assert.Equal(new[] { "Original", "TimeAdjusted" }, adjusted.GetProperty("assignments")[0].GetProperty("goals").EnumerateArray().Select(g => g.GetProperty("kind").GetString()).ToArray());
        Assert.Equal(0m, adjusted.GetProperty("metrics").GetProperty("coverageGoalSuccessRatePercent").GetDecimal());
        await plant.Ok(sup2, $"/api/workforce/coverage/{coverage}/comparability", new { comparable = false, exclusionReason = "Booth extractor fault for 40 minutes", reason = "Maintenance log" });
        var excluded = (await Get(sup, url)).GetProperty("metrics");
        Assert.Equal(JsonValueKind.Null, excluded.GetProperty("coverageGoalSuccessRatePercent").ValueKind);
        Assert.Equal(1, excluded.GetProperty("excludedGoalAssignmentCount").GetInt32());

        // The worker sees their own card; another worker cannot.
        Assert.Equal(HttpStatusCode.OK, (await plant.As("sarah").GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.As("dana").GetAsync(url)).StatusCode);
    }
}
