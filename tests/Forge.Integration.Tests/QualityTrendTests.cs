using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Forge.Integration.Tests;

/// <summary>
/// Sprint 10 WW-S10-09 through the real API: a part's quality trend is shown by week with the machines that ran it (and their recorded
/// stoppages) and the material that went into it, and it never names or blames an operator.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class QualityTrendTests(PostgresFixture postgres)
{
    private const string Sarah = "B-SARAH-QT";

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static string Day(int days) => DateTime.UtcNow.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public async Task The_trend_shows_equipment_and_material_context_and_assigns_no_operator_fault()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var e = plant.Engineer;
        foreach (var (subject, role) in new[] { ("qe", "QualityEngineer"), ("kiosk", "ShopFloorTerminal") })
            await plant.Person(subject, role);
        var (qe, kiosk, stores) = (plant.As("qe"), plant.As("kiosk"), plant.As("stores"));
        await plant.Ok(e, "/api/execution/reason-codes", new { code = "EDGE", kind = "Scrap", description = "Edge wave" });
        await plant.Ok(e, "/api/execution/reason-codes", new { code = "QT-JAM", kind = "Downtime", category = "Unplanned", description = "Web jam" });
        await plant.Ok(e, "/api/resources/shifts", new { code = "ALL", siteCode = "PLANT1", name = "Around the clock", start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 } });
        await plant.Ok(e, "/api/resources/work-centers/SLIT-WC/shifts", new { shiftCode = "ALL" });
        await plant.Ok(e, $"/api/workforce/workers/{plant.People["sarah"]}/shift", new { shiftCode = "ALL" });
        await plant.Ok(plant.Admin, $"/api/workforce/people/{plant.People["sarah"]}/badge", new { badge = Sarah });
        await plant.Ok(kiosk, "/api/shopfloor/clock-in", new { badge = Sarah });

        // Two orders: one slit on Slitter A, which jammed and scrapped two rolls; one on Slitter B, all good.
        var (a, b) = (Id(await plant.Ok(e, "/api/orders/", new { partNumber = "ROLL-100", quantity = 20m, dueDate = Day(10), priority = 3 })),
            Id(await plant.Ok(e, "/api/orders/", new { partNumber = "ROLL-100", quantity = 20m, dueDate = Day(10), priority = 3 })));
        foreach (var order in new[] { a, b })
        {
            await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Customer order" });
            await plant.Ok(stores, "/api/inventory/issues", new { orderNumber = order, partNumber = "COIL-AL", quantity = 52m, locationCode = "STORES" });
        }

        await plant.Ok(kiosk, "/api/shopfloor/start", new { badge = Sarah, orderNumber = a, sequence = 10, machineCode = "SLITTER-A" });
        var jam = Id(await plant.Ok(e, "/api/execution/stoppages", new { machineCode = "SLITTER-A", category = "Unplanned", reasonCode = "QT-JAM", orderNumber = a, sequence = 10,
            startedAt = DateTimeOffset.UtcNow.AddMinutes(-20), note = "Web jammed at the rewind", reason = "Record the jam" }));
        await plant.Ok(e, $"/api/execution/stoppages/{jam}/end", new { reason = "Web rethreaded" });
        await plant.Ok(kiosk, "/api/shopfloor/report", new { badge = Sarah, orderNumber = a, sequence = 10, good = 18m, scrap = 2m, scrapReasonCode = "EDGE", complete = true });
        await plant.Ok(kiosk, "/api/shopfloor/start", new { badge = Sarah, orderNumber = b, sequence = 10, machineCode = "SLITTER-B" });
        await plant.Ok(kiosk, "/api/shopfloor/report", new { badge = Sarah, orderNumber = b, sequence = 10, good = 20m, scrap = 0m, complete = true });
        await plant.Ok(qe, "/api/quality/ncrs", new { partNumber = "ROLL-100", orderNumber = a, sequence = 10, quantity = 2m, defect = "Edge wave", description = "Two rolls wavy after the jam" });

        const string url = "/api/quality/trends?part=ROLL-100";
        var response = await qe.GetAsync(new Uri(url, UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        var trend = JsonDocument.Parse(text).RootElement;

        // No person dimension at all, and it says so.
        Assert.DoesNotContain(plant.People["sarah"].ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("personId", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sarah", text, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Operator fault is not assigned", trend.GetProperty("attribution").GetString(), StringComparison.Ordinal);

        var week = Assert.Single(trend.GetProperty("weeks").EnumerateArray(), w => w.GetProperty("good").GetDecimal() > 0);
        Assert.Equal((38m, 2m, 95.0m, 2m), (week.GetProperty("good").GetDecimal(), week.GetProperty("scrap").GetDecimal(),
            week.GetProperty("yieldPercent").GetDecimal(), week.GetProperty("nonconformingQuantity").GetDecimal()));
        Assert.Equal("EDGE: 2", Assert.Single(week.GetProperty("scrapReasons").EnumerateArray()).GetString());
        var machines = week.GetProperty("machines").EnumerateArray().ToDictionary(m => m.GetProperty("machineCode").GetString()!, m => m.GetProperty("stoppages").GetInt32());
        Assert.Equal((1, 0), (machines["SLITTER-A"], machines["SLITTER-B"]));
        var coil = Assert.Single(week.GetProperty("materials").EnumerateArray());
        Assert.Equal(("COIL-AL", 2, 1), (coil.GetProperty("componentPart").GetString(), coil.GetProperty("orders").GetInt32(), coil.GetProperty("ordersWithNonconformance").GetInt32()));

        // Context says what coincided, never what caused it.
        var context = trend.GetProperty("context").EnumerateArray().Select(c => c.GetString()!).ToList();
        Assert.Contains(context, c => c.StartsWith("SLITTER-A had 1 recorded stoppage", StringComparison.Ordinal) && c.Contains("before drawing a conclusion", StringComparison.Ordinal));
        Assert.Contains(context, c => c.StartsWith("SLITTER-B ran this part", StringComparison.Ordinal));
        Assert.Contains(context, c => c.StartsWith("COIL-AL", StringComparison.Ordinal) && c.Contains("1 of the 1 order(s) with nonconformance", StringComparison.Ordinal));
        Assert.Contains(trend.GetProperty("limitations").EnumerateArray(), l => l.GetString()!.Contains("not a confirmed cause", StringComparison.Ordinal));

        // Quality readers only; the terminal and the operator cannot read it, and a backwards period is refused.
        Assert.Equal(HttpStatusCode.Forbidden, (await kiosk.GetAsync(new Uri(url, UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.As("sarah").GetAsync(new Uri(url, UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await qe.GetAsync(new Uri(url + $"&from={Day(5)}&to={Day(-30)}", UriKind.Relative))).StatusCode);
    }
}
