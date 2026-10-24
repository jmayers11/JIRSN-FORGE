using System.Globalization;
using System.Text.Json.Serialization;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Workforce;
using Forge.Workforce.Contracts;
using Forge.Quality.Contracts;

namespace Forge.Inventory;

/// <param name="FromLocation">Where to take it from, when already known: the task starts allocated.</param>
public sealed record RequestMaterial(MaterialTaskKind Kind, long? OrderNumber, string PartNumber, decimal Quantity, string ToLocation, string? LotNumber = null,
    string? FromLocation = null, string? KitCode = null, int? OperationSequence = null, bool OperatorInitiated = false, Guid? RequesterPersonId = null,
    long? SourceSuspectReportId = null, bool AllowNonBomMaterial = false);

public sealed record AllocateMaterial(long TaskId, string FromLocation, string? LotNumber);

/// <param name="Quantity">What was actually picked; less than the task when the location was short.</param>
public sealed record PickMaterial(long TaskId, string ScannedLocation, string? ScannedLot, decimal? Quantity = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ScannedMaterialIdentity? ScannedMaterial = null);

public sealed record DeliverMaterial(long TaskId, string ScannedLocation);

/// <summary>A receiver independently settles accepted and rejected quantities from a scanned transit task.</summary>
public sealed record ReceiveMaterial(long TaskId, string ScannedLocation, Guid ReceiverPersonId, decimal AcceptedQuantity, decimal RejectedQuantity = 0m, string? QuarantineLocation = null);
public sealed record ReportMaterialCustodyException(long TaskId, Guid ReporterPersonId, string ScannedLocation);
public sealed record ReturnReceivedMaterial(long TaskId, decimal Quantity, string StoresLocation);
public sealed record ReverseMaterialReceipt(long TaskId, decimal Quantity, string QuarantineLocation);

public sealed record CancelMaterialTask(long TaskId);
public sealed record ReviewAdditionalMaterialRequest(long TaskId, bool Approve);
public sealed record RequestOrderMaterialShortages(long OrderNumber, string ToLocation);

public sealed record CreateCountProgram(string ProgramCode, string Name, string? LocationCode, string? PartNumber, int FrequencyDays);

public sealed record RetireCountProgram(string ProgramCode);

public sealed record MaterialTaskChanged(long TaskId, string Status, long? OrderNumber) : IDomainEvent
{
    public static string EventType => nameof(MaterialTaskChanged);

    public static int SchemaVersion => 1;
}
public sealed record MaterialTaskClosed(long TaskId, string Status) : IDomainEvent
{
    public static string EventType => nameof(MaterialTaskClosed);
    public static int SchemaVersion => 1;
}

public sealed record MaterialTaskReviewed(long TaskId, string Status) : IDomainEvent
{
    public static string EventType => nameof(MaterialTaskReviewed);
    public static int SchemaVersion => 1;
}

public sealed record MaterialCustodyExceptionReported(long TaskId, Guid ReporterPersonId, string LocationCode) : IDomainEvent
{
    public static string EventType => nameof(MaterialCustodyExceptionReported);
    public static int SchemaVersion => 1;
}

public sealed record MaterialCustodyAccepted(long TaskId, Guid ReceiverPersonId, string Location, long TransactionId) : IDomainEvent
{
    public static string EventType => nameof(MaterialCustodyAccepted);
    public static int SchemaVersion => 1;
}

public sealed record MaterialCustodyDeliveryDiscrepancy(long TaskId, Guid ReceiverPersonId, decimal RejectedQuantity, string QuarantineLocation) : IDomainEvent
{
    public static string EventType => nameof(MaterialCustodyDeliveryDiscrepancy);
    public static int SchemaVersion => 1;
}

public sealed record MaterialCustodyResolved(long TaskId, string Status) : IDomainEvent
{
    public static string EventType => nameof(MaterialCustodyResolved);
    public static int SchemaVersion => 1;
}

public sealed record MaterialCustodyAdjusted(long TaskId, string Adjustment, decimal Quantity, string Location) : IDomainEvent
{
    public static string EventType => nameof(MaterialCustodyAdjusted);
    public static int SchemaVersion => 1;
}

public sealed record CountProgramChanged(string ProgramCode, bool Active) : IDomainEvent
{
    public static string EventType => nameof(CountProgramChanged);

    public static int SchemaVersion => 1;
}

/// <summary>
/// Movement and point of use (V3.4, V3 brief I, BB; spec §49). Material for an order is requested, allocated
/// from specific stock (which holds it back from other allocations), picked with the location and lot scanned,
/// and delivered with the destination scanned; only delivery moves stock in the ledger. Each step can fall short
/// of the last: a short pick delivers what was picked. Put-away and replenishment follow the same steps without
/// an order. Cycle-count programs say which balances are counted how often.
/// </summary>
public sealed partial class InventoryService
{
    private const string TaskEntity = "MaterialTask";

    public Task<CommandResult> RequestOrderMaterialShortagesAsync(RequestOrderMaterialShortages c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(SourceContext, nameof(RequestOrderMaterialShortages), Permissions.InventoryMaterialRequest,
            ResourceRef.Of("ProductionOrder", c.OrderNumber.ToString(CultureInfo.InvariantCulture)), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var order = await ReleasedOrderAsync(uow, c.OrderNumber, context, ct);
            if (order.Materials.Count == 0)
                throw new CommandRejectedException("order_has_no_materials", $"Order {order.OrderNumber} has no released BOM material demand.");
            var destination = await CompanyLocationAsync(uow, c.ToLocation, ct);
            if (destination.Kind is not (LocationKind.Staging or LocationKind.Production)
                && !(destination.Kind == LocationKind.MachineSide && destination.PointOfUse))
                throw new CommandRejectedException("not_point_of_use", $"{destination.Code} is not an active staging or point-of-use destination.");
            if (destination.WorkCenterCode is { } center && order.Operations.All(operation => operation.WorkCenterCode != center))
                throw new CommandRejectedException("wrong_work_center", $"{destination.Code} serves {center}; order {order.OrderNumber} has no operation there.");

            var partNumbers = order.Materials.Select(material => material.ComponentPartNumber).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var inventory = uow.Store<IInventoryStore>();
            foreach (var partNumber in partNumbers) await inventory.LockPartAsync(partNumber, ct);
            var lookup = uow.Store<IInventoryLookup>();
            var tasks = await lookup.ListMaterialTasksAsync(false, order.OrderNumber, ct);
            var created = new List<MaterialTask>();
            foreach (var (partNumber, required) in order.Materials.GroupBy(material => material.ComponentPartNumber, StringComparer.Ordinal)
                .Select(group => (group.Key, group.Sum(line => line.RequiredQuantity))).OrderBy(row => row.Key, StringComparer.Ordinal))
            {
                var item = await ItemAsync(uow, partNumber, ct);
                var transactions = await lookup.ListTransactionsAsync(partNumber, order.OrderNumber, 100_000, ct);
                var issued = transactions.Where(transaction => transaction.Kind is InventoryTransactionKind.Issue or InventoryTransactionKind.Return && transaction.SubstitutesFor is null)
                    .Sum(transaction => transaction.Kind == InventoryTransactionKind.Issue ? transaction.Quantity : -transaction.Quantity);
                var remaining = MaterialTaskDemand.Remaining(required, issued, tasks.Where(task => task.PartNumber == partNumber));
                if (remaining <= 0) continue;
                RequireQuantity(item, remaining);
                created.Add(new MaterialTask(0, MaterialTaskKind.Stage, order.OrderNumber, null, partNumber, null, remaining, null, destination.Code,
                    MaterialTaskStatus.Requested, null, context.Reason, context.Actor, now));
            }

            if (created.Count == 0)
                return CommandOutcome.NoChange(CommandResult.NoChange(order.OrderNumber.ToString(CultureInfo.InvariantCulture), "No remaining BOM quantity needs a material task; existing issues and requests cover the released demand."));

            var events = new List<Forge.Kernel.Events.EventEnvelope>();
            var saved = new List<MaterialTask>();
            foreach (var task in created)
            {
                var id = await inventory.InsertMaterialTaskAsync(task, ct);
                var persisted = task with { TaskId = id };
                saved.Add(persisted);
                events.Add(pipeline.Event(new MaterialTaskChanged(id, persisted.Status.ToString(), persisted.OrderNumber), context, SourceContext, TaskEntity,
                    id.ToString(CultureInfo.InvariantCulture), order.ProgramId is { } eventProgramId ? new ProgramId(eventProgramId) : null));
            }
            var ids = saved.Select(task => task.TaskId).ToArray();
            return new CommandOutcome(CommandResult.Ok(order.OrderNumber.ToString(CultureInfo.InvariantCulture),
                $"Created {saved.Count} staging task(s) for remaining BOM demand: {string.Join(", ", saved.Select(task => $"{Qty(task.Quantity)} {task.PartNumber} (task {task.TaskId})"))}."),
                events, new AuditChange("MaterialTaskBatch", order.OrderNumber.ToString(CultureInfo.InvariantCulture), null,
                    CanonicalJson.Serialize(new { orderNumber = order.OrderNumber, destination = destination.Code, taskIds = ids, tasks = saved }),
                    order.ProgramId is { } auditProgramId ? new ProgramId(auditProgramId) : null));
        }, ct);
    }

    public Task<CommandResult> RequestMaterialAsync(RequestMaterial c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var requestPermission = c.Kind is MaterialTaskKind.Stage or MaterialTaskKind.Kit
            ? Permissions.InventoryMaterialRequest : Permissions.InventoryTransact;
        var spec = new CommandSpec(SourceContext, nameof(RequestMaterial), requestPermission, ResourceRef.Of(StockEntity, part),
            ReasonRequired: c.OperatorInitiated || c.AllowNonBomMaterial || c.SourceSuspectReportId is not null);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var item = await ItemAsync(uow, part, ct);
            var quantity = RequireQuantity(item, c.Quantity);
            var to = await CompanyLocationAsync(uow, c.ToLocation, ct);
            var lot = Optional(c.LotNumber) is null ? null : RequireLot(item, c.LotNumber);
            var lookup = uow.Store<IInventoryLookup>();
            if (c.OperatorInitiated)
            {
                if (c.Kind is not (MaterialTaskKind.Stage or MaterialTaskKind.Kit) || c.RequesterPersonId is not { } requester || c.OperationSequence is null)
                    throw new CommandRejectedException("operator_request_context_required", "An operator request must identify an active operation and employee.");
                var identity = await uow.People.FindAsync(new PersonId(requester), ct);
                if (identity is not { IsActive: true } || !await uow.Store<IWorkforceLookup>().WorkerExistsAsync(requester, ct))
                    throw new CommandRejectedException("requester_not_found", "The requesting employee must be an active Forge worker.");
            }
            if (c.SourceSuspectReportId is { } reportId)
            {
                var report = await uow.Store<IQualityLookup>().FindSuspectScrapReportAsync(reportId, ct)
                    ?? throw new CommandRejectedException("suspect_report_not_found", "The source suspect report does not exist.");
                if (report.Kind != SuspectReportKind.Scrap || c.Kind is not (MaterialTaskKind.Stage or MaterialTaskKind.Kit)
                    || c.OrderNumber != report.OrderNumber || c.OperationSequence != report.Sequence || part != report.PartNumber
                    || quantity > report.Quantity)
                    throw new CommandRejectedException("replacement_request_context_mismatch", "A replacement request must match the reported scrap's work order, operation, part, and quantity, and use a staging or kit task.");
                if (await lookup.FindMaterialTaskBySuspectReportAsync(reportId, ct) is { } existing)
                    throw new CommandRejectedException("replacement_already_requested", $"Suspect report {reportId} is already linked to material task {existing.TaskId}.");
            }
            OrderInfo? order = null;
            string? kit = null;
            var isNonBom = false;
            switch (c.Kind)
            {
                case MaterialTaskKind.Stage or MaterialTaskKind.Kit:
                {
                    order = await ReleasedOrderAsync(uow, c.OrderNumber ?? throw new CommandRejectedException("order_required", "Staging and kitting are for a production order."), context, ct);
                    if (c.OperationSequence is { } sequence)
                    {
                        var operation = order.Operations.SingleOrDefault(candidate => candidate.Sequence == sequence)
                            ?? throw new CommandRejectedException("operation_not_found", $"Order {order.OrderNumber} has no operation {sequence}.");
                        if (to.WorkCenterCode is not null && to.WorkCenterCode != operation.WorkCenterCode)
                            throw new CommandRejectedException("wrong_work_center", $"Location {to.Code} is not at operation {sequence}'s work center {operation.WorkCenterCode}.");
                    }
                    if (c.OperatorInitiated)
                    {
                        var activeLabor = await uow.Store<IExecutionLookup>().ListOpenLaborAsync(ct);
                        if (!activeLabor.Any(labor => labor.PersonId == c.RequesterPersonId && labor.OrderNumber == order.OrderNumber
                            && labor.Sequence == c.OperationSequence!.Value && labor.Role != LaborRole.Trainer))
                            throw new CommandRejectedException("not_on_operation", "The employee must have an active shop-floor session on the requested work order operation.");
                    }
                    var required = order.Materials.Where(m => m.ComponentPartNumber == part).Sum(m => m.RequiredQuantity);
                    if (required == 0)
                    {
                        if (!c.AllowNonBomMaterial)
                            throw new CommandRejectedException("not_on_order", $"Order {order.OrderNumber} does not need {part}; an additional material request needs a reason and independent approval.");
                        isNonBom = true;
                    }

                    if (!to.PointOfUse && to.Kind is not LocationKind.Staging and not LocationKind.Production)
                    {
                        throw new CommandRejectedException("not_point_of_use", $"{to.Code} is a {to.Kind} location; material for an order goes to staging or the point of use.");
                    }

                    if (to.WorkCenterCode is { } wc && order.Operations.All(o => o.WorkCenterCode != wc))
                    {
                        throw new CommandRejectedException("wrong_work_center", $"{to.Code} serves {wc}; order {order.OrderNumber} has no operation there.");
                    }

                    await uow.Store<IInventoryStore>().LockPartAsync(part, ct);
                    var issued = (await lookup.ListTransactionsAsync(part, order.OrderNumber, 100_000, ct))
                        .Where(t => t.Kind is InventoryTransactionKind.Issue or InventoryTransactionKind.Return && t.SubstitutesFor is null)
                        .Sum(t => t.Kind == InventoryTransactionKind.Issue ? t.Quantity : -t.Quantity);
                    var requested = await lookup.ListMaterialTasksAsync(false, order.OrderNumber, ct);
                    var open = MaterialTaskDemand.Remaining(required, issued, requested.Where(task => task.PartNumber == part));
                    if (quantity > open)
                    {
                        if (!c.AllowNonBomMaterial)
                            throw new CommandRejectedException("over_request", $"Order {order.OrderNumber} has {Qty(Math.Max(0, open))} of released demand for {part} not already issued or tasked; an additional request needs independent approval.");
                        isNonBom = true;
                    }

                    if (c.Kind == MaterialTaskKind.Kit)
                    {
                        kit = BusinessCode.Require(c.KitCode ?? throw new CommandRejectedException("kit_required", "Give the kit a code."), "Kit");
                    }

                    break;
                }

                case MaterialTaskKind.PutAway:
                    if (to.Kind is not (LocationKind.Stores or LocationKind.WipStorage))
                    {
                        throw new CommandRejectedException("not_storage", $"{to.Code} is a {to.Kind} location; put-away goes to stores.");
                    }

                    break;
                case MaterialTaskKind.Replenish:
                    if (!to.PointOfUse && to.Kind != LocationKind.Staging)
                    {
                        throw new CommandRejectedException("not_point_of_use", $"{to.Code} is not a point-of-use or staging location.");
                    }

                    break;
                default:
                    throw new CommandRejectedException("invalid_kind", $"Unknown task kind {c.Kind}.");
            }

            var initialStatus = isNonBom ? MaterialTaskStatus.ApprovalNeeded : MaterialTaskStatus.Requested;
            if (isNonBom && c.FromLocation is not null)
                throw new CommandRejectedException("approval_before_allocation", "Additional material cannot be allocated or picked before independent review.");
            if (isNonBom && c.SourceSuspectReportId is not null)
                throw new CommandRejectedException("replacement_requires_released_bom_demand", "A linked scrap replacement request must match uncovered released-BOM demand; additional-material review is a separate workflow.");
            var task = new MaterialTask(0, c.Kind, order?.OrderNumber, kit, part, lot, quantity, null, to.Code, initialStatus, null, context.Reason, context.Actor, now,
                OperationSequence: c.OperationSequence, RequesterPersonId: c.RequesterPersonId, SourceSuspectReportId: c.SourceSuspectReportId, IsNonBom: isNonBom);
            if (Optional(c.FromLocation) is { } from)
            {
                task = await AllocatedAsync(uow, task, from, lot, context.Actor, now, ct);
            }

            var id = await uow.Store<IInventoryStore>().InsertMaterialTaskAsync(task, ct);
            var key = id.ToString(CultureInfo.InvariantCulture);
            return new CommandOutcome(
                CommandResult.Ok(key, $"Task {key}: {Qty(quantity)} {item.UnitOfMeasure} {part}{(lot is null ? string.Empty : " lot " + lot)} to {to.Code}{(order is null ? string.Empty : $" for order {order.OrderNumber}")} — {(isNonBom ? "awaiting independent planner / inventory approval" : task.Status.ToString().ToLowerInvariant())}{(task.FromLocation is null ? string.Empty : " from " + task.FromLocation)}."),
                [pipeline.Event(new MaterialTaskChanged(id, task.Status.ToString(), task.OrderNumber), context, SourceContext, TaskEntity, key,
                    order?.ProgramId is { } taskProgramId ? new ProgramId(taskProgramId) : null)],
                new AuditChange(TaskEntity, key, null, CanonicalJson.Serialize(task with { TaskId = id }),
                    order?.ProgramId is { } taskAuditProgramId ? new ProgramId(taskAuditProgramId) : null));
        }, ct);
    }

    public Task<CommandResult> AllocateMaterialAsync(AllocateMaterial c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeTaskAsync(nameof(AllocateMaterial), c.TaskId, c, context, MaterialTaskStatus.Requested, false, async (uow, task, now) =>
        {
            var item = await ItemAsync(uow, task.PartNumber, ct);
            var lot = Optional(c.LotNumber) is null ? task.LotNumber : RequireLot(item, c.LotNumber);
            if (task.LotNumber is not null && lot != task.LotNumber)
            {
                throw new CommandRejectedException("lot_mismatch", $"Task {task.TaskId} asks for lot {task.LotNumber}.");
            }

            if ((item.LotTracked || item.Serialized) && lot is null)
            {
                RequireLot(item, null);
            }

            var allocated = await AllocatedAsync(uow, task, c.FromLocation, lot, context.Actor, now, ct);
            return (allocated, $"Task {task.TaskId}: {Qty(task.Quantity)} {task.PartNumber}{(lot is null ? string.Empty : " lot " + lot)} allocated from {allocated.FromLocation}.");
        }, ct);
    }

    public Task<CommandResult> PickMaterialAsync(PickMaterial c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeTaskAsync(nameof(PickMaterial), c.TaskId, c, context, MaterialTaskStatus.Allocated, false, async (uow, task, now) =>
        {
            if (c.ScannedMaterial is { } material
                && (!string.Equals(material.PartNumber, task.PartNumber, StringComparison.Ordinal)
                    || task.LotNumber is null || !string.Equals(material.LotNumber, task.LotNumber, StringComparison.Ordinal)))
                throw new CommandRejectedException("scan_mismatch", "Scanned material does not match this task's part and lot.");
            Scanned(c.ScannedLocation, task.FromLocation!, "location");
            if (task.LotNumber is { } lot)
            {
                Scanned(c.ScannedLot, lot, "lot");
            }

            var item = await ItemAsync(uow, task.PartNumber, ct);
            var quantity = c.Quantity is { } q ? RequireQuantity(item, q) : task.Quantity;
            if (quantity > task.Quantity)
            {
                throw new CommandRejectedException("over_pick", $"Task {task.TaskId} is for {Qty(task.Quantity)}.");
            }

            await uow.Store<IInventoryStore>().LockPartAsync(task.PartNumber, ct);
            await RequireStockAsync(uow, task.PartNumber, task.FromLocation!, task.LotNumber, StockStatus.Released, quantity, ct);
            var picked = task with { Status = MaterialTaskStatus.Picked, PickedQuantity = quantity, PickedBy = context.Actor, PickedAt = now };
            return (picked, $"Task {task.TaskId}: picked {Qty(quantity)} {task.PartNumber} at {task.FromLocation}{(quantity < task.Quantity ? $" ({Qty(task.Quantity - quantity)} short)" : string.Empty)}.");
        }, ct);
    }

    public Task<CommandResult> DeliverMaterialAsync(DeliverMaterial c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeTaskAsync(nameof(DeliverMaterial), c.TaskId, c, context, MaterialTaskStatus.Picked, false, async (uow, task, now) =>
        {
            Scanned(c.ScannedLocation, task.ToLocation, "location");
            var store = uow.Store<IInventoryStore>();
            await store.LockPartAsync(task.PartNumber, ct);
            var quantity = task.PickedQuantity!.Value;
            await RequireStockAsync(uow, task.PartNumber, task.FromLocation!, task.LotNumber, StockStatus.Released, quantity, ct);
            await CompanyLocationAsync(uow, task.ToLocation, ct);
            var lookup = uow.Store<IInventoryLookup>();
            var locations = await lookup.ListLocationsAsync(ct);
            var destination = locations.FirstOrDefault(location => location.Code == task.ToLocation && location.Active)
                ?? throw new CommandRejectedException("destination_unavailable", $"Destination {task.ToLocation} is not active.");
            var transit = locations.Where(location => location.Active && location.SiteCode == destination.SiteCode && location.Kind == LocationKind.InTransit).ToArray();
            if (transit.Length != 1)
                throw new CommandRejectedException("in_transit_location_required", $"Site {destination.SiteCode} needs exactly one active In Transit location before a material task can leave Stores.");
            var inTransit = transit[0];
            if (inTransit.Nettable || inTransit.InCompanyCustody)
                throw new CommandRejectedException("invalid_in_transit_location", $"Location {inTransit.Code} must be non-nettable and marked In Transit custody.");
            var id = await store.InsertTransactionAsync(
                new NewTransaction(InventoryTransactionKind.Transfer, task.PartNumber, quantity, task.FromLocation, StockStatus.Released, inTransit.Code, StockStatus.Released, task.LotNumber,
                    OrderNumber: task.OrderNumber, Reason: $"Custody dispatch for material task {task.TaskId.ToString(CultureInfo.InvariantCulture)}"), now, context.Actor, ct);
            await store.InsertMaterialTaskCustodyEventAsync(new MaterialTaskCustodyEvent(0, task.TaskId, MaterialCustodyEventKind.DispatchedToTransit,
                now, context.Actor, context.Principal.PersonId.Value, inTransit.Code, id, "ForgeSession", null, quantity), ct);
            var delivered = task with { Status = MaterialTaskStatus.InTransit, DeliveredBy = context.Actor, DeliveredAt = now, TransactionId = id, TransitLocation = inTransit.Code };
            return (delivered, $"Task {task.TaskId}: {Qty(quantity)} {task.PartNumber} is in transit to {task.ToLocation}; receiver acknowledgment is required before it becomes usable there.");
        }, ct);
    }

    public Task<CommandResult> ReceiveMaterialAsync(ReceiveMaterial c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeTaskAsync(nameof(ReceiveMaterial), c.TaskId, c, context, MaterialTaskStatus.InTransit, false, async (uow, task, now) =>
        {
            if (c.ReceiverPersonId == Guid.Empty)
                throw new CommandRejectedException("receiver_authentication_required", "A receiver must authenticate with their own badge and PIN.");
            var receiver = await uow.People.FindAsync(new PersonId(c.ReceiverPersonId), ct);
            var workforce = uow.Store<IWorkforceLookup>();
            if (receiver is not { IsActive: true } || !await workforce.WorkerExistsAsync(c.ReceiverPersonId, ct))
                throw new CommandRejectedException("receiver_not_found", "The authenticated receiver must be an active Forge worker.");
            if (task.DeliveredBy == $"person:{c.ReceiverPersonId:D}")
                throw new CommandRejectedException("independent_receiver_required", "The person who dispatched the material cannot accept their own handoff.");
            Scanned(c.ScannedLocation, task.ToLocation, "destination");
            var locations = await uow.Store<IInventoryLookup>().ListLocationsAsync(ct);
            var transit = locations.FirstOrDefault(location => location.Code == task.TransitLocation && location.Active && location.Kind == LocationKind.InTransit)
                ?? throw new CommandRejectedException("transit_stock_unavailable", "The task's in-transit custody location is unavailable.");
            var destination = await CompanyLocationAsync(uow, task.ToLocation, ct);
            if (destination.SiteCode != transit.SiteCode)
                throw new CommandRejectedException("cross_site_handoff", "The receiver must accept material at the same site as its in-transit custody location.");
            var store = uow.Store<IInventoryStore>();
            await store.LockPartAsync(task.PartNumber, ct);
            var quarantine = c.RejectedQuantity > 0
                ? locations.FirstOrDefault(location => location.Code.Equals(c.QuarantineLocation?.Trim(), StringComparison.OrdinalIgnoreCase)
                    && location.Active && location.SiteCode == transit.SiteCode && location.Kind == LocationKind.Quarantine
                    && !location.Nettable && location.InCompanyCustody)
                    ?? throw new CommandRejectedException("quarantine_location_required", "Rejected material must be transferred to an active, non-nettable Quarantine location at the same site.")
                : null;
            if (c.RejectedQuantity > 0 && string.IsNullOrWhiteSpace(context.Reason))
                throw new CommandRejectedException("rejection_reason_required", "Record why the receiver rejected this quantity.");
            var picked = task.PickedQuantity ?? throw new CommandRejectedException("picked_quantity_missing", "The task has no recorded picked quantity.");
            var remaining = Math.Max(0m, picked - task.ReceivedQuantity - task.RejectedQuantity);
            if (c.AcceptedQuantity < 0 || c.RejectedQuantity < 0 || c.AcceptedQuantity + c.RejectedQuantity <= 0
                || c.AcceptedQuantity + c.RejectedQuantity > remaining)
                throw new CommandRejectedException("invalid_receipt_quantities", $"Enter a positive accepted or rejected quantity totaling no more than {Qty(remaining)} still in transit.");
            await RequireStockAsync(uow, task.PartNumber, transit.Code, task.LotNumber, StockStatus.Released, c.AcceptedQuantity + c.RejectedQuantity, ct);
            long? receiptTransaction = null;
            if (c.AcceptedQuantity > 0)
            {
                receiptTransaction = await store.InsertTransactionAsync(
                    new NewTransaction(InventoryTransactionKind.Transfer, task.PartNumber, c.AcceptedQuantity, transit.Code, StockStatus.Released, destination.Code, StockStatus.Released, task.LotNumber,
                        OrderNumber: task.OrderNumber, Reason: $"Receiver acknowledgment for material task {task.TaskId.ToString(CultureInfo.InvariantCulture)}"), now, context.Actor, ct);
                await store.InsertMaterialTaskCustodyEventAsync(new MaterialTaskCustodyEvent(0, task.TaskId,
                    c.AcceptedQuantity + c.RejectedQuantity == remaining ? MaterialCustodyEventKind.ReceivedAtDestination : MaterialCustodyEventKind.PartiallyReceived,
                    now, context.Actor, c.ReceiverPersonId, destination.Code, receiptTransaction, "BadgePin", context.Reason, c.AcceptedQuantity), ct);
            }
            if (c.RejectedQuantity > 0)
            {
                var rejectedTransaction = await store.InsertTransactionAsync(
                    new NewTransaction(InventoryTransactionKind.Transfer, task.PartNumber, c.RejectedQuantity, transit.Code, StockStatus.Released, quarantine!.Code, StockStatus.Hold, task.LotNumber,
                        OrderNumber: task.OrderNumber, Reason: $"Rejected material task {task.TaskId.ToString(CultureInfo.InvariantCulture)}: {context.Reason}"), now, context.Actor, ct);
                await store.InsertMaterialTaskCustodyEventAsync(new MaterialTaskCustodyEvent(0, task.TaskId, MaterialCustodyEventKind.RejectedAtReceipt,
                    now, context.Actor, c.ReceiverPersonId, quarantine.Code, rejectedTransaction, "BadgePin", context.Reason, c.RejectedQuantity), ct);
            }
            var receivedQuantity = task.ReceivedQuantity + c.AcceptedQuantity;
            var rejectedQuantity = task.RejectedQuantity + c.RejectedQuantity;
            var settled = receivedQuantity + rejectedQuantity >= picked;
            var nextStatus = settled
                ? receivedQuantity > 0 ? MaterialTaskStatus.Delivered : MaterialTaskStatus.DeliveryRejected
                : MaterialTaskStatus.InTransit;
            var updated = task with { Status = nextStatus, ReceivedBy = c.AcceptedQuantity > 0 ? context.Actor : task.ReceivedBy,
                ReceivedAt = c.AcceptedQuantity > 0 ? now : task.ReceivedAt, ReceiverPersonId = c.ReceiverPersonId,
                ReceiptTransactionId = receiptTransaction ?? task.ReceiptTransactionId, ReceivedQuantity = receivedQuantity, RejectedQuantity = rejectedQuantity };
            return (updated, $"Task {task.TaskId}: {Qty(c.AcceptedQuantity)} {task.PartNumber} accepted and {Qty(c.RejectedQuantity)} rejected; {Qty(picked - receivedQuantity - rejectedQuantity)} remains in transit.");
        }, ct);
    }

    public Task<CommandResult> ReturnReceivedMaterialAsync(ReturnReceivedMaterial c, CommandContext context, CancellationToken ct) =>
        AdjustReceivedMaterialAsync(nameof(ReturnReceivedMaterial), c.TaskId, c, c.Quantity, c.StoresLocation, MaterialCustodyEventKind.ReturnedToStores,
            Permissions.InventoryManage, StockStatus.Released, LocationKind.Stores, context, ct);

    public Task<CommandResult> ReverseMaterialReceiptAsync(ReverseMaterialReceipt c, CommandContext context, CancellationToken ct) =>
        AdjustReceivedMaterialAsync(nameof(ReverseMaterialReceipt), c.TaskId, c, c.Quantity, c.QuarantineLocation, MaterialCustodyEventKind.ReceiptReversed,
            Permissions.InventoryManage, StockStatus.Hold, LocationKind.Quarantine, context, ct);

    private Task<CommandResult> AdjustReceivedMaterialAsync<TCommand>(string commandName, long taskId, TCommand command, decimal quantity, string targetCode,
        MaterialCustodyEventKind eventKind, string permission, StockStatus targetStatus, LocationKind targetKind, CommandContext context, CancellationToken ct)
        where TCommand : notnull
    {
        var key = taskId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, commandName, permission, ResourceRef.Of(TaskEntity, key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var task = await uow.Store<IInventoryLookup>().FindMaterialTaskAsync(taskId, ct)
                ?? throw new CommandRejectedException("task_not_found", $"Task {key} does not exist.");
            if (task.Status != MaterialTaskStatus.Delivered)
                throw new CommandRejectedException("task_not_delivered", "Only a fully settled material task can be returned or corrected.");
            if (task.OrderNumber is { } orderNumber && await uow.Store<IExecutionLookup>().FindOrderAsync(orderNumber, ct) is { } order && !Visible(context, order))
                throw new CommandRejectedException("task_not_found", $"Task {key} does not exist.");
            if (quantity <= 0 || quantity > task.ReceivedQuantity - task.ReturnedQuantity - task.ReversedQuantity)
                throw new CommandRejectedException("invalid_correction_quantity", $"Quantity must be positive and no greater than {Qty(task.ReceivedQuantity - task.ReturnedQuantity - task.ReversedQuantity)} still held at the destination.");
            var locations = await uow.Store<IInventoryLookup>().ListLocationsAsync(ct);
            var from = locations.FirstOrDefault(location => location.Code == task.ToLocation && location.Active && location.InCompanyCustody)
                ?? throw new CommandRejectedException("destination_unavailable", "The original receiving location is not an active company-custody location.");
            var to = locations.FirstOrDefault(location => location.Code.Equals(targetCode?.Trim(), StringComparison.OrdinalIgnoreCase) && location.Active
                && location.SiteCode == from.SiteCode && location.Kind == targetKind && location.InCompanyCustody
                && (targetKind != LocationKind.Quarantine || !location.Nettable))
                ?? throw new CommandRejectedException("correction_location_invalid", targetKind == LocationKind.Stores
                    ? "Choose an active Stores location in the same site." : "Choose an active, non-nettable Quarantine location in the same site.");
            var store = uow.Store<IInventoryStore>();
            await store.LockPartAsync(task.PartNumber, ct);
            await RequireStockAsync(uow, task.PartNumber, from.Code, task.LotNumber, StockStatus.Released, quantity, ct);
            var transaction = await store.InsertTransactionAsync(new NewTransaction(InventoryTransactionKind.Transfer, task.PartNumber, quantity,
                from.Code, StockStatus.Released, to.Code, targetStatus, task.LotNumber, OrderNumber: task.OrderNumber,
                Reason: $"{eventKind} for material task {taskId.ToString(CultureInfo.InvariantCulture)}: {context.Reason}"), now, context.Actor, ct);
            var personId = context.Principal.PersonId.Value;
            var eventId = await store.InsertMaterialTaskCustodyEventAsync(new MaterialTaskCustodyEvent(0, taskId, eventKind, now, context.Actor,
                personId, to.Code, transaction, "ForgeSession", context.Reason, quantity), ct);
            var updated = eventKind == MaterialCustodyEventKind.ReturnedToStores
                ? task with { ReturnedQuantity = task.ReturnedQuantity + quantity }
                : task with { ReversedQuantity = task.ReversedQuantity + quantity };
            if (!await store.UpdateMaterialTaskAsync(updated, task, ct))
                throw new CommandRejectedException("wrong_step", $"Task {key} changed while the receipt correction was being recorded; look again.");
            var adjusted = pipeline.Event(new MaterialCustodyAdjusted(taskId, eventKind.ToString(), quantity, to.Code), context, SourceContext, TaskEntity, key);
            return new CommandOutcome(CommandResult.Ok(eventId.ToString(CultureInfo.InvariantCulture),
                    $"Task {taskId}: {Qty(quantity)} returned from {from.Code} to {to.Code}; original custody evidence remains in history."),
                [adjusted, pipeline.Event(new InventoryMoved(transaction, nameof(InventoryTransactionKind.Transfer), task.PartNumber, quantity, from.Code, to.Code),
                    context, SourceContext, StockEntity, task.PartNumber)], new AuditChange("MaterialTaskCustodyEvent", eventId.ToString(CultureInfo.InvariantCulture), null,
                    CanonicalJson.Serialize(new { taskId, kind = eventKind, quantity, from = from.Code, to = to.Code, transaction, reason = context.Reason })));
        }, ct);
    }

    public Task<CommandResult> ReportMaterialCustodyExceptionAsync(ReportMaterialCustodyException c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var key = c.TaskId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, nameof(ReportMaterialCustodyException), Permissions.InventoryCustodyReceive,
            ResourceRef.Of(TaskEntity, key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var lookup = uow.Store<IInventoryLookup>();
            var task = await lookup.FindMaterialTaskAsync(c.TaskId, ct)
                ?? throw new CommandRejectedException("task_not_found", $"Task {key} does not exist.");
            if (task.Status != MaterialTaskStatus.InTransit || task.TransitLocation is null)
                throw new CommandRejectedException("task_not_in_transit", "A custody exception can be recorded only while the task is in transit.");
            ProgramId? programId = null;
            if (task.OrderNumber is { } orderNumber)
            {
                var order = await uow.Store<IExecutionLookup>().FindOrderAsync(orderNumber, ct);
                if (order is null || !Visible(context, order)) throw new CommandRejectedException("task_not_found", $"Task {key} does not exist.");
                programId = order.ProgramId is { } id ? new ProgramId(id) : null;
            }
            if (c.ReporterPersonId == Guid.Empty)
                throw new CommandRejectedException("reporter_authentication_required", "A worker must authenticate with their own badge and PIN to report a custody exception.");
            var person = await uow.People.FindAsync(new PersonId(c.ReporterPersonId), ct);
            if (person is not { IsActive: true } || !await uow.Store<IWorkforceLookup>().WorkerExistsAsync(c.ReporterPersonId, ct))
                throw new CommandRejectedException("reporter_not_found", "The authenticated reporter must be an active Forge worker.");
            if (task.DeliveredBy == $"person:{c.ReporterPersonId:D}")
                throw new CommandRejectedException("independent_receiver_required", "The dispatcher cannot independently report the receiving exception.");
            var locations = await lookup.ListLocationsAsync(ct);
            var transit = locations.FirstOrDefault(location => location.Code == task.TransitLocation && location.Active && location.Kind == LocationKind.InTransit)
                ?? throw new CommandRejectedException("transit_stock_unavailable", "The task's in-transit custody location is unavailable.");
            var destination = locations.FirstOrDefault(location => location.Code == task.ToLocation && location.Active)
                ?? throw new CommandRejectedException("destination_unavailable", "The task destination is no longer active.");
            var observed = locations.FirstOrDefault(location => location.Code.Equals(c.ScannedLocation?.Trim(), StringComparison.OrdinalIgnoreCase) && location.Active
                && location.SiteCode == transit.SiteCode && (location.InCompanyCustody || location.Kind == LocationKind.InTransit))
                ?? throw new CommandRejectedException("exception_location_invalid", "Scan an active company or in-transit location at the task's site where the material was observed.");
            var eventId = await uow.Store<IInventoryStore>().InsertMaterialTaskCustodyEventAsync(new MaterialTaskCustodyEvent(0, task.TaskId,
                MaterialCustodyEventKind.ExceptionRecorded, now, context.Actor, c.ReporterPersonId, observed.Code, null, "BadgePin", context.Reason), ct);
            var evt = pipeline.Event(new MaterialCustodyExceptionReported(task.TaskId, c.ReporterPersonId, observed.Code), context, SourceContext,
                TaskEntity, key, programId);
            return new CommandOutcome(CommandResult.Ok(eventId.ToString(CultureInfo.InvariantCulture),
                    $"Custody exception {eventId} recorded for task {task.TaskId} at {observed.Code}. The material remains non-nettable in transit pending reconciliation."),
                [evt], new AuditChange("MaterialTaskCustodyEvent", eventId.ToString(CultureInfo.InvariantCulture), null,
                    CanonicalJson.Serialize(new { taskId = task.TaskId, kind = MaterialCustodyEventKind.ExceptionRecorded, observedLocation = observed.Code,
                        reporterPersonId = c.ReporterPersonId, reason = context.Reason, inventoryLocation = transit.Code, intendedDestination = destination.Code }), programId));
        }, ct);
    }

    public Task<CommandResult> CancelMaterialTaskAsync(CancelMaterialTask c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeTaskAsync(nameof(CancelMaterialTask), c.TaskId, c, context, null, true, (_, task, now) =>
        {
            if (task.Status is MaterialTaskStatus.InTransit or MaterialTaskStatus.Delivered or MaterialTaskStatus.DeliveryRejected or MaterialTaskStatus.Cancelled)
            {
                throw new CommandRejectedException("task_closed", task.Status == MaterialTaskStatus.InTransit
                    ? $"Task {task.TaskId} is in transit; reconcile the physical custody exception before changing it."
                    : $"Task {task.TaskId} is {task.Status.ToString().ToLowerInvariant()}.");
            }

            var cancelled = task with { Status = MaterialTaskStatus.Cancelled, CancelledBy = context.Actor, CancelledAt = now, CancelReason = context.Reason };
            return Task.FromResult((cancelled, $"Task {task.TaskId} cancelled; anything allocated is free again."));
        }, ct);
    }

    public Task<CommandResult> ReviewAdditionalMaterialRequestAsync(ReviewAdditionalMaterialRequest c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeTaskAsync(nameof(ReviewAdditionalMaterialRequest), c.TaskId, c, context, MaterialTaskStatus.ApprovalNeeded, true, (uow, task, now) =>
        {
            if (!task.IsNonBom)
                throw new CommandRejectedException("not_additional_material", "Only a controlled additional-material request can be reviewed here.");
            if (string.Equals(task.RequestedBy, context.Actor, StringComparison.Ordinal))
                throw new CommandRejectedException("independent_review_required", "The requester cannot approve or reject their own additional-material request.");
            var status = c.Approve ? MaterialTaskStatus.Requested : MaterialTaskStatus.Rejected;
            var changed = task with { Status = status, ReviewedBy = context.Actor, ReviewedAt = now, ReviewNote = context.Reason };
            return Task.FromResult((changed, c.Approve
                ? $"Additional material task {task.TaskId} approved and queued for stockroom fulfillment."
                : $"Additional material task {task.TaskId} rejected; its request remains in the audit history."));
        }, ct, Permissions.InventoryApprove);
    }

    public Task<CommandResult> CreateCountProgramAsync(CreateCountProgram c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var code = BusinessCode.Require(c.ProgramCode, "Program");
        var spec = new CommandSpec(SourceContext, nameof(CreateCountProgram), Permissions.InventoryManage, ResourceRef.Of("CountProgram", code));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var location = OptionalCode(c.LocationCode);
            var part = OptionalCode(c.PartNumber);
            if (location is null && part is null)
            {
                throw new CommandRejectedException("scope_required", "Count a location (and what is in it), a part, or a part in a location.");
            }

            if (location is not null)
            {
                await LocationAsync(uow, location, ct);
            }

            if (part is not null)
            {
                await ItemAsync(uow, part, ct);
            }

            if (c.FrequencyDays is < 1 or > 366)
            {
                throw new CommandRejectedException("invalid_frequency", "Count every 1 to 366 days.");
            }

            var program = new CycleCountProgram(code, BusinessCode.RequireName(c.Name, "Program"), location, part, c.FrequencyDays, true, context.Actor, now);
            if (!await uow.Store<IInventoryStore>().InsertCountProgramAsync(program, ct))
            {
                throw new CommandRejectedException("code_exists", $"Count program {code} already exists.");
            }

            return new CommandOutcome(
                CommandResult.Ok(code, $"Count program {code}: {(part ?? "every part")} in {(location ?? "every location")}, every {c.FrequencyDays.ToString(CultureInfo.InvariantCulture)} day(s)."),
                [pipeline.Event(new CountProgramChanged(code, true), context, SourceContext, "CountProgram", code)],
                new AuditChange("CountProgram", code, null, CanonicalJson.Serialize(program)));
        }, ct);
    }

    public Task<CommandResult> RetireCountProgramAsync(RetireCountProgram c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var code = BusinessCode.Require(c.ProgramCode, "Program");
        var spec = new CommandSpec(SourceContext, nameof(RetireCountProgram), Permissions.InventoryManage, ResourceRef.Of("CountProgram", code), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var program = (await uow.Store<IInventoryLookup>().ListCountProgramsAsync(ct)).FirstOrDefault(p => p.ProgramCode == code)
                ?? throw new CommandRejectedException("program_not_found", $"Count program {code} does not exist.");
            if (!program.Active)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(code, $"Count program {code} is already retired."));
            }

            await uow.Store<IInventoryStore>().RetireCountProgramAsync(code, context.Actor, now, ct);
            return new CommandOutcome(
                CommandResult.Ok(code, $"Count program {code} retired."),
                [pipeline.Event(new CountProgramChanged(code, false), context, SourceContext, "CountProgram", code)],
                new AuditChange("CountProgram", code, CanonicalJson.Serialize(program), CanonicalJson.Serialize(program with { Active = false, RetiredBy = context.Actor, RetiredAt = now })));
        }, ct);
    }

    private Task<CommandResult> ChangeTaskAsync<TCommand>(string name, long taskId, TCommand command, CommandContext context, MaterialTaskStatus? expected, bool reasonRequired,
        Func<IKernelUnitOfWork, MaterialTask, DateTimeOffset, Task<(MaterialTask Changed, string Message)>> change, CancellationToken ct, string? permission = null)
        where TCommand : notnull
    {
        var key = taskId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, name, permission ?? (name == nameof(ReceiveMaterial) ? Permissions.InventoryCustodyReceive : Permissions.InventoryTransact),
            ResourceRef.Of(TaskEntity, key), ReasonRequired: reasonRequired);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var task = await uow.Store<IInventoryLookup>().FindMaterialTaskAsync(taskId, ct)
                ?? throw new CommandRejectedException("task_not_found", $"Task {key} does not exist.");
            ProgramId? programId = null;
            if (task.OrderNumber is { } orderNumber && await uow.Store<IExecutionLookup>().FindOrderAsync(orderNumber, ct) is { } order)
            {
                if (!Visible(context, order)) throw new CommandRejectedException("task_not_found", $"Task {key} does not exist.");
                programId = order.ProgramId is { } id ? new ProgramId(id) : null;
            }

            if (expected is { } status && task.Status != status)
            {
                throw new CommandRejectedException("wrong_step", $"Task {key} is {task.Status.ToString().ToLowerInvariant()}; this step needs it {status.ToString().ToLowerInvariant()}.");
            }

            var (changed, message) = await change(uow, task, now);
            if (!await uow.Store<IInventoryStore>().UpdateMaterialTaskAsync(changed, task, ct))
            {
                throw new CommandRejectedException("wrong_step", $"Task {key} changed while you were working on it; look again.");
            }

            var events = new List<EventEnvelope>
            {
                pipeline.Event(new MaterialTaskChanged(taskId, changed.Status.ToString(), changed.OrderNumber), context, SourceContext, TaskEntity, key, programId),
            };
            if (task.Status == MaterialTaskStatus.ApprovalNeeded && changed.Status != MaterialTaskStatus.ApprovalNeeded)
                events.Add(pipeline.Event(new MaterialTaskReviewed(taskId, changed.Status.ToString()), context, SourceContext, TaskEntity, key, programId));
            if (changed.Status is MaterialTaskStatus.Delivered or MaterialTaskStatus.DeliveryRejected or MaterialTaskStatus.Rejected or MaterialTaskStatus.Cancelled)
                events.Add(pipeline.Event(new MaterialTaskClosed(taskId, changed.Status.ToString()), context, SourceContext, TaskEntity, key, programId));
            if (changed.ReceiverPersonId is { } receiver && changed.Status is (MaterialTaskStatus.Delivered or MaterialTaskStatus.DeliveryRejected))
            {
                if (changed.ReceivedQuantity > 0 && changed.ReceiptTransactionId is { } receipt)
                    events.Add(pipeline.Event(new MaterialCustodyAccepted(taskId, receiver, changed.ToLocation, receipt), context, SourceContext, TaskEntity, key, programId));
            }
            if (changed.Status == MaterialTaskStatus.InTransit && changed.TransactionId is { } dispatch && changed.TransactionId != task.TransactionId)
                events.Add(pipeline.Event(new InventoryMoved(dispatch, nameof(InventoryTransactionKind.Transfer), changed.PartNumber, changed.PickedQuantity ?? changed.Quantity,
                    changed.FromLocation, changed.TransitLocation), context, SourceContext, StockEntity, changed.PartNumber, programId));
            if (changed.ReceivedQuantity > task.ReceivedQuantity)
            {
                var custodyEvents = await uow.Store<IInventoryLookup>().ListMaterialTaskCustodyEventsAsync(taskId, ct);
                var receiptEvent = custodyEvents.LastOrDefault(e => e.Kind is MaterialCustodyEventKind.ReceivedAtDestination or MaterialCustodyEventKind.PartiallyReceived);
                if (receiptEvent?.TransactionId is { } receiptId)
                    events.Add(pipeline.Event(new InventoryMoved(receiptId, nameof(InventoryTransactionKind.Transfer), changed.PartNumber,
                        changed.ReceivedQuantity - task.ReceivedQuantity, changed.TransitLocation, changed.ToLocation), context, SourceContext, StockEntity, changed.PartNumber, programId));
            }
            if (changed.RejectedQuantity > task.RejectedQuantity)
            {
                var custodyEvents = await uow.Store<IInventoryLookup>().ListMaterialTaskCustodyEventsAsync(taskId, ct);
                var rejectedEvent = custodyEvents.LastOrDefault(e => e.Kind == MaterialCustodyEventKind.RejectedAtReceipt);
                if (rejectedEvent is not null)
                {
                    if (rejectedEvent.TransactionId is { } rejectionTransaction)
                        events.Add(pipeline.Event(new InventoryMoved(rejectionTransaction, nameof(InventoryTransactionKind.Transfer), changed.PartNumber,
                            changed.RejectedQuantity - task.RejectedQuantity, changed.TransitLocation, rejectedEvent.LocationCode), context, SourceContext, StockEntity, changed.PartNumber, programId));
                    if (changed.ReceiverPersonId is { } reporter)
                        events.Add(pipeline.Event(new MaterialCustodyDeliveryDiscrepancy(taskId, reporter,
                            changed.RejectedQuantity - task.RejectedQuantity, rejectedEvent.LocationCode), context, SourceContext, TaskEntity, key, programId));
                }
            }
            if (changed.ReceiverPersonId is not null && changed.Status is (MaterialTaskStatus.Delivered or MaterialTaskStatus.DeliveryRejected))
                events.Add(pipeline.Event(new MaterialCustodyResolved(taskId, changed.Status.ToString()), context, SourceContext, TaskEntity, key, programId));
            return new CommandOutcome(
                CommandResult.Ok(key, message),
                events,
                new AuditChange(TaskEntity, key, CanonicalJson.Serialize(task), CanonicalJson.Serialize(changed)));
        }, ct);
    }

    /// <summary>Holds stock for the task: released, in the company's custody, not already held by another task.</summary>
    private static async Task<MaterialTask> AllocatedAsync(IKernelUnitOfWork uow, MaterialTask task, string rawFrom, string? lot, string actor, DateTimeOffset now, CancellationToken ct)
    {
        var from = await CompanyLocationAsync(uow, rawFrom, ct);
        if (from.Code == task.ToLocation)
        {
            throw new CommandRejectedException("same_location", $"The material is already at {from.Code}.");
        }

        var store = uow.Store<IInventoryStore>();
        await store.LockPartAsync(task.PartNumber, ct);
        var balance = await store.BalanceAsync(task.PartNumber, from.Code, lot, StockStatus.Released, ct);
        var held = (await uow.Store<IInventoryLookup>().ListMaterialTasksAsync(true, null, ct))
            .Where(t => t.TaskId != task.TaskId && t.PartNumber == task.PartNumber && t.FromLocation == from.Code && t.LotNumber == lot)
            .Sum(t => t.Reserved);
        if (balance - held < task.Quantity)
        {
            throw new CommandRejectedException("insufficient_unallocated", $"{from.Code} has {Qty(balance)} released {task.PartNumber}{(lot is null ? string.Empty : " lot " + lot)}, {Qty(held)} of it held for other tasks; {Qty(task.Quantity)} needed.");
        }

        return task with { Status = MaterialTaskStatus.Allocated, FromLocation = from.Code, LotNumber = lot, AllocatedBy = actor, AllocatedAt = now };
    }

    /// <summary>A scan confirms the step only when it reads what the task says.</summary>
    private static void Scanned(string? scanned, string expected, string what)
    {
        if (!string.Equals(scanned?.Trim(), expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new CommandRejectedException("scan_mismatch", $"The scanned {what} does not match the material task.");
        }
    }
}
