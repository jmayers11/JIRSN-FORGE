using Forge.Planning.Engines;
using Forge.Resources.Contracts;
using static Forge.Planning.Tests.TestFactory;

namespace Forge.Planning.Tests;

public class CalendarTests
{
    private static readonly SiteSnapshot Plant = Slitting().Build();

    [Fact]
    public void Shifts_are_local_time_and_clipped_to_the_horizon()
    {
        // Now is Thursday 2026-10-01 10:00 in New York (14:00 UTC); the day shift is 07:00-15:00.
        var spans = WorkCalendar.Instances(DayShift, Plant, Now, Now.AddDays(5));

        Assert.Equal(new Span(Now, At(2026, 10, 1, 19)), spans[0]);              // rest of Thursday
        Assert.Equal(new Span(At(2026, 10, 2, 11), At(2026, 10, 2, 19)), spans[1]); // Friday
        Assert.Equal(new Span(At(2026, 10, 5, 11), At(2026, 10, 5, 19)), spans[2]); // Monday; no weekend
        Assert.Equal(4, spans.Count);
    }

    [Fact]
    public void Daylight_saving_change_moves_the_utc_time_not_the_local_time()
    {
        // Clocks go back on Sunday 2026-11-01 in New York.
        var spans = WorkCalendar.Instances(DayShift, Plant, At(2026, 10, 30, 0), At(2026, 11, 3, 0));

        Assert.Equal(At(2026, 10, 30, 11), spans[0].Start); // Friday 07:00 EDT
        Assert.Equal(At(2026, 11, 2, 12), spans[1].Start);  // Monday 07:00 EST
    }

    [Fact]
    public void Overnight_shifts_cross_midnight_and_closures_skip_days()
    {
        var night = new ShiftInfo("NIGHT", "PLANT1", "Night", new TimeOnly(22, 0), 480, [1, 2, 3, 4, 5]);
        var f = Slitting();
        f.Closures.Add(new SiteClosureInfo("PLANT1", new DateOnly(2026, 10, 2), "Plant shutdown"));
        var plant = f.Build();

        var nights = WorkCalendar.Instances(night, plant, Now, Now.AddDays(5));
        var days = WorkCalendar.Instances(DayShift, plant, Now, Now.AddDays(5));

        Assert.Equal(new Span(At(2026, 10, 2, 2), At(2026, 10, 2, 10)), nights[0]); // Thu 22:00 to Fri 06:00 local
        Assert.DoesNotContain(days, d => d.Start.Day == 2);                         // Friday closed
        Assert.DoesNotContain(nights, n => n.Start == At(2026, 10, 3, 2));            // Friday night closed too
    }

    internal static DateTimeOffset At(int year, int month, int day, int hourUtc) => new(year, month, day, hourUtc, 0, 0, TimeSpan.Zero);
}
