using System.Globalization;
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
            var first = Preventive(plan with { PlanId = planId }, plan.IntervalDays is null ? null : c.FirstDue, plan.IntervalRunHours, now, context.Actor, now);
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
        return pipeline.ExecuteAsync(spec, c, context, async (uow, _) =>
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
                await store.UpdateWorkOrderAsync(open with { Status = WorkOrderStatus.Cancelled, CancelReason = $"PM plan retired: {context.Reason}" }, ct);
            }

            await store.UpdatePlanAsync(plan with { Active = false }, ct);
            return new CommandOutcome(CommandResult.Ok(id, $"PM plan {id} retired."), [], new AuditChange(PlanEntity, id, "Active", "Retired"));
        }, ct);
    }

    public Task<CommandResult> RaiseWorkOrderAsync(RaiseWorkOrder c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var machine = BusinessCode.Require(c.MachineCode, "Machine");
        var spec = new CommandSpec(SourceContext, nameof(RaiseWorkOrder), Permissions.DowntimeReport, ResourceRef.Of(WorkOrderEntity));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            await RequireMachineAsync(uow.Store<IResourcesLookup>(), machine, ct);
            var order = new WorkOrderInfo(0, machine, WorkOrderKind.Corrective, null, BusinessCode.RequireName(c.Title, "Work order"), Shorten(c.Description, 2000), [], c.DueDate,
                WorkOrderStatus.Open, null, null, null, context.Actor, now, null, null, null, null, null, null, null, null);
            var orderId = await uow.Store<IMaintenanceStore>().InsertWorkOrderAsync(order, ct);
            return Outcome(context, order with { WorkOrderId = orderId }, null, $"Corrective work order {orderId} raised for {machine}, due {c.DueDate:yyyy-MM-dd}.");
        }, ct);
    }

    public Task<CommandResult> ScheduleWorkOrderAsync(ScheduleWorkOrder c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return WorkOrderAsync(nameof(ScheduleWorkOrder), c.WorkOrderId, c, context, Permissions.MaintenancePlan, false, async (uow, order, now) =>
        {
            if (order.Status is not (WorkOrderStatus.Open or WorkOrderStatus.Scheduled))
            {
                throw new CommandRejectedException("not_schedulable", $"Work order {order.WorkOrderId} is {order.Status}.");
            }

            var start = c.StartsAt.ToUniversalTime();
            var end = c.EndsAt.ToUniversalTime();
            if (end <= start || end <= now || end - start > TimeSpan.FromDays(30))
            {
                throw new CommandRejectedException("invalid_window", "The window must end after it starts, end in the future, and last at most 30 days.");
            }

            var store = uow.Store<IMaintenanceStore>();
            long? downtime = order.DowntimeId;
            if (await RepairsOutageAsync(store, order, ct))
            {
                // The machine is already down; the window only says when the repair is planned.
            }
            else if (downtime is { } existing)
            {
                await store.SetPlannedWindowAsync(existing, start, end, ct);
            }
            else
            {
                downtime = await store.InsertDowntimeAsync(order.MachineCode, DowntimeKind.Planned, start, end, $"Work order {order.WorkOrderId}: {order.Title}", now, context.Actor, ct);
            }

            return (order with { Status = WorkOrderStatus.Scheduled, ScheduledStart = start, ScheduledEnd = end, DowntimeId = downtime },
                $"scheduled {start:u} to {end:u}; {order.MachineCode} is unavailable to production then");
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
                var nextId = await store.InsertWorkOrderAsync(next, ct);
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
                var nextId = await store.InsertWorkOrderAsync(next, ct);
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
            [pipeline.Event(new WorkOrderChanged(order.WorkOrderId, order.MachineCode, order.Kind.ToString(), order.Status.ToString()), context, SourceContext, WorkOrderEntity, id)],
            new AuditChange(WorkOrderEntity, id, before is null ? null : CanonicalJson.Serialize(before), CanonicalJson.Serialize(order)));
    }

    /// <summary>A corrective order raised by a breakdown repairs an outage that is still open.</summary>
    private static async Task<bool> RepairsOutageAsync(IMaintenanceStore store, WorkOrderInfo order, CancellationToken ct) =>
        order.Kind == WorkOrderKind.Corrective && order.DowntimeId is { } id && await store.FindOpenOutageAsync(order.MachineCode, ct) is { } open && open.Id == id;

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
        var sites = await uow.Store<IResourcesLookup>().ListSitesAsync(ct);
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(sites.Count > 0 ? sites[0].TimeZone : "UTC") ?? DateTimeZone.Utc;
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

    private static string Key(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string? Shorten(string? value, int max)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > max ? text[..max] : text;
    }
}
