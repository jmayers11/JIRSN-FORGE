using Forge.Inventory.Contracts;

namespace Forge.Planning.Tests;

/// <summary>V3.2: an instance's lineage follows split and merge links back to the first instances.</summary>
public class InstanceLineageTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static InstanceLink Link(long id, InstanceRelation relation, string from, string to, decimal qty) =>
        new(id, relation, "FOIL", from, "FOIL", to, qty, id * 10, (id * 10) + 1, "why", At, "person:1");

    private static readonly InstanceLink[] Links =
    [
        Link(1, InstanceRelation.Split, "R-1", "R-1A", 600),
        Link(2, InstanceRelation.Split, "R-1", "R-1B", 400),
        Link(3, InstanceRelation.Merge, "R-1B", "M-1", 400),
        Link(4, InstanceRelation.Merge, "R-2", "M-1", 100),
        Link(5, InstanceRelation.Split, "M-1", "M-1A", 250),
    ];

    [Fact]
    public void Sources_and_results_are_the_direct_links()
    {
        var lineage = InstanceLineage.From("FOIL", "M-1", Links);
        Assert.Equal("R-1B,R-2", string.Join(",", lineage.Sources.Select(l => l.SourceLot)));
        Assert.Equal("M-1A", Assert.Single(lineage.Results).ResultLot);
    }

    [Fact]
    public void Origins_are_the_first_instances_through_splits_and_merges()
    {
        Assert.Equal("R-1,R-2", string.Join(",", InstanceLineage.From("FOIL", "M-1A", Links).Origins));
        Assert.Empty(InstanceLineage.From("FOIL", "R-1", Links).Origins);
        Assert.Equal(2, InstanceLineage.From("FOIL", "R-1", Links).Results.Count);
    }

    [Fact]
    public void A_loop_in_the_links_does_not_hang()
    {
        var loop = new[] { Link(1, InstanceRelation.Merge, "A", "B", 1), Link(2, InstanceRelation.Merge, "B", "A", 1) };
        Assert.Empty(InstanceLineage.From("FOIL", "A", loop).Origins);
    }
}
