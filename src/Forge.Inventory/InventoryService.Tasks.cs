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

namespace Forge.Inventory;

/// <param name="FromLocation">Where to take it from, when already known: the task starts allocated.</param>
public sealed record RequestMaterial(MaterialTaskKind Kind, long? OrderNumber, string PartNumber, decimal Quantity, string ToLocation, string? LotNumber = null, string? FromLocation = null, string? KitCode = null);

public sealed record AllocateMaterial(long TaskId, string FromLocation, string? LotNumber);

/// <param name="Quantity">What was actually picked; less than the task when the location was short.</param>
public sealed record PickMaterial(long TaskId, string ScannedLocation, string? ScannedLot, decimal? Quantity = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ScannedMaterialIdentity? ScannedMaterial = null);

public sealed record DeliverMaterial(long TaskId, string ScannedLocation);

public sealed record CancelMaterialTask(long TaskId);

public sealed record CreateCountProgram(string ProgramCode, string Name, string? LocationCode, string? PartNumber, int FrequencyDays);

public sealed record RetireCountProgram(string ProgramCode);

public sealed record MaterialTaskChanged(long TaskId, string Status, long? OrderNumber) : IDomainEvent
{
    public static string EventType => nameof(MaterialTaskChanged);

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

    public Task<CommandResult> RequestMaterialAsync(RequestMaterial c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var spec = new CommandSpec(SourceContext, nameof(RequestMaterial), Permissions.InventoryTransact, ResourceRef.Of(StockEntity, part));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var item = await ItemAsync(uow, part, ct);
            var quantity = RequireQuantity(item, c.Quantity);
            var to = await CompanyLocationAsync(uow, c.ToLocation, ct);
            var lot = Optional(c.LotNumber) is null ? null : RequireLot(item, c.LotNumber);
            var lookup = uow.Store<IInventoryLookup>();
            OrderInfo? order = null;
            string? kit = null;
            switch (c.Kind)
            {
                case MaterialTaskKind.Stage or MaterialTaskKind.Kit:
                {
                    order = await ReleasedOrderAsync(uow, c.OrderNumber ?? throw new CommandRejectedException("order_required", "Staging and kitting are for a production order."), context, ct);
                    var required = order.Materials.Where(m => m.ComponentPartNumber == part).Sum(m => m.RequiredQuantity);
                    if (required == 0)
                    {
                        throw new CommandRejectedException("not_on_order", $"Order {order.OrderNumber} does not need {part}.");
                    }

                    if (!to.PointOfUse && to.Kind is not LocationKind.Staging and not LocationKind.Production)
                    {
                        throw new CommandRejectedException("not_point_of_use", $"{to.Code} is a {to.Kind} location; material for an order goes to staging or the point of use.");
                    }

                    if (to.WorkCenterCode is { } wc && order.Operations.All(o => o.WorkCenterCode != wc))
                    {
                        throw new CommandRejectedException("wrong_work_center", $"{to.Code} serves {wc}; order {order.OrderNumber} has no operation there.");
                    }

                    var issued = (await lookup.ListTransactionsAsync(part, order.OrderNumber, 100_000, ct))
                        .Where(t => t.Kind is InventoryTransactionKind.Issue or InventoryTransactionKind.Return && t.SubstitutesFor is null)
                        .Sum(t => t.Kind == InventoryTransactionKind.Issue ? t.Quantity : -t.Quantity);
                    var requested = (await lookup.ListMaterialTasksAsync(false, order.OrderNumber, ct))
                        .Where(t => t.PartNumber == part && t.Status != MaterialTaskStatus.Cancelled)
                        .Sum(t => t.Status == MaterialTaskStatus.Delivered ? t.PickedQuantity ?? t.Quantity : t.Quantity);
                    var open = required - Math.Max(issued, requested);
                    if (quantity > open)
                    {
                        throw new CommandRejectedException("over_request", $"Order {order.OrderNumber} needs {Qty(required)} {part}; {Qty(Math.Max(issued, requested))} is already requested or issued, so at most {Qty(Math.Max(0, open))} more.");
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

            var task = new MaterialTask(0, c.Kind, order?.OrderNumber, kit, part, lot, quantity, null, to.Code, MaterialTaskStatus.Requested, null, context.Reason, context.Actor, now);
            if (Optional(c.FromLocation) is { } from)
            {
                task = await AllocatedAsync(uow, task, from, lot, context.Actor, now, ct);
            }

            var id = await uow.Store<IInventoryStore>().InsertMaterialTaskAsync(task, ct);
            var key = id.ToString(CultureInfo.InvariantCulture);
            return new CommandOutcome(
                CommandResult.Ok(key, $"Task {key}: {Qty(quantity)} {item.UnitOfMeasure} {part}{(lot is null ? string.Empty : " lot " + lot)} to {to.Code}{(order is null ? string.Empty : $" for order {order.OrderNumber}")} — {task.Status.ToString().ToLowerInvariant()}{(task.FromLocation is null ? string.Empty : " from " + task.FromLocation)}."),
                [pipeline.Event(new MaterialTaskChanged(id, task.Status.ToString(), task.OrderNumber), context, SourceContext, TaskEntity, key)],
                new AuditChange(TaskEntity, key, null, CanonicalJson.Serialize(task with { TaskId = id })));
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
            var id = await store.InsertTransactionAsync(
                new NewTransaction(InventoryTransactionKind.Transfer, task.PartNumber, quantity, task.FromLocation, StockStatus.Released, task.ToLocation, StockStatus.Released, task.LotNumber,
                    OrderNumber: task.OrderNumber, Reason: $"{task.Kind} task {task.TaskId.ToString(CultureInfo.InvariantCulture)}"), now, context.Actor, ct);
            var delivered = task with { Status = MaterialTaskStatus.Delivered, DeliveredBy = context.Actor, DeliveredAt = now, TransactionId = id };
            return (delivered, $"Task {task.TaskId}: delivered {Qty(quantity)} {task.PartNumber} to {task.ToLocation}.");
        }, ct);
    }

    public Task<CommandResult> CancelMaterialTaskAsync(CancelMaterialTask c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeTaskAsync(nameof(CancelMaterialTask), c.TaskId, c, context, null, true, (_, task, now) =>
        {
            if (task.Status is MaterialTaskStatus.Delivered or MaterialTaskStatus.Cancelled)
            {
                throw new CommandRejectedException("task_closed", $"Task {task.TaskId} is {task.Status.ToString().ToLowerInvariant()}.");
            }

            var cancelled = task with { Status = MaterialTaskStatus.Cancelled, CancelledBy = context.Actor, CancelledAt = now, CancelReason = context.Reason };
            return Task.FromResult((cancelled, $"Task {task.TaskId} cancelled; anything allocated is free again."));
        }, ct);
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
        Func<IKernelUnitOfWork, MaterialTask, DateTimeOffset, Task<(MaterialTask Changed, string Message)>> change, CancellationToken ct)
        where TCommand : notnull
    {
        var key = taskId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, name, Permissions.InventoryTransact, ResourceRef.Of(TaskEntity, key), ReasonRequired: reasonRequired);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var task = await uow.Store<IInventoryLookup>().FindMaterialTaskAsync(taskId, ct)
                ?? throw new CommandRejectedException("task_not_found", $"Task {key} does not exist.");
            if (task.OrderNumber is { } orderNumber && await uow.Store<IExecutionLookup>().FindOrderAsync(orderNumber, ct) is { } order && !Visible(context, order))
            {
                throw new CommandRejectedException("task_not_found", $"Task {key} does not exist.");
            }

            if (expected is { } status && task.Status != status)
            {
                throw new CommandRejectedException("wrong_step", $"Task {key} is {task.Status.ToString().ToLowerInvariant()}; this step needs it {status.ToString().ToLowerInvariant()}.");
            }

            var (changed, message) = await change(uow, task, now);
            if (!await uow.Store<IInventoryStore>().UpdateMaterialTaskAsync(changed, task.Status, ct))
            {
                throw new CommandRejectedException("wrong_step", $"Task {key} changed while you were working on it; look again.");
            }

            return new CommandOutcome(
                CommandResult.Ok(key, message),
                [pipeline.Event(new MaterialTaskChanged(taskId, changed.Status.ToString(), changed.OrderNumber), context, SourceContext, TaskEntity, key)],
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
