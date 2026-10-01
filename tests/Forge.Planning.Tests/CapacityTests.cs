using Forge.Maintenance.Contracts;
using Forge.Planning.Engines;
using Forge.Workforce.Contracts;
using static Forge.Planning.Tests.CalendarTests;
using static Forge.Planning.Tests.TestFactory;

namespace Forge.Planning.Tests;

public class CapacityTests
{
    private readonly ExecutableCapacityEngine engine = new();

    private static TestFactory Staffed(out Guid sarah, out Guid michael)
    {
        var f = Slitting().SlittingAreaRule();
        sarah = f.AddWorker("Sarah", "SLIT-WC", SlitII, FirstAid);
        michael = f.AddWorker("Michael", "SLIT-WC", SlitII);
        f.AddWorker("Jennifer", "PAINT-WC", PaintQual, FirstAid);
        return f;
    }

    private static MachineCapacity Machine(CapacityResult result, string machine) => result.Machines.Single(m => m.MachineCode == machine && m.ActivityCode == RunSlitter);

    [Fact]
    public void Staffed_machine_is_executable_for_every_working_hour()
    {
        var f = Staffed(out _, out _);
        var result = engine.Evaluate(f.Build(), new CapacityRequest(Now, At(2026, 10, 3, 0)), Now).Output;

        var b = Machine(result, "SLITTER-B");
        Assert.Equal(13m, b.WorkingHours);   // 5 h left Thursday + 8 h Friday
        Assert.Equal(13m, b.ExecutableHours);
        Assert.Contains("Projected", result.Confidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Machine_availability_is_not_executable_capacity_when_safety_coverage_is_missing()
    {
        // Spec §81: Sarah has approved PTO on Friday. Slitters lose their second person that day.
        var f = Staffed(out var sarah, out _);
        f.Absences.Add(new UnavailabilityWindow(sarah, UnavailabilityKind.ApprovedPto, At(2026, 10, 2, 4), At(2026, 10, 3, 4)));

        var result = engine.Evaluate(f.Build(), new CapacityRequest(Now, At(2026, 10, 3, 0)), Now).Output;

        var b = Machine(result, "SLITTER-B");
        Assert.Equal(13m, b.WorkingHours);
        Assert.Equal(5m, b.ExecutableHours);
        var friday = Assert.Single(b.Intervals, i => !i.Executable);
        Assert.Equal("Safety", friday.Limiting);
        Assert.Equal(At(2026, 10, 2, 11), friday.Start);

        // Skills MRP supply: FIRST-AID hours on Friday come only from Jennifer in Paint.
        var firstAidFriday = Assert.Single(result.QualifiedLabor, q => q.Day == new DateOnly(2026, 10, 2) && q.Qualification == FirstAid);
        Assert.Equal(8m, firstAidFriday.Hours);
        Assert.Equal(["Jennifer"], firstAidFriday.People);
    }

    [Fact]
    public void Planned_maintenance_is_limited_by_equipment()
    {
        var f = Staffed(out _, out _);
        f.Downtime.Add(new DowntimeWindow(1, "SLITTER-B", DowntimeKind.Planned, At(2026, 10, 2, 12), At(2026, 10, 2, 14), "PM"));

        var b = Machine(engine.Evaluate(f.Build(), new CapacityRequest(Now, At(2026, 10, 3, 0)), Now).Output, "SLITTER-B");

        var down = Assert.Single(b.Intervals, i => !i.Executable);
        Assert.Equal("Equipment", down.Limiting);
        Assert.Equal(new Span(At(2026, 10, 2, 12), At(2026, 10, 2, 14)), down.Span);
        Assert.Equal(11m, b.ExecutableHours);
    }

    [Fact]
    public void A_qualification_expiring_mid_horizon_removes_capacity_from_that_instant()
    {
        var f = Slitting().SlittingAreaRule();
        var sarah = f.AddWorker("Sarah", "SLIT-WC", SlitII, FirstAid);
        f.AddWorker("Priya", "SLIT-WC", FirstAid);
        f.Expiring(sarah, SlitII, At(2026, 10, 2, 15));

        var b = Machine(engine.Evaluate(f.Build(), new CapacityRequest(Now, At(2026, 10, 3, 0)), Now).Output, "SLITTER-B");

        var lost = Assert.Single(b.Intervals, i => !i.Executable);
        Assert.Equal(At(2026, 10, 2, 15), lost.Start);
    }
}
