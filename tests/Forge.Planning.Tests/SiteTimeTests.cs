using Forge.Resources.Contracts;

namespace Forge.Planning.Tests;

/// <summary>ADR 0057 §1: "today" is the site's local date, never the UTC date.</summary>
public class SiteTimeTests
{
    private const string NewYork = "America/New_York";

    [Fact]
    public void An_evening_in_New_York_is_still_the_same_day_although_the_UTC_date_has_changed()
    {
        // 20:30 EDT on Oct 19 is 00:30 UTC on Oct 20; 19:30 EST on Nov 30 is 00:30 UTC on Dec 1.
        Assert.Equal(new DateOnly(2026, 10, 19), SiteTime.DateAt(new DateTimeOffset(2026, 10, 20, 0, 30, 0, TimeSpan.Zero), NewYork));
        Assert.Equal(new DateOnly(2026, 11, 30), SiteTime.DateAt(new DateTimeOffset(2026, 12, 1, 0, 30, 0, TimeSpan.Zero), NewYork));
        Assert.Equal(new DateOnly(2026, 10, 20), SiteTime.DateAt(new DateTimeOffset(2026, 10, 20, 4, 0, 0, TimeSpan.Zero), NewYork));
        // A date west of UTC that is ahead: Tokyo is already on the next day at 15:00 UTC.
        Assert.Equal(new DateOnly(2026, 10, 20), SiteTime.DateAt(new DateTimeOffset(2026, 10, 19, 15, 0, 0, TimeSpan.Zero), "Asia/Tokyo"));
    }

    [Fact]
    public void A_local_day_starts_at_local_midnight_across_the_change_from_daylight_saving_time()
    {
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 4, 0, 0, TimeSpan.Zero), SiteTime.StartOf(new DateOnly(2026, 11, 1), NewYork));
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 5, 0, 0, TimeSpan.Zero), SiteTime.StartOf(new DateOnly(2026, 11, 2), NewYork));
        Assert.Equal(TimeSpan.Zero, SiteTime.StartOf(new DateOnly(2026, 11, 2), NewYork).Offset);
    }

    [Fact]
    public void A_missing_or_unknown_zone_is_the_default_and_engines_without_one_stay_in_UTC()
    {
        var evening = new DateTimeOffset(2026, 10, 20, 0, 30, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2026, 10, 19), SiteTime.DateAt(evening, null));
        Assert.Equal(new DateOnly(2026, 10, 19), SiteTime.DateAt(evening, "Not/A_Zone"));
        Assert.Equal(new DateOnly(2026, 10, 20), SiteTime.DateAtOrUtc(evening, null));
        Assert.Equal(new DateOnly(2026, 10, 19), SiteTime.DateAtOrUtc(evening, NewYork));
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero), SiteTime.StartOfOrUtc(new DateOnly(2026, 10, 20), null));
    }

    [Fact]
    public void The_site_clock_answers_for_each_site_and_the_first_site_is_home()
    {
        var clock = new SiteClock();
        Assert.Equal(SiteTime.DefaultTimeZone, clock.HomeTimeZone);

        clock.Load([("PLANT1", "America/Chicago"), ("TOKYO", "Asia/Tokyo")]);
        var at = new DateTimeOffset(2026, 10, 20, 3, 0, 0, TimeSpan.Zero); // 22:00 Oct 19 in Chicago, 12:00 Oct 20 in Tokyo
        Assert.Equal("America/Chicago", clock.HomeTimeZone);
        Assert.Equal(new DateOnly(2026, 10, 19), clock.DateAt(at));
        Assert.Equal(new DateOnly(2026, 10, 20), clock.DateAt(at, "TOKYO"));
        Assert.Equal(new DateOnly(2026, 10, 19), clock.DateAt(at, "UNKNOWN"));

        // An administrator changes the home site's zone: the next answer uses it.
        clock.Remember("PLANT1", "America/Los_Angeles");
        clock.Remember("WEST", "America/Denver");
        Assert.Equal("America/Los_Angeles", clock.HomeTimeZone);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 7, 0, 0, TimeSpan.Zero), clock.StartOf(new DateOnly(2026, 10, 20)));
    }

    [Fact]
    public void A_gauge_due_today_is_still_valid_at_the_end_of_the_swing_shift()
    {
        var caliper = new ToolTypeInfo("CAL", "Caliper", ToolKind.Gauge, 180, null, null);
        var registered = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var gauge = new ToolInfo("MIC-1", "CAL", "x", null, null, ToolCustody.InHouse, null, ToolCondition.Good, [], [], 0, 0, null, null, ToolStatus.Available, registered, "person:1");
        var history = new[] { new ToolEvent(1, "MIC-1", ToolEventKind.Registered, registered, "person:1", Condition: ToolCondition.Good, DueOn: new DateOnly(2026, 10, 19)) };
        var lateSwing = new DateTimeOffset(2026, 10, 20, 1, 30, 0, TimeSpan.Zero); // 21:30 Oct 19 in New York

        Assert.True(ToolValidity.At(gauge, caliper, history, lateSwing, timeZone: NewYork).Valid);
        Assert.False(ToolValidity.At(gauge, caliper, history, lateSwing.AddHours(3), timeZone: NewYork).Valid); // 00:30 Oct 20 there
    }
}
