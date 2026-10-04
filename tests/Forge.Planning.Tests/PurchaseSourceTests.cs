using Forge.Engineering.Contracts;
using Forge.Planning.Engines;
using Forge.Procurement.Contracts;

namespace Forge.Planning.Tests;

/// <summary>V2.7: a planned purchase's source is found by walking its pegging back to the root requirements.</summary>
public sealed class PurchaseSourceTests
{
    private static readonly DateOnly Day = new(2026, 10, 20);
    private static readonly DateTimeOffset At = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    private static MrpPeg Peg(string key, string kind, string text = "need") => new(key, kind, text, Day, 1);

    private static PlannedOrder Planned(string key, string part, MakeBuy makeBuy, params MrpPeg[] pegs) =>
        new(key, part, makeBuy, 1, "EA", Day, Day, false, 1, null, "plan", pegs);

    private static DemandInfo Demand(long id, DemandKind kind, string? source) =>
        new(id, "X", 1, Day, 5, kind, null, null, null, DemandStatus.Open, "person:p", At, source);

    [Theory]
    [InlineData(DemandKind.Maintenance, "work-order:57", PurchaseSourceKind.Maintenance)]
    [InlineData(DemandKind.MroReplenishment, "mro:BRG-6204", PurchaseSourceKind.MroReplenishment)]
    [InlineData(DemandKind.FacilityProject, "project:FAC-2027-014/3", PurchaseSourceKind.FacilityProject)]
    [InlineData(DemandKind.CapitalProject, "project:CAP-9", PurchaseSourceKind.CapitalProject)]
    [InlineData(DemandKind.Quality, "ncr:12", PurchaseSourceKind.Quality)]
    [InlineData(DemandKind.Tooling, "tool:DIE-4", PurchaseSourceKind.Tooling)]
    [InlineData(DemandKind.Engineering, "ecn:ECN-0002", PurchaseSourceKind.Engineering)]
    [InlineData(DemandKind.CustomerOrder, null, PurchaseSourceKind.CustomerOrder)]
    public void A_purchase_for_one_demand_carries_that_demands_kind_and_source(DemandKind kind, string? sourceKey, PurchaseSourceKind expected)
    {
        var buy = Planned("PLN-X-001", "X", MakeBuy.Buy, Peg("D4", "Demand", "Demand 4"));
        var source = PurchaseTraceService.SourceOf(buy, [buy], new Dictionary<long, DemandInfo> { [4] = Demand(4, kind, sourceKey) });
        Assert.Equal((expected, sourceKey ?? "demand:4", (long?)4), (source.Kind, source.Key, source.DemandId));
        Assert.Equal("Demand 4", source.Label);
    }

    [Fact]
    public void A_component_purchase_is_traced_through_its_parent_planned_order_to_the_demand()
    {
        var make = Planned("PLN-ASSY-001", "ASSY", MakeBuy.Make, Peg("D9", "Demand", "Demand 9 (tooling)"));
        var buy = Planned("PLN-PIN-001", "PIN", MakeBuy.Buy, Peg("PLN-ASSY-001/2", "PlannedOrder"));
        var source = PurchaseTraceService.SourceOf(buy, [make, buy], new Dictionary<long, DemandInfo> { [9] = Demand(9, DemandKind.Tooling, "tool:DIE-7") });
        Assert.Equal((PurchaseSourceKind.Tooling, "tool:DIE-7"), (source.Kind, source.Key));
        Assert.Equal(["Demand 9 (tooling)"], source.Roots);
    }

    [Fact]
    public void Order_material_substitution_and_stock_have_their_own_sources()
    {
        var order = Planned("PLN-C-001", "C", MakeBuy.Buy, Peg("WO10012/3", "OrderMaterial"));
        Assert.Equal((PurchaseSourceKind.Production, "order:10012"), Pick(order));
        var sub = Planned("PLN-C-002", "C", MakeBuy.Buy, Peg("SUB-C-1", "Substitution"));
        Assert.Equal((PurchaseSourceKind.Production, "substitution:SUB-C-1"), Pick(sub));
        var stock = Planned("PLN-C-003", "C", MakeBuy.Buy, Peg("SS-C", "SafetyStock"));
        Assert.Equal((PurchaseSourceKind.SafetyStock, "stock:C"), Pick(stock));
    }

    [Fact]
    public void Roots_of_different_sources_are_mixed_and_all_listed()
    {
        var buy = Planned("PLN-B-001", "B", MakeBuy.Buy, Peg("D1", "Demand", "Demand 1"), Peg("WO7/1", "OrderMaterial", "Order 7"));
        var source = PurchaseTraceService.SourceOf(buy, [buy], new Dictionary<long, DemandInfo> { [1] = Demand(1, DemandKind.Maintenance, "work-order:3") });
        Assert.Equal((PurchaseSourceKind.Mixed, "mrp:PLN-B-001@2026-10-20", (long?)null), (source.Kind, source.Key, source.DemandId));
        Assert.Equal(["Demand 1", "Order 7"], source.Roots);

        var two = Planned("PLN-B-002", "B", MakeBuy.Buy, Peg("WO7/1", "OrderMaterial"), Peg("WO8/1", "OrderMaterial"));
        Assert.Equal(PurchaseSourceKind.Production, PurchaseTraceService.SourceOf(two, [two], new Dictionary<long, DemandInfo>()).Kind);
    }

    private static (PurchaseSourceKind, string) Pick(PlannedOrder p)
    {
        var s = PurchaseTraceService.SourceOf(p, [p], new Dictionary<long, DemandInfo>());
        return (s.Kind, s.Key);
    }
}
