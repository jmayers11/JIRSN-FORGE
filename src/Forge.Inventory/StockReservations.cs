using System.Globalization;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;

namespace Forge.Inventory;

/// <summary>Brings a production order's reservations in step with it: what its released material still needs, as far as free stock allows.</summary>
public sealed record SyncOrderReservations(long OrderNumber);

/// <summary>
/// Brings customer-order demand's reservation in step with it. Demand belongs to Planning, so the caller states the part and the quantity
/// still to ship; zero releases the reservation.
/// </summary>
public sealed record SyncDemandReservation(long DemandId, string PartNumber, decimal QuantityToShip);

/// <summary>An inventory controller releases a reservation so the stock can be used elsewhere; the holder can be re-reserved later.</summary>
public sealed record ReleaseReservation(long ReservationId);

public sealed record StockReservationChanged(long ReservationId, string PartNumber, string Holder, string Change) : IDomainEvent
{
    public static string EventType => nameof(StockReservationChanged);

    public static int SchemaVersion => 1;
}

public interface IStockReservationStore
{
    Task LockPartAsync(string partNumber, CancellationToken ct);

    Task<long> InsertReservationAsync(StockReservation reservation, CancellationToken ct);

    Task SetReservationQuantityAsync(long reservationId, decimal quantity, CancellationToken ct);

    Task<bool> ReleaseReservationAsync(long reservationId, DateTimeOffset at, string actor, string reason, CancellationToken ct);

    Task<StockReservation?> FindReservationAsync(long reservationId, CancellationToken ct);
}

/// <summary>
/// Stock reservations (owner decision 2026-10-31). Released production orders and approved sales orders hold the free stock they need;
/// issues and shipments draw it down, and no one else may issue or ship stock that is reserved for someone else.
/// </summary>
public sealed class StockReservationService(CommandPipeline pipeline)
{
    private const string Entity = "StockReservation";

    public Task<CommandResult> SyncOrderAsync(SyncOrderReservations c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var id = c.OrderNumber.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(InventoryService.SourceContext, nameof(SyncOrderReservations), Permissions.InventoryReserve, ResourceRef.Of("ProductionOrder", id));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var order = await uow.Store<IExecutionLookup>().FindOrderAsync(c.OrderNumber, ct) ?? throw new CommandRejectedException("order_not_found", $"Order {id} does not exist.");
            var holding = order.Status is OrderStatus.Released or OrderStatus.Held;
            var issued = (await uow.Store<IInventoryLookup>().ListOrderIssuesAsync(ct)).Where(i => i.OrderNumber == order.OrderNumber).ToDictionary(i => i.PartNumber, i => i.NetIssued);
            var needs = holding
                ? order.Materials.GroupBy(m => m.ComponentPartNumber).ToDictionary(g => g.Key, g => Math.Max(0m, g.Sum(m => m.RequiredQuantity) - issued.GetValueOrDefault(g.Key)))
                : new Dictionary<string, decimal>();
            var existing = (await uow.Store<IStockReservationLookup>().ListReservationsAsync(ReservationTarget.ProductionOrder, order.OrderNumber, ct)).Where(r => r.Status == ReservationStatus.Active).ToList();
            var parts = needs.Keys.Concat(existing.Select(r => r.PartNumber)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            return await SyncAsync(uow, context, now, ReservationTarget.ProductionOrder, order.OrderNumber, parts.Select(p => (p, needs.GetValueOrDefault(p))).ToList(), existing,
                holding ? $"order {id} released" : $"order {id} is {order.Status}", ct);
        }, ct);
    }

    public Task<CommandResult> SyncDemandAsync(SyncDemandReservation c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var id = c.DemandId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(InventoryService.SourceContext, nameof(SyncDemandReservation), Permissions.InventoryReserve, ResourceRef.Of("Demand", id));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var part = BusinessCode.Require(c.PartNumber, "Part");
            var existing = (await uow.Store<IStockReservationLookup>().ListReservationsAsync(ReservationTarget.Demand, c.DemandId, ct)).Where(r => r.Status == ReservationStatus.Active).ToList();
            var parts = existing.Select(r => r.PartNumber).Append(part).Distinct(StringComparer.Ordinal).Select(p => (p, p == part ? Math.Max(0m, c.QuantityToShip) : 0m)).ToList();
            return await SyncAsync(uow, context, now, ReservationTarget.Demand, c.DemandId, parts, existing, $"demand {id}", ct);
        }, ct);
    }

    public Task<CommandResult> ReleaseAsync(ReleaseReservation c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var id = c.ReservationId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(InventoryService.SourceContext, nameof(ReleaseReservation), Permissions.InventoryApprove, ResourceRef.Of(Entity, id), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var store = uow.Store<IStockReservationStore>();
            var reservation = await store.FindReservationAsync(c.ReservationId, ct) ?? throw new CommandRejectedException("reservation_not_found", $"Reservation {id} does not exist.");
            if (!await store.ReleaseReservationAsync(reservation.ReservationId, now, context.Actor, context.Reason!, ct))
                return CommandOutcome.NoChange(CommandResult.NoChange(id, $"Reservation {id} is already released."));
            var holder = Holder(reservation.Target, reservation.Target == ReservationTarget.ProductionOrder ? reservation.OrderNumber!.Value : reservation.DemandId!.Value);
            return new CommandOutcome(CommandResult.Ok(id, $"Reservation {id} of {reservation.PartNumber} for {holder} released; the stock is free for others."),
                [pipeline.Event(new StockReservationChanged(reservation.ReservationId, reservation.PartNumber, holder, "Released"), context, InventoryService.SourceContext, Entity, id)],
                new AuditChange(Entity, id, nameof(ReservationStatus.Active), nameof(ReservationStatus.Released)));
        }, ct);
    }

    private async Task<CommandOutcome> SyncAsync(IKernelUnitOfWork uow, CommandContext context, DateTimeOffset now, ReservationTarget target, long key,
        IReadOnlyList<(string Part, decimal Need)> parts, IReadOnlyList<StockReservation> existing, string why, CancellationToken ct)
    {
        var store = uow.Store<IStockReservationStore>();
        var holder = Holder(target, key);
        var events = new List<EventEnvelope>();
        var said = new List<string>();
        foreach (var (part, need) in parts)
        {
            await store.LockPartAsync(part, ct);
            var balances = await StockReservationQueries.OutstandingAsync(uow, part, ct);
            var mine = balances.FirstOrDefault(b => b.Reservation.Target == target && Key(b.Reservation) == key);
            var others = balances.Where(b => b != mine).Sum(b => b.Outstanding);
            var free = Math.Max(0m, await StockReservationQueries.AvailableAsync(uow, part, now, ct) - others);
            var hold = Math.Min(need, free);
            var current = existing.FirstOrDefault(r => r.PartNumber == part);
            if (current is null)
            {
                if (hold <= 0) continue;
                var reservation = new StockReservation(0, target, target == ReservationTarget.ProductionOrder ? key : null, target == ReservationTarget.Demand ? key : null, part, hold,
                    ReservationStatus.Active, context.Actor, now, null, null, null);
                var rid = await store.InsertReservationAsync(reservation, ct);
                events.Add(pipeline.Event(new StockReservationChanged(rid, part, holder, "Reserved"), context, InventoryService.SourceContext, Entity, rid.ToString(CultureInfo.InvariantCulture)));
                said.Add(string.Create(CultureInfo.InvariantCulture, $"{hold} {part} reserved{(hold < need ? $" ({need - hold} short)" : string.Empty)}"));
                continue;
            }

            var drawn = mine?.Drawn ?? 0;
            if (need <= 0)
            {
                if (await store.ReleaseReservationAsync(current.ReservationId, now, context.Actor, $"No longer needed: {why}", ct))
                {
                    events.Add(pipeline.Event(new StockReservationChanged(current.ReservationId, part, holder, "Released"), context, InventoryService.SourceContext, Entity, current.ReservationId.ToString(CultureInfo.InvariantCulture)));
                    said.Add($"{part} reservation released");
                }

                continue;
            }

            var quantity = drawn + hold;
            if (quantity == current.Quantity) continue;
            await store.SetReservationQuantityAsync(current.ReservationId, quantity, ct);
            events.Add(pipeline.Event(new StockReservationChanged(current.ReservationId, part, holder, "Changed"), context, InventoryService.SourceContext, Entity, current.ReservationId.ToString(CultureInfo.InvariantCulture)));
            said.Add(string.Create(CultureInfo.InvariantCulture, $"{part} now holds {hold}{(hold < need ? $" ({need - hold} short)" : string.Empty)}"));
        }

        var id = key.ToString(CultureInfo.InvariantCulture);
        return events.Count == 0
            ? CommandOutcome.NoChange(CommandResult.NoChange(id, $"Reservations for {holder} already match."))
            : new CommandOutcome(CommandResult.Ok(id, $"Reservations for {holder}: {string.Join("; ", said)}."), events,
                new AuditChange(Entity, $"{target}:{id}", null, CanonicalJson.Serialize(new { why, said })));
    }

    private static long Key(StockReservation r) => r.Target == ReservationTarget.ProductionOrder ? r.OrderNumber!.Value : r.DemandId!.Value;

    private static string Holder(ReservationTarget target, long key) =>
        (target == ReservationTarget.ProductionOrder ? "order " : "demand ") + key.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Issues and shipments may not take stock that is reserved for someone else.</summary>
internal static class ReservationGate
{
    public static async Task RequireUnreservedAsync(IKernelUnitOfWork uow, string partNumber, string locationCode, string? lot, decimal quantity,
        ReservationTarget? holderTarget, long? holderKey, DateTimeOffset now, CancellationToken ct)
    {
        var balances = await StockReservationQueries.OutstandingAsync(uow, partNumber, ct);
        if (balances.Count == 0) return;
        var positions = await ShelfLifeQueries.PositionsAsync(uow, partNumber, now, ct);
        // Only reservable stock (released, nettable, usable) is protected; stock taken from elsewhere does not touch reservations.
        if (!positions.Any(p => p.PartNumber == partNumber && p.LocationCode == locationCode && p.LotNumber == lot && p.AvailableForPlanning)) return;
        var available = positions.Where(p => p.PartNumber == partNumber && p.AvailableForPlanning).Sum(p => p.Quantity);
        var others = balances.Where(b => !(holderTarget is { } t && b.Reservation.Target == t
            && (t == ReservationTarget.ProductionOrder ? b.Reservation.OrderNumber : b.Reservation.DemandId) == holderKey)).ToList();
        var reserved = others.Sum(b => b.Outstanding);
        if (quantity > available - reserved)
            throw new CommandRejectedException("reserved_for_other", string.Create(CultureInfo.InvariantCulture,
                $"Only {Math.Max(0m, available - reserved)} {partNumber} is free; {reserved} is reserved for {string.Join(", ", others.Where(b => b.Outstanding > 0).Select(b => b.Holder))}. An inventory controller can release a reservation, with a reason, if this need comes first."));
    }
}
