using System.Globalization;
using System.Net;
using System.Text.Json;
using Forge.Host.Notifications;
using Forge.Kernel.Postgres;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Integration.Tests;

/// <summary>
/// Statistical process control through the real API (owner decisions 2026-11-01): measurements become an individuals chart, a new
/// measurement that breaks a rule is recorded once and quality engineers are told, a Quality Manager keeps the required Cpk and marks
/// characteristics critical, and a quality engineer acknowledges a signal with a note. Nothing is held.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SpcTests(PostgresFixture postgres)
{
    private const string Sarah = "B-SARAH-0001";

    private static string Day(int fromToday) => DateTime.UtcNow.AddDays(fromToday).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task PropagateAsync(ForgeApp app)
    {
        var relay = app.Services.GetRequiredService<OutboxRelay>();
        for (var round = 0; round < 3; round++)
        {
            while (await relay.RelayBatchAsync(100, CancellationToken.None) > 0)
            {
            }

            await app.Services.GetRequiredService<SubscriptionPump>().PumpOnceAsync(1_000_000, CancellationToken.None);
        }

        var delivery = app.Services.GetRequiredService<NotificationDeliveryService>();
        while (await delivery.DispatchDueAsync(50, CancellationToken.None) > 0)
        {
        }
    }

    [Fact]
    public async Task A_measurement_that_breaks_a_rule_tells_quality_engineers_once_and_holds_nothing()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var e = plant.Engineer;
        await plant.Ok(e, "/api/resources/shifts", new { code = "ALL", siteCode = "PLANT1", name = "Around the clock", start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 } });
        await plant.Ok(e, "/api/resources/work-centers/SLIT-WC/shifts", new { shiftCode = "ALL" });
        await plant.Ok(e, $"/api/workforce/workers/{plant.People["sarah"]}/shift", new { shiftCode = "ALL" });
        await plant.Ok(plant.Admin, $"/api/workforce/people/{plant.People["sarah"]}/badge", new { badge = Sarah });
        await plant.Person("kiosk", "ShopFloorTerminal");
        await plant.Person("qe", "QualityEngineer");
        await plant.Person("qm", "QualityManager");
        await plant.Person("insp", "Inspector");
        var (k, qe, qm, insp) = (plant.As("kiosk"), plant.As("qe"), plant.As("qm"), plant.As("insp"));
        await plant.Ok(qe, "/api/quality/gauges", new { code = "MIC-1", description = "Micrometer 0-150 mm", calibrationDue = Day(30), certificate = "CAL-2231" });
        await plant.Ok(qe, "/api/quality/plans", new
        {
            partNumber = "ROLL-100", sequence = 10, title = "Slit width",
            characteristics = new object[] { new { name = "Width", kind = "Variable", nominal = 100m, lower = 99.5m, upper = 100.5m, unit = "mm" } },
        });

        // The Quality Manager keeps the policy; a quality engineer cannot change it.
        var policy = await Get(qe, "/api/quality/spc/policy");
        Assert.Equal((1.33m, 1.67m, 20), (policy.GetProperty("policy").GetProperty("minimumCpk").GetDecimal(), policy.GetProperty("policy").GetProperty("criticalMinimumCpk").GetDecimal(), policy.GetProperty("policy").GetProperty("minimumPoints").GetInt32()));
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(qe, "/api/quality/spc/policy", new { minimumCpk = 1m, criticalMinimumCpk = 1m, minimumPoints = 5, notifyOnSignal = true, reason = "x" })).StatusCode);
        var invalid = await plant.Try(qm, "/api/quality/spc/policy", new { minimumCpk = 1.67m, criticalMinimumCpk = 1.33m, minimumPoints = 5, notifyOnSignal = true, reason = "Pilot" });
        Assert.Contains("invalid_cpk", await invalid.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await plant.Ok(qm, "/api/quality/spc/policy", new { minimumCpk = 1.33m, criticalMinimumCpk = 1.67m, minimumPoints = 5, notifyOnSignal = true, reason = "Pilot on slitting" });

        var order = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "ROLL-100", quantity = 10m, dueDate = Day(10), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Customer order" });
        await plant.Ok(k, "/api/shopfloor/clock-in", new { badge = Sarah });
        await plant.Ok(k, "/api/shopfloor/start", new { badge = Sarah, orderNumber = order, sequence = 10, machineCode = "SLITTER-A" });
        await plant.Ok(k, "/api/shopfloor/report", new { badge = Sarah, orderNumber = order, sequence = 10, good = 10m, scrap = 0m, complete = true });

        // Seven steady measurements, then one inside the drawing but far outside the process's own limits.
        foreach (var width in new[] { 100.00m, 100.02m, 99.98m, 100.01m, 99.99m, 100.00m, 100.01m, 100.40m })
        {
            await plant.Ok(insp, "/api/quality/inspections", new { orderNumber = order, sequence = 10, quantity = 1m, gaugeCode = "MIC-1", findings = new object[] { new { number = 1, measured = width } } });
            await PropagateAsync(app);
        }

        var signal = Assert.Single((await Get(qe, "/api/quality/spc/signals?open=true")).EnumerateArray());
        Assert.Equal(("BeyondThreeSigma", 100.40m, order), (signal.GetProperty("rule").GetString(), signal.GetProperty("value").GetDecimal(), signal.GetProperty("orderNumber").GetInt64()));
        var alert = Assert.Single((await Get(qe, "/api/notifications/inbox")).EnumerateArray(), a => a.GetProperty("title").GetString() == "Control chart signal");
        Assert.Contains("ROLL-100 op 10 Width", alert.GetRawText(), StringComparison.Ordinal);

        // The chart: limits from the points, capability against the drawing; the rule break is at the last point.
        var chart = await Get(qe, "/api/quality/spc/chart?part=ROLL-100&sequence=10&number=1");
        Assert.Equal(8, chart.GetProperty("points").GetArrayLength());
        Assert.True(chart.GetProperty("capable").GetBoolean());
        Assert.Equal(7, Assert.Single(chart.GetProperty("violations").EnumerateArray()).GetProperty("pointIndex").GetInt32());
        Assert.True(chart.GetProperty("upperControlLimit").GetDecimal() < 100.40m);

        // Critical characteristics need the higher Cpk.
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(qe, "/api/quality/spc/critical", new { partNumber = "ROLL-100", sequence = 10, number = 1, critical = true, reason = "x" })).StatusCode);
        await plant.Ok(qm, "/api/quality/spc/critical", new { partNumber = "ROLL-100", sequence = 10, number = 1, critical = true, reason = "Fit with the customer's spindle" });
        var summary = Assert.Single((await Get(qe, "/api/quality/spc/charts")).EnumerateArray());
        Assert.Equal((true, 1.67m, 1), (summary.GetProperty("critical").GetBoolean(), summary.GetProperty("requiredCpk").GetDecimal(), summary.GetProperty("openSignals").GetInt32()));

        // Nothing was held: the order is still released and no NCR was raised.
        Assert.Equal("Released", (await Get(planner, $"/api/orders/{order}")).GetProperty("status").GetString());
        Assert.Empty((await Get(qe, "/api/quality/ncrs")).EnumerateArray());

        // Delivered again, the signal is not recorded twice. A quality engineer acknowledges it with a note.
        await PropagateAsync(app);
        Assert.Single((await Get(qe, "/api/quality/spc/signals?open=false")).EnumerateArray());
        var id = signal.GetProperty("signalId").GetInt64();
        var noNote = await plant.Try(qe, $"/api/quality/spc/signals/{id}/acknowledge", new { note = " " });
        Assert.Contains("note_required", await noNote.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await plant.Ok(qe, $"/api/quality/spc/signals/{id}/acknowledge", new { note = "Blade shifted; reset and re-measured the roll" });
        Assert.Empty((await Get(qe, "/api/quality/spc/signals?open=true")).EnumerateArray());
        var done = Assert.Single((await Get(qe, "/api/quality/spc/signals?open=false")).EnumerateArray());
        Assert.Equal("Acknowledged", done.GetProperty("status").GetString());
    }
}
