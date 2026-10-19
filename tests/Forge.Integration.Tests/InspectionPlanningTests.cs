using System.Globalization;
using System.Text.Json;
using Forge.Kernel.Postgres;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Integration.Tests;

/// <summary>
/// ADR 0057 §2 through the real API: an inspection that has not happened yet is planned time, not a stop; a failed
/// inspection stops the order and re-plans at once, while the approved schedule stays in force.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class InspectionPlanningTests(PostgresFixture postgres)
{
    private const string Sarah = "B-SARAH-0057";

    private static string Day(int fromToday) => DateTime.UtcNow.AddDays(fromToday).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
        using var json = JsonDocument.Parse(text);
        return json.RootElement.Clone();
    }

    private static async Task PropagateAsync(ForgeApp app)
    {
        var relay = app.Services.GetRequiredService<OutboxRelay>();
        while (await relay.RelayBatchAsync(100, CancellationToken.None) > 0)
        {
        }

        await app.Services.GetRequiredService<SubscriptionPump>().PumpOnceAsync(1_000_000, CancellationToken.None);
    }

    [Fact]
    public async Task Inspections_take_planned_time_and_a_failure_replans_without_replacing_the_approved_schedule()
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

        await plant.Ok(plant.Admin, $"/api/workforce/people/{plant.People["sarah"]}/badge", new { badge = Sarah });
        await plant.Person("kiosk", "ShopFloorTerminal");
        await plant.Person("qe", "QualityEngineer");
        await plant.Person("insp", "Inspector");
        await plant.Person("planner2", "Planner");
        var (k, qe, insp, planner2) = (plant.As("kiosk"), plant.As("qe"), plant.As("insp"), plant.As("planner2"));

        // The inspection after slitting is planned to take two hours.
        var bad = await plant.Try(qe, "/api/quality/plans", new { partNumber = "ROLL-100", sequence = 10, title = "Edge check", plannedMinutes = -5, characteristics = new[] { new { name = "Edge free of burrs", kind = "Attribute" } } });
        Assert.Contains("invalid_inspection_time", await bad.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await plant.Ok(qe, "/api/quality/plans", new { partNumber = "ROLL-100", sequence = 10, title = "Edge check", plannedMinutes = 120, characteristics = new[] { new { name = "Edge free of burrs", kind = "Attribute" } } });
        var plans = await Get(qe, "/api/quality/plans?part=ROLL-100");
        Assert.Contains(plans.EnumerateArray(), p => p.GetProperty("sequence").GetInt32() == 10 && p.GetProperty("plannedMinutes").GetInt32() == 120);

        var order = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "ROLL-100", quantity = 10m, dueDate = Day(10), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Customer order" });

        // Before the inspection has happened the order is planned through it, not left unschedulable.
        var first = Id(await plant.Ok(planner, "/api/planning/schedules", new { horizonDays = 14 }));
        var proposed = (await Get(planner, "/api/planning/schedules/proposed")).GetProperty("result");
        var planned = proposed.GetProperty("orders").EnumerateArray().Single(o => o.GetProperty("orderNumber").GetInt64() == order);
        Assert.NotEqual("Unschedulable", planned.GetProperty("status").GetString());
        var ops = proposed.GetProperty("operations").EnumerateArray().Where(o => o.GetProperty("orderNumber").GetInt64() == order).ToDictionary(o => o.GetProperty("sequence").GetInt32());
        Assert.Equal("Inspection", ops[20].GetProperty("bindingConstraint").GetString());
        Assert.True(ops[20].GetProperty("start").GetDateTimeOffset() >= ops[10].GetProperty("end").GetDateTimeOffset().AddMinutes(120));
        await plant.Ok(planner2, $"/api/planning/schedules/{first}/approve", new { reason = "Plan for the week" });

        // Slitting is done and the inspection fails: an NCR stops the order and Forge re-plans at once.
        await plant.Ok(k, "/api/shopfloor/clock-in", new { badge = Sarah });
        await plant.Ok(k, "/api/shopfloor/start", new { badge = Sarah, orderNumber = order, sequence = 10, machineCode = "SLITTER-A" });
        await plant.Ok(k, "/api/shopfloor/report", new { badge = Sarah, orderNumber = order, sequence = 10, good = 10m, scrap = 0m, complete = true });
        await plant.Ok(insp, "/api/quality/inspections", new { orderNumber = order, sequence = 10, quantity = 2m, findings = new object[] { new { number = 1, accepted = false } }, notes = "Burr on both edges" });
        await PropagateAsync(app);

        var replan = await Get(planner, "/api/planning/schedules/proposed");
        Assert.NotEqual(first, replan.GetProperty("versionId").GetInt64());
        Assert.Equal("system:quality-replan", replan.GetProperty("proposedBy").GetString());
        var summary = replan.GetProperty("summary").GetString()!;
        Assert.Contains($"the inspection after operation 10 of order {order} failed", summary, StringComparison.Ordinal);
        Assert.Contains($"Order {order}: projected", summary, StringComparison.Ordinal);
        var stopped = replan.GetProperty("result").GetProperty("orders").EnumerateArray().Single(o => o.GetProperty("orderNumber").GetInt64() == order);
        Assert.Equal("Unschedulable", stopped.GetProperty("status").GetString());

        // The approved schedule stays in force until a planner approves the re-plan; the event is delivered once.
        Assert.Equal(first, (await Get(planner, "/api/planning/schedules/approved")).GetProperty("versionId").GetInt64());
        await PropagateAsync(app);
        Assert.Equal(replan.GetProperty("versionId").GetInt64(), (await Get(planner, "/api/planning/schedules/proposed")).GetProperty("versionId").GetInt64());
        await plant.Ok(planner2, $"/api/planning/schedules/{replan.GetProperty("versionId").GetInt64()}/approve", new { reason = "Accept the re-plan" });
    }
}
