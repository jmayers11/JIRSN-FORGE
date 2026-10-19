using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Programs.Contracts;
using Forge.Quality.Contracts;
using Forge.Resources.Contracts;

namespace Forge.Quality.Qms;

public sealed record CreateQmsRecord(string Code, QmsRecordKind Kind, string FacilityCode, Guid? ProgramId, DataMarking Markings, QmsContent Content);
public sealed record CreateQmsRevision(string Code, int ExpectedVersion, QmsContent Content);
public sealed record EditQmsRevision(string Code, int Revision, int ExpectedVersion, QmsContent Content);
public sealed record TransitionQmsRevision(string Code, int Revision, int ExpectedVersion, string Action, string Note);
public sealed record QmsRevisionChanged(string Code, int Revision, int Version, string Action, QmsState State) : IDomainEvent
{
    public static string EventType => nameof(QmsRevisionChanged);
    public static int SchemaVersion => 1;
}

/// <summary>
/// Governed organization definitions, with explicit human context and exact source revisions. Local approval
/// does not establish ISO conformity or licensed normative verification, nor disable existing execution gates.
/// </summary>
public sealed class QmsService(CommandPipeline pipeline, IKernelUnitOfWorkFactory factory, IForgeAuthorizer authorizer, QmsOptions options)
{
    public const string ConfigurationKey = "Forge:Qms:Organization";
    public object Status => new { configured = options.Configured, organizationCode = options.OrganizationCode };

    public async Task<IReadOnlyList<QmsRecordView>> ListAsync(ForgePrincipal principal, QmsRecordKind? kind, CancellationToken ct)
    {
        Configured();
        if (!MayRead(principal)) throw new ForgeAuthorizationException(authorizer.Authorize(principal, Permissions.QmsRead, ResourceRef.Of("QmsRecord")));
        await using var uow = await factory.BeginSnapshotAsync(ct);
        var visible = new List<QmsRecordView>();
        foreach (var record in await uow.Store<IQmsLookup>().ListAsync(kind, ct))
            if (await CanReadAsync(uow, principal, record, ct)) visible.Add(record);
        return visible;
    }

    public async Task<QmsRecordView> ReadAsync(ForgePrincipal principal, string rawCode, CancellationToken ct)
    {
        Configured();
        await using var uow = await factory.BeginSnapshotAsync(ct);
        var record = await uow.Store<IQmsLookup>().FindAsync(Code(rawCode), ct);
        if (record is null || !await CanReadAsync(uow, principal, record, ct)) throw NotFound();
        return record;
    }

    public async Task<QmsRevisionView> ReadRevisionAsync(ForgePrincipal principal, string rawCode, int? number, DateTimeOffset? effectiveAt, CancellationToken ct)
    {
        Configured();
        await using var uow = await factory.BeginSnapshotAsync(ct);
        var record = await uow.Store<IQmsLookup>().FindAsync(Code(rawCode), ct) ?? throw NotFound();
        var revision = number is { } n ? record.Revisions.FirstOrDefault(r => r.Revision == n)
            : record.Revisions.FirstOrDefault(r => r.EffectiveFrom <= effectiveAt && (r.EffectiveTo is null || effectiveAt < r.EffectiveTo));
        if (revision is null || !await CanReadAsync(uow, principal, record, ct, revision.Revision)) throw NotFound();
        return revision;
    }

    public Task<CommandResult> CreateAsync(CreateQmsRecord c, CommandContext context, CancellationToken ct)
    {
        var code = Code(c.Code);
        var facility = BusinessCode.Require(c.FacilityCode, "Facility");
        var resource = new ResourceRef("QmsRecord", code, Program(c.ProgramId), c.Markings, FacilityId: facility);
        var spec = new CommandSpec("Quality", nameof(CreateQmsRecord), Permissions.QmsManage, resource, ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            Configured();
            if ((c.Markings & ~(DataMarking.ControlledUnclassified | DataMarking.ExportControlled | DataMarking.Proprietary)) != DataMarking.None)
                Reject("invalid_markings", "Select only the canonical handling markings.");
            if (await uow.Store<IQmsStore>().FindAsync(code, ct) is not null) Reject("qms_exists", "This QMS record already exists.");
            await HeaderSourcesAsync(uow, facility, c.ProgramId, ct);
            var sources = await ContentSourcesAsync(uow, context.Principal, code, c.Kind, c.Content, false, now, ct);
            var revision = Draft(code, 1, c.Content, sources, context, now);
            var record = new QmsRecordView(code, c.Kind, options.OrganizationCode!, facility, c.ProgramId, c.Markings, context.Actor, now, [revision]);
            await uow.Store<IQmsStore>().InsertRecordAsync(record, ct);
            await uow.Store<IQmsStore>().InsertRevisionAsync(revision, ct);
            return Outcome(record, revision, null, "Create", context);
        }, ct, async (uow, now) =>
        {
            Configured();
            if (await uow.Store<IQmsLookup>().FindAsync(code, ct) is { } existing)
            {
                if (existing.OrganizationCode != options.OrganizationCode) throw NotFound();
                await CurrentSourcesAsync(uow, context.Principal, existing, ct);
                return new CommandAuthority(Resource(existing) with { Markings = EffectiveMarkings(existing) }, $"{existing.OrganizationCode}:{existing.Code}");
            }
            // Scope is actual configuration/facility/program, not caller supplied replay metadata.
            await HeaderSourcesAsync(uow, facility, c.ProgramId, ct);
            await ContentSourcesAsync(uow, context.Principal, code, c.Kind, c.Content, false, now, ct);
            return new CommandAuthority(resource, $"{options.OrganizationCode}:{code}");
        });
    }

    public Task<CommandResult> CreateRevisionAsync(CreateQmsRevision c, CommandContext context, CancellationToken ct) =>
        ExecuteExistingAsync(nameof(CreateQmsRevision), c, c.Code, Permissions.QmsManage, context, c.Content, async (uow, record, now) =>
        {
            var latest = record.Revisions[^1];
            Version(latest, c.ExpectedVersion);
            if (record.Revisions.Any(r => r.State is QmsState.Draft or QmsState.Submitted or QmsState.Reviewed or QmsState.Approved)) Reject("open_revision", "Complete or withdraw the open QMS revision first.");
            var sources = await ContentSourcesAsync(uow, context.Principal, record.Code, record.Kind, c.Content, false, now, ct);
            var revision = Draft(record.Code, checked(latest.Revision + 1), c.Content, sources, context, now);
            await uow.Store<IQmsStore>().InsertRevisionAsync(revision, ct);
            return Outcome(record, revision, null, "CreateRevision", context);
        }, ct);

    public Task<CommandResult> EditAsync(EditQmsRevision c, CommandContext context, CancellationToken ct) =>
        ExecuteExistingAsync(nameof(EditQmsRevision), c, c.Code, Permissions.QmsManage, context, c.Content, async (uow, record, now) =>
        {
            var old = Revision(record, c.Revision); Version(old, c.ExpectedVersion);
            if (old.State != QmsState.Draft) Reject("immutable_revision", "Submitted QMS content is frozen; create a new governed revision.");
            var sources = await ContentSourcesAsync(uow, context.Principal, record.Code, record.Kind, c.Content, false, now, ct);
            var next = old with { Version = checked(old.Version + 1), Content = c.Content, LastEditedBy = context.Actor,
                Sources = sources, RequiresLicensedVerification = QmsContentRules.RequiresLicensedVerification(c.Content) };
            next = History(next, old.State, "Edit", "Draft content revised", context, now);
            await uow.Store<IQmsStore>().UpdateRevisionAsync(next, old.Version, true, ct);
            return Outcome(record, next, old, "Edit", context);
        }, ct);

    public Task<CommandResult> TransitionAsync(TransitionQmsRevision c, CommandContext context, CancellationToken ct)
    {
        var permission = c.Action switch { "Submit" => Permissions.QmsManage, "Review" => Permissions.QmsReview,
            "Approve" => Permissions.QmsApprove, "Effective" or "Withdraw" => Permissions.QmsPublish, _ => throw new CommandRejectedException("qms_action", "Unknown QMS lifecycle action.") };
        return ExecuteExistingAsync("Qms" + c.Action, c, c.Code, permission, context, null, async (uow, record, now) =>
        {
            var old = Revision(record, c.Revision); Version(old, c.ExpectedVersion);
            if (string.IsNullOrWhiteSpace(c.Note) || c.Note.Length > 4000) Reject("qms_note_required", "Record the human review, decision or applicability note.");
            var state = c.Action switch
            {
                "Submit" when old.State == QmsState.Draft => QmsState.Submitted,
                "Review" when old.State == QmsState.Submitted => QmsState.Reviewed,
                "Approve" when old.State == QmsState.Reviewed => QmsState.Approved,
                "Effective" when old.State == QmsState.Approved => QmsState.Effective,
                "Withdraw" when old.State is not (QmsState.Superseded or QmsState.Withdrawn) => QmsState.Withdrawn,
                _ => throw new CommandRejectedException("invalid_status", "This action does not follow the QMS revision's current governed state.")
            };
            if ((c.Action is "Review" or "Approve") && (context.Actor == old.CreatedBy || context.Actor == old.LastEditedBy
                || old.History.Any(h => h.Actor == context.Actor && h.Action is "Create" or "Edit")))
                Reject("separation_of_duties", "A content author cannot review or approve their own QMS revision.");
            // Authority is checked again after the record lock, in the same transaction as this transition.
            await ContentSourcesAsync(uow, context.Principal, record.Code, record.Kind, old.Content, c.Action != "Withdraw", now, ct);
            var next = old with { Version = checked(old.Version + 1), State = state,
                ReviewedBy = c.Action == "Review" ? context.Actor : old.ReviewedBy,
                ApprovedBy = c.Action == "Approve" ? context.Actor : old.ApprovedBy,
                EffectiveFrom = c.Action == "Effective" ? now : old.EffectiveFrom,
                EffectiveTo = c.Action == "Withdraw" && old.EffectiveFrom is not null ? now : old.EffectiveTo };
            var closedIntervals = new List<QmsRevisionView>();
            if (c.Action == "Effective")
            {
                foreach (var prior in record.Revisions.Where(r => r.State == QmsState.Effective))
                {
                    var superseded = History(prior with { Version = checked(prior.Version + 1), State = QmsState.Superseded, EffectiveTo = now }, prior.State,
                        "Supersede", $"Superseded by revision {next.Revision}", context, now);
                    await uow.Store<IQmsStore>().UpdateRevisionAsync(superseded, prior.Version, false, ct);
                    closedIntervals.Add(superseded);
                    // The replacement command's canonical audit includes both intervals; a separate event identifies the old exact revision.
                    await uow.Outbox.EnqueueAsync(pipeline.Event(new QmsRevisionChanged(record.Code, prior.Revision, superseded.Version, "Supersede", superseded.State),
                        context, "Quality", "QmsRevision", Key(record.Code, prior.Revision), Program(record.ProgramId), EffectiveMarkings(record)), ct);
                }
            }
            next = History(next, old.State, c.Action, c.Note.Trim(), context, now);
            await uow.Store<IQmsStore>().UpdateRevisionAsync(next, old.Version, false, ct);
            return Outcome(record, next, old, c.Action, context, c.Action == "Effective" ? record : null, closedIntervals);
        }, ct);
    }

    private Task<CommandResult> ExecuteExistingAsync<T>(string commandName, T c, string rawCode, string permission,
        CommandContext context, QmsContent? proposed,
        Func<IKernelUnitOfWork, QmsRecordView, DateTimeOffset, Task<CommandOutcome>> body, CancellationToken ct)
    {
        var code = Code(rawCode);
        var spec = new CommandSpec("Quality", commandName, permission, ResourceRef.Of("QmsRecord", code), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            await uow.Store<IQmsStore>().LockAsync(code, ct);
            var record = await RecordAsync(uow, code, ct);
            Permit(context.Principal, permission, Resource(record));
            await CurrentSourcesAsync(uow, context.Principal, record, ct);
            return await body(uow, record, now);
        }, ct, async (uow, now) =>
        {
            var record = await RecordAsync(uow, code, ct);
            await CurrentSourcesAsync(uow, context.Principal, record, ct);
            if (proposed is not null) await ContentSourcesAsync(uow, context.Principal, code, record.Kind, proposed, false, now, ct);
            // Lifecycle/version checks belong after idempotent replay. Current source access never does.
            return new CommandAuthority(Resource(record) with { Markings = EffectiveMarkings(record) }, $"{record.OrganizationCode}:{record.Code}");
        });
    }

    public static ResourceRef Resource(QmsRecordView record) => new("QmsRecord", record.Code, Program(record.ProgramId), record.Markings, FacilityId: record.FacilityCode);
    /// <summary>Owner-backed current read authority for Host evidence/audit/notifications. An exact revision uses only its recorded sources.</summary>
    public async Task<bool> CanReadAsync(IKernelUnitOfWork uow, ForgePrincipal principal, QmsRecordView record, CancellationToken ct, int? revision = null)
    {
        if (record.OrganizationCode != options.OrganizationCode || !authorizer.Authorize(principal, Permissions.QmsRead, Resource(record)).IsPermitted) return false;
        if (revision is { } n)
        {
            var selected = record.Revisions.FirstOrDefault(r => r.Revision == n);
            if (selected is null) return false;
            record = record with { Revisions = [selected] };
        }
        try { await CurrentSourcesAsync(uow, principal, record, ct); return true; }
        catch (ForgeAuthorizationException) { return false; }
        catch (CommandRejectedException) { return false; }
    }

    private async Task CurrentSourcesAsync(IKernelUnitOfWork uow, ForgePrincipal principal, QmsRecordView record, CancellationToken ct, HashSet<string>? visited = null)
    {
        visited ??= new HashSet<string>(StringComparer.Ordinal);
        // Recorded and current markings/scopes both apply. Historical effectivity does not require a document to remain current.
        foreach (var source in record.Revisions.Where(r => visited.Add(Key(record.Code, r.Revision))).SelectMany(r => r.Sources).Distinct())
        {
            Permit(principal, source.Kind == "DocumentRevision" ? Permissions.EngineeringRead : Permissions.QmsRead,
                new(source.Kind, source.Key, Program(source.ProgramId), source.Markings, FacilityId: source.FacilityCode));
            if (source.Kind == "DocumentRevision")
            {
                var doc = await uow.Store<IDocumentLookup>().FindDocumentAsync(source.Key, ct) ?? throw SourceNotFound();
                Permit(principal, Permissions.EngineeringRead, new("DocumentRevision", source.Key, Program(doc.ProgramId), doc.Markings));
                if (!doc.Revisions.Any(r => r.Revision == source.Revision)) throw SourceNotFound();
            }
            else
            {
                var linked = await RecordAsync(uow, source.Key, ct);
                Permit(principal, Permissions.QmsRead, Resource(linked));
                var linkedRevision = linked.Revisions.FirstOrDefault(r => r.Revision.ToString(CultureInfo.InvariantCulture) == source.Revision) ?? throw SourceNotFound();
                // Direct copied sources are checked too; references are acyclic at creation because self/circular sources are rejected below.
                await CurrentSourcesAsync(uow, principal, linked with { Revisions = [linkedRevision] }, ct, visited);
            }
        }
    }

    private async Task<IReadOnlyList<QmsSourceReference>> ContentSourcesAsync(IKernelUnitOfWork uow, ForgePrincipal principal, string ownCode,
        QmsRecordKind kind, QmsContent content, bool requireEffective, DateTimeOffset now, CancellationToken ct)
    {
        QmsContentRules.Validate(kind, content);
        var owner = content.Process?.OwnerPersonId ?? content.Policy?.OwnerPersonId ?? content.Requirement?.OwnerPersonId;
        if (owner is { } id && await uow.People.FindAsync(new PersonId(id), ct) is not { IsActive: true }) Reject("owner_not_found", "The responsible person is unavailable or inactive.");
        if (content.Policy?.Rules?.ProductRelease is { } release && release.ReleasePermission != Permissions.QualityProductRelease)
            Reject("release_permission", "Product release uses the canonical Quality product-release decision permission. A policy cannot substitute physical shipment or a weaker permission for that action.");
        var sources = new List<QmsSourceReference>();
        foreach (var reference in QmsContentRules.DocumentsFor(content))
        {
            var number = QmsContentRules.DocumentNumber(reference.DocumentNumber);
            var documentRevision = QmsContentRules.DocumentRevision(reference.Revision);
            await uow.Store<IQmsStore>().LockSourceAsync("DocumentRevision", number, documentRevision, ct);
            var doc = await uow.Store<IDocumentLookup>().FindDocumentAsync(number, ct) ?? throw SourceNotFound();
            Permit(principal, Permissions.EngineeringRead, new("DocumentRevision", number, Program(doc.ProgramId), doc.Markings));
            var revision = doc.Revisions.FirstOrDefault(r => r.Revision == documentRevision) ?? throw SourceNotFound();
            if (requireEffective)
            {
                var day = (await uow.SiteDateAsync(now, ct));
                if (doc.Authority == DocumentAuthority.Reference || revision.Status is not (DocumentRevisionStatus.Released or DocumentRevisionStatus.Applicable)
                    || revision.EffectiveFrom is null || revision.EffectiveFrom > day || revision.EffectiveTo is { } to && to <= day)
                    Reject("document_not_effective", "An adopting QMS revision needs the exact released internal or applicable external document revision in effect.");
            }
            sources.Add(new("DocumentRevision", number, revision.Revision, doc.ProgramId, doc.Markings, null));
        }
        if (requireEffective && (kind is QmsRecordKind.Policy or QmsRecordKind.Requirement) && sources.Count == 0)
            Reject("controlled_document_required", "Policies and requirements must reference their exact controlled Engineering document revision before submission.");
        foreach (var (reference, expectedKind) in QmsContentRules.ReferencesFor(content).Distinct())
        {
            var code = Code(reference.Code);
            if (code == ownCode) Reject("qms_dependency_cycle", "A QMS revision cannot reference its own record.");
            await uow.Store<IQmsStore>().LockSourceAsync("QmsRevision", code, reference.Revision.ToString(CultureInfo.InvariantCulture), ct);
            var linked = await RecordAsync(uow, code, ct);
            Permit(principal, Permissions.QmsRead, Resource(linked));
            var revision = Revision(linked, reference.Revision);
            await CurrentSourcesAsync(uow, principal, linked with { Revisions = [revision] }, ct);
            if (linked.Kind != expectedKind) Reject("source_kind", "The linked QMS revision has the wrong record kind.");
            if (linked.Revisions.SelectMany(r => r.Sources).Any(s => s.Kind == "QmsRevision" && s.Key == ownCode)
                || await ContainsSourceAsync(uow, linked, ownCode, new HashSet<string>(StringComparer.Ordinal), ct))
                Reject("qms_dependency_cycle", "QMS process dependencies must remain acyclic.");
            if (requireEffective && (revision.EffectiveFrom is null || revision.EffectiveFrom > now || revision.EffectiveTo is { } end && end <= now))
                Reject("qms_source_not_effective", "Use a QMS source revision that is in effect for the adoption decision.");
            if (requireEffective) await ContentSourcesAsync(uow, principal, linked.Code, linked.Kind, revision.Content, true, now, ct);
            if (content.Profile?.NonApplicability.Any(n => Code(n.RequirementCode) == code && n.Revision == revision.Revision) == true
                && revision.Content.Requirement?.Applicability != QmsApplicability.NotApplicable)
                Reject("non_applicability_conflict", "A profile's non-applicability basis must cite an exact requirement revision determined not applicable.");
            sources.Add(new("QmsRevision", code, revision.Revision.ToString(CultureInfo.InvariantCulture), linked.ProgramId, linked.Markings, linked.FacilityCode));
        }
        return sources.Distinct().OrderBy(s => s.Kind, StringComparer.Ordinal).ThenBy(s => s.Key, StringComparer.Ordinal).ThenBy(s => s.Revision, StringComparer.Ordinal).ToArray();
    }

    private static async Task<bool> ContainsSourceAsync(IKernelUnitOfWork uow, QmsRecordView record, string target, HashSet<string> seen, CancellationToken ct)
    {
        if (!seen.Add(record.Code)) return false;
        foreach (var source in record.Revisions.SelectMany(r => r.Sources).Where(s => s.Kind == "QmsRevision"))
        {
            if (source.Key == target) return true;
            var linked = await uow.Store<IQmsLookup>().FindAsync(source.Key, ct);
            if (linked is null || await ContainsSourceAsync(uow, linked, target, seen, ct)) return true;
        }
        return false;
    }
    private static async Task HeaderSourcesAsync(IKernelUnitOfWork uow, string facility, Guid? program, CancellationToken ct)
    {
        if (!(await uow.Store<IResourcesLookup>().ListSitesAsync(ct)).Any(s => s.Code == facility)) Reject("facility_not_found", "The configured facility is unavailable.");
        if (program is { } id && await uow.Store<IProgramsLookup>().FindProgramAsync(id, ct) is not { Status: ProgramStatus.Active }) Reject("program_not_found", "The owning program is unavailable or inactive.");
    }
    private async Task<QmsRecordView> RecordAsync(IKernelUnitOfWork uow, string code, CancellationToken ct)
    {
        Configured();
        var record = await uow.Store<IQmsLookup>().FindAsync(code, ct);
        if (record is null || record.OrganizationCode != options.OrganizationCode) throw NotFound();
        return record;
    }
    private CommandOutcome Outcome(QmsRecordView record, QmsRevisionView next, QmsRevisionView? old, string action, CommandContext context, QmsRecordView? priorIntervals = null, IReadOnlyList<QmsRevisionView>? closedIntervals = null) =>
        new(CommandResult.Ok(Key(record.Code, next.Revision), $"QMS {record.Code} revision {next.Revision}: {next.State}."),
            [pipeline.Event(new QmsRevisionChanged(record.Code, next.Revision, next.Version, action, next.State), context, "Quality", "QmsRevision", Key(record.Code, next.Revision), Program(record.ProgramId), EffectiveMarkings(record) | next.Sources.Aggregate(DataMarking.None, (m, s) => m | s.Markings))],
            new AuditChange("QmsRevision", Key(record.Code, next.Revision), old is null ? null : CanonicalJson.Serialize(new { revision = old, priorIntervals }),
                CanonicalJson.Serialize(new { revision = next, closedIntervals }), Program(record.ProgramId), EffectiveMarkings(record) | next.Sources.Aggregate(DataMarking.None, (m, s) => m | s.Markings)));
    private static QmsRevisionView Draft(string code, int number, QmsContent content, IReadOnlyList<QmsSourceReference> sources, CommandContext context, DateTimeOffset now) =>
        new(code, number, 1, QmsState.Draft, content, context.Actor, context.Actor, null, null, null, null,
            QmsContentRules.RequiresLicensedVerification(content), sources, [new("Create", context.Actor, now, "Draft created", context.Reason!.Trim(), null, QmsState.Draft, 1)]);
    private static QmsRevisionView History(QmsRevisionView next, QmsState from, string action, string note, CommandContext context, DateTimeOffset now) =>
        next with { History = next.History.Append(new QmsTransition(action, context.Actor, now, note, context.Reason!.Trim(), from, next.State, next.Version)).ToArray() };
    private static QmsRevisionView Revision(QmsRecordView record, int revision) => record.Revisions.FirstOrDefault(r => r.Revision == revision) ?? throw NotFound();
    private static void Version(QmsRevisionView revision, int expected) { if (expected != revision.Version) Reject("stale_version", "The QMS revision changed; read its current version before deciding."); }
    private void Configured() { if (!options.Configured) Reject("qms_not_configured", "Configure the explicit deployment organization before governing QMS records."); }
    private void Permit(ForgePrincipal principal, string permission, ResourceRef resource) { var decision = authorizer.Authorize(principal, permission, resource); if (!decision.IsPermitted) throw new ForgeAuthorizationException(decision); }
    private static bool MayRead(ForgePrincipal p) => p.HasPermission(Permissions.QmsRead) || p.ScopedPermissions?.Any(s => s.Permission == Permissions.QmsRead) == true || p.DelegatedPermissions?.Any(s => s.Permission == Permissions.QmsRead) == true;
    private static string Code(string code) => BusinessCode.Require(code, "QMS record");
    private static string Key(string code, int revision) => code + "/" + revision.ToString(CultureInfo.InvariantCulture);
    private static ProgramId? Program(Guid? id) => id is { } value ? new ProgramId(value) : null;
    private static DataMarking EffectiveMarkings(QmsRecordView record) => record.Revisions.SelectMany(r => r.Sources).Aggregate(record.Markings, (m, s) => m | s.Markings);
    private static CommandRejectedException NotFound() => new("qms_not_found", "The QMS record or revision is unavailable.");
    private static CommandRejectedException SourceNotFound() => new("qms_source_not_found", "An exact QMS source revision is unavailable.");
    private static void Reject(string code, string message) => throw new CommandRejectedException(code, message);
}
