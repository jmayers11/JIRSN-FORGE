using System.Globalization;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Procurement.Contracts;

namespace Forge.Inventory;

/// <summary>
/// Ask for stock to be scrapped (Sprint 10 §20). The request reserves the quantity against other scrap requests. Quality-held or
/// quality-related material waits for an authorized Quality disposition, and Released stock is put on hold until then.
/// </summary>
/// <param name="Status">The status of the stock to scrap: Released, or Hold/Inspection for quality-held material.</param>
/// <param name="ExecuteDirectly">Scrap at once under an active site rule for one of the requester's roles; refused when no rule covers it.</param>
public sealed record RequestScrap(
    string PartNumber,
    string LocationCode,
    string? LotNumber,
    StockStatus Status,
    decimal Quantity,
    ScrapReasonCode ReasonCode,
    string? Condition = null,
    string? Notes = null,
    long? LinkedOrderNumber = null,
    long? LinkedPoNumber = null,
    long? LinkedWorkOrderId = null,
    string? LinkedNcr = null,
    string? LinkedSupplier = null,
    bool ExecuteDirectly = false);

/// <summary>An independent Inventory approval of a request that does not need Quality.</summary>
public sealed record ApproveScrap(long RequestId, string? Note = null);

/// <summary>An independent Inventory rejection of a request that does not need Quality.</summary>
public sealed record RejectScrap(long RequestId, string? Note = null);

/// <summary>An authorized Quality (MRB) disposition of quality-held material: scrap it, or keep it (the request is rejected).</summary>
public sealed record DisposeScrap(long RequestId, bool Scrap, string? Note = null);

/// <summary>The requester withdraws a request nobody has decided yet.</summary>
public sealed record CancelScrap(long RequestId);

/// <summary>
/// Posts an approved request: the separate, authorized execution step.
/// </summary>
/// <param name="ReleaseAllocations">Release material-task allocations that would otherwise make the quantity unavailable; those tasks go back to Requested.</param>
public sealed record ExecuteScrap(long RequestId, bool ReleaseAllocations = false);

/// <summary>Correct an executed scrap with a linked compensating entry; the stock comes back on hold for Quality to release.</summary>
public sealed record RequestScrapReversal(long RequestId, decimal Quantity);

public sealed record ProposeScrapPolicy(string RoleName, ScrapReasonCode? ReasonCode, decimal? MaxQuantity, decimal? MaxValue);

public sealed record DecideScrapPolicy(long PolicyId, bool Approve);

public sealed record RetireScrapPolicy(long PolicyId);

public sealed record ScrapRequested(long RequestId, string Kind, string State, string PartNumber, string Location, decimal Quantity, bool RequiresQuality) : IDomainEvent
{
    public static string EventType => nameof(ScrapRequested);

    public static int SchemaVersion => 1;
}

public sealed record ScrapDecided(long RequestId, string Kind, string State, string PartNumber, decimal Quantity) : IDomainEvent
{
    public static string EventType => nameof(ScrapDecided);

    public static int SchemaVersion => 1;
}

public sealed record ScrapExecuted(long RequestId, string Kind, long TransactionId, string PartNumber, string Location, decimal Quantity, IReadOnlyList<long> ReleasedTaskIds) : IDomainEvent
{
    public static string EventType => nameof(ScrapExecuted);

    public static int SchemaVersion => 1;
}

public sealed record ScrapPolicyChanged(long PolicyId, string State, string RoleName) : IDomainEvent
{
    public static string EventType => nameof(ScrapPolicyChanged);

    public static int SchemaVersion => 1;
}

public interface IScrapStore
{
    Task<long> InsertScrapRequestAsync(ScrapRequest request, CancellationToken ct);

    /// <summary>Writes the request's new state; false when its state changed since the caller read it.</summary>
    Task<bool> UpdateScrapRequestAsync(ScrapRequest request, ScrapRequestState expected, CancellationToken ct);

    Task<long> InsertScrapPolicyAsync(ScrapPolicy policy, CancellationToken ct);

    Task<bool> DecideScrapPolicyAsync(long policyId, ScrapPolicyState expected, ScrapPolicyState state, string actor, DateTimeOffset at, CancellationToken ct);
}

public sealed partial class InventoryService
{
    private const string ScrapEntity = "ScrapRequest";
    private const string ScrapPolicyEntity = "ScrapPolicy";

    /// <summary>What one stock bucket can give up to scrap now (see <see cref="ScrapEligibility"/>).</summary>
    public static async Task<ScrapEligibility> ScrapEligibilityAsync(IKernelUnitOfWork uow, string partNumber, string locationCode, string? lotNumber,
        StockStatus status, long? excludeRequestId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var onHand = await uow.Store<IInventoryStore>().BalanceAsync(partNumber, locationCode, lotNumber, status, ct);
        var tasks = status == StockStatus.Released
            ? (await uow.Store<IInventoryLookup>().ListMaterialTasksAsync(true, null, ct))
                .Where(t => t.PartNumber == partNumber && t.FromLocation == locationCode && t.LotNumber == lotNumber).ToList()
            : [];
        var allocatedTasks = tasks.Where(t => t.Status == MaterialTaskStatus.Allocated).ToList();
        var allocated = allocatedTasks.Sum(t => t.Reserved);
        var picked = tasks.Where(t => t.Status == MaterialTaskStatus.Picked).Sum(t => t.Reserved);
        var pending = (await uow.Store<IScrapLookup>().ListScrapRequestsAsync(true, partNumber, ct))
            .Where(r => r.Kind == ScrapRequestKind.Scrap && r.RequestId != excludeRequestId && r.LocationCode == locationCode
                && r.LotNumber == lotNumber && r.ExecuteStatus == status)
            .Sum(r => r.Quantity);
        var packed = await uow.Store<IInventoryStore>().PackedQuantityAsync(partNumber, locationCode, lotNumber, status, ct);
        var free = onHand - picked - pending - packed;
        return new ScrapEligibility(partNumber, locationCode, lotNumber, status, onHand, allocated, picked, pending, packed,
            Math.Max(0m, free - allocated), Math.Max(0m, free), [.. allocatedTasks.Select(t => t.TaskId)]);
    }

    /// <summary>The part's last recorded purchase price and currency; Forge has no costing module, so this is only an estimate.</summary>
    public static async Task<(decimal? UnitValue, string? Currency)> LastPurchasePriceAsync(IKernelUnitOfWork uow, string partNumber, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var line = (await uow.Store<IProcurementLookup>().ListPurchaseOrdersAsync(ct))
            .OrderByDescending(po => po.PoNumber)
            .SelectMany(po => po.Lines)
            .FirstOrDefault(l => l.PartNumber == partNumber && l.UnitPrice is not null);
        return line is null ? (null, null) : (line.UnitPrice, line.Currency);
    }

    public Task<CommandResult> RequestScrapAsync(RequestScrap c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var spec = new CommandSpec(SourceContext, nameof(RequestScrap), Permissions.InventoryScrapRequest, ResourceRef.Of(StockEntity, part), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var item = await ItemAsync(uow, part, ct);
            var location = await CompanyLocationAsync(uow, c.LocationCode, ct);
            var lot = RequireLot(item, c.LotNumber);
            var quantity = RequireQuantity(item, c.Quantity);
            if (!Enum.IsDefined(c.ReasonCode))
                throw new CommandRejectedException("invalid_scrap_reason", "Choose a scrap reason.");
            if (!Enum.IsDefined(c.Status))
                throw new CommandRejectedException("invalid_status", "Choose the status of the stock to scrap.");
            var condition = Bounded(c.Condition, 200, "condition");
            var notes = Bounded(c.Notes, 2000, "notes");
            if (c.ReasonCode == ScrapReasonCode.Other && notes is null)
                throw new CommandRejectedException("scrap_notes_required", "Describe why the material is scrapped when the reason is Other.");
            var ncr = Bounded(c.LinkedNcr, 40, "NCR");
            var supplier = Bounded(c.LinkedSupplier, 80, "supplier");
            if (c.LinkedOrderNumber is { } orderNumber)
            {
                var order = await uow.Store<IExecutionLookup>().FindOrderAsync(orderNumber, ct);
                if (order is null || !Visible(context, order))
                    throw new CommandRejectedException("order_not_found", $"Order {orderNumber} does not exist.");
            }

            if (c.LinkedPoNumber is { } poNumber && await uow.Store<IProcurementLookup>().FindPurchaseOrderAsync(poNumber, ct) is null)
                throw new CommandRejectedException("purchase_order_not_found", $"Purchase order {poNumber} does not exist.");
            if (c.LinkedWorkOrderId is <= 0)
                throw new CommandRejectedException("invalid_work_order", "Give a valid maintenance work order number.");

            var requiresQuality = c.Status != StockStatus.Released || c.ReasonCode == ScrapReasonCode.QualityReject || ncr is not null;
            if (c.ExecuteDirectly && requiresQuality)
                throw new CommandRejectedException("quality_disposition_required", "Quality-held or quality-related material needs an authorized Quality disposition; it cannot be scrapped directly.");
            if (c.ExecuteDirectly && !context.Principal.HasPermission(Permissions.InventoryTransact))
                throw new CommandRejectedException("direct_scrap_not_permitted", "Executing scrap needs inventory transaction rights; submit it for approval instead.");

            var store = uow.Store<IInventoryStore>();
            await store.LockPartAsync(part, ct);
            var eligibility = await ScrapEligibilityAsync(uow, part, location.Code, lot, c.Status, null, ct);
            if (quantity > eligibility.EligibleIfAllocationsReleased)
            {
                throw new CommandRejectedException("insufficient_eligible",
                    $"Only {Qty(eligibility.EligibleIfAllocationsReleased)} {item.UnitOfMeasure} {part}{LotText(lot)} at {location.Code} ({c.Status}) can be scrapped: " +
                    $"{Qty(eligibility.OnHand)} on hand, {Qty(eligibility.PickedForTasks)} picked for material tasks, {Qty(eligibility.PendingScrap)} in other scrap requests, " +
                    $"{Qty(eligibility.Packed)} packed. Nothing was changed; request {Qty(eligibility.EligibleIfAllocationsReleased)} or less.");
            }

            var (unitValue, currency) = await LastPurchasePriceAsync(uow, part, ct);
            ScrapPolicy? policy = null;
            if (c.ExecuteDirectly)
            {
                policy = (await uow.Store<IScrapLookup>().ListScrapPoliciesAsync(ct))
                    .Where(p => p.State == ScrapPolicyState.Active && context.Principal.Roles.Contains(p.RoleName)
                        && (p.ReasonCode is null || p.ReasonCode == c.ReasonCode)
                        && (p.MaxQuantity is null || quantity <= p.MaxQuantity)
                        && (p.MaxValue is null || (unitValue is { } value && value * quantity <= p.MaxValue)))
                    .OrderBy(p => p.PolicyId)
                    .FirstOrDefault()
                    ?? throw new CommandRejectedException("direct_scrap_not_permitted", "No active site rule lets your role scrap this directly; submit it for approval.");
                if (quantity > eligibility.Eligible)
                    throw new CommandRejectedException("reserved_stock", $"Only {Qty(eligibility.Eligible)} is free of material-task allocations; submit the request for approval to release allocations.");
            }

            var events = new List<EventEnvelope>();
            long? holdTransactionId = null;
            var executeStatus = c.Status;
            if (requiresQuality && c.Status == StockStatus.Released)
            {
                if (quantity > eligibility.Eligible)
                {
                    throw new CommandRejectedException("reserved_stock",
                        $"{Qty(eligibility.AllocatedToTasks)} of this stock is allocated to material tasks {string.Join(", ", eligibility.AllocatedTaskIds)}; release those allocations before it is put on hold for Quality.");
                }

                var hold = new NewTransaction(InventoryTransactionKind.QualityHold, part, quantity, location.Code, StockStatus.Released, location.Code, StockStatus.Hold, lot,
                    Reason: $"Held for scrap disposition: {context.Reason}");
                holdTransactionId = await store.InsertTransactionAsync(hold, now, context.Actor, ct);
                executeStatus = StockStatus.Hold;
                events.Add(pipeline.Event(new InventoryMoved(holdTransactionId.Value, nameof(InventoryTransactionKind.QualityHold), part, quantity, location.Code, location.Code),
                    context, SourceContext, StockEntity, part));
            }

            var state = policy is not null ? ScrapRequestState.Approved : requiresQuality ? ScrapRequestState.PendingDisposition : ScrapRequestState.PendingApproval;
            var request = new ScrapRequest(0, ScrapRequestKind.Scrap, part, location.Code, lot, c.Status, executeStatus, quantity, c.ReasonCode, condition, notes,
                c.LinkedOrderNumber, c.LinkedPoNumber, c.LinkedWorkOrderId, ncr, supplier, requiresQuality, policy?.PolicyId, unitValue, currency, state,
                context.Actor, now, holdTransactionId,
                policy is null ? null : PolicyActor(policy.PolicyId), policy is null ? null : now, policy is null ? null : "Direct scrap under an active site rule.",
                null, null, null, null, null);
            var requestId = await uow.Store<IScrapStore>().InsertScrapRequestAsync(request, ct);
            request = request with { RequestId = requestId };
            var id = requestId.ToString(CultureInfo.InvariantCulture);
            events.Add(pipeline.Event(new ScrapRequested(requestId, request.Kind.ToString(), state.ToString(), part, location.Code, quantity, requiresQuality),
                context, SourceContext, ScrapEntity, id));
            string message;
            if (policy is not null)
            {
                var (executed, extra) = await PostScrapAsync(uow, request, context, now, false, ct);
                events.AddRange(extra);
                request = executed;
                message = $"Scrapped {Qty(quantity)} {item.UnitOfMeasure} {part}{LotText(lot)} at {location.Code} under site rule {policy.PolicyId} (request {id}).";
            }
            else
            {
                message = requiresQuality
                    ? $"Scrap request {id}: {Qty(quantity)} {item.UnitOfMeasure} {part}{LotText(lot)} {(holdTransactionId is null ? "stays" : "is now")} on hold at {location.Code} until Quality decides its disposition."
                    : $"Scrap request {id}: {Qty(quantity)} {item.UnitOfMeasure} {part}{LotText(lot)} at {location.Code}. A different person must approve it before it is executed.";
            }

            return new CommandOutcome(CommandResult.Ok(id, message), events, new AuditChange(ScrapEntity, id, null, CanonicalJson.Serialize(request)));
        }, ct);
    }

    public Task<CommandResult> ApproveScrapAsync(ApproveScrap c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return DecideScrapAsync(nameof(ApproveScrap), c.RequestId, c, context, Permissions.InventoryApprove, ScrapRequestState.PendingApproval, true, c.Note, ct);
    }

    public Task<CommandResult> RejectScrapAsync(RejectScrap c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return DecideScrapAsync(nameof(RejectScrap), c.RequestId, c, context, Permissions.InventoryApprove, ScrapRequestState.PendingApproval, false, c.Note, ct);
    }

    public Task<CommandResult> DisposeScrapAsync(DisposeScrap c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return DecideScrapAsync(nameof(DisposeScrap), c.RequestId, c, context, Permissions.QualityDisposition, ScrapRequestState.PendingDisposition, c.Scrap, c.Note, ct);
    }

    public Task<CommandResult> CancelScrapAsync(CancelScrap c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var id = c.RequestId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, nameof(CancelScrap), Permissions.InventoryScrapRequest, ResourceRef.Of(ScrapEntity, id), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var request = await FindScrapAsync(uow, c.RequestId, ct);
            if (request.RequestedBy != context.Actor)
                throw new CommandRejectedException("not_requester", "Only the person who asked for this scrap can withdraw it.");
            if (request.State is not (ScrapRequestState.PendingApproval or ScrapRequestState.PendingDisposition))
                throw new CommandRejectedException("not_pending", $"Scrap request {id} is {request.State}; only an undecided request can be withdrawn.");

            await uow.Store<IInventoryStore>().LockPartAsync(request.PartNumber, ct);
            var (released, extra) = await ReleaseScrapHoldAsync(uow, request, context, now, "withdrawn", ct);
            var cancelled = released with { State = ScrapRequestState.Cancelled };
            await SaveScrapAsync(uow, cancelled, request.State, ct);
            var events = new List<EventEnvelope>(extra)
            {
                pipeline.Event(new ScrapDecided(request.RequestId, request.Kind.ToString(), nameof(ScrapRequestState.Cancelled), request.PartNumber, request.Quantity),
                    context, SourceContext, ScrapEntity, id),
            };
            return new CommandOutcome(CommandResult.Ok(id, $"Scrap request {id} withdrawn{(request.HoldTransactionId is null ? "." : "; its hold was released.")}"),
                events, new AuditChange(ScrapEntity, id, request.State.ToString(), nameof(ScrapRequestState.Cancelled)));
        }, ct);
    }

    public Task<CommandResult> ExecuteScrapAsync(ExecuteScrap c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var id = c.RequestId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, nameof(ExecuteScrap), Permissions.InventoryTransact, ResourceRef.Of(ScrapEntity, id), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var request = await FindScrapAsync(uow, c.RequestId, ct);
            if (request.State != ScrapRequestState.Approved)
                throw new CommandRejectedException("not_approved", $"Scrap request {id} is {request.State}; only an approved request is executed.");
            await uow.Store<IInventoryStore>().LockPartAsync(request.PartNumber, ct);
            var (executed, events) = request.Kind == ScrapRequestKind.Reversal
                ? await PostReversalAsync(uow, request, context, now, ct)
                : await PostScrapAsync(uow, request, context, now, c.ReleaseAllocations, ct);
            var verb = request.Kind == ScrapRequestKind.Reversal ? "Reversal" : "Scrap";
            return new CommandOutcome(
                CommandResult.Ok(id, $"{verb} request {id} executed: {Qty(request.Quantity)} {request.PartNumber}{LotText(request.LotNumber)} at {request.LocationCode} (transaction {executed.TransactionId})."),
                events, new AuditChange(ScrapEntity, id, nameof(ScrapRequestState.Approved), CanonicalJson.Serialize(executed)));
        }, ct);
    }

    public Task<CommandResult> RequestScrapReversalAsync(RequestScrapReversal c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var id = c.RequestId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, nameof(RequestScrapReversal), Permissions.InventoryScrapRequest, ResourceRef.Of(ScrapEntity, id), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var original = await FindScrapAsync(uow, c.RequestId, ct);
            if (original.Kind != ScrapRequestKind.Scrap || original.State != ScrapRequestState.Executed)
                throw new CommandRejectedException("not_executed_scrap", $"Request {id} is not an executed scrap; only posted scrap is corrected.");
            if (c.Quantity <= 0 || c.Quantity > original.Quantity)
                throw new CommandRejectedException("invalid_quantity", $"A correction returns between 0 and {Qty(original.Quantity)} {original.PartNumber}.");
            var item = await ItemAsync(uow, original.PartNumber, ct);
            var quantity = RequireQuantity(item, c.Quantity);
            if ((await uow.Store<IScrapLookup>().ListScrapRequestsAsync(false, original.PartNumber, ct))
                .Any(r => r.CorrectsRequestId == original.RequestId && r.State is not (ScrapRequestState.Rejected or ScrapRequestState.Cancelled)))
                throw new CommandRejectedException("reversal_exists", $"Scrap request {id} already has a correction.");

            var reversal = new ScrapRequest(0, ScrapRequestKind.Reversal, original.PartNumber, original.LocationCode, original.LotNumber, StockStatus.Hold, StockStatus.Hold,
                quantity, original.ReasonCode, original.Condition, Bounded(context.Reason, 2000, "reason"), original.LinkedOrderNumber, original.LinkedPoNumber,
                original.LinkedWorkOrderId, original.LinkedNcr, original.LinkedSupplier, false, null, original.EstimatedUnitValue, original.Currency,
                ScrapRequestState.PendingApproval, context.Actor, now, null, null, null, null, null, null, null, null, original.RequestId);
            var reversalId = await uow.Store<IScrapStore>().InsertScrapRequestAsync(reversal, ct);
            reversal = reversal with { RequestId = reversalId };
            var key = reversalId.ToString(CultureInfo.InvariantCulture);
            return new CommandOutcome(
                CommandResult.Ok(key, $"Correction {key} of scrap request {id} requested: {Qty(quantity)} {original.PartNumber} would come back on hold at {original.LocationCode}. A different person must approve it."),
                [pipeline.Event(new ScrapRequested(reversalId, nameof(ScrapRequestKind.Reversal), nameof(ScrapRequestState.PendingApproval), original.PartNumber, original.LocationCode, quantity, false),
                    context, SourceContext, ScrapEntity, key)],
                new AuditChange(ScrapEntity, key, null, CanonicalJson.Serialize(reversal)));
        }, ct);
    }

    public Task<CommandResult> ProposeScrapPolicyAsync(ProposeScrapPolicy c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var spec = new CommandSpec(SourceContext, nameof(ProposeScrapPolicy), Permissions.InventoryManage, ResourceRef.Of(ScrapPolicyEntity), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var role = Bounded(c.RoleName, 80, "role") ?? throw new CommandRejectedException("role_required", "Name the role the rule applies to.");
            if (!Roles.PermissionsOf(role).Any())
                throw new CommandRejectedException("unknown_role", $"There is no role {role}.");
            if (c.ReasonCode is ScrapReasonCode.QualityReject)
                throw new CommandRejectedException("quality_disposition_required", "Quality rejections always need an authorized Quality disposition.");
            if (c.ReasonCode is { } reason && !Enum.IsDefined(reason))
                throw new CommandRejectedException("invalid_scrap_reason", "Choose a scrap reason.");
            if (c.MaxQuantity is <= 0 || c.MaxValue is < 0)
                throw new CommandRejectedException("invalid_limit", "Limits must be positive.");
            if (c.MaxQuantity is null && c.MaxValue is null)
                throw new CommandRejectedException("limit_required", "Set a quantity or value limit for direct scrap.");
            var policy = new ScrapPolicy(0, role, c.ReasonCode, c.MaxQuantity, c.MaxValue, ScrapPolicyState.Proposed, context.Actor, now, null, null);
            var policyId = await uow.Store<IScrapStore>().InsertScrapPolicyAsync(policy, ct);
            var id = policyId.ToString(CultureInfo.InvariantCulture);
            return new CommandOutcome(
                CommandResult.Ok(id, $"Direct-scrap rule {id} for {role} proposed. A different person must approve it before it applies."),
                [pipeline.Event(new ScrapPolicyChanged(policyId, nameof(ScrapPolicyState.Proposed), role), context, SourceContext, ScrapPolicyEntity, id)],
                new AuditChange(ScrapPolicyEntity, id, null, CanonicalJson.Serialize(policy with { PolicyId = policyId })));
        }, ct);
    }

    public Task<CommandResult> DecideScrapPolicyAsync(DecideScrapPolicy c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeScrapPolicyAsync(nameof(DecideScrapPolicy), c.PolicyId, c, context, Permissions.InventoryApprove, ScrapPolicyState.Proposed,
            c.Approve ? ScrapPolicyState.Active : ScrapPolicyState.Rejected, true, ct);
    }

    public Task<CommandResult> RetireScrapPolicyAsync(RetireScrapPolicy c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeScrapPolicyAsync(nameof(RetireScrapPolicy), c.PolicyId, c, context, Permissions.InventoryManage, ScrapPolicyState.Active,
            ScrapPolicyState.Retired, false, ct);
    }

    private Task<CommandResult> ChangeScrapPolicyAsync<TCommand>(string commandType, long policyId, TCommand command, CommandContext context, string permission,
        ScrapPolicyState from, ScrapPolicyState to, bool independent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var id = policyId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, commandType, permission, ResourceRef.Of(ScrapPolicyEntity, id), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var policy = (await uow.Store<IScrapLookup>().ListScrapPoliciesAsync(ct)).FirstOrDefault(p => p.PolicyId == policyId)
                ?? throw new CommandRejectedException("scrap_policy_not_found", $"Direct-scrap rule {id} does not exist.");
            if (policy.State != from)
                throw new CommandRejectedException("scrap_policy_state", $"Direct-scrap rule {id} is {policy.State}.");
            if (independent && policy.ProposedBy == context.Actor)
                throw new CommandRejectedException("separation_of_duties", "You proposed this rule; a different person must decide it.");
            if (!await uow.Store<IScrapStore>().DecideScrapPolicyAsync(policyId, from, to, context.Actor, now, ct))
                throw new CommandRejectedException("scrap_policy_changed", $"Direct-scrap rule {id} changed meanwhile; reload it.");
            return new CommandOutcome(
                CommandResult.Ok(id, $"Direct-scrap rule {id} for {policy.RoleName} is now {to}."),
                [pipeline.Event(new ScrapPolicyChanged(policyId, to.ToString(), policy.RoleName), context, SourceContext, ScrapPolicyEntity, id)],
                new AuditChange(ScrapPolicyEntity, id, from.ToString(), to.ToString()));
        }, ct);
    }

    private Task<CommandResult> DecideScrapAsync<TCommand>(string commandType, long requestId, TCommand command, CommandContext context, string permission,
        ScrapRequestState expected, bool approve, string? note, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var id = requestId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, commandType, permission, ResourceRef.Of(ScrapEntity, id), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var request = await FindScrapAsync(uow, requestId, ct);
            if (request.State != expected)
            {
                throw new CommandRejectedException(request.State is ScrapRequestState.PendingApproval or ScrapRequestState.PendingDisposition ? "wrong_decider" : "not_pending",
                    request.State switch
                    {
                        ScrapRequestState.PendingDisposition => $"Scrap request {id} is quality-related; an authorized Quality disposition decides it.",
                        ScrapRequestState.PendingApproval => $"Scrap request {id} needs an Inventory approval, not a Quality disposition.",
                        _ => $"Scrap request {id} is {request.State}.",
                    });
            }

            if (request.RequestedBy == context.Actor)
                throw new CommandRejectedException("separation_of_duties", "You asked for this scrap; a different person must decide it.");

            var decisionNote = Bounded(note, 2000, "note") ?? Bounded(context.Reason, 2000, "reason");
            var events = new List<EventEnvelope>();
            ScrapRequest decided;
            if (approve)
            {
                decided = request with { State = ScrapRequestState.Approved, DecidedBy = context.Actor, DecidedAt = now, DecisionNote = decisionNote };
            }
            else
            {
                await uow.Store<IInventoryStore>().LockPartAsync(request.PartNumber, ct);
                var (released, extra) = await ReleaseScrapHoldAsync(uow, request, context, now, "not scrapped", ct);
                events.AddRange(extra);
                decided = released with { State = ScrapRequestState.Rejected, DecidedBy = context.Actor, DecidedAt = now, DecisionNote = decisionNote };
            }

            await SaveScrapAsync(uow, decided, request.State, ct);
            events.Add(pipeline.Event(new ScrapDecided(request.RequestId, request.Kind.ToString(), decided.State.ToString(), request.PartNumber, request.Quantity),
                context, SourceContext, ScrapEntity, id));
            var message = approve
                ? $"Scrap request {id} approved: {Qty(request.Quantity)} {request.PartNumber}{LotText(request.LotNumber)} at {request.LocationCode}. Execute it as a separate step."
                : $"Scrap request {id} rejected; nothing was scrapped{(request.HoldTransactionId is null ? "." : " and the hold it placed was released.")}";
            return new CommandOutcome(CommandResult.Ok(id, message), events, new AuditChange(ScrapEntity, id, request.State.ToString(), decided.State.ToString()));
        }, ct);
    }

    /// <summary>The ledger entry of an approved scrap, inside the caller's transaction (the part is already locked).</summary>
    private async Task<(ScrapRequest Executed, IReadOnlyList<EventEnvelope> Events)> PostScrapAsync(IKernelUnitOfWork uow, ScrapRequest request, CommandContext context,
        DateTimeOffset now, bool releaseAllocations, CancellationToken ct)
    {
        var store = uow.Store<IInventoryStore>();
        var location = await CompanyLocationAsync(uow, request.LocationCode, ct);
        var eligibility = await ScrapEligibilityAsync(uow, request.PartNumber, location.Code, request.LotNumber, request.ExecuteStatus, request.RequestId, ct);
        var events = new List<EventEnvelope>();
        var releasedTasks = new List<long>();
        if (request.Quantity > eligibility.EligibleIfAllocationsReleased)
        {
            throw new CommandRejectedException("insufficient_eligible",
                $"Only {Qty(eligibility.EligibleIfAllocationsReleased)} {request.PartNumber}{LotText(request.LotNumber)} ({request.ExecuteStatus}) is left to scrap at {location.Code}; nothing was posted.");
        }

        if (request.Quantity > eligibility.Eligible)
        {
            if (!releaseAllocations)
            {
                throw new CommandRejectedException("reserved_stock",
                    $"{Qty(request.Quantity - eligibility.Eligible)} of this quantity is allocated to material tasks {string.Join(", ", eligibility.AllocatedTaskIds)}. " +
                    "Execute again choosing to release those allocations; the tasks go back to Requested and are allocated again from other stock.");
            }

            var shortBy = request.Quantity - eligibility.Eligible;
            var lookup = uow.Store<IInventoryLookup>();
            foreach (var taskId in eligibility.AllocatedTaskIds.OrderByDescending(t => t))
            {
                if (shortBy <= 0) break;
                var task = await lookup.FindMaterialTaskAsync(taskId, ct);
                if (task is null || task.Status != MaterialTaskStatus.Allocated) continue;
                var requeued = task with { Status = MaterialTaskStatus.Requested, FromLocation = null, AllocatedBy = null, AllocatedAt = null };
                if (!await store.UpdateMaterialTaskAsync(requeued, task, ct))
                    throw new CommandRejectedException("material_task_changed", $"Material task {taskId} changed meanwhile; try again.");
                shortBy -= task.Reserved;
                releasedTasks.Add(taskId);
                events.Add(pipeline.Event(new MaterialTaskChanged(taskId, nameof(MaterialTaskStatus.Requested), task.OrderNumber), context, SourceContext, "MaterialTask",
                    taskId.ToString(CultureInfo.InvariantCulture)));
            }
        }

        if (request.ExecuteStatus == StockStatus.Released)
            await productRelease.RequireReleasedScopeAsync(uow, context.Principal, request.PartNumber, request.LotNumber, location.Code, request.Quantity, now, ct);
        var reason = $"Scrap request {request.RequestId} ({request.ReasonCode}): {context.Reason}";
        var approvedBy = request.DecidedBy;
        // Links to orders, purchase orders and work orders stay on the request: a scrap entry is not consumption by that order.
        var txn = new NewTransaction(InventoryTransactionKind.Scrap, request.PartNumber, request.Quantity, location.Code, request.ExecuteStatus, null, null, request.LotNumber,
            Reason: Truncate(reason, 2000), ApprovedBy: approvedBy);
        var txnId = await store.InsertTransactionAsync(txn, now, context.Actor, ct);
        var executed = request with { State = ScrapRequestState.Executed, ExecutedBy = context.Actor, ExecutedAt = now, TransactionId = txnId };
        await SaveScrapAsync(uow, executed, request.State, ct);
        var id = request.RequestId.ToString(CultureInfo.InvariantCulture);
        events.Add(pipeline.Event(new InventoryMoved(txnId, nameof(InventoryTransactionKind.Scrap), request.PartNumber, request.Quantity, location.Code, null),
            context, SourceContext, StockEntity, request.PartNumber));
        events.Add(pipeline.Event(new ScrapExecuted(request.RequestId, request.Kind.ToString(), txnId, request.PartNumber, location.Code, request.Quantity, releasedTasks),
            context, SourceContext, ScrapEntity, id));
        return (executed, events);
    }

    /// <summary>The compensating entry of an approved correction: the quantity returns on hold, where Quality releases it as usual.</summary>
    private async Task<(ScrapRequest Executed, IReadOnlyList<EventEnvelope> Events)> PostReversalAsync(IKernelUnitOfWork uow, ScrapRequest request, CommandContext context,
        DateTimeOffset now, CancellationToken ct)
    {
        var location = await CompanyLocationAsync(uow, request.LocationCode, ct);
        var original = await FindScrapAsync(uow, request.CorrectsRequestId!.Value, ct);
        var txn = new NewTransaction(InventoryTransactionKind.Adjustment, request.PartNumber, request.Quantity, null, null, location.Code, StockStatus.Hold, request.LotNumber,
            Reason: Truncate($"Correction {request.RequestId} of scrap request {original.RequestId} (transaction {original.TransactionId}): {context.Reason}", 2000),
            ApprovedBy: request.DecidedBy);
        var txnId = await uow.Store<IInventoryStore>().InsertTransactionAsync(txn, now, context.Actor, ct);
        var executed = request with { State = ScrapRequestState.Executed, ExecutedBy = context.Actor, ExecutedAt = now, TransactionId = txnId };
        await SaveScrapAsync(uow, executed, request.State, ct);
        var id = request.RequestId.ToString(CultureInfo.InvariantCulture);
        return (executed,
        [
            pipeline.Event(new InventoryMoved(txnId, nameof(InventoryTransactionKind.Adjustment), request.PartNumber, request.Quantity, null, location.Code),
                context, SourceContext, StockEntity, request.PartNumber),
            pipeline.Event(new ScrapExecuted(request.RequestId, request.Kind.ToString(), txnId, request.PartNumber, location.Code, request.Quantity, []),
                context, SourceContext, ScrapEntity, id),
        ]);
    }

    /// <summary>Undoes the hold a request placed, when it is rejected or withdrawn. Stock it found already on hold stays there.</summary>
    private async Task<(ScrapRequest Request, IReadOnlyList<EventEnvelope> Events)> ReleaseScrapHoldAsync(IKernelUnitOfWork uow, ScrapRequest request, CommandContext context,
        DateTimeOffset now, string outcome, CancellationToken ct)
    {
        if (request.HoldTransactionId is null)
            return (request, []);
        var store = uow.Store<IInventoryStore>();
        var onHold = await store.BalanceAsync(request.PartNumber, request.LocationCode, request.LotNumber, StockStatus.Hold, ct);
        var quantity = Math.Min(onHold, request.Quantity);
        if (quantity <= 0)
            return (request, []);
        var txn = new NewTransaction(InventoryTransactionKind.QualityRelease, request.PartNumber, quantity, request.LocationCode, StockStatus.Hold, request.LocationCode,
            StockStatus.Released, request.LotNumber, Reason: Truncate($"Scrap request {request.RequestId} {outcome}: {context.Reason}", 2000));
        var txnId = await store.InsertTransactionAsync(txn, now, context.Actor, ct);
        return (request with { ReleaseTransactionId = txnId },
        [
            pipeline.Event(new InventoryMoved(txnId, nameof(InventoryTransactionKind.QualityRelease), request.PartNumber, quantity, request.LocationCode, request.LocationCode),
                context, SourceContext, StockEntity, request.PartNumber),
        ]);
    }

    private static async Task<ScrapRequest> FindScrapAsync(IKernelUnitOfWork uow, long requestId, CancellationToken ct) =>
        await uow.Store<IScrapLookup>().FindScrapRequestAsync(requestId, ct)
            ?? throw new CommandRejectedException("scrap_request_not_found", $"Scrap request {requestId} does not exist.");

    private static async Task SaveScrapAsync(IKernelUnitOfWork uow, ScrapRequest request, ScrapRequestState expected, CancellationToken ct)
    {
        if (!await uow.Store<IScrapStore>().UpdateScrapRequestAsync(request, expected, ct))
            throw new CommandRejectedException("scrap_request_changed", $"Scrap request {request.RequestId} changed meanwhile; reload it.");
    }

    private static string PolicyActor(long policyId) => "policy:" + policyId.ToString(CultureInfo.InvariantCulture);

    private static string LotText(string? lot) => lot is null ? string.Empty : " lot " + lot;

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    private static string? Bounded(string? value, int length, string what)
    {
        var text = Optional(value);
        if (text is not null && text.Length > length)
            throw new CommandRejectedException("text_too_long", $"Keep the {what} to {length} characters.");
        return text;
    }
}
