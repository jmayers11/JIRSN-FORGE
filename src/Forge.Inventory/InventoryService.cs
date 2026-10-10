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

/// <param name="Nettable">Whether released stock there counts for planning; null takes the kind's default (V3.1).</param>
public sealed record CreateLocation(
    string Code,
    string SiteCode,
    string Name,
    LocationKind Kind,
    string? ParentCode = null,
    LocationLevel? Level = null,
    bool? Nettable = null,
    string? WorkCenterCode = null,
    string? MachineCode = null,
    string? CustodianSupplier = null);

public sealed record RetireLocation(string Code);

/// <summary>
/// Material arriving. Against a purchase order line, or (with a reason) without one, for example
/// opening balances or customer-supplied material. <paramref name="Inspect"/> puts it into Inspection.
/// </summary>
/// <param name="Revision">The part revision received, when the item has released revisions (V2.3). Optional; stock without one is reported as revision not recorded.</param>
public sealed record ReceiveStock(string PartNumber, decimal Quantity, string LocationCode, string? LotNumber, string? SupplierLot, long? PoNumber, int? PoLine, bool Inspect, string? Revision = null);

/// <param name="AlternateFor">The part the order needs, when <paramref name="PartNumber"/> is an approved alternate issued in its place (V2.6).</param>
public sealed record IssueToOrder(long OrderNumber, string PartNumber, decimal Quantity, string LocationCode, string? LotNumber, string? AlternateFor = null);

public sealed record ReturnFromOrder(long OrderNumber, string PartNumber, decimal Quantity, string LocationCode, string? LotNumber, string? AlternateFor = null);

public sealed record TransferStock(string PartNumber, decimal Quantity, string FromLocation, string ToLocation, string? LotNumber, StockStatus Status);

public sealed record ScrapStock(string PartNumber, decimal Quantity, string LocationCode, string? LotNumber, StockStatus Status);

public sealed record HoldStock(string PartNumber, decimal Quantity, string LocationCode, string? LotNumber);

/// <summary>Releases stock from quality hold or incoming inspection to Released. Never by the person who put it there.</summary>
public sealed record ReleaseStock(string PartNumber, decimal Quantity, string LocationCode, string? LotNumber, StockStatus FromStatus);

/// <summary>Finished goods from a production order, up to the good quantity reported on its last operation.</summary>
/// <param name="Serials">For a serialized part (V2.11): one new serial number per unit, assigned here.</param>
public sealed record ReceiveFromProduction(long OrderNumber, decimal Quantity, string LocationCode, string? LotNumber, IReadOnlyList<string>? Serials = null);

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

/// <summary>An approved alternate was issued in place of the part an order (V2.6) or a maintenance work order (V2.9) needs.</summary>
public sealed record AlternateUsed(long TransactionId, long AlternateId, string PrimaryPart, string AlternatePart, decimal Quantity, decimal PrimaryQuantity, long? OrderNumber, long? WorkOrderId = null, string? PartRevision = null) : IDomainEvent
{
    public static string EventType => nameof(AlternateUsed);

    public static int SchemaVersion => 2;
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
    long? ShipmentId = null,
    long? WorkOrderId = null,
    string? PartRevision = null,
    string? SubstitutesFor = null,
    long? AlternateId = null,
    decimal? PrimaryQuantity = null,
    string? ProjectCode = null,
    int? ProjectLine = null);

public interface IInventoryStore
{
    Task<long> InsertShipmentAsync(ShipmentInfo shipment, CancellationToken ct);

    Task UpdateShipmentAsync(ShipmentInfo shipment, CancellationToken ct);

    /// <summary>Serializes stock changes of one part for the rest of the transaction.</summary>
    Task LockPartAsync(string partNumber, CancellationToken ct);

    Task<decimal> BalanceAsync(string partNumber, string locationCode, string? lotNumber, StockStatus status, CancellationToken ct);

    Task<long> InsertTransactionAsync(NewTransaction transaction, DateTimeOffset at, string actor, CancellationToken ct);

    Task EnsureLotAsync(string partNumber, string lotNumber, string? supplierLot, DateTimeOffset at, string actor, CancellationToken ct);

    Task SetLotRevisionAsync(string partNumber, string lotNumber, string revision, CancellationToken ct);

    Task<bool> InsertLocationAsync(LocationInfo location, DateTimeOffset at, string actor, CancellationToken ct);

    Task SetLocationActiveAsync(string code, bool active, CancellationToken ct);

    Task UpdateLocationAsync(LocationInfo location, DateTimeOffset at, string actor, CancellationToken ct);

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

            if ((await lookup.ListLocationsAsync(ct)).FirstOrDefault(l => l.Active && l.ParentCode == code) is { } child)
            {
                throw new CommandRejectedException("location_has_children", $"{child.Code} sits in {code}; retire or move it first.");
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

    /// <summary>The stated part revision, checked against engineering: it must be a released revision of the part.</summary>
    private static async Task<string?> ReceivedRevisionAsync(IKernelUnitOfWork uow, string partNumber, string? raw, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var revision = raw.Trim().ToUpperInvariant();
        var known = (await uow.Store<IEngineeringLookup>().ListRevisionsAsync(partNumber, ct)).FirstOrDefault(r => r.Revision == revision)
            ?? throw new CommandRejectedException("revision_not_found", $"{partNumber} has no revision {revision}.");
        return known.Status == RevisionStatus.Released
            ? revision
            : throw new CommandRejectedException("revision_not_released", $"{partNumber} rev {revision} is a draft; stock is only received at a released revision.");
    }

    /// <summary>
    /// Creates the lot with its revision, or checks an existing lot: one lot holds one revision, and a lot
    /// received before revisions were recorded is never given one after the fact.
    /// </summary>
    private static async Task LotAsync(IKernelUnitOfWork uow, string partNumber, string lot, string? supplierLot, string? revision, DateTimeOffset now, string actor, CancellationToken ct)
    {
        var existing = (await uow.Store<IInventoryLookup>().ListLotsAsync(partNumber, ct)).FirstOrDefault(l => l.LotNumber == lot);
        var store = uow.Store<IInventoryStore>();
        if (existing is null)
        {
            await store.EnsureLotAsync(partNumber, lot, supplierLot, now, actor, ct);
            if (revision is not null)
            {
                await store.SetLotRevisionAsync(partNumber, lot, revision, ct);
            }

            return;
        }

        if (revision is null || existing.PartRevision == revision)
        {
            return;
        }

        throw existing.PartRevision is null
            ? new CommandRejectedException("lot_revision_not_recorded", $"Lot {lot} was received without a revision; receive rev {revision} into a new lot.")
            : new CommandRejectedException("lot_revision_conflict", $"Lot {lot} holds rev {existing.PartRevision}; rev {revision} goes into a different lot.");
    }

    public Task<CommandResult> ReceiveAsync(ReceiveStock c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var reasonRequired = c.PoNumber is null;
        return PostAsync(nameof(ReceiveStock), c.PartNumber, c, context, Permissions.InventoryTransact, reasonRequired, async (uow, item, now) =>
        {
            var location = await CompanyLocationAsync(uow, c.LocationCode, ct);
            var lot = RequireLot(item, c.LotNumber);
            var quantity = RequireQuantity(item, c.Quantity);
            int? line = null;
            (string Code, int Line)? project = null;
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

                // Bought for a project line (V2.10): the receipt records the allocation to that line.
                if (Forge.Projects.Contracts.ProjectSource.TryParse(poLine.SourceKey, out var projectCode, out var projectLine)
                    && (await uow.Store<Forge.Projects.Contracts.IProjectLookup>().ListLinesAsync(projectCode, ct)).Any(l => l.LineNumber == projectLine))
                {
                    project = (projectCode, projectLine);
                }
            }

            var revision = await ReceivedRevisionAsync(uow, item.PartNumber, c.Revision, ct);
            if (item.Serialized && lot is not null)
            {
                await RequireNewSerialAsync(uow, item.PartNumber, lot, ct);
            }

            if (lot is not null)
            {
                await LotAsync(uow, item.PartNumber, lot, Optional(c.SupplierLot), revision, now, context.Actor, ct);
            }

            var status = c.Inspect ? StockStatus.Inspection : StockStatus.Released;
            var txn = new NewTransaction(InventoryTransactionKind.Receipt, item.PartNumber, quantity, null, null, location.Code, status, lot, PoNumber: c.PoNumber, PoLine: line, Reason: context.Reason, PartRevision: revision,
                ProjectCode: project?.Code, ProjectLine: project?.Line);
            return (txn, $"Received {quantity} {item.UnitOfMeasure} {item.PartNumber}{(revision is null ? string.Empty : " rev " + revision)} into {location.Code} ({status})" + (c.PoNumber is null ? "." : $" against PO {c.PoNumber} line {line}" + (project is { } alloc ? $", for project {alloc.Code} line {alloc.Line}." : ".")),
                id => pipeline.Event(new InventoryReceived(id, item.PartNumber, quantity, location.Code, status.ToString(), c.PoNumber, null), context, SourceContext, StockEntity, item.PartNumber));
        }, ct);
    }

    public Task<CommandResult> IssueToOrderAsync(IssueToOrder c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        OrderInfo? built = null;
        AlternateInfo? through = null;
        string? needs = null;
        decimal issuedPrimary = 0;
        return PostAsync(nameof(IssueToOrder), c.PartNumber, c, context, Permissions.InventoryTransact, false, async (uow, item, now) =>
        {
            var order = await ReleasedOrderAsync(uow, c.OrderNumber, context, ct);
            built = order;
            var (needed, alternate, factor) = await NeededPartAsync(uow, order, item.PartNumber, c.AlternateFor, now, ct);
            var required = order.Materials.Where(m => m.ComponentPartNumber == needed).Sum(m => m.RequiredQuantity);
            if (required == 0)
            {
                throw new CommandRejectedException("not_on_order", $"Order {order.OrderNumber} does not use {needed}.");
            }

            var quantity = RequireQuantity(item, c.Quantity);
            var primaryQuantity = alternate is null ? quantity : decimal.Round(quantity / factor, 6, MidpointRounding.AwayFromZero);
            var issued = (await uow.Store<IInventoryLookup>().ListOrderIssuesAsync(ct)).FirstOrDefault(i => i.OrderNumber == order.OrderNumber && i.PartNumber == needed)?.NetIssued ?? 0;
            if (issued + primaryQuantity > required)
            {
                throw new CommandRejectedException("over_issue", $"Order {order.OrderNumber} needs {required} {needed}; {issued} already issued, so at most {required - issued} more{(alternate is null ? string.Empty : $" ({(required - issued) * factor} {item.PartNumber} at {factor} per 1)")}. Over-issue is not allowed; scrap or adjust instead.");
            }

            var location = await CompanyLocationAsync(uow, c.LocationCode, ct);
            var lot = RequireLot(item, c.LotNumber);
            await RequireStockAsync(uow, item.PartNumber, location.Code, lot, StockStatus.Released, quantity, ct);
            var txn = new NewTransaction(InventoryTransactionKind.Issue, item.PartNumber, quantity, location.Code, StockStatus.Released, null, null, lot, OrderNumber: order.OrderNumber, Reason: context.Reason,
                SubstitutesFor: alternate is null ? null : needed, AlternateId: alternate?.AlternateId, PrimaryQuantity: alternate is null ? null : primaryQuantity);
            (through, needs, issuedPrimary) = (alternate, needed, primaryQuantity);
            var message = alternate is null
                ? $"Issued {quantity} {item.UnitOfMeasure} {item.PartNumber} from {location.Code} to order {order.OrderNumber}."
                : $"Issued {quantity} {item.UnitOfMeasure} {item.PartNumber} from {location.Code} to order {order.OrderNumber} in place of {primaryQuantity} {needed} (approved alternate {alternate.AlternateId}).";
            return (txn, message, id => alternate is null
                ? pipeline.Event(new InventoryIssued(id, item.PartNumber, quantity, location.Code, order.OrderNumber), context, SourceContext, StockEntity, item.PartNumber)
                : pipeline.Event(new AlternateUsed(id, alternate.AlternateId, needed, item.PartNumber, quantity, primaryQuantity, order.OrderNumber), context, SourceContext, StockEntity, item.PartNumber));
        },
        (uow, item, txn, id, now) => AppendAsBuiltAsync(uow, built!, item, txn, id, AsBuiltKind.Issued, through, through is null ? null : needs, through is null ? null : issuedPrimary, context.Actor, now, ct),
        ct);
    }

    public Task<CommandResult> ReturnFromOrderAsync(ReturnFromOrder c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        OrderInfo? built = null;
        return PostAsync(nameof(ReturnFromOrder), c.PartNumber, c, context, Permissions.InventoryTransact, true, async (uow, item, _) =>
        {
            var order = await uow.Store<IExecutionLookup>().FindOrderAsync(c.OrderNumber, ct);
            if (order is null || !Visible(context, order))
            {
                throw new CommandRejectedException("order_not_found", $"Order {c.OrderNumber} does not exist.");
            }

            built = order;

            var quantity = RequireQuantity(item, c.Quantity);
            var transactions = (await uow.Store<IInventoryLookup>().ListTransactionsAsync(item.PartNumber, order.OrderNumber, 1000, ct))
                .Where(t => t.Kind is InventoryTransactionKind.Issue or InventoryTransactionKind.Return).ToList();
            var primary = string.IsNullOrWhiteSpace(c.AlternateFor) ? null : c.AlternateFor.Trim().ToUpperInvariant();
            var mine = transactions.Where(t => t.SubstitutesFor == primary).ToList();
            var issued = mine.Sum(t => t.Kind == InventoryTransactionKind.Issue ? t.Quantity : -t.Quantity);
            if (quantity > issued)
            {
                throw new CommandRejectedException("over_return", $"Only {issued} {item.PartNumber} is issued to order {order.OrderNumber}{(primary is null ? string.Empty : $" in place of {primary}")}.");
            }

            var location = await CompanyLocationAsync(uow, c.LocationCode, ct);
            var lot = RequireLot(item, c.LotNumber);
            var issuedPrimary = mine.Sum(t => (t.Kind == InventoryTransactionKind.Issue ? 1 : -1) * (t.PrimaryQuantity ?? t.Quantity));
            var primaryQuantity = primary is null ? (decimal?)null : decimal.Round(issuedPrimary * quantity / issued, 6, MidpointRounding.AwayFromZero);
            var alternateId = primary is null ? null : mine.LastOrDefault(t => t.AlternateId is not null)?.AlternateId;
            var txn = new NewTransaction(InventoryTransactionKind.Return, item.PartNumber, quantity, null, null, location.Code, StockStatus.Released, lot, OrderNumber: order.OrderNumber, Reason: context.Reason,
                SubstitutesFor: primary, AlternateId: alternateId, PrimaryQuantity: primaryQuantity);
            return (txn, $"Returned {quantity} {item.UnitOfMeasure} {item.PartNumber} from order {order.OrderNumber} to {location.Code}.",
                id => pipeline.Event(new InventoryMoved(id, nameof(InventoryTransactionKind.Return), item.PartNumber, quantity, null, location.Code), context, SourceContext, StockEntity, item.PartNumber));
        },
        async (uow, item, txn, id, now) =>
        {
            var alternate = txn.AlternateId is { } a ? (await uow.Store<IAlternateLookup>().ListAlternatesAsync(ct)).FirstOrDefault(x => x.AlternateId == a) : null;
            await AppendAsBuiltAsync(uow, built!, item, txn, id, AsBuiltKind.Returned, alternate, txn.SubstitutesFor, txn.PrimaryQuantity, context.Actor, now, ct);
        },
        ct);
    }

    /// <summary>
    /// The part an issue counts against: the part itself, or — for an alternate issued in place of a part the
    /// order needs — that part, with the approved relationship that allows it here and its conversion. Proposed,
    /// rejected, expired or conditional relationships whose conditions do not hold here are never used.
    /// </summary>
    private static async Task<(string Needed, AlternateInfo? Alternate, decimal Factor)> NeededPartAsync(Kernel.Access.IKernelUnitOfWork uow, OrderInfo order, string part, string? alternateFor, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(alternateFor))
        {
            return (part, null, 1m);
        }

        var primary = alternateFor.Trim().ToUpperInvariant();
        var context = new AlternateContext(DateOnly.FromDateTime(now.UtcDateTime), order.PartNumber, order.ProgramId);
        var options = Alternates.For(await uow.Store<IAlternateLookup>().ListAlternatesAsync(ct), primary).Where(x => x.OfferedPart == part).ToList();
        foreach (var (relationship, _, factor) in options)
        {
            if (Alternates.Evaluate(relationship, context).Usability == AlternateUsability.Usable)
            {
                return (primary, relationship, factor);
            }
        }

        var why = options.Count == 0
            ? $"{part} is not an alternate for {primary}."
            : string.Join(" ", options.SelectMany(o => Alternates.Evaluate(o.Relationship, context).Reasons).Distinct());
        throw new CommandRejectedException("alternate_not_approved", $"{part} cannot be issued in place of {primary} on order {order.OrderNumber}: {why}");
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
            var location = InCustody(await LocationAsync(uow, c.LocationCode, ct));
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
            await RequireStockAsync(uow, item.PartNumber, location.Code, lot, StockStatus.Released, quantity, ct);
            var txn = new NewTransaction(InventoryTransactionKind.QualityHold, item.PartNumber, quantity, location.Code, StockStatus.Released, location.Code, StockStatus.Hold, lot, Reason: context.Reason);
            return (txn, $"{quantity} {item.UnitOfMeasure} {item.PartNumber} at {location.Code} placed on hold.",
                id => pipeline.Event(new InventoryMoved(id, nameof(InventoryTransactionKind.QualityHold), item.PartNumber, quantity, location.Code, location.Code), context, SourceContext, StockEntity, item.PartNumber));
        }, ct);
    }

    public Task<CommandResult> ReleaseAsync(ReleaseStock c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return PostAsync(nameof(ReleaseStock), c.PartNumber, c, context, Permissions.InventoryApprove, true, async (uow, item, _) =>
        {
            if (c.FromStatus == StockStatus.Released)
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

            var txn = new NewTransaction(InventoryTransactionKind.QualityRelease, item.PartNumber, quantity, location.Code, c.FromStatus, location.Code, StockStatus.Released, lot, Reason: context.Reason);
            return (txn, $"{quantity} {item.UnitOfMeasure} {item.PartNumber} at {location.Code} released from {c.FromStatus} to Released.",
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
                var quantity = item.Serialized ? c.Quantity : RequireQuantity(item, c.Quantity);
                if (item.Serialized && (quantity <= 0 || quantity != decimal.Truncate(quantity)))
                {
                    throw new CommandRejectedException("invalid_quantity", $"{item.PartNumber} is serialized; receive whole units, one serial number each.");
                }

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

                var location = await CompanyLocationAsync(uow, c.LocationCode, ct);
                if (item.Serialized)
                {
                    return await ReceiveSerialsAsync(uow, store, item, order, quantity, location.Code, c.Serials, context, now, ct);
                }

                if (c.Serials is { Count: > 0 })
                {
                    throw new CommandRejectedException("not_serialized", $"{item.PartNumber} is not serialized; leave the serial numbers empty.");
                }

                // A made, lot-tracked part takes the order number as its lot unless one is given.
                var lot = RequireLot(item, item.LotTracked ? Optional(c.LotNumber) ?? $"WO{order.OrderNumber}" : c.LotNumber);
                // The order's revision is what was built (V2.3).
                if (lot is not null)
                {
                    await LotAsync(uow, item.PartNumber, lot, null, order.Revision, now, context.Actor, ct);
                }

                var txn = new NewTransaction(InventoryTransactionKind.ProductionReceipt, item.PartNumber, quantity, null, null, location.Code, StockStatus.Released, lot, OrderNumber: order.OrderNumber, Reason: context.Reason, PartRevision: order.Revision);
                var txnId = await store.InsertTransactionAsync(txn, now, context.Actor, ct);
                var id = txnId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return new CommandOutcome(
                    CommandResult.Ok(id, $"Received {quantity} {item.UnitOfMeasure} {item.PartNumber} rev {order.Revision} from order {order.OrderNumber} into {location.Code}" + (lot is null ? "." : $", lot {lot}.")),
                    [pipeline.Event(new InventoryReceived(txnId, item.PartNumber, quantity, location.Code, nameof(StockStatus.Released), null, order.OrderNumber), context, SourceContext, StockEntity, item.PartNumber)],
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
            var location = InCustody(await LocationAsync(uow, rawLocation, ct));
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
        CancellationToken ct) =>
        PostAsync(commandType, rawPart, command, context, permission, reasonRequired, build, null, ct);

    /// <param name="recorded">Runs in the same transaction once the ledger entry has its id (the as-built record, V2.11).</param>
    private Task<CommandResult> PostAsync<TCommand>(
        string commandType,
        string rawPart,
        TCommand command,
        CommandContext context,
        string permission,
        bool reasonRequired,
        Func<IKernelUnitOfWork, ItemInfo, DateTimeOffset, Task<(NewTransaction Transaction, string Message, Func<long, EventEnvelope> Event)>> build,
        Func<IKernelUnitOfWork, ItemInfo, NewTransaction, long, DateTimeOffset, Task>? recorded,
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
            if (recorded is not null)
            {
                await recorded(uow, item, txn, txnId, now);
            }

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

    /// <summary>
    /// An active location where the company has custody (V3.1). Stock at an outside processor or in transit is
    /// still the company's, but nobody here can issue, ship, receive, count or scrap it; it comes back by transfer.
    /// </summary>
    private static async Task<LocationInfo> CompanyLocationAsync(IKernelUnitOfWork uow, string raw, CancellationToken ct) =>
        InCustody(await ActiveLocationAsync(uow, raw, ct));

    private static LocationInfo InCustody(LocationInfo location) => location.InCompanyCustody
        ? location
        : throw new CommandRejectedException("not_in_custody", location.Custody == Custody.Supplier
            ? $"Stock at {location.Code} is in the custody of {location.CustodianSupplier}; transfer it back before using it."
            : $"Stock at {location.Code} is in transit; transfer it to a location here first.");

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
        if (item.Serialized && lot is null)
        {
            throw new CommandRejectedException("serial_required", $"{item.PartNumber} is serialized; give the serial number.");
        }

        if (item.LotTracked && lot is null)
        {
            throw new CommandRejectedException("lot_required", $"{item.PartNumber} is lot-tracked; give the lot number.");
        }

        if (!item.LotTracked && !item.Serialized && lot is not null)
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

        if (item.Serialized && quantity != 1)
        {
            throw new CommandRejectedException("serial_quantity", $"{item.PartNumber} is serialized; each serial number is one unit, moved one at a time.");
        }

        return quantity;
    }

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
