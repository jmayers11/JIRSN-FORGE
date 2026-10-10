using Forge.Inventory.Contracts;
using Forge.Planning.Engines;
using Forge.Procurement.Contracts;
using Forge.Projects.Contracts;

namespace Forge.Planning.Tests;

/// <summary>V2.10: a project's material lines against issues, shared stock, purchases and requisitions; what is left is short.</summary>
public sealed class ProjectMaterialTests
{
    private static readonly DateOnly Day = new(2026, 11, 2);
    private static readonly DateTimeOffset At = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
    private const string Code = "FAC-2027-014";

    private static readonly ProjectInfo Project = new(Code, "Gas line for slitter 3", ProjectType.Facility, ProjectFunding.Capital, null, null, 120000m, Day, Day.AddDays(60), "CAPEX-2027-014",
        ProjectStatus.Approved, "person:a", At, "person:b", At, null, null, null);

    private static ProjectLine Line(int n, string part, decimal quantity, int day = 0, ProjectLineStatus status = ProjectLineStatus.Active) =>
        new(Code, n, part, quantity, Day.AddDays(day), null, status);

    private static RequisitionInfo Requisition(long id, int line, decimal quantity, RequisitionStatus status = RequisitionStatus.Open) =>
        new(id, "VALVE", quantity, Day, PurchaseSourceKind.FacilityProject, ProjectSource.Key(Code, line), "x", null, null, null, null, null, status, "person:p", At,
            status == RequisitionStatus.Ordered ? 9 : null, status == RequisitionStatus.Ordered ? 1 : null, null, null, null, null, null);

    private static PurchaseOrderInfo Po(long number, PurchaseOrderStatus status, params PurchaseLineInfo[] lines) => new(number, "Acme", status, "person:bob", At, null, null, lines);

    private static PurchaseLineInfo PoLine(int n, string part, decimal quantity, int projectLine, PurchaseLineStatus status = PurchaseLineStatus.Open) =>
        new(n, part, quantity, "EA", Day, status, null, PurchaseSourceKind.FacilityProject, ProjectSource.Key(Code, projectLine), "x");

    private static ProjectMaterialCheck Check(ProjectLine[] lines, ProjectMaterial[]? moved = null, Dictionary<string, decimal>? stock = null, RequisitionInfo[]? requisitions = null, PurchaseOrderInfo[]? orders = null) =>
        ProjectMaterialEngine.Check(new ProjectMaterialRequest(Project, lines, moved ?? [], stock ?? new Dictionary<string, decimal>(), [], requisitions ?? [], orders ?? []));

    [Fact]
    public void Scenario_6_stock_is_shared_in_need_order_and_the_rest_is_short()
    {
        var check = Check([Line(1, "PIPE", 40, 5), Line(2, "VALVE", 6), Line(3, "REG", 2), Line(4, "PIPE", 30, 1)], stock: new() { ["PIPE"] = 50, ["VALVE"] = 6 });
        var byLine = check.Lines.ToDictionary(l => l.LineNumber);
        Assert.Equal(("In stock", 30m, 0m), (byLine[4].Status, byLine[4].FromStock, byLine[4].Shortage)); // needed first, so served first
        Assert.Equal(("Short", 20m, 20m), (byLine[1].Status, byLine[1].FromStock, byLine[1].Shortage));
        Assert.Equal(("In stock", 6m), (byLine[2].Status, byLine[2].FromStock));
        Assert.Equal(("Short", 2m), (byLine[3].Status, byLine[3].Shortage));
        Assert.Equal(2, check.ShortLines);
        Assert.Contains("line 1 20 PIPE", check.Summary, StringComparison.Ordinal);
        Assert.Equal("project:FAC-2027-014/3", byLine[3].SourceKey);
    }

    [Fact]
    public void Issues_requisitions_and_purchases_bought_for_the_line_count_and_others_do_not()
    {
        var moved = new[] { new ProjectMaterial(Code, 1, "VALVE", 4, 3) };
        var orders = new[]
        {
            Po(9, PurchaseOrderStatus.Approved, PoLine(1, "VALVE", 4, 1), PoLine(2, "VALVE", 50, 2)),
            Po(10, PurchaseOrderStatus.Cancelled, PoLine(1, "VALVE", 99, 1)),
            Po(11, PurchaseOrderStatus.Draft, PoLine(1, "VALVE", 1, 1, PurchaseLineStatus.Cancelled)),
        };
        var check = Check([Line(1, "VALVE", 10)], moved, requisitions: [Requisition(7001, 1, 2), Requisition(7002, 1, 5, RequisitionStatus.Cancelled)], orders: orders);
        var line = Assert.Single(check.Lines);
        // 10 needed, 3 issued -> 7 still; PO 9 line 1 bought 4 of which 4 received -> 0 on order; 2 requested; 5 short.
        Assert.Equal((3m, 7m, 4m, 0m, 2m, 5m, "Short"), (line.Issued, line.StillNeeded, line.Received, line.OnOrder, line.Requested, line.Shortage, line.Status));
        Assert.Equal(["PO 9 line 1"], line.PurchaseLines);
        Assert.Equal([7001L], line.RequisitionIds); // a cancelled requisition no longer stands for the line
    }

    [Fact]
    public void Lines_fully_issued_covered_by_orders_or_cancelled_are_not_short()
    {
        var check = Check(
            [Line(1, "PIPE", 10), Line(2, "VALVE", 5), Line(3, "REG", 2), Line(4, "CAP", 3, status: ProjectLineStatus.Cancelled)],
            [new ProjectMaterial(Code, 1, "PIPE", 0, 10)],
            requisitions: [Requisition(7003, 3, 2)],
            orders: [Po(12, PurchaseOrderStatus.Approved, PoLine(1, "VALVE", 5, 2))]);
        Assert.Equal(["Issued", "On order", "Requested", "Cancelled"], check.Lines.Select(l => l.Status).ToArray());
        Assert.Equal(0, check.ShortLines);
        Assert.Contains("every one of 3", check.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("project:FAC-2027-014/3", true, "FAC-2027-014", 3)]
    [InlineData("project:CAP-9/12", true, "CAP-9", 12)]
    [InlineData("project:FAC/0", false, "", 0)]
    [InlineData("work-order:5", false, "", 0)]
    [InlineData("project:/3", false, "", 0)]
    public void Project_source_keys_round_trip(string key, bool ok, string code, int line)
    {
        Assert.Equal(ok, ProjectSource.TryParse(key, out var c, out var n));
        if (ok)
        {
            Assert.Equal((code, line), (c, n));
            Assert.Equal(key, ProjectSource.Key(c, n));
        }
    }
}
