using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Integration.Tests;

/// <summary>
/// Sprint 10 WW-S10-10 through the real API: a labor standard that looks implausible against the labor recorded is flagged; once a
/// supervisor marks it for review it stops counting in anyone's efficiency (it is never a worker score) until an engineer decides:
/// confirmed counts again, corrected stays out.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class LaborStandardReviewTests(PostgresFixture postgres)
{
    private const string Sarah = "B-SARAH-STD";

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Set(DateTimeOffset at) => now = at;
    }

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
    public async Task A_standard_under_review_is_never_a_worker_score_until_an_engineer_confirms_it()
    {
        var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-1).AddHours(13), TimeSpan.Zero);
        var clock = new Clock(start.AddHours(-2));
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), services => services.AddSingleton<TimeProvider>(clock));
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var e = plant.Engineer; // manufacturing engineer (decides) and supervisor
        await plant.Ok(e, "/api/resources/shifts", new { code = "ALL", siteCode = "PLANT1", name = "Around the clock", start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 } });
        await plant.Ok(e, "/api/resources/work-centers/SLIT-WC/shifts", new { shiftCode = "ALL" });
        await plant.Ok(e, $"/api/workforce/workers/{plant.People["sarah"]}/shift", new { shiftCode = "ALL" });
        await plant.Ok(plant.Admin, $"/api/workforce/people/{plant.People["sarah"]}/badge", new { badge = Sarah });
        await plant.Person("kiosk", "ShopFloorTerminal");
        await plant.Person("sup", "Supervisor");
        await plant.Person("insp", "Inspector");
        await plant.Person("qe", "QualityEngineer");
        var (k, sup, insp, qe) = (plant.As("kiosk"), plant.As("sup"), plant.As("insp"), plant.As("qe"));
        var sarah = plant.People["sarah"];
        await plant.Ok(qe, "/api/quality/plans", new { partNumber = "ROLL-100", sequence = 10, title = "Edge check", plannedMinutes = 10, characteristics = new[] { new { name = "Edge free of burrs", kind = "Attribute" } } });
        var order = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "ROLL-100", quantity = 10m, dueDate = DateTime.UtcNow.AddDays(10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Customer order" });

        // Two hours of slitting against a standard of 1.5 minutes a roll for ten rolls: 15 earned minutes, eight times less than recorded.
        clock.Set(start);
        await plant.Ok(k, "/api/shopfloor/clock-in", new { badge = Sarah });
        await plant.Ok(k, "/api/shopfloor/start", new { badge = Sarah, orderNumber = order, sequence = 10, machineCode = "SLITTER-A" });
        clock.Set(start.AddMinutes(120));
        await plant.Ok(k, "/api/shopfloor/report", new { badge = Sarah, orderNumber = order, sequence = 10, good = 10m, scrap = 0m, complete = true });
        clock.Set(start.AddMinutes(125));
        await plant.Ok(insp, "/api/quality/inspections", new { orderNumber = order, sequence = 10, quantity = 10m, findings = new object[] { new { number = 1, accepted = true } }, notes = "All edges clean" });
        clock.Set(start.AddMinutes(180));
        await plant.Ok(k, "/api/shopfloor/clock-out", new { badge = Sarah });
        clock.Set(start.AddHours(4));

        var url = $"/api/workforce/cause-adjusted-performance?personId={sarah}&from={Uri.EscapeDataString(start.AddHours(-1).ToString("O"))}&to={Uri.EscapeDataString(start.AddHours(4).ToString("O"))}";
        var cardsUrl = $"/api/workforce/performance-cards?personIds={sarah}&from={Uri.EscapeDataString(start.AddDays(-80).ToString("O"))}&to={Uri.EscapeDataString(start.AddHours(4).ToString("O"))}";
        async Task<(int InScope, string[] States, string Status)> Efficiency()
        {
            var report = await Get(sup, url);
            return (report.GetProperty("efficiencyScope").GetArrayLength(),
                report.GetProperty("standardReviews").EnumerateArray().Select(n => n.GetProperty("state").GetString()!).ToArray(),
                report.GetProperty("efficiency").GetProperty("status").GetString()!);
        }

        // Implausible is only flagged: it still counts until a person marks the standard for review.
        var flagged = await Efficiency();
        Assert.Equal((1, new[] { "ReviewSuggested" }), (flagged.InScope, flagged.States));
        Assert.Contains("look implausible", flagged.Status, StringComparison.Ordinal);

        // A supervisor marks it; the worker and the terminal cannot, nor can anyone review an operation the routing does not have.
        const string reviews = "/api/engineering/labor-standard-reviews";
        object Raise(int sequence = 10) => new { partNumber = "ROLL-100", revision = "A", sequence, reason = "Run rate ignores the knife change on every roll" };
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(plant.As("sarah"), reviews, Raise())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(k, reviews, Raise())).StatusCode);
        await Rejected(plant, sup, reviews, Raise(99), "unknown_operation");
        var review = Id(await plant.Ok(sup, reviews, Raise()));
        await Rejected(plant, sup, reviews, Raise(), "review_open");

        var underReview = await Efficiency();
        Assert.Equal((0, new[] { "UnderReview" }), (underReview.InScope, underReview.States));
        Assert.Contains("not used as a worker score", underReview.Status, StringComparison.Ordinal);
        var report = await Get(sup, url);
        Assert.Equal(JsonValueKind.Null, report.GetProperty("efficiency").GetProperty("controllableEfficiencyPercent").ValueKind);
        Assert.Equal(1, report.GetProperty("operationsOutsideEfficiencyScope").GetInt32());
        var card = Assert.Single((await Get(sup, cardsUrl)).GetProperty("cards").EnumerateArray());
        Assert.Equal(0, card.GetProperty("efficiency").GetProperty("operationsInScope").GetInt32());
        Assert.Contains("under review", card.GetProperty("efficiency").GetProperty("status").GetString(), StringComparison.Ordinal);

        // Only an engineer decides. Confirmed: it counts again and is no longer flagged.
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(sup, $"{reviews}/{review}/decide", new { status = "Confirmed", resolution = "x", reason = "x" })).StatusCode);
        await Rejected(plant, e, $"{reviews}/{review}/decide", new { status = "Open", resolution = "x", reason = "x" }, "invalid_decision");
        await plant.Ok(e, $"{reviews}/{review}/decide", new { status = "Confirmed", resolution = "Time study 2026-10 matches the routing", reason = "Engineering review" });
        Assert.Equal((1, Array.Empty<string>()), ((await Efficiency()).InScope, (await Efficiency()).States));

        // Raised again and found wrong: work done against it stays out of efficiency.
        var second = Id(await plant.Ok(sup, reviews, Raise()));
        await plant.Ok(e, $"{reviews}/{second}/decide", new { status = "Corrected", resolution = "Rev B routing adds the knife change", reason = "Engineering review" });
        await Rejected(plant, e, $"{reviews}/{second}/decide", new { status = "Confirmed", resolution = "x", reason = "x" }, "review_decided");
        var corrected = await Efficiency();
        Assert.Equal((0, new[] { "Corrected" }), (corrected.InScope, corrected.States));
        var history = (await Get(sup, reviews)).EnumerateArray().Select(r => r.GetProperty("status").GetString()).ToArray();
        Assert.Equal(new[] { "Confirmed", "Corrected" }, history);
    }
}
