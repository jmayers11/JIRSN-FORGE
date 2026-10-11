using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Forge.Integration.Tests;

/// <summary>
/// V3.7 through the real API: production and delivery goals are kept apart and broken down; a child's required-by
/// date follows its parent's; a goal past its date and not complete is late whatever its order's due date says;
/// a reforecast is recorded per goal and never changed; and the goal-risk view of released orders is exposed.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class V37Tests(PostgresFixture postgres)
{
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

    private static async Task<Dictionary<long, JsonElement>> Goals(HttpClient client) =>
        (await Get(client, "/api/planning/goals")).EnumerateArray().ToDictionary(v => v.GetProperty("goal").GetProperty("goalId").GetInt64());

    [Fact]
    public async Task Goals_break_down_dates_cascade_and_reforecasts_are_kept()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var stores = plant.As("stores");
        var order = Id(await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = "ROLL-100", quantity = 10m, dueDate = Day(10), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Customer order" });
        var demand = Id(await plant.Ok(planner, "/api/planning/demand", new { partNumber = "ROLL-100", quantity = 10m, needDate = Day(12), priority = 2, kind = "CustomerOrder", customer = "Acme", reference = "PO-901" }));

        // Delivery and production goals are kept apart; dates are either a goal's own or follow its parent.
        const string url = "/api/planning/goals";
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(stores, url, new { kind = "Delivery", title = "x", requiredBy = Day(12) })).StatusCode);
        await Rejected(plant, planner, url, new { kind = "Delivery", title = "x", orderNumber = order, requiredBy = Day(12) }, "kind_mismatch");
        await Rejected(plant, planner, url, new { kind = "Delivery", title = "x", requiredBy = Day(12), leadDays = 1 }, "invalid_dates");
        await Rejected(plant, planner, url, new { kind = "Delivery", title = "x", leadDays = 1 }, "invalid_dates");
        var ship = Id(await plant.Ok(planner, url, new { kind = "Delivery", title = "Ship Acme 10 rolls", demandId = demand, requiredBy = Day(12) }));
        var make = Id(await plant.Ok(planner, url, new { kind = "Production", title = "Slit and kit the rolls", parentGoalId = ship, orderNumber = order, leadDays = 2 }));
        await Rejected(plant, planner, url, new { kind = "Delivery", title = "x", parentGoalId = make, leadDays = 0 }, "kind_mismatch");

        var goals = await Goals(planner);
        var shipGoal = goals[ship].GetProperty("goal");
        var makeGoal = goals[make].GetProperty("goal");
        Assert.Equal((Day(12), Day(10)), (shipGoal.GetProperty("requiredBy").GetString(), makeGoal.GetProperty("requiredBy").GetString()));
        Assert.Equal((0m, 10m, "Delivery"), (shipGoal.GetProperty("done").GetDecimal(), shipGoal.GetProperty("target").GetDecimal(), shipGoal.GetProperty("kind").GetString()));
        Assert.True(makeGoal.GetProperty("remainingMinutes").GetDecimal() > 0);
        Assert.NotEmpty(makeGoal.GetProperty("evidence").EnumerateArray());

        // Moving the delivery date moves the production goal under it.
        await Rejected(plant, planner, $"{url}/{ship}", new { title = "Ship Acme 10 rolls", requiredBy = Day(11) }, "reason_required");
        await plant.Ok(planner, $"{url}/{ship}", new { title = "Ship Acme 10 rolls", requiredBy = Day(11), reason = "Customer pulled in" });
        Assert.Equal(Day(9), (await Goals(planner))[make].GetProperty("goal").GetProperty("requiredBy").GetString());

        // A goal past its own date is late, whatever its order's due date says.
        var overdue = Id(await plant.Ok(planner, url, new { kind = "Production", title = "First article three days ago", orderNumber = order, requiredBy = Day(-3) }));
        var late = (await Goals(planner))[overdue].GetProperty("goal");
        Assert.Equal("Late", late.GetProperty("state").GetString());

        // A reforecast is recorded per goal and kept.
        await plant.Ok(planner, $"{url}/reforecast", new { });
        goals = await Goals(planner);
        Assert.All(goals.Values, v => Assert.Single(v.GetProperty("history").EnumerateArray()));
        Assert.Equal("Late", goals[overdue].GetProperty("history")[0].GetProperty("state").GetString());

        // The goal-risk view of released orders, the report and the Analyst.
        var risk = (await Get(planner, "/api/planning/goal-risk")).EnumerateArray().Single(l => l.GetProperty("orderNumber").GetInt64() == order);
        Assert.Equal("ROLL-100", risk.GetProperty("partNumber").GetString());
        var report = await Get(planner, "/api/reports/goal-attainment?status=Late");
        Assert.Contains(report.GetProperty("rows").EnumerateArray(), r => r.GetProperty("cells")[0].GetString() == overdue.ToString(CultureInfo.InvariantCulture));
        var answer = await (await planner.PostAsJsonAsync(new Uri("/api/analyst/ask", UriKind.Relative), new { question = "Which goals are at risk?" })).Content.ReadFromJsonAsync<JsonElement>();
        var request = answer.GetProperty("interpretation").GetProperty("report");
        Assert.Equal(("goal-attainment", "AtRisk"), (request.GetProperty("reportId").GetString(), request.GetProperty("parameters").GetProperty("status").GetString()));

        // Closing: the goals below first; history stays.
        await Rejected(plant, planner, $"{url}/{ship}/close", new { reason = "Shipped" }, "goal_has_children");
        await plant.Ok(planner, $"{url}/{make}/close", new { reason = "Done" });
        await plant.Ok(planner, $"{url}/{ship}/close", new { reason = "Done" });
        Assert.False((await Goals(planner)).ContainsKey(ship));
        var closed = (await Get(planner, $"{url}?includeClosed=true")).EnumerateArray().Single(v => v.GetProperty("goal").GetProperty("goalId").GetInt64() == ship);
        Assert.Equal(("Closed", 1), (closed.GetProperty("status").GetString(), closed.GetProperty("history").GetArrayLength()));
    }
}
