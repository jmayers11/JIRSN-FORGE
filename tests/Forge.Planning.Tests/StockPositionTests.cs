using Forge.Inventory.Contracts;

namespace Forge.Planning.Tests;

/// <summary>V3.1: on hand, released, available for planning, at point of use and custody are separate dimensions.</summary>
public class StockPositionTests
{
    private static readonly LocationInfo[] Locations =
    [
        new("AREA-1", "PLANT1", "Main stores", LocationKind.Stores, true),
        new("RACK-4", "PLANT1", "Rack 4", LocationKind.Stores, true, "AREA-1", LocationLevel.Rack),
        new("BIN-12", "PLANT1", "Bin 12", LocationKind.Stores, true, "RACK-4", LocationLevel.Bin),
        new("SL-B-SIDE", "PLANT1", "Beside slitter B", LocationKind.MachineSide, true, null, LocationLevel.Area, true, "SLIT-WC", "SLITTER-B"),
        new("COATCO", "PLANT1", "Coating Co.", LocationKind.ExternalProcessor, true, null, LocationLevel.Area, false, null, null, "COATCO"),
        new("QUAR", "PLANT1", "Quarantine", LocationKind.Quarantine, true, null, LocationLevel.Area, false),
    ];

    [Fact]
    public void A_path_lists_the_locations_a_location_sits_in_widest_first()
    {
        var byCode = Locations.ToDictionary(l => l.Code, StringComparer.Ordinal);
        Assert.Equal("AREA-1 / RACK-4 / BIN-12", StockPosition.Path("BIN-12", byCode));
        Assert.Equal("UNKNOWN", StockPosition.Path("UNKNOWN", byCode));
    }

    [Fact]
    public void Released_stock_counts_for_planning_only_in_a_nettable_location_and_custody_follows_the_location()
    {
        var positions = StockPosition.From(
            [
                new StockBalance("COIL", "BIN-12", "L-1", StockStatus.Released, 10),
                new StockBalance("COIL", "BIN-12", "L-2", StockStatus.Hold, 3),
                new StockBalance("COIL", "SL-B-SIDE", "L-1", StockStatus.Released, 4),
                new StockBalance("COIL", "COATCO", "L-3", StockStatus.Released, 6),
                new StockBalance("COIL", "QUAR", "L-4", StockStatus.Released, 2),
            ], Locations).ToDictionary(p => (p.LocationCode, p.LotNumber));

        Assert.Equal(25m, positions.Values.Sum(p => p.Quantity)); // on hand
        Assert.Equal(22m, positions.Values.Where(p => p.QualityStatus == StockStatus.Released).Sum(p => p.Quantity)); // released
        Assert.Equal(14m, positions.Values.Where(p => p.AvailableForPlanning).Sum(p => p.Quantity)); // available for planning
        Assert.Equal(4m, positions.Values.Where(p => p.PointOfUseWorkCenter is not null).Sum(p => p.Quantity)); // at point of use

        var away = positions[("COATCO", "L-3")];
        Assert.Equal((Custody.Supplier, "COATCO", false), (away.Custody, away.CustodianSupplier, away.AvailableForPlanning));
        Assert.Equal(("SLIT-WC", "SLITTER-B"), (positions[("SL-B-SIDE", "L-1")].PointOfUseWorkCenter, positions[("SL-B-SIDE", "L-1")].MachineCode));
        Assert.False(positions[("BIN-12", "L-2")].AvailableForPlanning); // held stock is on hand, not available
        Assert.Equal("AREA-1 / RACK-4 / BIN-12", positions[("BIN-12", "L-1")].LocationPath);
    }

    [Fact]
    public void Wip_storage_with_a_work_center_is_not_the_point_of_use()
    {
        var wip = new LocationInfo("WIP-1", "PLANT1", "WIP racks", LocationKind.WipStorage, true, null, LocationLevel.Area, true, "SLIT-WC");
        Assert.False(wip.PointOfUse);
        Assert.Equal(Custody.InTransit, (wip with { Kind = LocationKind.InTransit, WorkCenterCode = null, Nettable = false }).Custody);
    }
}
