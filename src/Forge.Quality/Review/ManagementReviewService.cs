using System.Globalization;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;
using Forge.Quality.Planning;
using static Forge.Quality.Review.QmsReviewService;

namespace Forge.Quality.Review;

public sealed record CreateManagementReview(Guid Id, string ProgramCode, int ProgramRevision, DateTimeOffset PeriodFrom, DateTimeOffset PeriodTo);
public sealed record FreezeManagementReviewInputs(Guid Id, int ExpectedVersion);
public sealed record RecordManagementReviewMeeting(Guid Id, int ExpectedVersion, string MeetingRecord,
    IReadOnlyList<Guid> Participants, IReadOnlyList<QmsReviewInputAssessment> Assessments,
    IReadOnlyList<QmsManagementReviewDecision> Decisions, string Limitations);
public sealed record TransitionManagementReview(Guid Id, int ExpectedVersion, string Action, string Note);
public sealed record TransitionManagementReviewAction(Guid Id, int ExpectedVersion, string Action, string Result,
    IReadOnlyList<QmsPlanningEvidenceReference> Evidence);

public sealed class ManagementReviewService(CommandPipeline pipeline, IKernelUnitOfWorkFactory factory,
    QmsReviewService definitions, QmsPlanningService planning, IQmsPlanningEvidenceReader evidence, TimeProvider time)
{
    public async Task<QmsReviewPage<QmsManagementReviewRun>> ListAsync(ForgePrincipal principal, string? programCode, int take, string? after, CancellationToken ct)
    {
        Require(take is > 0 and <= 100, "review_page", "Choose a page size from 1 to 100.");
        if (programCode is not null) programCode = BusinessCode.Require(programCode, "Review program");
        await using var uow = await factory.BeginSnapshotAsync(ct);
        var result = new List<QmsManagementReviewRun>(); var cursor = after;
        while (true)
        {
            var page = await uow.Store<IQmsReviewLookup>().RunsAsync(programCode, 100, cursor, ct);
            foreach (var run in page.Items)
            {
                try { await AuthorityAsync(uow, principal, run, ct); }
                catch (ForgeAuthorizationException) { continue; }
                catch (CommandRejectedException) { continue; }
                if (result.Count == take) return new(result, result[^1].Id.ToString("D"));
                result.Add(run);
            }
            if (page.NextCursor is null) return new(result, null);
            cursor = page.NextCursor;
        }
    }

    public async Task<QmsManagementReviewRun> ReadAsync(ForgePrincipal principal, Guid id, CancellationToken ct)
    {
        await using var uow = await factory.BeginSnapshotAsync(ct);
        var run = await RunAsync(uow, id, ct); await AuthorityAsync(uow, principal, run, ct);
        return run;
    }
    public async Task<IReadOnlyList<QmsReviewAction>> ActionsAsync(ForgePrincipal principal, Guid id, CancellationToken ct)
    {
        await using var uow = await factory.BeginSnapshotAsync(ct);
        var run = await RunAsync(uow, id, ct); await AuthorityAsync(uow, principal, run, ct);
        var actions = await uow.Store<IQmsReviewLookup>().ActionsAsync(id, ct);
        foreach (var action in actions) await planning.CurrentEvidenceAsync(uow, principal, action.Evidence, false, ct);
        return actions;
    }
    public Task<CommandResult> CreateAsync(CreateManagementReview c, CommandContext context, CancellationToken ct)
    {
        Require(c.Id != Guid.Empty, "review_identity", "Use a new opaque review identity.");
        return definitions.ExecuteAsync(nameof(CreateManagementReview), c, c.ProgramCode, Permissions.QmsManage, context,
            async (uow, definition, now) =>
            {
                Require(definition.Kind == QmsReviewDefinitionKind.ManagementReviewProgram, "review_program_kind", "Select an actual approved review program.");
                var revision = Revision(definition, c.ProgramRevision); Effective(revision, now);
                QmsReviewRules.Period(c.PeriodFrom, c.PeriodTo, now);
                Require(await uow.Store<IQmsReviewLookup>().FindRunAsync(c.Id, ct) is null, "review_exists", "This review already exists.");
                var run = new QmsManagementReviewRun(c.Id, 1, QmsReviewRunState.Draft, definition.Code, revision.Revision,
                    c.PeriodFrom, c.PeriodTo, context.Actor, now, null, [], [], "", [], null, null, null, "",
                    Append([], "Create", 1, "", "Draft", "Review for the exact approved program and closed period", context, now));
                await uow.Store<IQmsReviewStore>().SaveRunAsync(run, null, ct);
                return definitions.Outcome(definition, "QmsReviewRun", c.Id.ToString("D"), 1, "Create", "Draft", null, run, context, revision.Sources);
            }, ct, async (uow, definition, now) =>
            {
                Effective(Revision(definition, c.ProgramRevision), now);
                QmsReviewRules.Period(c.PeriodFrom, c.PeriodTo, now);
                await definitions.SourcesAsync(uow, context.Principal, definition.Kind, Revision(definition, c.ProgramRevision).Content, true, now, ct);
            });
    }
    public Task<CommandResult> FreezeAsync(FreezeManagementReviewInputs c, CommandContext context, CancellationToken ct) =>
        ExecuteAsync(nameof(FreezeManagementReviewInputs), c, c.Id, Permissions.QmsManage, context, async (uow, run, definition, now) =>
        {
            Version(run.Version, c.ExpectedVersion); Require(run.State == QmsReviewRunState.Draft && run.Manifest is null,
                "review_inputs_already_frozen", "Capture inputs once; a later period or changed cohort requires a new review.");
            var revision = Revision(definition, run.ProgramRevision); Effective(revision, now);
            QmsReviewInputManifest manifest;
            // Owner facts and metric rows share one actual PostgreSQL repeatable-read snapshot.
            // Current action/source authority remains in the separate locked command UOW.
            await using (var snapshot = await factory.BeginSnapshotAsync(ct))
            {
                var identity = await snapshot.Store<IQmsReviewLookup>().SnapshotIdentityAsync(ct);
                QmsReviewRules.Period(run.PeriodFrom, run.PeriodTo, identity.CapturedAt);
                var capturedDefinition = await definitions.DefinitionAsync(snapshot, definition.Code, ct);
                var capturedRevision = Revision(capturedDefinition, run.ProgramRevision);
                Require(CanonicalJson.Fingerprint(capturedRevision) == CanonicalJson.Fingerprint(revision),
                    "review_program_changed", "The exact program changed before input capture.");
                await definitions.AuthorityAsync(snapshot, context.Principal, capturedDefinition, ct);
                var inputs = new List<QmsReviewFrozenInput>();
                foreach (var agenda in capturedRevision.Content.Program!.Agenda)
                {
                    var sources = new List<QmsPlanningSourceSnapshot>(); var metrics = new List<QmsReviewMetricSnapshot>(); var gaps = new List<string>();
                    foreach (var reference in agenda.Sources.Distinct())
                    {
                        // Missing/hidden evidence stops capture; its identifier/narrative is never persisted as a visible gap.
                        sources.Add(await evidence.ResolveAsync(snapshot, context.Principal, reference, false, ct));
                    }
                    foreach (var reference in agenda.Metrics.Distinct())
                    {
                        var metricOwner = await snapshot.Store<IQmsPlanningLookup>().FindAsync(reference.Code, ct) ?? throw Unavailable();
                        var metricRevision = QmsPlanningService.Revision(metricOwner, reference.Revision);
                        Require(metricOwner.Kind == QmsPlanningKind.MetricDefinition && metricRevision.Content.MetricDefinition is not null,
                            "review_metric_kind", "Select an actual metric definition.");
                        Require(metricRevision.State == QmsState.Effective && metricRevision.EffectiveFrom <= now && (metricRevision.EffectiveTo is null || now < metricRevision.EffectiveTo),
                            "review_metric_not_effective", "The exact metric must be effective at input capture.");
                        await planning.AuthorityAsync(snapshot, context.Principal, metricOwner, ct);
                        sources.Add(await evidence.ResolveAsync(snapshot, context.Principal,
                            new("Quality", "QmsPlanningRevision", reference.Code, reference.Revision.ToString(CultureInfo.InvariantCulture)), false, ct));
                        sources.AddRange(metricRevision.Sources);
                        var measurement = await evidence.MeasureAsync(snapshot, context.Principal, metricRevision.Content.MetricDefinition!, run.PeriodFrom, run.PeriodTo, ct);
                        sources.AddRange(measurement.Evidence.Select(row => row.Source));
                        metrics.Add(new(reference.Code, reference.Revision, metricRevision.Version, CanonicalJson.Fingerprint(metricRevision), measurement));
                        if (measurement.Availability != QmsMetricAvailability.Complete)
                            gaps.Add("Metric " + reference.Code + " is " + measurement.Availability + ": " + measurement.Basis);
                    }
                    var exactSources = sources.DistinctBy(source => CanonicalJson.Serialize(source.Reference)).OrderBy(source => CanonicalJson.Serialize(source.Reference), StringComparer.Ordinal).ToArray();
                    foreach (var source in exactSources.Where(source => source.CapturedCanonicalJson is null))
                        gaps.Add("An actual input owner has retained metadata and a fingerprint but no captured owner-value projection.");
                    var fingerprint = CanonicalJson.Fingerprint(new { agenda.Code, agenda.Topic, agenda.Required, Sources = exactSources, Metrics = metrics, Gaps = gaps });
                    inputs.Add(new(agenda.Code, agenda.Topic, agenda.Required, exactSources, metrics, gaps, fingerprint));
                }
                var contributors = inputs.SelectMany(input => input.Sources).SelectMany(source => source.Contributors)
                    .Concat(Authors(capturedRevision)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                var manifestId = ForgeIds.New(time); var definitionFingerprint = CanonicalJson.Fingerprint(capturedRevision);
                var manifestFingerprint = CanonicalJson.Fingerprint(new { manifestId, run.Id, run.ProgramCode, run.ProgramRevision,
                    ProgramVersion = capturedRevision.Version, definitionFingerprint, run.PeriodFrom, run.PeriodTo,
                    DataCutoff = identity.CapturedAt, identity.SnapshotIdentity, Inputs = inputs, Contributors = contributors, CapturedBy = context.Actor });
                manifest = new(manifestId, run.Id, run.ProgramCode, run.ProgramRevision, capturedRevision.Version,
                    definitionFingerprint, run.PeriodFrom, run.PeriodTo, identity.CapturedAt, context.Actor, now, inputs, contributors,
                    manifestFingerprint, identity.SnapshotIdentity);
            }
            // Lock/recheck actual captured facts and permission-bearing sources before writing the immutable manifest.
            // A concurrent mutation forces a new capture rather than mixing two cohorts.
            await planning.CurrentEvidenceAsync(uow, context.Principal, manifest.Inputs.SelectMany(input => input.Sources), true, ct, true);
            await uow.Store<IQmsReviewStore>().AppendManifestAsync(manifest, ct);
            var next = run with { Version = run.Version + 1, State = QmsReviewRunState.InputsFrozen, Manifest = manifest,
                History = Append(run.History, "FreezeInputs", run.Version + 1, "Draft", "InputsFrozen", "Actual immutable input snapshot", context, now) };
            await uow.Store<IQmsReviewStore>().SaveRunAsync(next, run.Version, ct);
            return definitions.Outcome(definition, "QmsReviewRun", run.Id.ToString("D"), next.Version, "FreezeInputs", next.State.ToString(), run, next, context, RunSources(next));
        }, true, null, ct);
    public Task<CommandResult> RecordMeetingAsync(RecordManagementReviewMeeting c, CommandContext context, CancellationToken ct) =>
        ExecuteAsync(nameof(RecordManagementReviewMeeting), c, c.Id, Permissions.QmsManage, context, async (uow, run, definition, now) =>
        {
            Version(run.Version, c.ExpectedVersion); Require(run.State == QmsReviewRunState.InputsFrozen && run.Manifest is not null,
                "review_meeting_state", "Record human judgments against the immutable frozen inputs before submission.");
            Text(c.MeetingRecord); Participants(c.Participants, Revision(definition, run.ProgramRevision));
            foreach (var person in c.Participants) Require(await uow.People.FindAsync(new PersonId(person), ct) is { IsActive: true }, "review_participant", "Select actual active participants.");
            QmsReviewRules.ValidateAssessments(run.Manifest!, c.Assessments); QmsReviewRules.ValidateDecisions(c.Decisions);
            var assessments = new List<QmsReviewInputAssessment>();
            foreach (var assessment in c.Assessments)
                assessments.Add(assessment with { CapturedEvidence = await ResolveAsync(uow, context.Principal, assessment.AdditionalEvidence, ct) });
            var decisions = new List<QmsManagementReviewDecision>();
            foreach (var decision in c.Decisions)
            {
                if (decision.OwnerPersonId is { } owner) Require(await uow.People.FindAsync(new PersonId(owner), ct) is { IsActive: true }, "review_action_owner", "Select an actual active action owner.");
                decisions.Add(decision with { CapturedSources = await ResolveAsync(uow, context.Principal, decision.SourceActionReferences, ct) });
            }
            var next = run with { Version = run.Version + 1, MeetingRecord = c.MeetingRecord.Trim(), Participants = c.Participants,
                Assessments = assessments, Decisions = decisions, Limitations = c.Limitations,
                History = Append(run.History, "RecordMeeting", run.Version + 1, run.State.ToString(), run.State.ToString(), "Attributed human assessments and decisions", context, now) };
            await uow.Store<IQmsReviewStore>().SaveRunAsync(next, run.Version, ct);
            return definitions.Outcome(definition, "QmsReviewRun", run.Id.ToString("D"), next.Version, "RecordMeeting", next.State.ToString(), run, next, context, RunSources(next));
        }, true, async (uow, run, definition, now) =>
        {
            foreach (var assessment in c.Assessments) await ResolveAsync(uow, context.Principal, assessment.AdditionalEvidence, ct);
            foreach (var decision in c.Decisions) await ResolveAsync(uow, context.Principal, decision.SourceActionReferences, ct);
        }, ct);
    public Task<CommandResult> TransitionAsync(TransitionManagementReview c, CommandContext context, CancellationToken ct) =>
        ExecuteAsync("ManagementReview" + c.Action, c, c.Id, Permission(c.Action), context, async (uow, run, definition, now) =>
        {
            Version(run.Version, c.ExpectedVersion); Text(c.Note); var nextState = Next(run.State, c.Action);
            if (c.Action is "Submit" or "Review" or "Approve")
            {
                Require(run.Manifest is not null, "review_inputs_required", "Freeze actual inputs first.");
                Text(run.MeetingRecord); Participants(run.Participants, Revision(definition, run.ProgramRevision));
                QmsReviewRules.ValidateAssessments(run.Manifest!, run.Assessments); QmsReviewRules.ValidateDecisions(run.Decisions);
            }
            if (c.Action is "Review" or "Approve") QmsReviewRules.Independent(context.Actor, RunContributors(run));
            if (c.Action == "Approve") foreach (var decision in run.Decisions.Where(decision => decision.OwnerPersonId is not null))
            {
                var owner = decision.OwnerPersonId!.Value;
                Require(await uow.People.FindAsync(new PersonId(owner), ct) is { IsActive: true }, "review_action_owner", "The actual action owner must remain active.");
                var action = new QmsReviewAction(ForgeIds.New(time), run.Id, decision.Code, 1, QmsReviewActionState.Open,
                    owner, decision.DueAt!.Value, decision.Decision, decision.CompletionCriteria, decision.Resources, context.Actor, now, [], null, null, null, "",
                    Append([], "Create", 1, "", "Open", "Independent review decision assigned this action", context, now));
                await uow.Store<IQmsReviewStore>().SaveActionAsync(action, null, ct);
                await uow.Outbox.EnqueueAsync(pipeline.Event(new QmsReviewChanged("QmsReviewAction", action.Id.ToString("D"), 1, "Create", "Open"), context, "Quality", "QmsReviewAction", action.Id.ToString("D"), QmsPlanningService.Program(definition.ProgramId), QmsPlanningService.Marks(definition.Markings, RunSources(run))), ct);
            }
            if (c.Action == "Close") Require((await uow.Store<IQmsReviewLookup>().ActionsAsync(run.Id, ct)).All(action => action.State is QmsReviewActionState.Verified or QmsReviewActionState.Withdrawn),
                "review_actions_open", "Verify or independently withdraw each assigned action before closure.");
            var next = run with { Version = run.Version + 1, State = nextState,
                ReviewedBy = c.Action == "Review" ? context.Actor : run.ReviewedBy,
                ApprovedBy = c.Action == "Approve" ? context.Actor : run.ApprovedBy, ApprovedAt = c.Action == "Approve" ? now : run.ApprovedAt,
                History = Append(run.History, c.Action, run.Version + 1, run.State.ToString(), nextState.ToString(), c.Note, context, now) };
            await uow.Store<IQmsReviewStore>().SaveRunAsync(next, run.Version, ct);
            return definitions.Outcome(definition, "QmsReviewRun", run.Id.ToString("D"), next.Version, c.Action, next.State.ToString(), run, next, context, RunSources(next));
        }, c.Action != "Withdraw", null, ct);
    internal Task<CommandResult> ExecuteAsync<T>(string command, T c, Guid id, string permission, CommandContext context,
        Func<IKernelUnitOfWork, QmsManagementReviewRun, QmsReviewDefinition, DateTimeOffset, Task<CommandOutcome>> body,
        bool requireEffective,
        Func<IKernelUnitOfWork, QmsManagementReviewRun, QmsReviewDefinition, DateTimeOffset, Task>? beforeReplay,
        CancellationToken ct) =>
        pipeline.ExecuteAsync(new("Quality", command, permission, ResourceRef.Of("QmsReviewRun", id.ToString("D")), true), c, context,
            async (uow, now) =>
            {
                var run = await RunAsync(uow, id, ct); var definition = await definitions.DefinitionAsync(uow, run.ProgramCode, ct);
                definitions.Permit(context.Principal, permission, Resource(definition) with { Type = "QmsReviewRun", Id = id.ToString("D") });
                return await body(uow, run, definition, now);
            }, ct, async (uow, now) =>
            {
                var preliminary = await RunAsync(uow, id, ct);
                await uow.Store<IQmsReviewStore>().LockAsync(preliminary.ProgramCode, ct);
                await uow.Store<IQmsReviewStore>().LockAsync("Run:" + id.ToString("D"), ct);
                var run = await RunAsync(uow, id, ct); var definition = await definitions.DefinitionAsync(uow, run.ProgramCode, ct);
                var authority = await AuthorityAsync(uow, context.Principal, run, ct, true);
                if (requireEffective)
                {
                    var revision = Revision(definition, run.ProgramRevision); Effective(revision, now);
                    await definitions.SourcesAsync(uow, context.Principal, definition.Kind, revision.Content, true, now, ct);
                }
                if (beforeReplay is not null) await beforeReplay(uow, run, definition, now);
                return authority;
            });
    public async Task<CommandAuthority> AuthorityAsync(IKernelUnitOfWork uow, ForgePrincipal principal, QmsManagementReviewRun run, CancellationToken ct, bool lockSources = false)
    {
        var definition = await definitions.DefinitionAsync(uow, run.ProgramCode, ct);
        await definitions.AuthorityAsync(uow, principal, definition, ct, lockSources);
        await planning.CurrentEvidenceAsync(uow, principal, RunSources(run), false, ct, lockSources);
        foreach (var action in await uow.Store<IQmsReviewLookup>().ActionsAsync(run.Id, ct))
            await planning.CurrentEvidenceAsync(uow, principal, action.Evidence, false, ct, lockSources);
        var resource = Resource(definition) with { Type = "QmsReviewRun", Id = run.Id.ToString("D"), Markings = QmsPlanningService.Marks(definition.Markings, RunSources(run)) };
        definitions.Permit(principal, Permissions.QmsRead, resource); return new(resource, definition.OrganizationCode + ":" + run.Id.ToString("D"));
    }
    public Task<CommandResult> TransitionActionAsync(TransitionManagementReviewAction c, CommandContext context, CancellationToken ct)
    {
        var permission = c.Action switch
        {
            "Complete" or "Reopen" => Permissions.QmsManage,
            "Verify" or "Withdraw" => Permissions.QmsApprove,
            _ => throw new CommandRejectedException("review_action_transition", "Unknown action transition.")
        };
        return pipeline.ExecuteAsync(new("Quality", "ReviewAction" + c.Action, permission, ResourceRef.Of("QmsReviewAction", c.Id.ToString("D")), true), c, context,
            async (uow, now) =>
            {
                var action = await ActionAsync(uow, c.Id, ct); var run = await RunAsync(uow, action.ReviewId, ct);
                var definition = await definitions.DefinitionAsync(uow, run.ProgramCode, ct);
                Version(action.Version, c.ExpectedVersion); Text(c.Result);
                var sources = await ResolveAsync(uow, context.Principal, c.Evidence, ct);
                var state = (action.State, c.Action) switch
                {
                    (QmsReviewActionState.Open or QmsReviewActionState.Reopened, "Complete") => QmsReviewActionState.Completed,
                    (QmsReviewActionState.Completed, "Verify") => QmsReviewActionState.Verified,
                    (QmsReviewActionState.Completed or QmsReviewActionState.Verified, "Reopen") => QmsReviewActionState.Reopened,
                    (_, "Withdraw") when action.State != QmsReviewActionState.Withdrawn => QmsReviewActionState.Withdrawn,
                    _ => throw new CommandRejectedException("review_action_state", "The action is not in the required state.")
                };
                if (c.Action == "Complete") Require(context.Principal.PersonId.Value == action.OwnerPersonId,
                    "review_action_actual_owner", "The actual assigned owner must record completion.");
                if (c.Action is "Verify" or "Withdraw")
                    QmsReviewRules.Independent(context.Actor, new[] { action.CompletedBy ?? action.CreatedBy }
                        .Concat(action.Evidence.Concat(sources).SelectMany(source => source.Contributors)));
                if (c.Action is "Complete" or "Verify") Require(sources.Count != 0,
                    "review_action_evidence", "Record actual evidence against the approved completion criteria.");
                var next = action with { Version = action.Version + 1, State = state, Result = c.Result.Trim(),
                    Evidence = action.Evidence.Concat(sources).DistinctBy(source => CanonicalJson.Fingerprint(source)).ToArray(),
                    CompletedBy = c.Action == "Complete" ? context.Actor : c.Action == "Reopen" ? null : action.CompletedBy,
                    VerifiedBy = c.Action == "Verify" ? context.Actor : c.Action == "Reopen" ? null : action.VerifiedBy,
                    VerifiedAt = c.Action == "Verify" ? now : c.Action == "Reopen" ? null : action.VerifiedAt,
                    History = Append(action.History, c.Action, action.Version + 1, action.State.ToString(), state.ToString(), c.Result, context, now) };
                await uow.Store<IQmsReviewStore>().SaveActionAsync(next, action.Version, ct);
                if (c.Action == "Reopen" && run.State == QmsReviewRunState.Closed)
                {
                    var reopened = run with { Version = run.Version + 1, State = QmsReviewRunState.Approved,
                        History = Append(run.History, "ActionReopened", run.Version + 1, "Closed", "Approved", "A verified action was reopened; closure needs a new decision", context, now) };
                    await uow.Store<IQmsReviewStore>().SaveRunAsync(reopened, run.Version, ct);
                    await uow.Outbox.EnqueueAsync(pipeline.Event(new QmsReviewChanged("QmsReviewRun", run.Id.ToString("D"), reopened.Version, "ActionReopened", "Approved"), context, "Quality", "QmsReviewRun", run.Id.ToString("D"), QmsPlanningService.Program(definition.ProgramId), QmsPlanningService.Marks(definition.Markings, RunSources(run))), ct);
                }
                return definitions.Outcome(definition, "QmsReviewAction", action.Id.ToString("D"), next.Version, c.Action, state.ToString(), action, next, context, RunSources(run).Concat(next.Evidence));
            }, ct, async (uow, now) =>
            {
                var preliminary = await ActionAsync(uow, c.Id, ct); var preliminaryRun = await RunAsync(uow, preliminary.ReviewId, ct);
                await uow.Store<IQmsReviewStore>().LockAsync(preliminaryRun.ProgramCode, ct);
                await uow.Store<IQmsReviewStore>().LockAsync("Run:" + preliminary.ReviewId.ToString("D"), ct);
                await uow.Store<IQmsReviewStore>().LockAsync("Action:" + c.Id.ToString("D"), ct);
                var action = await ActionAsync(uow, c.Id, ct); var run = await RunAsync(uow, action.ReviewId, ct);
                var authority = await AuthorityAsync(uow, context.Principal, run, ct, true);
                await planning.CurrentEvidenceAsync(uow, context.Principal, action.Evidence, false, ct, true);
                await ResolveAsync(uow, context.Principal, c.Evidence, ct);
                // Only the immediately accepted original transition can replay. A later verify/reopen cannot return an obsolete completion.
                Require(action.Version == c.ExpectedVersion || action.Version == c.ExpectedVersion + 1
                    && action.History.Count > 0 && action.History[^1] is { } last && last.Action == c.Action && last.Actor == context.Actor,
                    "review_action_generation_changed", "The action changed after this transition; read its current version.");
                Require(run.State is QmsReviewRunState.Approved or QmsReviewRunState.Closed, "review_action_parent_state", "The parent review must have an approved decision.");
                if (c.Action == "Complete") Require(context.Principal.PersonId.Value == action.OwnerPersonId,
                    "review_action_actual_owner", "The actual assigned owner must record completion.");
                return authority with { Resource = authority.Resource with { Type = "QmsReviewAction", Id = action.Id.ToString("D"), Markings = QmsPlanningService.Marks(authority.Resource.Markings, action.Evidence) } };
            });
    }
    private static async Task<QmsReviewAction> ActionAsync(IKernelUnitOfWork uow, Guid id, CancellationToken ct) => await uow.Store<IQmsReviewLookup>().FindActionAsync(id, ct) ?? throw Unavailable();
    internal async Task<IReadOnlyList<QmsPlanningSourceSnapshot>> ResolveAsync(IKernelUnitOfWork uow, ForgePrincipal principal, IReadOnlyList<QmsPlanningEvidenceReference> references, CancellationToken ct)
    {
        Require(references is not null && references.Count <= 200 && references.All(reference => reference is not null), "review_evidence_list", "Select a finite actual evidence set.");
        var sources = new List<QmsPlanningSourceSnapshot>();
        foreach (var reference in references) sources.Add(await evidence.ResolveAsync(uow, principal, reference, true, ct));
        return sources;
    }
    internal static async Task<QmsManagementReviewRun> RunAsync(IKernelUnitOfWork uow, Guid id, CancellationToken ct) => await uow.Store<IQmsReviewLookup>().FindRunAsync(id, ct) ?? throw Unavailable();
    internal static IEnumerable<QmsPlanningSourceSnapshot> RunSources(QmsManagementReviewRun run) =>
        (run.Manifest?.Inputs.SelectMany(input => input.Sources) ?? []).Concat(run.Assessments.SelectMany(assessment => assessment.CapturedEvidence ?? [])).Concat(run.Decisions.SelectMany(decision => decision.CapturedSources ?? []));
    internal static IEnumerable<string> RunContributors(QmsManagementReviewRun run) => new[] { run.CreatedBy, run.Manifest?.CapturedBy ?? run.CreatedBy }
        .Concat(run.History.Where(transition => transition.Action == "RecordMeeting").Select(transition => transition.Actor))
        .Concat(run.Manifest?.Contributors ?? []).Concat(RunSources(run).SelectMany(source => source.Contributors));
    private static void Participants(IReadOnlyList<Guid> participants, QmsReviewDefinitionRevision revision)
    {
        Require(participants is not null && participants.Count is > 0 and <= 200 && participants.All(person => person != Guid.Empty)
            && participants.Distinct().Count() == participants.Count, "review_participants", "Record actual distinct registered participants.");
        Require(revision.Content.Program!.RequiredParticipantPersonIds.All(participants.Contains), "review_required_participants", "Record every exact required participant from the approved program.");
    }
    private static string Permission(string action) => action switch { "Submit" => Permissions.QmsManage, "Review" => Permissions.QmsReview,
        "Approve" or "Close" => Permissions.QmsApprove, "Withdraw" => Permissions.QmsPublish, _ => throw new CommandRejectedException("review_action", "Unknown review action.") };
    private static QmsReviewRunState Next(QmsReviewRunState state, string action) => (state, action) switch
    {
        (QmsReviewRunState.InputsFrozen, "Submit") => QmsReviewRunState.Submitted,
        (QmsReviewRunState.Submitted, "Review") => QmsReviewRunState.Reviewed,
        (QmsReviewRunState.Reviewed, "Approve") => QmsReviewRunState.Approved,
        (QmsReviewRunState.Approved, "Close") => QmsReviewRunState.Closed,
        (_, "Withdraw") when state is not (QmsReviewRunState.Closed or QmsReviewRunState.Withdrawn) => QmsReviewRunState.Withdrawn,
        _ => throw new CommandRejectedException("review_transition", "The actual review is not in the required state.")
    };
}
