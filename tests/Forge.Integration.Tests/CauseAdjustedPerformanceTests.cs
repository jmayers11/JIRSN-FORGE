using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Integration.Tests;

/// <summary>
/// Sprint 10 §16 through the real API: paid time is accounted for from attendance, labor, verified downtime and approved coded time;
/// a machine event is charged only where it overlaps the worker's labor; official FPY keeps every unit while the employee-attributable
/// diagnostic waits for a Quality-verified attribution.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CauseAdjustedPerformanceTests(PostgresFixture postgres)
{
    private const string Sarah = "B-SARAH-0016";

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
    public async Task Paid_time_downtime_and_first_pass_quality_are_attributed_from_verified_evidence()
    {
        var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-1).AddHours(13), TimeSpan.Zero);
        var clock = new Clock(start.AddHours(-2));
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), services => services.AddSingleton<TimeProvider>(clock));
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var e = plant.Engineer;
        await plant.Ok(e, "/api/resources/shifts", new { code = "ALL", siteCode = "PLANT1", name = "Around the clock", start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 } });
        await plant.Ok(e, "/api/resources/work-centers/SLIT-WC/shifts", new { shiftCode = "ALL" });
        foreach (var person in new[] { "sarah", "michael" })
            await plant.Ok(e, $"/api/workforce/workers/{plant.People[person]}/shift", new { shiftCode = "ALL" });
        await plant.Ok(plant.Admin, $"/api/workforce/people/{plant.People["sarah"]}/badge", new { badge = Sarah });
        await plant.Person("kiosk", "ShopFloorTerminal");
        await plant.Person("sup", "Supervisor");
        await plant.Person("sup2", "Supervisor");
        await plant.Person("insp", "Inspector");
        await plant.Person("qe", "QualityEngineer");
        await plant.Person("tech", "MaintenanceTechnician");
        var (k, sup, sup2, insp, qe, tech) = (plant.As("kiosk"), plant.As("sup"), plant.As("sup2"), plant.As("insp"), plant.As("qe"), plant.As("tech"));
        var sarah = plant.People["sarah"];
        await plant.Ok(qe, "/api/quality/plans", new { partNumber = "ROLL-100", sequence = 10, title = "Edge check", plannedMinutes = 10, characteristics = new[] { new { name = "Edge free of burrs", kind = "Attribute" } } });
        var order = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "ROLL-100", quantity = 10m, dueDate = Day(10), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Customer order" });

        // A 3-hour shift: 2 hours on the slitter, with a 20-minute verified breakdown in the middle.
        clock.Set(start);
        await plant.Ok(k, "/api/shopfloor/clock-in", new { badge = Sarah });
        await plant.Ok(k, "/api/shopfloor/start", new { badge = Sarah, orderNumber = order, sequence = 10, machineCode = "SLITTER-A" });
        clock.Set(start.AddMinutes(60));
        await plant.Ok(tech, "/api/maintenance/machines/SLITTER-A/down", new { description = "Knife drive fault" });
        clock.Set(start.AddMinutes(80));
        await plant.Ok(tech, "/api/maintenance/machines/SLITTER-A/restore", new { reason = "Drive replaced" });
        clock.Set(start.AddMinutes(120));
        await plant.Ok(k, "/api/shopfloor/report", new { badge = Sarah, orderNumber = order, sequence = 10, good = 10m, scrap = 0m, complete = true });
        clock.Set(start.AddMinutes(125));
        var inspection = Id(await plant.Ok(insp, "/api/quality/inspections", new { orderNumber = order, sequence = 10, quantity = 10m, findings = new object[] { new { number = 1, accepted = true } }, notes = "All edges clean" }));
        clock.Set(start.AddMinutes(180));
        await plant.Ok(k, "/api/shopfloor/clock-out", new { badge = Sarah });
        clock.Set(start.AddHours(4));

        var url = $"/api/workforce/cause-adjusted-performance?personId={sarah}&from={Uri.EscapeDataString(start.AddHours(-1).ToString("O"))}&to={Uri.EscapeDataString(start.AddHours(4).ToString("O"))}";
        var report = await Get(sup, url);
        var shift = Assert.Single(report.GetProperty("shifts").EnumerateArray());
        Assert.Equal(180m, shift.GetProperty("reconciliation").GetProperty("expectedPaidMinutes").GetDecimal());
        Assert.Equal(20m, shift.GetProperty("verifiedExternalDelayMinutes").GetDecimal());
        var machine = Assert.Single(shift.GetProperty("machineEvents").EnumerateArray(), m => m.GetProperty("verified").GetBoolean());
        Assert.Equal(20m, machine.GetProperty("employeeImpactMinutes").GetDecimal());
        var gapBefore = shift.GetProperty("reconciliation").GetProperty("gapMinutes").GetDecimal();
        Assert.True(gapBefore >= 20m, $"The unrecorded hour stays visible as a gap, not imputed: {gapBefore}.");

        // Without a verified attribution the employee metric is N/A; official FPY keeps every first-pass unit.
        var firstPass = report.GetProperty("firstPass");
        Assert.Equal(100m, firstPass.GetProperty("officialProcessFpyPercent").GetDecimal());
        Assert.Equal(JsonValueKind.Null, firstPass.GetProperty("employeeAttributableFpyPercent").ValueKind);

        // Coded time counts only once someone other than the recorder (and not the worker) approves it.
        var coded = await plant.Ok(sup, "/api/workforce/coded-time", new
        {
            personId = sarah, startsAt = start.AddMinutes(130), endsAt = start.AddMinutes(150), code = "Meeting", paid = true, note = "Safety huddle", reason = "Shift log",
        });
        var codedId = coded.GetProperty("entityId").GetString()!;
        Assert.Contains("independent_review_required", await (await plant.Try(sup, $"/api/workforce/coded-time/{codedId}/review", new { approve = true, reason = "Mine" })).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(gapBefore, (await Get(sup, url)).GetProperty("shifts")[0].GetProperty("reconciliation").GetProperty("gapMinutes").GetDecimal());
        await plant.Ok(sup2, $"/api/workforce/coded-time/{codedId}/review", new { approve = true, reason = "Matches the huddle sheet" });
        Assert.Equal(gapBefore - 20m, (await Get(sup, url)).GetProperty("shifts")[0].GetProperty("reconciliation").GetProperty("gapMinutes").GetDecimal());

        // Attribution is proposed from evidence and verified by someone else with disposition authority; operators cannot do either.
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(k, $"/api/quality/inspections/{inspection}/attribution", new { cause = "EmployeeControlled", employeeOpportunity = true, evidence = "Self", reason = "x" })).StatusCode);
        var attribution = Id(await plant.Ok(insp, $"/api/quality/inspections/{inspection}/attribution", new { cause = "EmployeeControlled", employeeOpportunity = true, evidence = "Inspection 1 record; operator set the knives", reason = "First-pass review" }));
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(insp, $"/api/quality/attributions/{attribution}/verify", new { verify = true, reason = "Mine" })).StatusCode);
        await plant.Ok(qe, $"/api/quality/attributions/{attribution}/verify", new { verify = true, reason = "Evidence checked" });
        var verified = (await Get(sup, url)).GetProperty("firstPass");
        Assert.Equal(100m, verified.GetProperty("employeeAttributableFpyPercent").GetDecimal());
        Assert.Equal(100m, verified.GetProperty("employeeAttributionCoveragePercent").GetDecimal());

        // A correction supersedes the verified attribution; both stay in history and the metric follows the new one.
        var correction = Id(await plant.Ok(insp, $"/api/quality/inspections/{inspection}/attribution", new { cause = "Unknown", employeeOpportunity = false, supersedesAttributionId = attribution, evidence = "Knife setup log missing", reason = "Re-review" }));
        await plant.Ok(qe, $"/api/quality/attributions/{correction}/verify", new { verify = true, reason = "Agreed" });
        var history = await Get(qe, $"/api/quality/attributions?inspection={inspection}");
        Assert.Equal(new[] { "Superseded", "Verified" }, history.EnumerateArray().Select(a => a.GetProperty("state").GetString()).ToArray());
        Assert.Equal(JsonValueKind.Null, (await Get(sup, url)).GetProperty("firstPass").GetProperty("employeeAttributableFpyPercent").ValueKind);
    }
}
