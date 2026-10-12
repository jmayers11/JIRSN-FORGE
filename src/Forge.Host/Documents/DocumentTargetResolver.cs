using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
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
                    return wo is null ? null : new DocumentTarget(type, Text(number), $"Work order {number} ({wo.Target})", null, Permissions.MaintenanceRead);
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

            case DocumentTargetType.Receipt:
                {
                    if (!long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return null;
                    var receipt = await uow.Store<IReceivingLookup>().FindTransactionAsync(number, ct);
                    if (receipt is null || receipt.Kind is not (InventoryTransactionKind.Receipt or InventoryTransactionKind.CustomerReturn)) return null;
                    var program = receipt.OrderNumber is { } orderNumber ? (await uow.Store<IExecutionLookup>().FindOrderAsync(orderNumber, ct))?.ProgramId : null;
                    return new DocumentTarget(type, Text(number), $"Receipt {number} ({receipt.PartNumber})", program, Permissions.InventoryRead);
                }

            case DocumentTargetType.ReceivingInspection:
                {
                    if (!long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return null;
                    var inspection = (await uow.Store<IReceivingLookup>().ListReceivingInspectionsAsync(null, ct)).FirstOrDefault(i => i.InspectionId == number);
                    if (inspection is null) return null;
                    var receipt = await ResolveAsync(uow, DocumentTargetType.Receipt, Text(inspection.ReceiptTransactionId), ct);
                    return receipt is null ? null : new DocumentTarget(type, Text(number), $"Receiving inspection {number}", receipt.ProgramId, Permissions.QualityRead);
                }

            case DocumentTargetType.Shipment:
                {
                    if (!long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return null;
                    var shipment = await uow.Store<IInventoryLookup>().FindShipmentAsync(number, ct);
                    return shipment is null ? null : new DocumentTarget(type, Text(number), $"Shipment {number} ({shipment.Customer})", null, Permissions.InventoryRead);
                }

            case DocumentTargetType.OutsideProcess:
                {
                    var configurations = await uow.Store<IOutsideProcessLookup>().ListDispatchesAsync(key, ct);
                    var configured = configurations.Count == 0 ? null : configurations[0];
                    if (configured is not null)
                        return new DocumentTarget(type, key, $"Outside processing {configured.PartNumber} lot {configured.LotNumber} PO {configured.PurchaseOrder}/{configured.PurchaseOrderLine}", configured.ProgramId, Permissions.InventoryRead, configured.Markings);
                    var transactions = await uow.Store<IInventoryLookup>().ListTransactionsAsync(null, null, 100_000, ct);
                    var matched = transactions.FirstOrDefault(t => t.Kind == InventoryTransactionKind.Transfer && t.PoNumber is not null && t.PoLine is not null &&
                        t.Reason?.StartsWith("Outside processing ", StringComparison.Ordinal) == true && $"{t.PartNumber}:{t.LotNumber}:PO{t.PoNumber}-L{t.PoLine}" == key);
                    return matched is null ? null : new DocumentTarget(type, key, $"Outside processing {matched.PartNumber} lot {matched.LotNumber} PO {matched.PoNumber}/{matched.PoLine}", null, Permissions.InventoryRead);
                }

            case DocumentTargetType.Fai:
                {
                    if (!long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return null;
                    var fai = (await uow.Store<IDeterminationLookup>().ListFaisAsync(null, ct)).FirstOrDefault(f => f.FaiId == number);
                    if (fai is null) return null;
                    var report = await uow.Store<IDocumentLookup>().FindDocumentAsync(fai.DocumentNumber, ct);
                    return report is null ? null : new DocumentTarget(type, Text(number), $"FAI {number} ({fai.Subject})", report.ProgramId, Permissions.QualityRead, report.Markings);
                }

            case DocumentTargetType.Inspection:
                {
                    if (!long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return null;
                    var inspection = (await uow.Store<IQualityLookup>().ListInspectionsAsync(null, int.MaxValue, ct)).FirstOrDefault(i => i.InspectionId == number);
                    if (inspection is null) return null;
                    var order = await uow.Store<IExecutionLookup>().FindOrderAsync(inspection.OrderNumber, ct);
                    return order is null ? null : new DocumentTarget(type, Text(number), $"Inspection {number} (order {order.OrderNumber})", order.ProgramId, Permissions.QualityRead);
                }

            case DocumentTargetType.Rfq:
                {
                    if (!long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return null;
                    var rfq = await uow.Store<ISourcingLookup>().FindRfqAsync(number, ct);
                    var need = rfq is null ? null : await uow.Store<IRequisitionLookup>().FindRequisitionAsync(rfq.RequisitionId, ct);
                    return need is null ? null : new DocumentTarget(type, Text(number), $"RFQ {number}", need.ProgramId, Permissions.PurchasingRead);
                }

            case DocumentTargetType.SupplierQuote:
                {
                    if (!long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return null;
                    var quote = (await uow.Store<ISourcingLookup>().ListQuotesAsync(null, ct)).FirstOrDefault(q => q.QuoteId == number);
                    if (quote is null) return null;
                    var rfq = await uow.Store<ISourcingLookup>().FindRfqAsync(quote.RfqId, ct);
                    var need = rfq is null ? null : await uow.Store<IRequisitionLookup>().FindRequisitionAsync(rfq.RequisitionId, ct);
                    var original = await uow.Store<IDocumentLookup>().FindDocumentAsync(quote.OriginalDocumentNumber, ct);
                    return need is null || original is null ? null : new DocumentTarget(type, Text(number), $"Supplier quote {number}", need.ProgramId, Permissions.PurchasingRead, original.Markings);
                }

            default:
                return null;
        }
    }

    private static string Text(long number) => number.ToString(CultureInfo.InvariantCulture);
}
