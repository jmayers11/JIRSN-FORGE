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

/// <summary>A PM plan due every <see cref="IntervalDays"/> days and/or every <see cref="IntervalRunHours"/> machine run hours, whichever comes first.</summary>
public sealed record DefinePmPlan(string MachineCode, string Title, int? IntervalDays, DateOnly? FirstDue, int DurationMinutes, IReadOnlyList<string> Tasks, bool LockOutWhenOverdue, int GraceDays, decimal? IntervalRunHours = null, decimal GraceRunHours = 0);

public sealed record ChangePmPlan(long PlanId, string Title, int? IntervalDays, int DurationMinutes, IReadOnlyList<string> Tasks, bool LockOutWhenOverdue, int GraceDays, decimal? IntervalRunHours = null, decimal GraceRunHours = 0);

public sealed record RetirePmPlan(long PlanId);

public sealed record RaiseWorkOrder(string MachineCode, string Title, string? Description, DateOnly DueDate);

public sealed record ScheduleWorkOrder(long WorkOrderId, DateTimeOffset StartsAt, DateTimeOffset EndsAt);

public sealed record StartWorkOrder(long WorkOrderId);

public sealed record CompleteWorkOrder(long WorkOrderId, string Findings, IReadOnlyList<bool> TasksDone);

public sealed record SignOffWorkOrder(long WorkOrderId, bool Accept);

public sealed record CancelWorkOrder(long WorkOrderId);

public sealed record WorkOrderChanged(long WorkOrderId, string MachineCode, string Kind, string Status) : IDomainEvent
{
    public static string EventType => nameof(WorkOrderChanged);

    public static int SchemaVersion => 1;
}

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

    Task<long> InsertPlanAsync(PmPlanInfo plan, DateTimeOffset at, string actor, CancellationToken ct);

    Task UpdatePlanAsync(PmPlanInfo plan, CancellationToken ct);

    Task<PmPlanInfo?> FindPlanAsync(long planId, CancellationToken ct);

    Task<long> InsertWorkOrderAsync(WorkOrderInfo order, CancellationToken ct);

    Task UpdateWorkOrderAsync(WorkOrderInfo order, CancellationToken ct);

    Task<WorkOrderInfo?> FindOpenWorkOrderForOutageAsync(long downtimeId, CancellationToken ct);

    Task SetPlannedWindowAsync(long downtimeId, DateTimeOffset start, DateTimeOffset end, CancellationToken ct);

    Task RemovePlannedWindowAsync(long downtimeId, CancellationToken ct);
}

/// <summary>
/// Machine maintenance state as it affects capacity: unplanned outages (open until a person
/// restores the machine) and planned maintenance windows. A machine counts as down only while a
/// recorded window covers the instant; Forge never infers an outage.
/// </summary>
public sealed partial class MaintenanceService(CommandPipeline pipeline)
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

            // Every breakdown gets a corrective work order; the machine returns to service when it is signed off.
            var order = new WorkOrderInfo(0, machine, WorkOrderKind.Corrective, null, $"Repair: {Shorten(description, 80)}", description, [], DateOnly.FromDateTime(now.UtcDateTime),
                WorkOrderStatus.Open, null, null, id, context.Actor, now, null, null, null, null, null, null, null, null);
            var orderId = await store.InsertWorkOrderAsync(order, ct);
            return new CommandOutcome(
                CommandResult.Ok(machine, $"{machine} down since {now:u}. Corrective work order {orderId} raised."),
                [pipeline.Event(new MachineDown(machine, id, description), context, SourceContext, MachineEntity, machine),
                 pipeline.Event(new WorkOrderChanged(orderId, machine, nameof(WorkOrderKind.Corrective), nameof(WorkOrderStatus.Open)), context, SourceContext, WorkOrderEntity, Key(orderId))],
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

            // A repair under way is returned to service by signing off its work order, not by a restore.
            var repair = await store.FindOpenWorkOrderForOutageAsync(open.Id, ct);
            if (repair is { Status: not WorkOrderStatus.Open })
            {
                throw new CommandRejectedException("repair_in_progress", $"Corrective work order {repair.WorkOrderId} is {repair.Status}. {machine} returns to service when that work is signed off.");
            }

            if (repair is not null)
            {
                await store.UpdateWorkOrderAsync(repair with { Status = WorkOrderStatus.Cancelled, CancelReason = $"Machine restored by {context.Actor} without repair work." }, ct);
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
