using System.Globalization;
using Forge.Competency.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Resources.Contracts;
using Forge.Workforce.Contracts;

namespace Forge.Workforce;

public enum TrainingRequestSource { Onboarding, Expiry, Supervisor, Development }
public enum TrainingRequestStatus { Requested, Scheduled, Completed, Cancelled }

/// <summary>Training every new employee must complete: plant-wide when <see cref="WorkCenterCode"/> is null, otherwise for new hires in that work center.</summary>
public sealed record OnboardingRequirement(Guid RequirementId, string? WorkCenterCode, string QualificationCode, int DueDays, string Note, bool Active,
    DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt, string UpdatedBy);

/// <summary>One person needing one piece of training scheduled. Delivering it grants nothing; certification stays in Competency.</summary>
public sealed record TrainingRequest(
    Guid RequestId,
    Guid PersonId,
    TrainingRequestSource Source,
    string SourceKey,
    string? QualificationCode,
    string Title,
    string Reason,
    DateOnly? DueOn,
    TrainingRequestStatus Status,
    Guid? TrainerPersonId,
    DateTimeOffset? ScheduledStart,
    DateTimeOffset? ScheduledEnd,
    string? Location,
    string? OutcomeNote,
    string RequestedBy,
    Guid? RequestedByPersonId,
    DateTimeOffset RequestedAt,
    DateTimeOffset UpdatedAt,
    int Version)
{
    public bool IsOpen => Status is TrainingRequestStatus.Requested or TrainingRequestStatus.Scheduled;
}

public sealed record TrainingRequestEvent(Guid EventId, Guid RequestId, TrainingRequestStatus? FromStatus, TrainingRequestStatus ToStatus, string Note,
    string RecordedBy, Guid? RecordedByPersonId, DateTimeOffset RecordedAt);

public sealed record DefineOnboardingRequirement(string? WorkCenterCode, string QualificationCode, int DueDays, string? Note);
public sealed record RetireOnboardingRequirement(Guid RequirementId);
public sealed record StartOnboarding(Guid PersonId);
public sealed record RequestTraining(Guid PersonId, string? QualificationCode, string? Title, DateOnly? DueOn);
public sealed record ScheduleTraining(Guid RequestId, Guid? TrainerPersonId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string? Location);
public sealed record CompleteTraining(Guid RequestId, string Note);
public sealed record CancelTraining(Guid RequestId, string Note);
public sealed record OpenExpiryTrainingRequests(int WithinDays);

/// <summary>A request needs scheduling; <see cref="Message"/> is the plain-words notice the technical trainers receive.</summary>
public sealed record TrainingRequested(Guid RequestId, Guid PersonId, string Source, string Title, DateOnly? DueOn, string Message) : IDomainEvent
{
    public static string EventType => nameof(TrainingRequested);
    public static int SchemaVersion => 1;
}

/// <summary>A request left the "needs scheduling" state (scheduled, completed or cancelled); clears the trainers' notice.</summary>
public sealed record TrainingRequestHandled(Guid RequestId, Guid PersonId, string Status, string Message) : IDomainEvent
{
    public static string EventType => nameof(TrainingRequestHandled);
    public static int SchemaVersion => 1;
}

public sealed record OnboardingRequirementChanged(Guid RequirementId, string? WorkCenterCode, string QualificationCode, bool Active) : IDomainEvent
{
    public static string EventType => nameof(OnboardingRequirementChanged);
    public static int SchemaVersion => 1;
}

public interface ITrainingRequestStore
{
    Task<IReadOnlyList<OnboardingRequirement>> ListRequirementsAsync(bool activeOnly, CancellationToken ct);
    Task<OnboardingRequirement?> FindRequirementAsync(Guid requirementId, CancellationToken ct);
    Task InsertRequirementAsync(OnboardingRequirement requirement, CancellationToken ct);
    Task<bool> RetireRequirementAsync(Guid requirementId, DateTimeOffset at, string actor, CancellationToken ct);
    Task<IReadOnlyList<TrainingRequest>> ListRequestsAsync(Guid? personId, bool openOnly, CancellationToken ct);
    Task<TrainingRequest?> FindRequestAsync(Guid requestId, CancellationToken ct);

    /// <summary>False when the same person already has a request from the same source and source key.</summary>
    Task<bool> InsertRequestAsync(TrainingRequest request, CancellationToken ct);
    Task<bool> UpdateRequestAsync(TrainingRequest request, int expectedVersion, CancellationToken ct);
    Task InsertRequestEventAsync(TrainingRequestEvent requestEvent, CancellationToken ct);
    Task<IReadOnlyList<TrainingRequestEvent>> ListRequestEventsAsync(Guid requestId, CancellationToken ct);
}

/// <summary>
/// New-employee training and training requests. Requests open from onboarding (a new worker is registered, or onboarding is started
/// for someone), from a qualification about to expire, from a supervisor, and from an approved development packet. Technical trainers
/// are notified, schedule the training and record that it was delivered. Nothing here certifies anyone.
/// </summary>
public sealed class TrainingRequestService(CommandPipeline pipeline)
{
    private const string SourceContext = "Workforce";
    private const string RequestEntity = "TrainingRequest";
    public const int DefaultExpiryWindowDays = 30;

    public Task<CommandResult> DefineRequirementAsync(DefineOnboardingRequirement c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var id = ForgeIds.New(pipeline.Time);
        var spec = new CommandSpec(SourceContext, nameof(DefineOnboardingRequirement), Permissions.OnboardingManage,
            ResourceRef.Of("OnboardingRequirement", id.ToString("D")), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var code = BusinessCode.Require(c.QualificationCode, "Qualification");
            if (!await uow.Store<ICompetencyLookup>().QualificationTypeExistsAsync(code, ct))
                throw new CommandRejectedException("unknown_qualification", $"Qualification {code} is not defined in Competency.");
            string? center = null;
            if (!string.IsNullOrWhiteSpace(c.WorkCenterCode))
            {
                center = BusinessCode.Require(c.WorkCenterCode, "Work center");
                if (await uow.Store<IResourcesLookup>().FindWorkCenterAsync(center, ct) is null)
                    throw new CommandRejectedException("unknown_work_center", $"Work center {center} does not exist.");
            }

            if (c.DueDays is < 1 or > 365)
                throw new CommandRejectedException("invalid_due_days", "New hires get between 1 and 365 days to complete a requirement.");
            var store = uow.Store<ITrainingRequestStore>();
            if ((await store.ListRequirementsAsync(true, ct)).Any(r => r.QualificationCode == code && r.WorkCenterCode == center))
                throw new CommandRejectedException("requirement_exists", $"{code} is already required for {(center is null ? "every new employee" : $"new employees in {center}")}.");
            var requirement = new OnboardingRequirement(id, center, code, c.DueDays, (c.Note ?? string.Empty).Trim(), true, now, context.Actor, now, context.Actor);
            await store.InsertRequirementAsync(requirement, ct);
            var key = id.ToString("D");
            return new CommandOutcome(
                CommandResult.Ok(key, $"{code} is now required within {c.DueDays} days for {(center is null ? "every new employee" : $"new employees in {center}")}."),
                [pipeline.Event(new OnboardingRequirementChanged(id, center, code, true), context, SourceContext, "OnboardingRequirement", key)],
                new AuditChange("OnboardingRequirement", key, null, CanonicalJson.Serialize(requirement)));
        }, ct);
    }

    public Task<CommandResult> RetireRequirementAsync(RetireOnboardingRequirement c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var key = c.RequirementId.ToString("D");
        var spec = new CommandSpec(SourceContext, nameof(RetireOnboardingRequirement), Permissions.OnboardingManage,
            ResourceRef.Of("OnboardingRequirement", key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var store = uow.Store<ITrainingRequestStore>();
            var prior = await store.FindRequirementAsync(c.RequirementId, ct)
                ?? throw new CommandRejectedException("requirement_not_found", "The onboarding requirement does not exist.");
            if (!prior.Active) return CommandOutcome.NoChange(CommandResult.NoChange(key, "The requirement was already retired."));
            await store.RetireRequirementAsync(c.RequirementId, now, context.Actor, ct);
            return new CommandOutcome(
                CommandResult.Ok(key, $"{prior.QualificationCode} is no longer required for new employees. Open requests stay as they are."),
                [pipeline.Event(new OnboardingRequirementChanged(prior.RequirementId, prior.WorkCenterCode, prior.QualificationCode, false), context, SourceContext, "OnboardingRequirement", key)],
                new AuditChange("OnboardingRequirement", key, CanonicalJson.Serialize(prior), CanonicalJson.Serialize(prior with { Active = false, UpdatedAt = now, UpdatedBy = context.Actor })));
        }, ct);
    }

    /// <summary>Opens a request for every onboarding requirement the person does not already hold or have open.</summary>
    public Task<CommandResult> StartOnboardingAsync(StartOnboarding c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var key = c.PersonId.ToString("D");
        var spec = new CommandSpec(SourceContext, nameof(StartOnboarding), Permissions.TrainingRequestsRequest, ResourceRef.Of("Worker", key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var worker = (await uow.Store<IWorkforceLookup>().GetWorkerStatesAsync(now, ct)).FirstOrDefault(w => w.PersonId == c.PersonId && w.IsActive)
                ?? throw new CommandRejectedException("worker_not_found", "Onboarding needs an active Forge worker.");
            var held = (await uow.Store<ICompetencyLookup>().GetQualificationsAsync([new PersonId(c.PersonId)], ct))
                .Where(q => q.IsValidAt(now)).Select(q => q.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var events = await OpenOnboardingAsync(pipeline, uow, worker.PersonId, worker.DisplayName, worker.HomeWorkCenterCode, held, now, context, ct);
            if (events.Count == 0)
                return CommandOutcome.NoChange(CommandResult.NoChange(key, $"{worker.DisplayName} already holds or has open requests for every new-employee requirement."));
            return new CommandOutcome(
                CommandResult.Ok(key, $"Opened {events.Count} new-employee training request(s) for {worker.DisplayName}; technical trainers were notified."),
                events, new AuditChange("Worker", key, null, CanonicalJson.Serialize(new { onboardingRequests = events.Count })));
        }, ct);
    }

    public Task<CommandResult> RequestAsync(RequestTraining c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var id = ForgeIds.New(pipeline.Time);
        var spec = new CommandSpec(SourceContext, nameof(RequestTraining), Permissions.TrainingRequestsRequest,
            ResourceRef.Of(RequestEntity, id.ToString("D")), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var worker = (await uow.Store<IWorkforceLookup>().GetWorkerStatesAsync(now, ct)).FirstOrDefault(w => w.PersonId == c.PersonId && w.IsActive)
                ?? throw new CommandRejectedException("worker_not_found", "Training can be requested only for an active Forge worker.");
            string? code = null;
            var title = c.Title?.Trim();
            if (!string.IsNullOrWhiteSpace(c.QualificationCode))
            {
                code = BusinessCode.Require(c.QualificationCode, "Qualification");
                var type = (await uow.Store<ICompetencyLookup>().ListTypesAsync(ct)).FirstOrDefault(t => string.Equals(t.Code, code, StringComparison.OrdinalIgnoreCase))
                    ?? throw new CommandRejectedException("unknown_qualification", $"Qualification {code} is not defined in Competency.");
                code = type.Code;
                if (string.IsNullOrWhiteSpace(title)) title = $"{type.Name} ({type.Code})";
            }

            if (string.IsNullOrWhiteSpace(title))
                throw new CommandRejectedException("training_title_required", "Choose a qualification or describe the training needed.");
            var store = uow.Store<ITrainingRequestStore>();
            if (code is not null && (await store.ListRequestsAsync(c.PersonId, true, ct)).Any(r => string.Equals(r.QualificationCode, code, StringComparison.OrdinalIgnoreCase)))
                throw new CommandRejectedException("training_already_requested", $"{worker.DisplayName} already has an open request for {code}.");
            if (c.DueOn is { } due && due < await uow.SiteDateAsync(now, ct))
                throw new CommandRejectedException("invalid_due_date", "The due date cannot be in the past.");
            var request = NewRequest(id, c.PersonId, TrainingRequestSource.Supervisor, id.ToString("N"), code, BusinessCode.RequireName(title, "Training"),
                context.Reason!, c.DueOn, context, now);
            await store.InsertRequestAsync(request, ct);
            await store.InsertRequestEventAsync(new TrainingRequestEvent(ForgeIds.New(pipeline.Time), id, null, TrainingRequestStatus.Requested, context.Reason!,
                context.Actor, PersonOf(context), now), ct);
            var key = id.ToString("D");
            return new CommandOutcome(CommandResult.Ok(key, $"Training requested for {worker.DisplayName}: {request.Title}. Technical trainers were notified."),
                [Requested(pipeline, request, worker.DisplayName, context)], new AuditChange(RequestEntity, key, null, CanonicalJson.Serialize(request)));
        }, ct);
    }

    public Task<CommandResult> ScheduleAsync(ScheduleTraining c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeAsync(nameof(ScheduleTraining), c.RequestId, c, context, async (uow, prior, now, name) =>
        {
            if (!prior.IsOpen) throw new CommandRejectedException("training_closed", $"This request is already {prior.Status}.");
            if (c.EndsAt <= c.StartsAt || c.EndsAt - c.StartsAt > TimeSpan.FromDays(14))
                throw new CommandRejectedException("invalid_training_time", "Choose an end after the start, within 14 days.");
            if (c.StartsAt < now.AddDays(-1))
                throw new CommandRejectedException("invalid_training_time", "Training cannot be scheduled in the past.");
            Guid? trainer = c.TrainerPersonId ?? PersonOf(context);
            if (trainer is { } t)
            {
                if (t == prior.PersonId) throw new CommandRejectedException("separation_of_duties", "A person cannot be their own trainer.");
                if (await uow.People.FindAsync(new PersonId(t), ct) is not { IsActive: true })
                    throw new CommandRejectedException("trainer_not_found", "The trainer must be an active Forge person.");
            }

            var next = prior with
            {
                Status = TrainingRequestStatus.Scheduled, TrainerPersonId = trainer, ScheduledStart = c.StartsAt, ScheduledEnd = c.EndsAt,
                Location = string.IsNullOrWhiteSpace(c.Location) ? null : c.Location.Trim(),
            };
            var when = c.StartsAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            return (next, $"{prior.Title} for {name} scheduled {when} UTC{(next.Location is null ? "" : $" at {next.Location}")}.",
                prior.Status == TrainingRequestStatus.Requested);
        }, ct);
    }

    public Task<CommandResult> CompleteAsync(CompleteTraining c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeAsync(nameof(CompleteTraining), c.RequestId, c, context, (uow, prior, now, name) =>
        {
            if (prior.Status != TrainingRequestStatus.Scheduled)
                throw new CommandRejectedException("training_not_scheduled", "Schedule the training before recording that it was delivered.");
            if (prior.ScheduledStart > now)
                throw new CommandRejectedException("training_not_started", "The scheduled training has not started yet.");
            var note = RequireNote(c.Note);
            return Task.FromResult((prior with { Status = TrainingRequestStatus.Completed, OutcomeNote = note },
                $"{prior.Title} delivered to {name}. Record course completion or certification in Competency; this request grants nothing by itself.", true));
        }, ct);
    }

    public Task<CommandResult> CancelAsync(CancelTraining c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ChangeAsync(nameof(CancelTraining), c.RequestId, c, context, (uow, prior, now, name) =>
        {
            if (!prior.IsOpen) throw new CommandRejectedException("training_closed", $"This request is already {prior.Status}.");
            var note = RequireNote(c.Note);
            return Task.FromResult((prior with { Status = TrainingRequestStatus.Cancelled, OutcomeNote = note }, $"{prior.Title} for {name} cancelled: {note}", true));
        }, ct);
    }

    /// <summary>Opens a reassessment request for every certified qualification expiring within the window (idempotent per expiry).</summary>
    public Task<CommandResult> OpenExpiryRequestsAsync(OpenExpiryTrainingRequests c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(SourceContext, nameof(OpenExpiryTrainingRequests), Permissions.TrainingRequestsRequest, ResourceRef.Of(RequestEntity, "expiry-scan"));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            if (c.WithinDays is < 1 or > 180) throw new CommandRejectedException("invalid_window", "Look ahead between 1 and 180 days.");
            var workers = (await uow.Store<IWorkforceLookup>().GetWorkerStatesAsync(now, ct)).Where(w => w.IsActive).ToDictionary(w => w.PersonId);
            if (workers.Count == 0) return CommandOutcome.NoChange(CommandResult.NoChange("expiry-scan", "No active workers."));
            var competency = uow.Store<ICompetencyLookup>();
            var names = (await competency.ListTypesAsync(ct)).DistinctBy(t => t.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(t => t.Code, t => t.Name, StringComparer.OrdinalIgnoreCase);
            var horizon = now.AddDays(c.WithinDays);
            var expiring = (await competency.GetQualificationsAsync([.. workers.Keys.Select(id => new PersonId(id))], ct))
                .Where(q => q.IsValidAt(now) && q.ExpiresAt is { } x && x <= horizon).ToList();
            var store = uow.Store<ITrainingRequestStore>();
            var zone = await uow.SiteTimeZoneAsync(ct);
            var events = new List<EventEnvelope>();
            foreach (var q in expiring)
            {
                var worker = workers[q.PersonId];
                var id = ForgeIds.New(pipeline.Time);
                var due = SiteTime.DateAt(q.ExpiresAt!.Value, zone);
                var title = $"Reassess {names.GetValueOrDefault(q.Code, q.Code)} ({q.Code})";
                var request = NewRequest(id, q.PersonId, TrainingRequestSource.Expiry, q.Code + ":" + q.ExpiresAt.Value.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture), q.Code, title,
                    $"{q.Code} expires on {Day(due)}; reassess before it lapses.", due, context, now);
                if (!await store.InsertRequestAsync(request, ct)) continue;
                await store.InsertRequestEventAsync(new TrainingRequestEvent(ForgeIds.New(pipeline.Time), id, null, TrainingRequestStatus.Requested, request.Reason,
                    context.Actor, PersonOf(context), now), ct);
                events.Add(Requested(pipeline, request, worker.DisplayName, context));
            }

            if (events.Count == 0) return CommandOutcome.NoChange(CommandResult.NoChange("expiry-scan", "Every expiring qualification already has a request."));
            return new CommandOutcome(CommandResult.Ok("expiry-scan", $"Opened {events.Count} reassessment request(s) for qualifications expiring within {c.WithinDays} days."),
                events, new AuditChange(RequestEntity, "expiry-scan", null, CanonicalJson.Serialize(new { opened = events.Count, c.WithinDays })));
        }, ct);
    }

    /// <summary>
    /// Opens the onboarding requests for a person inside the caller's command (registering a worker, or starting onboarding).
    /// Returns one TrainingRequested event per request opened.
    /// </summary>
    public static async Task<List<EventEnvelope>> OpenOnboardingAsync(CommandPipeline pipeline, IKernelUnitOfWork uow, Guid personId, string displayName,
        string homeWorkCenterCode, IReadOnlySet<string> held, DateTimeOffset now, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(context);
        var store = uow.Store<ITrainingRequestStore>();
        var requirements = (await store.ListRequirementsAsync(true, ct))
            .Where(r => r.WorkCenterCode is null || string.Equals(r.WorkCenterCode, homeWorkCenterCode, StringComparison.OrdinalIgnoreCase)).ToList();
        if (requirements.Count == 0) return [];
        var open = (await store.ListRequestsAsync(personId, true, ct)).Select(r => r.QualificationCode).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = (await uow.Store<ICompetencyLookup>().ListTypesAsync(ct)).DistinctBy(t => t.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(t => t.Code, t => t.Name, StringComparer.OrdinalIgnoreCase);
        var today = await uow.SiteDateAsync(now, ct);
        var events = new List<EventEnvelope>();
        foreach (var requirement in requirements.OrderBy(r => r.DueDays).ThenBy(r => r.QualificationCode, StringComparer.Ordinal))
        {
            if (held.Contains(requirement.QualificationCode) || open.Contains(requirement.QualificationCode)) continue;
            var id = ForgeIds.New(pipeline.Time);
            var title = $"{names.GetValueOrDefault(requirement.QualificationCode, requirement.QualificationCode)} ({requirement.QualificationCode})";
            var reason = requirement.WorkCenterCode is null ? "New employee training (every new hire)." : $"New employee training for {requirement.WorkCenterCode}.";
            if (requirement.Note.Length > 0) reason += " " + requirement.Note;
            var request = NewRequest(id, personId, TrainingRequestSource.Onboarding, requirement.RequirementId.ToString("N"), requirement.QualificationCode,
                title, reason, today.AddDays(requirement.DueDays), context, now);
            if (!await store.InsertRequestAsync(request, ct)) continue;
            await store.InsertRequestEventAsync(new TrainingRequestEvent(ForgeIds.New(pipeline.Time), id, null, TrainingRequestStatus.Requested, reason,
                context.Actor, PersonOf(context), now), ct);
            events.Add(Requested(pipeline, request, displayName, context));
            open.Add(requirement.QualificationCode);
        }

        return events;
    }

    /// <summary>An approved development packet becomes a request to schedule its training, inside the approval command.</summary>
    public static async Task<EventEnvelope?> OpenDevelopmentAsync(CommandPipeline pipeline, IKernelUnitOfWork uow, DevelopmentNomination nomination, string note,
        DateTimeOffset now, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(nomination);
        var worker = (await uow.Store<IWorkforceLookup>().GetWorkerStatesAsync(now, ct)).FirstOrDefault(w => w.PersonId == nomination.PersonId);
        if (worker is not { IsActive: true }) return null;
        var id = ForgeIds.New(pipeline.Time);
        var reason = $"Approved development packet: {note}";
        var request = NewRequest(id, nomination.PersonId, TrainingRequestSource.Development, nomination.NominationId.ToString("N"), null,
            Clip(nomination.Pathway, 200), Clip(reason, 800), null, context, now);
        var store = uow.Store<ITrainingRequestStore>();
        if (!await store.InsertRequestAsync(request, ct)) return null;
        await store.InsertRequestEventAsync(new TrainingRequestEvent(ForgeIds.New(pipeline.Time), id, null, TrainingRequestStatus.Requested, request.Reason,
            context.Actor, PersonOf(context), now), ct);
        return Requested(pipeline, request, worker.DisplayName, context);
    }

    private Task<CommandResult> ChangeAsync<TCommand>(string commandType, Guid requestId, TCommand command, CommandContext context,
        Func<IKernelUnitOfWork, TrainingRequest, DateTimeOffset, string, Task<(TrainingRequest Next, string Message, bool Handled)>> change, CancellationToken ct)
    {
        var key = requestId.ToString("D");
        var spec = new CommandSpec(SourceContext, commandType, Permissions.TrainingRequestsManage, ResourceRef.Of(RequestEntity, key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, command, context, async (uow, now) =>
        {
            var store = uow.Store<ITrainingRequestStore>();
            var prior = await store.FindRequestAsync(requestId, ct) ?? throw new CommandRejectedException("training_request_not_found", "The training request does not exist.");
            var name = (await uow.People.FindAsync(new PersonId(prior.PersonId), ct))?.DisplayName ?? "the employee";
            var (next, message, handled) = await change(uow, prior, now, name);
            next = next with { UpdatedAt = now, Version = prior.Version + 1 };
            if (!await store.UpdateRequestAsync(next, prior.Version, ct))
                throw new CommandRejectedException("training_request_changed", "Someone else updated this request. Refresh and try again.");
            await store.InsertRequestEventAsync(new TrainingRequestEvent(ForgeIds.New(pipeline.Time), requestId, prior.Status, next.Status,
                context.Reason ?? message, context.Actor, PersonOf(context), now), ct);
            EventEnvelope[] events = handled
                ? [pipeline.Event(new TrainingRequestHandled(requestId, prior.PersonId, next.Status.ToString(), message), context, SourceContext, RequestEntity, key)]
                : [];
            return new CommandOutcome(CommandResult.Ok(key, message), events,
                new AuditChange(RequestEntity, key, CanonicalJson.Serialize(prior), CanonicalJson.Serialize(next)));
        }, ct);
    }

    private static TrainingRequest NewRequest(Guid id, Guid personId, TrainingRequestSource source, string sourceKey, string? code, string title, string reason,
        DateOnly? dueOn, CommandContext context, DateTimeOffset now) =>
        new(id, personId, source, sourceKey, code, title, reason.Trim(), dueOn, TrainingRequestStatus.Requested, null, null, null, null, null,
            context.Actor, PersonOf(context), now, now, 1);

    private static EventEnvelope Requested(CommandPipeline pipeline, TrainingRequest request, string displayName, CommandContext context)
    {
        var why = request.Source switch
        {
            TrainingRequestSource.Onboarding => "new employee training",
            TrainingRequestSource.Expiry => "qualification expiring",
            TrainingRequestSource.Development => "approved development packet",
            _ => "requested by a supervisor",
        };
        var due = request.DueOn is { } d ? $", due {Day(d)}" : "";
        return pipeline.Event(new TrainingRequested(request.RequestId, request.PersonId, request.Source.ToString(), request.Title, request.DueOn,
            $"{displayName} needs {request.Title} scheduled ({why}{due})."), context, SourceContext, RequestEntity, request.RequestId.ToString("D"));
    }

    private static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Clip(string text, int length) => text.Length > length ? text[..length] : text;

    private static Guid? PersonOf(CommandContext context) => context.Principal.PersonId.Value == Guid.Empty ? null : context.Principal.PersonId.Value;

    private static string RequireNote(string note)
    {
        if (string.IsNullOrWhiteSpace(note) || note.Trim().Length > 800)
            throw new CommandRejectedException("note_required", "Add a note of at most 800 characters.");
        return note.Trim();
    }
}
