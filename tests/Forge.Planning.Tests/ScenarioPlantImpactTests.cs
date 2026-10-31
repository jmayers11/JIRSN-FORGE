using Forge.Execution.Contracts;
using Forge.Planning.Engines;

namespace Forge.Planning.Tests;

/// <summary>
/// The whole-plant comparison behind the recovery simulator (owner request 2026-10-31): output, labor, set-up, work in process and
/// commitments per schedule, with standard-cost value only when costing is visible.
/// </summary>
public sealed class ScenarioPlantImpactTests
{
    private static readonly DateTimeOffset From = new(2026, 11, 2, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = From.AddDays(14);
    private static readonly Guid Ann = Guid.NewGuid(), Bo = Guid.NewGuid();

    private static ScheduledOperation Op(long order, int seq, string machine, DateTimeOffset start, double hours, Guid[] crew, string? setup = null) =>
        new(order, seq, $"Op {seq}", "WC", machine, "RUN", true, start, start.AddHours(hours), (decimal)(hours * 60), "Machine", "", [new ScheduledSegment(start, start.AddHours(hours), crew)], [], [], Setup: setup);

    private static ScheduledOrder Order(long number, DeliveryStatus status, DateTimeOffset? done) =>
        new(number, "ROLL-100", 3, DateOnly.FromDateTime(To.UtcDateTime), To, done, status, "");

    private static readonly OrderInfo[] Orders =
    [
        new(1, "ROLL-100", "A", 10m, DateOnly.FromDateTime(To.UtcDateTime), 3, OrderStatus.Released, null, null, From,
            [new(10, "Slit", "WC", "RUN", ["M1"], [], 60m, 1, OperationStatus.Pending, SetupMinutes: 30m, SetupFamily: "W100", FamilySetupMinutes: 5m),
             new(20, "Kit", "WC", "KIT", [], [], 30m, 1, OperationStatus.Pending)], [], ContractId: 7, ContractClin: "0001"),
    ];

    [Fact]
    public void Output_labor_setup_wip_and_commitments_are_compared_from_the_two_schedules()
    {
        var baseline = new ScheduleResult(From, To, [Order(1, DeliveryStatus.OnTime, From.AddHours(4))],
            [Op(1, 10, "M1", From, 2, [Ann], "Same-family set-up: 5 min instead of 30"), Op(1, 20, "M1", From.AddHours(2), 2, [Ann, Bo])]);
        // The alternative moves the slitting later onto M2 with a full set-up and pushes the kitting past the horizon.
        var proposed = new ScheduleResult(From, To, [Order(1, DeliveryStatus.Late, To.AddDays(1))],
            [Op(1, 10, "M2", From.AddDays(13), 3, [Bo], "Full set-up (30 min): no earlier job on M2"), Op(1, 20, "M2", To.AddHours(10), 2, [Ann])]);
        var costing = new ScenarioCosting("USD", 75m, new Dictionary<string, decimal> { ["ROLL-100"] = 20m });

        var impact = ScenarioPlantImpacts.Compare(baseline, proposed, Orders, costing);

        Assert.Equal((10m, 2, 6m, 5m, 0), (impact.Baseline.UnitsCompleted, impact.Baseline.OperationsCompleted, impact.Baseline.LaborHours, impact.Baseline.SetupMinutes, impact.Baseline.OrdersInProcess));
        Assert.Equal((0m, 1, 3m, 30m, 1, 10m), (impact.Proposed.UnitsCompleted, impact.Proposed.OperationsCompleted, impact.Proposed.LaborHours, impact.Proposed.SetupMinutes, impact.Proposed.OrdersInProcess, impact.Proposed.WipUnits));
        Assert.Equal((200m, 450m), (impact.Baseline.OutputValue!.Value, impact.Baseline.LaborCost!.Value));
        Assert.Equal((0m, 225m), (impact.Proposed.OutputValue!.Value, impact.Proposed.LaborCost!.Value));
        Assert.Equal(2, impact.OperationsChanged);
        var slit = impact.Operations.Single(o => o.Sequence == 10);
        Assert.Equal(("M1", "M2", true, true), (slit.BaselineMachine, slit.ProposedMachine, slit.BaselineWithinHorizon, slit.ProposedWithinHorizon));
        var commitment = Assert.Single(impact.Commitments);
        Assert.Equal(("OnTime", "Late", 7L, "0001"), (commitment.BaselineStatus, commitment.ProposedStatus, commitment.ContractId!.Value, commitment.Clin));
        Assert.Equal("USD", impact.Currency);
    }

    [Fact]
    public void Without_visible_costing_no_money_is_shown()
    {
        var schedule = new ScheduleResult(From, To, [Order(1, DeliveryStatus.OnTime, From.AddHours(2))], [Op(1, 10, "M1", From, 1, [Ann]), Op(1, 20, "M1", From.AddHours(1), 1, [Ann])]);
        var impact = ScenarioPlantImpacts.Compare(schedule, schedule, Orders, null);
        Assert.Null(impact.Baseline.OutputValue);
        Assert.Null(impact.Proposed.LaborCost);
        Assert.Empty(impact.Operations);
        Assert.Empty(impact.Commitments);
        Assert.Contains(impact.Notes, n => n.StartsWith("No cost figures", StringComparison.Ordinal));
    }
}
