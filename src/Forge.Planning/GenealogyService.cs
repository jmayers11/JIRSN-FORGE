using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Maintenance.Contracts;
using Forge.Planning.Engines;
using Forge.Procurement.Contracts;
using Forge.Quality.Contracts;

namespace Forge.Planning;

/// <summary>What one component line of an order's as-built record amounts to: issues less returns, as recorded.</summary>
public sealed record GenealogyComponent(
    string PartNumber,
    string? Revision,
    string? LotNumber,
    bool Serial,
    decimal Quantity,
    string? InPlaceOf,
    decimal? PrimaryQuantity,
    long? AlternateId,
    string? AlternateKind,
    string? AlternateApprovedBy,
    DateTimeOffset? AlternateApprovedAt,
    string? AlternateConditions,
    string? Supplier,
    string? SupplierCode,
    long? PoNumber,
    int? PoLine,
    DateTimeOffset? ReceivedAt,
    long? MadeOnOrder,
    IReadOnlyList<long> Transactions);

/// <summary>Someone who worked on an operation of the order, with the qualifications recorded when they started.</summary>
public sealed record GenealogyLabor(string Person, int Sequence, string WorkCenter, string? Machine, string Role, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, IReadOnlyList<string>? Qualifications);

/// <param name="Children">Genealogy of components that were made in-house (their own orders), as far down as recorded.</param>
public sealed record OrderGenealogy(
    long OrderNumber,
    string PartNumber,
    string Revision,
    decimal Quantity,
    OrderStatus Status,
    DateTimeOffset? ReleasedAt,
    string? ReleasedBy,
    IReadOnlyList<GenealogyComponent> Components,
    IReadOnlyList<AsBuiltLine> Lines,
    IReadOnlyList<GenealogyLabor> Labor,
    IReadOnlyList<InspectionInfo> Inspections,
    IReadOnlyList<NcrInfo> Ncrs,
    IReadOnlyList<DocumentApplicability> Documents,
    IReadOnlyList<string> Produced,
    IReadOnlyList<OrderGenealogy> Children,
    IReadOnlyList<string> Notes);

/// <param name="UsedIn">Orders this serial (or lot) was itself issued to — where it went.</param>
public sealed record SerialGenealogy(
    string PartNumber,
    string Serial,
    string? Revision,
    long? OrderNumber,
    DateTimeOffset? ProducedAt,
    OrderGenealogy? Order,
    IReadOnlyList<ShipmentInfo> Shipments,
    IReadOnlyList<AsBuiltLine> UsedIn,
    IReadOnlyList<GenealogyComponent> Received,
    string Summary);

public sealed record AssetComponentGenealogy(AssetComponentInfo Component, string? LotNumber, string? Supplier, long? PoNumber, int? PoLine, string? Revision, long? MadeOnOrder, string Source);

public sealed record AssetGenealogy(AssetInfo Asset, IReadOnlyList<AssetComponentGenealogy> Components, IReadOnlyList<AssetEvent> History);

/// <summary>
/// Genealogy (V2.11) from the records only: a serial's production receipt gives its order and revision; the
/// order's as-built record gives each component as issued — revision, lot or serial, the approved alternate and
/// who approved it, supplier and purchase order — and components made in-house lead to their own orders; labor
/// records give who worked each operation with the qualifications recorded when they started; inspections, NCRs
/// and shipments are read as recorded; documents are those in force when the order was released. Nothing is
/// taken from today's item, alternate or supplier data.
/// </summary>
public sealed class GenealogyService(IKernelUnitOfWorkFactory unitOfWork, IForgeAuthorizer authorizer)
{
    private const int MaxDepth = 5;

    public async Task<SerialGenealogy> SerialAsync(ForgePrincipal principal, string? partNumber, string serial, CancellationToken ct)
    {
        Demand(principal, Permissions.OrdersRead, "Genealogy");
        var number = (serial ?? string.Empty).Trim().ToUpperInvariant();
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var inventory = uow.Store<IInventoryLookup>();
        var lots = (await inventory.ListLotsAsync(string.IsNullOrWhiteSpace(partNumber) ? null : BusinessCode.Require(partNumber, "Part"), ct)).Where(l => l.LotNumber == number).ToList();
        if (lots.Count == 0)
        {
            throw new CommandRejectedException("serial_not_found", $"Serial {number} is not recorded{(string.IsNullOrWhiteSpace(partNumber) ? string.Empty : " for " + partNumber.Trim().ToUpperInvariant())}.");
        }

        if (lots.Count > 1)
        {
            throw new CommandRejectedException("serial_ambiguous", $"Serial {number} exists for {string.Join(", ", lots.Select(l => l.PartNumber))}; name the part.");
        }

        var lot = lots[0];
        var ledger = (await inventory.ListTransactionsAsync(lot.PartNumber, null, 1000, ct)).Where(t => t.LotNumber == number).OrderBy(t => t.TransactionId).ToList();
        var produced = ledger.FirstOrDefault(t => t.Kind == InventoryTransactionKind.ProductionReceipt);
        OrderGenealogy? order = null;
        if (produced?.OrderNumber is { } orderNumber)
        {
            order = await OrderAsync(uow, principal, orderNumber, 0, ct) ?? throw new CommandRejectedException("serial_not_found", $"Serial {number} is not recorded.");
        }

        var received = ledger.Where(t => t.Kind == InventoryTransactionKind.Receipt).ToList();
        var bought = new List<GenealogyComponent>();
        foreach (var r in received)
        {
            var po = r.PoNumber is { } p ? await uow.Store<IProcurementLookup>().FindPurchaseOrderAsync(p, ct) : null;
            bought.Add(new GenealogyComponent(lot.PartNumber, r.PartRevision ?? lot.PartRevision, number, true, r.Quantity, null, null, null, null, null, null, null, po?.Supplier, po?.SupplierCode, r.PoNumber, r.PoLine, r.At, null, [r.TransactionId]));
        }

        var shipments = (await inventory.ListShipmentsAsync(true, ct))
            .Where(s => s.Status == ShipmentStatus.Shipped && s.Lines.Any(l => l.PartNumber == lot.PartNumber && l.LotNumber == number)).ToList();
        var usedIn = await uow.Store<IAsBuiltRecord>().ListAsync(null, lot.PartNumber, number, ct);
        var summary = order is not null
            ? $"{lot.PartNumber} serial {number}, rev {lot.PartRevision ?? order.Revision}, built on order {order.OrderNumber} from {order.Components.Count} component line(s)"
            : received.Count > 0 ? $"{lot.PartNumber} serial {number}, bought{(bought[0].Supplier is { } s ? " from " + s : string.Empty)}{(bought[0].PoNumber is { } n ? $" on PO {n}" : string.Empty)}"
            : $"{lot.PartNumber} serial {number}";
        summary += shipments.Count > 0 ? $"; shipped on shipment {string.Join(", ", shipments.Select(s => s.ShipmentId))} to {shipments[^1].Customer}." : usedIn.Count > 0 ? $"; issued to order {string.Join(", ", usedIn.Select(u => u.OrderNumber).Distinct())}." : "; not shipped.";
        return new SerialGenealogy(lot.PartNumber, number, lot.PartRevision, produced?.OrderNumber, produced?.At, order, shipments, usedIn, bought, summary);
    }

    public async Task<OrderGenealogy> OrderAsync(ForgePrincipal principal, long orderNumber, CancellationToken ct)
    {
        Demand(principal, Permissions.OrdersRead, "Genealogy");
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        return await OrderAsync(uow, principal, orderNumber, 0, ct) ?? throw new CommandRejectedException("order_not_found", $"Order {orderNumber} does not exist.");
    }

    public async Task<AssetGenealogy> AssetAsync(ForgePrincipal principal, string assetCode, CancellationToken ct)
    {
        Demand(principal, Permissions.MaintenanceRead, "AssetGenealogy");
        var code = BusinessCode.Require(assetCode, "Asset");
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var assets = uow.Store<IAssetLookup>();
        var asset = await assets.FindAssetAsync(code, ct) ?? throw new CommandRejectedException("asset_not_found", $"Asset {code} does not exist.");
        var inventory = uow.Store<IInventoryLookup>();
        var procurement = uow.Store<IProcurementLookup>();
        var list = new List<AssetComponentGenealogy>();
        foreach (var c in await assets.ListComponentsAsync(code, null, true, ct))
        {
            if (c.SourceTransactionId is not { } source)
            {
                list.Add(new AssetComponentGenealogy(c, null, null, null, null, c.Revision, null,
                    c.InstalledByWorkOrder is { } w ? $"Recorded by work order {w}; the part was not issued through stores." : "Recorded as delivered with the asset."));
                continue;
            }

            var issue = (await inventory.ListTransactionsAsync(c.PartNumber, null, 1000, ct)).FirstOrDefault(t => t.TransactionId == source);
            if (issue?.LotNumber is not { } lot)
            {
                list.Add(new AssetComponentGenealogy(c, null, null, null, null, c.Revision, null, $"Issued from stores (transaction {source}); the part is not lot-tracked, so its supplier is not traced."));
                continue;
            }

            var ledger = (await inventory.ListTransactionsAsync(c.PartNumber, null, 1000, ct)).Where(t => t.LotNumber == lot).OrderBy(t => t.TransactionId).ToList();
            var receipt = ledger.FirstOrDefault(t => t.Kind == InventoryTransactionKind.Receipt && t.PoNumber is not null);
            var made = ledger.FirstOrDefault(t => t.Kind == InventoryTransactionKind.ProductionReceipt)?.OrderNumber;
            var po = receipt?.PoNumber is { } p ? await procurement.FindPurchaseOrderAsync(p, ct) : null;
            var revision = (await inventory.ListLotsAsync(c.PartNumber, ct)).FirstOrDefault(l => l.LotNumber == lot)?.PartRevision ?? c.Revision;
            list.Add(new AssetComponentGenealogy(c, lot, po?.Supplier, receipt?.PoNumber, receipt?.PoLine, revision, made,
                $"Issued from stores (transaction {source}), {(made is { } m ? $"made on order {m}" : po is null ? "received without a purchase order" : $"bought from {po.Supplier} on PO {po.PoNumber} line {receipt!.PoLine}")}."));
        }

        return new AssetGenealogy(asset, list, await assets.ListAssetHistoryAsync(code, ct));
    }

    private static async Task<OrderGenealogy?> OrderAsync(IKernelUnitOfWork uow, ForgePrincipal principal, long orderNumber, int depth, CancellationToken ct)
    {
        var order = await uow.Store<IExecutionLookup>().FindOrderAsync(orderNumber, ct);
        if (order is null || (order.ProgramId is { } program && !principal.Programs.Contains(new ProgramId(program))))
        {
            return null;
        }

        var notes = new List<string>();
        var lines = await uow.Store<IAsBuiltRecord>().ListAsync(order.OrderNumber, null, null, ct);
        var inventory = uow.Store<IInventoryLookup>();
        var orderLedger = await inventory.ListTransactionsAsync(null, order.OrderNumber, 1000, ct);
        var recorded = lines.Select(l => l.TransactionId).ToHashSet();
        var unrecorded = orderLedger.Count(t => t.Kind is InventoryTransactionKind.Issue or InventoryTransactionKind.Return && !recorded.Contains(t.TransactionId));
        if (unrecorded > 0)
        {
            notes.Add($"{unrecorded} issue(s) or return(s) on this order were made before the as-built record existed (V2.11); they appear in the ledger but not here.");
        }

        var components = new List<GenealogyComponent>();
        var children = new List<OrderGenealogy>();
        foreach (var g in lines.GroupBy(l => (l.ComponentPart, l.LotNumber, l.SubstitutesFor, l.AlternateId)))
        {
            var quantity = g.Sum(l => l.Kind == AsBuiltKind.Issued ? l.Quantity : -l.Quantity);
            if (quantity <= 0)
            {
                continue;
            }

            var first = g.First(l => l.Kind == AsBuiltKind.Issued);
            long? madeOn = null;
            if (first.LotNumber is { } lot)
            {
                madeOn = (await inventory.ListTransactionsAsync(first.ComponentPart, null, 1000, ct))
                    .Where(t => t.LotNumber == lot && t.Kind == InventoryTransactionKind.ProductionReceipt).Select(t => t.OrderNumber).FirstOrDefault();
                if (madeOn is { } child && depth < MaxDepth && children.All(c => c.OrderNumber != child))
                {
                    if (await OrderAsync(uow, principal, child, depth + 1, ct) is { } sub)
                    {
                        children.Add(sub);
                    }
                    else
                    {
                        notes.Add($"{first.ComponentPart} {lot} was made on order {child}, which you cannot see.");
                    }
                }
            }

            decimal? primary = first.SubstitutesFor is null ? null : g.Sum(l => (l.Kind == AsBuiltKind.Issued ? 1 : -1) * (l.PrimaryQuantity ?? 0));
            components.Add(new GenealogyComponent(first.ComponentPart, first.ComponentRevision, first.LotNumber, first.Serial, quantity, first.SubstitutesFor, primary,
                first.AlternateId, first.AlternateKind, first.AlternateApprovedBy, first.AlternateApprovedAt, first.AlternateConditions,
                first.SupplierName, first.SupplierCode, first.PoNumber, first.PoLine, first.ReceivedAt, madeOn, g.Select(l => l.TransactionId).ToList()));
        }

        var execution = uow.Store<IExecutionLookup>();
        var labor = (await execution.ListLaborAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, order.OrderNumber, ct))
            .Select(l => new GenealogyLabor($"person:{l.PersonId}", l.Sequence, l.WorkCenterCode, l.MachineCode, l.Role.ToString(), l.StartedAt, l.EndedAt, l.Qualifications))
            .ToList();
        if (labor.Any(l => l.Qualifications is null))
        {
            notes.Add("Labor recorded before V2.11 has no qualifications kept with it.");
        }

        var quality = uow.Store<IQualityLookup>();
        var inspections = await quality.ListInspectionsAsync(order.OrderNumber, 500, ct);
        var ncrs = (await quality.ListNcrsAsync(true, ct)).Where(n => n.OrderNumber == order.OrderNumber).ToList();
        var documents = order.ReleasedAt is { } released
            ? (await DocumentationService.ForOrderAsync(uow, order, released, principal, ct)).Documents
            : [];
        if (order.ReleasedAt is not null)
        {
            notes.Add("Documents are those in force when the order was released, read from the revisions' effective dates.");
        }

        var produced = orderLedger.Where(t => t.Kind == InventoryTransactionKind.ProductionReceipt).OrderBy(t => t.TransactionId).Select(t => t.LotNumber ?? $"{t.Quantity.ToString("0.###", CultureInfo.InvariantCulture)} (no lot)").ToList();
        return new OrderGenealogy(order.OrderNumber, order.PartNumber, order.Revision, order.Quantity, order.Status, order.ReleasedAt, order.ReleasedBy,
            components, lines, labor, inspections, ncrs, documents, produced, children, notes);
    }

    private void Demand(ForgePrincipal principal, string permission, string resource)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var decision = authorizer.Authorize(principal, permission, ResourceRef.Of(resource));
        if (!decision.IsPermitted)
        {
            throw new ForgeAuthorizationException(decision);
        }
    }
}
