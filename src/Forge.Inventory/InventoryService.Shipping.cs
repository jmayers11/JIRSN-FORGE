using System.Globalization;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;

namespace Forge.Inventory;

public sealed record NewShipmentLine(string PartNumber, decimal Quantity, string LocationCode, string? LotNumber);

public sealed record CreateShipment(string Customer, string? Reference, long? DemandId, string? ShipTo, IReadOnlyList<NewShipmentLine> Lines);

public sealed record PackShipment(long ShipmentId);

public sealed record ShipShipment(long ShipmentId, string? Carrier, string? Tracking);

public sealed record CancelShipment(long ShipmentId);

public sealed record ShipmentChanged(long ShipmentId, string Customer, string Status) : IDomainEvent
{
    public static string EventType => nameof(ShipmentChanged);

    public static int SchemaVersion => 1;
}

/// <summary>
/// Shipping (spec §47). A shipment is put together and packed by one person, who confirms the stock
/// is there; a different person with shipping authority releases it, which is when the stock leaves
/// the ledger. Nothing on an undecided nonconformance ships.
/// </summary>
public sealed partial class InventoryService
{
    private const string ShipmentEntity = "Shipment";

    public Task<CommandResult> CreateShipmentAsync(CreateShipment c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(SourceContext, nameof(CreateShipment), Permissions.InventoryTransact, ResourceRef.Of(ShipmentEntity));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var customer = BusinessCode.RequireName(c.Customer, "Customer");
            if (c.Lines is null || c.Lines.Count is 0 or > 99)
            {
                throw new CommandRejectedException("lines_required", "A shipment has 1 to 99 lines.");
            }

            var lines = new List<ShipmentLineInfo>();
            foreach (var (line, index) in c.Lines.Select((l, i) => (l, i)))
            {
                var item = await ItemAsync(uow, BusinessCode.Require(line.PartNumber, "Part"), ct);
                var location = await CompanyLocationAsync(uow, line.LocationCode, ct);
                lines.Add(new ShipmentLineInfo(index + 1, item.PartNumber, RequireQuantity(item, line.Quantity), location.Code, RequireLot(item, line.LotNumber)));
            }

            var shipment = new ShipmentInfo(0, customer, Optional(c.Reference), c.DemandId, Optional(c.ShipTo), ShipmentStatus.Open, context.Actor, now,
                null, null, null, null, null, null, null, lines);
            var shipmentId = await uow.Store<IInventoryStore>().InsertShipmentAsync(shipment, ct);
            var id = shipmentId.ToString(CultureInfo.InvariantCulture);
            return new CommandOutcome(
                CommandResult.Ok(id, $"Shipment {id} for {customer} created with {lines.Count} line(s)."),
                [pipeline.Event(new ShipmentChanged(shipmentId, customer, nameof(ShipmentStatus.Open)), context, SourceContext, ShipmentEntity, id)],
                new AuditChange(ShipmentEntity, id, null, CanonicalJson.Serialize(shipment with { ShipmentId = shipmentId })));
        }, ct);
    }

    public Task<CommandResult> PackShipmentAsync(PackShipment c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ShipmentAsync(nameof(PackShipment), c.ShipmentId, c, context, Permissions.InventoryTransact, false, async (uow, shipment, now) =>
        {
            if (shipment.Status != ShipmentStatus.Open)
            {
                throw new CommandRejectedException("not_open", $"Shipment {shipment.ShipmentId} is {shipment.Status}.");
            }

            await CheckShippableAsync(uow, shipment, ct);
            return (shipment with { Status = ShipmentStatus.Packed, PackedBy = context.Actor, PackedAt = now }, "packed; a different person must ship it", []);
        }, ct);
    }

    public Task<CommandResult> ShipShipmentAsync(ShipShipment c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ShipmentAsync(nameof(ShipShipment), c.ShipmentId, c, context, Permissions.ShippingRelease, false, async (uow, shipment, now) =>
        {
            if (shipment.Status != ShipmentStatus.Packed)
            {
                throw new CommandRejectedException("not_packed", $"Shipment {shipment.ShipmentId} is {shipment.Status}; it ships after it is packed.");
            }

            if (shipment.PackedBy == context.Actor)
            {
                throw new CommandRejectedException("separation_of_duties", "You packed this shipment; a different person must ship it.");
            }

            var store = uow.Store<IInventoryStore>();
            foreach (var part in shipment.Lines.Select(l => l.PartNumber).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                await store.LockPartAsync(part, ct);
            }

            await CheckShippableAsync(uow, shipment, ct);
            var transactions = new List<long>();
            foreach (var line in shipment.Lines)
            {
                var txn = new NewTransaction(InventoryTransactionKind.Shipment, line.PartNumber, line.Quantity, line.LocationCode, StockStatus.Released, null, null, line.LotNumber,
                    Reason: $"Shipment {shipment.ShipmentId} to {shipment.Customer}", ShipmentId: shipment.ShipmentId);
                transactions.Add(await store.InsertTransactionAsync(txn, now, context.Actor, ct));
            }

            var shipped = shipment with { Status = ShipmentStatus.Shipped, ShippedBy = context.Actor, ShippedAt = now, Carrier = Optional(c.Carrier), Tracking = Optional(c.Tracking) };
            var events = shipment.Lines.Zip(transactions, (line, txnId) => pipeline.Event(
                new InventoryMoved(txnId, nameof(InventoryTransactionKind.Shipment), line.PartNumber, line.Quantity, line.LocationCode, null), context, SourceContext, StockEntity, line.PartNumber)).ToList();
            return (shipped, "shipped", events);
        }, ct);
    }

    public Task<CommandResult> CancelShipmentAsync(CancelShipment c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ShipmentAsync(nameof(CancelShipment), c.ShipmentId, c, context, Permissions.InventoryTransact, true, (_, shipment, _) =>
            Task.FromResult<(ShipmentInfo, string, IReadOnlyList<EventEnvelope>)>(shipment.Status is ShipmentStatus.Open or ShipmentStatus.Packed
                ? (shipment with { Status = ShipmentStatus.Cancelled, CancelReason = context.Reason }, "cancelled", [])
                : throw new CommandRejectedException("not_cancellable", $"Shipment {shipment.ShipmentId} is {shipment.Status}.")), ct);
    }

    /// <summary>Stock is there (released, at the line's location and lot) and is not on an undecided nonconformance.</summary>
    private static async Task CheckShippableAsync(IKernelUnitOfWork uow, ShipmentInfo shipment, CancellationToken ct)
    {
        foreach (var group in shipment.Lines.GroupBy(l => (l.PartNumber, l.LocationCode, l.LotNumber)))
        {
            await RequireStockAsync(uow, group.Key.PartNumber, group.Key.LocationCode, group.Key.LotNumber, StockStatus.Released, group.Sum(l => l.Quantity), ct);
        }

        var ncrs = (await uow.Store<IQualityLookup>().ListNcrsAsync(false, ct))
            .Where(n => !(n.Status == NcrStatus.Dispositioned && n.Disposition == Disposition.UseAsIs))
            .ToList();
        foreach (var line in shipment.Lines)
        {
            var blocking = ncrs.FirstOrDefault(n => n.PartNumber == line.PartNumber && line.LotNumber is { } lot &&
                (n.LotNumber == lot || (n.OrderNumber is { } order && lot == "WO" + order.ToString(CultureInfo.InvariantCulture))));
            if (blocking is not null)
            {
                throw new CommandRejectedException("ncr_open", $"{line.PartNumber} lot {line.LotNumber} is on NCR {blocking.NcrId} ({blocking.Status}); it cannot ship until the NCR is closed or accepted use-as-is.");
            }
        }
    }

    private Task<CommandResult> ShipmentAsync<TCommand>(
        string commandType,
        long shipmentId,
        TCommand command,
        CommandContext context,
        string permission,
        bool reasonRequired,
        Func<IKernelUnitOfWork, ShipmentInfo, DateTimeOffset, Task<(ShipmentInfo Next, string Message, IReadOnlyList<EventEnvelope> Events)>> change,
        CancellationToken ct)
    {
        var id = shipmentId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, commandType, permission, ResourceRef.Of(ShipmentEntity, id), reasonRequired);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var shipment = await uow.Store<IInventoryLookup>().FindShipmentAsync(shipmentId, ct)
                ?? throw new CommandRejectedException("shipment_not_found", $"Shipment {id} does not exist.");
            var (next, message, events) = await change(uow, shipment, now);
            await uow.Store<IInventoryStore>().UpdateShipmentAsync(next, ct);
            var all = new List<EventEnvelope> { pipeline.Event(new ShipmentChanged(shipmentId, next.Customer, next.Status.ToString()), context, SourceContext, ShipmentEntity, id) };
            all.AddRange(events);
            return new CommandOutcome(
                CommandResult.Ok(id, $"Shipment {id} {message}."),
                all,
                new AuditChange(ShipmentEntity, id, CanonicalJson.Serialize(shipment), CanonicalJson.Serialize(next)));
        }, ct);
    }
}
