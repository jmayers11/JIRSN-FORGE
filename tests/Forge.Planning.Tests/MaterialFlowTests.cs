using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;

namespace Forge.Planning.Tests;

/// <summary>V3.4: material flow states are separate and partial; in the warehouse is not a shortage; counts due follow programs.</summary>
public class MaterialFlowTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly LocationInfo[] Locations =
    [
        new("WH1", "PLANT1", "Warehouse", LocationKind.Stores, true),
        new("WH1-B1", "PLANT1", "Bin 1", LocationKind.Stores, true, "WH1", LocationLevel.Bin),
        new("SL-SIDE", "PLANT1", "Beside slitter", LocationKind.MachineSide, true, null, LocationLevel.Area, true, "SLIT-WC", "SLITTER-A"),
        new("PAINT-STAGE", "PLANT1", "Paint staging", LocationKind.Staging, true, null, LocationLevel.Area, true, "PAINT-WC"),
        new("QUAR", "PLANT1", "Quarantine", LocationKind.Quarantine, true, null, LocationLevel.Area, false),
    ];

    private static OrderInfo Order(params (string Part, decimal Qty)[] materials) => new(
        7, "PANEL", "A", 10, new DateOnly(2026, 10, 9), 3, OrderStatus.Released, null, null, At,
        [new OrderOperationInfo(10, "Slit", "SLIT-WC", "RUN", ["SLITTER-A"], [], 60, 1, OperationStatus.Pending), new OrderOperationInfo(20, "Paint", "PAINT-WC", "PAINT", [], [], 60, 1, OperationStatus.Pending)],
        materials.Select((m, i) => new OrderMaterialInfo(i + 1, m.Part, "A", m.Qty, "M")).ToList());

    private static MaterialTask Job(long id, string part, decimal qty, MaterialTaskStatus status, decimal? picked = null) =>
        new(id, MaterialTaskKind.Stage, 7, null, part, null, qty, status == MaterialTaskStatus.Requested ? null : "WH1-B1", "SL-SIDE", status, picked, null, "person:1", At);

    [Fact]
    public void Each_state_is_separate_and_partial_and_in_the_warehouse_is_not_a_shortage()
    {
        var positions = StockPosition.From(
            [
                new StockBalance("COIL", "WH1-B1", null, StockStatus.Released, 500),
                new StockBalance("COIL", "SL-SIDE", null, StockStatus.Released, 40),
                new StockBalance("TAPE", "SL-SIDE", null, StockStatus.Released, 10),
                new StockBalance("PAINT", "PAINT-STAGE", null, StockStatus.Released, 99), // another work center's point of use
                new StockBalance("PAINT", "QUAR", null, StockStatus.Released, 99),
                new StockBalance("PAINT", "WH1-B1", null, StockStatus.Hold, 99),
            ], Locations);
        var tasks = new[]
        {
            Job(1, "COIL", 60, MaterialTaskStatus.Delivered, 40),
            Job(2, "COIL", 30, MaterialTaskStatus.Picked, 25),
            Job(3, "COIL", 20, MaterialTaskStatus.Allocated),
            Job(4, "COIL", 10, MaterialTaskStatus.Requested),
            Job(5, "COIL", 10, MaterialTaskStatus.Cancelled),
            Job(6, "COIL", 500, MaterialTaskStatus.ApprovalNeeded) with { IsNonBom = true },
            Job(7, "COIL", 500, MaterialTaskStatus.Rejected) with { IsNonBom = true },
        };
        var ledger = new[]
        {
            new InventoryTransaction(1, InventoryTransactionKind.Issue, "COIL", 30, "SL-SIDE", StockStatus.Released, null, null, null, 7, null, null, null, At, "person:1", null, null),
        };

        var lines = MaterialFlowLine.For(Order(("COIL", 150), ("TAPE", 8), ("PAINT", 5)), tasks, ledger, [], positions).ToDictionary(l => l.PartNumber);
        var coil = lines["COIL"];
        Assert.Equal((150m, 120m, 110m, 65m, 40m, 30m), (coil.Required, coil.Requested, coil.Allocated, coil.Picked, coil.Delivered, coil.Issued));
        Assert.Equal(("SLIT-WC", 40m, 500m, MaterialFlowState.InWarehouse), (coil.WorkCenterCode, coil.AtPointOfUse, coil.Elsewhere, coil.State));
        Assert.Contains("not a shortage", coil.Explanation, StringComparison.Ordinal);
        Assert.Equal(MaterialFlowState.AtPointOfUse, lines["TAPE"].State);
        Assert.Equal((0m, 0m, MaterialFlowState.Shortage), (lines["PAINT"].AtPointOfUse, lines["PAINT"].Elsewhere, lines["PAINT"].State)); // held, quarantined or at another machine
        Assert.Equal(20m, new MaterialTask(9, MaterialTaskKind.Stage, 7, null, "COIL", null, 25, "WH1", "SL-SIDE", MaterialTaskStatus.Picked, 20, null, "p", At).Reserved);
    }

    [Fact]
    public void Counts_are_due_by_program_within_a_location_tree_and_never_counted_balances_are_due_today()
    {
        var today = new DateOnly(2026, 10, 10);
        var programs = new[]
        {
            new CycleCountProgram("WH", "Warehouse monthly", "WH1", null, 30, true, "p", At),
            new CycleCountProgram("COIL", "Coil weekly", null, "COIL", 7, true, "p", At),
            new CycleCountProgram("OLD", "Retired", "SL-SIDE", null, 1, false, "p", At),
        };
        var balances = new[]
        {
            new StockBalance("COIL", "WH1-B1", null, StockStatus.Released, 500),
            new StockBalance("TAPE", "WH1-B1", null, StockStatus.Released, 5),
            new StockBalance("BOLT", "WH1-B1", null, StockStatus.Released, 50),
            new StockBalance("TAPE", "SL-SIDE", null, StockStatus.Released, 10),
        };
        var last = new[]
        {
            new LastCount("COIL", "WH1-B1", null, new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero)),
            new LastCount("TAPE", "WH1-B1", null, new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero)),
        };

        var due = CountDue.For(programs, balances, Locations, last, today, at => DateOnly.FromDateTime(at.UtcDateTime));
        Assert.Equal("BOLT/WH,COIL/COIL", string.Join(",", due.Select(d => $"{d.PartNumber}/{d.ProgramCode}").Order(StringComparer.Ordinal)));
        Assert.Equal(new DateOnly(2026, 10, 8), due.Single(d => d.PartNumber == "COIL").DueOn); // weekly wins over monthly
        Assert.Null(due.Single(d => d.PartNumber == "BOLT").LastCountedAt);
    }
}
