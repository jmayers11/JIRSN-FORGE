using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Resources.Contracts;

namespace Forge.Execution;

/// <summary>A new operation an amendment adds. Its planned minutes are set-up plus run time for the order's quantity.</summary>
public sealed record AmendedOperation(int Sequence, string Description, string WorkCenterCode, string ActivityCode, IReadOnlyList<string>? EligibleMachines,
    IReadOnlyList<string>? RequiredQualifications, decimal SetupMinutes, decimal RunMinutesPerUnit, int CrewSize);

public sealed record AmendedMaterial(string ComponentPartNumber, decimal RequiredQuantity, int? ConsumedAtSequence);

public sealed record MaterialQuantityChange(int LineNumber, decimal RequiredQuantity);

/// <summary>
/// What an amendment changes. Operations are removed or added only where no work has started; reordering is removing a pending operation
/// and adding it back at a new sequence. A quantity change rescales the run time of pending operations and the material of unchanged lines.
/// </summary>
public sealed record AmendmentChanges(
    decimal? NewQuantity = null,
    DateOnly? NewDueDate = null,
    int? NewPriority = null,
    IReadOnlyList<int>? RemoveOperations = null,
    IReadOnlyList<AmendedOperation>? AddOperations = null,
    IReadOnlyList<int>? RemoveMaterialLines = null,
    IReadOnlyList<AmendedMaterial>? AddMaterials = null,
    IReadOnlyList<MaterialQuantityChange>? ChangeMaterials = null);

public enum AmendmentStatus { Proposed, Approved, Rejected, Withdrawn }

/// <param name="Basis">A fingerprint of the order as proposed against; approval refuses an amendment whose order has since changed.</param>
public sealed record OrderAmendment(long AmendmentId, long OrderNumber, AmendmentStatus Status, AmendmentChanges Changes, string Rationale, string Basis,
    string ProposedBy, DateTimeOffset ProposedAt, string? DecidedBy, DateTimeOffset? DecidedAt, string? DecisionNote);

public sealed record ProposeOrderAmendment(long OrderNumber, AmendmentChanges Changes, string Rationale);

public sealed record DecideOrderAmendment(long AmendmentId, bool Approve, string? Note);

public sealed record WithdrawOrderAmendment(long AmendmentId);

public sealed record OrderAmendmentChanged(long AmendmentId, long OrderNumber, string Change) : IDomainEvent
{
    public static string EventType => nameof(OrderAmendmentChanged);

    public static int SchemaVersion => 1;
}

/// <summary>An approved amendment changed a released order: operations, materials, quantity, due date or priority.</summary>
public sealed record OrderAmended(long OrderNumber, long AmendmentId, string Summary) : IDomainEvent
{
    public static string EventType => nameof(OrderAmended);

    public static int SchemaVersion => 1;
}

public interface IOrderAmendmentStore
{
    Task<long> InsertAmendmentAsync(OrderAmendment amendment, CancellationToken ct);

    Task<OrderAmendment?> FindAmendmentAsync(long amendmentId, CancellationToken ct);

    Task<IReadOnlyList<OrderAmendment>> ListAmendmentsAsync(long? orderNumber, CancellationToken ct);

    Task<bool> DecideAmendmentAsync(long amendmentId, AmendmentStatus status, DateTimeOffset at, string actor, string? note, CancellationToken ct);

    Task LockOrderAsync(long orderNumber, CancellationToken ct);

    /// <summary>Removes a pending operation nothing refers to; rejects one that labor, reports, rework or requests already name.</summary>
    Task DeletePendingOperationAsync(long orderNumber, int sequence, CancellationToken ct);

    Task InsertOperationAsync(long orderNumber, OrderOperationInfo operation, CancellationToken ct);

    Task SetPlannedMinutesAsync(long orderNumber, int sequence, decimal plannedMinutes, CancellationToken ct);

    Task DeleteMaterialLineAsync(long orderNumber, int lineNumber, CancellationToken ct);

    Task InsertMaterialAsync(long orderNumber, OrderMaterialInfo material, CancellationToken ct);

    Task SetMaterialQuantityAsync(long orderNumber, int lineNumber, decimal requiredQuantity, CancellationToken ct);

    Task SetQuantityDueAndPriorityAsync(long orderNumber, decimal quantity, DateOnly dueDate, int priority, DateTimeOffset at, string actor, CancellationToken ct);
}

/// <summary>How much of each component has been issued to an order, net of returns. Inventory owns issues; the host implements this.</summary>
public interface IOrderIssueLookup
{
    Task<IReadOnlyDictionary<string, decimal>> NetIssuedAsync(IKernelUnitOfWork uow, long orderNumber, CancellationToken ct);
}

/// <summary>
/// Released-order amendments (owner decisions 2026-10-31). A Manufacturing Engineer proposes a change to a released or held order: add,
/// remove or reorder operations that have not started, add, remove or change material not yet issued, change the quantity (never below what
/// was produced), the due date or the priority. A Planner who did not propose it approves it, and approval applies it in the same
/// transaction, provided the order has not changed since the proposal. Completed work and issued material are never rewritten.
/// </summary>
public sealed class OrderAmendmentService(CommandPipeline pipeline, IOrderIssueLookup issues)
{
    private const string Entity = "OrderAmendment";

    public Task<CommandResult> ProposeAsync(ProposeOrderAmendment c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var orderId = c.OrderNumber.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(ProductionOrderService.SourceContext, nameof(ProposeOrderAmendment), Permissions.OrderAmendPropose, ResourceRef.Of("ProductionOrder", orderId));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var order = await VisibleOrderAsync(uow, context, c.OrderNumber, ct);
            var rationale = (c.Rationale ?? string.Empty).Trim();
            if (rationale.Length is 0 or > 2000) throw new CommandRejectedException("rationale_required", "Say why the order must change (at most 2000 characters).");
            var changes = c.Changes ?? new AmendmentChanges();
            var plan = await PlanAsync(uow, order, changes, now, ct); // validates now; approval validates again
            var amendment = new OrderAmendment(0, order.OrderNumber, AmendmentStatus.Proposed, changes, rationale, Basis(order), context.Actor, now, null, null, null);
            var id = await uow.Store<IOrderAmendmentStore>().InsertAmendmentAsync(amendment, ct);
            var key = id.ToString(CultureInfo.InvariantCulture);
            var program = order.ProgramId is { } p ? new ProgramId(p) : (ProgramId?)null;
            return new CommandOutcome(
                CommandResult.Ok(key, $"Amendment {key} to order {orderId} proposed: {plan.Summary}. A planner who did not propose it approves it."),
                [pipeline.Event(new OrderAmendmentChanged(id, order.OrderNumber, "Proposed"), context, ProductionOrderService.SourceContext, Entity, key, program)],
                new AuditChange(Entity, key, null, CanonicalJson.Serialize(amendment with { AmendmentId = id }), program));
        }, ct);
    }

    public Task<CommandResult> DecideAsync(DecideOrderAmendment c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var key = c.AmendmentId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(ProductionOrderService.SourceContext, nameof(DecideOrderAmendment), Permissions.OrderAmendApprove, ResourceRef.Of(Entity, key), ReasonRequired: !c.Approve);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var store = uow.Store<IOrderAmendmentStore>();
            var amendment = await store.FindAmendmentAsync(c.AmendmentId, ct) ?? throw new CommandRejectedException("amendment_not_found", $"Amendment {key} does not exist.");
            await store.LockOrderAsync(amendment.OrderNumber, ct);
            var order = await VisibleOrderAsync(uow, context, amendment.OrderNumber, ct);
            if (amendment.Status != AmendmentStatus.Proposed) throw new CommandRejectedException("amendment_decided", $"Amendment {key} is {amendment.Status}.");
            if (amendment.ProposedBy == context.Actor)
                throw new CommandRejectedException("separation_of_duties", "You proposed this amendment; a different person must decide it.");
            var program = order.ProgramId is { } p ? new ProgramId(p) : (ProgramId?)null;
            var note = string.IsNullOrWhiteSpace(c.Note) ? context.Reason : c.Note.Trim();
            if (!c.Approve)
            {
                await store.DecideAmendmentAsync(amendment.AmendmentId, AmendmentStatus.Rejected, now, context.Actor, note, ct);
                return new CommandOutcome(CommandResult.Ok(key, $"Amendment {key} rejected; order {order.OrderNumber} is unchanged."),
                    [pipeline.Event(new OrderAmendmentChanged(amendment.AmendmentId, order.OrderNumber, "Rejected"), context, ProductionOrderService.SourceContext, Entity, key, program)],
                    new AuditChange(Entity, key, nameof(AmendmentStatus.Proposed), nameof(AmendmentStatus.Rejected), program));
            }

            if (Basis(order) != amendment.Basis)
                throw new CommandRejectedException("order_changed", $"Order {order.OrderNumber} changed after amendment {key} was proposed (work started, quantities reported or another change). Reject it and propose again against the current order.");
            var plan = await PlanAsync(uow, order, amendment.Changes, now, ct);
            await ApplyAsync(store, order, plan, now, context.Actor, ct);
            await store.DecideAmendmentAsync(amendment.AmendmentId, AmendmentStatus.Approved, now, context.Actor, note, ct);
            var before = CanonicalJson.Serialize(new { order.Quantity, order.DueDate, order.Priority, operations = order.Operations.Select(o => $"{o.Sequence} {o.WorkCenterCode} {o.Description} {o.PlannedMinutes}"), materials = order.Materials.Select(m => $"{m.LineNumber} {m.ComponentPartNumber} {m.RequiredQuantity}") });
            return new CommandOutcome(
                CommandResult.Ok(key, $"Amendment {key} approved and applied to order {order.OrderNumber}: {plan.Summary}."),
                [
                    pipeline.Event(new OrderAmendmentChanged(amendment.AmendmentId, order.OrderNumber, "Approved"), context, ProductionOrderService.SourceContext, Entity, key, program),
                    pipeline.Event(new OrderAmended(order.OrderNumber, amendment.AmendmentId, plan.Summary), context, ProductionOrderService.SourceContext, "ProductionOrder", order.OrderNumber.ToString(CultureInfo.InvariantCulture), program),
                ],
                new AuditChange("ProductionOrder", order.OrderNumber.ToString(CultureInfo.InvariantCulture), before, CanonicalJson.Serialize(new { amendment = amendment.AmendmentId, amendment.Changes }), program));
        }, ct);
    }

    public Task<CommandResult> WithdrawAsync(WithdrawOrderAmendment c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var key = c.AmendmentId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(ProductionOrderService.SourceContext, nameof(WithdrawOrderAmendment), Permissions.OrderAmendPropose, ResourceRef.Of(Entity, key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var store = uow.Store<IOrderAmendmentStore>();
            var amendment = await store.FindAmendmentAsync(c.AmendmentId, ct) ?? throw new CommandRejectedException("amendment_not_found", $"Amendment {key} does not exist.");
            var order = await VisibleOrderAsync(uow, context, amendment.OrderNumber, ct);
            if (amendment.ProposedBy != context.Actor) throw new CommandRejectedException("not_proposer", "Only the person who proposed an amendment withdraws it.");
            if (!await store.DecideAmendmentAsync(amendment.AmendmentId, AmendmentStatus.Withdrawn, now, context.Actor, context.Reason, ct))
                throw new CommandRejectedException("amendment_decided", $"Amendment {key} is {amendment.Status}.");
            var program = order.ProgramId is { } p ? new ProgramId(p) : (ProgramId?)null;
            return new CommandOutcome(CommandResult.Ok(key, $"Amendment {key} withdrawn."),
                [pipeline.Event(new OrderAmendmentChanged(amendment.AmendmentId, order.OrderNumber, "Withdrawn"), context, ProductionOrderService.SourceContext, Entity, key, program)],
                new AuditChange(Entity, key, nameof(AmendmentStatus.Proposed), nameof(AmendmentStatus.Withdrawn), program));
        }, ct);
    }

    /// <summary>A fingerprint of what an amendment depends on: status, quantity, dates, priority, operations with their progress, and material.</summary>
    public static string Basis(OrderInfo order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return CanonicalJson.Fingerprint(new
        {
            order.Status, order.Quantity, order.DueDate, order.Priority,
            operations = order.Operations.OrderBy(o => o.Sequence).Select(o => new { o.Sequence, o.WorkCenterCode, o.Status, o.GoodQuantity, o.ScrapQuantity, o.PlannedMinutes }),
            materials = order.Materials.OrderBy(m => m.LineNumber).Select(m => new { m.LineNumber, m.ComponentPartNumber, m.RequiredQuantity, m.ConsumedAtSequence }),
        });
    }

    private sealed record Plan(decimal Quantity, DateOnly DueDate, int Priority, IReadOnlyList<int> RemoveOperations, IReadOnlyList<OrderOperationInfo> AddOperations,
        IReadOnlyList<(int Sequence, decimal Minutes)> Rescaled, IReadOnlyList<int> RemoveMaterials, IReadOnlyList<OrderMaterialInfo> AddMaterials,
        IReadOnlyList<(int Line, decimal Quantity)> MaterialQuantities, string Summary);

    private async Task<Plan> PlanAsync(IKernelUnitOfWork uow, OrderInfo order, AmendmentChanges changes, DateTimeOffset now, CancellationToken ct)
    {
        if (order.Status is not (OrderStatus.Released or OrderStatus.Held))
            throw new CommandRejectedException("not_released", $"Order {order.OrderNumber} is {order.Status}; a planned order is changed directly, a finished one not at all.");
        var said = new List<string>();
        var removeOps = (changes.RemoveOperations ?? []).Distinct().ToList();
        var addOps = changes.AddOperations ?? [];
        var removeLines = (changes.RemoveMaterialLines ?? []).Distinct().ToList();
        var addLines = changes.AddMaterials ?? [];
        var changeLines = changes.ChangeMaterials ?? [];
        if (changes.NewQuantity is null && changes.NewDueDate is null && changes.NewPriority is null && removeOps.Count + addOps.Count + removeLines.Count + addLines.Count + changeLines.Count == 0)
            throw new CommandRejectedException("nothing_to_change", "The amendment changes nothing.");

        // Quantity: never below what the order's last routed operation has produced.
        var routed = order.Routed();
        var produced = routed.Count == 0 ? 0 : routed[^1].GoodQuantity;
        var quantity = changes.NewQuantity ?? order.Quantity;
        if (quantity <= 0 || quantity > 100_000_000) throw new CommandRejectedException("invalid_quantity", "The order quantity must be above zero.");
        if (quantity < produced) throw new CommandRejectedException("below_produced", $"Order {order.OrderNumber} has already produced {produced}; the quantity cannot go below that.");
        if (quantity != order.Quantity) said.Add($"quantity {order.Quantity} → {quantity}");
        var today = await uow.SiteDateAsync(now, ct);
        var due = changes.NewDueDate ?? order.DueDate;
        if (changes.NewDueDate is { } d && d != order.DueDate)
        {
            if (d < today) throw new CommandRejectedException("due_in_past", "The new due date is in the past.");
            said.Add($"due {order.DueDate:yyyy-MM-dd} → {d:yyyy-MM-dd}");
        }

        var priority = changes.NewPriority ?? order.Priority;
        if (priority is < 1 or > 9) throw new CommandRejectedException("invalid_priority", "Priority is 1 (highest) to 9.");
        if (priority != order.Priority) said.Add($"priority {order.Priority} → {priority}");

        // Operations: remove only pending ones of the order's own routing; add at unused sequences after any started work.
        foreach (var sequence in removeOps)
        {
            var op = order.Operations.FirstOrDefault(o => o.Sequence == sequence) ?? throw new CommandRejectedException("operation_not_found", $"Order {order.OrderNumber} has no operation {sequence}.");
            if (op.Status != OperationStatus.Pending) throw new CommandRejectedException("operation_started", $"Operation {sequence} is {op.Status}; work that has started is kept.");
            if (op.ReworkId is not null) throw new CommandRejectedException("rework_operation", $"Operation {sequence} carries out rework {op.ReworkId}; change it through its rework plan.");
        }

        var lastStarted = order.Operations.Where(o => o.Status != OperationStatus.Pending).Select(o => o.Sequence).DefaultIfEmpty(0).Max();
        var remaining = order.Operations.Where(o => !removeOps.Contains(o.Sequence)).Select(o => o.Sequence).ToHashSet();
        var resources = uow.Store<IResourcesLookup>();
        var added = new List<OrderOperationInfo>();
        foreach (var op in addOps)
        {
            if (op.Sequence <= lastStarted) throw new CommandRejectedException("sequence_before_started", $"Operation {op.Sequence} would come before operation {lastStarted}, which has started; add it after.");
            if (op.Sequence is < 1 or > 9999 || !remaining.Add(op.Sequence)) throw new CommandRejectedException("sequence_taken", $"Operation sequence {op.Sequence} is taken or invalid.");
            var workCenter = BusinessCode.Require(op.WorkCenterCode, "Work center");
            if (await resources.FindWorkCenterAsync(workCenter, ct) is null) throw new CommandRejectedException("unknown_work_center", $"Work center {workCenter} does not exist.");
            var description = (op.Description ?? string.Empty).Trim();
            if (description.Length is 0 or > 200) throw new CommandRejectedException("description_required", $"Operation {op.Sequence} needs a description.");
            if (op.SetupMinutes < 0 || op.RunMinutesPerUnit < 0 || op.CrewSize is < 1 or > 20)
                throw new CommandRejectedException("invalid_times", $"Operation {op.Sequence}: set-up and run minutes are zero or more, crew 1 to 20.");
            added.Add(new OrderOperationInfo(op.Sequence, description, workCenter, BusinessCode.Require(op.ActivityCode, "Activity"),
                (op.EligibleMachines ?? []).Select(m => BusinessCode.Require(m, "Machine")).ToList(), (op.RequiredQualifications ?? []).Select(q => BusinessCode.Require(q, "Qualification")).ToList(),
                decimal.Round(op.SetupMinutes + (op.RunMinutesPerUnit * quantity), 2), op.CrewSize, OperationStatus.Pending, SetupMinutes: op.SetupMinutes));
            said.Add($"add operation {op.Sequence} ({workCenter} {description})");
        }

        said.AddRange(removeOps.Select(s => $"remove operation {s}"));

        // A quantity change rescales the run time of pending operations that stay.
        var rescaled = new List<(int, decimal)>();
        if (quantity != order.Quantity)
            foreach (var op in order.Operations.Where(o => o.Status == OperationStatus.Pending && o.ReworkId is null && !removeOps.Contains(o.Sequence)))
            {
                var setup = op.SetupMinutes ?? 0;
                rescaled.Add((op.Sequence, decimal.Round(setup + ((op.PlannedMinutes - setup) * quantity / order.Quantity), 2)));
            }

        // Material: never below what has been issued; removed lines must have nothing issued against their part beyond what other lines keep.
        var issued = await issues.NetIssuedAsync(uow, order.OrderNumber, ct);
        foreach (var line in removeLines)
            if (order.Materials.All(m => m.LineNumber != line)) throw new CommandRejectedException("material_not_found", $"Order {order.OrderNumber} has no material line {line}.");
        var materialQuantities = new List<(int, decimal)>();
        foreach (var change in changeLines)
        {
            var line = order.Materials.FirstOrDefault(m => m.LineNumber == change.LineNumber) ?? throw new CommandRejectedException("material_not_found", $"Order {order.OrderNumber} has no material line {change.LineNumber}.");
            if (change.RequiredQuantity <= 0) throw new CommandRejectedException("invalid_quantity", $"Material line {line.LineNumber}: remove the line instead of setting zero.");
            materialQuantities.Add((line.LineNumber, decimal.Round(change.RequiredQuantity, 6)));
            said.Add($"material line {line.LineNumber} {line.ComponentPartNumber} {line.RequiredQuantity} → {change.RequiredQuantity}");
        }

        if (quantity != order.Quantity)
            foreach (var line in order.Materials.Where(m => m.ReworkId is null && !removeLines.Contains(m.LineNumber) && materialQuantities.All(q => q.Item1 != m.LineNumber)))
                materialQuantities.Add((line.LineNumber, decimal.Round(line.RequiredQuantity * quantity / order.Quantity, 6, MidpointRounding.AwayFromZero)));

        var engineering = uow.Store<IEngineeringLookup>();
        var addedMaterials = new List<OrderMaterialInfo>();
        var nextLine = order.Materials.Select(m => m.LineNumber).DefaultIfEmpty(0).Max();
        foreach (var material in addLines)
        {
            var part = BusinessCode.Require(material.ComponentPartNumber, "Part");
            var item = await engineering.FindItemAsync(part, ct) ?? throw new CommandRejectedException("unknown_part", $"Part {part} does not exist.");
            if (material.RequiredQuantity <= 0) throw new CommandRejectedException("invalid_quantity", $"{part}: the required quantity must be above zero.");
            if (material.ConsumedAtSequence is { } at && !remaining.Contains(at)) throw new CommandRejectedException("operation_not_found", $"{part}: there is no operation {at} to consume it.");
            var revision = await engineering.GetEffectiveRevisionAsync(part, now, ct);
            addedMaterials.Add(new OrderMaterialInfo(++nextLine, part, revision?.Revision, decimal.Round(material.RequiredQuantity, 6), item.UnitOfMeasure, material.ConsumedAtSequence));
            said.Add($"add material {part} × {material.RequiredQuantity}");
        }

        said.AddRange(removeLines.Select(l => $"remove material line {l}"));

        // What remains required of each part must still cover what has already been issued.
        var required = order.Materials.Where(m => !removeLines.Contains(m.LineNumber))
            .Select(m => (m.ComponentPartNumber, Quantity: materialQuantities.FirstOrDefault(q => q.Item1 == m.LineNumber) is { Item1: > 0 } q2 ? q2.Item2 : m.RequiredQuantity))
            .Concat(addedMaterials.Select(m => (m.ComponentPartNumber, Quantity: m.RequiredQuantity)))
            .GroupBy(x => x.ComponentPartNumber).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
        foreach (var (part, net) in issued.Where(i => i.Value > 0))
            if (required.GetValueOrDefault(part) < net)
                throw new CommandRejectedException("below_issued", $"{net} of {part} has already been issued to order {order.OrderNumber}; its requirement cannot go below that.");
        if (order.Operations.Count - removeOps.Count + added.Count == 0) throw new CommandRejectedException("no_operations", "An order keeps at least one operation.");

        return new Plan(quantity, due, priority, removeOps, added, rescaled, removeLines, addedMaterials, materialQuantities, string.Join("; ", said));
    }

    private static async Task ApplyAsync(IOrderAmendmentStore store, OrderInfo order, Plan plan, DateTimeOffset now, string actor, CancellationToken ct)
    {
        foreach (var line in plan.RemoveMaterials) await store.DeleteMaterialLineAsync(order.OrderNumber, line, ct);
        foreach (var sequence in plan.RemoveOperations) await store.DeletePendingOperationAsync(order.OrderNumber, sequence, ct);
        foreach (var op in plan.AddOperations) await store.InsertOperationAsync(order.OrderNumber, op, ct);
        foreach (var (sequence, minutes) in plan.Rescaled) await store.SetPlannedMinutesAsync(order.OrderNumber, sequence, minutes, ct);
        foreach (var (line, quantity) in plan.MaterialQuantities) await store.SetMaterialQuantityAsync(order.OrderNumber, line, quantity, ct);
        foreach (var material in plan.AddMaterials) await store.InsertMaterialAsync(order.OrderNumber, material, ct);
        if (plan.Quantity != order.Quantity || plan.DueDate != order.DueDate || plan.Priority != order.Priority)
            await store.SetQuantityDueAndPriorityAsync(order.OrderNumber, plan.Quantity, plan.DueDate, plan.Priority, now, actor, ct);
    }

    private static async Task<OrderInfo> VisibleOrderAsync(IKernelUnitOfWork uow, CommandContext context, long orderNumber, CancellationToken ct)
    {
        var order = await uow.Store<IExecutionLookup>().FindOrderAsync(orderNumber, ct);
        return order is null || (order.ProgramId is { } p && !context.Principal.Programs.Contains(new ProgramId(p)))
            ? throw new CommandRejectedException("order_not_found", $"Order {orderNumber} does not exist.")
            : order;
    }
}
