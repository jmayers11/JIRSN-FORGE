using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;
using Forge.Quality.Planning;
using Forge.Quality.Qms;

namespace Forge.Quality.Review;

public sealed record CreateReviewDefinition(string Code, QmsReviewDefinitionKind Kind, string FacilityCode,
    Guid? ProgramId, DataMarking Markings, QmsReviewDefinitionContent Content);
public sealed record ReviseReviewDefinition(string Code, int? Revision, int ExpectedVersion, QmsReviewDefinitionContent Content);
public sealed record TransitionReviewDefinition(string Code, int Revision, int ExpectedVersion, string Action, string Note);
public sealed record QmsReviewChanged(string EntityType, string EntityId, int Version, string Action, string State) : IDomainEvent
{
    public static string EventType => nameof(QmsReviewChanged);
    public static int SchemaVersion => 1;
}

public sealed class QmsReviewService(CommandPipeline pipeline, IKernelUnitOfWorkFactory factory,
    QmsOptions options, QmsPlanningService planning, QmsService qms, IQmsPlanningEvidenceReader evidence)
{
    public async Task<QmsReviewDefinition> ReadAsync(ForgePrincipal principal, string code, CancellationToken ct)
    {
        await using var uow = await factory.BeginSnapshotAsync(ct);
        var definition = await DefinitionAsync(uow, code, ct);
        if (!await CanReadAsync(uow, principal, definition, ct)) throw Unavailable();
        return definition;
    }
    public async Task<QmsReviewPage<QmsReviewDefinition>> ListAsync(ForgePrincipal principal, QmsReviewDefinitionKind? kind, int take, string? after, CancellationToken ct)
    {
        Require(take is > 0 and <= 100, "review_page", "Choose a page size from 1 to 100.");
        await using var uow = await factory.BeginSnapshotAsync(ct);
        var result = new List<QmsReviewDefinition>(); var cursor = after;
        while (true)
        {
            var page = await uow.Store<IQmsReviewLookup>().DefinitionsAsync(kind, 100, cursor, ct);
            foreach (var definition in page.Items)
                if (await CanReadAsync(uow, principal, definition, ct))
                {
                    if (result.Count == take) return new(result, result[^1].Code);
                    result.Add(definition);
                }
            if (page.NextCursor is null) return new(result, null);
            cursor = page.NextCursor;
        }
    }
    public Task<CommandResult> CreateAsync(CreateReviewDefinition c, CommandContext context, CancellationToken ct)
    {
        var code = BusinessCode.Require(c.Code, "Review definition");
        var facility = BusinessCode.Require(c.FacilityCode, "Facility");
        var resource = new ResourceRef("QmsReviewDefinition", code, QmsPlanningService.Program(c.ProgramId), c.Markings, FacilityId: facility);
        return pipeline.ExecuteAsync(new("Quality", nameof(CreateReviewDefinition), Permissions.QmsManage, resource, true), c, context,
            async (uow, now) =>
            {
                await uow.Store<IQmsReviewStore>().LockAsync(code, ct);
                Require(await uow.Store<IQmsReviewLookup>().FindDefinitionAsync(code, ct) is null, "review_exists", "This definition already exists.");
                await planning.HeaderAsync(uow, facility, c.ProgramId, c.Markings, ct);
                var sources = await SourcesAsync(uow, context.Principal, c.Kind, c.Content, false, now, ct);
                var revision = Draft(code, 1, c.Content, sources, context, now);
                var definition = new QmsReviewDefinition(code, c.Kind, options.OrganizationCode!, facility, c.ProgramId, c.Markings, context.Actor, now, [revision]);
                await uow.Store<IQmsReviewStore>().InsertDefinitionAsync(definition, ct);
                await uow.Store<IQmsReviewStore>().SaveDefinitionRevisionAsync(revision, null, ct);
                return Outcome(definition, "QmsReviewRevision", code + "/1", 1, "Create", "Draft", null, revision, context, sources);
            }, ct, async (uow, now) =>
            {
                await uow.Store<IQmsReviewStore>().LockAsync(code, ct);
                if (await uow.Store<IQmsReviewLookup>().FindDefinitionAsync(code, ct) is { } existing)
                    return await AuthorityAsync(uow, context.Principal, existing, ct, true);
                await planning.HeaderAsync(uow, facility, c.ProgramId, c.Markings, ct);
                await SourcesAsync(uow, context.Principal, c.Kind, c.Content, false, now, ct);
                return new CommandAuthority(resource, options.OrganizationCode + ":" + code);
            });
    }
    public Task<CommandResult> ReviseAsync(ReviseReviewDefinition c, CommandContext context, CancellationToken ct) =>
        ExecuteAsync(nameof(ReviseReviewDefinition), c, c.Code, Permissions.QmsManage, context, async (uow, definition, now) =>
        {
            var old = c.Revision is { } number ? Revision(definition, number) : definition.Revisions[^1];
            Version(old.Version, c.ExpectedVersion); var edit = c.Revision is not null;
            Require(edit ? old.State == QmsState.Draft : !definition.Revisions.Any(revision => revision.State is QmsState.Draft or QmsState.Submitted or QmsState.Reviewed or QmsState.Approved),
                "review_open_revision", "Edit only draft content or create a successor after the open proposal is completed.");
            var sources = await SourcesAsync(uow, context.Principal, definition.Kind, c.Content, false, now, ct);
            var next = edit ? old with { Version = old.Version + 1, Content = c.Content, LastEditedBy = context.Actor,
                Sources = sources, RequiresLicensedVerification = RequiresLicensed(c.Content, sources),
                History = Append(old.History, "Edit", old.Version + 1, "Draft", "Draft", "Human content revision", context, now) }
                : Draft(definition.Code, old.Revision + 1, c.Content, sources, context, now);
            await uow.Store<IQmsReviewStore>().SaveDefinitionRevisionAsync(next, edit ? old.Version : null, ct);
            return Outcome(definition, "QmsReviewRevision", definition.Code + "/" + next.Revision, next.Version, edit ? "Edit" : "NewRevision", "Draft", edit ? old : null, next, context, sources);
        }, ct, async (uow, definition, now) => { await SourcesAsync(uow, context.Principal, definition.Kind, c.Content, false, now, ct); });
    public Task<CommandResult> TransitionAsync(TransitionReviewDefinition c, CommandContext context, CancellationToken ct) =>
        ExecuteAsync("ReviewDefinition" + c.Action, c, c.Code, QmsPlanningService.Permission(c.Action), context, async (uow, definition, now) =>
        {
            var old = Revision(definition, c.Revision); Version(old.Version, c.ExpectedVersion); Text(c.Note);
            var state = QmsPlanningRules.Transition(old.State, c.Action);
            var sources = await SourcesAsync(uow, context.Principal, definition.Kind, old.Content, c.Action != "Withdraw", now, ct);
            if (c.Action is "Review" or "Approve") QmsReviewRules.Independent(context.Actor, Authors(old).Concat(sources.SelectMany(source => source.Contributors)));
            var next = old with { Version = old.Version + 1, State = state, Sources = sources,
                RequiresLicensedVerification = RequiresLicensed(old.Content, sources),
                ReviewedBy = c.Action == "Review" ? context.Actor : old.ReviewedBy,
                ApprovedBy = c.Action == "Approve" ? context.Actor : old.ApprovedBy,
                EffectiveFrom = c.Action == "Effective" ? now : old.EffectiveFrom,
                EffectiveTo = c.Action == "Withdraw" && old.EffectiveFrom is not null ? now : old.EffectiveTo,
                History = Append(old.History, c.Action, old.Version + 1, old.State.ToString(), state.ToString(), c.Note, context, now) };
            if (c.Action == "Effective") foreach (var prior in definition.Revisions.Where(revision => revision.State == QmsState.Effective))
            {
                var closed = prior with { Version = prior.Version + 1, State = QmsState.Superseded, EffectiveTo = now,
                    History = Append(prior.History, "Supersede", prior.Version + 1, "Effective", "Superseded", "Superseded by revision " + next.Revision, context, now) };
                await uow.Store<IQmsReviewStore>().SaveDefinitionRevisionAsync(closed, prior.Version, ct);
                await uow.Outbox.EnqueueAsync(pipeline.Event(new QmsReviewChanged("QmsReviewRevision", definition.Code + "/" + closed.Revision, closed.Version, "Supersede", "Superseded"), context, "Quality", "QmsReviewRevision", definition.Code + "/" + closed.Revision, QmsPlanningService.Program(definition.ProgramId), QmsPlanningService.Marks(definition.Markings, closed.Sources)), ct);
            }
            await uow.Store<IQmsReviewStore>().SaveDefinitionRevisionAsync(next, old.Version, ct);
            return Outcome(definition, "QmsReviewRevision", definition.Code + "/" + next.Revision, next.Version, c.Action, state.ToString(), old, next, context, sources);
        }, ct, async (uow, definition, now) =>
        {
            var revision = Revision(definition, c.Revision);
            await SourcesAsync(uow, context.Principal, definition.Kind, revision.Content, c.Action != "Withdraw", now, ct);
            if (c.Action is "Review" or "Approve") QmsReviewRules.Independent(context.Actor,
                Authors(revision).Concat(revision.Sources.SelectMany(source => source.Contributors)));
        });
    internal Task<CommandResult> ExecuteAsync<T>(string command, T c, string code, string permission, CommandContext context,
        Func<IKernelUnitOfWork, QmsReviewDefinition, DateTimeOffset, Task<CommandOutcome>> body, CancellationToken ct,
        Func<IKernelUnitOfWork, QmsReviewDefinition, DateTimeOffset, Task>? beforeReplay = null) =>
        pipeline.ExecuteAsync(new("Quality", command, permission, ResourceRef.Of("QmsReviewDefinition", code), true), c, context,
            async (uow, now) =>
            {
                var definition = await DefinitionAsync(uow, code, ct);
                await AuthorityAsync(uow, context.Principal, definition, ct, true);
                Permit(context.Principal, permission, Resource(definition));
                return await body(uow, definition, now);
            }, ct, async (uow, now) =>
            {
                await uow.Store<IQmsReviewStore>().LockAsync(code, ct);
                var definition = await DefinitionAsync(uow, code, ct);
                var authority = await AuthorityAsync(uow, context.Principal, definition, ct, true);
                if (beforeReplay is not null) await beforeReplay(uow, definition, now);
                return authority;
            });
    public async Task<bool> CanReadAsync(IKernelUnitOfWork uow, ForgePrincipal principal, QmsReviewDefinition definition, CancellationToken ct)
    {
        try { await AuthorityAsync(uow, principal, definition, ct); return true; }
        catch (ForgeAuthorizationException) { return false; } catch (CommandRejectedException) { return false; }
    }
    internal async Task<CommandAuthority> AuthorityAsync(IKernelUnitOfWork uow, ForgePrincipal principal, QmsReviewDefinition definition, CancellationToken ct, bool lockSources = false)
    {
        Require(options.Configured && definition.OrganizationCode == options.OrganizationCode, "review_not_found", "The review source is unavailable.");
        Permit(principal, Permissions.QmsRead, Resource(definition));
        var sources = definition.Revisions.SelectMany(revision => revision.Sources).ToArray();
        var current = await planning.CurrentEvidenceAsync(uow, principal, sources, false, ct, lockSources);
        return new(Resource(definition) with { Markings = QmsPlanningService.Marks(definition.Markings, sources.Concat(current)) }, definition.OrganizationCode + ":" + definition.Code);
    }
    internal async Task<QmsReviewDefinition> DefinitionAsync(IKernelUnitOfWork uow, string code, CancellationToken ct) =>
        await uow.Store<IQmsReviewLookup>().FindDefinitionAsync(BusinessCode.Require(code, "Review definition"), ct) is { } definition
            && definition.OrganizationCode == options.OrganizationCode ? definition : throw Unavailable();
    internal async Task<IReadOnlyList<QmsPlanningSourceSnapshot>> SourcesAsync(IKernelUnitOfWork uow, ForgePrincipal principal,
        QmsReviewDefinitionKind kind, QmsReviewDefinitionContent content, bool requireEffective, DateTimeOffset now, CancellationToken ct)
    {
        QmsReviewRules.Validate(kind, content); var sources = new List<QmsPlanningSourceSnapshot>();
        var owner = content.Program?.OwnerPersonId ?? content.Mapping!.ProcessOwnerPersonId;
        Require(await uow.People.FindAsync(new PersonId(owner), ct) is { IsActive: true }, "review_owner", "Select an active registered owner.");
        foreach (var document in (content.Documents ?? []).Concat(content.Mapping?.ImplementingProcedures ?? []).Concat(content.Mapping?.Criteria.Where(criterion => criterion.VerificationDocument is not null).Select(criterion => criterion.VerificationDocument!) ?? []).Distinct())
        {
            var actual = await uow.Store<IDocumentLookup>().FindDocumentAsync(document.DocumentNumber, ct) ?? throw Unavailable();
            var revision = actual.Revisions.FirstOrDefault(version => version.Revision == document.Revision) ?? throw Unavailable();
            if (requireEffective) Require(actual.Current?.Revision == document.Revision && revision.Status is DocumentRevisionStatus.Released or DocumentRevisionStatus.Applicable
                && (revision.EffectiveFrom is null || revision.EffectiveFrom <= DateOnly.FromDateTime(now.UtcDateTime))
                && (revision.EffectiveTo is null || DateOnly.FromDateTime(now.UtcDateTime) <= revision.EffectiveTo), "review_document_not_current", "Use an exact currently applicable controlled document.");
            sources.Add(await evidence.ResolveAsync(uow, principal, new("Engineering", "DocumentRevision", document.DocumentNumber, document.Revision), true, ct));
        }
        foreach (var (reference, expectedKind) in (content.Mapping?.Processes ?? []).Select(reference => (reference, QmsRecordKind.Process))
            .Concat((content.Mapping?.Requirements ?? []).Select(reference => (reference, QmsRecordKind.Requirement))))
        {
            var actual = await uow.Store<IQmsLookup>().FindAsync(reference.Code, ct) ?? throw Unavailable();
            var revision = actual.Revisions.FirstOrDefault(version => version.Revision == reference.Revision) ?? throw Unavailable();
            Require(actual.Kind == expectedKind && await qms.CanReadAsync(uow, principal, actual, ct, reference.Revision), "review_definition_source", "Use the actual accessible source of the required kind.");
            if (requireEffective) Require(revision.State == QmsState.Effective && revision.EffectiveFrom <= now && (revision.EffectiveTo is null || now < revision.EffectiveTo), "review_source_not_effective", "Use the exact currently effective QMS source.");
            sources.Add(await evidence.ResolveAsync(uow, principal, new("Quality", "QmsRevision", reference.Code, reference.Revision.ToString(CultureInfo.InvariantCulture)), true, ct));
        }
        foreach (var item in content.Program?.Agenda ?? [])
        {
            foreach (var reference in item.Sources.Distinct()) sources.Add(await evidence.ResolveAsync(uow, principal, reference, true, ct));
            foreach (var reference in item.Metrics.Distinct())
            {
                var actual = await uow.Store<IQmsPlanningLookup>().FindAsync(reference.Code, ct) ?? throw Unavailable();
                var revision = QmsPlanningService.Revision(actual, reference.Revision);
                Require(actual.Kind == QmsPlanningKind.MetricDefinition && revision.Content.MetricDefinition is not null, "review_metric_kind", "Select an exact metric definition.");
                await planning.AuthorityAsync(uow, principal, actual, ct, true);
                if (requireEffective) Require(revision.State == QmsState.Effective && revision.EffectiveFrom <= now && (revision.EffectiveTo is null || now < revision.EffectiveTo), "review_metric_not_effective", "Use the exact currently effective metric definition.");
                sources.Add(await evidence.ResolveAsync(uow, principal, new("Quality", "QmsPlanningRevision", reference.Code, reference.Revision.ToString(CultureInfo.InvariantCulture)), true, ct));
            }
        }
        return sources.DistinctBy(source => CanonicalJson.Serialize(source.Reference)).OrderBy(source => CanonicalJson.Serialize(source.Reference), StringComparer.Ordinal).ToArray();
    }
    internal void Permit(ForgePrincipal principal, string permission, ResourceRef resource) => planning.Permit(principal, permission, resource);
    internal CommandOutcome Outcome(QmsReviewDefinition definition, string type, string id, int version, string action, string state, object? old, object next, CommandContext context, IEnumerable<QmsPlanningSourceSnapshot> sources) =>
        new(CommandResult.Ok(id, type + ": " + state + "."), [pipeline.Event(new QmsReviewChanged(type, id, version, action, state), context, "Quality", type, id, QmsPlanningService.Program(definition.ProgramId), QmsPlanningService.Marks(definition.Markings, sources))],
            new(type, id, old is null ? null : CanonicalJson.Serialize(old), CanonicalJson.Serialize(next), QmsPlanningService.Program(definition.ProgramId), QmsPlanningService.Marks(definition.Markings, sources)));
    public static ResourceRef Resource(QmsReviewDefinition definition) => new("QmsReviewDefinition", definition.Code, QmsPlanningService.Program(definition.ProgramId), definition.Markings, FacilityId: definition.FacilityCode);
    internal static QmsReviewDefinitionRevision Revision(QmsReviewDefinition definition, int number) => definition.Revisions.FirstOrDefault(revision => revision.Revision == number) ?? throw Unavailable();
    internal static void Effective(QmsReviewDefinitionRevision revision, DateTimeOffset now) => Require(revision.State == QmsState.Effective && revision.EffectiveFrom <= now && (revision.EffectiveTo is null || now < revision.EffectiveTo), "review_program_not_effective", "The exact review definition must currently be effective.");
    internal static void Version(int actual, int expected) => Require(actual == expected, "stale_version", "The source changed; read its current version.");
    internal static void Text(string? value) => Require(!string.IsNullOrWhiteSpace(value) && value.Length <= 4000, "review_text", "Record a bounded explicit human note.");
    internal static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool allowed, string code, string message) { if (!allowed) throw new CommandRejectedException(code, message); }
    internal static CommandRejectedException Unavailable() => new("review_not_found", "The review or its evidence is unavailable.");
    internal static IReadOnlyList<QmsReviewTransition> Append(IReadOnlyList<QmsReviewTransition> history, string action, int version, string from, string to, string basis, CommandContext context, DateTimeOffset now) =>
        history.Append(new QmsReviewTransition(action, version, context.Actor, now, from, to, basis, context.Reason!.Trim())).ToArray();
    internal static IEnumerable<string> Authors(QmsReviewDefinitionRevision revision) => new[] { revision.CreatedBy, revision.LastEditedBy }.Concat(revision.History.Where(transition => transition.Action is "Create" or "Edit").Select(transition => transition.Actor));
    private static bool RequiresLicensed(QmsReviewDefinitionContent content, IEnumerable<QmsPlanningSourceSnapshot> sources) => sources.Any(source => source.RequiresLicensedVerification) || (content.Mapping?.Criteria.Any(criterion => criterion.Verification != QmsStandardVerification.VerifiedAuthorizedCopy) ?? false);
    private static QmsReviewDefinitionRevision Draft(string code, int number, QmsReviewDefinitionContent content, IReadOnlyList<QmsPlanningSourceSnapshot> sources, CommandContext context, DateTimeOffset now) =>
        new(code, number, 1, QmsState.Draft, content, context.Actor, context.Actor, null, null, null, null, RequiresLicensed(content, sources), sources,
            Append([], "Create", 1, "", "Draft", "Human-defined proposal", context, now));
}
