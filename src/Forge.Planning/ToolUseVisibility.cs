using System.Globalization;
using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Resources.Contracts;

namespace Forge.Planning;

/// <summary>Shared tool metadata keeps its access; canonical operation references inherit their source order's access.</summary>
public static class ToolUseVisibility
{
    public static async Task<IReadOnlyList<ToolEvent>> FilterAsync(IKernelUnitOfWork uow, ForgePrincipal principal,
        IEnumerable<ToolEvent> events, DateTimeOffset now, CancellationToken ct)
    {
        var orders = new Dictionary<long, OrderInfo?>();
        var visible = new List<ToolEvent>();
        foreach (var item in events)
        {
            if (OperationOf(item.Reference) is not { } operation)
            {
                visible.Add(item);
                continue;
            }
            if (!orders.TryGetValue(operation.Order, out var order))
            {
                order = await uow.Store<IExecutionLookup>().FindOrderAsync(operation.Order, ct);
                orders.Add(operation.Order, order);
            }
            if (order is null || order.ProgramId is { } program && !principal.Programs.Contains(new ProgramId(program)))
                continue;
            var recorded = order.Operations.FirstOrDefault(candidate => candidate.Sequence == operation.Sequence);
            if (recorded is null) continue;
            var resource = new ResourceRef("OrderOperation", $"{operation.Order}/{operation.Sequence}",
                order.ProgramId is { } id ? new ProgramId(id) : null, WorkCenterId: recorded.WorkCenterCode,
                ContractId: order.ContractId?.ToString(CultureInfo.InvariantCulture));
            if (principal.HasPermission(Permissions.ResourcesRead, resource, now)) visible.Add(item);
        }
        return visible;
    }

    private static (long Order, int Sequence)? OperationOf(string? reference)
    {
        if (reference is null) return null;
        var prefixLength = reference.StartsWith("order ", StringComparison.OrdinalIgnoreCase) ? 6
            : reference.StartsWith("inspection ", StringComparison.OrdinalIgnoreCase) ? 11 : 0;
        if (prefixLength == 0) return null;
        var body = reference.AsSpan(prefixLength).Trim();
        var separator = body.IndexOf('/');
        if (separator <= 0 || !long.TryParse(body[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var order)
            || !int.TryParse(body[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
            || order <= 0 || sequence <= 0) return null;
        return (order, sequence);
    }
}
