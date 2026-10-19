using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Engines;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Maintenance.Contracts;
using Forge.Planning.Engines;

namespace Forge.Planning;

/// <summary>
/// Read side of maintenance planning (V2.9): whether maintenance work is ready, and what a proposed outage
/// does to the schedule. Both are computed from one consistent snapshot; nothing is stored here.
/// </summary>
public sealed class MaintenancePlanningService(IKernelUnitOfWorkFactory unitOfWork, IForgeAuthorizer authorizer, TimeProvider time)
{
    /// <summary>Readiness of one work order, or of every open one.</summary>
    public async Task<IReadOnlyList<MaintenanceReadiness>> ReadinessAsync(ForgePrincipal principal, long? workOrderId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Demand(principal, Permissions.MaintenanceRead, "MaintenanceReadiness");
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        return await ReadinessAsync(uow, time, workOrderId, ct);
    }

    internal static async Task<IReadOnlyList<MaintenanceReadiness>> ReadinessAsync(IKernelUnitOfWork uow, TimeProvider time, long? workOrderId, CancellationToken ct)
    {
        var maintenance = uow.Store<IMaintenanceLookup>();
        IReadOnlyList<WorkOrderInfo> orders;
        if (workOrderId is { } id)
        {
            orders = [await maintenance.FindWorkOrderAsync(id, ct) ?? throw new CommandRejectedException("work_order_not_found", $"Work order {id} does not exist.")];
        }
        else
        {
            orders = (await maintenance.ListWorkOrdersAsync(false, ct)).Where(o => o.Status is WorkOrderStatus.Open or WorkOrderStatus.Scheduled or WorkOrderStatus.InProgress).ToList();
        }

        var snapshot = await SnapshotLoader.LoadAsync(uow, time, ct);
        var now = snapshot.AsOf;
        var parts = (await maintenance.ListRequiredPartsAsync(workOrderId, ct)).ToLookup(p => p.WorkOrderId);
        var inventory = uow.Store<IInventoryLookup>();
        var available = (await Forge.Inventory.Contracts.ShelfLifeQueries.PositionsAsync(uow, null, now, ct))
            .Where(b => b.AvailableForPlanning)
            .GroupBy(b => b.PartNumber, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(b => b.Quantity), StringComparer.Ordinal);
        var issued = (await inventory.ListWorkOrderPartsAsync(workOrderId, ct)).ToLookup(p => p.WorkOrderId);
        var alternates = await uow.Store<IAlternateLookup>().ListAlternatesAsync(ct);
        var assets = (await uow.Store<IAssetLookup>().ListAssetsAsync(ct)).ToDictionary(a => a.AssetCode, StringComparer.Ordinal);
        var documents = uow.Store<IDocumentLookup>();
        var plans = (await maintenance.ListPlansAsync(ct)).ToDictionary(p => p.PlanId);

        var results = new List<MaintenanceReadiness>();
        foreach (var order in orders)
        {
            var from = order.ScheduledStart is { } s && s > now ? s : now;
            var duration = order.ScheduledStart is { } a && order.ScheduledEnd is { } b ? b - a
                : order.PlanId is { } plan && plans.TryGetValue(plan, out var p) ? TimeSpan.FromMinutes(p.DurationMinutes) : TimeSpan.FromHours(2);
            var asset = order.AssetCode is { } code ? assets.GetValueOrDefault(code)
                : order.MachineCode is { } m ? assets.Values.FirstOrDefault(x => x.MachineCode == m) : null;
            var procedure = order.ProcedureDocument is { } doc ? await documents.FindDocumentAsync(doc, ct) : null;
            var request = new MaintenanceReadinessRequest(order, asset, parts[order.WorkOrderId].ToList(), issued[order.WorkOrderId].ToList(), available, alternates, procedure,
                snapshot.People, snapshot.Unavailability, from, from + duration, snapshot.HomeTimeZone);
            results.Add(MaintenanceReadinessEngine.Decide(request));
        }

        return results;
    }

    /// <summary>The scheduler run without and with an outage on a machine (scenario 8). Nothing is committed.</summary>
    public async Task<OutagePreview> PreviewAsync(ForgePrincipal principal, string machine, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Demand(principal, Permissions.MaintenanceRead, "OutagePreview");
        if (end <= start)
        {
            throw new CommandRejectedException("invalid_window", "The window must end after it starts.");
        }

        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var snapshot = await SnapshotLoader.LoadAsync(uow, time, ct);
        if (snapshot.Machines.All(m => m.Code != machine))
        {
            throw new CommandRejectedException("unknown_machine", $"Machine {machine} does not exist or is retired.");
        }

        var lookup = uow.Store<IExecutionLookup>();
        var orders = new List<OrderInfo>();
        foreach (var summary in (await lookup.ListOrdersAsync(ct)).Where(o => o.Status == OrderStatus.Released).OrderBy(o => o.OrderNumber))
        {
            orders.Add((await lookup.FindOrderAsync(summary.OrderNumber, ct))!);
        }

        var material = (await MaterialPlanningService.RunMaterialAsync(uow, time, ct, principal)).Output;
        var quality = await ScheduleQuality.LoadAsync(uow, orders, ct);
        var horizonEnd = end + TimeSpan.FromDays(14);
        var limit = snapshot.AsOf.AddDays(62);
        var request = new ScheduleRequest(snapshot.AsOf, horizonEnd > limit ? limit : horizonEnd < snapshot.AsOf.AddDays(14) ? snapshot.AsOf.AddDays(14) : horizonEnd, orders, material, quality);
        var without = new SchedulingEngine().Evaluate(snapshot, request, snapshot.AsOf).Output;
        var outage = new DowntimeWindow(long.MinValue, machine, DowntimeKind.Planned, start, end, "Proposed outage (preview)");
        var withOutage = snapshot with { Downtime = snapshot.Downtime.Append(outage).OrderBy(d => d.MachineCode, StringComparer.Ordinal).ThenBy(d => d.StartsAt).ThenBy(d => d.Id).ToList() };
        var with = new SchedulingEngine().Evaluate(withOutage, request, snapshot.AsOf).Output;

        // Orders of programs the reader cannot see are counted, never named.
        var hidden = orders.Where(o => o.ProgramId is { } p && !principal.Programs.Contains(new ProgramId(p))).Select(o => o.OrderNumber).ToHashSet();
        if (hidden.Count == 0)
        {
            return OutagePreviewEngine.Compare(machine, start, end, without, with);
        }

        ScheduleResult Visible(ScheduleResult r) => r with { Orders = r.Orders.Where(o => !hidden.Contains(o.OrderNumber)).ToList(), Operations = r.Operations.Where(o => !hidden.Contains(o.OrderNumber)).ToList() };
        var preview = OutagePreviewEngine.Compare(machine, start, end, Visible(without), Visible(with));
        return preview with { Summary = preview.Summary + $" Orders of programs you cannot see are not shown ({hidden.Count})." };
    }

    /// <summary>The preview of a proposed outage.</summary>
    public async Task<(OutageInfo Outage, OutagePreview Preview)> PreviewOutageAsync(ForgePrincipal principal, long outageId, CancellationToken ct)
    {
        OutageInfo outage;
        await using (var uow = await unitOfWork.BeginSnapshotAsync(ct))
        {
            outage = await uow.Store<IMaintenanceLookup>().FindOutageAsync(outageId, ct)
                ?? throw new CommandRejectedException("outage_not_found", $"Outage {outageId} does not exist.");
        }

        return (outage, await PreviewAsync(principal, outage.MachineCode, outage.StartsAt, outage.EndsAt, ct));
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
