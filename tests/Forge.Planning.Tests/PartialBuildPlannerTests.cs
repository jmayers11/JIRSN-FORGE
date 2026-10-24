using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Planning.Engines;

namespace Forge.Planning.Tests;

/// <summary>Sprint 10 §17–19: consumption points, approved WIP storage and usable dates decide how far an order can go before its missing material arrives.</summary>
public sealed class PartialBuildPlannerTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(TestFactory.Now.UtcDateTime);

    private static OrderOperationInfo Op(int sequence, decimal good = 0) =>
        new(sequence, $"Op {sequence}", "WC", "RUN", [], [], 100m, 1, good > 0 ? OperationStatus.InProgress : OperationStatus.Pending, good);

    // 50 frames: tube consumed at op 10 (all on hand), motor consumed at op 30 (none on hand, 50 on a PO due in 4 days).
    private static OrderInfo Order(decimal doneAt10 = 0, int? motorAt = 30) => new(
        7, "FRAME", "A", 50m, Today.AddDays(10), 3, OrderStatus.Released, null, null, TestFactory.Now.AddDays(-1),
        [Op(10, doneAt10), Op(20), Op(30)],
        [new OrderMaterialInfo(1, "TUBE", null, 100m, "EA", 10), new OrderMaterialInfo(2, "MOTOR", null, 50m, "EA", motorAt)]);

    private static OrderMaterialStatus Material(decimal tubeOnHand = 100m) => new(7, MaterialState.Expected, Today.AddDays(4), false, "",
    [
        new MaterialLineStatus("MOTOR", 50m, 0m, 50m, 0m, 0m, Today.AddDays(4), [new MaterialAllocation("PO9/1", "PO 9 line 1 (Acme)", 50m, Today.AddDays(4))]),
        new MaterialLineStatus("TUBE", 100m, 0m, 100m, tubeOnHand, 100m - tubeOnHand, null, [new MaterialAllocation("Stock", "Available stock", tubeOnHand, null)]),
    ]);

    private static StorageAvailability Rack(decimal max, decimal onHand = 0, int? dwell = null, params string[] others) =>
        WipStorageQueries.Availability(
            new StorageCapacityRule(1, "WIP-RACK", "FRAME", max, null, dwell, "Rack rating RR-12", StorageRuleState.Active, "person:a", TestFactory.Now, "person:b", TestFactory.Now),
            new StorageOccupancy("WIP-RACK", "FRAME", onHand, 0m, others));

    private static PartialBuildOptions Lead(decimal? quantity = null) => new(Quantity: quantity, ReceivingHours: 4, InspectionHours: 8, PutAwayHours: 2);

    [Fact]
    public void Work_goes_up_to_the_operation_that_consumes_the_missing_part_and_storage_caps_it()
    {
        var plan = PartialBuildPlanner.Plan(Order(), Material(), new Dictionary<int, OperationAnswer>(), [Rack(30)], Lead(), TestFactory.Now, null);

        Assert.Equal(20, plan.StopSequence);
        Assert.Equal(30, plan.BlockedSequence);
        Assert.Equal(30m, plan.Quantity);
        Assert.Equal(30m, plan.StorageQuantity);
        Assert.Equal("WIP-RACK", plan.StorageLocation);
        Assert.True(plan.CanApprove);
        Assert.Equal(new[] { 50m, 50m, 0m }, plan.Operations.Select(o => o.Reachable).ToArray());
        var tube = Assert.Single(plan.Materials);
        Assert.Equal(("TUBE", 60m), (tube.PartNumber, tube.Quantity));    // 2 per frame for the 30 built now
        Assert.Contains("Approved WIP storage at WIP-RACK", plan.Feasibility!.LimitingConstraints);
    }

    [Fact]
    public void Restart_is_the_usable_date_of_the_missing_part_and_dwell_beyond_the_rule_blocks_approval()
    {
        var plan = PartialBuildPlanner.Plan(Order(), Material(), new Dictionary<int, OperationAnswer>(), [Rack(80, dwell: 48)], Lead(), TestFactory.Now, null);

        var motor = Assert.Single(plan.MaterialDates);
        var arrival = new DateTimeOffset(Today.AddDays(4).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        Assert.Equal(arrival.AddHours(14), motor.ExpectedUsableAt);   // receiving 4 + inspection 8 + put-away 2
        Assert.Equal(arrival.AddHours(14), plan.ExpectedRestart);
        Assert.False(plan.CanApprove);
        Assert.Contains(plan.Blockers, b => b.Contains("maximum dwell is 48 hours", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_lead_times_the_restart_is_unknown_and_said_so()
    {
        var plan = PartialBuildPlanner.Plan(Order(), Material(), new Dictionary<int, OperationAnswer>(), [Rack(80)], new PartialBuildOptions(), TestFactory.Now, null);

        Assert.Null(plan.ExpectedRestart);
        Assert.Contains(plan.Notes, n => n.Contains("receiving, inspection and put-away hours", StringComparison.Ordinal));
        Assert.True(plan.CanApprove);
    }

    [Fact]
    public void Waiting_WIP_needs_an_approved_measurable_location()
    {
        var none = PartialBuildPlanner.Plan(Order(), Material(), new Dictionary<int, OperationAnswer>(), [], Lead(), TestFactory.Now, null);
        Assert.False(none.CanApprove);
        Assert.Equal(0m, none.Quantity);
        Assert.Contains(none.Blockers, b => b.Contains("No approved WIP storage rule", StringComparison.Ordinal));

        var mixed = PartialBuildPlanner.Plan(Order(), Material(), new Dictionary<int, OperationAnswer>(), [Rack(80, 0, null, "BRACKET")], Lead(), TestFactory.Now, null);
        Assert.False(mixed.CanApprove);
        Assert.Contains(mixed.Blockers, b => b.Contains("unknown free space", StringComparison.Ordinal));
    }

    [Fact]
    public void Units_already_past_the_consumption_point_carry_their_material()
    {
        // 20 frames already through op 10 used 40 tubes; 40 tubes remain on hand for 20 more frames.
        var order = Order(doneAt10: 20m);
        var material = Material(tubeOnHand: 40m) with
        {
            Lines =
            [
                new MaterialLineStatus("MOTOR", 50m, 0m, 50m, 0m, 0m, Today.AddDays(4), [new MaterialAllocation("PO9/1", "PO 9", 50m, Today.AddDays(4))]),
                new MaterialLineStatus("TUBE", 100m, 40m, 60m, 40m, 20m, null, []),
            ],
        };
        var plan = PartialBuildPlanner.Plan(order, material, new Dictionary<int, OperationAnswer>(), [Rack(100)], Lead(), TestFactory.Now, null);

        Assert.Equal(new[] { 40m, 40m, 0m }, plan.Operations.Select(o => o.Reachable).ToArray());
        Assert.Equal("TUBE", plan.Operations[0].LimitedBy);
        Assert.Equal(40m, plan.Quantity);   // 40 units through op 20: 20 already past op 10 and 20 more
        Assert.Equal(40m, Assert.Single(plan.Materials).Quantity);
    }

    [Fact]
    public void Without_consumption_points_all_material_is_needed_first_and_nothing_can_start()
    {
        var order = Order(motorAt: null) with { Materials = [new OrderMaterialInfo(1, "TUBE", null, 100m, "EA"), new OrderMaterialInfo(2, "MOTOR", null, 50m, "EA")] };
        var plan = PartialBuildPlanner.Plan(order, Material(), new Dictionary<int, OperationAnswer>(), [Rack(80)], Lead(), TestFactory.Now, null);

        Assert.Equal(10, plan.BlockedSequence);
        Assert.False(plan.CanApprove);
        Assert.Contains(plan.Notes.Concat(plan.Blockers), n => n.Contains("consumption point", StringComparison.Ordinal) || n.Contains("cannot start", StringComparison.Ordinal));
    }

    [Fact]
    public void Reserved_stock_goes_only_to_the_order_that_holds_it()
    {
        var material = new MaterialSnapshot(Today, [new MaterialStock("TUBE", 100m)], [], [],
            [
                new MaterialOrder(1, 1, Today, [new OrderMaterialInfo(1, "TUBE", null, 100m, "EA")]),
                new MaterialOrder(2, 5, Today, [new OrderMaterialInfo(1, "TUBE", null, 60m, "EA")]),
            ],
            [new MaterialReservation("PB4", 2, "TUBE", 60m)]);
        var result = new MaterialEngine().Evaluate(material, new MaterialRequest(), TestFactory.Now).Output.ToDictionary(o => o.OrderNumber);

        Assert.Equal(40m, result[1].Lines[0].FromStock);    // the higher-priority order cannot take the reserved 60
        Assert.Equal(60m, result[2].Lines[0].FromStock);
        Assert.Equal(MaterialState.Ready, result[2].State);
        Assert.Contains("partial build PB4", result[2].Lines[0].Allocations[0].Description, StringComparison.Ordinal);
    }
}
