using Forge.Engineering.Contracts;
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
    DateTimeOffset CreatedAt);

public sealed record RecordDemand(string PartNumber, decimal Quantity, DateOnly NeedDate, int Priority, DemandKind Kind, string? Customer, string? Reference, Guid? ProgramId);

public sealed record ChangeDemand(long DemandId, decimal Quantity, DateOnly NeedDate, int Priority);

public sealed record CloseDemand(long DemandId, bool Cancel);

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

            var demand = new DemandInfo(0, part, c.Quantity, c.NeedDate, c.Priority, c.Kind, Trim(c.Customer, 200), Trim(c.Reference, 100), c.ProgramId, DemandStatus.Open, context.Actor, now);
            var demandId = await uow.Store<IDemandStore>().InsertAsync(demand, ct);
            var id = demandId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new CommandOutcome(
                CommandResult.Ok(id, $"Demand {id}: {c.Quantity} {part} needed {c.NeedDate:yyyy-MM-dd} ({c.Kind})."),
                [pipeline.Event(new DemandChanged(demandId, part, "Recorded"), context, SourceContext, Entity, id, program)],
                new AuditChange(Entity, id, null, CanonicalJson.Serialize(demand with { DemandId = demandId }), program));
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
