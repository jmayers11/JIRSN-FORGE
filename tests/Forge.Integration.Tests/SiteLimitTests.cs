using System.Net;
using Microsoft.AspNetCore.Hosting;

namespace Forge.Integration.Tests;

/// <summary>
/// Sprint 10 WW-S10-38 (owner decision 2026-10-25): one Forge database runs one site. A second site is refused with a plain reason
/// until multi-site operation is turned on by raising Forge:Sites:MaxSites; the site already there keeps working.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SiteLimitTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_database_runs_one_site_until_multi_site_operation_is_turned_on()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), configureBuilder: builder => builder.UseSetting("Forge:Sites:MaxSites", "1"));
        var plant = await SlittingPlant.BuildAsync(app);
        var second = await plant.Try(plant.Engineer, "/api/resources/sites", new { code = "PLANT2", name = "Second plant", timeZone = "America/Chicago" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Contains("site_limit_reached", await second.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await plant.Ok(plant.Engineer, "/api/resources/shifts", new { code = "DAYS", siteCode = "PLANT1", name = "Days", start = "06:00:00", durationMinutes = 480, days = new[] { 1, 2, 3, 4, 5 } });

        await using var expanded = new ForgeApp(app.ConnectionString, configureBuilder: builder => builder.UseSetting("Forge:Sites:MaxSites", "2"));
        var engineer = expanded.As("eng");
        var response = await engineer.CommandAsync("/api/resources/sites", new { code = "PLANT2", name = "Second plant", timeZone = "America/Chicago", reason = "Expansion" }, "site-limit-expansion");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }
}
