using Forge.Kernel.Access;

namespace Forge.Inventory.Contracts;

// Stock reservations (owner decision 2026-10-31): releasing a production order or approving a sales order reserves available stock for it.
// Issues to that order and shipments against that demand draw the reservation down; nobody else may issue or ship reserved stock. An
// inventory controller can release a reservation, with a reason, when the stock is needed elsewhere.

public enum ReservationTarget
{
    /// <summary>Component stock held for a released production order.</summary>
    ProductionOrder,

    /// <summary>Finished stock held for customer-order demand (an approved sales order line).</summary>
    Demand,
}

public enum ReservationStatus { Active, Released }

/// <param name="Quantity">What has been reserved in total, including what has since been drawn down by issues or shipments.</param>
public sealed record StockReservation(long ReservationId, ReservationTarget Target, long? OrderNumber, long? DemandId, string PartNumber, decimal Quantity,
    ReservationStatus Status, string CreatedBy, DateTimeOffset CreatedAt, string? ReleasedBy, DateTimeOffset? ReleasedAt, string? ReleaseReason);

/// <summary>A reservation with what issues or shipments have drawn from it and what it still holds.</summary>
public sealed record ReservationBalance(StockReservation Reservation, decimal Drawn, decimal Outstanding)
{
    public string Holder => Reservation.Target == ReservationTarget.ProductionOrder
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"order {Reservation.OrderNumber}")
        : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"demand {Reservation.DemandId}");
}

public interface IStockReservationLookup
{
    /// <summary>Active reservations (of one part when given), oldest first.</summary>
    Task<IReadOnlyList<StockReservation>> ListActiveReservationsAsync(string? partNumber, CancellationToken ct);

    Task<IReadOnlyList<StockReservation>> ListReservationsAsync(ReservationTarget target, long key, CancellationToken ct);
}

public static class StockReservationQueries
{
    /// <summary>Active reservations with what they still hold: issued to the order, or shipped against the demand, draws them down.</summary>
    public static async Task<IReadOnlyList<ReservationBalance>> OutstandingAsync(IKernelUnitOfWork uow, string? partNumber, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var active = await uow.Store<IStockReservationLookup>().ListActiveReservationsAsync(partNumber, ct);
        if (active.Count == 0) return [];
        var lookup = uow.Store<IInventoryLookup>();
        var issued = (await lookup.ListOrderIssuesAsync(ct)).ToDictionary(i => (i.OrderNumber, i.PartNumber), i => i.NetIssued);
        var shipped = (await lookup.ListShippedByDemandAsync(ct)).ToDictionary(s => (s.DemandId, s.PartNumber), s => s.Shipped);
        return active.Select(r =>
        {
            var drawn = Math.Max(0m, r.Target == ReservationTarget.ProductionOrder
                ? issued.GetValueOrDefault((r.OrderNumber!.Value, r.PartNumber))
                : shipped.GetValueOrDefault((r.DemandId!.Value, r.PartNumber)));
            return new ReservationBalance(r, drawn, Math.Max(0m, r.Quantity - drawn));
        }).ToList();
    }

    /// <summary>Stock a part has available for planning (released, nettable, usable), summed over locations.</summary>
    public static async Task<decimal> AvailableAsync(IKernelUnitOfWork uow, string partNumber, DateTimeOffset now, CancellationToken ct) =>
        (await ShelfLifeQueries.PositionsAsync(uow, partNumber, now, ct)).Where(p => p.AvailableForPlanning && p.PartNumber == partNumber).Sum(p => p.Quantity);
}
