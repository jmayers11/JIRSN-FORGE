using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Forge.Integration.Tests;

/// <summary>
/// Released-order amendments through the real API (owner decisions 2026-10-31): a Manufacturing Engineer proposes, a Planner who did not
/// propose it approves and the change applies at once; quantity, dates, priority, operations and material can change, but never below what
/// was issued, and an amendment proposed against an order that has since changed is refused.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OrderAmendmentTests(PostgresFixture postgres)
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
    public async Task An_engineer_proposes_a_planner_approves_and_the_released_order_changes_within_what_was_issued()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("both", "ManufacturingEngineer", "Planner");
        var (e, both, stores) = (plant.Engineer, plant.As("both"), plant.As("stores"));
        var order = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "ROLL-100", quantity = 100m, dueDate = Day(10), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Customer order" });
        var released = await Get(planner, $"/api/orders/{order}");
        var coil = released.GetProperty("materials").EnumerateArray().Single(m => m.GetProperty("componentPartNumber").GetString() == "COIL-AL");
        var coilLine = coil.GetProperty("lineNumber").GetInt32();
        var coilRequired = coil.GetProperty("requiredQuantity").GetDecimal();
        var operations = released.GetProperty("operations").EnumerateArray().Select(o => o.GetProperty("sequence").GetInt32()).ToList();
        var last = operations.Max();
        var url = $"/api/orders/{order}/amendments";

        // Only engineers propose, only planners approve, never one's own.
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(planner, url, new { rationale = "Bigger run", changes = new { newQuantity = 120m } })).StatusCode);
        await Rejected(plant, e, url, new { rationale = "Nothing", changes = new { } }, "nothing_to_change");
        await Rejected(plant, e, url, new { rationale = "Wrong center", changes = new { addOperations = new[] { new { sequence = last + 10, description = "Pack", workCenterCode = "NOPE-WC", activityCode = "ASSEMBLE", setupMinutes = 5m, runMinutesPerUnit = 0.1m, crewSize = 1 } } } }, "unknown_work_center");
        var own = Id(await plant.Ok(both, url, new { rationale = "Priority bump", changes = new { newPriority = 2 } }));
        await Rejected(plant, both, $"/api/orders/amendments/{own}/decide", new { approve = true }, "separation_of_duties");
        await plant.Ok(both, $"/api/orders/amendments/{own}/withdraw", new { reason = "Not needed" });

        // Quantity 100 → 120 rescales material, plus a packing operation at the end.
        var grow = Id(await plant.Ok(e, url, new
        {
            rationale = "Customer raised the order",
            changes = new { newQuantity = 120m, newDueDate = Day(12), addOperations = new[] { new { sequence = last + 10, description = "Final pack", workCenterCode = "ASSY-WC", activityCode = "ASSEMBLE", setupMinutes = 5m, runMinutesPerUnit = 0.1m, crewSize = 1 } } },
        }));
        var stale = Id(await plant.Ok(e, url, new { rationale = "Expedite", changes = new { newPriority = 1 } }));
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(e, $"/api/orders/amendments/{grow}/decide", new { approve = true })).StatusCode);
        await plant.Ok(planner, $"/api/orders/amendments/{grow}/decide", new { approve = true });
        var grown = await Get(planner, $"/api/orders/{order}");
        Assert.Equal((120m, Day(12)), (grown.GetProperty("quantity").GetDecimal(), grown.GetProperty("dueDate").GetString()));
        var pack = grown.GetProperty("operations").EnumerateArray().Single(o => o.GetProperty("sequence").GetInt32() == last + 10);
        Assert.Equal(("ASSY-WC", 17m), (pack.GetProperty("workCenterCode").GetString(), pack.GetProperty("plannedMinutes").GetDecimal()));
        var coilNow = grown.GetProperty("materials").EnumerateArray().Single(m => m.GetProperty("lineNumber").GetInt32() == coilLine).GetProperty("requiredQuantity").GetDecimal();
        Assert.Equal(decimal.Round(coilRequired * 1.2m, 6), coilNow);

        // The expedite was proposed against the order before it grew: refused.
        await Rejected(plant, planner, $"/api/orders/amendments/{stale}/decide", new { approve = true }, "order_changed");
        Assert.Contains("reason", await (await plant.Try(planner, $"/api/orders/amendments/{stale}/decide", new { approve = false })).Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        await plant.Ok(planner, $"/api/orders/amendments/{stale}/decide", new { approve = false, reason = "Superseded by the larger order" });

        // Material cannot go below what was issued; removing a pending operation and adding it back elsewhere reorders the routing.
        await plant.Ok(stores, "/api/inventory/issues", new { orderNumber = order, partNumber = "COIL-AL", quantity = 50m, locationCode = "STORES" });
        await Rejected(plant, e, url, new { rationale = "Less coil", changes = new { changeMaterials = new[] { new { lineNumber = coilLine, requiredQuantity = 40m } } } }, "below_issued");
        await Rejected(plant, e, url, new { rationale = "Drop coil", changes = new { removeMaterialLines = new[] { coilLine } } }, "below_issued");
        await Rejected(plant, e, url, new { rationale = "Too small", changes = new { newQuantity = 0m } }, "invalid_quantity");
        var reorder = Id(await plant.Ok(e, url, new
        {
            rationale = "Pack before the last operation",
            changes = new { removeOperations = new[] { last + 10 }, addOperations = new[] { new { sequence = last + 5, description = "Final pack", workCenterCode = "ASSY-WC", activityCode = "ASSEMBLE", setupMinutes = 5m, runMinutesPerUnit = 0.1m, crewSize = 1 } } },
        }));
        await plant.Ok(planner, $"/api/orders/amendments/{reorder}/decide", new { approve = true, note = "OK" });
        var sequences = (await Get(planner, $"/api/orders/{order}")).GetProperty("operations").EnumerateArray().Select(o => o.GetProperty("sequence").GetInt32()).ToList();
        Assert.Contains(last + 5, sequences);
        Assert.DoesNotContain(last + 10, sequences);

        var history = (await Get(planner, url)).EnumerateArray().ToDictionary(a => a.GetProperty("amendmentId").GetInt64(), a => a.GetProperty("status").GetString());
        Assert.Equal(("Withdrawn", "Approved", "Rejected", "Approved"), (history[own], history[grow], history[stale], history[reorder]));
    }
}
