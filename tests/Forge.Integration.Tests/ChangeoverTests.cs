using System.Globalization;
using System.Text.Json;

namespace Forge.Integration.Tests;

/// <summary>ADR 0057 §3.2 through the real API: a job after one of the same set-up family gets the reduced set-up.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ChangeoverTests(PostgresFixture postgres)
{
    private static string Day(int fromToday) => DateTime.UtcNow.AddDays(fromToday).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Same_family_jobs_on_a_machine_plan_the_shorter_set_up()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var e = plant.Engineer;
        await plant.Ok(e, "/api/resources/shifts", new { code = "ALL", siteCode = "PLANT1", name = "Around the clock", start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 } });
        await plant.Ok(e, "/api/resources/work-centers/SLIT-WC/shifts", new { shiftCode = "ALL" });
        foreach (var person in new[] { "sarah", "michael" })
        {
            await plant.Ok(e, $"/api/workforce/workers/{plant.People[person]}/shift", new { shiftCode = "ALL" });
        }

        foreach (var part in new[] { "ROLL-400", "ROLL-401" })
        {
            await plant.Ok(e, "/api/engineering/items", new { partNumber = part, description = "Slit roll " + part, unitOfMeasure = "EA", makeBuy = "Make", leadTimeDays = 2 });
            await plant.Ok(e, $"/api/engineering/items/{part}/revisions", new { revision = "A" });
            await plant.Ok(e, $"/api/engineering/items/{part}/revisions/A/bom", new { lineNumber = 1, componentPartNumber = "COIL-AL", quantityPer = 1m, scrapPercent = 0m });
            var noMinutes = await plant.Try(e, $"/api/engineering/items/{part}/revisions/A/routing", new { sequence = 10, description = "Slit", workCenterCode = "SLIT-WC", activityCode = "RUN_SLITTER", eligibleMachines = new[] { "SLITTER-A" }, setupMinutes = 60m, runMinutesPerUnit = 1m, crewSize = 1, setupFamily = "KNIFE-100" });
            Assert.Contains("invalid_setup_family", await noMinutes.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            await plant.Ok(e, $"/api/engineering/items/{part}/revisions/A/routing", new { sequence = 10, description = "Slit", workCenterCode = "SLIT-WC", activityCode = "RUN_SLITTER", eligibleMachines = new[] { "SLITTER-A" }, setupMinutes = 60m, runMinutesPerUnit = 1m, crewSize = 1, setupFamily = "KNIFE-100", familySetupMinutes = 10m });
            await plant.Ok(plant.Approver, $"/api/engineering/items/{part}/revisions/A/release", new { reason = "ECN-57" });
        }

        var orders = new List<long>();
        foreach (var part in new[] { "ROLL-400", "ROLL-401" })
        {
            var created = await plant.Ok(e, "/api/orders/", new { partNumber = part, quantity = 10m, dueDate = Day(10), priority = 3 });
            var number = long.Parse(created.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);
            await plant.Ok(planner, $"/api/orders/{number}/release", new { reason = "Customer order" });
            orders.Add(number);
        }

        await plant.Ok(planner, "/api/planning/schedules", new { horizonDays = 14 });
        var response = await planner.GetAsync("/api/planning/schedules/proposed");
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        using var json = JsonDocument.Parse(text);
        var slits = json.RootElement.GetProperty("result").GetProperty("operations").EnumerateArray()
            .Where(o => orders.Contains(o.GetProperty("orderNumber").GetInt64())).OrderBy(o => o.GetProperty("start").GetDateTimeOffset()).ToList();
        Assert.Equal(2, slits.Count);
        Assert.Equal(70m, slits[0].GetProperty("plannedMinutes").GetDecimal());
        Assert.StartsWith("Full set-up (60 min)", slits[0].GetProperty("setup").GetString(), StringComparison.Ordinal);
        Assert.Equal(20m, slits[1].GetProperty("plannedMinutes").GetDecimal());
        Assert.StartsWith("Same-family set-up: 10 min instead of 60", slits[1].GetProperty("setup").GetString(), StringComparison.Ordinal);
    }
}
