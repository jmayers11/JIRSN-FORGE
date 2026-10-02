using System.Globalization;
using Forge.Inventory.Contracts;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Maintenance.Contracts;

namespace Forge.Inventory;

public sealed record IssueToWorkOrder(long WorkOrderId, string PartNumber, decimal Quantity, string LocationCode, string? LotNumber);

public sealed record ReturnFromWorkOrder(long WorkOrderId, string PartNumber, decimal Quantity, string LocationCode, string? LotNumber);

/// <summary>
/// Spare parts for maintenance (spec §47) move through the ledger like any stock: issued from Available
/// stock to an open work order, and unused parts returned (never more than was issued to that order).
/// </summary>
public sealed partial class InventoryService
{
    public Task<CommandResult> IssueToWorkOrderAsync(IssueToWorkOrder c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return PostAsync(nameof(IssueToWorkOrder), c.PartNumber, c, context, Permissions.SparesIssue, false, async (uow, item, _) =>
        {
            var order = await OpenWorkOrderAsync(uow, c.WorkOrderId, ct);
            var location = await ActiveLocationAsync(uow, c.LocationCode, ct);
            var lot = RequireLot(item, c.LotNumber);
            var quantity = RequireQuantity(item, c.Quantity);
            await RequireStockAsync(uow, item.PartNumber, location.Code, lot, StockStatus.Available, quantity, ct);
            var txn = new NewTransaction(InventoryTransactionKind.MaintenanceIssue, item.PartNumber, quantity, location.Code, StockStatus.Available, null, null, lot,
                Reason: $"Work order {order.WorkOrderId} on {order.MachineCode}", WorkOrderId: order.WorkOrderId);
            return (txn, $"Issued {quantity} {item.UnitOfMeasure} {item.PartNumber} to work order {order.WorkOrderId} ({order.MachineCode}).",
                id => pipeline.Event(new InventoryMoved(id, nameof(InventoryTransactionKind.MaintenanceIssue), item.PartNumber, quantity, location.Code, null), context, SourceContext, StockEntity, item.PartNumber));
        }, ct);
    }

    public Task<CommandResult> ReturnFromWorkOrderAsync(ReturnFromWorkOrder c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return PostAsync(nameof(ReturnFromWorkOrder), c.PartNumber, c, context, Permissions.SparesIssue, true, async (uow, item, _) =>
        {
            var order = await uow.Store<IMaintenanceLookup>().FindWorkOrderAsync(c.WorkOrderId, ct)
                ?? throw new CommandRejectedException("work_order_not_found", $"Work order {c.WorkOrderId} does not exist.");
            var issued = (await uow.Store<IInventoryLookup>().ListWorkOrderPartsAsync(order.WorkOrderId, ct)).FirstOrDefault(p => p.PartNumber == item.PartNumber)?.NetIssued ?? 0;
            var quantity = RequireQuantity(item, c.Quantity);
            if (quantity > issued)
            {
                throw new CommandRejectedException("over_return", $"Work order {order.WorkOrderId} has {issued.ToString("0.###", CultureInfo.InvariantCulture)} {item.PartNumber} issued; return at most that.");
            }

            var location = await ActiveLocationAsync(uow, c.LocationCode, ct);
            var lot = RequireLot(item, c.LotNumber);
            var txn = new NewTransaction(InventoryTransactionKind.MaintenanceReturn, item.PartNumber, quantity, null, null, location.Code, StockStatus.Available, lot,
                Reason: context.Reason, WorkOrderId: order.WorkOrderId);
            return (txn, $"Returned {quantity} {item.UnitOfMeasure} {item.PartNumber} from work order {order.WorkOrderId} to {location.Code}.",
                id => pipeline.Event(new InventoryMoved(id, nameof(InventoryTransactionKind.MaintenanceReturn), item.PartNumber, quantity, null, location.Code), context, SourceContext, StockEntity, item.PartNumber));
        }, ct);
    }

    private static async Task<WorkOrderInfo> OpenWorkOrderAsync(Kernel.Access.IKernelUnitOfWork uow, long workOrderId, CancellationToken ct)
    {
        var order = await uow.Store<IMaintenanceLookup>().FindWorkOrderAsync(workOrderId, ct)
            ?? throw new CommandRejectedException("work_order_not_found", $"Work order {workOrderId} does not exist.");
        return order.Status is WorkOrderStatus.Closed or WorkOrderStatus.Cancelled
            ? throw new CommandRejectedException("work_order_closed", $"Work order {workOrderId} is {order.Status}; parts are issued only to open work.")
            : order;
    }
}
