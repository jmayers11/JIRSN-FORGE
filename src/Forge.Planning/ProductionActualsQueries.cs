using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;

namespace Forge.Planning;

/// <summary>A stoppage as read: minutes so far, expected and variance for setups and changeovers, and its root causes.</summary>
public sealed record StoppageView(StoppageInfo Stoppage, string ReasonDescription, decimal Minutes, decimal? Variance, IReadOnlyList<RootCauseInfo> RootCauses);

/// <summary>Production actuals for one reader (V3.5): an order's quantity states and the stoppages of a period.</summary>
public sealed class ProductionActualsQueries(IKernelUnitOfWorkFactory unitOfWork, IForgeAuthorizer authorizer, TimeProvider time)
{
    public async Task<OrderQuantityStates> OrderAsync(ForgePrincipal principal, long orderNumber, CancellationToken ct)
    {
        Demand(principal, Permissions.OrdersRead);
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var order = await uow.Store<IExecutionLookup>().FindOrderAsync(orderNumber, ct);
        if (order is null || (order.ProgramId is { } p && !principal.Programs.Contains(new ProgramId(p))))
        {
            throw new CommandRejectedException("order_not_found", $"Order {orderNumber} does not exist.");
        }

        return (await StatesAsync(uow, [order], ct)).Single();
    }

    public async Task<IReadOnlyList<StoppageView>> StoppagesAsync(ForgePrincipal principal, bool openOnly, string? machine, int days, CancellationToken ct)
    {
        Demand(principal, Permissions.ShopFloorRead);
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var now = time.GetUtcNow();
        return await ViewsAsync(uow, openOnly, now.AddDays(-Math.Clamp(days, 1, 366)), now.AddTicks(1), now, machine, ct);
    }

    internal static async Task<IReadOnlyList<StoppageView>> ViewsAsync(IKernelUnitOfWork uow, bool openOnly, DateTimeOffset from, DateTimeOffset to, DateTimeOffset now, string? machine, CancellationToken ct)
    {
        var lookup = uow.Store<IProductionActualsLookup>();
        var stoppages = (await lookup.ListStoppagesAsync(from, to, openOnly, ct))
            .Where(s => string.IsNullOrWhiteSpace(machine) || string.Equals(s.MachineCode, machine.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        var reasons = (await lookup.ListReasonCodesAsync(ct)).ToDictionary(r => r.Code, StringComparer.Ordinal);
        var causes = (await lookup.ListRootCausesAsync(stoppages.Select(s => s.StoppageId).ToList(), ct)).ToLookup(r => r.StoppageId);
        return stoppages.Select(s => new StoppageView(s, reasons.TryGetValue(s.ReasonCode, out var r) ? r.Description : s.ReasonCode, s.Minutes(now), s.Variance(now), causes[s.StoppageId].ToList())).ToList();
    }

    /// <summary>Quantity states of whole orders (with operations), reading each other context once.</summary>
    internal static async Task<IReadOnlyList<OrderQuantityStates>> StatesAsync(IKernelUnitOfWork uow, IReadOnlyList<OrderInfo> orders, CancellationToken ct)
    {
        if (orders.Count == 0)
        {
            return [];
        }

        var rework = await uow.Store<IReworkLookup>().ListReworkAsync(orders.Count == 1 ? orders[0].OrderNumber : null, false, ct);
        var quality = uow.Store<IQualityLookup>();
        var ncrs = await quality.ListNcrsAsync(true, ct);
        var inventory = uow.Store<IInventoryLookup>();
        var locations = await inventory.ListLocationsAsync(ct);
        var result = new List<OrderQuantityStates>();
        foreach (var byPart in orders.GroupBy(o => o.PartNumber, StringComparer.Ordinal))
        {
            var partLedger = await inventory.ListTransactionsAsync(byPart.Key, null, 1_000_000, ct);
            var positions = StockPosition.From(await inventory.ListBalancesAsync(byPart.Key, ct), locations);
            foreach (var order in byPart)
            {
                var inspections = await quality.ListInspectionsAsync(order.OrderNumber, 10_000, ct);
                var orderLedger = partLedger.Where(t => t.OrderNumber == order.OrderNumber).ToList();
                result.Add(OrderQuantityStates.For(order, rework, inspections, ncrs, orderLedger, partLedger, positions));
            }
        }

        return result.OrderBy(r => r.OrderNumber).ToList();
    }

    private void Demand(ForgePrincipal principal, string permission)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var decision = authorizer.Authorize(principal, permission, ResourceRef.Of("ProductionActuals"));
        if (!decision.IsPermitted)
        {
            throw new ForgeAuthorizationException(decision);
        }
    }
}
