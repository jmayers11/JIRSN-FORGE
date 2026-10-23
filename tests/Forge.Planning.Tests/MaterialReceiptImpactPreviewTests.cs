using Forge.Execution.Contracts;
using Forge.Planning.Engines;
using static Forge.Planning.Tests.TestFactory;

namespace Forge.Planning.Tests;

public sealed class MaterialReceiptImpactPreviewTests
{
    [Fact]
    public void Preview_pegs_receipt_to_the_task_order_and_compares_finite_capacity_schedules()
    {
        var factory = Slitting();
        factory.AddWorker("Mika", "SLIT-WC", SlitII);
        factory.AddWorker("Noah", "SLIT-WC", SlitII, FirstAid);
        var target = Order(10001, priority: 3);
        var competitor = Order(10002, priority: 1);
        var orders = new[] { target, competitor };
        var materials = new MaterialSnapshot(new DateOnly(2026, 10, 1), [], [], [],
        [
            new(target.OrderNumber, target.Priority, target.DueDate, target.Materials),
            new(competitor.OrderNumber, competitor.Priority, competitor.DueDate, competitor.Materials),
        ]);

        var preview = MaterialReceiptImpactCalculator.Evaluate(factory.Build(), materials, orders,
            new Dictionary<long, Forge.Quality.Contracts.OrderQuality>(), new Dictionary<string, int>(),
            taskId: 55, orderNumber: target.OrderNumber, operationSequence: 10, partNumber: "COIL", quantity: 10m, destination: "SLIT-POU");

        Assert.Equal("Shortfall", preview.BaselineMaterialState);
        Assert.Equal("Expected", preview.ProposedMaterialState);
        Assert.Equal(DeliveryStatus.Unschedulable.ToString(), preview.TargetOrder.BeforeStatus);
        Assert.NotEqual(DeliveryStatus.Unschedulable.ToString(), preview.TargetOrder.ProposedStatus);
        Assert.Empty(preview.OtherOrderImpacts);
        Assert.Contains("pegged to this order", preview.Assumption);
    }


    [Fact]
    public void Receipt_preview_reports_collateral_shift_to_another_visible_order()
    {
        var factory = Slitting();
        factory.AddWorker("Mika", "SLIT-WC", SlitII);
        factory.AddWorker("Noah", "SLIT-WC", SlitII, FirstAid);
        var target = Order(10001, priority: 1);
        var competitor = Order(10002, priority: 2);
        var orders = new[] { target, competitor };
        var materials = new MaterialSnapshot(new DateOnly(2026, 10, 1), [],
            [new MaterialIssued(competitor.OrderNumber, "COIL", 10m)], [],
            [new(target.OrderNumber, target.Priority, target.DueDate, target.Materials),
                new(competitor.OrderNumber, competitor.Priority, competitor.DueDate, competitor.Materials)]);

        var preview = MaterialReceiptImpactCalculator.Evaluate(factory.Build(), materials, orders,
            new Dictionary<long, Forge.Quality.Contracts.OrderQuality>(), new Dictionary<string, int>(),
            taskId: 56, orderNumber: target.OrderNumber, operationSequence: 10, partNumber: "COIL", quantity: 10m, destination: "SLIT-POU");

        var impact = Assert.Single(preview.OtherOrderImpacts);
        Assert.Equal(competitor.OrderNumber, impact.OrderNumber);
        Assert.True(impact.CompletionShiftHours > 0);
    }
    private static OrderInfo Order(long number, int priority) => new(
        number, "ROLL-100", "A", 10, new DateOnly(2026, 10, 9), priority, OrderStatus.Released, null, null, Now,
        [new OrderOperationInfo(10, "Slit", "SLIT-WC", RunSlitter, ["SLITTER-A"], [], 60, 1, OperationStatus.Pending)],
        [new OrderMaterialInfo(1, "COIL", "A", 10, "KG")]);
}
