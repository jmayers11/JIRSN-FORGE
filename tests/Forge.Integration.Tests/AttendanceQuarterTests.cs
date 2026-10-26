using System.Net;
using System.Text.Json;
using Forge.Workforce;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Integration.Tests;

/// <summary>
/// Sprint 10 WW-S10-17: the attendance summary's current period starts again on the first day of each calendar quarter at the site,
/// while the previous quarter, or any earlier quarter by name, stays queryable. The rollover follows the plant's local date, not UTC.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AttendanceQuarterTests(PostgresFixture postgres)
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Set(DateTimeOffset at) => now = at;
    }

    [Fact]
    public void Quarters_follow_the_calendar_and_only_started_quarters_can_be_named()
    {
        Assert.Equal(new AttendancePeriod("2026-Q4", new DateOnly(2026, 10, 1), new DateOnly(2027, 1, 1)), AttendancePeriods.QuarterOf(new DateOnly(2026, 12, 31)));
        Assert.Equal(new AttendancePeriod("2026-Q1", new DateOnly(2026, 1, 1), new DateOnly(2026, 4, 1)), AttendancePeriods.QuarterOf(new DateOnly(2026, 1, 1)));
        var today = new DateOnly(2027, 1, 2);
        Assert.Equal("2027-Q1", AttendancePeriods.Resolve(null, null, today)!.Label);
        Assert.Equal(new AttendancePeriod("2026-Q4", new DateOnly(2026, 10, 1), new DateOnly(2027, 1, 1)), AttendancePeriods.Resolve("previous-quarter", null, today));
        Assert.Equal("2025-Q2", AttendancePeriods.Resolve(null, "2025-q2", today)!.Label);
        Assert.Null(AttendancePeriods.Resolve(null, "2027-Q2", today)); // not started
        Assert.Null(AttendancePeriods.Resolve(null, "2026-Q5", today));
        Assert.Null(AttendancePeriods.Resolve("fortnight", null, today));
    }

    [Fact]
    public async Task The_summary_rolls_over_on_the_sites_quarter_start_and_keeps_the_previous_quarter_queryable()
    {
        // 03:30 UTC on 1 October is still 30 September in New York: the plant is in the third quarter.
        var clock = new Clock(new DateTimeOffset(2026, 10, 1, 3, 30, 0, TimeSpan.Zero));
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), services => services.AddSingleton<TimeProvider>(clock));
        var plant = await SlittingPlant.BuildAsync(app, TestSite.NewYork);
        await plant.Person("reviewer", "Supervisor");
        var sup = plant.As("reviewer");
        async Task<JsonElement> Summary(string query)
        {
            var response = await sup.GetAsync(new Uri("/api/workforce/attendance-intelligence" + query, UriKind.Relative));
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, text);
            return JsonDocument.Parse(text).RootElement.Clone();
        }

        (string? Label, DateTimeOffset Start, DateTimeOffset End) Period(JsonElement s) =>
            (s.GetProperty("periodLabel").GetString(), s.GetProperty("periodStart").GetDateTimeOffset(), s.GetProperty("periodEnd").GetDateTimeOffset());

        Assert.Equal(("2026-Q3 to date", new DateTimeOffset(2026, 7, 1, 4, 0, 0, TimeSpan.Zero), clock.GetUtcNow()), Period(await Summary("")));

        // Midnight in New York (04:00 UTC): the current period starts again; the third quarter is complete and still there.
        clock.Set(new DateTimeOffset(2026, 10, 1, 5, 0, 0, TimeSpan.Zero));
        var october = new DateTimeOffset(2026, 10, 1, 4, 0, 0, TimeSpan.Zero);
        Assert.Equal(("2026-Q4 to date", october, clock.GetUtcNow()), Period(await Summary("")));
        Assert.Equal(("2026-Q4 to date", october, clock.GetUtcNow()), Period(await Summary("?period=quarter")));
        var previous = ("2026-Q3", new DateTimeOffset(2026, 7, 1, 4, 0, 0, TimeSpan.Zero), october);
        Assert.Equal(previous, Period(await Summary("?period=previous-quarter")));
        Assert.Equal(previous, Period(await Summary("?quarter=2026-Q3")));
        Assert.Equal(("2026-Q1", new DateTimeOffset(2026, 1, 1, 5, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 4, 1, 4, 0, 0, TimeSpan.Zero)), Period(await Summary("?quarter=2026-Q1")));

        // A quarter that has not started, or a period that is not one, is refused; explicit dates still work and carry no quarter name.
        Assert.Equal(HttpStatusCode.BadRequest, (await sup.GetAsync(new Uri("/api/workforce/attendance-intelligence?quarter=2027-Q1", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await sup.GetAsync(new Uri("/api/workforce/attendance-intelligence?period=fortnight", UriKind.Relative))).StatusCode);
        var explicitRange = await Summary("?from=2026-09-01T00:00:00Z&to=2026-09-15T00:00:00Z");
        Assert.Equal(JsonValueKind.Null, explicitRange.GetProperty("periodLabel").ValueKind);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), explicitRange.GetProperty("periodStart").GetDateTimeOffset());
    }
}
