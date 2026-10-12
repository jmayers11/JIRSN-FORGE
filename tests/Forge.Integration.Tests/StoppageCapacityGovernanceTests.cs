using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class StoppageCapacityGovernanceTests(PostgresFixture postgres)
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan elapsed) => now += elapsed;
    }

    [Fact]
    public async Task Setup_and_changeover_remove_capacity_and_projection_until_ended_without_closing_an_independent_breakdown()
    {
        var clock = new Clock(new DateTimeOffset(DateTime.UtcNow.Date.AddHours(12), TimeSpan.Zero));
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), services => services.AddSingleton<TimeProvider>(clock));
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("stoppage-tech", "MaintenanceTechnician");
        var tech = plant.As("stoppage-tech");
        var engineer = plant.Engineer;
        await plant.Ok(engineer, "/api/resources/shifts", new { code = "STOP-ALL", siteCode = "PLANT1", name = "Stoppage reference all-day shift", start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 } });
        await plant.Ok(engineer, "/api/resources/work-centers/SLIT-WC/shifts", new { shiftCode = "STOP-ALL" });
        foreach (var subject in new[] { "sarah", "michael" })
            await plant.Ok(engineer, $"/api/workforce/workers/{plant.People[subject]}/shift", new { shiftCode = "STOP-ALL" });

        var due = clock.GetUtcNow().AddDays(10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var orders = new Dictionary<string, long>(StringComparer.Ordinal);
        var goals = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var machine in new[] { "SLITTER-A", "SLITTER-B" })
        {
            var part = "STOP-" + machine[^1];
            await plant.Ok(engineer, "/api/engineering/items", new { partNumber = part, description = "Machine-specific stoppage proof", unitOfMeasure = "EA", makeBuy = "Make", leadTimeDays = 2 });
            await plant.Ok(engineer, $"/api/engineering/items/{part}/revisions", new { revision = "A" });
            await plant.Ok(engineer, $"/api/engineering/items/{part}/revisions/A/bom", new { lineNumber = 1, componentPartNumber = "COIL-AL", quantityPer = 1m, scrapPercent = 0m });
            await plant.Ok(engineer, $"/api/engineering/items/{part}/revisions/A/routing", new { sequence = 10, description = "Machine-specific slit", workCenterCode = "SLIT-WC", activityCode = "RUN_SLITTER", eligibleMachines = new[] { machine }, setupMinutes = 30m, runMinutesPerUnit = 1m, crewSize = 1 });
            await plant.Ok(plant.Approver, $"/api/engineering/items/{part}/revisions/A/release", new { reason = "Release factual machine eligibility" });
            var order = Id(await plant.Ok(engineer, "/api/orders/", new { partNumber = part, quantity = 10m, dueDate = due, priority = 3 }));
            await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Release source work for capacity proof" });
            orders[machine] = order;
            goals[machine] = Id(await plant.Ok(planner, "/api/planning/goals", new { kind = "Production", title = "Stoppage proof " + machine, orderNumber = order, requiredBy = due }));
        }
        foreach (var (code, category) in new[] { ("STOP-SETUP", "Setup"), ("STOP-CHANGE", "Changeover") })
            await plant.Ok(engineer, "/api/execution/reason-codes", new { code, kind = "Downtime", category, description = "Observed " + category.ToLowerInvariant() });
        Assert.Equal(2m, (await CapacityAsync(planner, clock, "SLITTER-A")).GetProperty("executableHours").GetDecimal());
        Assert.Equal("OnTrack", (await GoalAsync(planner, goals["SLITTER-A"])).GetProperty("state").GetString());

        var setup = Id(await plant.Ok(engineer, "/api/execution/stoppages", new { machineCode = "SLITTER-A", category = "Setup", reasonCode = "STOP-SETUP", orderNumber = orders["SLITTER-A"], sequence = 10, note = "First knife installation; observation STOP-S-01", reason = "Observed preparation in progress" }));
        await AssertBlockedAsync(planner, clock, "SLITTER-A", "Setup", "STOP-SETUP", "STOP-S-01");
        await AssertUnprojectedAsync(planner, goals["SLITTER-A"]);
        await plant.Ok(tech, "/api/maintenance/machines/SLITTER-B/down", new { description = "Independent blade-drive failure STOP-B-01", reason = "Observed mechanical failure" });
        await AssertBlockedAsync(planner, clock, "SLITTER-B", "STOP-B-01");
        var stoppedSchedule = await ScheduleAsync(plant, planner);
        Assert.All(stoppedSchedule.GetProperty("orders").EnumerateArray(), order => Assert.Equal("Unschedulable", order.GetProperty("status").GetString()));
        Assert.Contains(stoppedSchedule.GetProperty("operations").EnumerateArray(), operation => operation.GetProperty("orderNumber").GetInt64() == orders["SLITTER-A"] && operation.GetProperty("bindingDetail").GetString()!.Contains("Setup", StringComparison.Ordinal));

        // Closing the observed setup restores A, while the separate maintenance breakdown remains open.
        clock.Advance(TimeSpan.FromMinutes(30));
        await plant.Ok(engineer, $"/api/execution/stoppages/{setup}/end", new { reason = "Knife installation verified complete" });
        Assert.Equal(2m, (await CapacityAsync(planner, clock, "SLITTER-A")).GetProperty("executableHours").GetDecimal());
        Assert.Equal("OnTrack", (await GoalAsync(planner, goals["SLITTER-A"])).GetProperty("state").GetString());
        await AssertBlockedAsync(planner, clock, "SLITTER-B", "STOP-B-01");

        var changeover = Id(await plant.Ok(engineer, "/api/execution/stoppages", new { machineCode = "SLITTER-A", category = "Changeover", reasonCode = "STOP-CHANGE", orderNumber = orders["SLITTER-A"], sequence = 10, note = "Width change; observation STOP-C-01", reason = "Observed changeover in progress" }));
        await AssertBlockedAsync(planner, clock, "SLITTER-A", "Changeover", "STOP-CHANGE", "STOP-C-01");
        await AssertUnprojectedAsync(planner, goals["SLITTER-A"]);
        clock.Advance(TimeSpan.FromMinutes(15));
        await plant.Ok(engineer, $"/api/execution/stoppages/{changeover}/end", new { reason = "New width verified ready" });
        Assert.Equal(2m, (await CapacityAsync(planner, clock, "SLITTER-A")).GetProperty("executableHours").GetDecimal());
        var recovered = await GoalAsync(planner, goals["SLITTER-A"]);
        Assert.Equal("OnTrack", recovered.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.String, recovered.GetProperty("projected").ValueKind);
        await AssertUnprojectedAsync(planner, goals["SLITTER-B"]);
        await AssertBlockedAsync(planner, clock, "SLITTER-B", "STOP-B-01");
        var finalSchedule = await ScheduleAsync(plant, planner);
        Assert.True(finalSchedule.GetProperty("operations").EnumerateArray().Single(operation => operation.GetProperty("orderNumber").GetInt64() == orders["SLITTER-A"]).GetProperty("scheduled").GetBoolean());
        Assert.False(finalSchedule.GetProperty("operations").EnumerateArray().Single(operation => operation.GetProperty("orderNumber").GetInt64() == orders["SLITTER-B"]).GetProperty("scheduled").GetBoolean());

        var source = await GetAsync(engineer, "/api/execution/stoppages?days=1");
        var setupRecord = source.EnumerateArray().Single(record => record.GetProperty("stoppage").GetProperty("stoppageId").GetInt64() == setup);
        var changeRecord = source.EnumerateArray().Single(record => record.GetProperty("stoppage").GetProperty("stoppageId").GetInt64() == changeover);
        Assert.Equal(("Setup", 30m, 30m), (setupRecord.GetProperty("stoppage").GetProperty("category").GetString(), setupRecord.GetProperty("minutes").GetDecimal(), setupRecord.GetProperty("stoppage").GetProperty("expectedMinutes").GetDecimal()));
        Assert.Equal(("Changeover", 15m, -15m), (changeRecord.GetProperty("stoppage").GetProperty("category").GetString(), changeRecord.GetProperty("minutes").GetDecimal(), changeRecord.GetProperty("variance").GetDecimal()));
        // The planning projection did not copy production stoppages into the maintenance ledger.
        var maintenance = await GetAsync(engineer, "/api/maintenance/downtime");
        Assert.DoesNotContain(maintenance.EnumerateArray(), window => window.GetProperty("machineCode").GetString() == "SLITTER-A");
        Assert.Contains(maintenance.EnumerateArray(), window => window.GetProperty("machineCode").GetString() == "SLITTER-B" && window.GetProperty("endsAt").ValueKind == JsonValueKind.Null);
    }

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static async Task<JsonElement> ScheduleAsync(SlittingPlant.Plant plant, HttpClient planner)
    {
        var version = Id(await plant.Ok(planner, "/api/planning/schedules", new { horizonDays = 14, reason = "Project from current authoritative stoppages and outages" }));
        return (await GetAsync(planner, $"/api/planning/schedules/{version}")).GetProperty("result");
    }

    private static async Task<JsonElement> GoalAsync(HttpClient planner, long goal) =>
        (await GetAsync(planner, "/api/planning/goals")).EnumerateArray().Single(item => item.GetProperty("goal").GetProperty("goalId").GetInt64() == goal).GetProperty("goal");

    private static async Task AssertUnprojectedAsync(HttpClient planner, long goal)
    {
        var item = await GoalAsync(planner, goal);
        Assert.Equal("AtRisk", item.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("projected").ValueKind);
    }

    private static async Task<JsonElement> CapacityAsync(HttpClient planner, Clock clock, string machine) =>
        (await GetAsync(planner, $"/api/planning/capacity?from={Uri.EscapeDataString(clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture))}&to={Uri.EscapeDataString(clock.GetUtcNow().AddHours(2).ToString("O", CultureInfo.InvariantCulture))}&workCenter=SLIT-WC"))
        .GetProperty("output").GetProperty("machines").EnumerateArray().Single(item => item.GetProperty("machineCode").GetString() == machine);

    private static async Task AssertBlockedAsync(HttpClient planner, Clock clock, string machine, params string[] evidence)
    {
        var capacity = await CapacityAsync(planner, clock, machine);
        Assert.Equal(0m, capacity.GetProperty("executableHours").GetDecimal());
        var interval = Assert.Single(capacity.GetProperty("intervals").EnumerateArray());
        Assert.Equal("Equipment", interval.GetProperty("limiting").GetString());
        foreach (var value in evidence)
            Assert.Contains(value, interval.GetProperty("detail").GetString()!, StringComparison.Ordinal);
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }
}
