using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Resources.Contracts;

namespace Forge.Inventory.Contracts;

/// <summary>Shelf-life states and the stock positions that follow them (V3.6), read once per snapshot.</summary>
public static class ShelfLifeQueries
{
    /// <summary>Every lot's shelf-life state (of one part, or all parts).</summary>
    public static async Task<IReadOnlyList<LotShelfLife>> StatesAsync(IKernelUnitOfWork uow, string? partNumber, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var lookup = uow.Store<IShelfLifeLookup>();
        var lots = await uow.Store<IInventoryLookup>().ListLotsAsync(partNumber, ct);
        if (lots.Count == 0)
        {
            return [];
        }

        var rules = (await lookup.ListRulesAsync(ct)).ToDictionary(r => r.PartNumber, StringComparer.Ordinal);
        var extensions = (await lookup.ListExtensionsAsync(partNumber, ct)).ToLookup(e => (e.PartNumber, e.LotNumber));
        var retests = (await lookup.ListRetestsAsync(partNumber, ct)).ToLookup(r => (r.PartNumber, r.LotNumber));
        var excursions = await lookup.ListExcursionsAsync(ct);
        var residence = (await lookup.ListLotResidenceAsync(partNumber, ct)).ToLookup(r => (r.PartNumber, r.LotNumber), r => (r.Location, r.From, r.Until));
        var zone = await uow.SiteTimeZoneAsync(ct);
        var today = SiteTime.DateAt(now, zone);
        return lots.Select(l => LotShelfLife.For(l, rules.GetValueOrDefault(l.PartNumber), extensions[(l.PartNumber, l.LotNumber)], retests[(l.PartNumber, l.LotNumber)],
            excursions, residence[(l.PartNumber, l.LotNumber)], today, now, zone)).ToList();
    }

    /// <summary>Stock positions where a lot that is not usable by shelf life does not count for planning.</summary>
    public static async Task<IReadOnlyList<StockPosition>> PositionsAsync(IKernelUnitOfWork uow, string? partNumber, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var inventory = uow.Store<IInventoryLookup>();
        var blocked = (await StatesAsync(uow, partNumber, now, ct)).Where(s => !s.Usable).Select(s => (s.PartNumber, s.LotNumber)).ToHashSet();
        return StockPosition.From(await inventory.ListBalancesAsync(partNumber, ct), await inventory.ListLocationsAsync(ct), blocked);
    }
}
