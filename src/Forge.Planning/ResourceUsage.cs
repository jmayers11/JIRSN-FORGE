using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Resources.Contracts;
using Forge.Workforce.Contracts;

namespace Forge.Planning;

/// <summary>
/// Tells Resources whether a safety area, work center or machine is still in use before it is retired,
/// moved or loses an activity: unfinished order operations (released or held), routings that are
/// in force or being drafted, workers homed there and current work assignments.
/// </summary>
internal sealed class ResourceUsage(IKernelUnitOfWork uow) : IResourceUsage
{
    public async Task<IReadOnlyList<string>> FindUsesAsync(string kind, string code, string? activityCode, DateTimeOffset at, CancellationToken ct)
    {
        var uses = new List<string>();
        bool ActivityMatches(string activity) => activityCode is null || activity == activityCode;
        bool OperationUses(string workCenter, IReadOnlyList<string> machines, string activity) => ActivityMatches(activity) && kind switch
        {
            "WorkCenter" => workCenter == code,
            "Machine" => machines.Contains(code),
            _ => false,
        };

        foreach (var order in await uow.Store<IExecutionLookup>().ListOrdersAsync(ct))
        {
            if (order.Status is not (OrderStatus.Released or OrderStatus.Held))
            {
                continue;
            }

            uses.AddRange(order.Operations
                .Where(op => op.Status != OperationStatus.Complete && OperationUses(op.WorkCenterCode, op.EligibleMachines, op.ActivityCode))
                .Select(op => $"order {order.OrderNumber} operation {op.Sequence}"));
        }

        var engineering = uow.Store<IEngineeringLookup>();
        foreach (var item in await engineering.ListItemsAsync(ct))
        {
            foreach (var revision in await engineering.ListRevisionsAsync(item.PartNumber, ct))
            {
                var live = revision.Status == RevisionStatus.Draft || revision.EffectiveTo is null || revision.EffectiveTo > at;
                if (!live || await engineering.GetRevisionDetailAsync(item.PartNumber, revision.Revision, ct) is not { } detail)
                {
                    continue;
                }

                uses.AddRange(detail.Routing
                    .Where(op => OperationUses(op.WorkCenterCode, op.EligibleMachines, op.ActivityCode))
                    .Select(op => $"{item.PartNumber} rev {revision.Revision} ({revision.Status.ToString().ToLowerInvariant()}) routing step {op.Sequence}"));
            }
        }

        foreach (var worker in (await uow.Store<IWorkforceLookup>().GetWorkerStatesAsync(at, ct)).Where(w => w.IsActive))
        {
            if (activityCode is null && kind == "WorkCenter" && worker.HomeWorkCenterCode == code)
            {
                uses.Add($"home work center of {worker.DisplayName}");
            }

            if (worker.Assignment is { } a && ActivityMatches(a.ActivityCode) && kind switch
            {
                "SafetyArea" => a.SafetyAreaCode == code,
                "WorkCenter" => a.WorkCenterCode == code,
                "Machine" => a.MachineCode == code,
                _ => false,
            })
            {
                uses.Add($"{worker.DisplayName} is assigned there");
            }
        }

        return uses;
    }
}
