using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Planning.Engines;
using Forge.Procurement.Contracts;
using Forge.Programs.Contracts;
using Forge.Resources.Contracts;

namespace Forge.Planning;

/// <summary>
/// Reads what MRP and material readiness need from Inventory, Procurement, Execution and Engineering
/// in one consistent snapshot, and turns it into engine inputs. All dates are local to the (first) site.
/// </summary>
internal static class MaterialData
{
    private const string Restricted = "Restricted program work";

    public sealed record Supply(string Key, string PartNumber, decimal Quantity, DateOnly DueDate, string Kind, string Description);

    /// <summary>Each part's lead time in days, for planning from an overdue supply (ADR 0057 §4.4).</summary>
    public static async Task<IReadOnlyDictionary<string, int>> LeadTimesAsync(IKernelUnitOfWork uow, CancellationToken ct) =>
        (await uow.Store<IEngineeringLookup>().ListItemsAsync(ct)).ToDictionary(i => i.PartNumber, i => i.LeadTimeDays, StringComparer.Ordinal);

    /// <summary>Today at the home site (ADR 0057 §1).</summary>
    public static Task<DateOnly> TodayAsync(IKernelUnitOfWork uow, TimeProvider time, CancellationToken ct) =>
        uow.SiteDateAsync(time.GetUtcNow(), ct);

    /// <summary>Approved purchase order lines and unfinished production orders, less what has been received.</summary>
    public static async Task<IReadOnlyList<Supply>> SuppliesAsync(IKernelUnitOfWork uow, ForgePrincipal? principal, CancellationToken ct,
        IReadOnlyList<OrderInfo>? orderHeaders = null)
    {
        var inventory = uow.Store<IInventoryLookup>();
        var received = (await inventory.ListPurchaseReceiptsAsync(ct)).ToDictionary(r => (r.PoNumber, r.LineNumber), r => r.Received);
        var produced = (await inventory.ListProductionReceiptsAsync(ct)).ToDictionary(r => r.OrderNumber, r => r.Received);
        var supplies = new List<Supply>();

        foreach (var po in (await uow.Store<IProcurementLookup>().ListPurchaseOrdersAsync(ct)).Where(p => p.Status == PurchaseOrderStatus.Approved))
        {
            foreach (var line in po.Lines.Where(l => l.Status == PurchaseLineStatus.Open))
            {
                var open = line.Quantity - received.GetValueOrDefault((po.PoNumber, line.LineNumber));
                if (open > 0)
                {
                    supplies.Add(new Supply($"PO{po.PoNumber}/{line.LineNumber}", line.PartNumber, open, line.PromisedDate, "PurchaseOrder", $"PO {po.PoNumber} line {line.LineNumber} ({po.Supplier})"));
                }
            }
        }

        foreach (var order in orderHeaders ?? await uow.Store<IExecutionLookup>().ListOrdersAsync(ct))
        {
            if (order.Status is not (OrderStatus.Planned or OrderStatus.FirmPlanned or OrderStatus.Released or OrderStatus.Held))
            {
                continue;
            }

            var open = order.Quantity - produced.GetValueOrDefault(order.OrderNumber);
            if (open > 0)
            {
                supplies.Add(new Supply($"WO{order.OrderNumber}", order.PartNumber, open, order.DueDate, "ProductionOrder", Visible(principal, order.ProgramId) ? $"Order {order.OrderNumber} ({order.Status})" : Restricted));
            }
        }

        return supplies.OrderBy(s => s.PartNumber, StringComparer.Ordinal).ThenBy(s => s.DueDate).ThenBy(s => s.Key, StringComparer.Ordinal).ToList();
    }

    /// <summary>Material readiness inputs: available stock, issues, firm supply and every released order.</summary>
    public static async Task<MaterialSnapshot> MaterialSnapshotAsync(IKernelUnitOfWork uow, TimeProvider time, CancellationToken ct,
        IReadOnlyList<OrderInfo>? orderHeaders = null, IReadOnlyList<OrderInfo>? loadedOrders = null,
        IReadOnlyList<StockPosition>? positions = null)
    {
        var inventory = uow.Store<IInventoryLookup>();
        var stock = (positions ?? await Forge.Inventory.Contracts.ShelfLifeQueries.PositionsAsync(uow, null, time.GetUtcNow(), ct))
            .Where(p => p.AvailableForPlanning)
            .GroupBy(b => b.PartNumber, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new MaterialStock(g.Key, g.Sum(b => b.Quantity)))
            .ToList();
        var issued = (await inventory.ListOrderIssuesAsync(ct)).Select(i => new MaterialIssued(i.OrderNumber, i.PartNumber, i.NetIssued)).ToList();
        var execution = uow.Store<IExecutionLookup>();
        orderHeaders ??= await execution.ListOrdersAsync(ct);
        var supplies = (await SuppliesAsync(uow, null, ct, orderHeaders)).Select(s => new MaterialSupply(s.Key, s.PartNumber, s.Quantity, s.DueDate, s.Description)).ToList();
        // These are full records read by this caller in the same unit of work, never a cross-snapshot cache.
        var known = (loadedOrders ?? []).ToDictionary(o => o.OrderNumber);
        var orders = new List<MaterialOrder>();
        foreach (var summary in orderHeaders.Where(o => o.Status == OrderStatus.Released).OrderBy(o => o.OrderNumber))
        {
            var order = known.GetValueOrDefault(summary.OrderNumber) ?? await execution.FindOrderAsync(summary.OrderNumber, ct);
            if (order is not null)
            {
                orders.Add(new MaterialOrder(order.OrderNumber, order.Priority, order.DueDate, order.Materials));
            }
        }

        var reservations = (await WipStorageQueries.OutstandingReservationsAsync(uow, ct))
            .Select(r => new MaterialReservation("PB" + r.BuildId.ToString(System.Globalization.CultureInfo.InvariantCulture), r.OrderNumber, r.PartNumber, r.Outstanding))
            .ToList();
        // Owner decision 2026-10-31: stock reserved for a released order is that order's, in readiness as on the floor.
        reservations.AddRange((await StockReservationQueries.OutstandingAsync(uow, null, ct))
            .Where(r => r.Reservation.Target == ReservationTarget.ProductionOrder && r.Outstanding > 0)
            .Select(r => new MaterialReservation("SR" + r.Reservation.ReservationId.ToString(System.Globalization.CultureInfo.InvariantCulture), r.Reservation.OrderNumber!.Value, r.Reservation.PartNumber, r.Outstanding)));
        return new MaterialSnapshot(await TodayAsync(uow, time, ct), stock, issued, supplies, orders, reservations);
    }

    /// <summary>MRP inputs. Descriptions of program work the reader cannot see are replaced, never the quantities.</summary>
    public static async Task<MrpSnapshot> MrpSnapshotAsync(IKernelUnitOfWork uow, TimeProvider time, ForgePrincipal principal, CancellationToken ct)
    {
        var today = await TodayAsync(uow, time, ct);
        var zone = await uow.SiteTimeZoneAsync(ct);
        DateOnly Local(DateTimeOffset at) => SiteTime.DateAt(at, zone);

        var engineering = uow.Store<IEngineeringLookup>();
        var itemList = await engineering.ListItemsAsync(ct);
        var mro = (await uow.Store<IMroLookup>().ListMroPoliciesAsync(ct)).ToDictionary(m => m.PartNumber, StringComparer.Ordinal);
        var items = itemList
            .Select(i => new MrpItem(i.PartNumber, i.Description, i.UnitOfMeasure, i.MakeBuy, i.LeadTimeDays, i.SafetyStock, i.LotPolicy, i.OrderQuantity, i.OrderMultiple,
                mro.TryGetValue(i.PartNumber, out var m) && i.MakeBuy == MakeBuy.Buy ? new MrpMroLevels(m.MinQuantity, m.ReorderPoint, m.MaxQuantity, m.CriticalSpare) : null))
            .ToList();
        var leadTimes = itemList.ToDictionary(i => i.PartNumber, i => i.LeadTimeDays, StringComparer.Ordinal);
        var structures = (await engineering.ListReleasedStructuresAsync(ct))
            .Where(s => s.Revision.EffectiveFrom is not null)
            .Select(s => new MrpStructure(
                s.Revision.PartNumber,
                s.Revision.Revision,
                Local(s.Revision.EffectiveFrom!.Value),
                s.Revision.EffectiveTo is { } to ? Local(to) : null,
                s.Bom))
            .Where(s => s.EffectiveTo is null || s.EffectiveTo > s.EffectiveFrom)
            .ToList();

        // V3.1: MRP nets released stock in nettable locations only; stock at an outside processor, in transit or in
        // quarantine is on hand but not available for planning. Inspection and hold are reported, never netted.
        var inventoryLookup = uow.Store<IInventoryLookup>();
        var stock = (await Forge.Inventory.Contracts.ShelfLifeQueries.PositionsAsync(uow, null, time.GetUtcNow(), ct))
            .GroupBy(b => b.PartNumber, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new MrpStock(
                g.Key,
                g.Where(b => b.AvailableForPlanning).Sum(b => b.Quantity),
                g.Where(b => b.QualityStatus == StockStatus.Inspection).Sum(b => b.Quantity),
                g.Where(b => b.QualityStatus == StockStatus.Hold).Sum(b => b.Quantity)))
            .ToList();

        var requirements = new List<MrpRequirement>();
        var shipped = (await uow.Store<IInventoryLookup>().ListShippedByDemandAsync(ct)).ToDictionary(d => (d.DemandId, d.PartNumber), d => d.Shipped);
        var toProjects = (await uow.Store<IInventoryLookup>().ListProjectMaterialAsync(null, ct))
            .GroupBy(m => Forge.Projects.Contracts.ProjectSource.Key(m.ProjectCode, m.LineNumber), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(m => m.NetIssued), StringComparer.Ordinal);
        foreach (var listed in await uow.Store<IDemandStore>().ListAsync(false, ct))
        {
            // Only what has not shipped yet is still demand; project demand is met by what is issued to its line (V2.10).
            var met = shipped.GetValueOrDefault((listed.DemandId, listed.PartNumber))
                + (listed.SourceKey is { } source && toProjects.TryGetValue(source, out var issuedToProject) ? issuedToProject : 0);
            var demand = listed with { Quantity = listed.Quantity - met };
            if (demand.Quantity <= 0)
            {
                continue;
            }

            var label = Visible(principal, demand.ProgramId)
                ? $"Demand {demand.DemandId} ({Spaced(demand.Kind.ToString())}{(demand.Customer is null ? string.Empty : ", " + demand.Customer)})"
                : Restricted;
            requirements.Add(new MrpRequirement($"D{demand.DemandId}", demand.PartNumber, demand.Quantity, demand.NeedDate, demand.Priority, "Demand", label));
        }

        // Material for orders that already exist: released orders still need what has not been issued
        // (needed now); planned and firm orders need their revision's BOM one lead time before they are due.
        var issued = (await uow.Store<IInventoryLookup>().ListOrderIssuesAsync(ct)).ToDictionary(i => (i.OrderNumber, i.PartNumber), i => i.NetIssued);
        var execution = uow.Store<IExecutionLookup>();
        var customers = (await uow.Store<IProgramsLookup>().ListProgramsAsync(ct)).ToDictionary(p => p.ProgramId, p => p.Customer);
        MrpLineContext? LineOf(OrderInfo order, string component, int? lineNumber)
        {
            var bom = structures.FirstOrDefault(s => s.PartNumber == order.PartNumber && s.Revision == order.Revision)?.Bom ?? [];
            var line = bom.FirstOrDefault(l => lineNumber is { } n ? l.LineNumber == n : l.ComponentPartNumber == component);
            return line is null ? null : new MrpLineContext(order.PartNumber, line.LineNumber, line.Policy, order.ProgramId, order.ProgramId is { } p ? customers.GetValueOrDefault(p) : null);
        }

        foreach (var summary in (await execution.ListOrdersAsync(ct)).OrderBy(o => o.OrderNumber))
        {
            var label = Visible(principal, summary.ProgramId) ? $"Order {summary.OrderNumber} ({summary.PartNumber})" : Restricted;
            if (summary.Status is OrderStatus.Released or OrderStatus.Held)
            {
                var order = await execution.FindOrderAsync(summary.OrderNumber, ct);
                foreach (var group in (order?.Materials ?? []).GroupBy(m => m.ComponentPartNumber, StringComparer.Ordinal))
                {
                    var left = group.Sum(m => m.RequiredQuantity) - issued.GetValueOrDefault((summary.OrderNumber, group.Key));
                    if (left > 0)
                    {
                        requirements.Add(new MrpRequirement($"WO{summary.OrderNumber}/{group.Key}", group.Key, left, today, summary.Priority, "OrderMaterial", label + " material still to issue", LineOf(summary, group.Key, null)));
                    }
                }
            }
            else if (summary.Status is OrderStatus.Planned or OrderStatus.FirmPlanned)
            {
                var bom = structures.FirstOrDefault(s => s.PartNumber == summary.PartNumber && s.Revision == summary.Revision)?.Bom ?? [];
                var need = summary.DueDate.AddDays(-leadTimes.GetValueOrDefault(summary.PartNumber));
                foreach (var line in bom)
                {
                    var quantity = decimal.Round(summary.Quantity * line.QuantityPer * (1 + (line.ScrapPercent / 100m)), 6, MidpointRounding.AwayFromZero);
                    requirements.Add(new MrpRequirement($"WO{summary.OrderNumber}/{line.LineNumber}", line.ComponentPartNumber, quantity, need, summary.Priority, "OrderMaterial", $"{label} {summary.Status}, rev {summary.Revision} line {line.LineNumber}", LineOf(summary, line.ComponentPartNumber, line.LineNumber)));
                }
            }
        }

        var supplies = (await SuppliesAsync(uow, principal, ct)).Select(s => new MrpSupply(s.Key, s.PartNumber, s.Quantity, s.DueDate, s.Kind, s.Description)).ToList();
        return new MrpSnapshot(today, items, structures, stock, requirements, supplies, await uow.Store<IAlternateLookup>().ListAlternatesAsync(ct));
    }

    private static bool Visible(ForgePrincipal? principal, Guid? program) =>
        program is not { } p || principal is null || principal.Programs.Contains(new ProgramId(p));

    private static string Spaced(string pascal)
    {
        var text = new System.Text.StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            if (i > 0 && char.IsUpper(pascal[i]))
            {
                text.Append(' ').Append(char.ToLowerInvariant(pascal[i]));
            }
            else
            {
                text.Append(pascal[i]);
            }
        }

        return text.ToString();
    }
}
