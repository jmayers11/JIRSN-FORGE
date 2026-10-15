using System.Globalization;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;
using Forge.Quality.Qms;

namespace Forge.Quality.Commercial;

public sealed record CreateCommercialRecord(CommercialRecordKind Kind, string Code, string FacilityCode, CommercialContent Content);
public sealed record ReviseCommercialRecord(CommercialRecordKind Kind, string Code, int ExpectedVersion, CommercialContent Content);
public sealed record EditCommercialRecord(CommercialRecordKind Kind, string Code, int Revision, int ExpectedVersion, CommercialContent Content);
public sealed record TransitionCommercialRecord(CommercialRecordKind Kind, string Code, int Revision, int ExpectedVersion, string Action, string Basis);
public sealed record ObserveCommercialRecord(CommercialRecordKind Kind, string Code, int Revision, int ExpectedVersion,
    CommercialObservationKind Observation, DateTimeOffset ObservedAt, string Basis, IReadOnlyList<CommercialReference> Evidence, string? RecordedExternalPerson,
    IReadOnlyList<PreservationReading>? Preservation = null);
public sealed record CommercialQualityChanged(CommercialRecordKind Kind, string Code, int Revision, int Version, string Action, CommercialState State) : IDomainEvent
{
    public static string EventType => nameof(CommercialQualityChanged);
    public static int SchemaVersion => 1;
}

public sealed partial class CommercialQualityService(CommandPipeline pipeline, IKernelUnitOfWorkFactory factory,
    IForgeAuthorizer authorizer, ICommercialQualitySourceResolver sources, QmsOptions options) : ICommercialQualityAccess, ICommercialUseAccess, ICommercialCustomerApproval
{
    /// <summary>Serialize a planning snapshot with changes to the selected commercial source.</summary>
    public static async Task LockEvidenceSourceAsync(IKernelUnitOfWork uow, CommercialRevision record, CancellationToken ct)
    {
        var store = uow.Store<ICommercialQualityStore>();
        await store.LockScopeAsync(record.Kind, ScopeKey(record.Content), true, ct);
        await store.LockCodeAsync(record.Kind, record.Code, ct);
    }

    public async Task<CommercialRevision> ReadAsync(ForgePrincipal principal, CommercialRecordKind kind, string code, int revision, CancellationToken ct)
    {
        Configured();
        await using var uow = await factory.BeginSnapshotAsync(ct);
        var record = await uow.Store<ICommercialQualityLookup>().FindAsync(kind, Code(code), revision, ct);
        if (record is null || !await CanReadRecordAsync(uow, principal, record, ct)) throw Missing();
        return record;
    }
    public async Task<CommercialPage> ListAsync(ForgePrincipal principal, CommercialRecordKind? kind, int take, string? after, CancellationToken ct)
    {
        Configured();
        if (take is < 1 or > 100 || after?.Length > 100) Reject("commercial_page", "Use a page size from 1 to 100 and the returned cursor.");
        await using var uow = await factory.BeginSnapshotAsync(ct);
        var visible = new List<CommercialRevision>();
        string? cursor = after;
        // Page through source rows before filtering: a hidden first page never truncates visible records.
        while (visible.Count < take)
        {
            var page = await uow.Store<ICommercialQualityLookup>().ListAsync(kind, take - visible.Count, cursor, ct);
            if (page.Count == 0) return new(visible, null);
            foreach (var record in page)
            {
                cursor = Cursor(record);
                if (await CanReadRecordAsync(uow, principal, record, ct)) visible.Add(record);
            }
            if (page.Count == 0) return new(visible, null);
        }
        return new(visible, cursor);
    }
    public async Task<CommercialCaseEvidence> ShipmentEvidenceAsync(ForgePrincipal principal, long shipment, int? line, CancellationToken ct)
    {
        Configured();
        await using var uow = await factory.BeginSnapshotAsync(ct);
        return await sources.ShipmentEvidenceAsync(uow, principal, shipment, line, ct);
    }

    public Task<CommandResult> CreateAsync(CreateCommercialRecord c, CommandContext context, CancellationToken ct)
    {
        var code = Code(c.Code); var facility = BusinessCode.Require(c.FacilityCode, "Facility");
        var spec = new CommandSpec("Quality", nameof(CreateCommercialRecord), Permissions.QualityPlan,
            new("CommercialQuality", Key(c.Kind, code, 1), FacilityId: facility), ReasonRequired: true);
        async Task<CommandAuthority> Authority(IKernelUnitOfWork uow, DateTimeOffset now)
        {
            var currentPrincipal = await CurrentPrincipalAsync(uow, context.Principal, ct);
            Configured(); CommercialRules.Validate(c.Kind, c.Content);
            await uow.Store<ICommercialQualityStore>().LockScopeAsync(c.Kind, ScopeKey(c.Content), false, ct);
            await uow.Store<ICommercialQualityStore>().LockCodeAsync(c.Kind, code, ct);
            var scope = await sources.ResolveAsync(uow, context.Principal, facility, c.Kind, c.Content, ct);
            var existing = await uow.Store<ICommercialQualityLookup>().FindAsync(c.Kind, code, 1, ct);
            if (existing is not null) await RequireReadSourcesAsync(uow, context.Principal, existing, ct);
            var resources = Own(scope.Ownership.Concat(existing?.Ownership ?? []), Key(c.Kind, code, 1), facility);
            PermitAll(currentPrincipal, Permissions.QualityPlan, resources);
            return AuthorityFor(resources, scope.Fingerprint);
        }
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            await Authority(uow, now);
            if ((await uow.Store<ICommercialQualityLookup>().RevisionsAsync(c.Kind, code, ct)).Count != 0) Reject("commercial_exists", "Use a new governed revision of this record.");
            var scope = await sources.ResolveAsync(uow, context.Principal, facility, c.Kind, c.Content, ct);
            var record = new CommercialRevision(c.Kind, code, 1, 1, CommercialState.Draft, options.OrganizationCode!, facility,
                c.Content, context.Actor, context.Actor, null, null, now, null, null, scope.Ownership, scope.Sources, scope.Facts,
                scope.Fingerprint, [], [new("Create", CommercialState.Draft, CommercialState.Draft, 1, context.Actor, now, "Draft created", context.Reason!.Trim())]);
            await uow.Store<ICommercialQualityStore>().InsertAsync(record, ct);
            return Outcome(null, record, "Create", context);
        }, ct, Authority);
    }

    public Task<CommandResult> ReviseAsync(ReviseCommercialRecord c, CommandContext context, CancellationToken ct) =>
        ExistingAsync(nameof(ReviseCommercialRecord), c, c.Kind, c.Code, null, Permissions.QualityPlan, c.Content, context, async (uow, old, scope, now) =>
        {
            var revisions = await uow.Store<ICommercialQualityLookup>().RevisionsAsync(c.Kind, old.Code, ct);
            old = revisions[^1]; Version(old, c.ExpectedVersion);
            if (revisions.Any(r => r.State is CommercialState.Draft or CommercialState.Submitted or CommercialState.Reviewed or CommercialState.Approved)) Reject("open_revision", "Complete or withdraw the pending revision first.");
            var next = old with { Revision = checked(old.Revision + 1), Version = 1, State = CommercialState.Draft, Content = c.Content,
                CreatedBy = context.Actor, LastEditedBy = context.Actor, ReviewedBy = null, ApprovedBy = null, CreatedAt = now,
                EffectiveFrom = null, EffectiveTo = null, Ownership = scope.Ownership, Sources = scope.Sources, Facts = scope.Facts,
                SourceFingerprint = scope.Fingerprint, Observations = [], History = [] };
            next = History(next, CommercialState.Draft, "CreateRevision", "Draft revision created", context, now);
            await uow.Store<ICommercialQualityStore>().InsertAsync(next, ct);
            return Outcome(null, next, "CreateRevision", context);
        }, ct);

    public Task<CommandResult> EditAsync(EditCommercialRecord c, CommandContext context, CancellationToken ct) =>
        ExistingAsync(nameof(EditCommercialRecord), c, c.Kind, c.Code, c.Revision, Permissions.QualityPlan, c.Content, context, async (uow, old, scope, now) =>
        {
            Version(old, c.ExpectedVersion);
            if (old.State != CommercialState.Draft) Reject("immutable_revision", "Submitted content is frozen; create a new revision.");
            var next = History(old with { Version = checked(old.Version + 1), Content = c.Content, LastEditedBy = context.Actor,
                Ownership = scope.Ownership, Sources = scope.Sources, Facts = scope.Facts, SourceFingerprint = scope.Fingerprint }, old.State, "Edit", "Draft revised", context, now);
            await uow.Store<ICommercialQualityStore>().UpdateAsync(next, old.Version, ct);
            return Outcome(old, next, "Edit", context);
        }, ct);

    public Task<CommandResult> TransitionAsync(TransitionCommercialRecord c, CommandContext context, CancellationToken ct)
    {
        var permission = c.Action switch
        {
            "Submit" => Permissions.QualityPlan,
            "Review" or "Approve" or "Effective" or "Withdraw" or "Close" => c.Kind == CommercialRecordKind.Communication ? Permissions.QualityCommunicationsApprove : Permissions.QualityApprove,
            _ => throw new CommandRejectedException("commercial_action", "Use a declared governance transition.")
        };
        CommercialRules.Text(c.Basis);
        return ExistingAsync("Commercial" + c.Action, c, c.Kind, c.Code, c.Revision, permission, null, context, async (uow, old, scope, now) =>
        {
            Version(old, c.ExpectedVersion);
            var state = c.Action switch
            {
                "Submit" when old.State == CommercialState.Draft => CommercialState.Submitted,
                "Review" when old.State == CommercialState.Submitted => CommercialState.Reviewed,
                "Approve" when old.State == CommercialState.Reviewed => CommercialState.Approved,
                "Effective" when old.State == CommercialState.Approved => CommercialState.Effective,
                "Withdraw" when old.State is not (CommercialState.Withdrawn or CommercialState.Superseded or CommercialState.Closed) => CommercialState.Withdrawn,
                "Close" when old.State == CommercialState.Effective && old.Kind is CommercialRecordKind.Complaint or CommercialRecordKind.PropertyIncident or CommercialRecordKind.PostDelivery => CommercialState.Closed,
                _ => throw new CommandRejectedException("invalid_status", "The action does not follow the recorded governed state.")
            };
            if ((c.Action is "Review" or "Approve" or "Effective" or "Close")
                && (context.Actor == old.CreatedBy || context.Actor == old.LastEditedBy)) Reject("separation_of_duties", "A content author cannot independently review, approve, publish or close their own record.");
            if ((c.Action is "Approve" or "Effective") && old.SourceFingerprint != scope.Fingerprint) Reject("commercial_source_changed", "Actual source configuration changed; record and independently review a new revision.");
            if (c.Action == "Effective" && old.Content.ProviderEvaluation is { } provider
                && (!CommercialRules.Evaluate(provider).MeetsApprovedCriteria || now < provider.ValidFrom || now >= provider.ValidTo || now >= provider.ReevaluateBy))
                Reject("provider_not_qualified", "Recorded mandatory criteria, validity and reevaluation must permit this exact approval scope.");
            if (c.Action == "Close" && !old.Observations.Any(o => o.Kind == CommercialObservationKind.ComplaintDisposition || o.Kind == CommercialObservationKind.CorrectiveAction || o.Kind == CommercialObservationKind.OwnerAcceptance))
                Reject("closure_evidence_required", "Record a disposition, corrective action or owner acceptance with actual evidence before independent closure.");
            if (c.Action == "Close" && old.Kind == CommercialRecordKind.Complaint)
                await RequireLinkedCapasClosedAsync(uow, context.Principal, old, ct);
            var next = old with { Version = checked(old.Version + 1), State = state,
                ReviewedBy = c.Action == "Review" ? context.Actor : old.ReviewedBy,
                ApprovedBy = c.Action == "Approve" ? context.Actor : old.ApprovedBy,
                EffectiveFrom = c.Action == "Effective" ? now : old.EffectiveFrom,
                EffectiveTo = (c.Action is "Withdraw" or "Close") && old.EffectiveFrom is not null ? now : old.EffectiveTo };
            var superseded = new List<CommercialRevision>();
            if (c.Action == "Effective")
                foreach (var prior in (await uow.Store<ICommercialQualityLookup>().RevisionsAsync(c.Kind, old.Code, ct)).Where(r => r.State == CommercialState.Effective))
                {
                    var ended = History(prior with { Version = checked(prior.Version + 1), State = CommercialState.Superseded, EffectiveTo = now }, prior.State,
                        "Supersede", "Replaced by revision " + next.Revision.ToString(CultureInfo.InvariantCulture), context, now);
                    await uow.Store<ICommercialQualityStore>().UpdateAsync(ended, prior.Version, ct); superseded.Add(ended);
                }
            next = History(next, old.State, c.Action, c.Basis, context, now);
            await uow.Store<ICommercialQualityStore>().UpdateAsync(next, old.Version, ct);
            return Outcome(old, next, c.Action, context, superseded);
        }, ct);
    }

    public Task<CommandResult> ObserveAsync(ObserveCommercialRecord c, CommandContext context, CancellationToken ct)
    {
        CommercialRules.Text(c.Basis); CommercialRules.References(c.Evidence, 1);
        if (!Enum.IsDefined(c.Observation)) Reject("commercial_observation", "Use a declared observation kind.");
        return ExistingAsync(nameof(ObserveCommercialRecord), c, c.Kind, c.Code, c.Revision, Permissions.QualityPlan, null, context, async (uow, old, scope, now) =>
        {
            Version(old, c.ExpectedVersion);
            if (old.State != CommercialState.Effective || c.ObservedAt > now) Reject("observation_state", "Record observations on an effective record at an actual time no later than now.");
            if (!AllowedObservation(old.Kind, c.Observation)) Reject("observation_kind", "This observation belongs to a different workflow.");
            await sources.ValidateObservationAsync(uow, context.Principal, old, c.Observation, c.Evidence, c.ObservedAt, ct);
            if (old.Content.PropertyAgreement is { RequireOwnerReturnAcceptance: true } && c.Observation == CommercialObservationKind.PropertyReturn
                && !old.Observations.Any(o => o.Kind == CommercialObservationKind.OwnerAcceptance && o.ObservedAt <= c.ObservedAt))
                Reject("owner_return_acceptance_required", "Record the actual legal owner's return acceptance before completing this property return.");
            if (c.Observation is CommercialObservationKind.OwnerAcceptance or CommercialObservationKind.CommunicationObserved or CommercialObservationKind.CustomerApprovalReceived) CommercialRules.Text(c.RecordedExternalPerson);
            if (c.Observation == CommercialObservationKind.CustomerApprovalReceived && old.Content.Communication?.Concession is null)
                Reject("customer_concession_scope", "A received customer approval must bind the independently approved exact concession scope.");
            ValidatePreservation(old, c);
            var evidence = await sources.ResolveReferencesAsync(uow, context.Principal, old.FacilityCode, c.Evidence, ct);
            var observation = new CommercialObservation(checked(old.Observations.Count + 1), c.Observation, c.ObservedAt, context.Actor, c.Basis.Trim(), c.Evidence,
                evidence.Sources, evidence.Ownership, c.RecordedExternalPerson, c.Preservation);
            var next = History(old with { Version = checked(old.Version + 1), Observations = old.Observations.Append(observation).ToArray() }, old.State,
                "Observe", c.Basis, context, now);
            await uow.Store<ICommercialQualityStore>().UpdateAsync(next, old.Version, ct);
            return Outcome(old, next, "Observe", context);
        }, ct, c.Evidence);
    }

    private async Task RequireLinkedCapasClosedAsync(IKernelUnitOfWork uow, ForgePrincipal principal,
        CommercialRevision complaint, CancellationToken ct)
    {
        var linked = complaint.Observations.Where(observation => observation.Kind == CommercialObservationKind.CorrectiveAction)
            .SelectMany(observation => observation.Evidence)
            .Where(reference => reference.Kind == CommercialReferenceKind.Capa)
            .Distinct().ToArray();
        foreach (var reference in linked)
        {
            if (!long.TryParse(reference.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var capaId) || capaId <= 0)
                Reject("complaint_capa_link", "A complaint corrective-action link must identify an actual positive CAPA ID.");

            var governedStore = uow.Store<IAuditCapaStore>();
            await governedStore.LockCapaAsync(capaId, ct);
            _ = await sources.ResolveReferencesAsync(uow, principal, complaint.FacilityCode, [reference], ct);
            var core = await uow.Store<IQualityStore>().FindCapaAsync(capaId, ct);
            if (core is null || core.Status != CapaStatus.Verified)
                Reject("complaint_capa_not_closed", "Every CAPA linked to the complaint corrective-action observation must be independently verified.");
            var governed = await uow.Store<IAuditCapaLookup>().FindCapaAsync(capaId, ct);
            if (governed is not null && governed.State != QmsCapaState.Closed)
                Reject("complaint_capa_not_closed", "Every linked governed CAPA must have independently approved closure before the complaint closes.");
        }
    }

    private Task<CommandResult> ExistingAsync<T>(string command, T value, CommercialRecordKind kind, string code, int? revision,
        string permission, CommercialContent? proposed, CommandContext context,
        Func<IKernelUnitOfWork, CommercialRevision, CommercialScope, DateTimeOffset, Task<CommandOutcome>> body, CancellationToken ct,
        IReadOnlyList<CommercialReference>? evidence = null)
    {
        code = Code(code);
        var spec = new CommandSpec("Quality", command, permission, new("CommercialQuality", Key(kind, code, revision ?? 1)), ReasonRequired: true);
        async Task<(CommercialRevision Record, CommercialScope Scope, ResourceRef[] Resources)> Current(IKernelUnitOfWork uow)
        {
            var currentPrincipal = await CurrentPrincipalAsync(uow, context.Principal, ct);
            Configured();
            var records = await uow.Store<ICommercialQualityLookup>().RevisionsAsync(kind, code, ct);
            var record = revision is { } exact ? records.FirstOrDefault(r => r.Revision == exact) : records.Count == 0 ? null : records[^1];
            if (record is null || record.OrganizationCode != options.OrganizationCode) throw Missing();
            // Same lock key is used by consuming commands, so withdrawal and use cannot pass one another.
            var lockKeys = new[] { ScopeKey(record.Content), ScopeKey(proposed ?? record.Content) }.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            foreach (var scopeKey in lockKeys) await uow.Store<ICommercialQualityStore>().LockScopeAsync(kind, scopeKey, false, ct);
            await uow.Store<ICommercialQualityStore>().LockCodeAsync(kind, code, ct);
            record = await uow.Store<ICommercialQualityLookup>().FindAsync(kind, code, record.Revision, ct) ?? throw Missing();
            await RequireReadSourcesAsync(uow, context.Principal, record, ct);
            var scope = await sources.ResolveAsync(uow, context.Principal, record.FacilityCode, kind, proposed ?? record.Content, ct);
            var resources = Own(record.Ownership.Concat(scope.Ownership), Key(kind, code, record.Revision), record.FacilityCode);
            if (evidence is not null)
            {
                await sources.ValidateObservationAsync(uow, context.Principal, record,
                    value is ObserveCommercialRecord observation ? observation.Observation : CommercialObservationKind.Monitoring,
                    evidence, value is ObserveCommercialRecord observed ? observed.ObservedAt : pipeline.Time.GetUtcNow(), ct);
                var additional = await sources.ResolveReferencesAsync(uow, context.Principal, record.FacilityCode, evidence, ct);
                resources = Own(resources.Concat(additional.Ownership), Key(kind, code, record.Revision), record.FacilityCode);
            }
            PermitAll(currentPrincipal, permission, resources);
            return (record, scope, resources);
        }
        return pipeline.ExecuteAsync(spec, value, context, async (uow, now) =>
        {
            var current = await Current(uow); return await body(uow, current.Record, current.Scope, now);
        }, ct, async (uow, now) => { var current = await Current(uow); return AuthorityFor(current.Resources, current.Scope.Fingerprint); });
    }

    public async Task<IReadOnlyList<ResourceRef>?> ResourcesAsync(IKernelUnitOfWork uow, string kind, string key, CancellationToken ct)
    {
        if (!TryKey(key, out var recordKind, out var code, out var revision)) return null;
        var record = await uow.Store<ICommercialQualityLookup>().FindAsync(recordKind, code, revision, ct);
        if (record is null || record.OrganizationCode != options.OrganizationCode) return null;
        var current = await sources.ResourceContextsAsync(uow, record.FacilityCode,
            CommercialReferences.Of(record.Content).Concat(record.Observations.SelectMany(o => o.Evidence)).Distinct().ToArray(), ct);
        return Own(record.Ownership.Concat(current).Concat(record.Observations.SelectMany(o => o.Ownership)), key, record.FacilityCode);
    }
    public async Task<bool> CanReadAsync(IKernelUnitOfWork uow, ForgePrincipal principal, string kind, string key, CancellationToken ct)
    {
        if (!TryKey(key, out var recordKind, out var code, out var revision)) return false;
        var record = await uow.Store<ICommercialQualityLookup>().FindAsync(recordKind, code, revision, ct);
        return record is not null && await CanReadRecordAsync(uow, principal, record, ct);
    }
    private async Task<bool> CanReadRecordAsync(IKernelUnitOfWork uow, ForgePrincipal principal, CommercialRevision record, CancellationToken ct)
    {
        if (record.OrganizationCode != options.OrganizationCode) return false;
        try
        {
            principal = await CurrentPrincipalAsync(uow, principal, ct);
            var current = await sources.ResolveAsync(uow, principal, record.FacilityCode, record.Kind, record.Content, ct);
            PermitAll(principal, Permissions.QualityRead, Own(record.Ownership.Concat(current.Ownership).Concat(record.Observations.SelectMany(o => o.Ownership)), Key(record.Kind, record.Code, record.Revision), record.FacilityCode));
            await RequireReadSourcesAsync(uow, principal, record, ct); return true;
        }
        catch (ForgeAuthorizationException) { return false; }
        catch (CommandRejectedException) { return false; }
    }
    private async Task RequireReadSourcesAsync(IKernelUnitOfWork uow, ForgePrincipal principal, CommercialRevision record, CancellationToken ct)
    {
        await sources.RequireCapturedAsync(uow, principal, record.Sources.Concat(record.Observations.SelectMany(o => o.Sources)).Distinct().ToArray(), ct);
        await sources.ResolveAsync(uow, principal, record.FacilityCode, record.Kind, record.Content, ct);
        foreach (var observation in record.Observations) await sources.ResolveReferencesAsync(uow, principal, record.FacilityCode, observation.Evidence, ct);
    }
    private CommandOutcome Outcome(CommercialRevision? old, CommercialRevision next, string action, CommandContext context, IReadOnlyList<CommercialRevision>? superseded = null)
    {
        var markings = next.Ownership.Concat(next.Sources.Select(s => s.Resource)).Concat(next.Observations.SelectMany(o => o.Sources.Select(s => s.Resource))).Aggregate(DataMarking.None, (m, r) => m | r.Markings);
        var program = next.Ownership.Select(r => r.Program).FirstOrDefault(p => p is not null);
        return new(CommandResult.Ok(Key(next.Kind, next.Code, next.Revision), $"{next.Kind} {next.Code}/{next.Revision}: {next.State}."),
            [pipeline.Event(new CommercialQualityChanged(next.Kind, next.Code, next.Revision, next.Version, action, next.State), context, "Quality", "CommercialQuality", Key(next.Kind, next.Code, next.Revision), program, markings)],
            new("CommercialQuality", Key(next.Kind, next.Code, next.Revision), old is null ? null : CanonicalJson.Serialize(old), CanonicalJson.Serialize(new { revision = next, superseded }), program, markings));
    }
    private static bool AllowedObservation(CommercialRecordKind kind, CommercialObservationKind observation) => kind switch
    {
        CommercialRecordKind.ProviderEvaluation => observation is CommercialObservationKind.Monitoring or CommercialObservationKind.Reassessment or CommercialObservationKind.CorrectiveAction,
        CommercialRecordKind.PropertyAgreement => observation is CommercialObservationKind.PropertyReceipt or CommercialObservationKind.PropertyUse or CommercialObservationKind.PropertyReturn or CommercialObservationKind.OwnerAcceptance or CommercialObservationKind.PreservationObservation,
        CommercialRecordKind.PropertyIncident => observation is CommercialObservationKind.Investigation or CommercialObservationKind.Correction or CommercialObservationKind.OwnerAcceptance,
        CommercialRecordKind.Complaint or CommercialRecordKind.PostDelivery => observation is CommercialObservationKind.Investigation or CommercialObservationKind.Correction or CommercialObservationKind.CorrectiveAction or CommercialObservationKind.ComplaintDisposition,
        CommercialRecordKind.Communication => observation is CommercialObservationKind.CommunicationObserved or CommercialObservationKind.CustomerApprovalReceived,
        _ => observation == CommercialObservationKind.Monitoring
    };
    private static void ValidatePreservation(CommercialRevision record, ObserveCommercialRecord command)
    {
        if (command.Observation != CommercialObservationKind.PreservationObservation)
        { if (command.Preservation is not null) Reject("preservation_observation", "Readings belong to a preservation observation."); return; }
        var controls = record.Content.PropertyAgreement?.Preservation ?? [];
        if (controls.Count == 0 || command.Preservation is null || command.Preservation.Count != controls.Count
            || command.Preservation.Select(r => r.ControlCode).Distinct(StringComparer.Ordinal).Count() != controls.Count)
            Reject("preservation_observation", "Record one actual reading for every governed preservation control.");
        foreach (var reading in command.Preservation!)
        {
            var control = controls.FirstOrDefault(c => c.Code == reading.ControlCode) ?? throw new CommandRejectedException("preservation_control", "The reading does not belong to this agreement's preservation controls.");
            CommercialRules.Text(reading.Basis);
            if ((reading.Value is null) == (reading.Accepted is null) || control.Unit != reading.Unit
                || (control.Minimum is not null || control.Maximum is not null) && reading.Value is null)
                Reject("preservation_measurement", "Supply the actual measurement and declared unit, or an explicit attribute observation.");
        }
    }
    internal static ResourceRef[] Own(IEnumerable<ResourceRef> resources, string key, string facility) => resources
        .Select(r => r with { Type = "CommercialQuality", Id = key, FacilityId = r.FacilityId ?? facility }).Distinct().ToArray();
    internal static string ScopeKey(CommercialContent content) => content.ProviderEvaluation is { } p ? "supplier:" + p.SupplierCode
        : content.PropertyAgreement is { } a ? PropertyKey(a.Subject)
        : content.CustomerRequirements is { } c ? CustomerScopeKey(c)
        : content.DesignApplicability is { } d ? "part:" + d.PartNumber
        : content.Communication?.Concession is{} concession ? ConcessionScopeKey(concession)
        : content.PropertyIncident is not null ? "all-incidents" : CanonicalJson.Fingerprint(content);
    internal static string PropertyKey(PropertySubject subject) => subject.Kind + ":" + subject.PartNumber + ":" + subject.Key;
    internal static string CustomerScopeKey(CustomerRequirementsContent content) => content.ContractId is{} contract
        ? "contract:"+contract.ToString(CultureInfo.InvariantCulture) : "shipment:"+content.CustomerSource!.Key;
    internal static string ConcessionScopeKey(CustomerConcessionScope scope) => "concession:"+AcceptanceSubjectReference.Format(scope.Subject)+":"+scope.NcrId.ToString(CultureInfo.InvariantCulture)+":"+scope.Disposition;
    private static CommandAuthority AuthorityFor(ResourceRef[] resources, string fingerprint) => new(resources[0], fingerprint, resources.Skip(1).ToArray());
    private void PermitAll(ForgePrincipal p, string permission, IEnumerable<ResourceRef> resources)
    { foreach (var resource in resources) { var decision = authorizer.Authorize(p, permission, resource); if (!decision.IsPermitted) throw new ForgeAuthorizationException(decision, resource); } }
    private static async Task<ForgePrincipal> CurrentPrincipalAsync(IKernelUnitOfWork uow, ForgePrincipal principal, CancellationToken ct) =>
        await uow.People.LoadPrincipalAsync(principal.PersonId, ct) is { IsActive: true } current ? current
            : throw new ForgeAuthorizationException(new(false, ["A current registered manufacturing principal is required."]));
    private void Configured() { if (!options.Configured) Reject("qms_not_configured", "Configure the organization's code before governing commercial records."); }
    private static CommercialRevision History(CommercialRevision next, CommercialState from, string action, string basis, CommandContext context, DateTimeOffset now) => next with
    { History = next.History.Append(new CommercialTransition(action, from, next.State, next.Version, context.Actor, now, basis.Trim(), context.Reason!.Trim())).ToArray() };
    private static void Version(CommercialRevision record, int expected) { if (record.Version != expected) Reject("stale_version", "Read the current commercial revision before deciding."); }
    public static string Key(CommercialRecordKind kind, string code, int revision) => kind + "/" + code + "/" + revision.ToString(CultureInfo.InvariantCulture);
    public static bool TryKey(string key, out CommercialRecordKind kind, out string code, out int revision)
    {
        var fields = key.Split('/'); kind = default; code = string.Empty; revision = 0;
        if (fields.Length != 3 || !Enum.TryParse(fields[0], out kind) || !Enum.IsDefined(kind) || !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out revision) || revision <= 0) return false;
        try { code = Code(fields[1]); return true; } catch (CommandRejectedException) { return false; }
    }
    private static string Cursor(CommercialRevision r) => r.Kind + "/" + r.Code + "/" + r.Revision.ToString("D10", CultureInfo.InvariantCulture);
    private static string Code(string code) => BusinessCode.Require(code, "Commercial record");
    private static CommandRejectedException Missing() => new("commercial_not_found", "The commercial record or its sources are unavailable.");
    private static void Reject(string code, string message) => throw new CommandRejectedException(code, message);
}
