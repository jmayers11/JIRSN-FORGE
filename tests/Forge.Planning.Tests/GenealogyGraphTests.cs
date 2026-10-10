using Forge.Inventory.Contracts;
using Forge.Maintenance.Contracts;

namespace Forge.Planning.Tests;

/// <summary>V3.3: material reconciliation and the genealogy graph, from records alone.</summary>
public class GenealogyGraphTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static long next = 100;

    private static InventoryTransaction Txn(InventoryTransactionKind kind, string part, string? lot, decimal qty, long? order = null, long? shipment = null, long? po = null) =>
        new(Interlocked.Increment(ref next), kind, part, qty, null, null, "WH1", StockStatus.Released, lot, order, po, po is null ? null : 1, "why", At, "person:1", null, null, shipment);

    private static TransformationInfo Record(long id, long order, params TransformationLine[] lines) => new(id, order, null, "why", At, "person:1", lines);

    [Fact]
    public void Issued_equals_consumed_returned_scrap_loss_remnant_and_remaining_within_tolerance()
    {
        var ledger = new[]
        {
            Txn(InventoryTransactionKind.Issue, "COIL", "C-1", 1000, 7),
            Txn(InventoryTransactionKind.Return, "COIL", "C-1", 100, 7),
            Txn(InventoryTransactionKind.Issue, "TAPE", null, 10, 7),
            Txn(InventoryTransactionKind.Issue, "COIL", "C-9", 5, 8), // another order
        };
        var records = new[]
        {
            Record(1, 7, new TransformationLine(1, TransformationRole.Consumed, "COIL", "C-1", 700), new TransformationLine(2, TransformationRole.Remaining, "COIL", "C-1", 150)),
            Record(2, 7, new TransformationLine(1, TransformationRole.Scrap, "COIL", "C-1", 20), new TransformationLine(2, TransformationRole.Remnant, "COIL", "C-1R", 120, "COIL", "C-1", "WH1", 55),
                new TransformationLine(3, TransformationRole.Remaining, "COIL", "C-1", 0), new TransformationLine(4, TransformationRole.Consumed, "TAPE", null, 9.9m)),
        };

        var rows = MaterialReconciliation.For(7, ledger, records, part => part == "TAPE" ? 2 : 0).ToDictionary(r => r.PartNumber);
        var coil = rows["COIL"];
        Assert.Equal((900m, 700m, 20m, 120m, 0m), (coil.NetIssued, coil.Consumed, coil.Scrap, coil.Remnant, coil.Remaining)); // the latest remaining replaces the earlier one
        Assert.Equal((60m, ReconciliationStatus.Unexplained), (coil.Unexplained, coil.Status));
        Assert.Equal((0.1m, ReconciliationStatus.WithinTolerance), (rows["TAPE"].Unexplained, rows["TAPE"].Status));
        Assert.Equal(2, rows.Count);

        var none = Assert.Single(MaterialReconciliation.For(8, ledger, records, _ => 0));
        Assert.Equal(ReconciliationStatus.NotRecorded, none.Status);
    }

    [Fact]
    public void A_supplier_lot_traces_through_remnant_and_second_slitting_to_a_shipped_serial_and_back()
    {
        var lots = new LotInfo[]
        {
            new("COIL", "MASTER", "MILL-7", At, "A", IdentityKind.Roll),
            new("COIL", "REM-1", "MILL-7", At, "A", IdentityKind.Roll),
            new("STRIP", "WO11", null, At, "A"),
            new("STRIP", "WO12", null, At, "A"),
            new("UNIT", "SN-1", null, At, "A", IdentityKind.Serial),
        };
        var ledger = new[]
        {
            Txn(InventoryTransactionKind.Receipt, "COIL", "MASTER", 1000, po: 5),
            Txn(InventoryTransactionKind.Transfer, "COIL", "MASTER", 1000), // moves are never genealogy
            Txn(InventoryTransactionKind.Issue, "COIL", "MASTER", 1000, 11),
            Txn(InventoryTransactionKind.ProductionReceipt, "STRIP", "WO11", 800, 11),
            Txn(InventoryTransactionKind.Issue, "COIL", "REM-1", 150, 12),
            Txn(InventoryTransactionKind.ProductionReceipt, "STRIP", "WO12", 140, 12),
            Txn(InventoryTransactionKind.Issue, "STRIP", "WO12", 140, 13),
            Txn(InventoryTransactionKind.ProductionReceipt, "UNIT", "SN-1", 1, 13),
            Txn(InventoryTransactionKind.Shipment, "UNIT", "SN-1", 1, shipment: 3),
            Txn(InventoryTransactionKind.Issue, "COIL", "HIDDEN-LOT", 5, 99),
        };
        var records = new[]
        {
            Record(1, 11, new TransformationLine(1, TransformationRole.Consumed, "COIL", "MASTER", 830), new TransformationLine(2, TransformationRole.Remnant, "COIL", "REM-1", 150, "COIL", "MASTER", "WH1", 1),
                new TransformationLine(3, TransformationRole.Loss, "COIL", "MASTER", 20)),
            Record(2, 12, new TransformationLine(1, TransformationRole.Consumed, "COIL", "REM-1", 150)),
        };
        var graph = GenealogyGraph.Build(new GenealogySources(lots, [], ledger, records, Array.Empty<AssetComponentInfo>(), new Dictionary<long, string> { [5] = "Mill Co." }, new HashSet<long> { 99 }));

        Assert.DoesNotContain(graph.Edges, e => e.Relation == GenealogyRelation.Issued && e.To == GenealogyGraph.OrderId(99));
        Assert.Equal(1, graph.HiddenOrderCount);

        var forward = graph.Trace(GenealogyGraph.InstanceId("COIL", "MASTER"), TraceDirection.Forward);
        var reached = forward.Nodes.Select(n => n.Id).ToHashSet();
        Assert.Contains(GenealogyGraph.InstanceId("COIL", "REM-1"), reached);
        Assert.Contains(GenealogyGraph.InstanceId("STRIP", "WO12"), reached);
        Assert.Contains(GenealogyGraph.ShipmentId(3), reached);
        Assert.Contains(forward.Edges, e => e.Relation == GenealogyRelation.Remnant && e.To == GenealogyGraph.InstanceId("COIL", "REM-1"));
        Assert.DoesNotContain(forward.Edges, e => e.Relation == GenealogyRelation.Received);

        var back = graph.Trace(GenealogyGraph.ShipmentId(3), TraceDirection.Back);
        var receipt = Assert.Single(back.Nodes, n => n.Kind == GenealogyNodeKind.Receipt);
        Assert.Contains("Mill Co.", receipt.Label, StringComparison.Ordinal);
        Assert.Contains("supplier lot MILL-7", receipt.Label, StringComparison.Ordinal);
        Assert.Contains(back.Nodes, n => n.Id == GenealogyGraph.InstanceId("COIL", "MASTER"));
        Assert.DoesNotContain(back.Nodes, n => n.Id == GenealogyGraph.InstanceId("STRIP", "WO11")); // a sibling, not an ancestor
        Assert.Contains(back.Notes, n => n.Contains("cannot see", StringComparison.Ordinal));

        Assert.Equal(13, Enum.GetValues<GenealogyRelation>().Length);
    }
}
