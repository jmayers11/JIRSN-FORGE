using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Integration.Tests;

/// <summary>
/// Owner decisions 2026-11-02 through the real host: forecasting from simulated history, approved by a planner into forecast demand; and
/// the V5 allocation optimizer proposing free stock and approved alternates to short released orders, approved into reservations.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ForecastAllocationTests(PostgresFixture postgres)
{
    private static string Day(int fromToday) => DateTime.UtcNow.AddDays(fromToday).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task Rejected(SlittingPlant.Plant plant, HttpClient client, string url, object body, string code)
    {
        var response = await plant.Try(client, url, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{url} -> {(int)response.StatusCode}: {text}");
        Assert.Contains(code, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Simulated_history_is_forecast_and_a_planner_approves_it_into_forecast_demand()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var stores = plant.As("stores");
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(stores, "/api/forecasting/simulate", new { months = 24 })).StatusCode);
        await Rejected(plant, planner, "/api/forecasting/run", new { }, "no_history");

        // Simulated history for the finished part only, marked simulated.
        var simulated = await plant.Ok(planner, "/api/forecasting/simulate", new { months = 24, seed = 2026 });
        Assert.Contains("Simulated 24 months of demand", simulated.GetProperty("message").GetString(), StringComparison.Ordinal);
        var history = await Get(planner, "/api/forecasting/history");
        var roll = history.EnumerateArray().Single(h => h.GetProperty("partNumber").GetString() == "ROLL-100");
        Assert.Equal(24, roll.GetProperty("simulatedMonths").GetInt32());
        Assert.Contains(roll.GetProperty("flags").EnumerateArray(), f => f.GetProperty("code").GetString() == "simulated");
        Assert.DoesNotContain(history.EnumerateArray(), h => h.GetProperty("partNumber").GetString() == "COIL-AL");

        // Three months ahead; the run says it is built on simulated history; nothing is demand until approved.
        await plant.Ok(planner, "/api/forecasting/policy", new { horizon = 3, holdout = 6, intervalPercent = 80, useSimulated = true, reason = "Quarter ahead" });
        var run = await plant.Ok(planner, "/api/forecasting/run", new { });
        Assert.Contains("simulated history", run.GetProperty("message").GetString(), StringComparison.Ordinal);
        var latest = await Get(planner, "/api/forecasting/latest");
        Assert.True(latest.GetProperty("run").GetProperty("simulated").GetBoolean());
        var line = latest.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("forecast").GetProperty("partNumber").GetString() == "ROLL-100").GetProperty("forecast");
        Assert.Equal(3, line.GetProperty("months").GetArrayLength());
        Assert.True(line.GetProperty("scores").GetArrayLength() >= 3);
        Assert.DoesNotContain((await Get(planner, "/api/planning/demand")).EnumerateArray(), d => d.GetProperty("kind").GetString() == "Forecast");

        var runId = latest.GetProperty("run").GetProperty("runId").GetInt64();
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(stores, "/api/forecasting/approve", new { runId, partNumber = "ROLL-100" })).StatusCode);
        await plant.Ok(planner, "/api/forecasting/approve", new { runId, partNumber = "ROLL-100" });
        var forecastDemand = (await Get(planner, "/api/planning/demand")).EnumerateArray().Where(d => d.GetProperty("kind").GetString() == "Forecast").ToList();
        var positive = line.GetProperty("months").EnumerateArray().Count(m => m.GetProperty("forecast").GetDecimal() > 0);
        Assert.Equal(positive, forecastDemand.Count);
        Assert.All(forecastDemand, d => Assert.StartsWith("forecast:ROLL-100/", d.GetProperty("sourceKey").GetString(), StringComparison.Ordinal));
        Assert.Equal("no_change", (await plant.Ok(planner, "/api/forecasting/approve", new { runId, partNumber = "ROLL-100" })).GetProperty("code").GetString());

        // Real history replaces simulated: clearing it leaves nothing to forecast here.
        await plant.Ok(planner, "/api/forecasting/simulated/clear", new { reason = "Real history arrived" });
        Assert.Empty((await Get(planner, "/api/forecasting/history")).EnumerateArray());
        await plant.Ok(planner, "/api/forecasting/import", new { rows = new[] { new { partNumber = "ROLL-100", month = "2025-01-01", quantity = 120m } }, reason = "From the old ERP" });
        Assert.Equal(1, (await Get(planner, "/api/forecasting/history")).EnumerateArray().Single().GetProperty("importedMonths").GetInt32());
    }

    private static async Task BoughtAsync(SlittingPlant.Plant plant, string part)
    {
        await plant.Ok(plant.Engineer, "/api/engineering/items", new { partNumber = part, description = part, unitOfMeasure = "EA", makeBuy = "Buy", leadTimeDays = 10 });
        await plant.Ok(plant.Engineer, $"/api/engineering/items/{part}/revisions", new { revision = "A" });
        await plant.Ok(plant.Approver, $"/api/engineering/items/{part}/revisions/A/release", new { reason = "Baseline" });
    }

    [Fact]
    public async Task The_optimizer_ranks_short_orders_offers_approved_alternates_and_a_person_approves_reservations()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var (e, approver, stores) = (plant.Engineer, plant.Approver, plant.As("stores"));
        await plant.Person("bob", "Buyer");
        var bob = plant.As("bob");
        await BoughtAsync(plant, "CORE-1");
        await BoughtAsync(plant, "CORE-EQ");
        await plant.Ok(e, "/api/engineering/items", new { partNumber = "SPOOL", description = "Spool", unitOfMeasure = "EA", makeBuy = "Make", leadTimeDays = 2 });
        await plant.Ok(e, "/api/engineering/items/SPOOL/revisions", new { revision = "A" });
        await plant.Ok(e, "/api/engineering/items/SPOOL/revisions/A/bom", new { lineNumber = 1, componentPartNumber = "CORE-1", quantityPer = 1m, scrapPercent = 0m });
        await plant.Ok(e, "/api/engineering/items/SPOOL/revisions/A/routing", new { sequence = 10, description = "Wind", workCenterCode = "SLIT-WC", activityCode = "RUN_SLITTER", eligibleMachines = new[] { "SLITTER-A" }, setupMinutes = 0m, runMinutesPerUnit = 1m, crewSize = 1 });
        await plant.Ok(approver, "/api/engineering/items/SPOOL/revisions/A/release", new { reason = "Baseline" });
        var eq = Id(await plant.Ok(bob, "/api/engineering/alternates", new { primaryPart = "CORE-1", alternatePart = "CORE-EQ", kind = "Equivalent", factor = 1m, evidence = "Same drawing" }));
        await plant.Ok(approver, $"/api/engineering/alternates/{eq}/approve", new { reason = "Reviewed" });

        // Order A (due in 20 days) is released first and reserves the 5 cores there are; order B (due in 5 days) gets none.
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "CORE-1", quantity = 5m, locationCode = "STORES", reason = "Opening" });
        var a = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "SPOOL", quantity = 8m, dueDate = Day(20), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{a}/release", new { reason = "Customer order" });
        var b = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "SPOOL", quantity = 6m, dueDate = Day(5), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{b}/release", new { reason = "Customer order" });
        await PropagateAsync(app);
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "CORE-1", quantity = 4m, locationCode = "STORES", reason = "Late delivery" });
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "CORE-EQ", quantity = 10m, locationCode = "STORES", reason = "Second source" });

        // The 4 free cores go to B (earlier due); the rest of B and the rest of A are offered from the approved alternate, for review
        // (the BOM line only suggests alternates). A's 5 reserved cores are never touched.
        var proposal = (await Get(planner, "/api/allocation/proposals")).EnumerateArray().Single(p => p.GetProperty("partNumber").GetString() == "CORE-1");
        var lines = proposal.GetProperty("lines").EnumerateArray().Select(l => (Order: l.GetProperty("orderNumber").GetInt64(), Part: l.GetProperty("offeredPart").GetString()!,
            Quantity: l.GetProperty("quantity").GetDecimal(), Mode: l.GetProperty("mode").GetString()!)).ToList();
        Assert.Equal(new[] { (b, "CORE-1", 4m, "Primary"), (b, "CORE-EQ", 2m, "ReviewAlternate"), (a, "CORE-EQ", 3m, "ReviewAlternate") }, lines.ToArray());
        Assert.Equal(5m, proposal.GetProperty("reservedForOthers").GetDecimal());
        Assert.Contains(proposal.GetProperty("ruledOut").EnumerateArray(), r => r.GetString()!.Contains("never taken", StringComparison.Ordinal));
        Assert.Contains("Ranked 1 of 2", proposal.GetProperty("lines")[0].GetProperty("why").GetString(), StringComparison.Ordinal);

        // Only planners and inventory controllers approve; never more than proposed; a reviewed alternate needs a reason.
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(bob, "/api/allocation/approve", new { orderNumber = b, partNumber = "CORE-1", quantity = 4m })).StatusCode);
        await Rejected(plant, planner, "/api/allocation/approve", new { orderNumber = b, partNumber = "CORE-1", quantity = 5m }, "over_proposal");
        await plant.Ok(planner, "/api/allocation/approve", new { orderNumber = b, partNumber = "CORE-1", quantity = 4m });
        await plant.Ok(stores, "/api/allocation/approve", new { orderNumber = b, partNumber = "CORE-EQ", quantity = 2m, alternateFor = "CORE-1", reason = "Equivalent reviewed for B" });
        var after = (await Get(planner, "/api/allocation/proposals")).EnumerateArray().Single(p => p.GetProperty("partNumber").GetString() == "CORE-1");
        var left = Assert.Single(after.GetProperty("lines").EnumerateArray());
        Assert.Equal((a, "CORE-EQ", 3m), (left.GetProperty("orderNumber").GetInt64(), left.GetProperty("offeredPart").GetString(), left.GetProperty("quantity").GetDecimal()));

        // Issuing the reserved alternate to B draws its alternate reservation, not B's cores; B stays covered.
        await plant.Ok(stores, "/api/inventory/issues", new { orderNumber = b, partNumber = "CORE-EQ", quantity = 2m, locationCode = "STORES", alternateFor = "CORE-1" });
        var again = (await Get(planner, "/api/allocation/proposals")).EnumerateArray().Single(p => p.GetProperty("partNumber").GetString() == "CORE-1");
        Assert.DoesNotContain(again.GetProperty("lines").EnumerateArray(), l => l.GetProperty("orderNumber").GetInt64() == b);
        await Rejected(plant, planner, "/api/allocation/policy", new { rules = Array.Empty<string>(), reason = "x" }, "rules_required");
        await plant.Ok(planner, "/api/allocation/policy", new { rules = new[] { "Priority", "DueDate" }, reason = "Priority first" });
        Assert.Equal("Priority", (await Get(planner, "/api/allocation/policy")).GetProperty("rules")[0].GetString());
    }

    private static async Task PropagateAsync(ForgeApp app)
    {
        var relay = app.Services.GetRequiredService<Forge.Kernel.Postgres.OutboxRelay>();
        while (await relay.RelayBatchAsync(100, CancellationToken.None) > 0)
        {
        }

        await app.Services.GetRequiredService<Forge.Kernel.Postgres.SubscriptionPump>().PumpOnceAsync(1_000_000, CancellationToken.None);
    }
}
