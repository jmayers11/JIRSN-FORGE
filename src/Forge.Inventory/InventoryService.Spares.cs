using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Events;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Maintenance.Contracts;

namespace Forge.Inventory;

/// <param name="AlternateFor">The part this is issued in place of, through an approved alternate usable for the work order's asset (V2.9).</param>
public sealed record IssueToWorkOrder(long WorkOrderId, string PartNumber, decimal Quantity, string LocationCode, string? LotNumber, string? AlternateFor = null);

public sealed record ReturnFromWorkOrder(long WorkOrderId, string PartNumber, decimal Quantity, string LocationCode, string? LotNumber, string? AlternateFor = null);

/// <summary>A part was issued to a maintenance work order (V2.9); an installation follows when the work order names its position.</summary>
public sealed record SparesIssued(long TransactionId, long WorkOrderId, string PartNumber, decimal Quantity, string? LotNumber, string? PartRevision) : IDomainEvent
{
    public static string EventType => nameof(SparesIssued);

    public static int SchemaVersion => 1;
}

/// <summary>
/// Spare parts for maintenance (spec §47) move through the ledger like any stock: issued from released
/// stock to an open work order, and unused parts returned (never more than was issued to that order).
/// </summary>
public sealed partial class InventoryService
{
    public Task<CommandResult> IssueToWorkOrderAsync(IssueToWorkOrder c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return PostAsync(nameof(IssueToWorkOrder), c.PartNumber, c, context, Permissions.SparesIssue, false, async (uow, item, now) =>
        {
            var order = await OpenWorkOrderAsync(uow, c.WorkOrderId, ct);
            var location = await CompanyLocationAsync(uow, c.LocationCode, ct);
            var lot = RequireLot(item, c.LotNumber);
            var quantity = RequireQuantity(item, c.Quantity);
            await RequireStockAsync(uow, item.PartNumber, location.Code, lot, StockStatus.Released, quantity, ct);
            var (primary, alternate, factor) = await MaintenanceAlternateAsync(uow, order, item.PartNumber, c.AlternateFor, now, ct);
            var primaryQuantity = alternate is null ? (decimal?)null : decimal.Round(quantity / factor, 6, MidpointRounding.AwayFromZero);
            var revision = lot is null ? null : (await uow.Store<IInventoryLookup>().ListLotsAsync(item.PartNumber, ct)).FirstOrDefault(l => l.LotNumber == lot)?.PartRevision;
            var txn = new NewTransaction(InventoryTransactionKind.MaintenanceIssue, item.PartNumber, quantity, location.Code, StockStatus.Released, null, null, lot,
                Reason: $"Work order {order.WorkOrderId} on {order.Target}", WorkOrderId: order.WorkOrderId,
                SubstitutesFor: alternate is null ? null : primary, AlternateId: alternate?.AlternateId, PrimaryQuantity: primaryQuantity);
            var message = $"Issued {quantity} {item.UnitOfMeasure} {item.PartNumber} to work order {order.WorkOrderId} ({order.Target})"
                + (alternate is null ? "." : $" in place of {primaryQuantity} {primary} (approved alternate {alternate.AlternateId}).");
            return (txn, message, id => alternate is null
                ? pipeline.Event(new SparesIssued(id, order.WorkOrderId, item.PartNumber, quantity, lot, revision), context, SourceContext, StockEntity, item.PartNumber)
                : pipeline.Event(new AlternateUsed(id, alternate.AlternateId, primary, item.PartNumber, quantity, primaryQuantity!.Value, null, order.WorkOrderId, revision), context, SourceContext, StockEntity, item.PartNumber));
        }, ct);
    }

    public Task<CommandResult> ReturnFromWorkOrderAsync(ReturnFromWorkOrder c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return PostAsync(nameof(ReturnFromWorkOrder), c.PartNumber, c, context, Permissions.SparesIssue, true, async (uow, item, _) =>
        {
            var order = await uow.Store<IMaintenanceLookup>().FindWorkOrderAsync(c.WorkOrderId, ct)
                ?? throw new CommandRejectedException("work_order_not_found", $"Work order {c.WorkOrderId} does not exist.");
            var primary = string.IsNullOrWhiteSpace(c.AlternateFor) ? null : c.AlternateFor.Trim().ToUpperInvariant();
            var rows = (await uow.Store<IInventoryLookup>().ListWorkOrderPartsAsync(order.WorkOrderId, ct)).Where(p => p.PartNumber == item.PartNumber && p.SubstitutesFor == primary).ToList();
            var issued = rows.Sum(p => p.NetIssued);
            var quantity = RequireQuantity(item, c.Quantity);
            if (quantity > issued)
            {
                throw new CommandRejectedException("over_return", $"Work order {order.WorkOrderId} has {issued.ToString("0.###", CultureInfo.InvariantCulture)} {item.PartNumber} issued{(primary is null ? string.Empty : $" in place of {primary}")}; return at most that.");
            }

            var location = await CompanyLocationAsync(uow, c.LocationCode, ct);
            var lot = RequireLot(item, c.LotNumber);
            decimal? primaryQuantity = null;
            long? alternateId = null;
            if (primary is not null)
            {
                var issuedPrimary = rows.Sum(p => p.PrimaryNetIssued ?? 0);
                primaryQuantity = decimal.Round(issuedPrimary * quantity / issued, 6, MidpointRounding.AwayFromZero);
                alternateId = (await uow.Store<IInventoryLookup>().ListTransactionsAsync(item.PartNumber, null, 1000, ct))
                    .FirstOrDefault(t => t.WorkOrderId == order.WorkOrderId && t.SubstitutesFor == primary && t.AlternateId is not null)?.AlternateId;
            }

            var txn = new NewTransaction(InventoryTransactionKind.MaintenanceReturn, item.PartNumber, quantity, null, null, location.Code, StockStatus.Released, lot,
                Reason: context.Reason, WorkOrderId: order.WorkOrderId, SubstitutesFor: primary, AlternateId: alternateId, PrimaryQuantity: primaryQuantity);
            return (txn, $"Returned {quantity} {item.UnitOfMeasure} {item.PartNumber} from work order {order.WorkOrderId} to {location.Code}.",
                id => pipeline.Event(new InventoryMoved(id, nameof(InventoryTransactionKind.MaintenanceReturn), item.PartNumber, quantity, null, location.Code), context, SourceContext, StockEntity, item.PartNumber));
        }, ct);
    }

    /// <summary>
    /// The part an issue stands in for (V2.9): only through an approved relationship usable in maintenance for
    /// this work order's asset and machine — maintenance-only alternates included, proposed ones never.
    /// </summary>
    private static async Task<(string Primary, AlternateInfo? Alternate, decimal Factor)> MaintenanceAlternateAsync(Kernel.Access.IKernelUnitOfWork uow, WorkOrderInfo order, string part, string? alternateFor, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(alternateFor))
        {
            return (part, null, 1m);
        }

        var primary = alternateFor.Trim().ToUpperInvariant();
        var context = new AlternateContext(DateOnly.FromDateTime(now.UtcDateTime), AssetCode: order.AssetCode, MachineCode: order.MachineCode, Maintenance: true);
        var options = Alternates.For(await uow.Store<IAlternateLookup>().ListAlternatesAsync(ct), primary).Where(x => x.OfferedPart == part).ToList();
        foreach (var (relationship, _, factor) in options)
        {
            if (Alternates.Evaluate(relationship, context).Usability == AlternateUsability.Usable)
            {
                return (primary, relationship, factor);
            }
        }

        var why = options.Count == 0
            ? $"{part} is not an alternate for {primary}."
            : string.Join(" ", options.SelectMany(o => Alternates.Evaluate(o.Relationship, context).Reasons).Distinct());
        throw new CommandRejectedException("alternate_not_approved", $"{part} cannot be issued in place of {primary} on work order {order.WorkOrderId}: {why}");
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
