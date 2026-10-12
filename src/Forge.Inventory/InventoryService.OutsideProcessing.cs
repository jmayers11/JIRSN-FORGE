using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Kernel.Holds;
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
        var key = $"{part}:{c.LotNumber?.Trim().ToUpperInvariant()}:PO{c.PurchaseOrder}-L{c.PurchaseOrderLine}";
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
            await RequireUsableAsync(uow, item, lot, now, ct);
            await HoldGate.RequireNoneAsync(uow, new HoldCheck(HoldActivity.Ship, Parts: [part], LotList: [new HoldLot(part, lot!)], SupplierCode: processor.CustodianSupplier), ct);
            var package = await uow.Store<ITechnicalPackageLookup>().FindPackageAsync(po.PoNumber, c.PurchaseOrderLine, ct)
                ?? throw new CommandRejectedException("processor_package_required", "The approved service purchase order must have its recorded technical package.");
            var instance = (await uow.Store<IInventoryLookup>().ListLotsAsync(part, ct)).Single(l => l.LotNumber == lot);
            var revision = Optional(c.MaterialRevision) ?? instance.PartRevision;
            if (Optional(c.MaterialRevision) is { } requestedRevision)
            {
                if (instance.PartRevision != requestedRevision || !(await uow.Store<IEngineeringLookup>().ListRevisionsAsync(part, ct)).Any(r => r.Revision == requestedRevision && r.Status == RevisionStatus.Released))
                    throw new CommandRejectedException("material_configuration_mismatch", "The sent material revision must match the identified lot and a released item revision.");
            }
            var specification = Optional(c.SpecificationDocument);
            var specificationRevision = Optional(c.SpecificationRevision);
            Guid? programId = null;
            var markings = DataMarking.None;
            if (specification is not null)
            {
                var document = await uow.Store<IDocumentLookup>().FindDocumentAsync(specification, ct);
                if (document is null || !DocumentAccess.CanSee(context.Principal, document))
                    throw new CommandRejectedException("process_specification_unavailable", "The process specification is unavailable to the sender.");
                var applicable = document.Current;
                if (applicable is null || (specificationRevision is not null && applicable.Revision != specificationRevision)
                    || applicable.FileSha256 is null || applicable.EffectiveFrom > DateOnly.FromDateTime(now.UtcDateTime)
                    || applicable.EffectiveTo < DateOnly.FromDateTime(now.UtcDateTime))
                    throw new CommandRejectedException("process_specification_not_applicable", "Send an applicable controlled process specification with its recorded file.");
                specificationRevision = applicable.Revision;
                programId = document.ProgramId;
                markings = document.Markings;
            }
            else if (specificationRevision is not null)
                throw new CommandRejectedException("process_specification_required", "A specification revision needs its document number.");
            var priorDispatches = await uow.Store<IOutsideProcessLookup>().ListDispatchesAsync(key, ct);
            var prior = priorDispatches.Count == 0 ? null : priorDispatches[0];
            if (!c.RequireCertificate && !context.Principal.HasPermission(Permissions.QualityDisposition,
                new ResourceRef("OutsideProcess", key, programId is { } program ? new ProgramId(program) : null)))
                throw new CommandRejectedException("certificate_waiver_authority_required", "Quality disposition authority is required to waive the processor certificate requirement.");
            if (prior is not null && (prior.MaterialRevision != revision || prior.SpecificationDocument != specification || prior.SpecificationRevision != specificationRevision || prior.RequireCertificate != c.RequireCertificate))
                throw new CommandRejectedException("process_configuration_changed", "This lot and service order already have a sent configuration; use a new service order for a changed process.");
            var reason = $"Outside processing dispatch PO {po.PoNumber}/{c.PurchaseOrderLine}";
            var txn = new NewTransaction(InventoryTransactionKind.Transfer, item.PartNumber, quantity, from.Code, StockStatus.Released, processor.Code, StockStatus.Released, lot,
                PoNumber: po.PoNumber, PoLine: c.PurchaseOrderLine, Reason: reason, PartRevision: revision);
            var txnId = await store.InsertTransactionAsync(txn, now, context.Actor, ct);
            var snapshot = new OutsideProcessDispatchInfo(txnId, key, part, lot!, revision, po.PoNumber, c.PurchaseOrderLine, processor.Code,
                specification, specificationRevision, c.ExpectedReturnDate, c.RequireCertificate, package, now, context.Actor, programId, markings);
            await uow.Store<IOutsideProcessStore>().InsertDispatchAsync(snapshot, ct);
            return new CommandOutcome(CommandResult.Ok(key, $"Dispatched {quantity} {item.UnitOfMeasure} {item.PartNumber} to processor {processor.CustodianSupplier}; company ownership and lot {lot} are retained."),
                [pipeline.Event(new InventoryMoved(txnId, nameof(InventoryTransactionKind.Transfer), item.PartNumber, quantity, from.Code, processor.Code), context, SourceContext, "OutsideProcess", key)],
                new AuditChange("OutsideProcess", key, null, CanonicalJson.Serialize(new { transaction = txn, configuration = snapshot })));
        }, ct);
    }

    public Task<CommandResult> ReturnFromOutsideProcessAsync(ReturnFromOutsideProcess c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var key = $"{part}:{c.LotNumber?.Trim().ToUpperInvariant()}:PO{c.PurchaseOrder}-L{c.PurchaseOrderLine}";
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
            var sentConfigurations = await uow.Store<IOutsideProcessLookup>().ListDispatchesAsync(key, ct);
            var configuration = sentConfigurations.Count == 0 ? null : sentConfigurations[0];
            RequireOutsideProcessAccess(configuration, context.Principal);
            var txn = new NewTransaction(InventoryTransactionKind.Transfer, item.PartNumber, quantity, processor.Code, StockStatus.Released, destination.Code, StockStatus.Inspection, lot,
                PoNumber: po.PoNumber, PoLine: c.PurchaseOrderLine, Reason: reason, PartRevision: configuration?.MaterialRevision);
            var txnId = await store.InsertTransactionAsync(txn, now, context.Actor, ct);
            await uow.Store<IOutsideProcessStore>().InsertReturnAsync(new OutsideProcessReturnInfo(txnId, key, part, lot!, destination.Code, now, context.Actor), ct);
            return new CommandOutcome(CommandResult.Ok(key, $"Returned {quantity} {item.UnitOfMeasure} {item.PartNumber} with lot {lot} to {destination.Code} for inspection; ownership and identity are retained."),
                [pipeline.Event(new InventoryMoved(txnId, nameof(InventoryTransactionKind.Transfer), item.PartNumber, quantity, processor.Code, destination.Code), context, SourceContext, "OutsideProcess", key)],
                new AuditChange("OutsideProcess", key, null, CanonicalJson.Serialize(txn)));
        }, ct);
    }

    private static void RequireOutsideProcessAccess(OutsideProcessDispatchInfo? dispatch, ForgePrincipal principal)
    {
        if (dispatch is not null && ((dispatch.ProgramId is { } program && !principal.Programs.Contains(new ProgramId(program))) || (dispatch.Markings & ~principal.Markings) != DataMarking.None))
            throw new CommandRejectedException("outside_process_unavailable", "The outside-process configuration is unavailable to this person.");
    }

    private static async Task RequireOutsideProcessCertificateAsync(IKernelUnitOfWork uow, string key, ForgePrincipal principal, CancellationToken ct)
    {
        var dispatches = await uow.Store<IOutsideProcessLookup>().ListDispatchesAsync(key, ct);
        var dispatch = dispatches.Count == 0 ? null : dispatches[0];
        RequireOutsideProcessAccess(dispatch, principal);
        // Legacy ledger transfers have no policy snapshot; require certification rather than invent one.
        if (dispatch is { RequireCertificate: false }) return;
        var documents = uow.Store<IDocumentLookup>();
        foreach (var link in await documents.ListLinksAsync(DocumentTargetType.OutsideProcess, key, null, false, ct))
        {
            var document = await documents.FindDocumentAsync(link.DocumentNumber, ct);
            if (document?.Authority != DocumentAuthority.Reference || !DocumentAccess.CanSee(principal, document) || document.Type is not (DocumentType.Certificate or DocumentType.CertificateOfConformance or DocumentType.CertificateOfAnalysis)) continue;
            if (document.Latest?.FileSha256 is { } hash && await documents.FindFileAsync(hash, ct) is not null) return;
        }
        throw new CommandRejectedException("processor_certificate_required", "Attach the processor certificate file to this outside-process record before accepting returned material.");
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
