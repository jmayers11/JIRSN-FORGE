using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Workforce.Contracts;

namespace Forge.Host.Workforce;

/// <summary>Time is charged to an existing operation of a released, held or completed order the employee may see.</summary>
public sealed class TimeChargeResolver : ITimeChargeResolver
{
    public async Task<string?> OrderChargeProblemAsync(IKernelUnitOfWork uow, ForgePrincipal principal, long orderNumber, int sequence, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(principal);
        var order = await uow.Store<IExecutionLookup>().FindOrderAsync(orderNumber, ct);
        if (order is null || (order.ProgramId is { } program && !principal.Programs.Contains(new ProgramId(program))))
            return $"Order {orderNumber} does not exist.";
        if (order.Status is not (OrderStatus.Released or OrderStatus.Held or OrderStatus.Completed))
            return $"Order {orderNumber} is {order.Status}; time is charged to released orders.";
        return order.Operations.Any(o => o.Sequence == sequence) ? null : $"Order {orderNumber} has no operation {sequence}.";
    }
}
