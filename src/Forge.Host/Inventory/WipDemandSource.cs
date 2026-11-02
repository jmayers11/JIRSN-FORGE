using System.Globalization;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Planning;

namespace Forge.Host.Inventory;

/// <summary>
/// Open customer, forecast and other independent demand for a part, net of what has shipped and what stock is already reserved for it:
/// the demand open production orders of the part are counting on (owner decision 2026-11-02: only output nothing counts on is offered).
/// </summary>
public sealed class WipDemandSource : IWipDemandSource
{
    public async Task<IReadOnlyList<IndependentDemand>> OpenDemandAsync(IKernelUnitOfWork uow, string partNumber, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var demands = (await uow.Store<IDemandStore>().ListAsync(false, ct)).Where(d => d.PartNumber == partNumber && d.Status == DemandStatus.Open).ToList();
        if (demands.Count == 0) return [];
        var shipped = (await uow.Store<IInventoryLookup>().ListShippedByDemandAsync(ct)).Where(s => s.PartNumber == partNumber).GroupBy(s => s.DemandId).ToDictionary(g => g.Key, g => g.Sum(s => s.Shipped));
        var reserved = (await StockReservationQueries.OutstandingAsync(uow, partNumber, ct)).Where(b => b.Reservation.Target == ReservationTarget.Demand && b.Reservation.DemandId is not null)
            .GroupBy(b => b.Reservation.DemandId!.Value).ToDictionary(g => g.Key, g => g.Sum(b => b.Outstanding));
        return demands.Select(d => new IndependentDemand(d.DemandId,
                d.Reference ?? string.Create(CultureInfo.InvariantCulture, $"{d.Kind} demand {d.DemandId}"),
                d.NeedDate, Math.Max(0m, d.Quantity - shipped.GetValueOrDefault(d.DemandId) - reserved.GetValueOrDefault(d.DemandId))))
            .Where(d => d.Quantity > 0).ToList();
    }
}
