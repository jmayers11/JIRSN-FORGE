using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Engineering.Contracts;
using Forge.Procurement.Contracts;

namespace Forge.Inventory;

/// <summary>Outside processing (V3.14) changes custody by a ledger transfer; ownership and lot identity stay with the company.</summary>
public sealed partial class InventoryService
{
    public Task<CommandResult> DispatchOutsideProcessAsync(DispatchOutsideProcess c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var key = $"{part}:{c.LotNumber}:PO{c.PurchaseOrder}-L{c.PurchaseOrderLine}";
        var spec = new CommandSpec(SourceContext, nameof(DispatchOutsideProcess), Permissions.InventoryTransact, ResourceRef.Of("OutsideProcess", key), true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var item = await ItemAsync(uow, part, ct);
            var lot = RequireLot(item, c.LotNumber);
            var quantity = RequireQuantity(item, c.Quantity);
            var from = await CompanyLocationAsync(uow, c.FromLocation, ct);
            var processor = await ActiveLocationAsync(uow, c.ProcessorLocation, ct);
            if (processor.Kind != LocationKind.ExternalProcessor)
                throw new CommandRejectedException("processor_location_required", $"{processor.Code} is not an outside-processor location.");
            var po = await RequireProcessorPoAsync(uow, c.PurchaseOrder, c.PurchaseOrderLine, processor.CustodianSupplier!, ct);
            var store = uow.Store<IInventoryStore>();
            await store.LockPartAsync(item.PartNumber, ct);
            await RequireStockAsync(uow, item.PartNumber, from.Code, lot, StockStatus.Released, quantity, ct);
            var reason = $"Outside processing dispatch PO {po.PoNumber}/{c.PurchaseOrderLine}";
            var txn = new NewTransaction(InventoryTransactionKind.Transfer, item.PartNumber, quantity, from.Code, StockStatus.Released, processor.Code, StockStatus.Released, lot,
                PoNumber: po.PoNumber, PoLine: c.PurchaseOrderLine, Reason: reason);
            var txnId = await store.InsertTransactionAsync(txn, now, context.Actor, ct);
            return new CommandOutcome(CommandResult.Ok(key, $"Dispatched {quantity} {item.UnitOfMeasure} {item.PartNumber} to processor {processor.CustodianSupplier}; company ownership and lot {lot} are retained."),
                [pipeline.Event(new InventoryMoved(txnId, nameof(InventoryTransactionKind.Transfer), item.PartNumber, quantity, from.Code, processor.Code), context, SourceContext, "OutsideProcess", key)],
                new AuditChange("OutsideProcess", key, null, CanonicalJson.Serialize(txn)));
        }, ct);
    }

    public Task<CommandResult> ReturnFromOutsideProcessAsync(ReturnFromOutsideProcess c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var key = $"{part}:{c.LotNumber}:PO{c.PurchaseOrder}-L{c.PurchaseOrderLine}";
        var spec = new CommandSpec(SourceContext, nameof(ReturnFromOutsideProcess), Permissions.InventoryTransact, ResourceRef.Of("OutsideProcess", key), true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var item = await ItemAsync(uow, part, ct);
            var lot = RequireLot(item, c.LotNumber);
            var quantity = RequireQuantity(item, c.Quantity);
            var processor = await ActiveLocationAsync(uow, c.ProcessorLocation, ct);
            var destination = await CompanyLocationAsync(uow, c.ToLocation, ct);
            if (processor.Kind != LocationKind.ExternalProcessor)
                throw new CommandRejectedException("processor_location_required", $"{processor.Code} is not an outside-processor location.");
            var po = await RequireProcessorPoAsync(uow, c.PurchaseOrder, c.PurchaseOrderLine, processor.CustodianSupplier!, ct);
            var store = uow.Store<IInventoryStore>();
            await store.LockPartAsync(item.PartNumber, ct);
            await RequireStockAsync(uow, item.PartNumber, processor.Code, lot, StockStatus.Released, quantity, ct);
            var history = await uow.Store<IInventoryLookup>().ListTransactionsAsync(item.PartNumber, null, 100_000, ct);
            var dispatches = history.Where(t => t.Kind == InventoryTransactionKind.Transfer && t.PoNumber == po.PoNumber && t.PoLine == c.PurchaseOrderLine && t.LotNumber == lot && t.ToLocation == processor.Code).Sum(t => t.Quantity);
            var returns = history.Where(t => t.Kind == InventoryTransactionKind.Transfer && t.PoNumber == po.PoNumber && t.PoLine == c.PurchaseOrderLine && t.LotNumber == lot && t.FromLocation == processor.Code && t.ToLocation != processor.Code && t.ToStatus == StockStatus.Inspection).Sum(t => t.Quantity);
            if (returns + quantity > dispatches)
                throw new CommandRejectedException("return_exceeds_dispatch", $"Return quantity would exceed the {dispatches} units dispatched for this part, lot, PO and line.");
            var reason = $"Outside processing return PO {po.PoNumber}/{c.PurchaseOrderLine}; awaiting receiving inspection";
            var txn = new NewTransaction(InventoryTransactionKind.Transfer, item.PartNumber, quantity, processor.Code, StockStatus.Released, destination.Code, StockStatus.Inspection, lot,
                PoNumber: po.PoNumber, PoLine: c.PurchaseOrderLine, Reason: reason);
            var txnId = await store.InsertTransactionAsync(txn, now, context.Actor, ct);
            return new CommandOutcome(CommandResult.Ok(key, $"Returned {quantity} {item.UnitOfMeasure} {item.PartNumber} with lot {lot} to {destination.Code} for inspection; ownership and identity are retained."),
                [pipeline.Event(new InventoryMoved(txnId, nameof(InventoryTransactionKind.Transfer), item.PartNumber, quantity, processor.Code, destination.Code), context, SourceContext, "OutsideProcess", key)],
                new AuditChange("OutsideProcess", key, null, CanonicalJson.Serialize(txn)));
        }, ct);
    }

    private static async Task<PurchaseOrderInfo> RequireProcessorPoAsync(IKernelUnitOfWork uow, long poNumber, int lineNumber, string supplierCode, CancellationToken ct)
    {
        var po = await uow.Store<IProcurementLookup>().FindPurchaseOrderAsync(poNumber, ct)
            ?? throw new CommandRejectedException("po_not_found", $"Purchase order {poNumber} does not exist.");
        if (po.Status != PurchaseOrderStatus.Approved || po.SupplierCode != supplierCode)
            throw new CommandRejectedException("processor_po_mismatch", $"Purchase order {poNumber} must be approved and placed with processor {supplierCode}.");
        var line = po.Lines.SingleOrDefault(candidate => candidate.LineNumber == lineNumber && candidate.Status == PurchaseLineStatus.Open);
        if (line is null)
            throw new CommandRejectedException("po_line_not_open", $"Purchase order {poNumber} line {lineNumber} is not open.");
        var service = await uow.Store<IEngineeringLookup>().FindItemAsync(line.PartNumber, ct);
        if (service?.Class != ItemClass.Service)
            throw new CommandRejectedException("processor_service_required", $"Purchase order {poNumber} line {lineNumber} must purchase an item classified as a service or special process.");
        return po;
    }
}
