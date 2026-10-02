using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Programs.Contracts;

namespace Forge.Execution;

public sealed record CreateProductionOrder(string PartNumber, decimal Quantity, DateOnly DueDate, int Priority, Guid? ProgramId);

public sealed record FirmProductionOrder(long OrderNumber);

/// <summary>Charges a program order to an element of its program's WBS (or clears it).</summary>
public sealed record AssignOrderWbs(long OrderNumber, string? WbsCode);

public sealed record OrderWbsChanged(long OrderNumber, string? WbsCode) : IDomainEvent
{
    public static string EventType => nameof(OrderWbsChanged);

    public static int SchemaVersion => 1;
}

public sealed record ReleaseProductionOrder(long OrderNumber);

public sealed record HoldProductionOrder(long OrderNumber);

public sealed record ResumeProductionOrder(long OrderNumber);

public sealed record CancelProductionOrder(long OrderNumber);

public sealed record ProductionOrderCreated(long OrderNumber, string PartNumber, string Revision, decimal Quantity, DateOnly DueDate) : IDomainEvent
{
    public static string EventType => nameof(ProductionOrderCreated);

    public static int SchemaVersion => 1;
}

public sealed record ProductionOrderStatusChanged(long OrderNumber, string From, string To) : IDomainEvent
{
    public static string EventType => nameof(ProductionOrderStatusChanged);

    public static int SchemaVersion => 1;
}

public interface IExecutionStore
{
    Task<long> InsertOrderAsync(string partNumber, string revision, decimal quantity, DateOnly dueDate, int priority, Guid? programId, DateTimeOffset at, string actor, CancellationToken ct);

    Task SetStatusAsync(long orderNumber, OrderStatus status, OrderStatus? heldFrom, DateTimeOffset at, string actor, CancellationToken ct);

    Task SetWbsAsync(long orderNumber, string? wbsCode, DateTimeOffset at, string actor, CancellationToken ct);

    Task SaveReleaseAsync(long orderNumber, IReadOnlyList<OrderOperationInfo> operations, IReadOnlyList<OrderMaterialInfo> materials, DateTimeOffset at, string actor, CancellationToken ct);
}

/// <summary>
/// Production orders. The person who sets up an order cannot release it. Release snapshots the BOM and routing of the order's revision, so later
/// engineering changes never alter released work. Orders tied to a program are visible and
/// changeable only by people with access to that program.
/// </summary>
public sealed class ProductionOrderService(CommandPipeline pipeline)
{
    public const string SourceContext = "Execution";
    private const string OrderEntity = "ProductionOrder";

    public Task<CommandResult> CreateAsync(CreateProductionOrder c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var program = c.ProgramId is { } p ? new ProgramId(p) : (ProgramId?)null;
        var spec = new CommandSpec(SourceContext, nameof(CreateProductionOrder), Permissions.OrdersManage, new ResourceRef(OrderEntity, null, program));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            if (c.Quantity <= 0 || c.Quantity > 1_000_000 || c.Priority is < 1 or > 9)
            {
                throw new CommandRejectedException("invalid_order", "Quantity must be above zero and priority between 1 (highest) and 9.");
            }

            if (c.ProgramId is { } programId)
            {
                var registered = await uow.Store<IProgramsLookup>().FindProgramAsync(programId, ct)
                    ?? throw new CommandRejectedException("program_not_found", $"Program {programId} does not exist.");
                if (registered.Status != ProgramStatus.Active)
                {
                    throw new CommandRejectedException("program_closed", $"Program {registered.Code} is closed; it takes no new orders.");
                }
            }

            var engineering = uow.Store<IEngineeringLookup>();
            var item = await engineering.FindItemAsync(part, ct) ?? throw new CommandRejectedException("unknown_part", $"Part {part} does not exist.");
            if (item.MakeBuy != MakeBuy.Make)
            {
                throw new CommandRejectedException("not_made", $"Part {part} is purchased, not made.");
            }

            var revision = await engineering.GetEffectiveRevisionAsync(part, now, ct)
                ?? throw new CommandRejectedException("no_released_revision", $"Part {part} has no released revision in force.");
            var number = await uow.Store<IExecutionStore>().InsertOrderAsync(part, revision.Revision, c.Quantity, c.DueDate, c.Priority, c.ProgramId, now, context.Actor, ct);
            var id = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new CommandOutcome(
                CommandResult.Ok(id, $"Order {id} planned: {c.Quantity} x {part} rev {revision.Revision}, due {c.DueDate:yyyy-MM-dd}."),
                [pipeline.Event(new ProductionOrderCreated(number, part, revision.Revision, c.Quantity, c.DueDate), context, SourceContext, OrderEntity, id, program)],
                new AuditChange(OrderEntity, id, null, CanonicalJson.Serialize(new { part, revision = revision.Revision, c.Quantity, c.DueDate, c.Priority, c.ProgramId }), program));
        }, ct);
    }

    public Task<CommandResult> AssignWbsAsync(AssignOrderWbs c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var id = c.OrderNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, nameof(AssignOrderWbs), Permissions.ProgramsManage, ResourceRef.Of(OrderEntity, id));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var order = await uow.Store<IExecutionLookup>().FindOrderAsync(c.OrderNumber, ct);
            if (order is null || (order.ProgramId is { } p && !context.Principal.Programs.Contains(new ProgramId(p))))
            {
                throw new CommandRejectedException("order_not_found", $"Order {id} does not exist.");
            }

            if (order.ProgramId is not { } programId)
            {
                throw new CommandRejectedException("not_program_order", $"Order {id} belongs to no program, so it has no WBS.");
            }

            var code = string.IsNullOrWhiteSpace(c.WbsCode) ? null : c.WbsCode.Trim();
            if (code is not null && (await uow.Store<IProgramsLookup>().ListWbsAsync(programId, ct)).All(w => w.Code != code))
            {
                throw new CommandRejectedException("unknown_wbs", $"The order's program has no WBS element {code}.");
            }

            if (code == order.WbsCode)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(id, "Nothing changed."));
            }

            await uow.Store<IExecutionStore>().SetWbsAsync(order.OrderNumber, code, now, context.Actor, ct);
            var program = new ProgramId(programId);
            return new CommandOutcome(
                CommandResult.Ok(id, code is null ? $"Order {id} no longer charged to a WBS element." : $"Order {id} charged to WBS {code}."),
                [pipeline.Event(new OrderWbsChanged(order.OrderNumber, code), context, SourceContext, OrderEntity, id, program)],
                new AuditChange(OrderEntity, id, CanonicalJson.Serialize(new { wbs = order.WbsCode }), CanonicalJson.Serialize(new { wbs = code }), program));
        }, ct);
    }

    public Task<CommandResult> FirmAsync(FirmProductionOrder c, CommandContext context, CancellationToken ct) =>
        TransitionAsync(nameof(FirmProductionOrder), c?.OrderNumber ?? 0, c!, context, false, (order, _) => order.Status == OrderStatus.Planned
            ? Task.FromResult<OrderStatus?>(OrderStatus.FirmPlanned)
            : throw Invalid(order, "firm"), ct);

    public Task<CommandResult> ReleaseAsync(ReleaseProductionOrder c, CommandContext context, CancellationToken ct) =>
        TransitionAsync(nameof(ReleaseProductionOrder), c?.OrderNumber ?? 0, c!, context, false, async (order, uow) =>
        {
            if (order.Status is not (OrderStatus.Planned or OrderStatus.FirmPlanned))
            {
                throw Invalid(order, "release");
            }

            if (order.CreatedBy == context.Actor)
            {
                throw new CommandRejectedException("separation_of_duties", "You set up this order; a different person must release it.");
            }

            var engineering = uow.Store<IEngineeringLookup>();
            var now = pipeline.Time.GetUtcNow();
            var detail = await engineering.GetRevisionDetailAsync(order.PartNumber, order.Revision, ct)
                ?? throw new CommandRejectedException("unknown_revision", "The order's revision no longer exists.");
            if (!detail.Revision.IsEffectiveAt(now))
            {
                throw new CommandRejectedException("revision_not_in_force", $"{order.PartNumber} rev {order.Revision} is no longer in force. Cancel and re-plan against the current revision.");
            }

            var operations = detail.Routing
                .Select(op => new OrderOperationInfo(
                    op.Sequence,
                    op.Description,
                    op.WorkCenterCode,
                    op.ActivityCode,
                    op.EligibleMachines,
                    op.RequiredQualifications,
                    op.SetupMinutes + (op.RunMinutesPerUnit * order.Quantity),
                    op.CrewSize,
                    OperationStatus.Pending))
                .ToList();

            var materials = new List<OrderMaterialInfo>();
            foreach (var line in detail.Bom)
            {
                var component = await engineering.FindItemAsync(line.ComponentPartNumber, ct);
                var componentRevision = await engineering.GetEffectiveRevisionAsync(line.ComponentPartNumber, now, ct);
                var required = decimal.Round(line.QuantityPer * order.Quantity * (1 + (line.ScrapPercent / 100m)), 6, MidpointRounding.AwayFromZero);
                materials.Add(new OrderMaterialInfo(line.LineNumber, line.ComponentPartNumber, componentRevision?.Revision, required, component?.UnitOfMeasure ?? "EA"));
            }

            await uow.Store<IExecutionStore>().SaveReleaseAsync(order.OrderNumber, operations, materials, now, context.Actor, ct);
            return OrderStatus.Released;
        }, ct, Permissions.OrdersRelease);

    public Task<CommandResult> HoldAsync(HoldProductionOrder c, CommandContext context, CancellationToken ct) =>
        TransitionAsync(nameof(HoldProductionOrder), c?.OrderNumber ?? 0, c!, context, true, (order, _) => order.Status is OrderStatus.Planned or OrderStatus.FirmPlanned or OrderStatus.Released
            ? Task.FromResult<OrderStatus?>(OrderStatus.Held)
            : throw Invalid(order, "hold"), ct);

    public Task<CommandResult> ResumeAsync(ResumeProductionOrder c, CommandContext context, CancellationToken ct) =>
        TransitionAsync(nameof(ResumeProductionOrder), c?.OrderNumber ?? 0, c!, context, true, (order, _) => order is { Status: OrderStatus.Held, HeldFrom: { } previous }
            ? Task.FromResult<OrderStatus?>(previous)
            : throw Invalid(order, "resume"), ct);

    public Task<CommandResult> CancelAsync(CancelProductionOrder c, CommandContext context, CancellationToken ct) =>
        TransitionAsync(nameof(CancelProductionOrder), c?.OrderNumber ?? 0, c!, context, true, (order, _) => order.Status != OrderStatus.Cancelled && order.Operations.All(o => o.Status == OperationStatus.Pending)
            ? Task.FromResult<OrderStatus?>(OrderStatus.Cancelled)
            : throw Invalid(order, "cancel"), ct);

    private Task<CommandResult> TransitionAsync<TCommand>(
        string commandType,
        long orderNumber,
        TCommand command,
        CommandContext context,
        bool reasonRequired,
        Func<OrderInfo, Kernel.Access.IKernelUnitOfWork, Task<OrderStatus?>> decide,
        CancellationToken ct,
        string permission = Permissions.OrdersManage)
    {
        ArgumentNullException.ThrowIfNull(command);
        var id = orderNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return pipeline.ExecuteAsync(
            new CommandSpec(SourceContext, commandType, permission, ResourceRef.Of(OrderEntity, id), reasonRequired),
            command,
            context,
            async (uow, now) =>
            {
                var order = await uow.Store<IExecutionLookup>().FindOrderAsync(orderNumber, ct)
                    ?? throw new CommandRejectedException("order_not_found", $"Order {id} does not exist.");
                var program = order.ProgramId is { } p ? new ProgramId(p) : (ProgramId?)null;
                if (program is { } required && !context.Principal.Programs.Contains(required))
                {
                    // Same answer as a missing order: program-segregated orders are not disclosed.
                    throw new CommandRejectedException("order_not_found", $"Order {id} does not exist.");
                }

                var next = await decide(order, uow) ?? order.Status;
                var heldFrom = next == OrderStatus.Held ? order.Status : (OrderStatus?)null;
                await uow.Store<IExecutionStore>().SetStatusAsync(orderNumber, next, heldFrom, now, context.Actor, ct);
                return new CommandOutcome(
                    CommandResult.Ok(id, $"Order {id}: {order.Status} → {next}."),
                    [pipeline.Event(new ProductionOrderStatusChanged(orderNumber, order.Status.ToString(), next.ToString()), context, SourceContext, OrderEntity, id, program)],
                    new AuditChange(OrderEntity, id, order.Status.ToString(), next.ToString(), program));
            },
            ct);
    }

    private static CommandRejectedException Invalid(OrderInfo order, string action) =>
        new("invalid_transition", $"Order {order.OrderNumber} is {order.Status}; it cannot {action}.");
}
