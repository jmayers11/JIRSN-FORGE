using System.Globalization;
using Forge.Execution.Contracts;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Planning.Engines;

namespace Forge.Planning;

/// <summary>A released order is at risk of missing its due date because of material: some is not covered, or arrives too late.</summary>
public sealed record MaterialRiskRecord(long OrderNumber, string Kind, string Message, DateTimeOffset DetectedAt, DateTimeOffset UpdatedAt);

public sealed record ScanMaterialRisk;

/// <summary>An order became at risk from material. <see cref="Message"/> is the planner's notice: what is short and what to do.</summary>
public sealed record MaterialRiskDetected(long OrderNumber, string Kind, string Message) : IDomainEvent
{
    public static string EventType => nameof(MaterialRiskDetected);

    public static int SchemaVersion => 1;
}

/// <summary>An order's material is covered in time again, or the order is no longer released; clears the planners' notice.</summary>
public sealed record MaterialRiskCleared(long OrderNumber, string Message) : IDomainEvent
{
    public static string EventType => nameof(MaterialRiskCleared);

    public static int SchemaVersion => 1;
}

/// <summary>The orders currently at risk from material; history lives in the events and the audit trail.</summary>
public interface IMaterialRiskStore
{
    Task<IReadOnlyList<MaterialRiskRecord>> ListOpenAsync(CancellationToken ct);

    Task UpsertAsync(MaterialRiskRecord risk, CancellationToken ct);

    Task RemoveAsync(long orderNumber, CancellationToken ct);
}

/// <summary>
/// Due-date risk from material (Sprint 10 WW-S10-30). The same material allocation the readiness view uses (stock, then scheduled
/// receipts, in schedule priority order) decides which released orders cannot be covered by their due date: a shortfall with no
/// stock or supply, or supply that arrives after the due date or is already overdue. Only a change raises an event: an order
/// becoming at risk notifies planners once (the notification rule deduplicates and checks each recipient's access to the order);
/// coverage restored clears it. Nothing is re-planned, ordered or changed.
/// </summary>
public sealed class MaterialRiskService(CommandPipeline pipeline)
{
    public const string SourceContext = "Planning";
    private const string OrderEntity = "ProductionOrder";

    public Task<CommandResult> ScanAsync(ScanMaterialRisk c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(SourceContext, nameof(ScanMaterialRisk), Permissions.MaterialPlanRead, ResourceRef.Of("MaterialPlan", "risk"));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var run = await MaterialPlanningService.RunMaterialAsync(uow, pipeline.Time, ct);
            var orders = (await uow.Store<IExecutionLookup>().ListOrdersAsync(ct)).ToDictionary(o => o.OrderNumber);
            var store = uow.Store<IMaterialRiskStore>();
            var open = (await store.ListOpenAsync(ct)).ToDictionary(r => r.OrderNumber);
            var events = new List<EventEnvelope>();
            var current = new Dictionary<long, (string Kind, string Message)>();
            foreach (var status in run.Output)
            {
                if (!orders.TryGetValue(status.OrderNumber, out var order) || order.Status != OrderStatus.Released) continue;
                if (Assess(status, order) is { } risk) current[status.OrderNumber] = risk;
            }

            foreach (var (number, risk) in current.OrderBy(r => r.Key))
            {
                var order = orders[number];
                var program = order.ProgramId is { } p ? new ProgramId(p) : (ProgramId?)null;
                if (open.TryGetValue(number, out var known))
                {
                    if (known.Kind == risk.Kind && known.Message == risk.Message) continue;
                    await store.UpsertAsync(known with { Kind = risk.Kind, Message = risk.Message, UpdatedAt = now }, ct);
                    if (known.Kind == risk.Kind) continue; // same risk, new detail: the open notice stands
                }
                else
                {
                    await store.UpsertAsync(new MaterialRiskRecord(number, risk.Kind, risk.Message, now, now), ct);
                }

                events.Add(pipeline.Event(new MaterialRiskDetected(number, risk.Kind, risk.Message), context, SourceContext, OrderEntity, Key(number), program));
            }

            foreach (var known in open.Values.Where(r => !current.ContainsKey(r.OrderNumber)).OrderBy(r => r.OrderNumber))
            {
                await store.RemoveAsync(known.OrderNumber, ct);
                var program = orders.TryGetValue(known.OrderNumber, out var order) && order.ProgramId is { } p ? new ProgramId(p) : (ProgramId?)null;
                var why = order is { Status: OrderStatus.Released } ? "its material is covered by the due date again" : $"it is no longer released ({order?.Status.ToString() ?? "removed"})";
                events.Add(pipeline.Event(new MaterialRiskCleared(known.OrderNumber, $"Order {known.OrderNumber} is no longer at risk from material: {why}."),
                    context, SourceContext, OrderEntity, Key(known.OrderNumber), program));
            }

            if (events.Count == 0)
                return CommandOutcome.NoChange(CommandResult.NoChange("material-risk", $"{current.Count} order(s) at risk from material; nothing changed since the last check."));
            var raised = events.Count(e => e.EventType == MaterialRiskDetected.EventType);
            return new CommandOutcome(
                CommandResult.Ok("material-risk", $"{current.Count} order(s) at risk from material: {raised} newly at risk, {events.Count - raised} cleared."),
                events,
                new AuditChange("MaterialRisk", "scan", CanonicalJson.Serialize(open.Values.OrderBy(r => r.OrderNumber).Select(r => new { r.OrderNumber, r.Kind })),
                    CanonicalJson.Serialize(current.OrderBy(r => r.Key).Select(r => new { OrderNumber = r.Key, r.Value.Kind }))));
        }, ct);
    }

    /// <summary>Why an order's material puts its due date at risk, or null when it is covered in time.</summary>
    public static (string Kind, string Message)? Assess(OrderMaterialStatus status, OrderInfo order)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(order);
        var head = $"Order {order.OrderNumber} ({order.PartNumber}, due {Day(order.DueDate)})";
        return status.State switch
        {
            MaterialState.Shortfall => ("Shortfall", $"{head} is short of material with no stock or supply to cover it. {status.Summary} Plan supply, use an approved alternate or replan the order."),
            MaterialState.Expected when status.Overdue => ("OverdueSupply", $"{head} depends on supply that is overdue and not received. {status.Summary} Expedite it or replan the order."),
            MaterialState.Expected when status.ReadyOn is { } ready && ready > order.DueDate =>
                ("LateSupply", $"{head} cannot have its material before {Day(ready)}, after its due date. {status.Summary} Expedite the supply or replan the order."),
            _ => null,
        };
    }

    private static string Key(long orderNumber) => orderNumber.ToString(CultureInfo.InvariantCulture);

    private static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
