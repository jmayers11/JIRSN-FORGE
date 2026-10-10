using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Maintenance.Contracts;
using Forge.Procurement.Contracts;

namespace Forge.Planning;

/// <summary>Reads the genealogy graph (V3.3) for one reader: orders in programs they cannot see are left out.</summary>
public sealed class GenealogyGraphService(IKernelUnitOfWorkFactory unitOfWork, IForgeAuthorizer authorizer)
{
    public async Task<GenealogyGraph> GraphAsync(ForgePrincipal principal, CancellationToken ct)
    {
        Demand(principal);
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        return await BuildAsync(uow, principal, ct);
    }

    /// <summary>Traces from an instance (part and lot or serial), an order or a shipment.</summary>
    public async Task<GenealogyTrace> TraceAsync(ForgePrincipal principal, string? part, string? lot, long? order, long? shipment, TraceDirection direction, CancellationToken ct)
    {
        var graph = await GraphAsync(principal, ct);
        string start;
        if (order is { } o)
        {
            start = GenealogyGraph.OrderId(o);
        }
        else if (shipment is { } s)
        {
            start = GenealogyGraph.ShipmentId(s);
        }
        else
        {
            var number = string.IsNullOrWhiteSpace(lot) ? throw new CommandRejectedException("instance_required", "Name a lot or serial, an order or a shipment to trace from.") : lot.Trim().ToUpperInvariant();
            var parts = string.IsNullOrWhiteSpace(part)
                ? graph.Nodes.Where(n => n.Kind == GenealogyNodeKind.Instance && n.LotNumber == number).Select(n => n.PartNumber!).Distinct(StringComparer.Ordinal).ToList()
                : [BusinessCode.Require(part, "Part")];
            if (parts.Count > 1)
            {
                throw new CommandRejectedException("instance_ambiguous", $"{number} exists for {string.Join(", ", parts)}; name the part.");
            }

            start = parts.Count == 1 ? GenealogyGraph.InstanceId(parts[0], number) : string.Empty;
        }

        if (graph.Find(start) is null)
        {
            throw new CommandRejectedException("trace_not_found", "Nothing is recorded for that yet: no receipt, split, issue, production, shipment or install names it, or it is in a program you cannot see.");
        }

        return graph.Trace(start, direction);
    }

    internal static async Task<GenealogyGraph> BuildAsync(IKernelUnitOfWork uow, ForgePrincipal principal, CancellationToken ct)
    {
        var inventory = uow.Store<IInventoryLookup>();
        var hidden = (await uow.Store<IExecutionLookup>().ListOrdersAsync(ct))
            .Where(o => o.ProgramId is { } p && !principal.Programs.Contains(new ProgramId(p)))
            .Select(o => o.OrderNumber)
            .ToHashSet();
        var suppliers = (await uow.Store<IProcurementLookup>().ListPurchaseOrdersAsync(ct)).ToDictionary(p => p.PoNumber, p => p.Supplier);
        return GenealogyGraph.Build(new GenealogySources(
            await inventory.ListLotsAsync(null, ct),
            await inventory.ListInstanceLinksAsync(null, null, ct),
            await inventory.ListGenealogyTransactionsAsync(ct),
            await inventory.ListTransformationsAsync(null, null, null, ct),
            await uow.Store<IAssetLookup>().ListComponentsAsync(null, null, true, ct),
            suppliers,
            hidden));
    }

    private void Demand(ForgePrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var decision = authorizer.Authorize(principal, Permissions.InventoryRead, ResourceRef.Of("Genealogy"));
        if (!decision.IsPermitted)
        {
            throw new ForgeAuthorizationException(decision);
        }
    }
}
