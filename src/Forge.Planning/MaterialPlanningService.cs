using Forge.Kernel.Access;
using Forge.Kernel.Engines;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Planning.Engines;

namespace Forge.Planning;

/// <summary>
/// Spec §11 figures for one item, each explained by records, with the V3 brief's distinctions kept apart (V3.1):
/// on hand (every quality status, every location) is not released, released is not available for planning
/// (stock at an outside processor, in transit or in quarantine is not), and none of them is at the point of use.
/// Allocated is what released orders still need (required, not yet issued); available for planning is
/// released stock in nettable locations less that.
/// </summary>
public sealed record StockSummary(
    string PartNumber,
    string Description,
    string UnitOfMeasure,
    string MakeBuy,
    decimal OnHand,
    decimal Released,
    decimal Inspection,
    decimal Hold,
    decimal NotNettable,
    decimal AtPointOfUse,
    decimal Allocated,
    decimal AvailableForPlanning,
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
        var run = EngineRunner.Run(mrp, snapshot, new MrpRequest(part), time.GetUtcNow());
        return (await MaterialSourceVisibility.LoadAsync(uow, principal, ct, authorizer: authorizer)).Mrp(run);
    }

    /// <summary>Material status of every released order the reader can see.</summary>
    public async Task<EngineRun<IReadOnlyList<OrderMaterialStatus>>> EvaluateMaterialAsync(ForgePrincipal principal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Demand(principal, Permissions.ReadinessRead, "Readiness");
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var run = await RunMaterialAsync(uow, time, ct);
        return (await MaterialSourceVisibility.LoadAsync(uow, principal, ct, authorizer: authorizer)).Material(run);
    }

    /// <summary>Stock position of every item that has stock, supply, allocations or safety stock.</summary>
    public async Task<IReadOnlyList<StockSummary>> StockSummaryAsync(ForgePrincipal principal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Demand(principal, Permissions.InventoryRead, "Inventory");
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var items = await uow.Store<Forge.Engineering.Contracts.IEngineeringLookup>().ListItemsAsync(ct);
        var inventory = uow.Store<Forge.Inventory.Contracts.IInventoryLookup>();
        var positions = await Forge.Inventory.Contracts.ShelfLifeQueries.PositionsAsync(uow, null, time.GetUtcNow(), ct);
        var material = await MaterialData.MaterialSnapshotAsync(uow, time, ct);
        var supplies = await MaterialData.SuppliesAsync(uow, principal, ct);
        var allocated = material.Orders
            .SelectMany(o => o.Materials.GroupBy(m => m.ComponentPartNumber, StringComparer.Ordinal).Select(g => (Part: g.Key, Left: Math.Max(0, g.Sum(m => m.RequiredQuantity) - material.Issued.Where(i => i.OrderNumber == o.OrderNumber && i.PartNumber == g.Key).Sum(i => i.Quantity)))))
            .GroupBy(x => x.Part, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Left), StringComparer.Ordinal);

        var result = new List<StockSummary>();
        foreach (var item in items)
        {
            var mine = positions.Where(b => b.PartNumber == item.PartNumber).ToList();
            decimal Sum(Func<Forge.Inventory.Contracts.StockPosition, bool> which) => mine.Where(which).Sum(b => b.Quantity);
            var released = Sum(b => b.QualityStatus == Forge.Inventory.Contracts.StockStatus.Released);
            var forPlanning = Sum(b => b.AvailableForPlanning);
            var alloc = allocated.GetValueOrDefault(item.PartNumber);
            var onOrder = supplies.Where(s => s.PartNumber == item.PartNumber && s.Kind == "PurchaseOrder").Sum(s => s.Quantity);
            var inProduction = supplies.Where(s => s.PartNumber == item.PartNumber && s.Kind == "ProductionOrder").Sum(s => s.Quantity);
            var summary = new StockSummary(
                item.PartNumber, item.Description, item.UnitOfMeasure, item.MakeBuy.ToString(),
                Sum(_ => true), released,
                Sum(b => b.QualityStatus == Forge.Inventory.Contracts.StockStatus.Inspection), Sum(b => b.QualityStatus == Forge.Inventory.Contracts.StockStatus.Hold),
                released - forPlanning, Sum(b => b.QualityStatus == Forge.Inventory.Contracts.StockStatus.Released && b.PointOfUseWorkCenter is not null),
                alloc, forPlanning - alloc, onOrder, inProduction, item.SafetyStock, forPlanning - alloc < item.SafetyStock);
            if (mine.Count > 0 || alloc > 0 || onOrder > 0 || inProduction > 0 || item.SafetyStock > 0)
            {
                result.Add(summary);
            }
        }

        return result;
    }

    /// <summary>Allocation across all released orders; callers pick out the orders they may show.</summary>
    internal static async Task<EngineRun<IReadOnlyList<OrderMaterialStatus>>> RunMaterialAsync(IKernelUnitOfWork uow, TimeProvider time, CancellationToken ct, ForgePrincipal? viewer = null)
    {
        var snapshot = await MaterialData.MaterialSnapshotAsync(uow, time, ct);
        var run = EngineRunner.Run(new MaterialEngine(), snapshot, new MaterialRequest(), time.GetUtcNow());
        if (viewer is null) return run;
        var access = await MaterialSourceVisibility.LoadAsync(uow, viewer, ct, authorizer: new ForgeAuthorizer(time));
        return run with { Output = access.ForEvaluation(run.Output) };
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
