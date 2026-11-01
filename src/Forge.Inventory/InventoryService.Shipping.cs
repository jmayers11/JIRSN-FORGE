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

public sealed record CreateShipment(string Customer, string? Reference, long? DemandId, string? ShipTo, IReadOnlyList<NewShipmentLine> Lines, bool RequirePackaging = false, string? ContainerCode = null);

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
                null, null, null, null, null, null, null, lines, c.RequirePackaging, Optional(c.ContainerCode));
            if (shipment.ContainerCode is { } containerCode)
            {
                var container = await uow.Store<IInventoryLookup>().FindContainerAsync(containerCode, ct)
                    ?? throw new CommandRejectedException("container_not_found", $"Container {containerCode} does not exist.");
                if (container.Status != ContainerStatus.Sealed || container.ShipmentId is not null)
                    throw new CommandRejectedException("container_not_sealed", $"Container {containerCode} must be sealed and unassigned to a shipment.");
            }
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

            await CheckShippableAsync(uow, shipment, now, ct);
            foreach (var line in shipment.Lines)
                await productRelease.RequireReleasedScopeAsync(uow, context.Principal, line.PartNumber, line.LotNumber, line.LocationCode, line.Quantity, now, ct);
            return (shipment with { Status = ShipmentStatus.Packed, PackedBy = context.Actor, PackedAt = now }, "packed; a different person must ship it", []);
        }, ct);
    }

    public Task<CommandResult> ShipShipmentAsync(ShipShipment c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ShipmentAsync(nameof(ShipShipment), c.ShipmentId, c, context, Permissions.ShippingRelease, false, async (uow, shipment, now) =>
        {
            var store = uow.Store<IInventoryStore>();
            foreach (var part in shipment.Lines.Select(l => l.PartNumber).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                await store.LockPartAsync(part, ct);
            }

            await CheckShipmentReleaseAsync(uow, shipment, context.Actor, now, ct);
            foreach (var line in shipment.Lines)
                await productRelease.RequireReleasedScopeAsync(uow, context.Principal, line.PartNumber, line.LotNumber, line.LocationCode, line.Quantity, now, ct);
            var transactions = new List<long>();
            foreach (var line in shipment.Lines)
            {
                var txn = new NewTransaction(InventoryTransactionKind.Shipment, line.PartNumber, line.Quantity, line.LocationCode, StockStatus.Released, null, null, line.LotNumber,
                    Reason: $"Shipment {shipment.ShipmentId} to {shipment.Customer}", ShipmentId: shipment.ShipmentId);
                transactions.Add(await store.InsertTransactionAsync(txn, now, context.Actor, ct));
            }

            if (shipment.ContainerCode is { } containerCode)
                await store.ShipContainerAsync(containerCode, shipment.ShipmentId, now, ct);

            var shipped = shipment with { Status = ShipmentStatus.Shipped, ShippedBy = context.Actor, ShippedAt = now, Carrier = Optional(c.Carrier), Tracking = Optional(c.Tracking) };
            var events = shipment.Lines.Zip(transactions, (line, txnId) => pipeline.Event(
                new InventoryMoved(txnId, nameof(InventoryTransactionKind.Shipment), line.PartNumber, line.Quantity, line.LocationCode, null), context, SourceContext, StockEntity, line.PartNumber)).ToList();
            if (shipment.ContainerCode is { } shippedContainer)
                events.Add(pipeline.Event(new ContainerChanged(shippedContainer, $"ShippedWith:{shipment.ShipmentId}"), context, SourceContext, "Container", shippedContainer));
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

    /// <summary>The command's read-only release gate, also used by private recovery trials. It never packs or ships.</summary>
    public static async Task CheckShipmentReleaseAsync(IKernelUnitOfWork uow, ShipmentInfo shipment, string actor, DateTimeOffset now, CancellationToken ct)
    {
        if (shipment.Status != ShipmentStatus.Packed)
            throw new CommandRejectedException("not_packed", $"Shipment {shipment.ShipmentId} is {shipment.Status}; it ships after it is packed.");
        if (shipment.PackedBy == actor)
            throw new CommandRejectedException("separation_of_duties", "You packed this shipment; a different person must ship it.");
        await CheckShippableAsync(uow, shipment, now, ct);
        await Forge.Kernel.Holds.HoldGate.RequireNoneAsync(uow, new Forge.Kernel.Holds.HoldCheck(
            Forge.Kernel.Holds.HoldActivity.Ship,
            Parts: shipment.Lines.Select(l => l.PartNumber).Distinct(StringComparer.Ordinal).ToList(),
            LotList: shipment.Lines.Where(l => l.LotNumber is not null).Select(l => new Forge.Kernel.Holds.HoldLot(l.PartNumber, l.LotNumber!)).ToList(),
            ShipmentId: shipment.ShipmentId), ct);
    }

    /// <summary>Stock is there (released, at the line's location and lot) and is not on an undecided nonconformance.</summary>
    private static async Task CheckShippableAsync(IKernelUnitOfWork uow, ShipmentInfo shipment, DateTimeOffset now, CancellationToken ct)
    {
        if (shipment.PackagingRequired)
        {
            var hasPackaging = false;
            foreach (var line in shipment.Lines)
            {
                if ((await ItemAsync(uow, line.PartNumber, ct)).Class == Forge.Engineering.Contracts.ItemClass.Packaging)
                    hasPackaging = true;
            }

            if (!hasPackaging)
                throw new CommandRejectedException("packaging_required", $"Shipment {shipment.ShipmentId} requires an explicitly identified packaging material line before it can be packed.");
        }

        if (shipment.ContainerCode is { } containerCode)
        {
            var container = await uow.Store<IInventoryLookup>().FindContainerAsync(containerCode, ct)
                ?? throw new CommandRejectedException("container_not_found", $"Container {containerCode} does not exist.");
            if (container.Status != ContainerStatus.Sealed || container.ShipmentId is not null)
                throw new CommandRejectedException("container_not_sealed", $"Container {containerCode} must be sealed and available.");
            var shipmentContents = shipment.Lines.GroupBy(l => (l.PartNumber, l.LocationCode, l.LotNumber)).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
            var containerContents = container.Contents.Where(x => x.Status == StockStatus.Released).GroupBy(x => (x.PartNumber, x.LocationCode, x.LotNumber)).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
            if (shipmentContents.Count != containerContents.Count || shipmentContents.Any(pair => containerContents.GetValueOrDefault(pair.Key) != pair.Value))
                throw new CommandRejectedException("container_contents_mismatch", $"Container {containerCode} contents must exactly match the shipment's part, lot, location and quantity lines.");
        }

        foreach (var group in shipment.Lines.GroupBy(l => (l.PartNumber, l.LocationCode, l.LotNumber)))
        {
            await RequireStockAsync(uow, group.Key.PartNumber, group.Key.LocationCode, group.Key.LotNumber, StockStatus.Released, group.Sum(l => l.Quantity), ct);
            await RequireUsableAsync(uow, await ItemAsync(uow, group.Key.PartNumber, ct), group.Key.LotNumber, now, ct); // never ship what has expired (V3.6)
            // Owner decision 2026-10-31: stock reserved for another order or demand is not shipped here.
            await ReservationGate.RequireUnreservedAsync(uow, group.Key.PartNumber, group.Key.LocationCode, group.Key.LotNumber, group.Sum(l => l.Quantity),
                shipment.DemandId is null ? null : ReservationTarget.Demand, shipment.DemandId, now, ct);
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
            var preliminary = await uow.Store<IInventoryLookup>().FindShipmentAsync(shipmentId, ct)
                ?? throw new CommandRejectedException("shipment_not_found", $"Shipment {id} does not exist.");
            if (preliminary.ContainerCode is { } lockedContainer)
                await uow.Store<IProductReleaseInventoryStore>().LockContainerAsync(lockedContainer, ct);
            await uow.Store<IProductReleaseInventoryStore>().LockShipmentAsync(shipmentId, ct);
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
        }, ct, async (uow, now) =>
        {
            var actual = await sourceAuthority.ResolveAsync(uow, spec, context, now, ct);
            var shipment = await uow.Store<IInventoryLookup>().FindShipmentAsync(shipmentId, ct)
                ?? throw new CommandRejectedException("shipment_not_found", $"Shipment {id} does not exist.");
            if (command is not CancelShipment)
                foreach (var line in shipment.Lines)
                    if (shipment.Status == ShipmentStatus.Shipped)
                        await productRelease.RecheckReleasedScopeAsync(uow, context.Principal, line.PartNumber, line.LotNumber, line.LocationCode, now, ct);
                    else
                        await productRelease.RequireReleasedScopeAsync(uow, context.Principal, line.PartNumber, line.LotNumber, line.LocationCode, line.Quantity, now, ct);
            return actual;
        });
    }
}
