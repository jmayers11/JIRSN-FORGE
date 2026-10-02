using Forge.Engineering.Contracts;
using Forge.Kernel.Access;
using Forge.Programs.Contracts;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;

namespace Forge.Planning;

public enum DemandKind
{
    CustomerOrder,
    Forecast,
    Service,
    Internal,
    ContractDelivery,
}

public enum DemandStatus
{
    Open,

    /// <summary>Fulfilled or otherwise no longer needed; kept for history.</summary>
    Closed,

    Cancelled,
}

/// <summary>A need for a quantity of a part by a date, from outside the bill of materials.</summary>
public sealed record DemandInfo(
    long DemandId,
    string PartNumber,
    decimal Quantity,
    DateOnly NeedDate,
    int Priority,
    DemandKind Kind,
    string? Customer,
    string? Reference,
    Guid? ProgramId,
    DemandStatus Status,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string? SourceKey = null);

public sealed record RecordDemand(string PartNumber, decimal Quantity, DateOnly NeedDate, int Priority, DemandKind Kind, string? Customer, string? Reference, Guid? ProgramId);

public sealed record ChangeDemand(long DemandId, decimal Quantity, DateOnly NeedDate, int Priority);

public sealed record CloseDemand(long DemandId, bool Cancel);

/// <summary>Turns a contract delivery into demand, or brings that demand back in step with the contract.</summary>
public sealed record PlanDelivery(long DeliveryId, int? Priority);

public sealed record DemandChanged(long DemandId, string PartNumber, string Change) : IDomainEvent
{
    public static string EventType => nameof(DemandChanged);

    public static int SchemaVersion => 1;
}

public interface IDemandStore
{
    Task<long> InsertAsync(DemandInfo demand, CancellationToken ct);

    Task UpdateAsync(DemandInfo demand, DateTimeOffset at, string actor, CancellationToken ct);

    Task<DemandInfo?> FindAsync(long demandId, CancellationToken ct);

    Task<DemandInfo?> FindBySourceAsync(string sourceKey, CancellationToken ct);

    /// <summary>Demand by need date; closed and cancelled only when asked.</summary>
    Task<IReadOnlyList<DemandInfo>> ListAsync(bool includeClosed, CancellationToken ct);
}

/// <summary>Independent demand. Changing a quantity or date needs a reason because MRP results move with it.</summary>
public sealed class DemandService(CommandPipeline pipeline)
{
    public const string SourceContext = "Planning";
    private const string Entity = "Demand";

    public Task<CommandResult> RecordAsync(RecordDemand c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var program = c.ProgramId is { } p ? new ProgramId(p) : (ProgramId?)null;
        var spec = new CommandSpec(SourceContext, nameof(RecordDemand), Permissions.DemandManage, new ResourceRef(Entity, null, program));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var part = BusinessCode.Require(c.PartNumber, "Part");
            Validate(c.Quantity, c.Priority);
            if (await uow.Store<IEngineeringLookup>().FindItemAsync(part, ct) is null)
            {
                throw new CommandRejectedException("unknown_part", $"Part {part} does not exist.");
            }

            if (c.ProgramId is { } programId)
            {
                await RequireProgramAsync(uow, programId, context, ct);
            }

            var demand = new DemandInfo(0, part, c.Quantity, c.NeedDate, c.Priority, c.Kind, Trim(c.Customer, 200), Trim(c.Reference, 100), c.ProgramId, DemandStatus.Open, context.Actor, now);
            var demandId = await uow.Store<IDemandStore>().InsertAsync(demand, ct);
            var id = demandId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new CommandOutcome(
                CommandResult.Ok(id, $"Demand {id}: {c.Quantity} {part} needed {c.NeedDate:yyyy-MM-dd} ({c.Kind})."),
                [pipeline.Event(new DemandChanged(demandId, part, "Recorded"), context, SourceContext, Entity, id, program)],
                new AuditChange(Entity, id, null, CanonicalJson.Serialize(demand with { DemandId = demandId }), program));
        }, ct);
    }

    /// <summary>
    /// Plans a contract delivery: creates ContractDelivery demand for it, or updates the demand it
    /// already made to the contract's current quantity and date, or cancels it when the delivery was
    /// cancelled. The demand carries the program, so it is segregated like the contract.
    /// </summary>
    public Task<CommandResult> PlanDeliveryAsync(PlanDelivery c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(SourceContext, nameof(PlanDelivery), Permissions.DemandManage, ResourceRef.Of("Delivery", c.DeliveryId));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var reference = await uow.Store<IProgramsLookup>().FindDeliveryAsync(c.DeliveryId, ct);
            if (reference is null || !context.Principal.Programs.Contains(new ProgramId(reference.ProgramId)))
            {
                throw new CommandRejectedException("delivery_not_found", $"Delivery {c.DeliveryId} does not exist.");
            }

            Validate(reference.Delivery.Quantity, c.Priority ?? 3);
            var program = new ProgramId(reference.ProgramId);
            var store = uow.Store<IDemandStore>();
            var existing = await store.FindBySourceAsync(reference.SourceKey, ct);
            var delivery = reference.Delivery;
            var label = $"{reference.ContractNumber} CLIN {delivery.Clin} delivery {delivery.DeliveryId}";

            if (existing is null)
            {
                if (delivery.Cancelled || reference.ContractStatus != ContractStatus.Active || reference.PartNumber is null)
                {
                    throw new CommandRejectedException("delivery_not_plannable", $"{label} is cancelled or its contract is not active; there is nothing to plan.");
                }

                var demand = new DemandInfo(0, reference.PartNumber, delivery.Quantity, delivery.DueDate, c.Priority ?? 3, DemandKind.ContractDelivery, reference.Customer, label, reference.ProgramId,
                    DemandStatus.Open, context.Actor, now, reference.SourceKey);
                var demandId = await store.InsertAsync(demand, ct);
                var newId = demandId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return new CommandOutcome(
                    CommandResult.Ok(newId, $"Demand {newId}: {delivery.Quantity} {reference.PartNumber} for {label}, needed {delivery.DueDate:yyyy-MM-dd}."),
                    [pipeline.Event(new DemandChanged(demandId, reference.PartNumber, "Recorded"), context, SourceContext, Entity, newId, program)],
                    new AuditChange(Entity, newId, null, CanonicalJson.Serialize(demand with { DemandId = demandId }), program));
            }

            var id = existing.DemandId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (existing.Status != DemandStatus.Open)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(id, $"Demand {id} for {label} is already {existing.Status.ToString().ToLowerInvariant()}."));
            }

            var updated = delivery.Cancelled || reference.ContractStatus == ContractStatus.Cancelled
                ? existing with { Status = DemandStatus.Cancelled }
                : existing with { Quantity = delivery.Quantity, NeedDate = delivery.DueDate, Priority = c.Priority ?? existing.Priority };
            if (updated == existing)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(id, $"Demand {id} already matches {label}."));
            }

            await store.UpdateAsync(updated, now, context.Actor, ct);
            return new CommandOutcome(
                CommandResult.Ok(id, updated.Status == DemandStatus.Cancelled
                    ? $"Demand {id} cancelled: {label} was cancelled on the contract."
                    : $"Demand {id} now {updated.Quantity} needed {updated.NeedDate:yyyy-MM-dd}, as {label} says."),
                [pipeline.Event(new DemandChanged(existing.DemandId, existing.PartNumber, updated.Status == DemandStatus.Open ? "Changed" : "Cancelled"), context, SourceContext, Entity, id, program)],
                new AuditChange(Entity, id, CanonicalJson.Serialize(existing), CanonicalJson.Serialize(updated), program));
        }, ct);
    }

    public Task<CommandResult> ChangeAsync(ChangeDemand c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return UpdateAsync(nameof(ChangeDemand), c.DemandId, c, context, demand =>
        {
            Validate(c.Quantity, c.Priority);
            return demand with { Quantity = c.Quantity, NeedDate = c.NeedDate, Priority = c.Priority };
        }, ct);
    }

    public Task<CommandResult> CloseAsync(CloseDemand c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return UpdateAsync(c.Cancel ? "CancelDemand" : nameof(CloseDemand), c.DemandId, c, context, demand => demand with { Status = c.Cancel ? DemandStatus.Cancelled : DemandStatus.Closed }, ct);
    }

    private Task<CommandResult> UpdateAsync<TCommand>(string commandType, long demandId, TCommand command, CommandContext context, Func<DemandInfo, DemandInfo> change, CancellationToken ct)
    {
        var id = demandId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, commandType, Permissions.DemandManage, ResourceRef.Of(Entity, id), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var store = uow.Store<IDemandStore>();
            var demand = await store.FindAsync(demandId, ct);
            if (demand is null || (demand.ProgramId is { } p && !context.Principal.Programs.Contains(new ProgramId(p))))
            {
                throw new CommandRejectedException("demand_not_found", $"Demand {id} does not exist.");
            }

            if (demand.Status != DemandStatus.Open)
            {
                throw new CommandRejectedException("demand_closed", $"Demand {id} is {demand.Status}.");
            }

            var updated = change(demand);
            if (updated == demand)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(id, "Nothing changed."));
            }

            await store.UpdateAsync(updated, now, context.Actor, ct);
            var program = demand.ProgramId is { } pid ? new ProgramId(pid) : (ProgramId?)null;
            return new CommandOutcome(
                CommandResult.Ok(id, updated.Status == DemandStatus.Open
                    ? $"Demand {id}: {updated.Quantity} {updated.PartNumber} needed {updated.NeedDate:yyyy-MM-dd}."
                    : $"Demand {id} {updated.Status.ToString().ToLowerInvariant()}."),
                [pipeline.Event(new DemandChanged(demandId, demand.PartNumber, updated.Status == DemandStatus.Open ? "Changed" : updated.Status.ToString()), context, SourceContext, Entity, id, program)],
                new AuditChange(Entity, id, CanonicalJson.Serialize(demand), CanonicalJson.Serialize(updated), program));
        }, ct);
    }

    private static async Task RequireProgramAsync(IKernelUnitOfWork uow, Guid programId, CommandContext context, CancellationToken ct)
    {
        var program = await uow.Store<IProgramsLookup>().FindProgramAsync(programId, ct);
        if (program is null || !context.Principal.Programs.Contains(new ProgramId(programId)))
        {
            throw new CommandRejectedException("program_not_found", $"Program {programId} does not exist.");
        }

        if (program.Status != ProgramStatus.Active)
        {
            throw new CommandRejectedException("program_closed", $"Program {program.Code} is closed.");
        }
    }

    private static void Validate(decimal quantity, int priority)
    {
        if (quantity <= 0 || quantity > 100_000_000 || priority is < 1 or > 9)
        {
            throw new CommandRejectedException("invalid_demand", "Quantity must be above zero and priority between 1 (highest) and 9.");
        }
    }

    private static string? Trim(string? value, int max)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > max ? text[..max] : text;
    }
}
