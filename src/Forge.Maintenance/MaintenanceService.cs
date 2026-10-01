using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Maintenance.Contracts;
using Forge.Resources.Contracts;

namespace Forge.Maintenance;

public sealed record ReportMachineDown(string MachineCode, string Description);

public sealed record RestoreMachine(string MachineCode);

public sealed record PlanMaintenance(string MachineCode, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Description);

public sealed record MachineDown(string MachineCode, long DowntimeId, string Description) : IDomainEvent
{
    public static string EventType => nameof(MachineDown);

    public static int SchemaVersion => 1;
}

public sealed record MachineRestored(string MachineCode, long DowntimeId, DateTimeOffset DownSince) : IDomainEvent
{
    public static string EventType => nameof(MachineRestored);

    public static int SchemaVersion => 1;
}

public sealed record MaintenancePlanned(string MachineCode, long DowntimeId, DateTimeOffset StartsAt, DateTimeOffset EndsAt) : IDomainEvent
{
    public static string EventType => nameof(MaintenancePlanned);

    public static int SchemaVersion => 1;
}

public interface IMaintenanceStore
{
    Task<DowntimeWindow?> FindOpenOutageAsync(string machineCode, CancellationToken ct);

    Task<long> InsertDowntimeAsync(string machineCode, DowntimeKind kind, DateTimeOffset startsAt, DateTimeOffset? endsAt, string reason, DateTimeOffset at, string actor, CancellationToken ct);

    Task CloseOutageAsync(long id, DateTimeOffset endsAt, string actor, CancellationToken ct);
}

/// <summary>
/// Machine maintenance state as it affects capacity: unplanned outages (open until a person
/// restores the machine) and planned maintenance windows. A machine counts as down only while a
/// recorded window covers the instant; Forge never infers an outage.
/// </summary>
public sealed class MaintenanceService(CommandPipeline pipeline)
{
    public const string SourceContext = "Maintenance";
    private const string MachineEntity = "Machine";

    public Task<CommandResult> ReportDownAsync(ReportMachineDown c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var machine = BusinessCode.Require(c.MachineCode, "Machine");
        var spec = new CommandSpec(SourceContext, nameof(ReportMachineDown), Permissions.DowntimeReport, ResourceRef.Of(MachineEntity, machine));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var description = BusinessCode.RequireName(c.Description, "Downtime description");
            await RequireMachineAsync(uow.Store<IResourcesLookup>(), machine, ct);
            var store = uow.Store<IMaintenanceStore>();
            if (await store.FindOpenOutageAsync(machine, ct) is { } open)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(machine, $"{machine} is already down since {open.StartsAt:u}."));
            }

            var id = await store.InsertDowntimeAsync(machine, DowntimeKind.Unplanned, now, null, description, now, context.Actor, ct);
            return new CommandOutcome(
                CommandResult.Ok(machine, $"{machine} down since {now:u}."),
                [pipeline.Event(new MachineDown(machine, id, description), context, SourceContext, MachineEntity, machine)],
                new AuditChange(MachineEntity, machine, "Up", CanonicalJson.Serialize(new { status = "Down", description })));
        }, ct);
    }

    public Task<CommandResult> RestoreAsync(RestoreMachine c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var machine = BusinessCode.Require(c.MachineCode, "Machine");
        var spec = new CommandSpec(SourceContext, nameof(RestoreMachine), Permissions.DowntimeReport, ResourceRef.Of(MachineEntity, machine));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var store = uow.Store<IMaintenanceStore>();
            var open = await store.FindOpenOutageAsync(machine, ct);
            if (open is null)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(machine, $"{machine} has no open outage."));
            }

            await store.CloseOutageAsync(open.Id, now, context.Actor, ct);
            return new CommandOutcome(
                CommandResult.Ok(machine, $"{machine} restored after {(now - open.StartsAt).TotalHours:0.0} hours."),
                [pipeline.Event(new MachineRestored(machine, open.Id, open.StartsAt), context, SourceContext, MachineEntity, machine)],
                new AuditChange(MachineEntity, machine, "Down", "Up"));
        }, ct);
    }

    public Task<CommandResult> PlanAsync(PlanMaintenance c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var machine = BusinessCode.Require(c.MachineCode, "Machine");
        var spec = new CommandSpec(SourceContext, nameof(PlanMaintenance), Permissions.MaintenancePlan, ResourceRef.Of(MachineEntity, machine));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var description = BusinessCode.RequireName(c.Description, "Maintenance description");
            var start = c.StartsAt.ToUniversalTime();
            var end = c.EndsAt.ToUniversalTime();
            if (end <= start || end <= now || end - start > TimeSpan.FromDays(30))
            {
                throw new CommandRejectedException("invalid_window", "The window must end after it starts, end in the future, and last at most 30 days.");
            }

            await RequireMachineAsync(uow.Store<IResourcesLookup>(), machine, ct);
            var id = await uow.Store<IMaintenanceStore>().InsertDowntimeAsync(machine, DowntimeKind.Planned, start, end, description, now, context.Actor, ct);
            return new CommandOutcome(
                CommandResult.Ok(machine, $"Maintenance on {machine} planned {start:u} to {end:u}."),
                [pipeline.Event(new MaintenancePlanned(machine, id, start, end), context, SourceContext, MachineEntity, machine)],
                new AuditChange(MachineEntity, machine, null, CanonicalJson.Serialize(new { planned = description, start, end })));
        }, ct);
    }

    private static async Task RequireMachineAsync(IResourcesLookup resources, string machine, CancellationToken ct)
    {
        if (await resources.FindMachineAsync(machine, ct) is null)
        {
            throw new CommandRejectedException("unknown_machine", $"Machine {machine} does not exist.");
        }
    }
}
