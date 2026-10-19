using Forge.Planning.Engines;
using Forge.Workforce.Contracts;
using static Forge.Planning.Tests.TestFactory;

namespace Forge.Planning.Tests;

/// <summary>ADR 0057 §5 (amends ADR 0056 §1): projected capacity counts a worker where a temporary move or transfer puts them that day.</summary>
public class WorkerMoveProjectionTests
{
    [Fact]
    public void A_temporary_move_counts_the_worker_in_the_new_work_center_only_while_it_lasts()
    {
        var f = Slitting();
        var sarah = f.AddWorker("Sarah", "SLIT-WC", SlitII);
        var snapshot = f.Build();
        var today = DateOnly.FromDateTime(Now.UtcDateTime);
        var moved = snapshot with
        {
            People = snapshot.People.Select(p => p.PersonId != sarah ? p : p with
            {
                WorkCenterPeriods =
                [
                    new WorkCenterPeriod(sarah, "SLIT-WC", DateOnly.MinValue, null, "Home"),
                    new WorkCenterPeriod(sarah, "PAINT-WC", today.AddDays(1), today.AddDays(2), "TemporaryMove", 1),
                ],
            }).ToList(),
        };
        IReadOnlyDictionary<Guid, IReadOnlyList<Span>> schedules = moved.People.ToDictionary(p => p.PersonId, p => (IReadOnlyList<Span>)new List<Span> { new(Now.AddDays(-1), Now.AddDays(10)) });

        string? Where(DateTimeOffset at) => Projection.At(moved, at, schedules).People.Single(p => p.PersonId == sarah).Assignment?.WorkCenterCode;
        Assert.Equal("SLIT-WC", Where(Now));
        Assert.Equal("PAINT-WC", Where(Now.AddDays(1)));
        Assert.Equal("PAINT-WC", Where(Now.AddDays(2)));
        Assert.Equal("SLIT-WC", Where(Now.AddDays(3))); // the end date passed: back home
    }

    [Fact]
    public void A_transfer_changes_the_home_from_its_date_and_keeps_the_earlier_home_before_it()
    {
        var person = Guid.NewGuid();
        var day = new DateOnly(2026, 10, 12);
        var snapshotPerson = new PersonSnapshot(person, "Sam", true, null, null, null, null, [])
        {
            HomeWorkCenterCode = "SLIT-WC",
            WorkCenterPeriods =
            [
                new WorkCenterPeriod(person, "SLIT-WC", DateOnly.MinValue, day.AddDays(-1), "Home"),
                new WorkCenterPeriod(person, "PAINT-WC", day, null, "Home"),
                new WorkCenterPeriod(person, "ASSY-WC", day.AddDays(3), day.AddDays(4), "TemporaryMove", 7),
            ],
        };

        Assert.Equal("SLIT-WC", snapshotPerson.WorkCenterOn(day.AddDays(-1)));
        Assert.Equal("PAINT-WC", snapshotPerson.WorkCenterOn(day));
        Assert.Equal("ASSY-WC", snapshotPerson.WorkCenterOn(day.AddDays(4)));
        Assert.Equal("PAINT-WC", snapshotPerson.WorkCenterOn(day.AddDays(5)));
    }

    [Fact]
    public void Reminders_count_working_days_and_skip_weekends_and_closures()
    {
        var friday = new DateOnly(2026, 10, 9);
        Assert.Equal(2, Forge.Workforce.WorkforceService.WorkingDaysUntil(friday, new DateOnly(2026, 10, 13), new HashSet<DateOnly>()));
        Assert.Equal(1, Forge.Workforce.WorkforceService.WorkingDaysUntil(friday, new DateOnly(2026, 10, 13), new HashSet<DateOnly> { new(2026, 10, 12) }));
        Assert.Equal(0, Forge.Workforce.WorkforceService.WorkingDaysUntil(friday, friday, new HashSet<DateOnly>()));
    }
}
