using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Competency.Contracts;
using Forge.Kernel.Access;
using Forge.Resources.Contracts;
using Forge.Workforce.Contracts;

namespace Forge.Workforce;

public sealed record RegisterWorker(PersonId PersonId, string HomeWorkCenterCode);

public sealed record SetWorkerJobTitle(PersonId PersonId, string? JobTitle);

/// <summary>Assign a person to work. Give a machine, or a work center, or just a safety area.</summary>
public sealed record AssignWork(PersonId PersonId, string? SafetyAreaCode, string? WorkCenterCode, string? MachineCode, string ActivityCode, bool AsTrainee);

public sealed record EndAssignment(PersonId PersonId);

public sealed record SetWorkerShift(PersonId PersonId, string? ShiftCode);

public sealed record WorkerShiftChanged(Guid PersonId, string? Previous, string? Current) : IDomainEvent
{
    public static string EventType => nameof(WorkerShiftChanged);

    public static int SchemaVersion => 1;
}

public sealed record RecordPresence(PersonId PersonId, PresenceKind Kind, DateTimeOffset? ValidUntil);

public sealed record RecordDeparture(PersonId PersonId);

public sealed record RecordUnavailability(PersonId PersonId, UnavailabilityKind Kind, DateTimeOffset StartsAt, DateTimeOffset EndsAt);

public sealed record WorkerRegistered(Guid PersonId, string HomeWorkCenterCode) : IDomainEvent
{
    public static string EventType => nameof(WorkerRegistered);

    public static int SchemaVersion => 1;
}

public sealed record WorkerJobTitleChanged(Guid PersonId, string? PreviousTitle, string? CurrentTitle) : IDomainEvent
{
    public static string EventType => nameof(WorkerJobTitleChanged);

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

    Task<string?> SetShiftAsync(PersonId person, string? shiftCode, CancellationToken ct);

    Task<string?> SetJobTitleAsync(PersonId person, string? jobTitle, DateTimeOffset at, string actor, CancellationToken ct);

    Task SetWorkerPhotoAsync(PersonId person, string? sha256, string? contentType, DateTimeOffset at, string actor, CancellationToken ct);

    Task InsertUnavailabilityAsync(PersonId person, UnavailabilityKind kind, DateTimeOffset startsAt, DateTimeOffset endsAt, DateTimeOffset at, string actor, CancellationToken ct);

    Task<long> InsertTemporaryMoveAsync(Guid person, string from, string to, DateOnly startsOn, DateOnly endsOn, DateTimeOffset at, string actor, CancellationToken ct);

    /// <summary>The move, locked for update.</summary>
    Task<TemporaryMoveInfo?> FindTemporaryMoveAsync(long moveId, CancellationToken ct);

    Task ChangeTemporaryMoveAsync(long moveId, string kind, DateOnly previousEndsOn, DateOnly endsOn, DateTimeOffset at, string actor, CancellationToken ct);

    Task CloseTemporaryMoveAsync(long moveId, TemporaryMoveStatus status, DateOnly endsOn, DateTimeOffset at, string actor, CancellationToken ct);

    Task SetMoveRemindedAsync(long moveId, DateOnly endsOn, CancellationToken ct);

    Task<long?> InsertTransferAsync(Guid person, string from, string to, DateOnly effectiveFrom, DateTimeOffset at, string actor, CancellationToken ct);
}

/// <summary>
/// Manufacturing workforce state. Keeps presence (evidence someone is on site, which expires),
/// assignment (where and on what they work) and unavailability (no reason recorded) as separate facts.
/// </summary>
public sealed partial class WorkforceService(CommandPipeline pipeline)
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
            await RequireSpecializedAssignmentCoverageAsync(uow, c, assignment, now, ct);
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

    public Task<CommandResult> SetWorkerShiftAsync(SetWorkerShift c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(SourceContext, nameof(SetWorkerShift), Permissions.WorkforceManage, ResourceRef.Of(WorkerEntity, c.PersonId));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, _) =>
        {
            await RequireWorkerAsync(uow.Store<IWorkforceLookup>(), c.PersonId, ct);
            var shift = string.IsNullOrWhiteSpace(c.ShiftCode) ? null : BusinessCode.Require(c.ShiftCode, "Shift");
            if (shift is not null && !(await uow.Store<IResourcesLookup>().ListShiftsAsync(ct)).Any(s => s.Code == shift))
            {
                throw new CommandRejectedException("unknown_shift", $"Shift {shift} does not exist.");
            }

            var id = c.PersonId.ToString();
            var previous = await uow.Store<IWorkforceStore>().SetShiftAsync(c.PersonId, shift, ct);
            if (previous == shift)
            {
                return CommandOutcome.NoChange(CommandResult.NoChange(id, "Shift unchanged."));
            }

            return new CommandOutcome(
                CommandResult.Ok(id, shift is null ? "No scheduled shift." : $"Scheduled on shift {shift}."),
                [pipeline.Event(new WorkerShiftChanged(c.PersonId.Value, previous, shift), context, SourceContext, WorkerEntity, id)],
                new AuditChange(WorkerEntity, id, previous, shift));
        }, ct);
    }

    public Task<CommandResult> SetWorkerJobTitleAsync(SetWorkerJobTitle c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var title = string.IsNullOrWhiteSpace(c.JobTitle) ? null : c.JobTitle.Trim();
        if (title?.Length > 100 || title?.Any(char.IsControl) == true)
            throw new CommandRejectedException("invalid_job_title", "An official title must be 100 characters or fewer and contain no control characters.");

        var spec = new CommandSpec(SourceContext, nameof(SetWorkerJobTitle), Permissions.WorkforceManage, ResourceRef.Of(WorkerEntity, c.PersonId), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c with { JobTitle = title }, context, async (uow, now) =>
        {
            var previous = await uow.Store<IWorkforceStore>().SetJobTitleAsync(c.PersonId, title, now, context.Actor, ct);
            if (previous is null && title is null)
                return CommandOutcome.NoChange(CommandResult.NoChange(c.PersonId.ToString(), "Official title is already blank."));
            if (previous == title)
                return CommandOutcome.NoChange(CommandResult.NoChange(c.PersonId.ToString(), "Official title is unchanged."));

            var id = c.PersonId.ToString();
            return new CommandOutcome(
                CommandResult.Ok(id, title is null ? "Official title cleared." : "Official title updated."),
                [pipeline.Event(new WorkerJobTitleChanged(c.PersonId.Value, previous, title), context, SourceContext, WorkerEntity, id)],
                new AuditChange(WorkerEntity, id, CanonicalJson.Serialize(new { jobTitle = previous }), CanonicalJson.Serialize(new { jobTitle = title })));
        }, ct);
    }

    public Task<CommandResult> RecordPresenceAsync(RecordPresence c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(SourceContext, nameof(RecordPresence), Permissions.WorkforceSupervise, ResourceRef.Of(WorkerEntity, c.PersonId));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            await RequireWorkerAsync(uow.Store<IWorkforceLookup>(), c.PersonId, ct);
            if (c.Kind is not (PresenceKind.ClockedIn or PresenceKind.SupervisorConfirmed))
            {
                throw new CommandRejectedException("invalid_presence_kind", "Presence must come from a clock-in or a supervisor confirmation.");
            }

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
            var events = new List<EventEnvelope>
            {
                pipeline.Event(new EmployeeUnavailable(c.PersonId.Value, c.Kind.ToString(), c.StartsAt, c.EndsAt), context, SourceContext, WorkerEntity, id),
            };
            if (c.Kind is UnavailabilityKind.CallOut or UnavailabilityKind.ApprovedPto)
            {
                var intelligence = uow.Store<IAttendanceReliabilityStore>();
                var opportunities = (await intelligence.ListAttendanceOpportunitiesAsync(c.StartsAt.AddDays(-1), c.EndsAt.AddDays(1), ct))
                    .Where(opportunity => opportunity.PersonId == c.PersonId.Value
                        && opportunity.ScheduledStart < c.EndsAt && opportunity.ScheduledEnd > c.StartsAt).ToArray();
                var existing = await intelligence.ListAttendanceIntelligenceEventsAsync(c.StartsAt.AddDays(-1), c.EndsAt.AddDays(1), ct);
                var kind = c.Kind == UnavailabilityKind.CallOut ? AttendanceEventKind.CallOut : AttendanceEventKind.ApprovedPto;
                foreach (var opportunity in opportunities.Where(opportunity => !existing.Any(item => item.OpportunityId == opportunity.OpportunityId
                    && item.Kind == kind && item.ReviewState != AttendanceEventReviewState.Superseded)))
                {
                    var item = new AttendanceIntelligenceEvent(ForgeIds.New(pipeline.Time), c.PersonId.Value, opportunity.OpportunityId, kind, c.StartsAt,
                        AttendanceEventReviewState.PendingReview, AttendanceEventSource.Manual, now, RecordedBy: context.Actor);
                    await intelligence.InsertAttendanceIntelligenceEventAsync(item, ct);
                    events.Add(pipeline.Event(new AttendanceIntelligenceEventRecorded(item.EventId, item.OpportunityId, item.PersonId,
                        item.Kind.ToString(), item.ReviewState.ToString(), item.Source.ToString()), context, SourceContext, "AttendanceEvent", item.EventId.ToString("D")));
                }
            }
            return new CommandOutcome(
                CommandResult.Ok(id, $"Unavailable ({c.Kind}) {c.StartsAt:u} to {c.EndsAt:u}."),
                events,
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

    private static async Task RequireSpecializedAssignmentCoverageAsync(IKernelUnitOfWork uow, AssignWork command,
        WorkAssignment assignment, DateTimeOffset now, CancellationToken ct)
    {
        var resources = uow.Store<IResourcesLookup>();
        var machines = await resources.ListMachinesAsync(ct);
        var machineCodes = assignment.MachineCode is { } selected
            ? new HashSet<string>([selected], StringComparer.Ordinal)
            : machines.Where(machine => machine.WorkCenterCode == assignment.WorkCenterCode).Select(machine => machine.Code).ToHashSet(StringComparer.Ordinal);
        var matchingProfiles = (await resources.ListMachineActivitiesAsync(ct))
            .Where(profile => profile.ActivityCode == assignment.ActivityCode && machineCodes.Contains(profile.MachineCode)).ToArray();
        var required = matchingProfiles.SelectMany(profile => profile.OperatorQualifications).Distinct(StringComparer.Ordinal).ToArray();
        if (required.Length == 0) return;
        var machineProfile = assignment.MachineCode is { } targetMachine
            ? matchingProfiles.SingleOrDefault(profile => profile.MachineCode == targetMachine)
            : null;
        var requiresMachineAuthorization = machineProfile?.RequiresMachineAuthorization == true;

        var competency = uow.Store<ICompetencyLookup>();
        var workerStates = await uow.Store<IWorkforceLookup>().GetWorkerStatesAsync(now, ct);
        var worker = workerStates.SingleOrDefault(state => state.PersonId == command.PersonId.Value)
            ?? throw new CommandRejectedException("worker_not_found", "This person is not a registered worker.");
        var qualifications = await competency.GetQualificationsAsync([command.PersonId], ct);
        var holdsRequiredQualification = required.Any(code => qualifications.Any(record => record.Code == code && record.IsValidAt(now)));
        var currentMachineAuthorizations = requiresMachineAuthorization
            ? await competency.GetMachineAuthorizationsAsync([command.PersonId], ct)
            : [];
        var hasMachineAuthorization = !requiresMachineAuthorization || currentMachineAuthorizations.Any(record =>
            record.MachineCode == machineProfile!.MachineCode && record.ActivityCode == machineProfile.ActivityCode
            && record.IsValidAt(now) && qualifications.Any(q => q.Code == record.QualificationCode && q.IsValidAt(now)));
        if (holdsRequiredQualification && hasMachineAuthorization) return;

        var trainingCodes = required.Where(code => qualifications.Any(record => record.Code == code && record.Status == QualificationStatus.InTraining)).ToArray();
        if (!command.AsTrainee || (!holdsRequiredQualification && trainingCodes.Length == 0))
        {
            if (holdsRequiredQualification && requiresMachineAuthorization)
                throw new CommandRejectedException("machine_authorization_required",
                    $"A current practical authorization for {machineProfile!.MachineCode} {machineProfile.ActivityCode} is required for independent assignment.");
            throw new CommandRejectedException("qualification_required",
                $"This assignment requires a current qualification ({string.Join(" or ", required)}). An unqualified worker cannot be assigned as an independent operator.");
        }

        var trainerPrerequisites = trainingCodes.Length > 0 ? trainingCodes : requiresMachineAuthorization && holdsRequiredQualification ? required : [];
        var trainerTypes = (await competency.ListTypesAsync(ct)).Where(type => type.Kind == QualificationKind.Trainer
            && trainerPrerequisites.Any(code => type.Prerequisites.Contains(code, StringComparer.Ordinal)
                || (type.PrerequisiteAlternatives ?? []).Any(group => group.QualificationCodes.Contains(code, StringComparer.Ordinal)))).ToList();
        var potentialTrainers = workerStates.Where(state => state.PersonId != worker.PersonId && state.IsActive && state.Unavailability is null
            && state.PresenceKind is PresenceKind.ClockedIn or PresenceKind.SupervisorConfirmed
            && state.PresentUntil is { } presentUntil && presentUntil > now
            && state.Assignment is { AsTrainee: false } current && current.WorkCenterCode == assignment.WorkCenterCode
            && (worker.ShiftCode is null || state.ShiftCode == worker.ShiftCode)).ToList();
        IReadOnlyList<PersonQualification> trainerRecords = potentialTrainers.Count == 0 ? []
            : await competency.GetQualificationsAsync(potentialTrainers.Select(state => new PersonId(state.PersonId)).ToArray(), ct);
        var authorizedTrainers = !requiresMachineAuthorization ? potentialTrainers.Select(trainer => trainer.PersonId).ToHashSet()
            : (await competency.GetMachineAuthorizationsAsync(potentialTrainers.Select(state => new PersonId(state.PersonId)).ToArray(), ct))
                .Where(record => record.MachineCode == machineProfile!.MachineCode && record.ActivityCode == machineProfile.ActivityCode && record.IsValidAt(now)
                    && required.Contains(record.QualificationCode, StringComparer.Ordinal))
                .Select(record => record.PersonId).ToHashSet();
        var covered = potentialTrainers.Any(trainer => authorizedTrainers.Contains(trainer.PersonId)
            && trainerTypes.Any(type => trainerRecords.Any(record => record.PersonId == trainer.PersonId
                && record.Code == type.Code && record.IsValidAt(now))));
        if (!covered)
        {
            if (requiresMachineAuthorization)
                throw new CommandRejectedException("authorized_trainer_required",
                    $"A qualified trainer with current authorization for {machineProfile!.MachineCode} {machineProfile.ActivityCode} must be clocked in and assigned to this work center on the same shift before this trainee placement can proceed.");
            throw new CommandRejectedException("qualified_trainer_required",
                $"{string.Join(" or ", trainingCodes)} is in training. A qualified trainer must already be clocked in and assigned to {assignment.WorkCenterCode ?? assignment.SafetyAreaCode} on the same shift before this placement can proceed.");
        }
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
