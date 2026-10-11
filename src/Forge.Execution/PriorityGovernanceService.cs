using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;

namespace Forge.Execution;

/// <summary>Changes an order's priority only against the exact, recent schedule impact preview shown to the user.</summary>
public sealed class PriorityGovernanceService(
    CommandPipeline pipeline,
    IKernelUnitOfWorkFactory unitOfWork,
    IPriorityImpactProvider impactProvider,
    TimeProvider time)
{
    private const string OrderEntity = "ProductionOrder";
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(5);

    public async Task<PriorityChangePreview> PreviewAsync(long orderNumber, int newPriority, ForgePrincipal principal, CancellationToken ct)
    {
        ValidatePriority(newPriority);
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var order = await uow.Store<IExecutionLookup>().FindOrderAsync(orderNumber, ct);
        EnsureVisible(order, principal, orderNumber);
        var asOf = DatabaseTime(time.GetUtcNow());
        var preview = await impactProvider.PreviewAsync(uow, orderNumber, newPriority, asOf, ct);
        return FilterVisible(preview, principal);
    }

    public async Task<CommandResult> ChangeAsync(ChangeOrderPriority command, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        ValidatePriority(command.NewPriority);
        if (string.IsNullOrWhiteSpace(command.PreviewFingerprint) || command.PreviewFingerprint.Length != 64)
        {
            throw new CommandRejectedException("invalid_priority_preview", "Request a fresh priority impact preview before committing the change.");
        }

        OrderInfo target;
        await using (var read = await unitOfWork.BeginSnapshotAsync(ct))
        {
            target = await read.Store<IExecutionLookup>().FindOrderAsync(command.OrderNumber, ct)
                ?? throw new CommandRejectedException("order_not_found", $"Order {command.OrderNumber} does not exist.");
            EnsureVisible(target, context.Principal, command.OrderNumber);
        }

        var id = command.OrderNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var resource = target.ProgramId is { } program
            ? new ResourceRef(OrderEntity, id, new ProgramId(program))
            : ResourceRef.Of(OrderEntity, id);
        var spec = new CommandSpec("Execution", nameof(ChangeOrderPriority), Permissions.OrdersManage, resource, ReasonRequired: true);
        return await pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var order = await uow.Store<IExecutionLookup>().FindOrderAsync(command.OrderNumber, ct);
            EnsureVisible(order, context.Principal, command.OrderNumber);

            if (order!.Status is OrderStatus.Cancelled or OrderStatus.Completed)
            {
                throw new CommandRejectedException("order_closed", $"Order {id} is {order.Status}; its priority cannot be changed.");
            }

            if (order.Priority != command.ExpectedPriority)
            {
                throw new CommandRejectedException("stale_priority", $"Order {id} priority changed after the preview; request a new preview.");
            }

            if (order.Priority == command.NewPriority)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(id, "Priority is already set to that value."));
            }

            var previewAsOf = DatabaseTime(command.PreviewAsOf);
            if (previewAsOf > now || now - previewAsOf > PreviewLifetime)
            {
                throw new CommandRejectedException("stale_priority_preview", "The priority impact preview expired; request a new preview.");
            }

            var current = await impactProvider.PreviewAsync(uow, command.OrderNumber, command.NewPriority, previewAsOf, ct);
            if (current.CurrentPriority != command.ExpectedPriority || !string.Equals(current.Fingerprint, command.PreviewFingerprint, StringComparison.Ordinal))
            {
                throw new CommandRejectedException("stale_priority_preview", "Planning inputs changed after the preview; request a new preview and review its updated impact.");
            }

            var visible = FilterVisible(current, context.Principal);
            var delayed = visible.Orders.Where(o => o.OrderNumber != command.OrderNumber && o.DelayMinutes is not null).ToList();
            if (!await uow.Store<IExecutionStore>().SetPriorityAsync(command.OrderNumber, command.ExpectedPriority, command.NewPriority, now, context.Actor, ct))
            {
                throw new CommandRejectedException("stale_priority", $"Order {id} changed while the priority update was being committed.");
            }

            var program = order.ProgramId is { } p ? new ProgramId(p) : (ProgramId?)null;
            var message = delayed.Count == 0
                ? $"Order {id} priority changed from {command.ExpectedPriority} to {command.NewPriority}; no released order's projected completion is delayed."
                : $"Order {id} priority changed from {command.ExpectedPriority} to {command.NewPriority}; projected delays: {string.Join(", ", delayed.Select(o => $"order {o.OrderNumber} by {o.DelayMinutes:0} min"))}.";
            return new CommandOutcome(
                CommandResult.Ok(id, message),
                [pipeline.Event(new OrderPriorityChanged(command.OrderNumber, command.ExpectedPriority, command.NewPriority, current.Fingerprint, delayed), context, "Execution", OrderEntity, id, program)],
                new AuditChange(OrderEntity, id,
                    CanonicalJson.Serialize(new { priority = command.ExpectedPriority }),
                    CanonicalJson.Serialize(new { priority = command.NewPriority, authority = Permissions.OrdersManage, previewAsOf, previewFingerprint = current.Fingerprint, delayedOrders = delayed }),
                    program));
        }, ct);
    }

    private static PriorityChangePreview FilterVisible(PriorityChangePreview preview, ForgePrincipal principal) => preview with
    {
        Orders = preview.Orders.Where(o => o.ProgramId is null || principal.Programs.Contains(new ProgramId(o.ProgramId.Value))).ToList(),
    };

    private static void EnsureVisible(OrderInfo? order, ForgePrincipal principal, long orderNumber)
    {
        if (order is null || order.ProgramId is { } program && !principal.Programs.Contains(new ProgramId(program)))
        {
            throw new CommandRejectedException("order_not_found", $"Order {orderNumber} does not exist.");
        }
    }

    private static DateTimeOffset DatabaseTime(DateTimeOffset value) =>
        new(value.UtcDateTime.Ticks / 10 * 10, TimeSpan.Zero);

    private static void ValidatePriority(int priority)
    {
        if (priority is < 1 or > 9)
        {
            throw new CommandRejectedException("invalid_priority", "Priority must be between 1 (highest) and 9.");
        }
    }
}
