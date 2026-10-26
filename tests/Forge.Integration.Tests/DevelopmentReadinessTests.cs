using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Forge.Integration.Tests;

/// <summary>
/// Sprint 10 WW-S10-12: before a person is nominated toward a qualification, the nominator sees which prerequisites they hold and lack
/// and how much of a bottleneck the qualification is today (holders and open work that needs it). Nothing is ranked or decided.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DevelopmentReadinessTests(PostgresFixture postgres)
{
    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task Prerequisites_and_todays_bottleneck_are_shown_before_nomination()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var e = plant.Engineer; // also a supervisor: may nominate
        await plant.Ok(e, "/api/competency/qualification-types", new { code = "SLIT-LEAD", name = "Slitting lead", kind = "Process", validityDays = 365 });
        foreach (var prerequisite in new[] { "SLIT-II", "FIRST-AID" })
            await plant.Ok(e, "/api/competency/qualification-types/SLIT-LEAD/prerequisites", new { prerequisiteCode = prerequisite, reason = "Lead needs both" });
        var order = (await plant.Ok(e, "/api/orders/", new { partNumber = "ROLL-100", quantity = 20m, dueDate = DateTime.UtcNow.AddDays(10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), priority = 3 })).GetProperty("entityId").GetString();
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Customer order" });
        string Url(string person, string code) => $"/api/workforce/development-nominations/readiness?personId={plant.People[person]}&qualificationCode={code}";

        // Dana (assembly) toward SLIT-II: no prerequisites; two people hold it and the released order's slitting operation needs it.
        var dana = await Get(e, Url("dana", "SLIT-II"));
        Assert.Equal((false, true, 2, 1), (dana.GetProperty("alreadyHeld").GetBoolean(), dana.GetProperty("prerequisitesMet").GetBoolean(),
            dana.GetProperty("currentHolders").GetInt32(), dana.GetProperty("openOperationsNeedingIt").GetInt32()));
        Assert.Contains("would make 3", dana.GetProperty("benefit").GetString(), StringComparison.Ordinal);

        // Michael toward slitting lead: holds SLIT-II, lacks first aid; nobody holds the lead qualification yet.
        var michael = await Get(e, Url("michael", "SLIT-LEAD"));
        var checks = michael.GetProperty("prerequisites").EnumerateArray().ToDictionary(p => p.GetProperty("code").GetString()!, p => p.GetProperty("held").GetBoolean());
        Assert.Equal((true, false), (checks["SLIT-II"], checks["FIRST-AID"]));
        Assert.False(michael.GetProperty("prerequisitesMet").GetBoolean());
        Assert.Equal(0, michael.GetProperty("currentHolders").GetInt32());
        Assert.Contains("single point of failure", michael.GetProperty("benefit").GetString(), StringComparison.Ordinal);
        Assert.Contains("Prerequisites are missing", michael.GetProperty("benefit").GetString(), StringComparison.Ordinal);

        // Sarah already holds SLIT-II; unknown qualifications and people without nomination authority are refused.
        Assert.True((await Get(e, Url("sarah", "SLIT-II"))).GetProperty("alreadyHeld").GetBoolean());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await e.GetAsync(new Uri(Url("sarah", "NOPE-1"), UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await planner.GetAsync(new Uri(Url("dana", "SLIT-II"), UriKind.Relative))).StatusCode);
    }
}
