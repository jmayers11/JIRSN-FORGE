using Forge.Execution.Contracts;
using Forge.Kernel.Engines;
using Forge.Maintenance.Contracts;
using Forge.Planning.Engines;
using Forge.Quality.Contracts;
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
    private ScheduleResult RunWithQuality(TestFactory f, OrderInfo order, OrderQuality quality) =>
        engine.Evaluate(f.Build(), new ScheduleRequest(Now, Now.AddDays(14), [order], Quality: new Dictionary<long, OrderQuality> { [order.OrderNumber] = quality }), Now).Output;

    private static NcrInfo OpenNcr(long id) => new(
        id, "ROLL-100", 10001, 10, null, 2, "Burr on edge", "Edge burr over limit", NcrStatus.Open, null, null, "insp", Now.AddMinutes(-10), null, null, null, null, null);

    [Fact]
    public void An_inspection_that_has_not_happened_yet_is_planned_time_not_a_stop()
    {
        // ADR 0057 §2: the inspection after operation 10 takes 90 minutes; operation 20 is planned after it.
        var result = RunWithQuality(Plant(), Order(10001, 3, ["SLITTER-A"], 120), new OrderQuality(10001, [10], [], [], new Dictionary<int, int> { [10] = 90 }));
        var slit = result.Operations.Single(o => o.Sequence == 10);
        var kit = result.Operations.Single(o => o.Sequence == 20);

        Assert.True(kit.Scheduled);
        Assert.True(kit.Start >= slit.End!.Value.AddMinutes(90));
        Assert.Equal("Inspection", kit.BindingConstraint);
        Assert.Contains("inspection after operation 10 (90 min)", kit.BindingDetail, StringComparison.Ordinal);
        Assert.NotEqual(DeliveryStatus.Unschedulable, result.Orders[0].Status);
        Assert.Contains("waited for Inspection", result.Orders[0].Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void An_inspection_without_planned_time_changes_nothing_and_a_final_inspection_moves_completion()
    {
        var plain = RunWithQuality(Plant(), Order(10001, 3, ["SLITTER-A"], 120), new OrderQuality(10001, [10], [], []));
        Assert.Equal(plain.Operations.Single(o => o.Sequence == 10).End, plain.Operations.Single(o => o.Sequence == 20).Start);

        var final = RunWithQuality(Plant(), Order(10001, 3, ["SLITTER-A"], 120), new OrderQuality(10001, [20], [], [], new Dictionary<int, int> { [20] = 45 }));
        var kitEnd = final.Operations.Single(o => o.Sequence == 20).End!.Value;
        Assert.Equal(kitEnd.AddMinutes(45), final.Orders[0].ProjectedCompletion);
        Assert.Contains("inspection after operation 20 (45 min)", final.Orders[0].Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_inspection_stops_the_order_until_its_nonconformance_is_decided_then_the_reinspection_is_planned()
    {
        var undecided = RunWithQuality(Plant(), Order(10001, 3, ["SLITTER-A"], 120), new OrderQuality(10001, [10], [], [OpenNcr(9001)], new Dictionary<int, int> { [10] = 30 }, [10]));
        Assert.Equal(DeliveryStatus.Unschedulable, undecided.Orders[0].Status);
        Assert.Equal("Quality", undecided.Operations.Single(o => o.Sequence == 10).BindingConstraint);

        var decided = RunWithQuality(Plant(), Order(10001, 3, ["SLITTER-A"], 120), new OrderQuality(10001, [10], [], [], new Dictionary<int, int> { [10] = 30 }, [10]));
        var kit = decided.Operations.Single(o => o.Sequence == 20);
        Assert.True(kit.Scheduled);
        Assert.Contains("re-inspection after operation 10", kit.BindingDetail, StringComparison.Ordinal);
    }
    private static OrderMaterialStatus Waiting(long order, string part, string source, string description, DateOnly due) =>
        new(order, MaterialState.Expected, due, due < Today, $"Waiting for {part}.",
            [new MaterialLineStatus(part, 10, 0, 10, 0, 0, due, [new MaterialAllocation(source, description, 10, due)])]);

    private static readonly DateOnly Today = new(2026, 10, 1);

    private ScheduleResult RunWithMaterial(IReadOnlyList<OrderInfo> orders, IReadOnlyList<OrderMaterialStatus> material, IReadOnlyDictionary<string, int>? leadTimes = null) =>
        engine.Evaluate(Plant().Build(), new ScheduleRequest(Now, Now.AddDays(14), orders, material, LeadTimeDays: leadTimes), Now).Output;

    [Fact]
    public void A_parent_starts_after_its_sub_assembly_orders_planned_finish_and_components_are_planned_first()
    {
        // ADR 0057 §4: the sub-assembly order is due today; that no longer means the parts are in stores.
        var sub = Order(10001, 5, ["SLITTER-B"], 240) with { PartNumber = "SUB-1" };
        var parent = Order(10002, 1, ["SLITTER-A"], 60);
        var result = RunWithMaterial([parent, sub], [Waiting(10002, "SUB-1", "WO10001", "Order 10001 (Released)", Today)]);
        var subDone = result.Orders.Single(o => o.OrderNumber == 10001).ProjectedCompletion!.Value;
        var slit = result.Operations.Single(o => o.OrderNumber == 10002 && o.Sequence == 10);

        Assert.True(slit.Start >= subDone);
        Assert.Equal("Material", slit.BindingConstraint);
        Assert.Contains("SUB-1 (order 10001), planned to finish", slit.BindingDetail, StringComparison.Ordinal);
        Assert.Equal(new long[] { 10002, 10001 }, result.Orders.Select(o => o.OrderNumber)); // reported in the documented order
    }

    [Fact]
    public void An_overdue_purchase_order_makes_the_order_late_and_estimated_not_unschedulable()
    {
        var order = Order(10002, 1, ["SLITTER-A"], 60);
        var result = RunWithMaterial([order], [Waiting(10002, "COIL-X", "PO5011/1", "PO 5011 line 1 (Acme)", Today.AddDays(-3))], new Dictionary<string, int> { ["COIL-X"] = 4 });
        var planned = result.Orders.Single();
        var slit = result.Operations.Single(o => o.Sequence == 10);

        Assert.NotEqual(DeliveryStatus.Unschedulable, planned.Status);
        Assert.True(slit.Start >= new DateTimeOffset(2026, 10, 5, 4, 0, 0, TimeSpan.Zero)); // Oct 5 at the New York site
        Assert.Contains("Late, needs a supplier date: COIL-X from PO 5011 line 1 (Acme) was due 2026-09-28", planned.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void The_parent_names_the_deepest_cause_and_its_late_sub_assembly_is_shown_late()
    {
        var sub = Order(10001, 5, ["SLITTER-B"], 120, due: "2026-10-20") with { PartNumber = "SUB-1" };
        var parent = Order(10002, 1, ["SLITTER-A"], 60, due: "2026-10-01");
        var result = RunWithMaterial([parent, sub],
            [Waiting(10001, "COIL-X", "PO5011/1", "PO 5011 line 1 (Acme)", Today.AddDays(-3)), Waiting(10002, "SUB-1", "WO10001", "Order 10001 (Released)", new DateOnly(2026, 10, 20))],
            new Dictionary<string, int> { ["COIL-X"] = 4 });
        var top = result.Orders.Single(o => o.OrderNumber == 10002);
        var component = result.Orders.Single(o => o.OrderNumber == 10001);

        Assert.Equal(DeliveryStatus.Late, top.Status);
        Assert.Contains("SUB-1 (order 10001), planned to finish", top.Explanation, StringComparison.Ordinal);
        Assert.Contains("which is waiting for COIL-X from PO 5011 line 1 (Acme), due 2026-09-28, not received", top.Explanation, StringComparison.Ordinal);
        Assert.Equal(DeliveryStatus.Late, component.Status);
        Assert.Contains("Late for order 10002 (ROLL-100)", component.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sub_assembly_that_cannot_be_planned_stops_its_parent_with_the_cause()
    {
        var sub = Order(10001, 5, ["SLITTER-Z"], 120) with { PartNumber = "SUB-1" };
        var parent = Order(10002, 1, ["SLITTER-A"], 60);
        var result = RunWithMaterial([parent, sub], [Waiting(10002, "SUB-1", "WO10001", "Order 10001 (Released)", Today)]);
        var top = result.Orders.Single(o => o.OrderNumber == 10002);

        Assert.Equal(DeliveryStatus.Unschedulable, top.Status);
        Assert.Contains("Waiting for SUB-1 (order 10001), which cannot be planned", top.Explanation, StringComparison.Ordinal);
    }
    private static OrderInfo Family(long number, int priority, string family, decimal setup = 60, decimal same = 10)
    {
        var order = Order(number, priority, ["SLITTER-A"], 120);
        return order with
        {
            Operations = order.Operations.Select(o => o.Sequence == 10 ? o with { SetupMinutes = setup, SetupFamily = family, FamilySetupMinutes = same } : o).ToList(),
        };
    }

    [Fact]
    public void A_same_family_job_after_another_gets_the_reduced_set_up_and_the_scheduler_says_why()
    {
        // ADR 0057 §3.2: 120 planned minutes include a 60-minute set-up; after a job of the same family it takes 10.
        var result = Run(Plant(), Family(10001, 1, "FIX-1"), Family(10002, 2, "FIX-1"), Family(10003, 3, "FIX-2"));
        ScheduledOperation Slit(long order) => result.Operations.Single(o => o.OrderNumber == order && o.Sequence == 10);

        Assert.Equal(120, Slit(10001).PlannedMinutes);
        Assert.Contains("Full set-up (60 min): no earlier job on SLITTER-A", Slit(10001).Setup!, StringComparison.Ordinal);
        Assert.Equal(70, Slit(10002).PlannedMinutes);
        Assert.Contains("Same-family set-up: 10 min instead of 60, after order 10001 operation 10 (FIX-1)", Slit(10002).Setup!, StringComparison.Ordinal);
        Assert.Equal(120, Slit(10003).PlannedMinutes);
        Assert.Contains("is FIX-1, not FIX-2", Slit(10003).Setup!, StringComparison.Ordinal);
    }
}
