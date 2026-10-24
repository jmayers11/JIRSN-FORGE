using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Engines;
using Forge.Kernel.Security;
using Forge.Planning.Engines;

namespace Forge.Planning;

/// <param name="BeforeMaterialState">The order's material state now (Ready, Expected, Shortfall).</param>
/// <param name="ProposedMaterialState">The order's material state if the quantity were scrapped.</param>
public sealed record ScrapOrderImpact(
    long OrderNumber,
    string PartNumber,
    DateOnly DueDate,
    string BeforeMaterialState,
    string ProposedMaterialState,
    string ProposedMaterialSummary,
    string BeforeScheduleStatus,
    string ProposedScheduleStatus,
    DateTimeOffset? BeforeCompletion,
    DateTimeOffset? ProposedCompletion,
    decimal? CompletionShiftHours);

/// <param name="Nettable">Whether the stock counts for planning at all: released stock in a nettable location.</param>
/// <param name="AvailableBefore">The part's nettable released stock now.</param>
public sealed record MaterialScrapImpactPreview(
    string PartNumber,
    decimal Quantity,
    string LocationCode,
    bool Nettable,
    decimal AvailableBefore,
    DateTimeOffset AsOf,
    IReadOnlyList<ScrapOrderImpact> AffectedOrders,
    string Assumption);

/// <summary>
/// A read-only scrap what-if (Sprint 10 §20.1). It removes the quantity from nettable stock once, then runs the canonical material
/// engine and finite-capacity scheduler on both snapshots and reports the released orders whose material or schedule would change.
/// </summary>
public static class MaterialScrapImpactCalculator
{
    public const string Assumption = "Assumes the quantity leaves nettable released stock now. Material netting and the finite-capacity schedule are recalculated for released orders; nothing is posted, reserved or rescheduled. Quality-held, inspection or non-nettable stock does not count for planning, so scrapping it changes no order.";

    public static IReadOnlyList<ScrapOrderImpact> Evaluate(
        SiteSnapshot snapshot,
        MaterialSnapshot material,
        IReadOnlyList<OrderInfo> orders,
        IReadOnlyDictionary<long, Forge.Quality.Contracts.OrderQuality> quality,
        IReadOnlyDictionary<string, int> leadTimes,
        string partNumber,
        decimal quantity)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(orders);
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity), "The scrap quantity must be positive.");

        var proposedInput = material with
        {
            Stock = [.. material.Stock.Select(s => s.PartNumber == partNumber ? s with { Available = Math.Max(0m, s.Available - quantity) } : s)],
        };
        var beforeMaterial = EngineRunner.Run(new MaterialEngine(), material, new MaterialRequest(), snapshot.AsOf).Output;
        var afterMaterial = EngineRunner.Run(new MaterialEngine(), proposedInput, new MaterialRequest(), snapshot.AsOf).Output;
        var horizon = snapshot.AsOf.AddDays(62);
        var beforeSchedule = EngineRunner.Run(new SchedulingEngine(), snapshot,
            new ScheduleRequest(snapshot.AsOf, horizon, orders, beforeMaterial, quality, leadTimes), snapshot.AsOf).Output;
        var afterSchedule = EngineRunner.Run(new SchedulingEngine(), snapshot,
            new ScheduleRequest(snapshot.AsOf, horizon, orders, afterMaterial, quality, leadTimes), snapshot.AsOf).Output;

        var impacts = new List<ScrapOrderImpact>();
        foreach (var order in afterSchedule.Orders)
        {
            var a = beforeSchedule.Orders.SingleOrDefault(row => row.OrderNumber == order.OrderNumber);
            var mBefore = beforeMaterial.SingleOrDefault(row => row.OrderNumber == order.OrderNumber);
            var mAfter = afterMaterial.SingleOrDefault(row => row.OrderNumber == order.OrderNumber);
            if (a is null) continue;
            var materialChanged = mBefore?.State != mAfter?.State || mBefore?.ReadyOn != mAfter?.ReadyOn;
            var scheduleChanged = a.Status != order.Status || a.ProjectedCompletion != order.ProjectedCompletion;
            if (!materialChanged && !scheduleChanged) continue;
            decimal? shift = a.ProjectedCompletion is { } beforeAt && order.ProjectedCompletion is { } afterAt
                ? decimal.Round((decimal)(afterAt - beforeAt).TotalHours, 2, MidpointRounding.AwayFromZero)
                : null;
            impacts.Add(new ScrapOrderImpact(order.OrderNumber, order.PartNumber, order.DueDate,
                mBefore?.State.ToString() ?? "Unknown", mAfter?.State.ToString() ?? "Unknown", mAfter?.Summary ?? "Material status is unavailable.",
                a.Status.ToString(), order.Status.ToString(), a.ProjectedCompletion, order.ProjectedCompletion, shift));
        }

        return [.. impacts.OrderByDescending(i => i.CompletionShiftHours ?? 0).ThenBy(i => i.DueDate).ThenBy(i => i.OrderNumber)];
    }
}

/// <summary>Permission-scoped loader for the scrap what-if; only orders the reader may see are reported.</summary>
public sealed class MaterialScrapImpactPreviewService(
    IKernelUnitOfWorkFactory unitOfWork,
    IForgeAuthorizer authorizer,
    TimeProvider time)
{
    public async Task<MaterialScrapImpactPreview> PreviewAsync(ForgePrincipal principal, string partNumber, string locationCode, StockStatus status, decimal quantity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Demand(principal, Permissions.InventoryRead, "Inventory");
        Demand(principal, Permissions.ScheduleRead, "Schedule");
        if (quantity <= 0)
            throw new CommandRejectedException("invalid_quantity", "Quantity must be above zero.");

        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var location = (await uow.Store<IInventoryLookup>().ListLocationsAsync(ct)).SingleOrDefault(l => l.Code == locationCode)
            ?? throw new CommandRejectedException("unknown_location", $"Location {locationCode} does not exist.");
        var execution = uow.Store<IExecutionLookup>();
        var headers = await execution.ListOrdersAsync(ct);
        var access = await MaterialSourceVisibility.LoadAsync(uow, principal, ct, headers, authorizer);
        var orders = new List<OrderInfo>();
        foreach (var header in headers.Where(order => order.Status == OrderStatus.Released).OrderBy(order => order.OrderNumber))
            if (await execution.FindOrderAsync(header.OrderNumber, ct) is { } order) orders.Add(order);

        var snapshot = await SnapshotLoader.LoadAsync(uow, time, ct);
        var materialInput = await MaterialData.MaterialSnapshotAsync(uow, time, ct, headers, orders);
        var available = materialInput.Stock.Where(s => s.PartNumber == partNumber).Sum(s => s.Available);
        var nettable = status == StockStatus.Released && location.Nettable && location.InCompanyCustody;
        if (!nettable)
            return new MaterialScrapImpactPreview(partNumber, quantity, location.Code, false, available, snapshot.AsOf, [], MaterialScrapImpactCalculator.Assumption);

        var impacts = MaterialScrapImpactCalculator.Evaluate(snapshot, materialInput, orders, await ScheduleQuality.LoadAsync(uow, orders, ct),
            await MaterialData.LeadTimesAsync(uow, ct), partNumber, quantity);
        return new MaterialScrapImpactPreview(partNumber, quantity, location.Code, true, available, snapshot.AsOf,
            [.. impacts.Where(i => access.CanSeeOrder(i.OrderNumber)).Select(i => i with { ProposedMaterialSummary = access.Text(i.ProposedMaterialSummary) }).Take(25)],
            MaterialScrapImpactCalculator.Assumption);
    }

    private void Demand(ForgePrincipal principal, string permission, string resource)
    {
        var decision = authorizer.Authorize(principal, permission, ResourceRef.Of(resource));
        if (!decision.IsPermitted) throw new ForgeAuthorizationException(decision);
    }
}
