using Forge.Inventory.Contracts;

namespace Forge.Planning.Tests;

/// <summary>V3.6: age, expiry, open life, retest and storage are separate facts; selection follows the hard rules first.</summary>
public class ShelfLifeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 1);

    private static ShelfLifeRule Rule(int? openHours = null, decimal? max = null, SelectionRule selection = SelectionRule.Fefo) =>
        new("ADH", 180, ShelfLifeBasis.Manufacture, 90, openHours, null, max, null, selection, "x", "person:1", Now);

    private static LotInfo Lot(string lot, int expiresIn, DateTimeOffset? opened = null, string revision = "A") =>
        new("ADH", lot, null, Now.AddDays(-10), revision, IdentityKind.Lot, Today.AddDays(-100), Today.AddDays(expiresIn), Today.AddDays(60), opened);

    private static LotShelfLife Life(LotInfo lot, ShelfLifeRule? rule = null, ShelfLifeExtension[]? extensions = null, RetestInfo[]? retests = null, StorageExcursion[]? excursions = null) =>
        LotShelfLife.For(lot, rule ?? Rule(), extensions ?? [], retests ?? [], excursions ?? [], [("FRIDGE", Now.AddDays(-10), null)], Today, Now);

    [Fact]
    public void An_approved_extension_moves_the_expiry_and_keeps_the_original()
    {
        var expired = Life(Lot("L1", -5));
        Assert.Equal((ShelfLifeStatus.Expired, false, 100), (expired.Status, expired.Usable, expired.AgeDays));
        var extension = new ShelfLifeExtension(1, "ADH", "L1", Today.AddDays(-5), Today.AddDays(45), "QR-1", "person:1", Now, true, "person:2", Now);
        var extended = Life(Lot("L1", -5), extensions: [extension, extension with { ExtensionId = 2, NewExpires = Today.AddDays(400), Approved = false }]);
        Assert.Equal((ShelfLifeStatus.Ok, Today.AddDays(45), Today.AddDays(-5)), (extended.Status, extended.Expires, extended.OriginalExpires));
    }

    [Fact]
    public void Open_life_and_retests_are_their_own_limits()
    {
        Assert.Equal(ShelfLifeStatus.OpenLifeExpired, Life(Lot("L1", 50, Now.AddHours(-9)), Rule(openHours: 8)).Status);
        Assert.Equal(ShelfLifeStatus.Ok, Life(Lot("L1", 50, Now.AddHours(-7)), Rule(openHours: 8)).Status);
        var failed = new RetestInfo(1, "ADH", "L1", false, null, "out", "person:3", Now.AddDays(-1));
        Assert.Equal(ShelfLifeStatus.RetestFailed, Life(Lot("L1", 50), retests: [failed]).Status);
        var overdue = Lot("L1", 50) with { RetestDue = Today.AddDays(-1) };
        Assert.Equal(ShelfLifeStatus.RetestOverdue, Life(overdue).Status);
        var passed = new RetestInfo(2, "ADH", "L1", true, Today.AddDays(90), "ok", "person:3", Now.AddHours(-1));
        Assert.Equal((ShelfLifeStatus.Ok, Today.AddDays(90)), (Life(overdue, retests: [passed]).Status, Life(overdue, retests: [passed]).RetestDue));
    }

    [Fact]
    public void An_excursion_outside_the_limits_needs_a_retest_after_it()
    {
        var hot = new StorageExcursion(1, "FRIDGE", "Power cut", 15, Now.AddDays(-2), Now.AddDays(-1), null, "person:1", Now);
        var mild = hot with { ExcursionId = 2, MeasuredC = 7 };
        Assert.Equal(ShelfLifeStatus.Ok, Life(Lot("L1", 50), Rule(max: 8), excursions: [mild]).Status);
        Assert.Equal(ShelfLifeStatus.Excursion, Life(Lot("L1", 50), Rule(max: 8), excursions: [hot]).Status);
        Assert.Equal(ShelfLifeStatus.Ok, Life(Lot("L1", 50), Rule(max: 8), retests: [new RetestInfo(1, "ADH", "L1", true, null, "ok", "person:3", Now)], excursions: [hot]).Status);
        Assert.Equal(ShelfLifeStatus.Ok, Life(Lot("L1", 50), Rule(), excursions: [hot]).Status); // no storage limits, nothing broken
    }

    [Fact]
    public void Fefo_and_fifo_only_order_what_the_hard_rules_leave()
    {
        var lots = new[] { Lot("OLD", 300) with { FirstReceivedAt = Now.AddDays(-50) }, Lot("SOON", 10), Lot("GONE", -1), Lot("REV-B", 5, revision: "B") }.ToDictionary(l => l.LotNumber);
        var lives = lots.Values.Select(l => Life(l)).ToDictionary(l => l.LotNumber);
        StockPosition P(string lot, StockStatus status = StockStatus.Released) => new("ADH", lot, "FRIDGE", "FRIDGE", LocationKind.Stores, status, 5, Custody.Company, null, true, null, null);
        var positions = new[] { P("OLD"), P("SOON"), P("GONE"), P("REV-B") };

        var fefo = LotSelection.Rank(positions, lots, lives, SelectionRule.Fefo, "A");
        Assert.Equal("SOON,OLD", string.Join(",", fefo.Where(c => c.Rank > 0).Select(c => c.LotNumber)));
        Assert.Contains("revision B", fefo.Single(c => c.LotNumber == "REV-B").ExcludedBecause, StringComparison.Ordinal);
        Assert.Contains("Expired", fefo.Single(c => c.LotNumber == "GONE").ExcludedBecause, StringComparison.Ordinal);

        var fifo = LotSelection.Rank(positions, lots, lives, SelectionRule.Fifo, null);
        Assert.Equal("OLD,REV-B,SOON", string.Join(",", fifo.Where(c => c.Rank > 0).Select(c => c.LotNumber)));
        Assert.Contains("Hold", LotSelection.Rank([P("OLD", StockStatus.Hold)], lots, lives, SelectionRule.Fifo, null).Single().ExcludedBecause, StringComparison.Ordinal);
    }
}
