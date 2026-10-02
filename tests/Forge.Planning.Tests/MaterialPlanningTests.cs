using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Kernel.Engines;
using Forge.Planning.Engines;
using static Forge.Planning.Tests.TestFactory;

namespace Forge.Planning.Tests;

/// <summary>Spec §11-13 and §38: MRP netting, lot sizing, explosion, pegging and exceptions; material allocation and its effect on the schedule.</summary>
public class MaterialPlanningTests
{
    private static readonly DateOnly Today = new(2026, 10, 1);
    private readonly MrpEngine mrp = new();
    private readonly MaterialEngine material = new();

    private static MrpItem Buy(string part, int lead = 7, decimal safety = 0, LotPolicy policy = LotPolicy.LotForLot, decimal quantity = 0, decimal multiple = 0) =>
        new(part, part, "EA", MakeBuy.Buy, lead, safety, policy, quantity, multiple);

    private static MrpItem Make(string part, int lead = 2) => new(part, part, "EA", MakeBuy.Make, lead, 0, LotPolicy.LotForLot, 0, 0);

    private static MrpRequirement Need(string key, string part, decimal quantity, int inDays, int priority = 5) =>
        new(key, part, quantity, Today.AddDays(inDays), priority, "Demand", $"Demand {key}");

    private static MrpSupply Po(string key, string part, decimal quantity, int inDays) =>
        new(key, part, quantity, Today.AddDays(inDays), "PurchaseOrder", $"PO {key}");

    private static MrpSnapshot Snapshot(MrpItem[] items, MrpRequirement[] needs, MrpSupply[]? supplies = null, MrpStock[]? stock = null, MrpStructure[]? structures = null) =>
        new(Today, items, structures ?? [], stock ?? [], needs, supplies ?? []);

    private MrpResult Run(MrpSnapshot snapshot) => mrp.Evaluate(snapshot, new MrpRequest(), Now).Output;

    [Fact]
    public void Nets_requirements_against_stock_and_receipts_and_offsets_by_lead_time()
    {
        var result = Run(Snapshot(
            [Buy("COIL", lead: 7)],
            [Need("D1", "COIL", 100, 10)],
            [Po("PO1/1", "COIL", 20, 5)],
            [new MrpStock("COIL", 30, 5, 0)]));

        var order = Assert.Single(result.PlannedOrders);
        Assert.Equal(50, order.Quantity);
        Assert.Equal(Today.AddDays(10), order.DueDate);
        Assert.Equal(Today.AddDays(3), order.ReleaseDate);
        Assert.False(order.PastDue);
        Assert.Contains("50 more is needed", order.Explanation, StringComparison.Ordinal);

        // Pegging: the demand is covered by stock, the purchase order and the planned order, in that order.
        var peg = Assert.Single(order.Pegging);
        Assert.Equal("D1", peg.RequirementKey);
        Assert.Equal(50, peg.Quantity);

        var plan = Assert.Single(result.Items);
        Assert.Equal(5, plan.Inspection); // shown, never counted
        Assert.Equal(0, plan.Buckets[^1].ProjectedAvailable);
    }

    [Fact]
    public void Safety_stock_and_fixed_lots_explain_why_the_order_is_bigger_than_the_need()
    {
        var result = Run(Snapshot(
            [Buy("COIL", safety: 50, policy: LotPolicy.FixedQuantity, quantity: 200)],
            [Need("D1", "COIL", 100, 20)],
            stock: [new MrpStock("COIL", 60, 0, 0)]));

        var order = Assert.Single(result.PlannedOrders);
        Assert.Equal(90, order.NetRequirement);
        Assert.Equal(200, order.Quantity);
        Assert.Contains("Fixed lots of 200 makes the order 200", order.Explanation, StringComparison.Ordinal);
        Assert.Contains(order.Pegging, p => p.Kind == "SafetyStock" && p.Quantity == 160);
        Assert.Equal(160, result.Items[0].Buckets[^1].ProjectedAvailable);
    }

    [Theory]
    [InlineData(LotPolicy.LotForLot, 0, 0, 37, 37)]
    [InlineData(LotPolicy.FixedQuantity, 25, 0, 37, 50)]
    [InlineData(LotPolicy.MinimumQuantity, 100, 0, 37, 100)]
    [InlineData(LotPolicy.MinimumQuantity, 10, 12, 37, 48)]
    [InlineData(LotPolicy.LotForLot, 0, 0.5, 2.2, 2.5)]
    public void Lot_sizing(LotPolicy policy, double quantity, double multiple, double net, double expected) =>
        Assert.Equal((decimal)expected, MrpEngine.LotSize(Buy("X", policy: policy, quantity: (decimal)quantity, multiple: (decimal)multiple), (decimal)net));

    [Fact]
    public void Made_parts_explode_through_the_bom_in_force_and_late_releases_are_flagged()
    {
        var structures = new[]
        {
            new MrpStructure("ROLL", "A", Today.AddDays(-100), null, [new BomLineInfo(1, "COIL", 2.5m, 4m)]),
        };
        var result = Run(Snapshot([Make("ROLL", lead: 2), Buy("COIL", lead: 14)], [Need("D1", "ROLL", 100, 10)], structures: structures));

        var roll = result.PlannedOrders.Single(p => p.PartNumber == "ROLL");
        Assert.Equal(100, roll.Quantity);
        Assert.Equal(Today.AddDays(8), roll.ReleaseDate);
        Assert.Equal("A", roll.Revision);

        var coil = result.PlannedOrders.Single(p => p.PartNumber == "COIL");
        Assert.Equal(260, coil.Quantity); // 100 x 2.5 x 1.04
        Assert.Equal(Today.AddDays(8), coil.DueDate);
        Assert.True(coil.PastDue);
        Assert.Equal(Today, coil.ReleaseDate);
        Assert.Equal(roll.Key + "/1", Assert.Single(coil.Pegging).RequirementKey);
        Assert.Contains(result.Exceptions, e => e.PartNumber == "COIL" && e.Kind == "PastDueRelease");
        Assert.Equal(0, result.Items.Single(i => i.PartNumber == "ROLL").Level);
        Assert.Equal(1, result.Items.Single(i => i.PartNumber == "COIL").Level);
    }

    [Fact]
    public void A_receipt_due_after_stock_runs_out_is_asked_to_come_earlier()
    {
        var result = Run(Snapshot([Buy("COIL", lead: 1)], [Need("D1", "COIL", 40, 5)], [Po("PO1/1", "COIL", 40, 15)]));

        Assert.Contains(result.Exceptions, e => e.Kind == "RescheduleIn" && e.Detail.Contains("PO PO1/1", StringComparison.Ordinal));
        Assert.Single(result.PlannedOrders); // MRP still covers the date the receipt misses
    }

    [Fact]
    public void Bom_loops_are_reported_not_followed()
    {
        var structures = new[]
        {
            new MrpStructure("A", "1", Today.AddDays(-10), null, [new BomLineInfo(1, "B", 1, 0)]),
            new MrpStructure("B", "1", Today.AddDays(-10), null, [new BomLineInfo(1, "A", 1, 0)]),
        };
        var result = Run(Snapshot([Make("A"), Make("B")], [Need("D1", "A", 1, 5)], structures: structures));

        Assert.Contains(result.Exceptions, e => e.Kind == "BomLoop" && e.PartNumber == "A");
        Assert.Contains(result.Exceptions, e => e.Kind == "BomLoop" && e.PartNumber == "B");
    }

    [Fact]
    public void Identical_inputs_give_identical_outputs()
    {
        var snapshot = Snapshot([Buy("COIL"), Make("ROLL")], [Need("D2", "ROLL", 5, 3), Need("D1", "COIL", 7, 4)], [Po("P", "COIL", 3, 2)],
            structures: [new MrpStructure("ROLL", "A", Today.AddDays(-1), null, [new BomLineInfo(1, "COIL", 2, 0)])]);
        var first = EngineRunner.Run(mrp, snapshot, new MrpRequest(), Now);
        var second = EngineRunner.Run(mrp, snapshot, new MrpRequest(), Now);
        Assert.Equal(first.InputFingerprint, second.InputFingerprint);
        Assert.Equal(first.OutputFingerprint, second.OutputFingerprint);
    }

    private static MaterialOrder NeedsCoil(long number, int priority, decimal coil) =>
        new(number, priority, Today.AddDays(5), [new OrderMaterialInfo(1, "COIL", "A", coil, "KG")]);

    [Fact]
    public void Stock_and_receipts_are_allocated_in_schedule_order_so_nothing_is_counted_twice()
    {
        var snapshot = new MaterialSnapshot(
            Today,
            [new MaterialStock("COIL", 100)],
            [new MaterialIssued(3, "COIL", 10)],
            [new MaterialSupply("PO1/1", "COIL", 50, Today.AddDays(4), "PO 1 line 1")],
            [NeedsCoil(1, 3, 80), NeedsCoil(2, 1, 60), NeedsCoil(3, 5, 80)]);
        var result = material.Evaluate(snapshot, new MaterialRequest(), Now).Output;

        // Order 2 (priority 1) takes 60 from stock; order 1 takes the other 40 and waits for 40 from the PO;
        // order 3 has 10 issued, gets the PO's last 10 and is short 60.
        Assert.Equal(MaterialState.Ready, result.Single(r => r.OrderNumber == 2).State);
        var one = result.Single(r => r.OrderNumber == 1);
        Assert.Equal(MaterialState.Expected, one.State);
        Assert.Equal(Today.AddDays(4), one.ReadyOn);
        var three = result.Single(r => r.OrderNumber == 3);
        Assert.Equal(MaterialState.Shortfall, three.State);
        Assert.Equal(60, three.Lines[0].Shortfall);
        Assert.Equal(70, three.Lines[0].Remaining);
    }

    [Fact]
    public void An_overdue_receipt_is_flagged_not_assumed()
    {
        var snapshot = new MaterialSnapshot(Today, [], [], [new MaterialSupply("PO1/1", "COIL", 50, Today.AddDays(-2), "PO 1 line 1")], [NeedsCoil(1, 3, 10)]);
        var status = Assert.Single(material.Evaluate(snapshot, new MaterialRequest(), Now).Output);
        Assert.Equal(MaterialState.Expected, status.State);
        Assert.True(status.Overdue);
        Assert.Contains("overdue", status.Summary, StringComparison.Ordinal);
    }

    private static OrderInfo Released(long number) => new(
        number, "ROLL-100", "A", 10, new DateOnly(2026, 10, 20), 3, OrderStatus.Released, null, null, Now,
        [new OrderOperationInfo(10, "Slit", "SLIT-WC", RunSlitter, ["SLITTER-A", "SLITTER-B"], [], 60, 1, OperationStatus.Pending)],
        [new OrderMaterialInfo(1, "COIL-AL", "A", 25, "KG")]);

    private static OrderMaterialStatus Status(long number, MaterialState state, DateOnly? readyOn = null) =>
        new(number, state, readyOn, false, state == MaterialState.Shortfall ? "Short: COIL-AL." : "Waiting for COIL-AL.", []);

    [Fact]
    public void The_scheduler_waits_for_expected_material_and_leaves_short_orders_unscheduled()
    {
        var f = Slitting().SlittingAreaRule();
        f.AddWorker("Sarah", "SLIT-WC", SlitII, FirstAid);
        f.AddWorker("Michael", "SLIT-WC", SlitII);
        var request = new ScheduleRequest(Now, Now.AddDays(14), [Released(1), Released(2), Released(3)], [Status(1, MaterialState.Expected, new DateOnly(2026, 10, 5)), Status(2, MaterialState.Shortfall), Status(3, MaterialState.Ready)]);
        var result = new SchedulingEngine().Evaluate(f.Build(), request, Now).Output;

        var waiting = result.Operations.Single(o => o.OrderNumber == 1);
        Assert.Equal("Material", waiting.BindingConstraint);
        Assert.True(waiting.Start >= new DateTimeOffset(2026, 10, 5, 4, 0, 0, TimeSpan.Zero)); // midnight in New York
        var shortOp = result.Operations.Single(o => o.OrderNumber == 2);
        Assert.False(shortOp.Scheduled);
        Assert.Equal("Material", shortOp.BindingConstraint);
        Assert.Equal(DeliveryStatus.Unschedulable, result.Orders.Single(o => o.OrderNumber == 2).Status);
        Assert.Equal(Now, result.Operations.Single(o => o.OrderNumber == 3).Start);

        var findings = new ConstraintEngine().Evaluate(f.Build(), new ConstraintRequest(result), Now).Output;
        Assert.Contains(findings, c => c.Category == "Material" && c.AffectedOrders.Contains(2));
    }
}
