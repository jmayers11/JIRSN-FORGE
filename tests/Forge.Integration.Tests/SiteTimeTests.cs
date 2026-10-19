using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Integration.Tests;

/// <summary>
/// ADR 0057 §1 through the real API: late in the swing shift at a New York site the UTC date has already changed, but
/// date rules still use the site's date.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SiteTimeTests(PostgresFixture postgres)
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Calibration_dates_use_the_sites_date_in_the_evening()
    {
        // Yesterday at 21:30 in New York (the default site zone): after 20:00 there, the UTC date is a day ahead.
        var zone = NodaTime.DateTimeZoneProviders.Tzdb["America/New_York"];
        var local = NodaTime.Instant.FromDateTimeOffset(DateTimeOffset.UtcNow).InZone(zone).Date.PlusDays(-1);
        var evening = (local + new NodaTime.LocalTime(21, 30)).InZoneLeniently(zone).ToDateTimeOffset().ToUniversalTime();
        var siteDay = new DateOnly(local.Year, local.Month, local.Day);
        Assert.Equal(siteDay.AddDays(1), new DateOnly(evening.Year, evening.Month, evening.Day));

        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), services => services.AddSingleton<TimeProvider>(new Clock(evening)));
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("site-qe", "QualityEngineer");
        var qe = plant.As("site-qe");

        // A gauge due today is still in calibration at 21:30, whatever the UTC date says.
        await plant.Ok(qe, "/api/quality/gauges", new { code = "MIC-EVE", description = "Micrometer due today", calibrationDue = Iso(siteDay), certificate = "CAL-1" });
        var tools = await qe.GetAsync("/api/resources/tools");
        var text = await tools.Content.ReadAsStringAsync();
        Assert.True(tools.IsSuccessStatusCode, text);
        using (var json = JsonDocument.Parse(text))
        {
            var validity = json.RootElement.EnumerateArray().Single(t => t.GetProperty("tool").GetProperty("code").GetString() == "MIC-EVE").GetProperty("validity");
            Assert.True(validity.GetProperty("valid").GetBoolean(), validity.ToString());
        }

        // Tomorrow at the site is still the future, although it is already that date in UTC.
        var future = await plant.Try(qe, "/api/quality/gauges/MIC-EVE/calibrations", new { calibratedOn = Iso(siteDay.AddDays(1)), dueDate = Iso(siteDay.AddDays(180)), certificate = "CAL-2", reason = "Calibrated" });
        Assert.Contains("invalid_calibration", await future.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await plant.Ok(qe, "/api/quality/gauges/MIC-EVE/calibrations", new { calibratedOn = Iso(siteDay), dueDate = Iso(siteDay.AddDays(180)), certificate = "CAL-2", reason = "Calibrated" });
    }
}
