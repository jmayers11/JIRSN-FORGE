using Forge.Engineering.Contracts;
using Forge.Maintenance.Contracts;
using Forge.Planning.Engines;
using Forge.Procurement.Contracts;
using static Forge.Planning.Tests.TestFactory;

namespace Forge.Planning.Tests;

/// <summary>V2.8: MRO min/max replenishment in MRP, and obsolescence impact (scenario 7) stated from data only.</summary>
public sealed class V28EngineTests
{
    private static readonly DateOnly Today = new(2026, 10, 1);
    private static readonly DateTimeOffset At = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    private static MrpItem Spare(string part, decimal min, decimal reorder, decimal max, bool critical = false) =>
        new(part, part, "EA", MakeBuy.Buy, 5, 0, LotPolicy.FixedQuantity, 50, 0, new MrpMroLevels(min, reorder, max, critical));

    private static MrpResult Run(MrpItem item, decimal stock, params MrpRequirement[] requirements) =>
        new MrpEngine().Evaluate(new MrpSnapshot(Today, [item], [], [new MrpStock(item.PartNumber, stock, 0, 0)], requirements, []), new MrpRequest(), Now).Output;

    [Fact]
    public void At_or_below_the_reorder_point_mrp_buys_up_to_the_maximum_ignoring_the_lot_policy()
    {
        var result = Run(Spare("FILTER", 2, 4, 12), 4);
        var planned = Assert.Single(result.PlannedOrders);
        Assert.Equal((8m, Today.AddDays(5), Today, false), (planned.Quantity, planned.DueDate, planned.ReleaseDate, planned.PastDue));
        Assert.Contains("reorder point 4", planned.Explanation, StringComparison.Ordinal);
        Assert.Contains("maximum 12", planned.Explanation, StringComparison.Ordinal);
        var peg = Assert.Single(planned.Pegging);
        Assert.Equal(("mro:FILTER", "MroReplenishment", 8m), (peg.RequirementKey, peg.Kind, peg.Quantity));
        Assert.Equal((PurchaseSourceKind.MroReplenishment, "mro:FILTER"), Source(planned));
        Assert.DoesNotContain(result.Exceptions, e => e.Kind == "PastDueRelease");
    }

    [Fact]
    public void Above_the_reorder_point_nothing_is_bought_until_demand_takes_it_down()
    {
        Assert.Empty(Run(Spare("FILTER", 2, 4, 12), 5).PlannedOrders);
        var need = new MrpRequirement("D7", "FILTER", 3, Today.AddDays(20), 3, "Demand", "Demand 7 (maintenance)");
        var planned = Assert.Single(Run(Spare("FILTER", 2, 4, 12), 6, need).PlannedOrders);
        Assert.Equal((9m, Today.AddDays(20)), (planned.Quantity, planned.DueDate));
        Assert.Equal(PurchaseSourceKind.MroReplenishment, Source(planned).Kind);
    }

    [Fact]
    public void An_order_placed_at_the_reorder_point_arrives_after_its_lead_time_and_a_shortage_before_then_is_its_own_late_order()
    {
        var need = new MrpRequirement("D9", "FILTER", 10, Today.AddDays(2), 3, "Demand", "Demand 9");
        var result = Run(Spare("FILTER", 2, 4, 12), 3, need);
        Assert.Equal(2, result.PlannedOrders.Count);
        var replenish = result.PlannedOrders.Single(p => !p.PastDue);
        Assert.Equal((9m, Today.AddDays(5)), (replenish.Quantity, replenish.DueDate));
        Assert.Equal(PurchaseSourceKind.MroReplenishment, Source(replenish).Kind);

        // On day 2 the 10 needed find 3 on hand and 9 still on order: 10 more, late, bring it back to the maximum when the first arrives.
        var shortage = result.PlannedOrders.Single(p => p.PastDue);
        Assert.Equal((10m, Today.AddDays(2)), (shortage.Quantity, shortage.DueDate));
        Assert.Contains(result.Exceptions, e => e.Kind == "PastDueRelease");
        Assert.Equal(PurchaseSourceKind.Mixed, PurchaseTraceService.SourceOf(shortage, result.PlannedOrders, new Dictionary<long, DemandInfo>
        {
            [9] = new(9, "FILTER", 10, Today.AddDays(2), 3, DemandKind.Maintenance, null, null, null, DemandStatus.Open, "person:p", At, "work-order:3"),
        }).Kind);
    }

    [Fact]
    public void A_critical_spare_projected_below_its_minimum_is_reported()
    {
        var result = Run(Spare("BELT", 2, 3, 6, critical: true), 1);
        Assert.Contains(result.Exceptions, e => e.Kind == "CriticalSpareShort" && e.PartNumber == "BELT");
        Assert.Equal(5m, Assert.Single(result.PlannedOrders).Quantity);
        Assert.DoesNotContain(Run(Spare("BELT", 2, 3, 6, critical: false), 1).Exceptions, e => e.Kind == "CriticalSpareShort");
    }

    private static (PurchaseSourceKind Kind, string Key) Source(PlannedOrder p)
    {
        var s = PurchaseTraceService.SourceOf(p, [p], new Dictionary<long, DemandInfo>());
        return (s.Kind, s.Key);
    }

    private static ObsolescenceInfo Record(ReplacementStatus replacement = ReplacementStatus.None, string? part = null) =>
        new(9001, "SC-440", ObsolescenceStatus.Obsolete, null, null, new DateOnly(2028, 12, 31), "Manufacturer notice PCN-2291", "person:e", At,
            part, replacement, part is null ? null : "person:e", part is null ? null : At, replacement == ReplacementStatus.Approved ? "person:a" : null, null, null);

    private static ObsolescenceSnapshot Scenario7(ObsolescenceInfo record) => new(
        record,
        "EA",
        [
            new ObsolescenceInstall(1, "SL-02", "Slitter 2", AssetStatus.Active, "SLIT-WC", "SLITTER-B", "Cabinet slot 1", 1, null),
            new ObsolescenceInstall(2, "SL-02", "Slitter 2", AssetStatus.Active, "SLIT-WC", "SLITTER-B", "Cabinet slot 2", 1, 3002),
            new ObsolescenceInstall(3, "OLD-1", "Old line", AssetStatus.Decommissioned, "SLIT-WC", null, "Cabinet", 1, null),
        ],
        0,
        0,
        [],
        [],
        [],
        [],
        Today);

    [Fact]
    public void Scenario_7_obsolescence_action_is_required_with_counts_and_no_invented_replacement()
    {
        var impact = ObsolescenceImpactEngine.Evaluate(Scenario7(Record()));
        Assert.True(impact.ActionRequired);
        Assert.Equal((2m, 0m, 0m), (impact.InstalledQuantity, impact.SparesAvailable, impact.OnOrder));
        Assert.Equal(["SL-02"], impact.Assets);
        Assert.Equal(["SLIT-WC"], impact.WorkCenters);
        Assert.Empty(impact.ApprovedAlternates);
        Assert.Equal("None recorded", impact.Replacement);
        Assert.StartsWith("Obsolescence action required: SC-440 — installed 2, spares 0, on order 0, approved alternates none, replacement none recorded.", impact.Headline, StringComparison.Ordinal);
        Assert.Contains(impact.Findings, f => f.Contains("end of support 2028-12-31", StringComparison.Ordinal));
        Assert.Contains(impact.Findings, f => f.Contains("would not replace what is installed", StringComparison.Ordinal));
        Assert.DoesNotContain(impact.Findings, f => f.Contains("SC-450", StringComparison.Ordinal));
    }

    [Fact]
    public void An_approved_replacement_or_nothing_using_it_means_no_action()
    {
        Assert.False(ObsolescenceImpactEngine.Evaluate(Scenario7(Record(ReplacementStatus.Approved, "SC-450"))).ActionRequired);
        var proposed = ObsolescenceImpactEngine.Evaluate(Scenario7(Record(ReplacementStatus.Proposed, "SC-450")));
        Assert.True(proposed.ActionRequired);
        Assert.Contains("not yet approved", proposed.Replacement, StringComparison.Ordinal);
        var unused = ObsolescenceImpactEngine.Evaluate(Scenario7(Record()) with { Installed = [] });
        Assert.False(unused.ActionRequired);
        Assert.Contains("nothing recorded uses it", unused.Headline, StringComparison.Ordinal);
    }
}
