using Forge.Engineering.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Procurement.Contracts;
using Forge.Resources.Contracts;

namespace Forge.Procurement;

/// <param name="RequisitionId">The open requisition this line buys for (V2.7); the line keeps its source.</param>
public sealed record NewPurchaseLine(string PartNumber, decimal Quantity, DateOnly PromisedDate, long? RequisitionId = null);

/// <param name="Supplier">The supplier's code (or its exact name) in the supplier master.</param>
public sealed record CreatePurchaseOrder(string Supplier, IReadOnlyList<NewPurchaseLine> Lines);

public sealed record AddPurchaseLine(long PoNumber, string PartNumber, decimal Quantity, DateOnly PromisedDate, long? RequisitionId = null);

public sealed record ApprovePurchaseOrder(long PoNumber,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? QualityProcessCode=null,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? QualityWorkCenterCode=null);

/// <summary>The supplier moved its date. MRP and material readiness pick the new date up on their next run.</summary>
public sealed record ChangePromisedDate(long PoNumber, int LineNumber, DateOnly PromisedDate);

public sealed record CancelPurchaseLine(long PoNumber, int LineNumber);

public sealed record ClosePurchaseOrder(long PoNumber);

public sealed record CancelPurchaseOrder(long PoNumber);

public sealed record PurchaseOrderChanged(long PoNumber, string Change) : IDomainEvent
{
    public static string EventType => nameof(PurchaseOrderChanged);

    public static int SchemaVersion => 1;
}

public sealed record SupplierDateChanged(long PoNumber, int LineNumber, string PartNumber, DateOnly From, DateOnly To) : IDomainEvent
{
    public static string EventType => nameof(SupplierDateChanged);

    public static int SchemaVersion => 1;
}

public interface IProcurementStore
{
    Task LockPurchaseOrderAsync(long poNumber,CancellationToken ct);
    Task<long> InsertOrderAsync(SupplierInfo supplier, DateTimeOffset at, string actor, CancellationToken ct);

    Task<bool> InsertSupplierAsync(SupplierInfo supplier, CancellationToken ct);

    Task<bool> NameTakenAsync(string name, string exceptCode, CancellationToken ct);

    Task UpdateSupplierAsync(SupplierInfo supplier, DateTimeOffset at, string actor, CancellationToken ct);

    Task<bool> InsertApprovedSourceAsync(ApprovedSourceInfo source, CancellationToken ct);

    Task<bool> DeleteApprovedSourceAsync(string partNumber, string supplierCode, CancellationToken ct);

    Task<bool> InsertLineAsync(long poNumber, PurchaseLineInfo line, CancellationToken ct);

    Task SetStatusAsync(long poNumber, PurchaseOrderStatus status, DateTimeOffset at, string actor, CancellationToken ct);

    Task UpdateLineAsync(long poNumber, PurchaseLineInfo line, CancellationToken ct);

    Task InsertPackageAsync(TechnicalPackage package, CancellationToken ct);

    Task<long> InsertRequisitionAsync(RequisitionInfo requisition, CancellationToken ct);

    /// <summary>Marks a requisition ordered on a line, or back to open when the line or order is cancelled (null line).</summary>
    Task SetRequisitionOrderedAsync(long requisitionId, long? poNumber, int? poLine, string? actor, DateTimeOffset? at, CancellationToken ct);

    Task CancelRequisitionAsync(long requisitionId, string actor, DateTimeOffset at, string reason, CancellationToken ct);
}

/// <summary>
/// Purchase orders. A buyer writes a draft; a different person approves it, and only then do its open
/// lines count as scheduled receipts. Supplier date changes are recorded with a reason so MRP and
/// readiness can explain why material moved.
/// </summary>
public sealed class PurchasingService(CommandPipeline pipeline, ITechnicalPackageResolver packages, IKernelUnitOfWorkFactory unitOfWork, IForgeAuthorizer authorizer)
{
    public const string SourceContext = "Procurement";
    private const string Entity = "PurchaseOrder";

    public async Task<CommandResult> CreateAsync(CreatePurchaseOrder c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        IReadOnlyList<ResourceRef> resources;
        await using (var read = await unitOfWork.BeginSnapshotAsync(ct))
        {
            try
            {
                resources = await ResourcesForRequisitionsAsync(read, null, (c.Lines ?? []).Select(line => line.RequisitionId), ct);
            }
            catch (CommandRejectedException rejected) when (rejected.Code == "requisition_not_found")
            {
                resources = [ResourceRef.Of(Entity)]; // The audited body rejects the missing source; no program is invented.
            }
        }
        var resource = resources.FirstOrDefault(item => !authorizer.Authorize(context.Principal, Permissions.PurchasingManage, item).IsPermitted) ?? resources[0];
        var spec = new CommandSpec(SourceContext, nameof(CreatePurchaseOrder), Permissions.PurchasingManage, resource);
        return await pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var supplier = await SupplierService.RequireOrderableAsync(uow, c.Supplier, ct);
            if (c.Lines is null || c.Lines.Count is 0 or > 999)
            {
                throw new CommandRejectedException("lines_required", "A purchase order needs 1 to 999 lines.");
            }

            var lines = new List<PurchaseLineInfo>();
            foreach (var (line, index) in c.Lines.Select((l, i) => (l, i)))
            {
                var valid = await ValidLineAsync(uow, index + 1, line.PartNumber, line.Quantity, line.PromisedDate, now, supplier.Code, ct);
                lines.Add(await WithSourceAsync(uow, valid, line.RequisitionId, context, lines, supplier.Code, now, ct));
            }
            if (!(await ResourcesForRequisitionsAsync(uow, null, lines.Select(line => line.RequisitionId), ct)).SequenceEqual(resources))
                throw new CommandRejectedException("purchase_context_changed", "The purchase source context changed. Review the current requisitions before retrying.");

            var store = uow.Store<IProcurementStore>();
            var number = await store.InsertOrderAsync(supplier, now, context.Actor, ct);
            foreach (var line in lines)
            {
                await store.InsertLineAsync(number, line, ct);
                if (line.RequisitionId is { } requisition)
                {
                    await store.SetRequisitionOrderedAsync(requisition, number, line.LineNumber, context.Actor, now, ct);
                }
            }

            var id = Id(number);
            var program = resources.Count == 1 ? resources[0].Program : null;
            return new CommandOutcome(
                CommandResult.Ok(id, $"Purchase order {id} drafted for {supplier.Name} ({supplier.Code}): {lines.Count} line(s). A different person must approve it."),
                [pipeline.Event(new PurchaseOrderChanged(number, "Drafted"), context, SourceContext, Entity, id, program)],
                new AuditChange(Entity, id, null, CanonicalJson.Serialize(new { supplier = supplier.Name, supplierCode = supplier.Code, lines }), program));
        }, ct);
    }

    public Task<CommandResult> AddLineAsync(AddPurchaseLine c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeAsync(nameof(AddPurchaseLine), c.PoNumber, c, context, false, Permissions.PurchasingManage, async (uow, po, now) =>
        {
            if (po.Status != PurchaseOrderStatus.Draft)
            {
                throw new CommandRejectedException("not_draft", $"Purchase order {po.PoNumber} is {po.Status}; lines are added only to drafts.");
            }

            var line = await ValidLineAsync(uow, po.Lines.Count == 0 ? 1 : po.Lines.Max(l => l.LineNumber) + 1, c.PartNumber, c.Quantity, c.PromisedDate, now, po.SupplierCode, ct);
            line = await WithSourceAsync(uow, line, c.RequisitionId, context, [], po.SupplierCode, now, ct);
            await uow.Store<IProcurementStore>().InsertLineAsync(po.PoNumber, line, ct);
            if (line.RequisitionId is { } requisition)
            {
                await uow.Store<IProcurementStore>().SetRequisitionOrderedAsync(requisition, po.PoNumber, line.LineNumber, context.Actor, now, ct);
            }
            return ($"line {line.LineNumber} added: {line.Quantity} {line.UnitOfMeasure} {line.PartNumber} by {line.PromisedDate:yyyy-MM-dd}", "LineAdded", null, CanonicalJson.Serialize(line), null);
        }, ct);
    }

    public Task<CommandResult> ApproveAsync(ApprovePurchaseOrder c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeAsync(nameof(ApprovePurchaseOrder), c.PoNumber, c, context, true, Permissions.PurchasingApprove, async (uow, po, now) =>
        {
            if (po.Status != PurchaseOrderStatus.Draft)
            {
                throw new CommandRejectedException("not_draft", $"Purchase order {po.PoNumber} is {po.Status}; only a draft can be approved.");
            }

            if (po.CreatedBy == context.Actor)
            {
                throw new CommandRejectedException("separation_of_duties", "You raised this purchase order; a different person must approve it.");
            }

            if (po.SupplierCode is { } code && await uow.Store<IProcurementLookup>().FindSupplierAsync(code, ct) is { Status: not SupplierStatus.Active } held)
            {
                throw new CommandRejectedException("supplier_not_active", $"{held.Name} is {held.Status}; lift the hold before approving orders to it.");
            }

            if (po.Lines.All(l => l.Status != PurchaseLineStatus.Open))
            {
                throw new CommandRejectedException("no_open_lines", "The purchase order has no open lines.");
            }

            // V3.10: a procurement (or administrative) hold on the supplier, the order or a part on it stops approval.
            await Forge.Kernel.Holds.HoldGate.RequireNoneAsync(uow, new Forge.Kernel.Holds.HoldCheck(
                Forge.Kernel.Holds.HoldActivity.Purchase, Parts: po.Lines.Where(l => l.Status == PurchaseLineStatus.Open).Select(l => l.PartNumber).Distinct(StringComparer.Ordinal).ToList(),
                SupplierCode: po.SupplierCode, PoNumber: po.PoNumber), ct);

            await uow.Store<IProcurementStore>().SetStatusAsync(po.PoNumber, PurchaseOrderStatus.Approved, now, context.Actor, ct);
            var recorded = await packages.ResolveAsync(uow, po with { Lines = po.Lines.Where(l => l.Status == PurchaseLineStatus.Open).ToList() }, now, context.Actor, ct);
            foreach (var package in recorded)
            {
                await uow.Store<IProcurementStore>().InsertPackageAsync(package, ct);
            }

            return ($"approved; technical package recorded for {recorded.Count} line(s)", "Approved", nameof(PurchaseOrderStatus.Draft), nameof(PurchaseOrderStatus.Approved), null);
        }, ct);
    }

    public Task<CommandResult> ChangePromisedDateAsync(ChangePromisedDate c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeAsync(nameof(ChangePromisedDate), c.PoNumber, c, context, true, Permissions.PurchasingManage, async (uow, po, _) =>
        {
            var line = OpenLine(po, c.LineNumber);
            if (line.PromisedDate == c.PromisedDate)
            {
                return null;
            }

            await uow.Store<IProcurementStore>().UpdateLineAsync(po.PoNumber, line with { PromisedDate = c.PromisedDate }, ct);
            return ($"line {line.LineNumber} {line.PartNumber} now promised {c.PromisedDate:yyyy-MM-dd} (was {line.PromisedDate:yyyy-MM-dd})", "DateChanged", line.PromisedDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), c.PromisedDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                pipeline.Event(new SupplierDateChanged(po.PoNumber, line.LineNumber, line.PartNumber, line.PromisedDate, c.PromisedDate), context, SourceContext, Entity, Id(po.PoNumber)));
        }, ct);
    }

    public Task<CommandResult> CancelLineAsync(CancelPurchaseLine c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeAsync(nameof(CancelPurchaseLine), c.PoNumber, c, context, true, Permissions.PurchasingManage, async (uow, po, _) =>
        {
            var line = OpenLine(po, c.LineNumber);
            await uow.Store<IProcurementStore>().UpdateLineAsync(po.PoNumber, line with { Status = PurchaseLineStatus.Cancelled }, ct);
            if (line.RequisitionId is { } requisition)
            {
                await uow.Store<IProcurementStore>().SetRequisitionOrderedAsync(requisition, null, null, null, null, ct);
            }

            return ($"line {line.LineNumber} cancelled{(line.RequisitionId is { } r ? $"; requisition {r} is open again" : string.Empty)}", "LineCancelled", CanonicalJson.Serialize(line), nameof(PurchaseLineStatus.Cancelled), null);
        }, ct);
    }

    public Task<CommandResult> CloseAsync(ClosePurchaseOrder c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeAsync(nameof(ClosePurchaseOrder), c.PoNumber, c, context, true, Permissions.PurchasingManage, async (uow, po, now) =>
        {
            if (po.Status != PurchaseOrderStatus.Approved)
            {
                throw new CommandRejectedException("not_approved", $"Purchase order {po.PoNumber} is {po.Status}; only an approved order is closed.");
            }

            await uow.Store<IProcurementStore>().SetStatusAsync(po.PoNumber, PurchaseOrderStatus.Closed, now, context.Actor, ct);
            return ("closed; nothing more is expected", "Closed", nameof(PurchaseOrderStatus.Approved), nameof(PurchaseOrderStatus.Closed), null);
        }, ct);
    }

    public Task<CommandResult> CancelAsync(CancelPurchaseOrder c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeAsync(nameof(CancelPurchaseOrder), c.PoNumber, c, context, true, Permissions.PurchasingManage, async (uow, po, now) =>
        {
            if (po.Status != PurchaseOrderStatus.Draft)
            {
                throw new CommandRejectedException("not_draft", $"Purchase order {po.PoNumber} is {po.Status}; an approved order is closed, not cancelled.");
            }

            await uow.Store<IProcurementStore>().SetStatusAsync(po.PoNumber, PurchaseOrderStatus.Cancelled, now, context.Actor, ct);
            foreach (var requisition in po.Lines.Where(l => l.Status == PurchaseLineStatus.Open && l.RequisitionId is not null).Select(l => l.RequisitionId!.Value))
            {
                await uow.Store<IProcurementStore>().SetRequisitionOrderedAsync(requisition, null, null, null, null, ct);
            }

            return ("cancelled", "Cancelled", nameof(PurchaseOrderStatus.Draft), nameof(PurchaseOrderStatus.Cancelled), null);
        }, ct);
    }

    private static string Id(long number) => number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A line bought for a requisition carries its source; the requisition must be open, for the same part, and not used twice.</summary>
    private static async Task<PurchaseLineInfo> WithSourceAsync(Kernel.Access.IKernelUnitOfWork uow, PurchaseLineInfo line, long? requisitionId, CommandContext context, IReadOnlyList<PurchaseLineInfo> earlier, string? supplierCode, DateTimeOffset now, CancellationToken ct)
    {
        if (requisitionId is not { } id)
        {
            return line;
        }

        await uow.Store<ISourcingStore>().LockRfqAsync(-id, ct);
        var requisition = await uow.Store<IRequisitionLookup>().FindRequisitionAsync(id, ct);
        if (requisition is null || (requisition.ProgramId is { } p && !context.Principal.Programs.Contains(new ProgramId(p))))
        {
            throw new CommandRejectedException("requisition_not_found", $"Requisition {id} does not exist.");
        }

        if (requisition.Status != RequisitionStatus.Open || earlier.Any(l => l.RequisitionId == id))
        {
            throw new CommandRejectedException("requisition_not_open", $"Requisition {id} is {requisition.Status}{(requisition.PoNumber is { } po ? $" on PO {po} line {requisition.PoLine}" : string.Empty)}.");
        }

        if (requisition.PartNumber != line.PartNumber)
        {
            throw new CommandRejectedException("requisition_part", $"Requisition {id} is for {requisition.PartNumber}, not {line.PartNumber}.");
        }

        var sourcing = uow.Store<ISourcingLookup>();
        var rfq = (await sourcing.ListRfqsAsync(ct)).FirstOrDefault(r => r.RequisitionId == id && r.Status != RfqStatus.Cancelled);
        if (rfq is not null)
        {
            await uow.Store<ISourcingStore>().LockRfqAsync(rfq.RfqId, ct);
            var selected = await sourcing.SelectedQuoteForRequisitionAsync(id, ct);
            if (selected is null) throw new CommandRejectedException("source_selection_required", "This RFQ requires an authorized source selection before a purchase order is created.");
            var original = await uow.Store<IDocumentLookup>().FindDocumentAsync(selected.OriginalDocumentNumber, ct);
            if (original is null || !DocumentAccess.CanSee(context.Principal, original))
                throw new CommandRejectedException("source_selection_required", "The selected supplier response is not available to you.");
            if (selected.SupplierCode != supplierCode || selected.Quantity < line.Quantity || selected.ValidThrough < (await uow.SiteDateAsync(now, ct)))
                throw new CommandRejectedException("selected_source_mismatch", "The purchase line must match the selected supplier, quoted quantity and current response validity.");
        }

        return line with { RequisitionId = id, SourceKind = requisition.SourceKind, SourceKey = requisition.SourceKey, SourceLabel = requisition.SourceLabel };
    }

    private static PurchaseLineInfo OpenLine(PurchaseOrderInfo po, int lineNumber)
    {
        if (po.Status is not (PurchaseOrderStatus.Draft or PurchaseOrderStatus.Approved))
        {
            throw new CommandRejectedException("not_open", $"Purchase order {po.PoNumber} is {po.Status}.");
        }

        var line = po.Lines.FirstOrDefault(l => l.LineNumber == lineNumber)
            ?? throw new CommandRejectedException("line_not_found", $"Purchase order {po.PoNumber} has no line {lineNumber}.");
        return line.Status == PurchaseLineStatus.Open
            ? line
            : throw new CommandRejectedException("line_cancelled", $"Line {lineNumber} is cancelled.");
    }

    private static async Task<PurchaseLineInfo> ValidLineAsync(Kernel.Access.IKernelUnitOfWork uow, int lineNumber, string rawPart, decimal quantity, DateOnly promised, DateTimeOffset now, string? supplierCode, CancellationToken ct)
    {
        var part = BusinessCode.Require(rawPart, "Part");
        if (quantity <= 0 || quantity > 100_000_000)
        {
            throw new CommandRejectedException("invalid_quantity", $"Line {lineNumber}: quantity must be above zero.");
        }

        if (promised < (await uow.SiteDateAsync(now, ct)).AddDays(-1))
        {
            throw new CommandRejectedException("promised_in_past", $"Line {lineNumber}: the promised date {promised:yyyy-MM-dd} is in the past.");
        }

        var item = await uow.Store<IEngineeringLookup>().FindItemAsync(part, ct)
            ?? throw new CommandRejectedException("unknown_part", $"Line {lineNumber}: part {part} does not exist.");
        if (item.MakeBuy != MakeBuy.Buy)
        {
            throw new CommandRejectedException("not_purchased", $"Line {lineNumber}: {part} is made, not bought.");
        }

        // A COTS item is bought as one of its approved manufacturer parts (V2 §12); without one, there is nothing approved to buy.
        if (item.Class == ItemClass.Cots
            && !(await uow.Store<IEngineeringLookup>().ListManufacturerPartsAsync(part, ct)).Any(m => m.Status == ManufacturerPartStatus.Approved))
        {
            throw new CommandRejectedException("no_approved_manufacturer_part", $"Line {lineNumber}: {part} is COTS and has no approved manufacturer part yet.");
        }

        var sources = await uow.Store<IProcurementLookup>().ListApprovedSourcesAsync(part, ct);
        if (sources.Count > 0 && !sources.Any(x => x.SupplierCode == supplierCode))
        {
            throw new CommandRejectedException("not_approved_source", $"Line {lineNumber}: {part} is bought only from its approved sources ({string.Join(", ", sources.Select(x => x.SupplierCode))}).");
        }

        return new PurchaseLineInfo(lineNumber, part, quantity, item.UnitOfMeasure, promised, PurchaseLineStatus.Open);
    }

    private async Task<CommandResult> ChangeAsync<TCommand>(
        string commandType,
        long poNumber,
        TCommand command,
        CommandContext context,
        bool reasonRequired,
        string permission,
        Func<Kernel.Access.IKernelUnitOfWork, PurchaseOrderInfo, DateTimeOffset, Task<(string Message, string Change, string? Old, string? New, EventEnvelope? Extra)?>> change,
        CancellationToken ct)
    {
        var id = Id(poNumber);
        IReadOnlyList<ResourceRef> recordedResources;
        IReadOnlyList<ResourceRef> proposedResources = [];
        await using (var read = await unitOfWork.BeginSnapshotAsync(ct))
        {
            var po = await read.Store<IProcurementLookup>().FindPurchaseOrderAsync(poNumber, ct);
            recordedResources = po is null ? [ResourceRef.Of(Entity, id)] : await ResourcesForPurchaseOrderAsync(read, po, ct);
            if (command is AddPurchaseLine added)
            {
                try
                {
                    proposedResources = await ResourcesForRequisitionsAsync(read, id, [added.RequisitionId], ct);
                }
                catch (CommandRejectedException rejected) when (rejected.Code == "requisition_not_found")
                {
                    proposedResources = [ResourceRef.Of(Entity, id)];
                }
            }
        }
        var resources = recordedResources.Concat(proposedResources).Distinct().OrderBy(item => item.Program?.Value).ToArray();
        // A PO may buy for multiple programs. Send the first denied context through the common
        // pipeline so every denial is audited, and require every recorded context before acting.
        var resource = resources.FirstOrDefault(item => !authorizer.Authorize(context.Principal, permission, item).IsPermitted) ?? resources[0];
        var spec = new CommandSpec(SourceContext, commandType, permission, resource, reasonRequired);
        return await pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            await uow.Store<IProcurementStore>().LockPurchaseOrderAsync(poNumber,ct);
            var po = await uow.Store<IProcurementLookup>().FindPurchaseOrderAsync(poNumber, ct)
                ?? throw new CommandRejectedException("purchase_order_not_found", $"Purchase order {id} does not exist.");
            if (!(await ResourcesForPurchaseOrderAsync(uow, po, ct)).SequenceEqual(recordedResources))
                throw new CommandRejectedException("purchase_context_changed", "The purchase order's source context changed. Review its current requisitions and retry.");
            if (command is AddPurchaseLine added && !(await ResourcesForRequisitionsAsync(uow, id, [added.RequisitionId], ct)).SequenceEqual(proposedResources))
                throw new CommandRejectedException("purchase_context_changed", "The proposed line's source context changed. Review its current requisition and retry.");
            var result = await change(uow, po, now);
            if (result is not { } r)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(id, "Nothing changed."));
            }

            var program = resources.Length == 1 ? resources[0].Program : null;
            var events = new List<EventEnvelope> { pipeline.Event(new PurchaseOrderChanged(poNumber, r.Change), context, SourceContext, Entity, id, program) };
            if (r.Extra is { } extra)
            {
                events.Add(extra);
            }
            return new CommandOutcome(
                CommandResult.Ok(id, $"Purchase order {id}: {r.Message}."),
                events,
                new AuditChange(Entity, id, r.Old, r.New, program));
        }, ct);
    }

    /// <summary>Each actual requisition program is a required PO context; unsourced lines remain unscoped.</summary>
    public static Task<IReadOnlyList<ResourceRef>> ResourcesForPurchaseOrderAsync(IKernelUnitOfWork uow, PurchaseOrderInfo po, CancellationToken ct) =>
        PurchaseOrderAuthority.ResourcesAsync(uow, po, ct);

    private static Task<IReadOnlyList<ResourceRef>> ResourcesForRequisitionsAsync(IKernelUnitOfWork uow, string? subjectId, IEnumerable<long?> requisitionIds, CancellationToken ct) =>
        PurchaseOrderAuthority.ResourcesForRequisitionsAsync(uow, subjectId, requisitionIds, ct);
}
