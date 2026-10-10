using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Resources.Contracts;
using NodaTime;

namespace Forge.Planning;

/// <summary>One order's material on its way to the machine, with its tasks (V3.4).</summary>
public sealed record OrderMaterialFlow(long OrderNumber, string PartNumber, string Status, IReadOnlyList<MaterialFlowLine> Lines, IReadOnlyList<MaterialTask> Tasks);

/// <summary>Material flow per order and cycle counts due (V3.4), for one reader.</summary>
public sealed class MaterialFlowService(IKernelUnitOfWorkFactory unitOfWork, IForgeAuthorizer authorizer, TimeProvider time)
{
    public async Task<OrderMaterialFlow> OrderAsync(ForgePrincipal principal, long orderNumber, CancellationToken ct)
    {
        Demand(principal, Permissions.InventoryRead);
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var order = await uow.Store<IExecutionLookup>().FindOrderAsync(orderNumber, ct);
        if (order is null || (order.ProgramId is { } p && !principal.Programs.Contains(new ProgramId(p))))
        {
            throw new CommandRejectedException("order_not_found", $"Order {orderNumber} does not exist.");
        }

        var inventory = uow.Store<IInventoryLookup>();
        var tasks = await inventory.ListMaterialTasksAsync(false, orderNumber, ct);
        var positions = StockPosition.From(await inventory.ListBalancesAsync(null, ct), await inventory.ListLocationsAsync(ct));
        var lines = MaterialFlowLine.For(order, tasks, await inventory.ListTransactionsAsync(null, orderNumber, 100_000, ct), await inventory.ListTransformationsAsync(orderNumber, null, null, ct), positions);
        return new OrderMaterialFlow(order.OrderNumber, order.PartNumber, order.Status.ToString(), lines, tasks);
    }

    /// <summary>Every released order's material flow the reader may see (for reports).</summary>
    internal static async Task<IReadOnlyList<MaterialFlowLine>> ReleasedAsync(IKernelUnitOfWork uow, Func<Guid?, bool> visible, CancellationToken ct)
    {
        var inventory = uow.Store<IInventoryLookup>();
        var execution = uow.Store<IExecutionLookup>();
        var orders = new List<OrderInfo>();
        foreach (var summary in (await execution.ListOrdersAsync(ct)).Where(o => o.Status == OrderStatus.Released && visible(o.ProgramId)))
        {
            // The list carries no operations or materials; each order is read whole.
            if (await execution.FindOrderAsync(summary.OrderNumber, ct) is { } order)
            {
                orders.Add(order);
            }
        }

        if (orders.Count == 0)
        {
            return [];
        }

        var tasks = await inventory.ListMaterialTasksAsync(false, null, ct);
        var ledger = (await inventory.ListGenealogyTransactionsAsync(ct)).Where(t => t.OrderNumber is not null).ToLookup(t => t.OrderNumber!.Value);
        var transformations = (await inventory.ListTransformationsAsync(null, null, null, ct)).ToLookup(t => t.OrderNumber);
        var positions = StockPosition.From(await inventory.ListBalancesAsync(null, ct), await inventory.ListLocationsAsync(ct));
        return orders.OrderBy(o => o.OrderNumber)
            .SelectMany(o => MaterialFlowLine.For(o, tasks, ledger[o.OrderNumber], transformations[o.OrderNumber], positions))
            .ToList();
    }

    public async Task<IReadOnlyList<CountDue>> CountsDueAsync(ForgePrincipal principal, CancellationToken ct)
    {
        Demand(principal, Permissions.InventoryRead);
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        return await CountsDueAsync(uow, time, ct);
    }

    internal static async Task<IReadOnlyList<CountDue>> CountsDueAsync(IKernelUnitOfWork uow, TimeProvider time, CancellationToken ct)
    {
        var inventory = uow.Store<IInventoryLookup>();
        var sites = await uow.Store<IResourcesLookup>().ListSitesAsync(ct);
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull((sites.Count > 0 ? sites[0].TimeZone : null) ?? "America/New_York") ?? DateTimeZoneProviders.Tzdb["America/New_York"];
        DateOnly Local(DateTimeOffset at)
        {
            var d = Instant.FromDateTimeOffset(at).InZone(zone).Date;
            return new DateOnly(d.Year, d.Month, d.Day);
        }

        return CountDue.For(
            await inventory.ListCountProgramsAsync(ct),
            await inventory.ListBalancesAsync(null, ct),
            await inventory.ListLocationsAsync(ct),
            await inventory.ListLastCountsAsync(ct),
            Local(time.GetUtcNow()),
            Local);
    }

    private void Demand(ForgePrincipal principal, string permission)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var decision = authorizer.Authorize(principal, permission, ResourceRef.Of("MaterialFlow"));
        if (!decision.IsPermitted)
        {
            throw new ForgeAuthorizationException(decision);
        }
    }
}
