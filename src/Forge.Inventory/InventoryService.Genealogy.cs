using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Serialization;
using Forge.Procurement.Contracts;

namespace Forge.Inventory;

/// <summary>Finished units of a serialized part received from production, one serial number each (V2.11).</summary>
public sealed record SerialsAssigned(long OrderNumber, string PartNumber, string Revision, IReadOnlyList<string> Serials, IReadOnlyList<long> TransactionIds) : Kernel.Events.IDomainEvent
{
    public static string EventType => nameof(SerialsAssigned);

    public static int SchemaVersion => 1;
}

/// <summary>
/// Serial numbers and the as-built record (V2.11). A serialized part is moved one serial at a time; its serial
/// numbers are assigned at production receipt (or recorded at purchase receipt) and never reused. Every issue
/// to, or return from, a production order appends a line to that order's as-built record with what was true
/// at that moment — revision, lot or serial, the approved alternate and its approval, supplier and purchase
/// order — so genealogy never has to reconstruct it from today's data.
/// </summary>
public sealed partial class InventoryService
{
    private async Task<CommandOutcome> ReceiveSerialsAsync(IKernelUnitOfWork uow, IInventoryStore store, ItemInfo item, OrderInfo order, decimal quantity, string location, IReadOnlyList<string>? raw,
        CommandContext context, DateTimeOffset now, CancellationToken ct)
    {
        var serials = (raw ?? []).Select(s => RequireLot(item, s)!).ToList();
        if (serials.Count != quantity)
        {
            throw new CommandRejectedException("serials_required", $"{item.PartNumber} is serialized: give {quantity.ToString("0", CultureInfo.InvariantCulture)} serial number(s), one per unit ({serials.Count} given).");
        }

        if (serials.Distinct(StringComparer.Ordinal).Count() != serials.Count)
        {
            throw new CommandRejectedException("duplicate_serial", "Each unit gets its own serial number.");
        }

        var ids = new List<long>();
        var events = new List<Kernel.Events.EventEnvelope>();
        var governedRelease = await productRelease.OutputPolicyAsync(uow, context.Principal, item.PartNumber, order.Revision, location, order.OrderNumber, null, now, ct);
        var outputStatus = governedRelease is null ? StockStatus.Released : StockStatus.Inspection;
        foreach (var serial in serials)
        {
            await RequireNewSerialAsync(uow, item.PartNumber, serial, ct);
            await LotAsync(uow, item.PartNumber, serial, null, order.Revision, IdentityKind.Serial, now, context.Actor, ct);
            var txn = new NewTransaction(InventoryTransactionKind.ProductionReceipt, item.PartNumber, 1, null, null, location, outputStatus, serial, OrderNumber: order.OrderNumber,
                Reason: context.Reason, PartRevision: order.Revision);
            var id = await store.InsertTransactionAsync(txn, now, context.Actor, ct);
            ids.Add(id);
            events.Add(pipeline.Event(new InventoryReceived(id, item.PartNumber, 1, location, outputStatus.ToString(), null, order.OrderNumber), context, SourceContext, StockEntity, item.PartNumber));
        }

        var key = ids[0].ToString(CultureInfo.InvariantCulture);
        events.Add(pipeline.Event(new SerialsAssigned(order.OrderNumber, item.PartNumber, order.Revision, serials, ids), context, SourceContext, StockEntity, item.PartNumber));
        return new CommandOutcome(
            CommandResult.Ok(key, $"Received {serials.Count} {item.PartNumber} rev {order.Revision} from order {order.OrderNumber} into {location}: serial {string.Join(", ", serials)}."),
            events,
            new AuditChange("InventoryTransaction", key, null, CanonicalJson.Serialize(new { order = order.OrderNumber, part = item.PartNumber, revision = order.Revision, serials, transactions = ids })));
    }

    /// <summary>A serial number is given once per part: never received again, whatever happened to the unit since.</summary>
    private static async Task RequireNewSerialAsync(IKernelUnitOfWork uow, string part, string serial, CancellationToken ct)
    {
        if ((await uow.Store<IInventoryLookup>().ListLotsAsync(part, ct)).Any(l => l.LotNumber == serial))
        {
            throw new CommandRejectedException("serial_exists", $"{part} serial {serial} already exists; serial numbers are never reused.");
        }
    }

    private static async Task AppendAsBuiltAsync(IKernelUnitOfWork uow, OrderInfo order, ItemInfo item, NewTransaction txn, long transactionId, AsBuiltKind kind, AlternateInfo? alternate,
        string? substitutesFor, decimal? primaryQuantity, string actor, DateTimeOffset now, CancellationToken ct)
    {
        var inventory = uow.Store<IInventoryLookup>();
        string? revision = null;
        InventoryTransaction? receipt = null;
        if (txn.LotNumber is { } lot)
        {
            revision = (await inventory.ListLotsAsync(item.PartNumber, ct)).FirstOrDefault(l => l.LotNumber == lot)?.PartRevision;
            receipt = (await inventory.ListTransactionsAsync(item.PartNumber, null, 1000, ct))
                .Where(t => t.LotNumber == lot && t.Kind == InventoryTransactionKind.Receipt && t.PoNumber is not null)
                .MinBy(t => t.TransactionId);
        }

        var po = receipt?.PoNumber is { } number ? await uow.Store<IProcurementLookup>().FindPurchaseOrderAsync(number, ct) : null;
        await uow.Store<IAsBuiltRecord>().AppendAsync(new AsBuiltLine(
            0, order.OrderNumber, transactionId, kind, item.PartNumber, revision, txn.LotNumber, item.Serialized, txn.Quantity, substitutesFor, primaryQuantity,
            alternate?.AlternateId, alternate is null ? null : Alternates.Label(alternate.Kind), alternate?.DecidedBy, alternate?.DecidedAt, alternate?.Conditions,
            po?.SupplierCode, po?.Supplier, po?.PoNumber, receipt?.PoLine, receipt?.At, now, actor), ct);
    }
}
