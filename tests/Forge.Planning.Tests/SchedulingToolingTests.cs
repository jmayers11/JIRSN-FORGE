using Forge.Execution.Contracts;
using Forge.Kernel.Engines;
using Forge.Planning.Engines;
using Forge.Resources.Contracts;
using static Forge.Planning.Tests.TestFactory;

namespace Forge.Planning.Tests;

/// <summary>Schedule projections use tooling valid at planned use, with the shared capacity timeline.</summary>
public sealed class SchedulingToolingTests
{
    private static SiteSnapshot Staffed()
    {
        var f = Slitting().SlittingAreaRule();
        f.AddWorker("Sarah", "SLIT-WC", SlitII, FirstAid);
        f.AddWorker("Michael", "SLIT-WC", SlitII);
        return f.Build();
    }

    private static OrderInfo Order(long number = 10001, decimal minutes = 60, string[]? machines = null) => new(
        number, "ROLL-100", "A", 10, new DateOnly(2026, 10, 9), 3, OrderStatus.Released, null, null, Now,
        [new OrderOperationInfo(10, "Slit", "SLIT-WC", RunSlitter, machines ?? ["SLITTER-A"], [], minutes, 1, OperationStatus.Pending)], []);

    private static SiteSnapshot Gauge(SiteSnapshot snapshot, DateOnly due, string[]? eligible = null) => snapshot with
    {
        ToolTypes = [new ToolTypeInfo("CALIPER", "Caliper", ToolKind.Gauge, 180, null, null)],
        Tools = [new ToolInfo("CAL-1", "CALIPER", "Gauge", null, "CRIB", ToolCustody.InHouse, null, ToolCondition.Good,
            eligible ?? [], [RunSlitter], 0, 0, due, "LAB-1", ToolStatus.Available, Now.AddDays(-20), "qe")],
        ToolEvents = [new ToolEvent(1, "CAL-1", ToolEventKind.Registered, Now.AddDays(-20), "qe", DueOn: due)],
        ToolRequirements = [new ToolRequirement(1, RunSlitter, null, "CALIPER", 1, Now.AddDays(-20), "me")],
    };

    private static ScheduleResult Run(SiteSnapshot snapshot, params OrderInfo[] orders) =>
        new SchedulingEngine().Evaluate(snapshot, new ScheduleRequest(Now, Now.AddDays(4), orders), Now).Output;

    [Fact]
    public void Gauge_valid_now_but_expired_before_its_planned_operation_blocks_that_operation()
    {
        var snapshot = Gauge(Staffed(), new DateOnly(2026, 10, 1));
        Assert.Equal(EvidenceOutcome.Pass, ExecutableChecks.Tooling(snapshot, "SLITTER-A", RunSlitter, Now).Outcome);
        // The first operation occupies A through the rest of Thursday. The second can only start
        // on Friday, after the gauge's calibration expires at UTC midnight.
        var schedule = Run(snapshot, Order(10001, 300), Order(10002, 60));
        Assert.True(schedule.Operations.Single(o => o.OrderNumber == 10001).Scheduled);
        var waiting = schedule.Operations.Single(o => o.OrderNumber == 10002);
        Assert.False(waiting.Scheduled);
        Assert.Equal("Tooling", waiting.BindingConstraint);
        Assert.Contains("CAL-1 was due for calibration 2026-10-01", waiting.BindingDetail, StringComparison.Ordinal);
        Assert.Equal(DeliveryStatus.Unschedulable, schedule.Orders.Single(o => o.OrderNumber == 10002).Status);
        var capacity = new ExecutableCapacityEngine().Evaluate(snapshot, new CapacityRequest(Now, Now.AddDays(2)), Now).Output;
        var a = capacity.Machines.Single(m => m.MachineCode == "SLITTER-A");
        Assert.Equal(5m, a.ExecutableHours);
        Assert.Contains(a.Intervals, i => !i.Executable && i.Limiting == "Tooling" && i.Start > Now);
    }

    [Fact]
    public void Missing_or_ineligible_required_tools_block_the_schedule_and_eligible_machine_can_recover()
    {
        var withGauge = Gauge(Staffed(), new DateOnly(2027, 1, 1), ["SLITTER-B"]);
        var missing = Run(withGauge with { Tools = [], ToolEvents = [] }, Order());
        Assert.Equal("Tooling", Assert.Single(missing.Operations).BindingConstraint);
        Assert.Contains("no tool of that type is registered", Assert.Single(missing.Operations).BindingDetail, StringComparison.Ordinal);
        var wrongMachine = Run(withGauge, Order());
        Assert.False(Assert.Single(wrongMachine.Operations).Scheduled);
        Assert.Contains("not for SLITTER-A", Assert.Single(wrongMachine.Operations).BindingDetail, StringComparison.Ordinal);
        var recovered = Run(withGauge, Order(machines: ["SLITTER-A", "SLITTER-B"]));
        Assert.True(Assert.Single(recovered.Operations).Scheduled);
        Assert.Equal("SLITTER-B", Assert.Single(recovered.Operations).MachineCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Inactive_machine_or_work_center_cannot_provide_projected_execution(bool inactiveMachine)
    {
        var snapshot = Staffed();
        snapshot = inactiveMachine
            ? snapshot with { Machines = snapshot.Machines.Select(m => m.Code == "SLITTER-A" ? m with { Active = false } : m).ToArray() }
            : snapshot with { WorkCenters = snapshot.WorkCenters.Select(w => w.Code == "SLIT-WC" ? w with { Active = false } : w).ToArray() };
        var schedule = Run(snapshot, Order());
        var blocked = Assert.Single(schedule.Operations);
        Assert.False(blocked.Scheduled);
        Assert.Equal("Equipment", blocked.BindingConstraint);
        Assert.Contains(inactiveMachine ? "SLITTER-A" : "SLIT-WC", blocked.BindingDetail, StringComparison.Ordinal);
        Assert.Contains("inactive", blocked.BindingDetail, StringComparison.Ordinal);
        Assert.Equal(DeliveryStatus.Unschedulable, Assert.Single(schedule.Orders).Status);
    }

    [Fact]
    public void Calibration_event_during_the_horizon_restores_future_tool_capacity_without_rewriting_history()
    {
        var due = new DateOnly(2026, 9, 30);
        var snapshot = Gauge(Staffed(), due);
        var calibrationAt = Now.AddHours(2);
        snapshot = snapshot with
        {
            ToolEvents = [.. snapshot.ToolEvents, new ToolEvent(2, "CAL-1", ToolEventKind.Calibrated, calibrationAt, "qe", CalibratedOn: new DateOnly(2026, 10, 1), DueOn: new DateOnly(2027, 1, 1))],
        };
        var operation = Assert.Single(Run(snapshot, Order()).Operations);
        Assert.True(operation.Scheduled);
        Assert.Equal(calibrationAt, operation.Start);
        Assert.Equal("Tooling", operation.BindingConstraint);
        Assert.Equal(EvidenceOutcome.Fail, ExecutableChecks.Tooling(snapshot, "SLITTER-A", RunSlitter, Now).Outcome);
        Assert.Equal(EvidenceOutcome.Pass, ExecutableChecks.Tooling(snapshot, "SLITTER-A", RunSlitter, calibrationAt).Outcome);
    }

    [Fact]
    public void Zero_duration_operation_still_obeys_required_tooling()
    {
        var snapshot = Gauge(Staffed(), new DateOnly(2026, 9, 30));
        var operation = Assert.Single(Run(snapshot, Order(minutes: 0)).Operations);
        Assert.False(operation.Scheduled);
        Assert.Equal("Tooling", operation.BindingConstraint);
    }
}
