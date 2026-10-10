using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;

namespace Forge.Inventory;

/// <param name="LotNumber">The issued lot (consumed, scrap, loss, remaining) or the new instance (remnant, byproduct).</param>
/// <param name="SourceLot">For a remnant: the issued lot it was cut from.</param>
/// <param name="LocationCode">For a remnant or byproduct: where it goes into stock.</param>
public sealed record TransformationEntry(TransformationRole Role, string PartNumber, string? LotNumber, decimal Quantity, string? SourceLot = null, string? LocationCode = null);

/// <summary>What became of material issued to a production order (V3.3).</summary>
public sealed record RecordTransformation(long OrderNumber, int? OperationSequence, IReadOnlyList<TransformationEntry> Entries);

public sealed record SetYieldTolerance(string PartNumber, decimal Percent);

public sealed record TransformationRecorded(long TransformationId, long OrderNumber, int Unexplained) : IDomainEvent
{
    public static string EventType => nameof(TransformationRecorded);

    public static int SchemaVersion => 1;
}

public sealed record YieldToleranceSet(string PartNumber, decimal Percent) : IDomainEvent
{
    public static string EventType => nameof(YieldToleranceSet);

    public static int SchemaVersion => 1;
}

/// <summary>
/// Transformation (V3.3, V3 brief C, E, F, J). Material issued to a production order is accounted for: consumed
/// into the output, scrapped, lost, cut back into stock as a remnant (a new instance of the same part, traced to
/// the lot it came from), or still at the work center. Byproducts are new instances of other parts the order
/// made. Issued = consumed + returned + scrap + loss + remnant + remaining, within the part's yield tolerance;
/// anything else is unexplained and stays flagged until a later record explains it. Nothing is scrapped to
/// make the numbers add up, and no record is ever changed.
/// </summary>
public sealed partial class InventoryService
{
    private const int MaxEntries = 100;

    public Task<CommandResult> RecordTransformationAsync(RecordTransformation c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var key = c.OrderNumber.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, nameof(RecordTransformation), Permissions.InventoryTransact, ResourceRef.Of("ProductionOrder", key));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var order = await uow.Store<IExecutionLookup>().FindOrderAsync(c.OrderNumber, ct);
            if (order is null || !Visible(context, order))
            {
                throw new CommandRejectedException("order_not_found", $"Order {c.OrderNumber} does not exist.");
            }

            if (order.Status is not (OrderStatus.Released or OrderStatus.Held or OrderStatus.Completed))
            {
                throw new CommandRejectedException("order_not_started", $"Order {order.OrderNumber} is {order.Status}; material is accounted for once the order is released.");
            }

            if (c.OperationSequence is { } sequence && order.Operations.All(o => o.Sequence != sequence))
            {
                throw new CommandRejectedException("unknown_operation", $"Order {order.OrderNumber} has no operation {sequence}.");
            }

            var entries = c.Entries ?? [];
            if (entries.Count is 0 or > MaxEntries)
            {
                throw new CommandRejectedException("invalid_entries", $"Record 1 to {MaxEntries} lines.");
            }

            if (entries.Any(e => e.Role is TransformationRole.Scrap or TransformationRole.Loss) && string.IsNullOrWhiteSpace(context.Reason))
            {
                throw new CommandRejectedException("reason_required", "Say why material was scrapped or lost.");
            }

            var lookup = uow.Store<IInventoryLookup>();
            var store = uow.Store<IInventoryStore>();
            var orderTransactions = await lookup.ListTransactionsAsync(null, order.OrderNumber, 100_000, ct);
            var tolerances = (await lookup.ListYieldTolerancesAsync(ct)).ToDictionary(t => t.PartNumber, t => t.Percent, StringComparer.Ordinal);
            decimal Tolerance(string part) => tolerances.GetValueOrDefault(part);
            var before = (await lookup.ListTransformationsAsync(order.OrderNumber, null, null, ct)).ToList();
            var issued = MaterialReconciliation.For(order.OrderNumber, orderTransactions, before, Tolerance)
                .ToDictionary(r => (r.PartNumber, r.LotNumber));

            var lines = new List<TransformationLine>();
            var events = new List<EventEnvelope>();
            foreach (var entry in entries)
            {
                ArgumentNullException.ThrowIfNull(entry);
                var item = await ItemAsync(uow, BusinessCode.Require(entry.PartNumber, "Part"), ct);
                var number = lines.Count + 1;
                switch (entry.Role)
                {
                    case TransformationRole.Consumed or TransformationRole.Scrap or TransformationRole.Loss or TransformationRole.Remaining:
                    {
                        var lot = item.LotTracked || item.Serialized ? RequireLot(item, entry.LotNumber) : null;
                        if (!issued.TryGetValue((item.PartNumber, lot), out var row) || row.NetIssued <= 0)
                        {
                            throw new CommandRejectedException("not_issued", $"No {item.PartNumber}{(lot is null ? string.Empty : " lot " + lot)} is issued to order {order.OrderNumber}.");
                        }

                        var quantity = entry.Role == TransformationRole.Remaining && entry.Quantity == 0 ? 0 : RequireQuantity(item, entry.Quantity);
                        lines.Add(new TransformationLine(number, entry.Role, item.PartNumber, lot, quantity));
                        break;
                    }

                    case TransformationRole.Remnant:
                    {
                        RequireDivisible(item);
                        var source = RequireLot(item, entry.SourceLot ?? throw new CommandRejectedException("source_required", "A remnant names the issued lot it was cut from."));
                        if (!issued.TryGetValue((item.PartNumber, source), out var row) || row.NetIssued <= 0)
                        {
                            throw new CommandRejectedException("not_issued", $"No {item.PartNumber} lot {source} is issued to order {order.OrderNumber}.");
                        }

                        var sourceLot = (await lookup.ListLotsAsync(item.PartNumber, ct)).First(l => l.LotNumber == source);
                        var lot = await NewInstanceAsync(uow, item, entry.LotNumber, lines, ct);
                        var quantity = RequireQuantity(item, entry.Quantity);
                        var location = await CompanyLocationAsync(uow, entry.LocationCode ?? throw new CommandRejectedException("location_required", "Say where the remnant goes."), ct);
                        await store.LockPartAsync(item.PartNumber, ct);
                        await store.EnsureLotAsync(item.PartNumber, lot, sourceLot.SupplierLot, sourceLot.IdentityKind, now, context.Actor, ct);
                        if (sourceLot.PartRevision is { } revision)
                        {
                            await store.SetLotRevisionAsync(item.PartNumber, lot, revision, ct);
                        }

                        var id = await store.InsertTransactionAsync(
                            new NewTransaction(InventoryTransactionKind.Remnant, item.PartNumber, quantity, null, null, location.Code, StockStatus.Released, lot, OrderNumber: order.OrderNumber,
                                Reason: context.Reason, PartRevision: sourceLot.PartRevision), now, context.Actor, ct);
                        lines.Add(new TransformationLine(number, entry.Role, item.PartNumber, lot, quantity, item.PartNumber, source, location.Code, id));
                        events.Add(pipeline.Event(new InventoryReceived(id, item.PartNumber, quantity, location.Code, nameof(StockStatus.Released), null, order.OrderNumber), context, SourceContext, StockEntity, item.PartNumber));
                        break;
                    }

                    case TransformationRole.Byproduct:
                    {
                        if (item.PartNumber == order.PartNumber)
                        {
                            throw new CommandRejectedException("use_production_receipt", $"{item.PartNumber} is what order {order.OrderNumber} makes; receive it from production.");
                        }

                        var lot = item.LotTracked || item.Serialized ? await NewInstanceAsync(uow, item, entry.LotNumber, lines, ct) : RequireLot(item, entry.LotNumber);
                        var quantity = RequireQuantity(item, entry.Quantity);
                        var location = await CompanyLocationAsync(uow, entry.LocationCode ?? throw new CommandRejectedException("location_required", "Say where the byproduct goes."), ct);
                        await store.LockPartAsync(item.PartNumber, ct);
                        if (lot is not null)
                        {
                            await store.EnsureLotAsync(item.PartNumber, lot, null, item.Serialized ? IdentityKind.Serial : IdentityKind.Lot, now, context.Actor, ct);
                        }

                        var id = await store.InsertTransactionAsync(
                            new NewTransaction(InventoryTransactionKind.Byproduct, item.PartNumber, quantity, null, null, location.Code, StockStatus.Released, lot, OrderNumber: order.OrderNumber, Reason: context.Reason),
                            now, context.Actor, ct);
                        lines.Add(new TransformationLine(number, entry.Role, item.PartNumber, lot, quantity, null, null, location.Code, id));
                        events.Add(pipeline.Event(new InventoryReceived(id, item.PartNumber, quantity, location.Code, nameof(StockStatus.Released), null, order.OrderNumber), context, SourceContext, StockEntity, item.PartNumber));
                        break;
                    }

                    default:
                        throw new CommandRejectedException("invalid_role", $"Unknown role {entry.Role}.");
                }
            }

            if (lines.GroupBy(l => (l.Role, l.PartNumber, l.LotNumber)).Any(g => g.Key.Role == TransformationRole.Remaining && g.Count() > 1))
            {
                throw new CommandRejectedException("invalid_entries", "Give one remaining figure per issued lot.");
            }

            var record = new TransformationInfo(0, order.OrderNumber, c.OperationSequence, context.Reason, now, context.Actor, lines);
            var after = MaterialReconciliation.For(order.OrderNumber, orderTransactions, [.. before, record with { TransformationId = long.MaxValue }], Tolerance);

            // More accounted for than was issued (beyond tolerance) is refused: a remnant or consumption that never left stock.
            var over = after.FirstOrDefault(r => r.Unexplained < 0 && -r.Unexplained > r.NetIssued * r.TolerancePercent / 100m);
            if (over is not null)
            {
                throw new CommandRejectedException("over_accounted", $"{over.PartNumber}{(over.LotNumber is null ? string.Empty : " lot " + over.LotNumber)}: {Qty(over.NetIssued)} is issued to order {order.OrderNumber} but {Qty(over.Accounted)} would be accounted for.");
            }

            var transformationId = await store.InsertTransformationAsync(record, ct);
            var unexplained = after.Where(r => r.Status == ReconciliationStatus.Unexplained).ToList();
            events.Add(pipeline.Event(new TransformationRecorded(transformationId, order.OrderNumber, unexplained.Count), context, SourceContext, "ProductionOrder", key));
            var summary = string.Join("; ", after.Select(r => $"{r.PartNumber}{(r.LotNumber is null ? string.Empty : " " + r.LotNumber)} {Describe(r)}"));
            return new CommandOutcome(
                CommandResult.Ok(transformationId.ToString(CultureInfo.InvariantCulture), $"Recorded for order {order.OrderNumber}: {summary}."),
                events,
                new AuditChange("Transformation", transformationId.ToString(CultureInfo.InvariantCulture), null, CanonicalJson.Serialize(record with { TransformationId = transformationId })));
        }, ct);
    }

    public Task<CommandResult> SetYieldToleranceAsync(SetYieldTolerance c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var spec = new CommandSpec(SourceContext, nameof(SetYieldTolerance), Permissions.InventoryApprove, ResourceRef.Of(StockEntity, part), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            await ItemAsync(uow, part, ct);
            if (c.Percent is < 0 or > 50)
            {
                throw new CommandRejectedException("invalid_tolerance", "A yield tolerance is 0 to 50 percent of what was issued.");
            }

            var current = (await uow.Store<IInventoryLookup>().ListYieldTolerancesAsync(ct)).FirstOrDefault(t => t.PartNumber == part);
            if ((current?.Percent ?? 0) == c.Percent)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(part, $"The yield tolerance for {part} is already {Qty(c.Percent)}%."));
            }

            var tolerance = new YieldTolerance(part, c.Percent, context.Reason!, context.Actor, now);
            await uow.Store<IInventoryStore>().InsertYieldToleranceAsync(tolerance, ct);
            return new CommandOutcome(
                CommandResult.Ok(part, $"Issued and accounted {part} may now differ by {Qty(c.Percent)}% before it is unexplained."),
                [pipeline.Event(new YieldToleranceSet(part, c.Percent), context, SourceContext, StockEntity, part)],
                new AuditChange("YieldTolerance", part, current is null ? null : CanonicalJson.Serialize(current), CanonicalJson.Serialize(tolerance)));
        }, ct);
    }

    /// <summary>A new instance number for the part: given, valid, and never used before.</summary>
    private static async Task<string> NewInstanceAsync(IKernelUnitOfWork uow, ItemInfo item, string? raw, List<TransformationLine> earlier, CancellationToken ct)
    {
        var lot = RequireLot(item, raw) ?? throw new CommandRejectedException("lot_required", $"Give the new {item.PartNumber} instance a number.");
        if (earlier.Any(l => l.PartNumber == item.PartNumber && l.LotNumber == lot)
            || (await uow.Store<IInventoryLookup>().ListLotsAsync(item.PartNumber, ct)).Any(l => l.LotNumber == lot))
        {
            throw new CommandRejectedException("lot_exists", $"{item.PartNumber} {lot} already exists; a remnant or byproduct is always a new instance.");
        }

        return lot;
    }

    private static string Describe(MaterialReconciliation r) => r.Status switch
    {
        ReconciliationStatus.Balanced => "balanced",
        ReconciliationStatus.WithinTolerance => $"within tolerance ({Qty(r.Unexplained)} of {Qty(r.NetIssued)})",
        ReconciliationStatus.Unexplained => $"{Qty(r.Unexplained)} unexplained of {Qty(r.NetIssued)} issued",
        _ => "not recorded",
    };
}
