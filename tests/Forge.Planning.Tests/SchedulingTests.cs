using Forge.Execution.Contracts;
using Forge.Kernel.Engines;
using Forge.Maintenance.Contracts;
using Forge.Planning.Engines;
using Forge.Workforce.Contracts;
using static Forge.Planning.Tests.CalendarTests;
using static Forge.Planning.Tests.TestFactory;

namespace Forge.Planning.Tests;

public class SchedulingTests
{
    private readonly SchedulingEngine engine = new();

    private static TestFactory Plant()
    {
        var f = Slitting().SlittingAreaRule();
        f.AddWorker("Sarah", "SLIT-WC", SlitII, FirstAid);
        f.AddWorker("Michael", "SLIT-WC", SlitII);
        f.AddWorker("Jennifer", "PAINT-WC", PaintQual, FirstAid);
        return f;
    }

    private static OrderInfo Order(long number, int priority, string[] machines, decimal slitMinutes, decimal kitMinutes = 60, string due = "2026-10-09") => new(
        number,
        "ROLL-100",
        "A",
        10,
        DateOnly.Parse(due, System.Globalization.CultureInfo.InvariantCulture),
        priority,
        OrderStatus.Released,
        null,
        null,
        Now,
        [
            new OrderOperationInfo(10, "Slit", "SLIT-WC", RunSlitter, machines, [], slitMinutes, 1, OperationStatus.Pending),
            new OrderOperationInfo(20, "Kit", "SLIT-WC", Kitting, [], [FirstAid], kitMinutes, 1, OperationStatus.Pending),
        ],
        []);

    private ScheduleResult Run(TestFactory f, params OrderInfo[] orders) =>
        engine.Evaluate(f.Build(), new ScheduleRequest(Now, Now.AddDays(14), orders), Now).Output;

    [Fact]
    public void Operations_follow_their_predecessor_and_record_why_they_start_when_they_do()
    {
        var result = Run(Plant(), Order(10001, 3, ["SLITTER-A", "SLITTER-B"], 120));
        var slit = result.Operations.Single(o => o.Sequence == 10);
        var kit = result.Operations.Single(o => o.Sequence == 20);

        Assert.Equal(Now, slit.Start);
        Assert.Equal("Now", slit.BindingConstraint);
        Assert.Equal("SLITTER-A", slit.MachineCode);
        Assert.Equal(slit.End, kit.Start);
        Assert.Equal("Predecessor", kit.BindingConstraint);
        Assert.Equal(DeliveryStatus.OnTime, result.Orders[0].Status);
    }

    [Fact]
    public void A_machine_is_booked_once_and_the_wait_is_explained()
    {
        var result = Run(Plant(), Order(10001, 1, ["SLITTER-B"], 120), Order(10002, 5, ["SLITTER-B"], 120));
        var second = result.Operations.Single(o => o.OrderNumber == 10002 && o.Sequence == 10);

        Assert.Equal(Now.AddMinutes(120), second.Start);
        Assert.Equal("MachineBusy", second.BindingConstraint);
        Assert.Contains("10001", second.BindingDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Spec_77_a_down_machine_moves_work_to_an_alternate_or_waits_for_restoration()
    {
        var f = Plant();
        f.Downtime.Add(new DowntimeWindow(7, "SLITTER-B", DowntimeKind.Unplanned, Now.AddHours(-1), null, "Blade drive fault"));

        var result = Run(f, Order(10001, 1, ["SLITTER-A", "SLITTER-B"], 120), Order(10002, 2, ["SLITTER-B"], 120));

        Assert.Equal("SLITTER-A", result.Operations.Single(o => o.OrderNumber == 10001 && o.Sequence == 10).MachineCode);
        var stuck = result.Operations.Single(o => o.OrderNumber == 10002 && o.Sequence == 10);
        Assert.False(stuck.Scheduled);
        Assert.Equal("Equipment", stuck.BindingConstraint);
        Assert.Contains("Blade drive fault", stuck.BindingDetail, StringComparison.Ordinal);
        Assert.Equal(DeliveryStatus.Unschedulable, result.Orders.Single(o => o.OrderNumber == 10002).Status);
    }

    [Fact]
    public void Spec_81_future_pto_interrupts_work_and_makes_the_order_late()
    {
        var f = Plant();
        var sarah = f.Build().People.Single(p => p.Name == "Sarah").PersonId;
        f.Absences.Add(new UnavailabilityWindow(sarah, UnavailabilityKind.ApprovedPto, At(2026, 10, 2, 4), At(2026, 10, 3, 4)));

        // 10 hours of slitting starting Thursday 10:00 needs Friday, when Slitting lacks its second person.
        var result = Run(f, Order(10001, 1, ["SLITTER-A", "SLITTER-B"], 600, due: "2026-10-02"));
        var slit = result.Operations.Single(o => o.Sequence == 10);

        var pause = Assert.Single(slit.Interruptions);
        Assert.Equal("Safety", pause.Limiting);
        Assert.Equal(At(2026, 10, 2, 11), pause.From);
        Assert.Equal(At(2026, 10, 5, 16), slit.End); // Thursday 5 h, Friday lost, Monday 5 h
        Assert.Equal(DeliveryStatus.Late, result.Orders[0].Status);
        Assert.Contains("interrupted by Safety", result.Orders[0].Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void People_are_not_booked_on_two_things_at_once()
    {
        // Only Sarah holds FIRST-AID in Slitting; two kitting operations cannot overlap.
        var result = Run(Plant(), Order(10001, 1, ["SLITTER-A"], 0, 60), Order(10002, 2, ["SLITTER-B"], 0, 60));
        var kits = result.Operations.Where(o => o.Sequence == 20).OrderBy(o => o.Start).ToList();

        Assert.Equal(kits[0].End, kits[1].Start);
        Assert.Equal("OperatorsBusy", kits[1].BindingConstraint);
    }

    [Fact]
    public void The_schedule_is_deterministic()
    {
        var f = Plant();
        var orders = new[] { Order(10002, 2, ["SLITTER-A", "SLITTER-B"], 300), Order(10001, 2, ["SLITTER-A", "SLITTER-B"], 300) };
        var a = EngineRunner.Run(engine, f.Build(), new ScheduleRequest(Now, Now.AddDays(14), orders), Now);
        var b = EngineRunner.Run(engine, f.Build(), new ScheduleRequest(Now, Now.AddDays(14), orders.Reverse().ToList()), Now);

        Assert.Equal(a.OutputFingerprint, b.OutputFingerprint);
        Assert.Equal(10001, a.Output.Orders[0].OrderNumber); // same priority and due date: lower number first
    }

    [Fact]
    public void Constraints_come_from_recorded_causes_with_alternatives()
    {
        var f = Plant();
        var sarah = f.Build().People.Single(p => p.Name == "Sarah").PersonId;
        f.Absences.Add(new UnavailabilityWindow(sarah, UnavailabilityKind.ApprovedPto, At(2026, 10, 2, 4), At(2026, 10, 3, 4)));
        var snapshot = f.Build();
        var schedule = engine.Evaluate(snapshot, new ScheduleRequest(Now, Now.AddDays(14), [Order(10001, 1, ["SLITTER-A", "SLITTER-B"], 600, due: "2026-10-05")]), Now).Output;

        var findings = new ConstraintEngine().Evaluate(snapshot, new ConstraintRequest(schedule), Now).Output;

        var finding = Assert.Single(findings);
        Assert.Equal("SafetyStaffing", finding.Category);
        Assert.Equal("SafetyStaffing|SLITTING|2026-10-02", finding.Key);
        Assert.Equal([10001L], finding.AffectedOrders);
        Assert.Contains(finding.Alternatives, a => a.StartsWith("Jennifer", StringComparison.Ordinal) && a.Contains(FirstAid, StringComparison.Ordinal));
    }

    [Fact]
    public void A_healthy_plant_produces_no_constraints()
    {
        var snapshot = Plant().Build();
        var schedule = engine.Evaluate(snapshot, new ScheduleRequest(Now, Now.AddDays(14), [Order(10001, 1, ["SLITTER-A", "SLITTER-B"], 120), Order(10002, 2, ["SLITTER-B"], 120)]), Now).Output;
        Assert.Empty(new ConstraintEngine().Evaluate(snapshot, new ConstraintRequest(schedule), Now).Output);
    }
}
