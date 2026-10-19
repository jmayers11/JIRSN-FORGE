using System.Globalization;
using Forge.Competency.Contracts;
using Forge.Engineering.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Maintenance.Contracts;
using Forge.Resources.Contracts;
using NodaTime;

namespace Forge.Maintenance;

/// <summary>
/// Preventive maintenance plans and maintenance work orders (spec §47). A plan keeps exactly one open
/// preventive work order; signing it off raises the next one, an interval after the work was done. Work
/// occupies a planned downtime window, so capacity, readiness and the schedule see it. A technician does
/// the work and confirms every checklist task; a different person with sign-off authority closes it. A
/// breakdown raises a corrective work order, and the machine returns to service when it is signed off.
/// </summary>
public sealed partial class MaintenanceService
{
    private const string WorkOrderEntity = "WorkOrder";
    private const string PlanEntity = "PmPlan";

    public Task<CommandResult> DefinePmPlanAsync(DefinePmPlan c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var machine = BusinessCode.Require(c.MachineCode, "Machine");
        var spec = new CommandSpec(SourceContext, nameof(DefinePmPlan), Permissions.MaintenanceProgram, ResourceRef.Of(PlanEntity));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            await RequireMachineAsync(uow.Store<IResourcesLookup>(), machine, ct);
            var plan = Plan(0, machine, c.Title, c.IntervalDays, c.DurationMinutes, c.Tasks, c.LockOutWhenOverdue, c.GraceDays, true, c.IntervalRunHours, c.GraceRunHours);
            if (plan.IntervalDays is not null && c.FirstDue is null)
            {
                throw new CommandRejectedException("first_due_required", "A plan with a calendar interval needs the date the first one is due.");
            }

            var store = uow.Store<IMaintenanceStore>();
            var planId = await store.InsertPlanAsync(plan, now, context.Actor, ct);
            var first = Preventive(plan with { PlanId = planId }, plan.IntervalDays is null ? null : c.FirstDue, plan.IntervalRunHours, now, context.Actor, now)
                with { AssetCode = await AssetOfMachineAsync(uow, machine, ct) };
            var orderId = await store.InsertWorkOrderAsync(first, ct);
            var id = Key(planId);
            return new CommandOutcome(
                CommandResult.Ok(id, $"PM plan {id} for {machine}: {Every(plan)}. First work order {orderId} {Due(first)}."),
                [pipeline.Event(new WorkOrderChanged(orderId, machine, nameof(WorkOrderKind.Preventive), nameof(WorkOrderStatus.Open)), context, SourceContext, WorkOrderEntity, Key(orderId))],
                new AuditChange(PlanEntity, id, null, CanonicalJson.Serialize(plan with { PlanId = planId })));
        }, ct);
    }

    public Task<CommandResult> ChangePmPlanAsync(ChangePmPlan c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var id = Key(c.PlanId);
        var spec = new CommandSpec(SourceContext, nameof(ChangePmPlan), Permissions.MaintenanceProgram, ResourceRef.Of(PlanEntity, id), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, _) =>
        {
            var store = uow.Store<IMaintenanceStore>();
            var plan = await store.FindPlanAsync(c.PlanId, ct) ?? throw new CommandRejectedException("plan_not_found", $"PM plan {id} does not exist.");
            var next = Plan(plan.PlanId, plan.MachineCode, c.Title, c.IntervalDays, c.DurationMinutes, c.Tasks, c.LockOutWhenOverdue, c.GraceDays, plan.Active, c.IntervalRunHours, c.GraceRunHours);
            await store.UpdatePlanAsync(next, ct);

            // Work not yet started follows the new checklist; work under way keeps the one it started with.
            if (await OpenOrderForPlanAsync(uow, plan.PlanId, ct) is { Status: WorkOrderStatus.Open or WorkOrderStatus.Scheduled } open)
            {
                await store.UpdateWorkOrderAsync(open with { Title = next.Title, Tasks = next.Tasks }, ct);
            }

            return new CommandOutcome(
                CommandResult.Ok(id, $"PM plan {id} changed."),
                [],
                new AuditChange(PlanEntity, id, CanonicalJson.Serialize(plan), CanonicalJson.Serialize(next)));
        }, ct);
    }

    public Task<CommandResult> RetirePmPlanAsync(RetirePmPlan c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var id = Key(c.PlanId);
        var spec = new CommandSpec(SourceContext, nameof(RetirePmPlan), Permissions.MaintenanceProgram, ResourceRef.Of(PlanEntity, id), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var store = uow.Store<IMaintenanceStore>();
            var plan = await store.FindPlanAsync(c.PlanId, ct) ?? throw new CommandRejectedException("plan_not_found", $"PM plan {id} does not exist.");
            if (!plan.Active)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(id, "Already retired."));
            }

            if (await OpenOrderForPlanAsync(uow, plan.PlanId, ct) is { } open)
            {
                if (open.Status is WorkOrderStatus.InProgress or WorkOrderStatus.Completed)
                {
                    throw new CommandRejectedException("work_in_progress", $"Work order {open.WorkOrderId} is {open.Status}; finish it before retiring the plan.");
                }

                await ReleaseWindowAsync(store, open, ct);
                await WithdrawPendingAsync(uow, open, context, now, $"PM plan retired: {context.Reason}", ct);
                await store.UpdateWorkOrderAsync(open with { Status = WorkOrderStatus.Cancelled, CancelReason = $"PM plan retired: {context.Reason}" }, ct);
            }

            await store.UpdatePlanAsync(plan with { Active = false }, ct);
            return new CommandOutcome(CommandResult.Ok(id, $"PM plan {id} retired."), [], new AuditChange(PlanEntity, id, "Active", "Retired"));
        }, ct);
    }

    public Task<CommandResult> RaiseWorkOrderAsync(RaiseWorkOrder c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(SourceContext, nameof(RaiseWorkOrder), Permissions.DowntimeReport, ResourceRef.Of(WorkOrderEntity));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            if (c.Kind == WorkOrderKind.Preventive)
            {
                throw new CommandRejectedException("preventive_from_plan", "Preventive work orders come from a PM plan; raise a different type of work.");
            }

            var (machine, asset) = await TargetAsync(uow, c.MachineCode, c.AssetCode, ct);
            var order = new WorkOrderInfo(0, machine, c.Kind, null, BusinessCode.RequireName(c.Title, "Work order"), Shorten(c.Description, 2000), [], c.DueDate,
                WorkOrderStatus.Open, null, null, null, context.Actor, now, null, null, null, null, null, null, null, null, AssetCode: asset);
            var orderId = await uow.Store<IMaintenanceStore>().InsertWorkOrderAsync(order, ct);
            var what = c.Kind == WorkOrderKind.Emergency ? "Emergency work order" : $"{c.Kind} work order";
            return Outcome(context, order with { WorkOrderId = orderId }, null, $"{what} {orderId} raised for {order.Target}, due {c.DueDate:yyyy-MM-dd}."
                + (c.Kind == WorkOrderKind.Emergency && machine is not null ? $" Starting it takes {machine} out of production at once." : string.Empty));
        }, ct);
    }

    /// <summary>
    /// Records what a work order needs (V2.9): the asset, the procedure revision to follow, the qualifications
    /// whoever does it must hold, and the parts (with where each is installed). Changed only before work starts.
    /// </summary>
    public Task<CommandResult> SetRequirementsAsync(SetWorkOrderRequirements c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return WorkOrderAsync(nameof(SetWorkOrderRequirements), c.WorkOrderId, c, context, Permissions.MaintenancePlan, false, async (uow, order, _) =>
        {
            if (order.Status is not (WorkOrderStatus.Open or WorkOrderStatus.Scheduled))
            {
                throw new CommandRejectedException("work_started", $"Work order {order.WorkOrderId} is {order.Status}; what it needs is kept as it was when work started.");
            }

            string? asset = order.AssetCode;
            if (!string.IsNullOrWhiteSpace(c.AssetCode))
            {
                asset = (await TargetAsync(uow, order.MachineCode, c.AssetCode, ct)).Asset;
            }

            var (document, revision) = await ProcedureAsync(uow, c.ProcedureDocument, c.ProcedureRevision, ct);
            var qualifications = await QualificationsAsync(uow, c.Qualifications, ct);
            var parts = await PartsAsync(uow, order.WorkOrderId, c.Parts, ct);
            if (parts.Any(p => p.Position is not null) && asset is null)
            {
                throw new CommandRejectedException("position_needs_asset", "A position is a place on an asset; name the asset first.");
            }

            await uow.Store<IMaintenanceStore>().ReplaceRequiredPartsAsync(order.WorkOrderId, parts, ct);
            var next = order with { AssetCode = asset, ProcedureDocument = document, ProcedureRevision = revision, RequiredQualifications = qualifications };
            var summary = new List<string>();
            if (asset is not null)
            {
                summary.Add($"asset {asset}");
            }

            if (document is not null)
            {
                summary.Add($"procedure {document} rev {revision}");
            }

            if (qualifications.Count > 0)
            {
                summary.Add($"qualifications {string.Join(", ", qualifications)}");
            }

            summary.Add(parts.Count == 0 ? "no parts" : $"{parts.Count} part line(s)");
            return (next, $"needs {string.Join("; ", summary)}");
        }, ct);
    }

    /// <summary>
    /// Plans when the work is done. A repair of a machine that is already down is scheduled at once; any other
    /// work proposes an outage (V2.9), and the work is scheduled when a different person approves it.
    /// Whether the work is ready (parts, technician, procedure) is checked by the caller before this.
    /// </summary>
    public Task<CommandResult> ScheduleWorkOrderAsync(ScheduleWorkOrder c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var id = Key(c.WorkOrderId);
        var spec = new CommandSpec(SourceContext, nameof(ScheduleWorkOrder), Permissions.MaintenancePlan, ResourceRef.Of(WorkOrderEntity, id));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var order = await uow.Store<IMaintenanceLookup>().FindWorkOrderAsync(c.WorkOrderId, ct)
                ?? throw new CommandRejectedException("work_order_not_found", $"Work order {id} does not exist.");
            if (order.Status is not (WorkOrderStatus.Open or WorkOrderStatus.Scheduled))
            {
                throw new CommandRejectedException("not_schedulable", $"Work order {order.WorkOrderId} is {order.Status}.");
            }

            var (start, end) = Window(c.StartsAt, c.EndsAt, now);
            var store = uow.Store<IMaintenanceStore>();
            if (order.MachineCode is not { } machine)
            {
                // Work on a building or utility takes no production capacity.
                var planned = order with { Status = WorkOrderStatus.Scheduled, ScheduledStart = start, ScheduledEnd = end };
                await store.UpdateWorkOrderAsync(planned, ct);
                return Outcome(context, planned, order, $"Work order {id} scheduled {start:u} to {end:u}.");
            }

            if (await RepairsOutageAsync(store, order, ct))
            {
                // The machine is already down; the window only says when the repair is planned.
                var planned = order with { Status = WorkOrderStatus.Scheduled, ScheduledStart = start, ScheduledEnd = end };
                await store.UpdateWorkOrderAsync(planned, ct);
                return Outcome(context, planned, order, $"Work order {id} scheduled {start:u} to {end:u}; {machine} is already down.");
            }

            if ((await uow.Store<IMaintenanceLookup>().ListOutagesAsync(false, ct)).FirstOrDefault(o => o.WorkOrderId == order.WorkOrderId) is { } pending)
            {
                throw new CommandRejectedException("outage_pending", $"Outage {pending.OutageId} for work order {id} is waiting for approval; withdraw it to propose another window.");
            }

            return await ProposeAsync(uow, context, machine, start, end, $"Work order {order.WorkOrderId}: {order.Title}", order.WorkOrderId, now, ct);
        }, ct);
    }

    public Task<CommandResult> StartWorkOrderAsync(StartWorkOrder c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return WorkOrderAsync(nameof(StartWorkOrder), c.WorkOrderId, c, context, Permissions.MaintenanceWork, false, async (uow, order, now) =>
        {
            if (order.Status is not (WorkOrderStatus.Open or WorkOrderStatus.Scheduled))
            {
                throw new CommandRejectedException("not_startable", $"Work order {order.WorkOrderId} is {order.Status}.");
            }

            // Work on a machine takes it out of production from now until the planned end (or the plan's duration).
            var store = uow.Store<IMaintenanceStore>();
            var downtime = order.DowntimeId;
            if (order.MachineCode is null)
            {
                return (order with { Status = WorkOrderStatus.InProgress, StartedBy = context.Actor, StartedAt = now }, "started");
            }

            await WithdrawPendingAsync(uow, order, context, now, "Work started", ct);
            if (!await RepairsOutageAsync(store, order, ct))
            {
                var duration = order.PlanId is { } planId && await store.FindPlanAsync(planId, ct) is { } plan ? TimeSpan.FromMinutes(plan.DurationMinutes) : TimeSpan.FromHours(2);
                var end = order.ScheduledEnd is { } planned && planned > now + duration ? planned : now + duration;
                if (downtime is { } existing)
                {
                    await store.SetPlannedWindowAsync(existing, now, end, ct);
                }
                else
                {
                    downtime = await store.InsertDowntimeAsync(order.MachineCode, DowntimeKind.Planned, now, end, $"Work order {order.WorkOrderId}: {order.Title}", now, context.Actor, ct);
                }
            }

            return (order with { Status = WorkOrderStatus.InProgress, StartedBy = context.Actor, StartedAt = now, DowntimeId = downtime }, $"started; {order.MachineCode} is out of production");
        }, ct);
    }

    public Task<CommandResult> CompleteWorkOrderAsync(CompleteWorkOrder c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return WorkOrderAsync(nameof(CompleteWorkOrder), c.WorkOrderId, c, context, Permissions.MaintenanceWork, false, async (uow, order, now) =>
        {
            if (order.Status != WorkOrderStatus.InProgress)
            {
                throw new CommandRejectedException("not_in_progress", $"Work order {order.WorkOrderId} is {order.Status}; start it before completing it.");
            }

            var done = c.TasksDone ?? [];
            if (done.Count != order.Tasks.Count || done.Any(d => !d))
            {
                var missing = order.Tasks.Where((_, i) => i >= done.Count || !done[i]).ToList();
                throw new CommandRejectedException("checklist_incomplete", $"Confirm every task before completing: {string.Join("; ", missing)}.");
            }

            var findings = BusinessCode.RequireName(c.Findings, "Findings");
            var store = uow.Store<IMaintenanceStore>();
            var repairsOutage = await RepairsOutageAsync(store, order, ct);
            if (!repairsOutage && order.DowntimeId is { } window && order.StartedAt is { } started)
            {
                // Preventive and planned work: the machine is back the moment the work is done.
                await store.SetPlannedWindowAsync(window, started, now > started ? now : started.AddTicks(1), ct);
            }

            return (order with { Status = WorkOrderStatus.Completed, CompletedBy = context.Actor, CompletedAt = now, Findings = findings },
                repairsOutage ? $"completed; {order.MachineCode} stays down until a different person signs the repair off" : "completed; waiting for sign-off by a different person");
        }, ct);
    }

    public Task<CommandResult> SignOffWorkOrderAsync(SignOffWorkOrder c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return WorkOrderAsync(nameof(SignOffWorkOrder), c.WorkOrderId, c, context, Permissions.MaintenanceApprove, !c.Accept, async (uow, order, now) =>
        {
            if (order.Status != WorkOrderStatus.Completed)
            {
                throw new CommandRejectedException("not_completed", $"Work order {order.WorkOrderId} is {order.Status}; it is signed off after it is completed.");
            }

            if (order.CompletedBy == context.Actor || order.StartedBy == context.Actor)
            {
                throw new CommandRejectedException("separation_of_duties", "You worked on this order; a different person must sign it off.");
            }

            if (!c.Accept)
            {
                return (order with { Status = WorkOrderStatus.InProgress, CompletedBy = null, CompletedAt = null }, "sent back to the technician");
            }

            var store = uow.Store<IMaintenanceStore>();
            var message = "signed off";
            if (await RepairsOutageAsync(store, order, ct))
            {
                await store.CloseOutageAsync(order.DowntimeId!.Value, now, context.Actor, ct);
                message = $"signed off; {order.MachineCode} is back in service";
            }

            if (order.PlanId is { } planId && await store.FindPlanAsync(planId, ct) is { Active: true } plan)
            {
                // The next one counts from when the work was done: calendar days and machine run hours alike.
                var doneAt = order.CompletedAt ?? now;
                var doneOn = await LocalDateAsync(uow, doneAt, ct);
                var next = Preventive(plan, plan.IntervalDays is { } days ? doneOn.AddDays(days) : null, plan.IntervalRunHours, doneAt, context.Actor, now);
                await store.UpdateWorkOrderAsync(order with { Status = WorkOrderStatus.Closed, ClosedBy = context.Actor, ClosedAt = now }, ct);
                var nextId = await InsertNextAsync(uow, order, next, ct);
                message += $"; next PM is work order {nextId}, {Due(next)}";
            }

            return (order with { Status = WorkOrderStatus.Closed, ClosedBy = context.Actor, ClosedAt = now }, message);
        }, ct);
    }

    public Task<CommandResult> CancelWorkOrderAsync(CancelWorkOrder c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return WorkOrderAsync(nameof(CancelWorkOrder), c.WorkOrderId, c, context, Permissions.MaintenancePlan, true, async (uow, order, now) =>
        {
            if (order.Status is not (WorkOrderStatus.Open or WorkOrderStatus.Scheduled))
            {
                throw new CommandRejectedException("not_cancellable", $"Work order {order.WorkOrderId} is {order.Status}.");
            }

            var store = uow.Store<IMaintenanceStore>();
            if (await RepairsOutageAsync(store, order, ct))
            {
                throw new CommandRejectedException("machine_down", $"{order.MachineCode} is down; repair it, or restore it if it needed no repair.");
            }

            await ReleaseWindowAsync(store, order, ct);
            await WithdrawPendingAsync(uow, order, context, now, $"Work order cancelled: {context.Reason}", ct);
            var message = "cancelled";
            if (order.PlanId is { } planId && await store.FindPlanAsync(planId, ct) is { Active: true } plan)
            {
                // A skipped PM is due again one interval after the one skipped.
                await store.UpdateWorkOrderAsync(order with { Status = WorkOrderStatus.Cancelled, CancelReason = context.Reason, DowntimeId = null }, ct);
                // A skipped PM's next one is one interval further on, by date and by run hours from the same start.
                var today = await LocalDateAsync(uow, now, ct);
                var nextDue = plan.IntervalDays is { } days ? (order.DueDate ?? today).AddDays(days) : (DateOnly?)null;
                var next = plan.IntervalRunHours is { } hours
                    ? Preventive(plan, nextDue, (order.DueRunHours ?? 0) + hours, order.RunHoursFrom ?? now, context.Actor, now)
                    : Preventive(plan, nextDue, null, now, context.Actor, now);
                var nextId = await InsertNextAsync(uow, order, next, ct);
                message += $"; next PM is work order {nextId}, {Due(next)}";
            }

            return (order with { Status = WorkOrderStatus.Cancelled, CancelReason = context.Reason, DowntimeId = null }, message);
        }, ct);
    }

    // ------------------------------------------------------------------ helpers

    private Task<CommandResult> WorkOrderAsync<TCommand>(
        string commandType,
        long workOrderId,
        TCommand command,
        CommandContext context,
        string permission,
        bool reasonRequired,
        Func<IKernelUnitOfWork, WorkOrderInfo, DateTimeOffset, Task<(WorkOrderInfo Next, string Message)>> change,
        CancellationToken ct)
    {
        var id = Key(workOrderId);
        var spec = new CommandSpec(SourceContext, commandType, permission, ResourceRef.Of(WorkOrderEntity, id), reasonRequired);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var order = await uow.Store<IMaintenanceLookup>().FindWorkOrderAsync(workOrderId, ct)
                ?? throw new CommandRejectedException("work_order_not_found", $"Work order {id} does not exist.");
            var (next, message) = await change(uow, order, now);
            await uow.Store<IMaintenanceStore>().UpdateWorkOrderAsync(next, ct);
            return Outcome(context, next, order, $"Work order {id} {message}.");
        }, ct);
    }

    private CommandOutcome Outcome(CommandContext context, WorkOrderInfo order, WorkOrderInfo? before, string message)
    {
        var id = Key(order.WorkOrderId);
        return new CommandOutcome(
            CommandResult.Ok(id, message),
            [pipeline.Event(new WorkOrderChanged(order.WorkOrderId, order.Target, order.Kind.ToString(), order.Status.ToString()), context, SourceContext, WorkOrderEntity, id)],
            new AuditChange(WorkOrderEntity, id, before is null ? null : CanonicalJson.Serialize(before), CanonicalJson.Serialize(order)));
    }

    /// <summary>A corrective order raised by a breakdown repairs an outage that is still open.</summary>
    private static async Task<bool> RepairsOutageAsync(IMaintenanceStore store, WorkOrderInfo order, CancellationToken ct) =>
        order.Kind == WorkOrderKind.Corrective && order.DowntimeId is { } id && order.MachineCode is { } machine && await store.FindOpenOutageAsync(machine, ct) is { } open && open.Id == id;

    private static async Task ReleaseWindowAsync(IMaintenanceStore store, WorkOrderInfo order, CancellationToken ct)
    {
        if (order.DowntimeId is { } window && !await RepairsOutageAsync(store, order, ct))
        {
            await store.RemovePlannedWindowAsync(window, ct);
        }
    }

    private static async Task<WorkOrderInfo?> OpenOrderForPlanAsync(IKernelUnitOfWork uow, long planId, CancellationToken ct) =>
        (await uow.Store<IMaintenanceLookup>().ListWorkOrdersAsync(false, ct)).FirstOrDefault(o => o.PlanId == planId && o.IsOpen);

    private static async Task<DateOnly> LocalDateAsync(IKernelUnitOfWork uow, DateTimeOffset at, CancellationToken ct)
    {
        // NodaTime's own time-zone database, so IANA zone names work the same on every operating system.
        var zone = SiteTime.Zone(await uow.SiteTimeZoneAsync(ct));
        var date = Instant.FromDateTimeOffset(at).InZone(zone).Date;
        return new DateOnly(date.Year, date.Month, date.Day);
    }

    private static PmPlanInfo Plan(long planId, string machine, string title, int? interval, int duration, IReadOnlyList<string>? tasks, bool lockOut, int grace, bool active, decimal? runHours, decimal graceHours)
    {
        var checklist = (tasks ?? []).Select(t => t?.Trim() ?? string.Empty).Where(t => t.Length > 0).Select(t => t.Length > 300 ? t[..300] : t).ToList();
        if (interval is null && runHours is null)
        {
            throw new CommandRejectedException("interval_required", "Give a calendar interval in days, a run-hour interval, or both.");
        }

        if (interval is < 1 or > 3650 || runHours is < 1 or > 100000 || duration is < 1 or > 10080 || grace is < 0 or > 90 || graceHours is < 0 or > 10000)
        {
            throw new CommandRejectedException("invalid_plan", "Interval must be 1 to 3650 days and/or 1 to 100,000 run hours, duration 1 minute to 7 days, and grace 0 to 90 days (0 to 10,000 run hours).");
        }

        if (checklist.Count is 0 or > 50)
        {
            throw new CommandRejectedException("tasks_required", "A PM plan has 1 to 50 checklist tasks.");
        }

        return new PmPlanInfo(planId, machine, BusinessCode.RequireName(title, "PM plan"), interval, duration, checklist, lockOut, grace, active, runHours, graceHours);
    }

    private static WorkOrderInfo Preventive(PmPlanInfo plan, DateOnly? due, decimal? dueRunHours, DateTimeOffset runHoursFrom, string actor, DateTimeOffset now) =>
        new(0, plan.MachineCode, WorkOrderKind.Preventive, plan.PlanId, plan.Title, null, plan.Tasks, due, WorkOrderStatus.Open,
            null, null, null, actor, now, null, null, null, null, null, null, null, null,
            dueRunHours, dueRunHours is null ? null : runHoursFrom);

    private static string Every(PmPlanInfo plan) => string.Join(" or ", new[]
    {
        plan.IntervalDays is { } days ? $"every {days} day(s)" : null,
        plan.IntervalRunHours is { } hours ? $"every {hours:0.##} run hours" : null,
    }.Where(x => x is not null)) + (plan.IntervalDays is not null && plan.IntervalRunHours is not null ? ", whichever comes first" : string.Empty);

    private static string Due(WorkOrderInfo order) => "due " + string.Join(" or ", new[]
    {
        order.DueDate is { } date ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
        order.DueRunHours is { } hours ? $"after {hours:0.##} run hours" : null,
    }.Where(x => x is not null));

    // ------------------------------------------------------------------ V2.9 helpers

    /// <summary>The machine and asset a work order is on: either may be given; they must agree.</summary>
    private static async Task<(string? Machine, string? Asset)> TargetAsync(IKernelUnitOfWork uow, string? rawMachine, string? rawAsset, CancellationToken ct)
    {
        string? machine = string.IsNullOrWhiteSpace(rawMachine) ? null : BusinessCode.Require(rawMachine, "Machine");
        if (machine is not null)
        {
            await RequireMachineAsync(uow.Store<IResourcesLookup>(), machine, ct);
        }

        if (string.IsNullOrWhiteSpace(rawAsset))
        {
            return machine is null
                ? throw new CommandRejectedException("target_required", "Name the machine or the asset the work is on.")
                : (machine, await AssetOfMachineAsync(uow, machine, ct));
        }

        var code = BusinessCode.Require(rawAsset, "Asset");
        var asset = await uow.Store<IAssetLookup>().FindAssetAsync(code, ct) ?? throw new CommandRejectedException("asset_not_found", $"Asset {code} does not exist.");
        if (asset.Status == AssetStatus.Decommissioned)
        {
            throw new CommandRejectedException("asset_decommissioned", $"Asset {code} is decommissioned.");
        }

        if (machine is not null && asset.MachineCode != machine)
        {
            throw new CommandRejectedException("asset_machine_mismatch", $"Asset {code} is {(asset.MachineCode is null ? "not a machine" : "machine " + asset.MachineCode)}, not {machine}.");
        }

        return (asset.MachineCode, code);
    }

    private static async Task<string?> AssetOfMachineAsync(IKernelUnitOfWork uow, string machine, CancellationToken ct) =>
        (await uow.Store<IAssetLookup>().ListAssetsAsync(ct)).FirstOrDefault(a => a.MachineCode == machine)?.AssetCode;

    private static async Task<(string? Document, string? Revision)> ProcedureAsync(IKernelUnitOfWork uow, string? rawDocument, string? rawRevision, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawDocument))
        {
            return (null, null);
        }

        var number = rawDocument.Trim().ToUpperInvariant();
        var document = await uow.Store<IDocumentLookup>().FindDocumentAsync(number, ct) ?? throw new CommandRejectedException("document_not_found", $"Document {number} does not exist.");
        if (document.Type is not (DocumentType.Procedure or DocumentType.WorkInstruction or DocumentType.Specification or DocumentType.Standard) || document.Authority == DocumentAuthority.Reference)
        {
            throw new CommandRejectedException("not_a_procedure", $"{number} is a {document.Authority} {document.Type}; a work order follows a controlled procedure, work instruction, specification or standard.");
        }

        var revision = string.IsNullOrWhiteSpace(rawRevision) ? document.Current?.Revision : rawRevision.Trim().ToUpperInvariant();
        if (revision is null || document.Revisions.All(r => r.Revision != revision))
        {
            throw new CommandRejectedException("revision_not_found", revision is null ? $"{number} has no released revision; name the revision." : $"{number} has no revision {revision}.");
        }

        return (number, revision);
    }

    private static async Task<IReadOnlyList<string>> QualificationsAsync(IKernelUnitOfWork uow, IReadOnlyList<string>? raw, CancellationToken ct)
    {
        var codes = (raw ?? []).Where(q => !string.IsNullOrWhiteSpace(q)).Select(q => q.Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (codes.Count > 20)
        {
            throw new CommandRejectedException("too_many_qualifications", "At most 20 qualifications.");
        }

        var competency = uow.Store<ICompetencyLookup>();
        foreach (var code in codes)
        {
            if (!await competency.QualificationTypeExistsAsync(code, ct))
            {
                throw new CommandRejectedException("unknown_qualification", $"Qualification {code} does not exist.");
            }
        }

        return codes;
    }

    private static async Task<IReadOnlyList<WorkOrderPartRequirement>> PartsAsync(IKernelUnitOfWork uow, long workOrderId, IReadOnlyList<RequiredPart>? raw, CancellationToken ct)
    {
        var list = new List<WorkOrderPartRequirement>();
        var engineering = uow.Store<IEngineeringLookup>();
        foreach (var part in raw ?? [])
        {
            var number = BusinessCode.Require(part.PartNumber, "Part");
            if (await engineering.FindItemAsync(number, ct) is null)
            {
                throw new CommandRejectedException("unknown_part", $"Part {number} does not exist.");
            }

            if (part.Quantity <= 0 || part.Quantity > 100_000)
            {
                throw new CommandRejectedException("invalid_quantity", $"{number}: quantity must be above zero.");
            }

            if (list.Any(l => l.PartNumber == number))
            {
                throw new CommandRejectedException("duplicate_part", $"{number} is listed twice; give one line with the total.");
            }

            var position = string.IsNullOrWhiteSpace(part.Position) ? null : part.Position.Trim();
            list.Add(new WorkOrderPartRequirement(workOrderId, list.Count + 1, number, part.Quantity, position is { Length: > 100 } ? position[..100] : position));
        }

        return list.Count > 100 ? throw new CommandRejectedException("too_many_parts", "At most 100 part lines.") : list;
    }

    /// <summary>The next PM keeps what this one needed: asset, procedure, qualifications and parts.</summary>
    private static async Task<long> InsertNextAsync(IKernelUnitOfWork uow, WorkOrderInfo done, WorkOrderInfo next, CancellationToken ct)
    {
        var store = uow.Store<IMaintenanceStore>();
        var id = await store.InsertWorkOrderAsync(next with { AssetCode = done.AssetCode, ProcedureDocument = done.ProcedureDocument, ProcedureRevision = done.ProcedureRevision, RequiredQualifications = done.Qualifications }, ct);
        var parts = await uow.Store<IMaintenanceLookup>().ListRequiredPartsAsync(done.WorkOrderId, ct);
        if (parts.Count > 0)
        {
            await store.ReplaceRequiredPartsAsync(id, parts.Select(p => p with { WorkOrderId = id }).ToList(), ct);
        }

        return id;
    }

    private static async Task WithdrawPendingAsync(IKernelUnitOfWork uow, WorkOrderInfo order, CommandContext context, DateTimeOffset now, string reason, CancellationToken ct)
    {
        if ((await uow.Store<IMaintenanceLookup>().ListOutagesAsync(false, ct)).FirstOrDefault(o => o.WorkOrderId == order.WorkOrderId) is { } pending)
        {
            await uow.Store<IMaintenanceStore>().DecideOutageAsync(pending with { Status = OutageStatus.Withdrawn, DecidedBy = context.Actor, DecidedAt = now, DecisionReason = reason }, ct);
        }
    }

    private static string Key(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string? Shorten(string? value, int max)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > max ? text[..max] : text;
    }
}
