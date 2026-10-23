using System.Net;
using System.Text.Json;

namespace Forge.Integration.Tests;

/// <summary>
/// Phase 4 exit gate: spec §77 (machine outage) and §81 (future PTO) through the real API, with
/// the scheduler recording binding constraints and constraints following their lifecycle.
/// The plant runs a 24-hour, 7-day shift so the tests do not depend on the time they run.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class Phase4Tests(PostgresFixture postgres)
{
    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    private static async Task<(SlittingPlant.Plant Plant, HttpClient Planner, HttpClient Planner2)> CalendarPlantAsync(ForgeApp app)
    {
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var e = plant.Engineer;
        await plant.Ok(e, "/api/resources/shifts", new { code = "ALL", siteCode = "PLANT1", name = "Around the clock", start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 } });
        foreach (var wc in new[] { "SLIT-WC", "PAINT-WC", "ASSY-WC" })
        {
            await plant.Ok(e, $"/api/resources/work-centers/{wc}/shifts", new { shiftCode = "ALL" });
        }

        foreach (var person in new[] { "sarah", "michael", "jennifer", "dana" })
        {
            await plant.Ok(e, $"/api/workforce/workers/{plant.People[person]}/shift", new { shiftCode = "ALL" });
        }

        await plant.Person("planner2", "Planner");
        await plant.Person("tech", "MaintenanceTechnician");

        // ROLL-300 can only be slit on SLITTER-B.
        await plant.Ok(e, "/api/engineering/items", new { partNumber = "ROLL-300", description = "Wide roll", unitOfMeasure = "EA", makeBuy = "Make", leadTimeDays = 2 });
        await plant.Ok(e, "/api/engineering/items/ROLL-300/revisions", new { revision = "A" });
        await plant.Ok(e, "/api/engineering/items/ROLL-300/revisions/A/bom", new { lineNumber = 1, componentPartNumber = "COIL-AL", quantityPer = 4m, scrapPercent = 0m });
        await plant.Ok(e, "/api/engineering/items/ROLL-300/revisions/A/routing", new { sequence = 10, description = "Wide slit", workCenterCode = "SLIT-WC", activityCode = "RUN_SLITTER", eligibleMachines = new[] { "SLITTER-B" }, setupMinutes = 30m, runMinutesPerUnit = 3m, crewSize = 1 });
        await plant.Ok(plant.Approver, "/api/engineering/items/ROLL-300/revisions/A/release", new { reason = "ECN-3" });
        return (plant, planner, plant.As("planner2"));
    }

    private static async Task<long> ReleasedOrderAsync(SlittingPlant.Plant plant, HttpClient planner, string part, decimal quantity, string due)
    {
        var created = await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = part, quantity, dueDate = due, priority = 3 });
        var number = long.Parse(created.GetProperty("entityId").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        await plant.Ok(planner, $"/api/orders/{number}/release", new { reason = "Customer order" });
        return number;
    }

    private static async Task<long> ProposeAsync(SlittingPlant.Plant plant, HttpClient planner)
    {
        var proposed = await plant.Ok(planner, "/api/planning/schedules", new { horizonDays = 14 });
        return long.Parse(proposed.GetProperty("entityId").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Spec_77_machine_outage_propagates_to_schedule_orders_and_constraints_then_recovers()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner, planner2) = await CalendarPlantAsync(app);
        var due = DateTime.UtcNow.AddDays(10).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var flexible = await ReleasedOrderAsync(plant, planner, "ROLL-100", 10m, due);
        var bound = await ReleasedOrderAsync(plant, planner, "ROLL-300", 10m, due);

        // Machine status: SLITTER-B down.
        var tech = plant.As("tech");
        await plant.Ok(tech, "/api/maintenance/machines/SLITTER-B/down", new { description = "Blade drive fault" });
        var readiness = await Get(plant.Engineer, "/api/readiness/machines/SLITTER-B?activity=RUN_SLITTER");
        Assert.Equal("Equipment", readiness.GetProperty("output").GetProperty("primaryConstraint").GetString());

        // Capacity and scheduling: flexible work moves to SLITTER-A; SLITTER-B-only work cannot be scheduled.
        var version = await ProposeAsync(plant, planner);
        var schedule = (await Get(planner, "/api/planning/schedules/proposed")).GetProperty("result");
        var orders = schedule.GetProperty("orders").EnumerateArray().ToDictionary(o => o.GetProperty("orderNumber").GetInt64());
        Assert.Equal("OnTime", orders[flexible].GetProperty("status").GetString());
        Assert.Equal("Unschedulable", orders[bound].GetProperty("status").GetString());
        var flexibleSlit = schedule.GetProperty("operations").EnumerateArray().Single(o => o.GetProperty("orderNumber").GetInt64() == flexible && o.GetProperty("sequence").GetInt32() == 10);
        Assert.Equal("SLITTER-A", flexibleSlit.GetProperty("machineCode").GetString());

        // Constraint with evidence and the affected order.
        var constraints = await Get(planner, "/api/planning/constraints");
        var outage = constraints.EnumerateArray().Single(c => c.GetProperty("category").GetString() == "Maintenance");
        Assert.Equal("SLITTER-B", outage.GetProperty("subject").GetString());
        Assert.Contains("Blade drive fault", outage.GetProperty("evidence").GetString(), StringComparison.Ordinal);
        Assert.Equal(bound, outage.GetProperty("affectedOrders")[0].GetInt64());
        var constraintId = outage.GetProperty("constraintId").GetInt64();
        await plant.Ok(planner, $"/api/planning/constraints/{constraintId}/investigate", new { });

        // Approval takes a second person.
        var self = await plant.Try(planner, $"/api/planning/schedules/{version}/approve", new { reason = "x" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, self.StatusCode);
        await plant.Ok(planner2, $"/api/planning/schedules/{version}/approve", new { reason = "Reviewed outage plan" });

        // Restoration: the next run schedules the order and closes the constraint; a person verifies.
        await plant.Ok(tech, "/api/maintenance/machines/SLITTER-B/restore", new { });
        await ProposeAsync(plant, planner);
        schedule = (await Get(planner, "/api/planning/schedules/proposed")).GetProperty("result");
        Assert.All(schedule.GetProperty("orders").EnumerateArray(), o => Assert.Equal("OnTime", o.GetProperty("status").GetString()));

        var all = await Get(planner, "/api/planning/constraints?includeClosed=true");
        var closed = all.EnumerateArray().Single(c => c.GetProperty("constraintId").GetInt64() == constraintId);
        Assert.Equal("Resolved", closed.GetProperty("status").GetString());
        Assert.Equal("system:scheduler", closed.GetProperty("resolvedBy").GetString());
        await plant.Ok(planner2, $"/api/planning/constraints/{constraintId}/verify", new { reason = "Machine back, order scheduled" });
        Assert.Empty((await Get(planner, "/api/planning/constraints")).EnumerateArray());
    }

    [Fact]
    public async Task Spec_81_approved_pto_surfaces_before_the_absence_with_alternatives()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner, _) = await CalendarPlantAsync(app);
        var now = DateTimeOffset.UtcNow;
        var ptoStart = now.AddHours(3);
        var ptoEnd = now.AddHours(27);
        await plant.Ok(plant.Engineer, $"/api/workforce/workers/{plant.People["sarah"]}/unavailability",
            new { kind = "ApprovedPto", startsAt = ptoStart, endsAt = ptoEnd, reason = "Approved PTO from HR" });

        var unavailability = await Get(planner, $"/api/workforce/unavailability?from={Uri.EscapeDataString(now.ToString("o"))}&to={Uri.EscapeDataString(now.AddDays(30).ToString("o"))}");
        var ptoWindow = unavailability.EnumerateArray().Single(window => window.GetProperty("personId").GetGuid() == plant.People["sarah"]);
        Assert.Equal("ApprovedPto", ptoWindow.GetProperty("kind").GetString());
        Assert.Equal(ptoStart, ptoWindow.GetProperty("startsAt").GetDateTimeOffset());
        Assert.Equal(ptoEnd, ptoWindow.GetProperty("endsAt").GetDateTimeOffset());

        // Capacity: Slitting loses executable time while Sarah is away, though the machines are up.
        var capacity = await Get(planner, $"/api/planning/capacity?from={Uri.EscapeDataString(now.ToString("o"))}&to={Uri.EscapeDataString(now.AddDays(2).ToString("o"))}&workCenter=SLIT-WC");
        var slitterB = capacity.GetProperty("output").GetProperty("machines").EnumerateArray().Single(m => m.GetProperty("machineCode").GetString() == "SLITTER-B");
        Assert.Contains(slitterB.GetProperty("intervals").EnumerateArray(), i => !i.GetProperty("executable").GetBoolean() && i.GetProperty("limiting").GetString() == "Safety");
        Assert.True(slitterB.GetProperty("executableHours").GetDecimal() < slitterB.GetProperty("workingHours").GetDecimal());

        // An order whose slitting runs into the absence is interrupted; the constraint is open now,
        // before the absence, with the affected order and qualified alternatives.
        var number = await ReleasedOrderAsync(plant, planner, "ROLL-100", 400m, now.AddDays(10).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        await ProposeAsync(plant, planner);
        var constraints = await Get(planner, "/api/planning/constraints");
        var staffing = constraints.EnumerateArray().First(c => c.GetProperty("category").GetString() == "SafetyStaffing");
        Assert.Equal(number, staffing.GetProperty("affectedOrders")[0].GetInt64());
        Assert.True(staffing.GetProperty("windowFrom").GetDateTimeOffset() > now);
        Assert.Contains(staffing.GetProperty("alternatives").EnumerateArray(), a => a.GetString()!.StartsWith("Jennifer", StringComparison.Ordinal));

        // Drop-dead dates for the same order.
        var dropDead = await Get(planner, $"/api/orders/{number}/drop-dead-dates?shipDays=1");
        Assert.Equal(2, dropDead.GetProperty("output").GetProperty("operations").GetArrayLength());
        Assert.Single(dropDead.GetProperty("output").GetProperty("purchaseBy").EnumerateArray());
    }
}
