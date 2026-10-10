using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Quality.Contracts;

namespace Forge.Planning.Tests;

/// <summary>V3.5: an order's quantity states come from records that are never rewritten.</summary>
public class QuantityStatesTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static OrderOperationInfo Op(int sequence, decimal good, decimal scrap) =>
        new(sequence, "Op", "SLIT-WC", "RUN", [], [], 60, 1, OperationStatus.Complete, good, scrap);

    private static readonly OrderInfo Order = new(7, "ROLL", "A", 100, new DateOnly(2026, 10, 20), 3, OrderStatus.Completed, null, null, At, [Op(10, 96, 4), Op(20, 96, 0)], []);

    private static ReworkInfo Rework(ReworkKind kind, decimal quantity, decimal? good = null, decimal? scrap = null) =>
        new(1, kind, 7, 20, "ROLL", quantity, null, "RESLIT", "x", At, "person:1", good is null ? null : At, good is null ? null : "person:1", good, scrap);

    private static InventoryTransaction Txn(InventoryTransactionKind kind, decimal quantity, string lot, long? order) =>
        new(1, kind, "ROLL", quantity, null, null, "FG", StockStatus.Released, lot, order, null, null, null, At, "person:1", null, null);

    private static OrderQuantityStates States(IReadOnlyList<ReworkInfo> rework, IReadOnlyList<InspectionInfo>? inspections = null, IReadOnlyList<NcrInfo>? ncrs = null)
    {
        var receipt = Txn(InventoryTransactionKind.ProductionReceipt, 50, "WO7", 7);
        var shipped = Txn(InventoryTransactionKind.Shipment, 20, "WO7", null);
        var positions = new[]
        {
            new StockPosition("ROLL", "WO7", "FG", "FG", LocationKind.Stores, StockStatus.Released, 25, Custody.Company, null, true, null, null),
            new StockPosition("ROLL", "WO7", "DOCK", "DOCK", LocationKind.Shipping, StockStatus.Released, 5, Custody.Company, null, true, null, null),
            new StockPosition("ROLL", "OTHER", "FG", "FG", LocationKind.Stores, StockStatus.Hold, 99, Custody.Company, null, true, null, null),
        };
        return OrderQuantityStates.For(Order, rework, inspections ?? [], ncrs ?? [], [receipt], [receipt, shipped], positions);
    }

    [Fact]
    public void Open_rework_and_repair_take_output_back_and_closing_returns_the_good()
    {
        var open = States([Rework(ReworkKind.Rework, 10), Rework(ReworkKind.Repair, 4)]);
        Assert.Equal((100m, 82m, 10m, 4m, 4m, 0m), (open.Of("Started"), open.Of("Produced"), open.Of("Rework"), open.Of("Repair"), open.Of("Scrapped"), open.Of("In process")));

        var closed = States([Rework(ReworkKind.Rework, 10, 8, 2)]);
        Assert.Equal((94m, 0m, 6m), (closed.Of("Produced"), closed.Of("Rework"), closed.Of("Scrapped")));
    }

    [Fact]
    public void Stock_states_follow_the_lots_the_order_produced()
    {
        var q = States([]);
        Assert.Equal((50m, 25m, 5m, 20m, 0m, 46m), (q.Of("Received"), q.Of("Released"), q.Of("Staged"), q.Of("Shipped"), q.Of("Hold"), q.Of("Produced, not received")));
        Assert.Equal(0m, q.Of("Packed"));
    }

    [Fact]
    public void Accepted_is_what_passed_the_last_inspection_point_plus_what_was_accepted_as_it_is()
    {
        InspectionInfo Inspection(int sequence, decimal quantity, InspectionResult result) => new(1, 7, sequence, "ROLL", quantity, result, null, null, "person:2", At, [], null);
        var ncr = new NcrInfo(1, "ROLL", 7, 20, null, 3, "Scratch", "x", NcrStatus.Dispositioned, Disposition.UseAsIs, "ok", "person:2", At, "person:2", "person:3", null, null, null);
        var q = States([], [Inspection(10, 96, InspectionResult.Pass), Inspection(20, 80, InspectionResult.Pass), Inspection(20, 5, InspectionResult.Fail)], [ncr]);
        Assert.Equal(83m, q.Of("Accepted"));
    }
}
