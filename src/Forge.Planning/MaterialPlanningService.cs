using Forge.Kernel.Access;
using Forge.Kernel.Engines;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Planning.Engines;

namespace Forge.Planning;

/// <summary>
/// Spec §11 figures for one item, each explained by records: on hand by status, allocated to released
/// orders (required but not yet issued), available (on hand less allocated), on order from suppliers and
/// in production.
/// </summary>
public sealed record StockSummary(
    string PartNumber,
    string Description,
    string UnitOfMeasure,
    string MakeBuy,
    decimal OnHand,
    decimal Inspection,
    decimal Hold,
    decimal Allocated,
    decimal Available,
    decimal OnOrder,
    decimal InProduction,
    decimal SafetyStock,
    bool BelowSafetyStock);

/// <summary>Read side of material planning: MRP and material readiness, computed from one consistent snapshot.</summary>
public sealed class MaterialPlanningService(IKernelUnitOfWorkFactory unitOfWork, IForgeAuthorizer authorizer, TimeProvider time)
{
    private readonly MrpEngine mrp = new();

    /// <summary>MRP for every item (or one part). Nothing is stored; run it as often as you like.</summary>
    public async Task<EngineRun<MrpResult>> EvaluateMrpAsync(ForgePrincipal principal, string? partNumber, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Demand(principal, Permissions.MaterialPlanRead, "MaterialPlan");
        var part = string.IsNullOrWhiteSpace(partNumber) ? null : BusinessCode.Require(partNumber, "Part");
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var snapshot = await MaterialData.MrpSnapshotAsync(uow, time, principal, ct);
        return EngineRunner.Run(mrp, snapshot, new MrpRequest(part), time.GetUtcNow());
    }

    /// <summary>Material status of every released order the reader can see.</summary>
    public async Task<EngineRun<IReadOnlyList<OrderMaterialStatus>>> EvaluateMaterialAsync(ForgePrincipal principal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Demand(principal, Permissions.ReadinessRead, "Readiness");
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var run = await RunMaterialAsync(uow, time, ct);
        var hidden = (await uow.Store<Forge.Execution.Contracts.IExecutionLookup>().ListOrdersAsync(ct))
            .Where(o => o.ProgramId is { } p && !principal.Programs.Contains(new ProgramId(p)))
            .Select(o => o.OrderNumber)
            .ToHashSet();
        return run with { Output = run.Output.Where(o => !hidden.Contains(o.OrderNumber)).ToList() };
    }

    /// <summary>Stock position of every item that has stock, supply, allocations or safety stock.</summary>
    public async Task<IReadOnlyList<StockSummary>> StockSummaryAsync(ForgePrincipal principal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Demand(principal, Permissions.InventoryRead, "Inventory");
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var items = await uow.Store<Forge.Engineering.Contracts.IEngineeringLookup>().ListItemsAsync(ct);
        var balances = await uow.Store<Forge.Inventory.Contracts.IInventoryLookup>().ListBalancesAsync(null, ct);
        var material = await MaterialData.MaterialSnapshotAsync(uow, time, ct);
        var supplies = await MaterialData.SuppliesAsync(uow, principal, ct);
        var allocated = material.Orders
            .SelectMany(o => o.Materials.GroupBy(m => m.ComponentPartNumber, StringComparer.Ordinal).Select(g => (Part: g.Key, Left: Math.Max(0, g.Sum(m => m.RequiredQuantity) - material.Issued.Where(i => i.OrderNumber == o.OrderNumber && i.PartNumber == g.Key).Sum(i => i.Quantity)))))
            .GroupBy(x => x.Part, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Left), StringComparer.Ordinal);

        var result = new List<StockSummary>();
        foreach (var item in items)
        {
            var mine = balances.Where(b => b.PartNumber == item.PartNumber).ToList();
            decimal InStatus(Forge.Inventory.Contracts.StockStatus status) => mine.Where(b => b.Status == status).Sum(b => b.Quantity);
            var onHand = InStatus(Forge.Inventory.Contracts.StockStatus.Available);
            var alloc = allocated.GetValueOrDefault(item.PartNumber);
            var onOrder = supplies.Where(s => s.PartNumber == item.PartNumber && s.Kind == "PurchaseOrder").Sum(s => s.Quantity);
            var inProduction = supplies.Where(s => s.PartNumber == item.PartNumber && s.Kind == "ProductionOrder").Sum(s => s.Quantity);
            var summary = new StockSummary(
                item.PartNumber, item.Description, item.UnitOfMeasure, item.MakeBuy.ToString(),
                onHand, InStatus(Forge.Inventory.Contracts.StockStatus.Inspection), InStatus(Forge.Inventory.Contracts.StockStatus.Hold),
                alloc, onHand - alloc, onOrder, inProduction, item.SafetyStock, onHand - alloc < item.SafetyStock);
            if (mine.Count > 0 || alloc > 0 || onOrder > 0 || inProduction > 0 || item.SafetyStock > 0)
            {
                result.Add(summary);
            }
        }

        return result;
    }

    /// <summary>Allocation across all released orders; callers pick out the orders they may show.</summary>
    internal static async Task<EngineRun<IReadOnlyList<OrderMaterialStatus>>> RunMaterialAsync(IKernelUnitOfWork uow, TimeProvider time, CancellationToken ct)
    {
        var snapshot = await MaterialData.MaterialSnapshotAsync(uow, time, ct);
        return EngineRunner.Run(new MaterialEngine(), snapshot, new MaterialRequest(), time.GetUtcNow());
    }

    private void Demand(ForgePrincipal principal, string permission, string resource)
    {
        var decision = authorizer.Authorize(principal, permission, ResourceRef.Of(resource));
        if (!decision.IsPermitted)
        {
            throw new ForgeAuthorizationException(decision);
        }
    }
}
