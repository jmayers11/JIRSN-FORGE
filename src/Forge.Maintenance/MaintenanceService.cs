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

/// <param name="MachineCode">The machine; may be empty when <paramref name="AssetCode"/> names an asset that is not a machine.</param>
public sealed record RaiseWorkOrder(string? MachineCode, string Title, string? Description, DateOnly DueDate, WorkOrderKind Kind = WorkOrderKind.Corrective, string? AssetCode = null);

/// <summary>A part a work order needs, and where it is installed (V2.9).</summary>
public sealed record RequiredPart(string PartNumber, decimal Quantity, string? Position);

/// <summary>What a work order needs before it can be done (V2.9): its asset, a procedure revision, qualifications and parts.</summary>
public sealed record SetWorkOrderRequirements(long WorkOrderId, string? AssetCode, string? ProcedureDocument, string? ProcedureRevision, IReadOnlyList<string>? Qualifications, IReadOnlyList<RequiredPart>? Parts);

/// <summary>Approve or reject a proposed outage. <paramref name="Impact"/> is the schedule preview the approver saw (required to approve).</summary>
/// <summary>A machine a facility or capital project needs stopped (V2.10), proposed like any outage.</summary>
public sealed record ProposeProjectOutage(string ProjectCode, string MachineCode, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Description);

public sealed record DecideOutage(long OutageId, bool Approve, string? Impact);

public sealed record WithdrawOutage(long OutageId);

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

public sealed record OutageProposed(long OutageId, string MachineCode, DateTimeOffset StartsAt, DateTimeOffset EndsAt, long? WorkOrderId) : IDomainEvent
{
    public static string EventType => nameof(OutageProposed);

    public static int SchemaVersion => 1;
}

public sealed record OutageDecided(long OutageId, string MachineCode, string Status, long? DowntimeId, long? WorkOrderId) : IDomainEvent
{
    public static string EventType => nameof(OutageDecided);

    public static int SchemaVersion => 1;
}

public interface IMaintenanceStore
{
    Task ReplaceRequiredPartsAsync(long workOrderId, IReadOnlyList<WorkOrderPartRequirement> parts, CancellationToken ct);

    Task<long> InsertOutageAsync(OutageInfo outage, CancellationToken ct);

    Task DecideOutageAsync(OutageInfo outage, CancellationToken ct);

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
    private const string OutageEntity = "Outage";

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
                WorkOrderStatus.Open, null, null, id, context.Actor, now, null, null, null, null, null, null, null, null, AssetCode: await AssetOfMachineAsync(uow, machine, ct));
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

    /// <summary>
    /// Proposes a maintenance outage (V2.9). It is not capacity loss until a different person approves it,
    /// having seen what it does to the schedule.
    /// </summary>
    public Task<CommandResult> PlanAsync(PlanMaintenance c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var machine = BusinessCode.Require(c.MachineCode, "Machine");
        var spec = new CommandSpec(SourceContext, nameof(PlanMaintenance), Permissions.MaintenancePlan, ResourceRef.Of(MachineEntity, machine));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var description = BusinessCode.RequireName(c.Description, "Maintenance description");
            var (start, end) = Window(c.StartsAt, c.EndsAt, now);
            await RequireMachineAsync(uow.Store<IResourcesLookup>(), machine, ct);
            return await ProposeAsync(uow, context, machine, start, end, description, null, now, ct);
        }, ct);
    }

    /// <summary>
    /// Proposes an outage a facility or capital project needs (V2.10): the project must be approved, and the
    /// outage is decided like any other — by a different person, after the schedule preview.
    /// </summary>
    public Task<CommandResult> ProposeProjectOutageAsync(ProposeProjectOutage c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var machine = BusinessCode.Require(c.MachineCode, "Machine");
        var code = BusinessCode.Require(c.ProjectCode, "Project");
        var spec = new CommandSpec(SourceContext, nameof(ProposeProjectOutage), Permissions.ProjectsManage, ResourceRef.Of(MachineEntity, machine));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var project = await uow.Store<Forge.Projects.Contracts.IProjectLookup>().FindProjectAsync(code, ct)
                ?? throw new CommandRejectedException("project_not_found", $"Project {code} does not exist.");
            if (!project.IsOpen)
            {
                throw new CommandRejectedException("project_not_open", $"Project {code} is {project.Status}; outages are proposed for approved projects.");
            }

            var description = BusinessCode.RequireName(c.Description, "Outage description");
            var (start, end) = Window(c.StartsAt, c.EndsAt, now);
            await RequireMachineAsync(uow.Store<IResourcesLookup>(), machine, ct);
            return await ProposeCoreAsync(uow, context, machine, start, end, $"Project {code}: {description}", null, code, now, ct);
        }, ct);
    }

    private Task<CommandOutcome> ProposeAsync(Kernel.Access.IKernelUnitOfWork uow, CommandContext context, string machine, DateTimeOffset start, DateTimeOffset end, string reason, long? workOrderId, DateTimeOffset now, CancellationToken ct) =>
        ProposeCoreAsync(uow, context, machine, start, end, reason, workOrderId, null, now, ct);

    private async Task<CommandOutcome> ProposeCoreAsync(Kernel.Access.IKernelUnitOfWork uow, CommandContext context, string machine, DateTimeOffset start, DateTimeOffset end, string reason, long? workOrderId, string? projectCode, DateTimeOffset now, CancellationToken ct)
    {
        var outage = new OutageInfo(0, machine, start, end, reason, workOrderId, OutageStatus.Proposed, context.Actor, now, null, null, null, null, null, projectCode);
        var id = await uow.Store<IMaintenanceStore>().InsertOutageAsync(outage, ct);
        var key = Key(id);
        return new CommandOutcome(
            CommandResult.Ok(key, $"Outage {key} on {machine} proposed {start:u} to {end:u}{(workOrderId is { } w ? $" for work order {w}" : string.Empty)}. It takes no capacity until a different person approves it after seeing the schedule preview."),
            [pipeline.Event(new OutageProposed(id, machine, start, end, workOrderId), context, SourceContext, OutageEntity, key)],
            new AuditChange(OutageEntity, key, null, CanonicalJson.Serialize(outage with { OutageId = id })));
    }

    /// <summary>
    /// Approves (with the schedule preview seen) or rejects a proposed outage. The proposer never decides it.
    /// An approved outage becomes a planned downtime window; for a work order, the work is then scheduled.
    /// </summary>
    public Task<CommandResult> DecideOutageAsync(DecideOutage c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var key = Key(c.OutageId);
        var spec = new CommandSpec(SourceContext, nameof(DecideOutage), Permissions.OutageApprove, ResourceRef.Of(OutageEntity, key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var outage = await uow.Store<IMaintenanceLookup>().FindOutageAsync(c.OutageId, ct)
                ?? throw new CommandRejectedException("outage_not_found", $"Outage {key} does not exist.");
            if (outage.Status != OutageStatus.Proposed)
            {
                throw new CommandRejectedException("outage_decided", $"Outage {key} is {outage.Status}.");
            }

            if (outage.ProposedBy == context.Actor)
            {
                throw new CommandRejectedException("separation_of_duties", "You proposed this outage; a different person decides it.");
            }

            var store = uow.Store<IMaintenanceStore>();
            var events = new List<EventEnvelope>();
            OutageInfo decided;
            string message;
            if (!c.Approve)
            {
                decided = outage with { Status = OutageStatus.Rejected, DecidedBy = context.Actor, DecidedAt = now, DecisionReason = context.Reason };
                message = $"Outage {key} rejected; {outage.MachineCode} keeps its capacity.";
            }
            else
            {
                var impact = (c.Impact ?? string.Empty).Trim();
                if (impact.Length == 0)
                {
                    throw new CommandRejectedException("preview_required", "Approve an outage only after its schedule preview has been run.");
                }

                if (outage.EndsAt <= now)
                {
                    throw new CommandRejectedException("outage_past", $"Outage {key} ended {outage.EndsAt:u}; propose a new window.");
                }

                WorkOrderInfo? order = null;
                if (outage.WorkOrderId is { } w)
                {
                    order = await uow.Store<IMaintenanceLookup>().FindWorkOrderAsync(w, ct);
                    if (order is not { Status: WorkOrderStatus.Open or WorkOrderStatus.Scheduled })
                    {
                        throw new CommandRejectedException("work_order_not_open", $"Work order {w} is {order?.Status}; withdraw this outage.");
                    }
                }

                long downtime;
                if (order?.DowntimeId is { } existing)
                {
                    await store.SetPlannedWindowAsync(existing, outage.StartsAt, outage.EndsAt, ct);
                    downtime = existing;
                }
                else
                {
                    downtime = await store.InsertDowntimeAsync(outage.MachineCode, DowntimeKind.Planned, outage.StartsAt, outage.EndsAt, outage.Reason, now, context.Actor, ct);
                }

                decided = outage with { Status = OutageStatus.Approved, DecidedBy = context.Actor, DecidedAt = now, DecisionReason = context.Reason, Impact = impact.Length > 4000 ? impact[..4000] : impact, DowntimeId = downtime };
                message = $"Outage {key} approved; {outage.MachineCode} is unavailable to production {outage.StartsAt:u} to {outage.EndsAt:u}.";
                if (order is not null)
                {
                    var scheduled = order with { Status = WorkOrderStatus.Scheduled, ScheduledStart = outage.StartsAt, ScheduledEnd = outage.EndsAt, DowntimeId = downtime };
                    await store.UpdateWorkOrderAsync(scheduled, ct);
                    events.Add(pipeline.Event(new WorkOrderChanged(order.WorkOrderId, order.Target, order.Kind.ToString(), nameof(WorkOrderStatus.Scheduled)), context, SourceContext, WorkOrderEntity, Key(order.WorkOrderId)));
                    message += $" Work order {order.WorkOrderId} is scheduled.";
                }

                events.Add(pipeline.Event(new MaintenancePlanned(outage.MachineCode, downtime, outage.StartsAt, outage.EndsAt), context, SourceContext, MachineEntity, outage.MachineCode));
            }

            await store.DecideOutageAsync(decided, ct);
            events.Insert(0, pipeline.Event(new OutageDecided(outage.OutageId, outage.MachineCode, decided.Status.ToString(), decided.DowntimeId, outage.WorkOrderId), context, SourceContext, OutageEntity, key));
            return new CommandOutcome(CommandResult.Ok(key, message), events, new AuditChange(OutageEntity, key, CanonicalJson.Serialize(outage), CanonicalJson.Serialize(decided)));
        }, ct);
    }

    /// <summary>The proposer, or anyone who may approve outages, withdraws a proposal that is no longer wanted.</summary>
    public Task<CommandResult> WithdrawOutageAsync(WithdrawOutage c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var key = Key(c.OutageId);
        var spec = new CommandSpec(SourceContext, nameof(WithdrawOutage), Permissions.MaintenancePlan, ResourceRef.Of(OutageEntity, key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var outage = await uow.Store<IMaintenanceLookup>().FindOutageAsync(c.OutageId, ct)
                ?? throw new CommandRejectedException("outage_not_found", $"Outage {key} does not exist.");
            if (outage.Status != OutageStatus.Proposed)
            {
                throw new CommandRejectedException("outage_decided", $"Outage {key} is {outage.Status}.");
            }

            if (outage.ProposedBy != context.Actor && !context.Principal.HasPermission(Permissions.OutageApprove))
            {
                throw new CommandRejectedException("not_yours", "Only whoever proposed an outage, or someone who approves outages, withdraws it.");
            }

            var withdrawn = outage with { Status = OutageStatus.Withdrawn, DecidedBy = context.Actor, DecidedAt = now, DecisionReason = context.Reason };
            await uow.Store<IMaintenanceStore>().DecideOutageAsync(withdrawn, ct);
            return new CommandOutcome(
                CommandResult.Ok(key, $"Outage {key} withdrawn."),
                [pipeline.Event(new OutageDecided(outage.OutageId, outage.MachineCode, nameof(OutageStatus.Withdrawn), null, outage.WorkOrderId), context, SourceContext, OutageEntity, key)],
                new AuditChange(OutageEntity, key, nameof(OutageStatus.Proposed), nameof(OutageStatus.Withdrawn)));
        }, ct);
    }

    private static (DateTimeOffset Start, DateTimeOffset End) Window(DateTimeOffset startsAt, DateTimeOffset endsAt, DateTimeOffset now)
    {
        var start = startsAt.ToUniversalTime();
        var end = endsAt.ToUniversalTime();
        if (end <= start || end <= now || end - start > TimeSpan.FromDays(30))
        {
            throw new CommandRejectedException("invalid_window", "The window must end after it starts, end in the future, and last at most 30 days.");
        }

        return (start, end);
    }

    private static async Task RequireMachineAsync(IResourcesLookup resources, string machine, CancellationToken ct)
    {
        if (await resources.FindMachineAsync(machine, ct) is null)
        {
            throw new CommandRejectedException("unknown_machine", $"Machine {machine} does not exist.");
        }
    }
}
