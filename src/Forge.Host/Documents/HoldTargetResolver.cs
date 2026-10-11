using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Holds;
using Forge.Procurement.Contracts;
using Forge.Programs.Contracts;
using Forge.Resources.Contracts;

namespace Forge.Host.Documents;

/// <summary>Finds the record a hold is placed on (V3.10) through its owning context's contracts, with its program.</summary>
public sealed class HoldTargetResolver : IHoldTargetResolver
{
    public async Task<HoldTarget?> ResolveAsync(IKernelUnitOfWork uow, HoldTargetType type, string id, string? part, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var key = (id ?? string.Empty).Trim();
        var code = key.ToUpperInvariant();
        switch (type)
        {
            case HoldTargetType.Order:
                {
                    var order = Number(key) is { } n ? await uow.Store<IExecutionLookup>().FindOrderAsync(n, ct) : null;
                    return order is null ? null : new HoldTarget(type, Text(order.OrderNumber), null, $"order {order.OrderNumber}", order.ProgramId);
                }

            case HoldTargetType.Operation:
                {
                    var parts = key.Split('/');
                    if (parts.Length != 2 || Number(parts[0]) is not { } n || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
                    {
                        return null;
                    }

                    var order = await uow.Store<IExecutionLookup>().FindOrderAsync(n, ct);
                    return order is null || order.Operations.All(o => o.Sequence != sequence)
                        ? null
                        : new HoldTarget(type, $"{Text(n)}/{sequence.ToString(CultureInfo.InvariantCulture)}", null, $"operation {n}/{sequence}", order.ProgramId);
                }

            case HoldTargetType.Part:
                {
                    var item = await uow.Store<IEngineeringLookup>().FindItemAsync(code, ct);
                    return item is null ? null : new HoldTarget(type, item.PartNumber, null, $"part {item.PartNumber}", null);
                }

            case HoldTargetType.Lot:
                {
                    var partCode = string.IsNullOrWhiteSpace(part) ? null : part.Trim().ToUpperInvariant();
                    var lots = (await uow.Store<IInventoryLookup>().ListLotsAsync(partCode, ct)).Where(l => string.Equals(l.LotNumber, key, StringComparison.OrdinalIgnoreCase)).ToList();
                    return lots.Count == 0 ? null : new HoldTarget(type, lots[0].LotNumber, partCode ?? (lots.Count == 1 ? lots[0].PartNumber : null), $"lot {lots[0].LotNumber}", null);
                }

            case HoldTargetType.WorkCenter:
                {
                    var wc = await uow.Store<IResourcesLookup>().FindWorkCenterAsync(code, ct);
                    return wc is null ? null : new HoldTarget(type, wc.Code, null, $"work center {wc.Code}", null);
                }

            case HoldTargetType.Machine:
                {
                    var machine = await uow.Store<IResourcesLookup>().FindMachineAsync(code, ct);
                    return machine is null ? null : new HoldTarget(type, machine.Code, null, $"machine {machine.Code}", null);
                }

            case HoldTargetType.Supplier:
                {
                    var supplier = await uow.Store<IProcurementLookup>().FindSupplierAsync(code, ct);
                    return supplier is null ? null : new HoldTarget(type, supplier.Code, null, $"supplier {supplier.Name}", null);
                }

            case HoldTargetType.PurchaseOrder:
                {
                    var po = Number(key) is { } n ? await uow.Store<IProcurementLookup>().FindPurchaseOrderAsync(n, ct) : null;
                    return po is null ? null : new HoldTarget(type, Text(po.PoNumber), null, $"PO {po.PoNumber}", null);
                }

            case HoldTargetType.Shipment:
                {
                    var shipment = Number(key) is { } n ? await uow.Store<IInventoryLookup>().FindShipmentAsync(n, ct) : null;
                    return shipment is null ? null : new HoldTarget(type, Text(shipment.ShipmentId), null, $"shipment {shipment.ShipmentId} to {shipment.Customer}", null);
                }

            case HoldTargetType.Program:
                {
                    var program = Guid.TryParse(key, out var programId) ? await uow.Store<IProgramsLookup>().FindProgramAsync(programId, ct) : null;
                    return program is null ? null : new HoldTarget(type, program.ProgramId.ToString(), null, $"program {program.Code}", program.ProgramId);
                }

            default:
                return null;
        }
    }

    private static long? Number(string key) => long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static string Text(long number) => number.ToString(CultureInfo.InvariantCulture);
}
