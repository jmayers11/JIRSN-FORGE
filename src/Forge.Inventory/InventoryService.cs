using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Procurement.Contracts;
using Forge.Quality.Contracts;
using Forge.Resources.Contracts;

namespace Forge.Inventory;

public sealed record CreateLocation(string Code, string SiteCode, string Name, LocationKind Kind);

public sealed record RetireLocation(string Code);

/// <summary>
/// Material arriving. Against a purchase order line, or (with a reason) without one, for example
/// opening balances or customer-supplied material. <paramref name="Inspect"/> puts it into Inspection.
/// </summary>
public sealed record ReceiveStock(string PartNumber, decimal Quantity, string LocationCode, string? LotNumber, string? SupplierLot, long? PoNumber, int? PoLine, bool Inspect);

public sealed record IssueToOrder(long OrderNumber, string PartNumber, decimal Quantity, string LocationCode, string? LotNumber);

public sealed record ReturnFromOrder(long OrderNumber, string PartNumber, decimal Quantity, string LocationCode, string? LotNumber);

public sealed record TransferStock(string PartNumber, decimal Quantity, string FromLocation, string ToLocation, string? LotNumber, StockStatus Status);

public sealed record ScrapStock(string PartNumber, decimal Quantity, string LocationCode, string? LotNumber, StockStatus Status);

public sealed record HoldStock(string PartNumber, decimal Quantity, string LocationCode, string? LotNumber);

/// <summary>Releases stock from quality hold or incoming inspection to Available. Never by the person who put it there.</summary>
public sealed record ReleaseStock(string PartNumber, decimal Quantity, string LocationCode, string? LotNumber, StockStatus FromStatus);

/// <summary>Finished goods from a production order, up to the good quantity reported on its last operation.</summary>
public sealed record ReceiveFromProduction(long OrderNumber, decimal Quantity, string LocationCode, string? LotNumber);

public sealed record RequestAdjustment(string PartNumber, string LocationCode, string? LotNumber, StockStatus Status, decimal Change);

public sealed record RecordCount(string PartNumber, string LocationCode, string? LotNumber, StockStatus Status, decimal Counted);

public sealed record ApproveAdjustment(long RequestId);

public sealed record RejectAdjustment(long RequestId);

public sealed record InventoryReceived(long TransactionId, string PartNumber, decimal Quantity, string Location, string Status, long? PoNumber, long? OrderNumber) : IDomainEvent
{
    public static string EventType => nameof(InventoryReceived);

    public static int SchemaVersion => 1;
}

public sealed record InventoryIssued(long TransactionId, string PartNumber, decimal Quantity, string Location, long OrderNumber) : IDomainEvent
{
    public static string EventType => nameof(InventoryIssued);

    public static int SchemaVersion => 1;
}

public sealed record InventoryAdjusted(long TransactionId, string PartNumber, decimal Change, string Location, long RequestId) : IDomainEvent
{
    public static string EventType => nameof(InventoryAdjusted);

    public static int SchemaVersion => 1;
}

public sealed record InventoryMoved(long TransactionId, string Kind, string PartNumber, decimal Quantity, string? From, string? To) : IDomainEvent
{
    public static string EventType => nameof(InventoryMoved);

    public static int SchemaVersion => 1;
}

public sealed record AdjustmentRequested(long RequestId, string Kind, string PartNumber, string Location, decimal Variance) : IDomainEvent
{
    public static string EventType => nameof(AdjustmentRequested);

    public static int SchemaVersion => 1;
}

public sealed record AdjustmentDecided(long RequestId, string State) : IDomainEvent
{
    public static string EventType => nameof(AdjustmentDecided);

    public static int SchemaVersion => 1;
}

public sealed record LocationChanged(string Code, string Change) : IDomainEvent
{
    public static string EventType => nameof(LocationChanged);

    public static int SchemaVersion => 1;
}

/// <summary>A ledger entry before it is written.</summary>
public sealed record NewTransaction(
    InventoryTransactionKind Kind,
    string PartNumber,
    decimal Quantity,
    string? FromLocation,
    StockStatus? FromStatus,
    string? ToLocation,
    StockStatus? ToStatus,
    string? LotNumber,
    long? OrderNumber = null,
    long? PoNumber = null,
    int? PoLine = null,
    string? Reason = null,
    long? RequestId = null,
    string? ApprovedBy = null,
    long? ShipmentId = null);

public interface IInventoryStore
{
    Task<long> InsertShipmentAsync(ShipmentInfo shipment, CancellationToken ct);

    Task UpdateShipmentAsync(ShipmentInfo shipment, CancellationToken ct);

    /// <summary>Serializes stock changes of one part for the rest of the transaction.</summary>
    Task LockPartAsync(string partNumber, CancellationToken ct);

    Task<decimal> BalanceAsync(string partNumber, string locationCode, string? lotNumber, StockStatus status, CancellationToken ct);

    Task<long> InsertTransactionAsync(NewTransaction transaction, DateTimeOffset at, string actor, CancellationToken ct);

    Task EnsureLotAsync(string partNumber, string lotNumber, string? supplierLot, DateTimeOffset at, string actor, CancellationToken ct);

    Task<bool> InsertLocationAsync(LocationInfo location, DateTimeOffset at, string actor, CancellationToken ct);

    Task SetLocationActiveAsync(string code, bool active, CancellationToken ct);

    Task<long> InsertRequestAsync(AdjustmentRequest request, CancellationToken ct);

    Task<AdjustmentRequest?> FindRequestAsync(long requestId, CancellationToken ct);

    Task DecideRequestAsync(long requestId, AdjustmentState state, string actor, DateTimeOffset at, long? transactionId, CancellationToken ct);

    /// <summary>Who last moved stock of this part, location and lot into the status (for the two-person release rule).</summary>
    Task<string?> LastPlacedIntoAsync(string partNumber, string locationCode, string? lotNumber, StockStatus status, CancellationToken ct);
}

/// <summary>
/// Inventory is a ledger (spec §11): every quantity is the sum of transactions, each with who, when,
/// why and what it relates to. Stock never goes below zero. Adjustments and count variances need a
/// second person; so does releasing stock from hold or inspection.
/// </summary>
public sealed partial class InventoryService(CommandPipeline pipeline)
{
    public const string SourceContext = "Inventory";
    private const string StockEntity = "Stock";

    public Task<CommandResult> CreateLocationAsync(CreateLocation c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var code = BusinessCode.Require(c.Code, "Location");
        var spec = new CommandSpec(SourceContext, nameof(CreateLocation), Permissions.InventoryManage, ResourceRef.Of("Location", code));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var site = BusinessCode.Require(c.SiteCode, "Site");
            var name = BusinessCode.RequireName(c.Name, "Location");
            if (!(await uow.Store<IResourcesLookup>().ListSitesAsync(ct)).Any(s => s.Code == site))
            {
                throw new CommandRejectedException("unknown_site", $"Site {site} does not exist.");
            }

            var location = new LocationInfo(code, site, name, c.Kind, true);
            if (!await uow.Store<IInventoryStore>().InsertLocationAsync(location, now, context.Actor, ct))
            {
                throw new CommandRejectedException("code_exists", $"Location {code} already exists.");
            }

            return new CommandOutcome(
                CommandResult.Ok(code, $"Location {code} ({c.Kind}) created."),
                [pipeline.Event(new LocationChanged(code, "Created"), context, SourceContext, "Location", code)],
                new AuditChange("Location", code, null, CanonicalJson.Serialize(location)));
        }, ct);
    }

    public Task<CommandResult> RetireLocationAsync(RetireLocation c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var code = BusinessCode.Require(c.Code, "Location");
        var spec = new CommandSpec(SourceContext, nameof(RetireLocation), Permissions.InventoryManage, ResourceRef.Of("Location", code), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, _) =>
        {
            var lookup = uow.Store<IInventoryLookup>();
            var location = (await lookup.ListLocationsAsync(ct)).FirstOrDefault(l => l.Code == code)
                ?? throw new CommandRejectedException("location_not_found", $"Location {code} does not exist.");
            if (!location.Active)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(code, "Already retired."));
            }

            var stock = (await lookup.ListBalancesAsync(null, ct)).Where(b => b.LocationCode == code).ToList();
            if (stock.Count > 0)
            {
                throw new CommandRejectedException("location_not_empty", $"{code} still holds {string.Join(", ", stock.Select(b => $"{b.Quantity} {b.PartNumber}"))}. Move it first.");
            }

            await uow.Store<IInventoryStore>().SetLocationActiveAsync(code, false, ct);
            return new CommandOutcome(
                CommandResult.Ok(code, $"Location {code} retired."),
                [pipeline.Event(new LocationChanged(code, "Retired"), context, SourceContext, "Location", code)],
                new AuditChange("Location", code, "Active", "Retired"));
        }, ct);
    }

    public Task<CommandResult> ReceiveAsync(ReceiveStock c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var reasonRequired = c.PoNumber is null;
        return PostAsync(nameof(ReceiveStock), c.PartNumber, c, context, Permissions.InventoryTransact, reasonRequired, async (uow, item, now) =>
        {
            var location = await ActiveLocationAsync(uow, c.LocationCode, ct);
            var lot = RequireLot(item, c.LotNumber);
            var quantity = RequireQuantity(item, c.Quantity);
            int? line = null;
            if (c.PoNumber is { } poNumber)
            {
                var po = await uow.Store<IProcurementLookup>().FindPurchaseOrderAsync(poNumber, ct)
                    ?? throw new CommandRejectedException("purchase_order_not_found", $"Purchase order {poNumber} does not exist.");
                if (po.Status != PurchaseOrderStatus.Approved)
                {
                    throw new CommandRejectedException("purchase_order_not_open", $"Purchase order {poNumber} is {po.Status}; receive only against an approved order.");
                }

                var poLine = c.PoLine is { } n
                    ? po.Lines.FirstOrDefault(l => l.LineNumber == n)
                    : po.Lines.SingleOrDefault(l => l.PartNumber == item.PartNumber && l.Status == PurchaseLineStatus.Open);
                if (poLine is null || poLine.PartNumber != item.PartNumber || poLine.Status != PurchaseLineStatus.Open)
                {
                    throw new CommandRejectedException("purchase_line_not_found", $"Purchase order {poNumber} has no open line for {item.PartNumber}" + (c.PoLine is null ? "; name the line." : $" at line {c.PoLine}."));
                }

                var received = (await uow.Store<IInventoryLookup>().ListPurchaseReceiptsAsync(ct)).FirstOrDefault(r => r.PoNumber == poNumber && r.LineNumber == poLine.LineNumber)?.Received ?? 0;
                if (received + quantity > poLine.Quantity)
                {
                    throw new CommandRejectedException("over_receipt", $"Line {poLine.LineNumber} ordered {poLine.Quantity}; {received} already received, so at most {poLine.Quantity - received} more.");
                }

                line = poLine.LineNumber;
            }

            if (lot is not null)
            {
                await uow.Store<IInventoryStore>().EnsureLotAsync(item.PartNumber, lot, Optional(c.SupplierLot), now, context.Actor, ct);
            }

            var status = c.Inspect ? StockStatus.Inspection : StockStatus.Available;
            var txn = new NewTransaction(InventoryTransactionKind.Receipt, item.PartNumber, quantity, null, null, location.Code, status, lot, PoNumber: c.PoNumber, PoLine: line, Reason: context.Reason);
            return (txn, $"Received {quantity} {item.UnitOfMeasure} {item.PartNumber} into {location.Code} ({status})" + (c.PoNumber is null ? "." : $" against PO {c.PoNumber} line {line}."),
                id => pipeline.Event(new InventoryReceived(id, item.PartNumber, quantity, location.Code, status.ToString(), c.PoNumber, null), context, SourceContext, StockEntity, item.PartNumber));
        }, ct);
    }

    public Task<CommandResult> IssueToOrderAsync(IssueToOrder c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return PostAsync(nameof(IssueToOrder), c.PartNumber, c, context, Permissions.InventoryTransact, false, async (uow, item, _) =>
        {
            var order = await ReleasedOrderAsync(uow, c.OrderNumber, context, ct);
            var required = order.Materials.Where(m => m.ComponentPartNumber == item.PartNumber).Sum(m => m.RequiredQuantity);
            if (required == 0)
            {
                throw new CommandRejectedException("not_on_order", $"Order {order.OrderNumber} does not use {item.PartNumber}.");
            }

            var quantity = RequireQuantity(item, c.Quantity);
            var issued = (await uow.Store<IInventoryLookup>().ListOrderIssuesAsync(ct)).FirstOrDefault(i => i.OrderNumber == order.OrderNumber && i.PartNumber == item.PartNumber)?.NetIssued ?? 0;
            if (issued + quantity > required)
            {
                throw new CommandRejectedException("over_issue", $"Order {order.OrderNumber} needs {required} {item.PartNumber}; {issued} already issued, so at most {required - issued} more. Over-issue is not allowed; scrap or adjust instead.");
            }

            var location = await ActiveLocationAsync(uow, c.LocationCode, ct);
            var lot = RequireLot(item, c.LotNumber);
            await RequireStockAsync(uow, item.PartNumber, location.Code, lot, StockStatus.Available, quantity, ct);
            var txn = new NewTransaction(InventoryTransactionKind.Issue, item.PartNumber, quantity, location.Code, StockStatus.Available, null, null, lot, OrderNumber: order.OrderNumber, Reason: context.Reason);
            return (txn, $"Issued {quantity} {item.UnitOfMeasure} {item.PartNumber} from {location.Code} to order {order.OrderNumber}.",
                id => pipeline.Event(new InventoryIssued(id, item.PartNumber, quantity, location.Code, order.OrderNumber), context, SourceContext, StockEntity, item.PartNumber));
        }, ct);
    }

    public Task<CommandResult> ReturnFromOrderAsync(ReturnFromOrder c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return PostAsync(nameof(ReturnFromOrder), c.PartNumber, c, context, Permissions.InventoryTransact, true, async (uow, item, _) =>
        {
            var order = await uow.Store<IExecutionLookup>().FindOrderAsync(c.OrderNumber, ct);
            if (order is null || !Visible(context, order))
            {
                throw new CommandRejectedException("order_not_found", $"Order {c.OrderNumber} does not exist.");
            }

            var quantity = RequireQuantity(item, c.Quantity);
            var issued = (await uow.Store<IInventoryLookup>().ListOrderIssuesAsync(ct)).FirstOrDefault(i => i.OrderNumber == order.OrderNumber && i.PartNumber == item.PartNumber)?.NetIssued ?? 0;
            if (quantity > issued)
            {
                throw new CommandRejectedException("over_return", $"Only {issued} {item.PartNumber} is issued to order {order.OrderNumber}.");
            }

            var location = await ActiveLocationAsync(uow, c.LocationCode, ct);
            var lot = RequireLot(item, c.LotNumber);
            var txn = new NewTransaction(InventoryTransactionKind.Return, item.PartNumber, quantity, null, null, location.Code, StockStatus.Available, lot, OrderNumber: order.OrderNumber, Reason: context.Reason);
            return (txn, $"Returned {quantity} {item.UnitOfMeasure} {item.PartNumber} from order {order.OrderNumber} to {location.Code}.",
                id => pipeline.Event(new InventoryMoved(id, nameof(InventoryTransactionKind.Return), item.PartNumber, quantity, null, location.Code), context, SourceContext, StockEntity, item.PartNumber));
        }, ct);
    }

    public Task<CommandResult> TransferAsync(TransferStock c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return PostAsync(nameof(TransferStock), c.PartNumber, c, context, Permissions.InventoryTransact, false, async (uow, item, _) =>
        {
            var from = await ActiveLocationAsync(uow, c.FromLocation, ct);
            var to = await ActiveLocationAsync(uow, c.ToLocation, ct);
            if (from.Code == to.Code)
            {
                throw new CommandRejectedException("same_location", "Transfer to a different location.");
            }

            var lot = RequireLot(item, c.LotNumber);
            var quantity = RequireQuantity(item, c.Quantity);
            await RequireStockAsync(uow, item.PartNumber, from.Code, lot, c.Status, quantity, ct);
            var txn = new NewTransaction(InventoryTransactionKind.Transfer, item.PartNumber, quantity, from.Code, c.Status, to.Code, c.Status, lot, Reason: context.Reason);
            return (txn, $"Moved {quantity} {item.UnitOfMeasure} {item.PartNumber} from {from.Code} to {to.Code}.",
                id => pipeline.Event(new InventoryMoved(id, nameof(InventoryTransactionKind.Transfer), item.PartNumber, quantity, from.Code, to.Code), context, SourceContext, StockEntity, item.PartNumber));
        }, ct);
    }

    public Task<CommandResult> ScrapAsync(ScrapStock c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return PostAsync(nameof(ScrapStock), c.PartNumber, c, context, Permissions.InventoryTransact, true, async (uow, item, _) =>
        {
            var location = await LocationAsync(uow, c.LocationCode, ct);
            var lot = RequireLot(item, c.LotNumber);
            var quantity = RequireQuantity(item, c.Quantity);
            await RequireStockAsync(uow, item.PartNumber, location.Code, lot, c.Status, quantity, ct);
            var txn = new NewTransaction(InventoryTransactionKind.Scrap, item.PartNumber, quantity, location.Code, c.Status, null, null, lot, Reason: context.Reason);
            return (txn, $"Scrapped {quantity} {item.UnitOfMeasure} {item.PartNumber} at {location.Code}.",
                id => pipeline.Event(new InventoryMoved(id, nameof(InventoryTransactionKind.Scrap), item.PartNumber, quantity, location.Code, null), context, SourceContext, StockEntity, item.PartNumber));
        }, ct);
    }

    public Task<CommandResult> HoldAsync(HoldStock c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return PostAsync(nameof(HoldStock), c.PartNumber, c, context, Permissions.InventoryTransact, true, async (uow, item, _) =>
        {
            var location = await LocationAsync(uow, c.LocationCode, ct);
            var lot = RequireLot(item, c.LotNumber);
            var quantity = RequireQuantity(item, c.Quantity);
            await RequireStockAsync(uow, item.PartNumber, location.Code, lot, StockStatus.Available, quantity, ct);
            var txn = new NewTransaction(InventoryTransactionKind.QualityHold, item.PartNumber, quantity, location.Code, StockStatus.Available, location.Code, StockStatus.Hold, lot, Reason: context.Reason);
            return (txn, $"{quantity} {item.UnitOfMeasure} {item.PartNumber} at {location.Code} placed on hold.",
                id => pipeline.Event(new InventoryMoved(id, nameof(InventoryTransactionKind.QualityHold), item.PartNumber, quantity, location.Code, location.Code), context, SourceContext, StockEntity, item.PartNumber));
        }, ct);
    }

    public Task<CommandResult> ReleaseAsync(ReleaseStock c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return PostAsync(nameof(ReleaseStock), c.PartNumber, c, context, Permissions.InventoryApprove, true, async (uow, item, _) =>
        {
            if (c.FromStatus == StockStatus.Available)
            {
                throw new CommandRejectedException("already_available", "Release stock from Hold or Inspection.");
            }

            var location = await LocationAsync(uow, c.LocationCode, ct);
            var lot = RequireLot(item, c.LotNumber);
            var quantity = RequireQuantity(item, c.Quantity);
            var store = uow.Store<IInventoryStore>();
            await RequireStockAsync(uow, item.PartNumber, location.Code, lot, c.FromStatus, quantity, ct);
            if (await store.LastPlacedIntoAsync(item.PartNumber, location.Code, lot, c.FromStatus, ct) == context.Actor)
            {
                throw new CommandRejectedException("separation_of_duties", $"You put this stock into {c.FromStatus}; a different person must release it.");
            }

            var txn = new NewTransaction(InventoryTransactionKind.QualityRelease, item.PartNumber, quantity, location.Code, c.FromStatus, location.Code, StockStatus.Available, lot, Reason: context.Reason);
            return (txn, $"{quantity} {item.UnitOfMeasure} {item.PartNumber} at {location.Code} released from {c.FromStatus} to Available.",
                id => pipeline.Event(new InventoryMoved(id, nameof(InventoryTransactionKind.QualityRelease), item.PartNumber, quantity, location.Code, location.Code), context, SourceContext, StockEntity, item.PartNumber));
        }, ct);
    }

    public Task<CommandResult> ReceiveFromProductionAsync(ReceiveFromProduction c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return pipeline.ExecuteAsync(
            new CommandSpec(SourceContext, nameof(ReceiveFromProduction), Permissions.InventoryTransact, ResourceRef.Of("ProductionOrder", c.OrderNumber.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            c,
            context,
            async (uow, now) =>
            {
                var order = await uow.Store<IExecutionLookup>().FindOrderAsync(c.OrderNumber, ct);
                if (order is null || !Visible(context, order))
                {
                    throw new CommandRejectedException("order_not_found", $"Order {c.OrderNumber} does not exist.");
                }

                var item = await uow.Store<IEngineeringLookup>().FindItemAsync(order.PartNumber, ct)
                    ?? throw new CommandRejectedException("item_not_found", $"Part {order.PartNumber} does not exist.");
                var store = uow.Store<IInventoryStore>();
                await store.LockPartAsync(item.PartNumber, ct);
                var good = order.Operations.Count == 0 ? 0 : order.Operations.OrderBy(o => o.Sequence).Last().GoodQuantity;
                var received = (await uow.Store<IInventoryLookup>().ListProductionReceiptsAsync(ct)).FirstOrDefault(r => r.OrderNumber == order.OrderNumber)?.Received ?? 0;
                var quantity = RequireQuantity(item, c.Quantity);
                if (received + quantity > good)
                {
                    throw new CommandRejectedException("over_receipt", $"Order {order.OrderNumber} has {good} good from its last operation; {received} already received, so at most {good - received} more.");
                }

                // Quality (spec §45): finished goods go into stock only after every inspection point has
                // passed, and never while a nonconformance on the order is undecided.
                var quality = await uow.Store<IQualityLookup>().GetOrderQualityAsync(order.OrderNumber, order.PartNumber, ct);
                var uninspected = quality.PlannedSequences.Where(s => !quality.PassedSequences.Contains(s)).ToList();
                if (uninspected.Count > 0)
                {
                    throw new CommandRejectedException("inspection_required", $"Order {order.OrderNumber} has not passed inspection after operation {string.Join(", ", uninspected)}.");
                }

                if (quality.OpenNcrs.Count > 0)
                {
                    throw new CommandRejectedException("ncr_open", $"Order {order.OrderNumber} has NCR {string.Join(", ", quality.OpenNcrs.Select(n => n.NcrId))} waiting for a disposition.");
                }

                var location = await ActiveLocationAsync(uow, c.LocationCode, ct);
                // A made, lot-tracked part takes the order number as its lot unless one is given.
                var lot = RequireLot(item, item.LotTracked ? Optional(c.LotNumber) ?? $"WO{order.OrderNumber}" : c.LotNumber);
                if (lot is not null)
                {
                    await store.EnsureLotAsync(item.PartNumber, lot, null, now, context.Actor, ct);
                }

                var txn = new NewTransaction(InventoryTransactionKind.ProductionReceipt, item.PartNumber, quantity, null, null, location.Code, StockStatus.Available, lot, OrderNumber: order.OrderNumber, Reason: context.Reason);
                var txnId = await store.InsertTransactionAsync(txn, now, context.Actor, ct);
                var id = txnId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return new CommandOutcome(
                    CommandResult.Ok(id, $"Received {quantity} {item.UnitOfMeasure} {item.PartNumber} from order {order.OrderNumber} into {location.Code}" + (lot is null ? "." : $", lot {lot}.")),
                    [pipeline.Event(new InventoryReceived(txnId, item.PartNumber, quantity, location.Code, nameof(StockStatus.Available), null, order.OrderNumber), context, SourceContext, StockEntity, item.PartNumber)],
                    new AuditChange("InventoryTransaction", id, null, CanonicalJson.Serialize(txn)));
            },
            ct);
    }

    public Task<CommandResult> RequestAdjustmentAsync(RequestAdjustment c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return RequestAsync(nameof(RequestAdjustment), AdjustmentKind.Adjustment, c.PartNumber, c.LocationCode, c.LotNumber, c.Status, c.Change, c, context, true, ct);
    }

    public Task<CommandResult> RecordCountAsync(RecordCount c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return RequestAsync(nameof(RecordCount), AdjustmentKind.Count, c.PartNumber, c.LocationCode, c.LotNumber, c.Status, c.Counted, c, context, false, ct);
    }

    public Task<CommandResult> ApproveAdjustmentAsync(ApproveAdjustment c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return DecideAsync(nameof(ApproveAdjustment), c.RequestId, c, context, async (uow, request, now) =>
        {
            var store = uow.Store<IInventoryStore>();
            await store.LockPartAsync(request.PartNumber, ct);
            var current = await store.BalanceAsync(request.PartNumber, request.LocationCode, request.LotNumber, request.Status, ct);
            if (request.Kind == AdjustmentKind.Count && current != request.SystemQuantity)
            {
                throw new CommandRejectedException("stale_count", $"Stock of {request.PartNumber} at {request.LocationCode} changed from {request.SystemQuantity} to {current} since the count. Count again.");
            }

            var change = request.Variance;
            if (current + change < 0)
            {
                throw new CommandRejectedException("negative_stock", $"Only {current} on hand; a change of {change} would go below zero.");
            }

            var kind = request.Kind == AdjustmentKind.Count ? InventoryTransactionKind.CycleCount : InventoryTransactionKind.Adjustment;
            var txn = change > 0
                ? new NewTransaction(kind, request.PartNumber, change, null, null, request.LocationCode, request.Status, request.LotNumber, Reason: request.Reason, RequestId: request.RequestId)
                : new NewTransaction(kind, request.PartNumber, -change, request.LocationCode, request.Status, null, null, request.LotNumber, Reason: request.Reason, RequestId: request.RequestId);

            // The requester is the actor of record; the approver is recorded beside them.
            var txnId = await store.InsertTransactionAsync(txn with { ApprovedBy = context.Actor }, now, request.RequestedBy, ct);
            await store.DecideRequestAsync(request.RequestId, AdjustmentState.Approved, context.Actor, now, txnId, ct);
            return (AdjustmentState.Approved, $"{request.Kind} {request.RequestId} approved: {request.PartNumber} at {request.LocationCode} {(change > 0 ? "+" : string.Empty)}{change}.",
                (EventEnvelope?)pipeline.Event(new InventoryAdjusted(txnId, request.PartNumber, change, request.LocationCode, request.RequestId), context, SourceContext, StockEntity, request.PartNumber));
        }, ct);
    }

    public Task<CommandResult> RejectAdjustmentAsync(RejectAdjustment c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return DecideAsync(nameof(RejectAdjustment), c.RequestId, c, context, async (uow, request, now) =>
        {
            await uow.Store<IInventoryStore>().DecideRequestAsync(request.RequestId, AdjustmentState.Rejected, context.Actor, now, null, ct);
            return (AdjustmentState.Rejected, $"{request.Kind} {request.RequestId} rejected; stock unchanged.", (EventEnvelope?)null);
        }, ct);
    }

    private Task<CommandResult> RequestAsync<TCommand>(
        string commandType,
        AdjustmentKind kind,
        string rawPart,
        string rawLocation,
        string? rawLot,
        StockStatus status,
        decimal quantity,
        TCommand command,
        CommandContext context,
        bool reasonRequired,
        CancellationToken ct)
    {
        var part = BusinessCode.Require(rawPart, "Part");
        var spec = new CommandSpec(SourceContext, commandType, Permissions.InventoryTransact, ResourceRef.Of(StockEntity, part), reasonRequired);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var item = await ItemAsync(uow, part, ct);
            var location = await LocationAsync(uow, rawLocation, ct);
            var lot = RequireLot(item, rawLot);
            if (kind == AdjustmentKind.Adjustment && quantity == 0)
            {
                throw new CommandRejectedException("invalid_quantity", "An adjustment changes the quantity; give a positive or negative change.");
            }

            if (kind == AdjustmentKind.Count && quantity < 0)
            {
                throw new CommandRejectedException("invalid_quantity", "A count cannot be negative.");
            }

            if (Math.Abs(quantity) > 100_000_000 || (item.Serialized && quantity != decimal.Truncate(quantity)))
            {
                throw new CommandRejectedException("invalid_quantity", $"{item.PartNumber}: invalid quantity {quantity}.");
            }

            var store = uow.Store<IInventoryStore>();
            await store.LockPartAsync(part, ct);
            var system = await store.BalanceAsync(part, location.Code, lot, status, ct);
            var matched = kind == AdjustmentKind.Count && quantity == system;
            var reason = Optional(context.Reason) ?? (kind == AdjustmentKind.Count ? "Cycle count" : string.Empty);
            var request = new AdjustmentRequest(0, kind, part, location.Code, lot, status, quantity, system, reason, context.Actor, now, matched ? AdjustmentState.Matched : AdjustmentState.Pending, null, null, null);
            var requestId = await store.InsertRequestAsync(request, ct);
            var id = requestId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var message = matched
                ? $"Count of {part} at {location.Code} agrees with the system ({system}). Nothing to approve."
                : kind == AdjustmentKind.Count
                    ? $"Count {id} recorded: {quantity} counted, {system} on record, variance {request.Variance}. A different person must approve the variance."
                    : $"Adjustment {id} requested: {part} at {location.Code} {(quantity > 0 ? "+" : string.Empty)}{quantity}. A different person must approve it.";
            return new CommandOutcome(
                CommandResult.Ok(id, message),
                [pipeline.Event(new AdjustmentRequested(requestId, kind.ToString(), part, location.Code, request.Variance), context, SourceContext, "AdjustmentRequest", id)],
                new AuditChange("AdjustmentRequest", id, null, CanonicalJson.Serialize(request with { RequestId = requestId })));
        }, ct);
    }

    private Task<CommandResult> DecideAsync<TCommand>(
        string commandType,
        long requestId,
        TCommand command,
        CommandContext context,
        Func<IKernelUnitOfWork, AdjustmentRequest, DateTimeOffset, Task<(AdjustmentState State, string Message, EventEnvelope? Extra)>> decide,
        CancellationToken ct)
    {
        var id = requestId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, commandType, Permissions.InventoryApprove, ResourceRef.Of("AdjustmentRequest", id), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var request = await uow.Store<IInventoryStore>().FindRequestAsync(requestId, ct)
                ?? throw new CommandRejectedException("request_not_found", $"Adjustment request {id} does not exist.");
            if (request.State != AdjustmentState.Pending)
            {
                throw new CommandRejectedException("not_pending", $"Request {id} is {request.State}.");
            }

            if (request.RequestedBy == context.Actor)
            {
                throw new CommandRejectedException("separation_of_duties", "You asked for this change; a different person must decide it.");
            }

            var (state, message, extra) = await decide(uow, request, now);
            var events = new List<EventEnvelope> { pipeline.Event(new AdjustmentDecided(requestId, state.ToString()), context, SourceContext, "AdjustmentRequest", id) };
            if (extra is not null)
            {
                events.Add(extra);
            }

            return new CommandOutcome(CommandResult.Ok(id, message), events, new AuditChange("AdjustmentRequest", id, nameof(AdjustmentState.Pending), state.ToString()));
        }, ct);
    }

    /// <summary>Common path for one ledger entry: authorize, lock the part, validate, insert, audit.</summary>
    private Task<CommandResult> PostAsync<TCommand>(
        string commandType,
        string rawPart,
        TCommand command,
        CommandContext context,
        string permission,
        bool reasonRequired,
        Func<IKernelUnitOfWork, ItemInfo, DateTimeOffset, Task<(NewTransaction Transaction, string Message, Func<long, EventEnvelope> Event)>> build,
        CancellationToken ct)
    {
        var part = BusinessCode.Require(rawPart, "Part");
        var spec = new CommandSpec(SourceContext, commandType, permission, ResourceRef.Of(StockEntity, part), reasonRequired);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var item = await ItemAsync(uow, part, ct);
            var store = uow.Store<IInventoryStore>();
            await store.LockPartAsync(part, ct);
            var (txn, message, makeEvent) = await build(uow, item, now);
            var txnId = await store.InsertTransactionAsync(txn, now, context.Actor, ct);
            var id = txnId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new CommandOutcome(CommandResult.Ok(id, message), [makeEvent(txnId)], new AuditChange("InventoryTransaction", id, null, CanonicalJson.Serialize(txn)));
        }, ct);
    }

    private static async Task<ItemInfo> ItemAsync(IKernelUnitOfWork uow, string part, CancellationToken ct) =>
        await uow.Store<IEngineeringLookup>().FindItemAsync(part, ct)
            ?? throw new CommandRejectedException("item_not_found", $"Part {part} does not exist.");

    private static async Task<LocationInfo> LocationAsync(IKernelUnitOfWork uow, string raw, CancellationToken ct)
    {
        var code = BusinessCode.Require(raw, "Location");
        return (await uow.Store<IInventoryLookup>().ListLocationsAsync(ct)).FirstOrDefault(l => l.Code == code)
            ?? throw new CommandRejectedException("unknown_location", $"Location {code} does not exist.");
    }

    private static async Task<LocationInfo> ActiveLocationAsync(IKernelUnitOfWork uow, string raw, CancellationToken ct)
    {
        var location = await LocationAsync(uow, raw, ct);
        return location.Active ? location : throw new CommandRejectedException("location_retired", $"Location {location.Code} is retired.");
    }

    private static async Task RequireStockAsync(IKernelUnitOfWork uow, string part, string location, string? lot, StockStatus status, decimal quantity, CancellationToken ct)
    {
        var balance = await uow.Store<IInventoryStore>().BalanceAsync(part, location, lot, status, ct);
        if (balance < quantity)
        {
            throw new CommandRejectedException("insufficient_stock", $"Only {balance} {part} ({status}{(lot is null ? string.Empty : ", lot " + lot)}) at {location}; {quantity} needed.");
        }
    }

    private static async Task<OrderInfo> ReleasedOrderAsync(IKernelUnitOfWork uow, long number, CommandContext context, CancellationToken ct)
    {
        var order = await uow.Store<IExecutionLookup>().FindOrderAsync(number, ct);
        if (order is null || !Visible(context, order))
        {
            throw new CommandRejectedException("order_not_found", $"Order {number} does not exist.");
        }

        return order.Status == OrderStatus.Released
            ? order
            : throw new CommandRejectedException("order_not_released", $"Order {number} is {order.Status}; material is issued only to released orders.");
    }

    private static bool Visible(CommandContext context, OrderInfo order) =>
        order.ProgramId is not { } program || context.Principal.Programs.Contains(new ProgramId(program));

    private static string? RequireLot(ItemInfo item, string? raw)
    {
        var lot = Optional(raw)?.ToUpperInvariant();
        if (item.LotTracked && lot is null)
        {
            throw new CommandRejectedException("lot_required", $"{item.PartNumber} is lot-tracked; give the lot number.");
        }

        if (!item.LotTracked && lot is not null)
        {
            throw new CommandRejectedException("not_lot_tracked", $"{item.PartNumber} is not lot-tracked; leave the lot empty.");
        }

        if (lot is not null && (lot.Length > 40 || lot.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or '/'))))
        {
            throw new CommandRejectedException("invalid_lot", $"Lot '{raw}' is invalid. Use up to 40 letters, digits, '-', '_', '.' or '/'.");
        }

        return lot;
    }

    private static decimal RequireQuantity(ItemInfo item, decimal quantity)
    {
        if (quantity <= 0 || quantity > 100_000_000)
        {
            throw new CommandRejectedException("invalid_quantity", "Quantity must be above zero.");
        }

        if (item.Serialized && quantity != decimal.Truncate(quantity))
        {
            throw new CommandRejectedException("invalid_quantity", $"{item.PartNumber} is serialized; quantities are whole units.");
        }

        return quantity;
    }

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
