using Forge.Resources.Contracts;

namespace Forge.Planning.Tests;

public sealed class OperationalDayResolverTests
{
    [Fact]
    public void An_overnight_occurrence_keeps_the_date_on_which_the_shift_started()
    {
        var shift = new ShiftInfo("NIGHT", "PLANT1", "Night", new TimeOnly(22, 0), 480, [1, 2, 3, 4, 5]);

        var resolved = OperationalDayResolver.Resolve(shift, "America/New_York", new DateTimeOffset(2026, 10, 20, 5, 15, 0, TimeSpan.Zero));

        Assert.NotNull(resolved);
        Assert.Equal(new DateOnly(2026, 10, 19), resolved.OperationalDate);
        Assert.Equal("Current", resolved.Relation);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 2, 0, 0, TimeSpan.Zero), resolved.StartsAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero), resolved.EndsAt);
    }

    [Fact]
    public void A_pre_shift_login_resolves_to_the_upcoming_assigned_occurrence()
    {
        var shift = new ShiftInfo("DAY", "PLANT1", "Day", new TimeOnly(7, 0), 480, [1, 2, 3, 4, 5]);

        var resolved = OperationalDayResolver.Resolve(shift, "America/New_York", new DateTimeOffset(2026, 10, 20, 10, 30, 0, TimeSpan.Zero));

        Assert.NotNull(resolved);
        Assert.Equal(new DateOnly(2026, 10, 20), resolved.OperationalDate);
        Assert.Equal("Upcoming", resolved.Relation);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 11, 0, 0, TimeSpan.Zero), resolved.StartsAt);
    }

    [Fact]
    public void A_site_closure_is_skipped_when_resolving_an_upcoming_occurrence()
    {
        var shift = new ShiftInfo("DAY", "PLANT1", "Day", new TimeOnly(7, 0), 480, [1, 2, 3, 4, 5]);
        var closure = new SiteClosureInfo("PLANT1", new DateOnly(2026, 10, 20), "Shutdown");

        var resolved = OperationalDayResolver.Resolve(shift, "America/New_York", new DateTimeOffset(2026, 10, 20, 22, 0, 0, TimeSpan.Zero), [closure]);

        Assert.NotNull(resolved);
        Assert.Equal(new DateOnly(2026, 10, 21), resolved.OperationalDate);
        Assert.Equal("Upcoming", resolved.Relation);
    }

    [Fact]
    public void A_shift_with_no_scheduled_weekdays_has_no_operational_occurrence()
    {
        var shift = new ShiftInfo("NONE", "PLANT1", "None", new TimeOnly(7, 0), 480, []);

        Assert.Null(OperationalDayResolver.Resolve(shift, "America/New_York", DateTimeOffset.UtcNow));
    }
}
