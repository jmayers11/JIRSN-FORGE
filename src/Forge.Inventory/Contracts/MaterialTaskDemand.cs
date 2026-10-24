namespace Forge.Inventory.Contracts;

/// <summary>One calculation for unreconciled order demand, including partial picks and delivered tasks.</summary>
public static class MaterialTaskDemand
{
    public static decimal Remaining(decimal required, decimal netIssued, IEnumerable<MaterialTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentOutOfRangeException.ThrowIfNegative(required);
        var tasked = tasks.Where(task => !task.IsNonBom && task.Status is not (MaterialTaskStatus.Cancelled or MaterialTaskStatus.Rejected))
            .Sum(Coverage);
        return Math.Max(0, required - Math.Max(netIssued, tasked));
    }

    public static decimal Coverage(MaterialTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.Status is MaterialTaskStatus.Cancelled or MaterialTaskStatus.Rejected or MaterialTaskStatus.DeliveryRejected) return 0m;
        if (task.Status == MaterialTaskStatus.Delivered)
        {
            var received = task.ReceivedQuantity > 0 ? task.ReceivedQuantity : task.PickedQuantity ?? task.Quantity;
            return Math.Max(0m, received - task.ReturnedQuantity - task.ReversedQuantity);
        }
        return Math.Max(0m, (task.PickedQuantity ?? task.Quantity) - task.RejectedQuantity);
    }

    public static decimal Pending(MaterialTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return task.Status switch
        {
            MaterialTaskStatus.ApprovalNeeded or MaterialTaskStatus.Requested or MaterialTaskStatus.Allocated => task.Quantity,
            MaterialTaskStatus.Picked => task.PickedQuantity ?? task.Quantity,
            MaterialTaskStatus.InTransit => Math.Max(0m, (task.PickedQuantity ?? task.Quantity) - task.ReceivedQuantity - task.RejectedQuantity),
            _ => 0m,
        };
    }
}
