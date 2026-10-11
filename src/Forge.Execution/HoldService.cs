using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Resources.Contracts;

namespace Forge.Execution;

/// <summary>Stop work on a target until released. The reason given with the command is the hold's reason.</summary>
public sealed record PlaceHold(HoldScope Scope, long? OrderNumber = null, int? Sequence = null, string? PartNumber = null, string? WorkCenterCode = null, string? MachineCode = null);

public sealed record ReleaseHold(long HoldId);

public sealed record HoldChanged(long HoldId, string Scope, string Target, bool Active) : IDomainEvent
{
    public static string EventType => nameof(HoldChanged);

    public static int SchemaVersion => 1;
}

/// <summary>
/// Production holds (V3.9, V3 brief B). A hold on an order, an operation, a part, a work center or a machine stops
/// work starting there; a hold on an order or part also stops good output being reported. Placing and releasing
/// both need a reason, and a different person releases it.
/// </summary>
public sealed class HoldService(CommandPipeline pipeline)
{
    public const string SourceContext = "Execution";
    private const string Entity = "ProductionHold";

    public Task<CommandResult> PlaceAsync(PlaceHold c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var spec = new CommandSpec(SourceContext, nameof(PlaceHold), Permissions.HoldsManage, ResourceRef.Of(Entity, c.Scope.ToString()), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            long? order = null;
            int? sequence = null;
            string? part = null, workCenter = null, machine = null;
            Guid? program = null;
            switch (c.Scope)
            {
                case HoldScope.Order:
                case HoldScope.Operation:
                    var info = c.OrderNumber is { } n ? await uow.Store<IExecutionLookup>().FindOrderAsync(n, ct) : null;
                    if (info is null || (info.ProgramId is { } p && !context.Principal.Programs.Contains(new ProgramId(p))))
                    {
                        throw new CommandRejectedException("order_not_found", $"Order {c.OrderNumber} does not exist.");
                    }

                    order = info.OrderNumber;
                    program = info.ProgramId;
                    if (c.Scope == HoldScope.Operation)
                    {
                        sequence = info.Operations.FirstOrDefault(o => o.Sequence == c.Sequence)?.Sequence
                            ?? throw new CommandRejectedException("operation_not_found", $"Order {order} has no operation {c.Sequence}.");
                    }

                    break;
                case HoldScope.Part:
                    part = BusinessCode.Require(c.PartNumber ?? string.Empty, "Part");
                    if (await uow.Store<IEngineeringLookup>().FindItemAsync(part, ct) is null)
                    {
                        throw new CommandRejectedException("unknown_part", $"Part {part} does not exist.");
                    }

                    break;
                case HoldScope.WorkCenter:
                    workCenter = BusinessCode.Require(c.WorkCenterCode ?? string.Empty, "Work center");
                    if (!(await uow.Store<IResourcesLookup>().ListWorkCentersAsync(ct)).Any(w => w.Code == workCenter))
                    {
                        throw new CommandRejectedException("unknown_work_center", $"Work center {workCenter} does not exist.");
                    }

                    break;
                default:
                    machine = BusinessCode.Require(c.MachineCode ?? string.Empty, "Machine");
                    if (await uow.Store<IResourcesLookup>().FindMachineAsync(machine, ct) is null)
                    {
                        throw new CommandRejectedException("unknown_machine", $"Machine {machine} does not exist.");
                    }

                    break;
            }

            var hold = new ProductionHold(0, c.Scope, order, sequence, part, workCenter, machine, context.Reason!.Trim(), now, context.Actor);
            if ((await uow.Store<IProductionHoldLookup>().ListProductionHoldsAsync(false, ct)).FirstOrDefault(h => h with { HoldId = 0, Reason = hold.Reason, PlacedAt = now, PlacedBy = context.Actor } == hold) is { } same)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(Key(same.HoldId), $"{same.Target} is already on hold ({same.Describe()})."));
            }

            var id = await uow.Store<IProductionHoldStore>().InsertProductionHoldAsync(hold, ct);
            hold = hold with { HoldId = id };
            var programId = program is { } g ? new ProgramId(g) : (ProgramId?)null;
            return new CommandOutcome(
                CommandResult.Ok(Key(id), $"{hold.Target} is on hold (hold {id}); work there does not start until a different person releases it."),
                [pipeline.Event(new HoldChanged(id, c.Scope.ToString(), hold.Target, true), context, SourceContext, Entity, Key(id), programId)],
                new AuditChange(Entity, Key(id), null, CanonicalJson.Serialize(hold), programId));
        }, ct);
    }

    public Task<CommandResult> ReleaseAsync(ReleaseHold c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var key = Key(c.HoldId);
        var spec = new CommandSpec(SourceContext, nameof(ReleaseHold), Permissions.HoldsManage, ResourceRef.Of(Entity, key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var hold = (await uow.Store<IProductionHoldLookup>().ListProductionHoldsAsync(true, ct)).FirstOrDefault(h => h.HoldId == c.HoldId)
                ?? throw new CommandRejectedException("hold_not_found", $"Hold {key} does not exist.");
            if (!hold.Active)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(key, $"Hold {key} was already released."));
            }

            if (hold.PlacedBy == context.Actor)
            {
                throw new CommandRejectedException("separation_of_duties", "You placed this hold; a different person must release it.");
            }

            await uow.Store<IProductionHoldStore>().ReleaseProductionHoldAsync(c.HoldId, now, context.Actor, context.Reason!.Trim(), ct);
            var released = hold with { ReleasedAt = now, ReleasedBy = context.Actor, ReleaseReason = context.Reason!.Trim() };
            return new CommandOutcome(
                CommandResult.Ok(key, $"Hold {key} on {hold.Target} released."),
                [pipeline.Event(new HoldChanged(c.HoldId, hold.Scope.ToString(), hold.Target, false), context, SourceContext, Entity, key)],
                new AuditChange(Entity, key, CanonicalJson.Serialize(hold), CanonicalJson.Serialize(released)));
        }, ct);
    }

    /// <summary>Refuses to start (or report good output) where an active production hold applies.</summary>
    internal static async Task RequireNoProductionHoldAsync(IKernelUnitOfWork uow, OrderInfo order, OrderOperationInfo? operation, string? machine, CancellationToken ct)
    {
        var holds = (await uow.Store<IProductionHoldLookup>().ListProductionHoldsAsync(false, ct))
            .Where(h => operation is null
                ? h.CoversOrder(order.OrderNumber, order.PartNumber)
                : h.Covers(order.OrderNumber, order.PartNumber, operation.Sequence, operation.WorkCenterCode, machine))
            .OrderBy(h => h.HoldId)
            .ToList();
        if (holds.Count > 0)
        {
            throw new CommandRejectedException("on_hold", $"{string.Join(" ", holds.Select(h => h.Describe() + "."))} A different person from the one who placed it releases it.");
        }
    }

    private static string Key(long id) => id.ToString(CultureInfo.InvariantCulture);
}
