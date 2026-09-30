using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Resources.Contracts;
using Forge.Workforce.Contracts;

namespace Forge.Workforce;

public sealed record RegisterWorker(PersonId PersonId, string HomeWorkCenterCode);

/// <summary>Assign a person to work. Give a machine, or a work center, or just a safety area.</summary>
public sealed record AssignWork(PersonId PersonId, string? SafetyAreaCode, string? WorkCenterCode, string? MachineCode, string ActivityCode, bool AsTrainee);

public sealed record EndAssignment(PersonId PersonId);

public sealed record RecordPresence(PersonId PersonId, PresenceKind Kind, DateTimeOffset? ValidUntil);

public sealed record RecordDeparture(PersonId PersonId);

public sealed record RecordUnavailability(PersonId PersonId, UnavailabilityKind Kind, DateTimeOffset StartsAt, DateTimeOffset EndsAt);

public sealed record WorkerRegistered(Guid PersonId, string HomeWorkCenterCode) : IDomainEvent
{
    public static string EventType => nameof(WorkerRegistered);

    public static int SchemaVersion => 1;
}

public sealed record EmployeeReallocated(Guid PersonId, WorkAssignment? Previous, WorkAssignment? Current) : IDomainEvent
{
    public static string EventType => nameof(EmployeeReallocated);

    public static int SchemaVersion => 1;
}

public sealed record PresenceRecorded(Guid PersonId, string Kind, DateTimeOffset ValidUntil) : IDomainEvent
{
    public static string EventType => nameof(PresenceRecorded);

    public static int SchemaVersion => 1;
}

public sealed record PresenceEnded(Guid PersonId) : IDomainEvent
{
    public static string EventType => nameof(PresenceEnded);

    public static int SchemaVersion => 1;
}

public sealed record EmployeeUnavailable(Guid PersonId, string Kind, DateTimeOffset StartsAt, DateTimeOffset EndsAt) : IDomainEvent
{
    public static string EventType => nameof(EmployeeUnavailable);

    public static int SchemaVersion => 1;
}

public interface IWorkforceStore
{
    Task<bool> InsertWorkerAsync(PersonId person, string displayName, string homeWorkCenter, DateTimeOffset at, string actor, CancellationToken ct);

    Task<WorkAssignment?> FindAssignmentAsync(PersonId person, CancellationToken ct);

    Task SetAssignmentAsync(PersonId person, WorkAssignment? assignment, DateTimeOffset at, string actor, CancellationToken ct);

    Task InsertPresenceAsync(PersonId person, PresenceKind kind, DateTimeOffset recordedAt, DateTimeOffset validUntil, string actor, CancellationToken ct);

    /// <returns>Number of presence records ended.</returns>
    Task<int> EndPresenceAsync(PersonId person, DateTimeOffset at, CancellationToken ct);

    Task InsertUnavailabilityAsync(PersonId person, UnavailabilityKind kind, DateTimeOffset startsAt, DateTimeOffset endsAt, DateTimeOffset at, string actor, CancellationToken ct);
}

/// <summary>
/// Manufacturing workforce state. Keeps presence (evidence someone is on site, which expires),
/// assignment (where and on what they work) and unavailability (no reason recorded) as separate facts.
/// </summary>
public sealed class WorkforceService(CommandPipeline pipeline)
{
    public const string SourceContext = "Workforce";
    public static readonly TimeSpan DefaultPresence = TimeSpan.FromHours(8);
    public static readonly TimeSpan MaximumPresence = TimeSpan.FromHours(16);
    private const string WorkerEntity = "Worker";

    public Task<CommandResult> RegisterWorkerAsync(RegisterWorker c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(SourceContext, nameof(RegisterWorker), Permissions.WorkforceManage, ResourceRef.Of(WorkerEntity, c.PersonId));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var home = BusinessCode.Require(c.HomeWorkCenterCode, "Work center");
            var person = await uow.People.FindAsync(c.PersonId, ct) ?? throw new CommandRejectedException("person_not_found", "Register the person in Forge first.");
            if (!person.IsActive)
            {
                throw new CommandRejectedException("person_inactive", "Person is not active.");
            }

            if (await uow.Store<IResourcesLookup>().FindWorkCenterAsync(home, ct) is null)
            {
                throw new CommandRejectedException("unknown_work_center", $"Work center {home} does not exist.");
            }

            if (!await uow.Store<IWorkforceStore>().InsertWorkerAsync(c.PersonId, person.DisplayName, home, now, context.Actor, ct))
            {
                throw new CommandRejectedException("worker_exists", "This person is already a registered worker.");
            }

            var id = c.PersonId.ToString();
            return new CommandOutcome(
                CommandResult.Ok(id, $"{person.DisplayName} registered as a worker in {home}."),
                [pipeline.Event(new WorkerRegistered(c.PersonId.Value, home), context, SourceContext, WorkerEntity, id)],
                new AuditChange(WorkerEntity, id, null, CanonicalJson.Serialize(new { homeWorkCenter = home })));
        }, ct);
    }

    public Task<CommandResult> AssignWorkAsync(AssignWork c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(SourceContext, nameof(AssignWork), Permissions.WorkforceSupervise, ResourceRef.Of(WorkerEntity, c.PersonId));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            await RequireWorkerAsync(uow.Store<IWorkforceLookup>(), c.PersonId, ct);
            var assignment = await ResolveAssignmentAsync(uow.Store<IResourcesLookup>(), c, ct);
            var store = uow.Store<IWorkforceStore>();
            var previous = await store.FindAssignmentAsync(c.PersonId, ct);
            if (previous == assignment)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(c.PersonId.ToString(), "Assignment unchanged."));
            }

            await store.SetAssignmentAsync(c.PersonId, assignment, now, context.Actor, ct);
            return Reallocated(c.PersonId, previous, assignment, context, $"Assigned to {Describe(assignment)}.");
        }, ct);
    }

    public Task<CommandResult> EndAssignmentAsync(EndAssignment c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(SourceContext, nameof(EndAssignment), Permissions.WorkforceSupervise, ResourceRef.Of(WorkerEntity, c.PersonId));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var store = uow.Store<IWorkforceStore>();
            var previous = await store.FindAssignmentAsync(c.PersonId, ct);
            if (previous is null)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(c.PersonId.ToString(), "No current assignment."));
            }

            await store.SetAssignmentAsync(c.PersonId, null, now, context.Actor, ct);
            return Reallocated(c.PersonId, previous, null, context, "Assignment ended.");
        }, ct);
    }

    public Task<CommandResult> RecordPresenceAsync(RecordPresence c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(SourceContext, nameof(RecordPresence), Permissions.WorkforceSupervise, ResourceRef.Of(WorkerEntity, c.PersonId));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            await RequireWorkerAsync(uow.Store<IWorkforceLookup>(), c.PersonId, ct);
            var until = c.ValidUntil ?? now + DefaultPresence;
            if (until <= now || until > now + MaximumPresence)
            {
                throw new CommandRejectedException("invalid_presence_window", $"Presence must end after now and within {MaximumPresence.TotalHours:0} hours.");
            }

            await uow.Store<IWorkforceStore>().InsertPresenceAsync(c.PersonId, c.Kind, now, until, context.Actor, ct);
            var id = c.PersonId.ToString();
            return new CommandOutcome(
                CommandResult.Ok(id, $"Present until {until:u}."),
                [pipeline.Event(new PresenceRecorded(c.PersonId.Value, c.Kind.ToString(), until), context, SourceContext, WorkerEntity, id)],
                new AuditChange(WorkerEntity, id, null, CanonicalJson.Serialize(new { presence = c.Kind.ToString(), validUntil = until })));
        }, ct);
    }

    public Task<CommandResult> RecordDepartureAsync(RecordDeparture c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(SourceContext, nameof(RecordDeparture), Permissions.WorkforceSupervise, ResourceRef.Of(WorkerEntity, c.PersonId));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var id = c.PersonId.ToString();
            if (await uow.Store<IWorkforceStore>().EndPresenceAsync(c.PersonId, now, ct) == 0)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(id, "No current presence to end."));
            }

            return new CommandOutcome(
                CommandResult.Ok(id, "Departure recorded."),
                [pipeline.Event(new PresenceEnded(c.PersonId.Value), context, SourceContext, WorkerEntity, id)],
                new AuditChange(WorkerEntity, id, "present", "departed"));
        }, ct);
    }

    public Task<CommandResult> RecordUnavailabilityAsync(RecordUnavailability c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(SourceContext, nameof(RecordUnavailability), Permissions.WorkforceSupervise, ResourceRef.Of(WorkerEntity, c.PersonId));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            await RequireWorkerAsync(uow.Store<IWorkforceLookup>(), c.PersonId, ct);
            if (c.EndsAt <= c.StartsAt || c.EndsAt - c.StartsAt > TimeSpan.FromDays(60))
            {
                throw new CommandRejectedException("invalid_period", "The end must be after the start, within 60 days.");
            }

            await uow.Store<IWorkforceStore>().InsertUnavailabilityAsync(c.PersonId, c.Kind, c.StartsAt, c.EndsAt, now, context.Actor, ct);
            var id = c.PersonId.ToString();
            return new CommandOutcome(
                CommandResult.Ok(id, $"Unavailable ({c.Kind}) {c.StartsAt:u} to {c.EndsAt:u}."),
                [pipeline.Event(new EmployeeUnavailable(c.PersonId.Value, c.Kind.ToString(), c.StartsAt, c.EndsAt), context, SourceContext, WorkerEntity, id)],
                new AuditChange(WorkerEntity, id, null, CanonicalJson.Serialize(new { unavailable = c.Kind.ToString(), c.StartsAt, c.EndsAt })));
        }, ct);
    }

    /// <summary>Validates and normalizes an assignment request against the resource model.</summary>
    public static async Task<WorkAssignment> ResolveAssignmentAsync(IResourcesLookup resources, AssignWork c, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(c);
        var activity = BusinessCode.Require(c.ActivityCode, "Activity");
        if (!await resources.ActivityTypeExistsAsync(activity, ct))
        {
            throw new CommandRejectedException("unknown_activity", $"Activity type {activity} does not exist.");
        }

        if (!string.IsNullOrWhiteSpace(c.MachineCode))
        {
            var machine = await resources.FindMachineAsync(BusinessCode.Require(c.MachineCode, "Machine"), ct)
                ?? throw new CommandRejectedException("unknown_machine", $"Machine {c.MachineCode} does not exist.");
            if (await resources.FindMachineActivityAsync(machine.Code, activity, ct) is null)
            {
                throw new CommandRejectedException("activity_not_on_machine", $"{activity} is not defined for machine {machine.Code}.");
            }

            return new WorkAssignment(machine.SafetyAreaCode, machine.WorkCenterCode, machine.Code, activity, c.AsTrainee);
        }

        if (!string.IsNullOrWhiteSpace(c.WorkCenterCode))
        {
            var workCenter = await resources.FindWorkCenterAsync(BusinessCode.Require(c.WorkCenterCode, "Work center"), ct)
                ?? throw new CommandRejectedException("unknown_work_center", $"Work center {c.WorkCenterCode} does not exist.");
            if (!(await resources.ListWorkCenterActivitiesAsync(ct)).Any(a => a.WorkCenterCode == workCenter.Code && a.ActivityCode == activity))
            {
                throw new CommandRejectedException("activity_not_at_work_center", $"{activity} is not an allowed activity at {workCenter.Code}.");
            }

            return new WorkAssignment(workCenter.SafetyAreaCode, workCenter.Code, null, activity, c.AsTrainee);
        }

        var area = await resources.FindSafetyAreaAsync(BusinessCode.Require(c.SafetyAreaCode, "Safety area"), ct)
            ?? throw new CommandRejectedException("unknown_safety_area", $"Safety area {c.SafetyAreaCode} does not exist.");
        return new WorkAssignment(area.Code, null, null, activity, c.AsTrainee);
    }

    public static string Describe(WorkAssignment a)
    {
        ArgumentNullException.ThrowIfNull(a);
        return $"{a.ActivityCode}{(a.AsTrainee ? " (trainee)" : string.Empty)} in {a.MachineCode ?? a.WorkCenterCode ?? a.SafetyAreaCode}";
    }

    private CommandOutcome Reallocated(PersonId person, WorkAssignment? previous, WorkAssignment? current, CommandContext context, string message)
    {
        var id = person.ToString();
        return new CommandOutcome(
            CommandResult.Ok(id, message),
            [pipeline.Event(new EmployeeReallocated(person.Value, previous, current), context, SourceContext, WorkerEntity, id)],
            new AuditChange(WorkerEntity, id, previous is null ? null : CanonicalJson.Serialize(previous), current is null ? null : CanonicalJson.Serialize(current)));
    }

    private static async Task RequireWorkerAsync(IWorkforceLookup lookup, PersonId person, CancellationToken ct)
    {
        if (!await lookup.WorkerExistsAsync(person.Value, ct))
        {
            throw new CommandRejectedException("worker_not_found", "This person is not a registered worker.");
        }
    }
}
