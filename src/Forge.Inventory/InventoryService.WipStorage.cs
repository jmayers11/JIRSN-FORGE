using System.Globalization;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;

namespace Forge.Inventory;

/// <summary>Propose the approved safe holding capacity of a location for one part (Sprint 10 §18). A safety approver decides it.</summary>
/// <param name="MaxUtilizationPercent">Optional operating limit below the approved maximum, for example 85 to leave room to move.</param>
/// <param name="SafetyReference">The safety review, rack rating or SOP that approved the quantity.</param>
public sealed record ProposeStorageRule(string LocationCode, string PartNumber, decimal MaxUnits, decimal? MaxUtilizationPercent, int? MaxDwellHours, string SafetyReference);

public sealed record DecideStorageRule(long RuleId, bool Approve);

public sealed record RetireStorageRule(long RuleId);

public sealed record PartialBuildMaterialLine(string PartNumber, decimal Quantity);

/// <summary>
/// Approve a partial build (Sprint 10 §17.6): reserves the listed material for the order and the WIP storage space in one transaction,
/// re-checking both under lock. Planning's preview is advice; this command is the gate.
/// </summary>
/// <param name="Evidence">The preview the approver saw (quantities, limits, dates), kept with the approval.</param>
public sealed record ApprovePartialBuild(
    long OrderNumber,
    int Sequence,
    decimal Quantity,
    string? StorageLocation,
    decimal StorageQuantity,
    IReadOnlyList<PartialBuildMaterialLine> Materials,
    string Evidence);

/// <summary>The work reached its stopping point and the WIP moved on: the reservations end.</summary>
public sealed record CompletePartialBuild(long BuildId);

public sealed record CancelPartialBuild(long BuildId);

public sealed record StorageRuleChanged(long RuleId, string State, string LocationCode, string PartNumber) : IDomainEvent
{
    public static string EventType => nameof(StorageRuleChanged);

    public static int SchemaVersion => 1;
}

public sealed record PartialBuildApproved(long BuildId, long OrderNumber, int Sequence, decimal Quantity, string? StorageLocation, decimal StorageQuantity) : IDomainEvent
{
    public static string EventType => nameof(PartialBuildApproved);

    public static int SchemaVersion => 1;
}

public sealed record PartialBuildClosed(long BuildId, long OrderNumber, string State) : IDomainEvent
{
    public static string EventType => nameof(PartialBuildClosed);

    public static int SchemaVersion => 1;
}

public interface IWipStorageStore
{
    Task<long> InsertStorageRuleAsync(StorageCapacityRule rule, CancellationToken ct);

    Task<bool> DecideStorageRuleAsync(long ruleId, StorageRuleState expected, StorageRuleState state, string actor, DateTimeOffset at, CancellationToken ct);

    Task<long> InsertPartialBuildAsync(PartialBuild build, CancellationToken ct);

    /// <summary>Closes an active build; false when it is no longer active.</summary>
    Task<bool> ClosePartialBuildAsync(long buildId, PartialBuildState state, string actor, DateTimeOffset at, CancellationToken ct);
}

public sealed partial class InventoryService
{
    private const string StorageRuleEntity = "StorageCapacityRule";
    private const string PartialBuildEntity = "PartialBuild";

    public Task<CommandResult> ProposeStorageRuleAsync(ProposeStorageRule c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var spec = new CommandSpec(SourceContext, nameof(ProposeStorageRule), Permissions.InventoryManage, ResourceRef.Of(StockEntity, part), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            await ItemAsync(uow, part, ct);
            var location = await CompanyLocationAsync(uow, c.LocationCode, ct);
            if (c.MaxUnits is <= 0 or > 100_000_000)
                throw new CommandRejectedException("invalid_capacity", "Give the approved maximum quantity, above zero.");
            if (c.MaxUtilizationPercent is <= 0 or > 100)
                throw new CommandRejectedException("invalid_utilization", "The utilization limit is a percentage above 0 and at most 100.");
            if (c.MaxDwellHours is <= 0)
                throw new CommandRejectedException("invalid_dwell", "The maximum dwell is a positive number of hours.");
            var reference = Bounded(c.SafetyReference, 200, "safety reference")
                ?? throw new CommandRejectedException("safety_reference_required", "Name the safety review, rack rating or SOP that approved this capacity.");
            if ((await uow.Store<IWipStorageLookup>().ListStorageRulesAsync(ct)).Any(r => r.LocationCode == location.Code && r.PartNumber == part
                    && r.State is StorageRuleState.Proposed or StorageRuleState.Active))
                throw new CommandRejectedException("storage_rule_exists", $"{location.Code} already has a proposed or active rule for {part}; retire it first.");
            var rule = new StorageCapacityRule(0, location.Code, part, c.MaxUnits, c.MaxUtilizationPercent, c.MaxDwellHours, reference, StorageRuleState.Proposed,
                context.Actor, now, null, null);
            var ruleId = await uow.Store<IWipStorageStore>().InsertStorageRuleAsync(rule, ct);
            var id = ruleId.ToString(CultureInfo.InvariantCulture);
            return new CommandOutcome(
                CommandResult.Ok(id, $"Storage rule {id}: up to {Qty(c.MaxUnits)} {part} at {location.Code}. A safety approver must approve it before planning uses it."),
                [pipeline.Event(new StorageRuleChanged(ruleId, nameof(StorageRuleState.Proposed), location.Code, part), context, SourceContext, StorageRuleEntity, id)],
                new AuditChange(StorageRuleEntity, id, null, CanonicalJson.Serialize(rule with { RuleId = ruleId })));
        }, ct);
    }

    public Task<CommandResult> DecideStorageRuleAsync(DecideStorageRule c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeStorageRuleAsync(nameof(DecideStorageRule), c.RuleId, c, context, Permissions.SafetyRulesApprove, StorageRuleState.Proposed,
            c.Approve ? StorageRuleState.Active : StorageRuleState.Rejected, true, ct);
    }

    public Task<CommandResult> RetireStorageRuleAsync(RetireStorageRule c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeStorageRuleAsync(nameof(RetireStorageRule), c.RuleId, c, context, Permissions.InventoryManage, StorageRuleState.Active,
            StorageRuleState.Retired, false, ct);
    }

    public Task<CommandResult> ApprovePartialBuildAsync(ApprovePartialBuild c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var orderId = c.OrderNumber.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, nameof(ApprovePartialBuild), Permissions.ScheduleApprove, ResourceRef.Of("ProductionOrder", orderId), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var order = await ReleasedOrderAsync(uow, c.OrderNumber, context, ct);
            var operation = order.Operations.FirstOrDefault(o => o.Sequence == c.Sequence)
                ?? throw new CommandRejectedException("operation_not_found", $"Order {orderId} has no operation {c.Sequence}.");
            var open = order.Quantity - operation.GoodQuantity;
            if (operation.Status == OperationStatus.Complete || open <= 0)
                throw new CommandRejectedException("operation_complete", $"Operation {c.Sequence} of order {orderId} is complete.");
            if (c.Quantity <= 0 || c.Quantity > open)
                throw new CommandRejectedException("invalid_quantity", $"Build between 1 and {Qty(open)} through operation {c.Sequence}.");
            if (c.StorageQuantity < 0 || c.StorageQuantity > c.Quantity)
                throw new CommandRejectedException("invalid_storage_quantity", "The WIP to store cannot be negative or more than the quantity built.");
            var evidence = Bounded(c.Evidence, 8000, "evidence")
                ?? throw new CommandRejectedException("evidence_required", "Keep the preview the approval was based on.");
            var lookup = uow.Store<IWipStorageLookup>();
            if (await lookup.ListPartialBuildsAsync(true, c.OrderNumber, ct) is [var active, ..])
                throw new CommandRejectedException("partial_build_active", $"Order {orderId} already has approved partial build {active.BuildId}; complete or cancel it first.");

            var lines = (c.Materials ?? []).Select(m => (Part: BusinessCode.Require(m.PartNumber, "Part"), m.Quantity)).ToList();
            if (lines.GroupBy(l => l.Part, StringComparer.Ordinal).Any(g => g.Count() > 1) || lines.Any(l => l.Quantity <= 0))
                throw new CommandRejectedException("invalid_material", "List each material once, with a quantity above zero.");

            // Lock in a fixed order so two approvals never wait on each other.
            var store = uow.Store<IInventoryStore>();
            foreach (var part in lines.Select(l => l.Part).Append(order.PartNumber).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                await store.LockPartAsync(part, ct);

            var issued = (await uow.Store<IInventoryLookup>().ListOrderIssuesAsync(ct)).Where(i => i.OrderNumber == order.OrderNumber)
                .ToDictionary(i => i.PartNumber, i => i.NetIssued, StringComparer.Ordinal);
            var reservations = await WipStorageQueries.OutstandingReservationsAsync(uow, ct);
            var positions = await ShelfLifeQueries.PositionsAsync(uow, null, now, ct);
            var materials = new List<PartialBuildMaterial>();
            foreach (var (part, quantity) in lines)
            {
                var requirement = order.Materials.Where(m => m.ComponentPartNumber == part).ToList();
                if (requirement.Count == 0)
                    throw new CommandRejectedException("invalid_material", $"{part} is not material of order {orderId}.");
                if (requirement.Any(m => m.ConsumedAtSequence is { } at && at > c.Sequence))
                    throw new CommandRejectedException("invalid_material", $"{part} is consumed after operation {c.Sequence}; it is not needed for this partial build.");
                var stillToIssue = requirement.Sum(m => m.RequiredQuantity) - issued.GetValueOrDefault(part);
                if (quantity > stillToIssue)
                    throw new CommandRejectedException("invalid_material", $"Order {orderId} still needs only {Qty(Math.Max(0m, stillToIssue))} {part}.");
                var free = positions.Where(p => p.PartNumber == part && p.AvailableForPlanning).Sum(p => p.Quantity)
                    - reservations.Where(r => r.PartNumber == part).Sum(r => r.Outstanding);
                if (quantity > free)
                {
                    throw new CommandRejectedException("material_not_available",
                        $"Only {Qty(Math.Max(0m, free))} {part} is released, usable and not reserved by another partial build; {Qty(quantity)} was asked. Nothing was reserved.");
                }

                materials.Add(new PartialBuildMaterial(part, quantity, issued.GetValueOrDefault(part)));
            }

            string? storage = null;
            string storageText = string.Empty;
            if (c.StorageQuantity > 0)
            {
                var location = await CompanyLocationAsync(uow, c.StorageLocation ?? string.Empty, ct);
                var rule = (await lookup.ListStorageRulesAsync(ct)).FirstOrDefault(r => r.State == StorageRuleState.Active && r.LocationCode == location.Code && r.PartNumber == order.PartNumber)
                    ?? throw new CommandRejectedException("storage_not_approved", $"{location.Code} has no approved storage rule for {order.PartNumber}; WIP may only wait in approved space.");
                var availability = WipStorageQueries.Availability(rule, await WipStorageQueries.OccupancyAsync(uow, location.Code, order.PartNumber, now, null, ct));
                if (availability.Free is not { } room)
                    throw new CommandRejectedException("storage_unknown", availability.Status);
                if (c.StorageQuantity > room)
                    throw new CommandRejectedException("storage_full", $"Only {Qty(room)} fits: {availability.Status} Nothing was reserved.");
                storage = location.Code;
                storageText = $" and {Qty(c.StorageQuantity)} WIP space at {location.Code}";
            }
            else if (!string.IsNullOrWhiteSpace(c.StorageLocation))
            {
                throw new CommandRejectedException("invalid_storage_quantity", "Give the quantity of WIP to store at the location.");
            }

            var build = new PartialBuild(0, order.OrderNumber, c.Sequence, c.Quantity, storage, c.StorageQuantity, order.PartNumber, evidence, PartialBuildState.Active,
                context.Actor, now, null, null, materials);
            var buildId = await uow.Store<IWipStorageStore>().InsertPartialBuildAsync(build, ct);
            var id = buildId.ToString(CultureInfo.InvariantCulture);
            return new CommandOutcome(
                CommandResult.Ok(id, $"Partial build {id} approved: {Qty(c.Quantity)} of order {orderId} through operation {c.Sequence}. Reserved " +
                    (materials.Count == 0 ? "no material" : string.Join(", ", materials.Select(m => $"{Qty(m.Quantity)} {m.PartNumber}"))) + storageText + "."),
                [pipeline.Event(new PartialBuildApproved(buildId, order.OrderNumber, c.Sequence, c.Quantity, storage, c.StorageQuantity), context, SourceContext, PartialBuildEntity, id)],
                new AuditChange(PartialBuildEntity, id, null, CanonicalJson.Serialize(build with { BuildId = buildId })));
        }, ct);
    }

    public Task<CommandResult> CompletePartialBuildAsync(CompletePartialBuild c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ClosePartialBuildAsync(nameof(CompletePartialBuild), c.BuildId, c, PartialBuildState.Completed, context, ct);
    }

    public Task<CommandResult> CancelPartialBuildAsync(CancelPartialBuild c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ClosePartialBuildAsync(nameof(CancelPartialBuild), c.BuildId, c, PartialBuildState.Cancelled, context, ct);
    }

    private Task<CommandResult> ClosePartialBuildAsync<TCommand>(string commandType, long buildId, TCommand command, PartialBuildState state, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var id = buildId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, commandType, Permissions.ScheduleApprove, ResourceRef.Of(PartialBuildEntity, id), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var build = (await uow.Store<IWipStorageLookup>().ListPartialBuildsAsync(false, null, ct)).FirstOrDefault(b => b.BuildId == buildId)
                ?? throw new CommandRejectedException("partial_build_not_found", $"Partial build {id} does not exist.");
            var order = await uow.Store<IExecutionLookup>().FindOrderAsync(build.OrderNumber, ct);
            if (order is null || !Visible(context, order))
                throw new CommandRejectedException("partial_build_not_found", $"Partial build {id} does not exist.");
            if (build.State != PartialBuildState.Active || !await uow.Store<IWipStorageStore>().ClosePartialBuildAsync(buildId, state, context.Actor, now, ct))
                throw new CommandRejectedException("partial_build_closed", $"Partial build {id} is {build.State}.");
            return new CommandOutcome(
                CommandResult.Ok(id, $"Partial build {id} {(state == PartialBuildState.Completed ? "completed" : "cancelled")}; its material and storage reservations are released."),
                [pipeline.Event(new PartialBuildClosed(buildId, build.OrderNumber, state.ToString()), context, SourceContext, PartialBuildEntity, id)],
                new AuditChange(PartialBuildEntity, id, nameof(PartialBuildState.Active), state.ToString()));
        }, ct);
    }

    private Task<CommandResult> ChangeStorageRuleAsync<TCommand>(string commandType, long ruleId, TCommand command, CommandContext context, string permission,
        StorageRuleState from, StorageRuleState to, bool independent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var id = ruleId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, commandType, permission, ResourceRef.Of(StorageRuleEntity, id), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var rule = (await uow.Store<IWipStorageLookup>().ListStorageRulesAsync(ct)).FirstOrDefault(r => r.RuleId == ruleId)
                ?? throw new CommandRejectedException("storage_rule_not_found", $"Storage rule {id} does not exist.");
            if (rule.State != from)
                throw new CommandRejectedException("storage_rule_state", $"Storage rule {id} is {rule.State}.");
            if (independent && rule.ProposedBy == context.Actor)
                throw new CommandRejectedException("separation_of_duties", "You proposed this storage rule; a different person must decide it.");
            if (!await uow.Store<IWipStorageStore>().DecideStorageRuleAsync(ruleId, from, to, context.Actor, now, ct))
                throw new CommandRejectedException("storage_rule_changed", $"Storage rule {id} changed meanwhile; reload it.");
            return new CommandOutcome(
                CommandResult.Ok(id, $"Storage rule {id} for {rule.PartNumber} at {rule.LocationCode} is now {to}."),
                [pipeline.Event(new StorageRuleChanged(ruleId, to.ToString(), rule.LocationCode, rule.PartNumber), context, SourceContext, StorageRuleEntity, id)],
                new AuditChange(StorageRuleEntity, id, from.ToString(), to.ToString()));
        }, ct);
    }
}
