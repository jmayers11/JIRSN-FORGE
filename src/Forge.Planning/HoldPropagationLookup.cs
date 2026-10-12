using System.Globalization;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Holds;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Maintenance.Contracts;
using Forge.Procurement.Contracts;

namespace Forge.Planning;

/// <summary>Hold impact and enforcement reuse the canonical genealogy and recorded material allocations.</summary>
public sealed class HoldPropagationLookup(IKernelUnitOfWork uow) : IHoldPropagationLookup
{
    private GenealogySources? sources;
    private IReadOnlyList<MaterialTask> tasks = [];
    private IReadOnlyList<ShipmentInfo> shipments = [];
    private IReadOnlyDictionary<long, OrderInfo> orders = new Dictionary<long, OrderInfo>();
    private IReadOnlyDictionary<long, DemandInfo> demands = new Dictionary<long, DemandInfo>();
    private IReadOnlyList<GoalInfo> goals = [];

    public async Task<bool> TouchesDownstreamAsync(Hold hold, HoldCheck check, CancellationToken ct)
        => (await ScopeAsync(hold, ct)).Touches(check);

    public async Task<HoldPropagationScope> ScopeAsync(Hold hold, CancellationToken ct)
    {
        await LoadAsync(ct);
        var graph = GenealogyGraph.Build(sources!);
        var reached = Reach(graph, hold);
        var affectedOrders = reached.Where(n => n.OrderNumber is not null).Select(n => n.OrderNumber!.Value).ToHashSet();
        foreach (var task in tasks.Where(t => t.Reserved > 0 && Match(reached, t.PartNumber, t.LotNumber)))
            if (task.OrderNumber is { } number) affectedOrders.Add(number);
        return new HoldPropagationScope(affectedOrders, reached.Where(n => n.Kind == GenealogyNodeKind.Instance).Select(n => new HoldLot(n.PartNumber!, n.LotNumber!)).ToArray(),
            shipments.Where(s => s.Lines.Any(l => Match(reached, l.PartNumber, l.LotNumber))).Select(s => s.ShipmentId).ToHashSet());
    }

    public async Task<IReadOnlyList<HoldImpactRecord>> ListImpactAsync(Hold hold, ForgePrincipal principal, CancellationToken ct)
    {
        if (hold.TargetType != HoldTargetType.Lot) return [];
        await LoadAsync(ct);
        bool Visible(Guid? program) => program is null || principal.Programs.Contains(new ProgramId(program.Value));
        var hidden = orders.Values.Where(o => !Visible(o.ProgramId)).Select(o => o.OrderNumber).ToHashSet();
        var graph = GenealogyGraph.Build(sources! with { HiddenOrders = hidden });
        var reached = Reach(graph, hold);
        var records = new List<HoldImpactRecord>();
        var affectedOrders = reached.Where(n => n.OrderNumber is not null).Select(n => n.OrderNumber!.Value).ToHashSet();
        bool Can(string permission, string type, string id, Guid? program = null, string? workCenter = null) =>
            principal.HasPermission(permission, new ResourceRef(type, id, program is { } p ? new ProgramId(p) : null, WorkCenterId: workCenter)) && Visible(program);
        foreach (var node in reached.Where(n => n.Kind == GenealogyNodeKind.Instance))
        {
            if (Can(Permissions.InventoryRead, "Lot", node.Id))
            {
                var evidence = graph.Edges.Where(e => e.To == node.Id && reached.Any(n => n.Id == e.From))
                    .Select(e => $"{e.From} --{e.Relation}--> {e.To} at {e.At:O}").ToArray();
                records.Add(new HoldImpactRecord("Lot", node.Id, node.LotNumber == hold.TargetId && (hold.TargetPart is null || node.PartNumber == hold.TargetPart) ? "Held instance" : "Recorded downstream instance",
                    evidence.Length == 0 ? $"lot:{node.PartNumber}/{node.LotNumber}" : string.Join("; ", evidence)));
            }
        }
        foreach (var task in tasks.Where(t => t.Reserved > 0 && Match(reached, t.PartNumber, t.LotNumber)))
        {
            var program = task.OrderNumber is { } number && orders.TryGetValue(number, out var order) ? order.ProgramId : null;
            if (!Can(Permissions.InventoryRead, "MaterialTask", Text(task.TaskId), program)) continue;
            records.Add(new HoldImpactRecord("Allocation", $"material-task:{task.TaskId}", "Reserved from the held or descendant instance",
                $"task:{task.TaskId}; lot:{task.PartNumber}/{task.LotNumber}; source:{task.FromLocation}; destination:{task.ToLocation}", program, task.Reserved, task.Status.ToString()));
            if (task.OrderNumber is { } affected) affectedOrders.Add(affected);
        }
        foreach (var number in affectedOrders.Order())
        {
            if (!orders.TryGetValue(number, out var order) || !Can(Permissions.OrdersRead, "Order", Text(number), order.ProgramId)) continue;
            var evidence = string.Join("; ", graph.Edges.Where(e => e.To == GenealogyGraph.OrderId(number) && reached.Any(n => n.Id == e.From)).Select(e => $"{e.From} --{e.Relation}--> {e.To} at {e.At:O}"));
            if (evidence.Length == 0) evidence = string.Join("; ", tasks.Where(t => t.OrderNumber == number && t.Reserved > 0 && Match(reached, t.PartNumber, t.LotNumber)).Select(t => $"material-task:{t.TaskId}"));
            records.Add(new HoldImpactRecord("Order", $"order:{number}", "Recorded issue, consumption or allocation dependency", evidence, order.ProgramId, order.Quantity, order.Status.ToString()));
            foreach (var operation in order.Operations.Where(o => Can(Permissions.OrdersRead, "Operation", $"{number}/{o.Sequence}", order.ProgramId, o.WorkCenterCode)))
                records.Add(new HoldImpactRecord("Operation", $"operation:{number}/{operation.Sequence}", "Operation of the affected order", $"order:{number}; operation:{number}/{operation.Sequence}; {evidence}", order.ProgramId, Status: operation.Status.ToString()));
        }
        var affectedDemands = new HashSet<long>();
        foreach (var shipment in shipments.Where(s => s.Lines.Any(l => Match(reached, l.PartNumber, l.LotNumber))))
        {
            var program = shipment.DemandId is { } demand && demands.TryGetValue(demand, out var d) ? d.ProgramId : null;
            if (!Can(Permissions.InventoryRead, "Shipment", Text(shipment.ShipmentId), program)) continue;
            var lines = shipment.Lines.Where(l => Match(reached, l.PartNumber, l.LotNumber)).ToArray();
            records.Add(new HoldImpactRecord("Shipment", $"shipment:{shipment.ShipmentId}", "Shipment line names the held or descendant instance",
                string.Join("; ", lines.Select(l => $"shipment:{shipment.ShipmentId}/line:{l.LineNumber}; lot:{l.PartNumber}/{l.LotNumber}")), program, lines.Sum(l => l.Quantity), shipment.Status.ToString()));
            if (shipment.DemandId is { } id) affectedDemands.Add(id);
        }
        var affectedGoals = goals.Where(g => (g.OrderNumber is { } number && affectedOrders.Contains(number)) || (g.DemandId is { } demand && affectedDemands.Contains(demand))).Select(g => g.GoalId).ToHashSet();
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var goal in goals.Where(g => affectedGoals.Contains(g.GoalId) && g.ParentGoalId is not null)) changed |= affectedGoals.Add(goal.ParentGoalId!.Value);
        }
        foreach (var goal in goals.Where(g => affectedGoals.Contains(g.GoalId) && Can(Permissions.ScheduleRead, "Goal", Text(g.GoalId), g.ProgramId)))
            records.Add(new HoldImpactRecord("Goal", $"goal:{goal.GoalId}", "Goal links to affected order, demand or child goal",
                $"goal:{goal.GoalId}; order:{goal.OrderNumber}; demand:{goal.DemandId}; parent:{goal.ParentGoalId}", goal.ProgramId, Status: goal.Status.ToString()));
        return records.OrderBy(r => r.RecordType, StringComparer.Ordinal).ThenBy(r => r.RecordId, StringComparer.Ordinal).ToArray();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        if (sources is not null) return;
        var inventory = uow.Store<IInventoryLookup>();
        var execution = uow.Store<IExecutionLookup>();
        var fullOrders = new Dictionary<long, OrderInfo>();
        foreach (var summary in await execution.ListOrdersAsync(ct)) fullOrders[summary.OrderNumber] = await execution.FindOrderAsync(summary.OrderNumber, ct) ?? summary;
        orders = fullOrders;
        tasks = await inventory.ListMaterialTasksAsync(false, null, ct);
        shipments = await inventory.ListShipmentsAsync(true, ct);
        demands = (await uow.Store<IDemandStore>().ListAsync(true, ct)).ToDictionary(d => d.DemandId);
        goals = await uow.Store<IGoalStore>().ListAsync(true, ct);
        sources = new GenealogySources(await inventory.ListLotsAsync(null, ct), await inventory.ListInstanceLinksAsync(null, null, ct),
            await inventory.ListGenealogyTransactionsAsync(ct), await inventory.ListTransformationsAsync(null, null, null, ct),
            await uow.Store<IAssetLookup>().ListComponentsAsync(null, null, true, ct),
            (await uow.Store<IProcurementLookup>().ListPurchaseOrdersAsync(ct)).ToDictionary(p => p.PoNumber, p => p.Supplier), new HashSet<long>());
    }

    private static IReadOnlyList<GenealogyNode> Reach(GenealogyGraph graph, Hold hold)
    {
        if (hold.TargetType != HoldTargetType.Lot) return [];
        var result = new Dictionary<string, GenealogyNode>(StringComparer.Ordinal);
        foreach (var root in graph.Nodes.Where(n => n.Kind == GenealogyNodeKind.Instance && n.LotNumber == hold.TargetId && (hold.TargetPart is null || n.PartNumber == hold.TargetPart)))
            foreach (var node in graph.Trace(root.Id, TraceDirection.Forward, graph.Nodes.Count + 1, graph.Nodes.Count + 1).Nodes) result[node.Id] = node;
        return result.Values.ToArray();
    }
    private static bool Match(IReadOnlyList<GenealogyNode> reached, string part, string? lot) => lot is not null && reached.Any(n => n.Kind == GenealogyNodeKind.Instance && n.PartNumber == part && n.LotNumber == lot);
    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
}
