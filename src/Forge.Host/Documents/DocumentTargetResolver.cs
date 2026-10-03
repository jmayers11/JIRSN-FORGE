using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Security;
using Forge.Maintenance.Contracts;
using Forge.Procurement.Contracts;
using Forge.Programs.Contracts;
using Forge.Quality.Contracts;
using Forge.Resources.Contracts;

namespace Forge.Host.Documents;

/// <summary>
/// Finds the record a document is attached to through its owning context's contracts, with the program
/// it belongs to and the permission needed to see it. Item revisions are written PART:REV.
/// </summary>
public sealed class DocumentTargetResolver : IDocumentTargetResolver
{
    public async Task<DocumentTarget?> ResolveAsync(IKernelUnitOfWork uow, DocumentTargetType type, string id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var key = (id ?? string.Empty).Trim();
        switch (type)
        {
            case DocumentTargetType.Item:
                {
                    var item = await uow.Store<IEngineeringLookup>().FindItemAsync(key.ToUpperInvariant(), ct);
                    return item is null ? null : new DocumentTarget(type, item.PartNumber, $"Part {item.PartNumber}", null, Permissions.EngineeringRead);
                }

            case DocumentTargetType.ItemRevision:
                {
                    var parts = key.ToUpperInvariant().Split(':');
                    if (parts.Length != 2)
                    {
                        return null;
                    }

                    var detail = await uow.Store<IEngineeringLookup>().GetRevisionDetailAsync(parts[0], parts[1], ct);
                    return detail is null ? null : new DocumentTarget(type, $"{parts[0]}:{parts[1]}", $"Part {parts[0]} revision {parts[1]}", null, Permissions.EngineeringRead);
                }

            case DocumentTargetType.Supplier:
                {
                    var supplier = await uow.Store<IProcurementLookup>().FindSupplierAsync(key.ToUpperInvariant(), ct);
                    return supplier is null ? null : new DocumentTarget(type, supplier.Code, $"Supplier {supplier.Name}", null, Permissions.PurchasingRead);
                }

            case DocumentTargetType.Program:
                {
                    if (!Guid.TryParse(key, out var programId))
                    {
                        return null;
                    }

                    var program = await uow.Store<IProgramsLookup>().FindProgramAsync(programId, ct);
                    return program is null ? null : new DocumentTarget(type, program.ProgramId.ToString(), $"Program {program.Code}", program.ProgramId, Permissions.ProgramsRead);
                }

            case DocumentTargetType.ProductionOrder:
                {
                    if (!long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                    {
                        return null;
                    }

                    var order = await uow.Store<IExecutionLookup>().FindOrderAsync(number, ct);
                    return order is null ? null : new DocumentTarget(type, Text(number), $"Order {number}", order.ProgramId, Permissions.OrdersRead);
                }

            case DocumentTargetType.PurchaseOrder:
                {
                    if (!long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                    {
                        return null;
                    }

                    var po = await uow.Store<IProcurementLookup>().FindPurchaseOrderAsync(number, ct);
                    return po is null ? null : new DocumentTarget(type, Text(number), $"PO {number} ({po.Supplier})", null, Permissions.PurchasingRead);
                }

            case DocumentTargetType.WorkOrder:
                {
                    if (!long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                    {
                        return null;
                    }

                    var wo = await uow.Store<IMaintenanceLookup>().FindWorkOrderAsync(number, ct);
                    return wo is null ? null : new DocumentTarget(type, Text(number), $"Work order {number} ({wo.MachineCode})", null, Permissions.MaintenanceRead);
                }

            case DocumentTargetType.Ncr:
                {
                    if (!long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                    {
                        return null;
                    }

                    var ncr = await uow.Store<IQualityLookup>().FindNcrAsync(number, ct);
                    if (ncr is null)
                    {
                        return null;
                    }

                    // An NCR against a program order belongs to that program.
                    var program = ncr.OrderNumber is { } orderNumber ? (await uow.Store<IExecutionLookup>().FindOrderAsync(orderNumber, ct))?.ProgramId : null;
                    return new DocumentTarget(type, Text(number), $"NCR {number}", program, Permissions.QualityRead);
                }

            case DocumentTargetType.WorkCenter:
                {
                    var wc = await uow.Store<IResourcesLookup>().FindWorkCenterAsync(key.ToUpperInvariant(), ct);
                    return wc is null ? null : new DocumentTarget(type, wc.Code, $"Work center {wc.Code}", null, Permissions.ResourcesRead);
                }

            default:
                return null;
        }
    }

    private static string Text(long number) => number.ToString(CultureInfo.InvariantCulture);
}
