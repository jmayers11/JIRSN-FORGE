using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;

namespace Forge.Host.Identification;

public sealed partial class IdentificationResolver
{
    private async Task<IdentificationSubject> ResolvePresentationAsync(IKernelUnitOfWork uow, ForgePrincipal principal, CanonicalIdentification parsed, CancellationToken ct)
    {
        principal = await uow.People.LoadPrincipalAsync(principal.PersonId, ct) ?? throw PresentationAccess.Missing();
        var reader = policyReader ?? throw new CommandRejectedException("label_policy_unavailable", "Presentation policy is unavailable.");
        var at = (time ?? throw new CommandRejectedException("identification_clock_unavailable", "Presentation requires the configured host clock.")).GetUtcNow();
        var access = new PresentationAccess(this, authorizer, sources, reader, configuration);
        var store = uow.Store<IIdentificationPresentationStore>();
        if (parsed.Kind == "label")
        {
            var copy = (await store.FindAsync<LabelCopyView>("Copy", PresentationAccess.Key(parsed.Id!.Value), ct))?.Record ?? throw PresentationAccess.Missing();
            if (copy.Code != parsed.Code || copy.State != LabelCopyState.Active) throw PresentationAccess.Missing();
            var subject = await access.CapturedSubjectAsync(uow, principal, copy.Snapshot, copy.Sources, ct);
            await RequireActivePrimitiveAsync(uow,subject,ct);
            PresentationReadRules.RequireCurrentIdentity(copy, subject);
            PermitSource(principal, Permissions.IdentificationRead, subject.Resources.Select(resource=>PresentationAccess.OwnResource(resource,copy.FacilityCode,"LabelCopy",PresentationAccess.Key(copy.LabelId))));
            var template = (await store.FindAsync<LabelTemplateView>("Template", PresentationAccess.TemplateKey(copy.TemplateCode, copy.TemplateVersion), ct))?.Record ?? throw PresentationAccess.Missing();
            var templateSources = (await store.HistoryAsync<LabelTemplateView>("Template", PresentationAccess.TemplateKey(copy.TemplateCode, copy.TemplateVersion), ct)).SelectMany(row=>row.Sources).Distinct().ToArray();
            await access.CurrentSourcesAsync(uow, principal, templateSources, ct);
            var policy = await access.PolicyAsync(uow, principal, subject, copy.FacilityCode, Permissions.IdentificationRead, at, ct,"LabelCopy",PresentationAccess.Key(copy.LabelId));
            var attempts=await store.SubjectRecordsAsync<LabelPrintAttemptView>("PrintAttempt",copy.SubjectCode,ct);
            foreach(var attempt in attempts.Where(attempt=>attempt.LabelId==copy.LabelId))
            {
                if(attempt.Snapshot is{} snapshot)await access.CapturedSubjectAsync(uow,principal,snapshot,attempt.Sources ?? [],ct);
                else if(attempt.Sources is{} captured)await access.CurrentSourcesAsync(uow,principal,captured,ct);
            }
            PresentationReadRules.RequireUsable(copy, template, policy, attempts);
            var fingerprint=CanonicalJson.Fingerprint(new{copy.LabelId,copy.Version,copy.State,copy.TemplateCode,copy.TemplateVersion,
                TemplateRecordVersion=template.Version,TemplateState=template.State,Policy=policy.Fingerprint,
                Attempts=attempts.Where(attempt=>attempt.LabelId==copy.LabelId).Select(attempt=>new{attempt.AttemptId,attempt.Version,attempt.State,attempt.ArtifactSha256,attempt.ObservedPrinted,attempt.ObservedAttached})});
            var readSources = subject.Resources.Select(resource => new PresentationReadSource(subject.ReadPermission, resource))
                .Concat(copy.Snapshot.Resources.Select(resource => new PresentationReadSource(copy.Snapshot.ReadPermission, resource)))
                .Concat(copy.Sources).Concat(templateSources)
                .Concat(policy.Sources.Select(source => new PresentationReadSource(source.Permission, source.Resource)))
                .Concat(subject.Resources.Select(resource => new PresentationReadSource(Permissions.IdentificationRead,
                    PresentationAccess.OwnResource(resource, copy.FacilityCode, "LabelCopy", PresentationAccess.Key(copy.LabelId)))))
                .Append(new(Permissions.IdentificationRead, new("LabelTemplate", PresentationAccess.TemplateKey(template.Code, template.Revision), FacilityId: template.FacilityCode)))
                .Concat(attempts.Where(attempt => attempt.LabelId == copy.LabelId).SelectMany(attempt =>
                    (attempt.Snapshot is { } retained ? retained.Resources.Select(resource => new PresentationReadSource(retained.ReadPermission, resource)) : [])
                        .Concat(attempt.Sources ?? []))).ToArray();
            return subject with { Display = subject.Display with { Code = parsed.Code }, LabelId = copy.LabelId,
                PresentationCode = parsed.Code, PresentationFingerprint = fingerprint,
                ReadSources = await CurrentReadSourcesAsync(uow, principal, readSources, ct) };
        }
        var alias = (await store.FindAsync<IdentificationAliasView>("Alias", PresentationAccess.Key(parsed.Id!.Value), ct))?.Record ?? throw PresentationAccess.Missing();
        if (alias.Code != parsed.Code || alias.State != IdentificationAliasState.Active) throw PresentationAccess.Missing();
        var target = await access.CapturedSubjectAsync(uow, principal, alias.Snapshot, alias.Sources, ct);
        await RequireActivePrimitiveAsync(uow,target,ct);
        PermitSource(principal, Permissions.IdentificationRead, target.Resources.Select(resource=>PresentationAccess.OwnResource(resource,alias.FacilityCode,"IdentificationAlias",PresentationAccess.Key(alias.AliasId))));
        var adopted = await access.PolicyAsync(uow, principal, target, alias.FacilityCode, Permissions.IdentificationRead, at, ct,"IdentificationAlias",PresentationAccess.Key(alias.AliasId));
        if (adopted.Rules.LabelCopy?.Lifecycle?.AllowedSubjectKinds?.Contains(target.PresentedKind ?? target.Display.Kind, StringComparer.Ordinal) != true)
            throw new CommandRejectedException("alias_policy", "The current adopted policy does not permit this alias use.");
        var aliasSources = target.Resources.Select(resource => new PresentationReadSource(target.ReadPermission, resource))
            .Concat(alias.Snapshot.Resources.Select(resource => new PresentationReadSource(alias.Snapshot.ReadPermission, resource)))
            .Concat(alias.Sources).Concat(adopted.Sources.Select(source => new PresentationReadSource(source.Permission, source.Resource)))
            .Concat(target.Resources.Select(resource => new PresentationReadSource(Permissions.IdentificationRead,
                PresentationAccess.OwnResource(resource, alias.FacilityCode, "IdentificationAlias", PresentationAccess.Key(alias.AliasId))))).ToArray();
        return target with { Display = target.Display with { Code = parsed.Code }, AliasId = alias.AliasId, PresentationCode = parsed.Code,
            PresentationFingerprint=CanonicalJson.Fingerprint(new{alias.AliasId,alias.Version,alias.State,Policy=adopted.Fingerprint}),
            ReadSources = await CurrentReadSourcesAsync(uow, principal, aliasSources, ct) };
    }

    /// <summary>Capture both retained and current typed source read refs for exact historical provenance.</summary>
    private async Task<IReadOnlyList<PresentationReadSource>> CurrentReadSourcesAsync(IKernelUnitOfWork uow,
        ForgePrincipal principal, IReadOnlyList<PresentationReadSource> captured, CancellationToken ct)
    {
        var result = new HashSet<PresentationReadSource>(captured);
        foreach (var source in captured.Distinct())
        {
            var decision = authorizer.Authorize(principal, source.Permission, source.Resource);
            if (!decision.IsPermitted) throw new ForgeAuthorizationException(decision, source.Resource);
            if (source.Resource.Id is not { } id || source.Resource.Type is "Lot" or "Person" or "TransformationOutput") continue;
            var current = await sources.ResolveAsync(uow, source.Resource.Type, id, ct);
            if (current is null)
            {
                if (Forge.Host.WorkInbox.OwnedSourceFamilies.Contains(source.Resource.Type)) throw PresentationAccess.Missing();
                continue; // Primitive point-owner refs already include their current facts above.
            }
            foreach (var resource in current.Resources)
            {
                decision = authorizer.Authorize(principal, current.ReadPermission, resource);
                if (!decision.IsPermitted) throw new ForgeAuthorizationException(decision, resource);
                result.Add(new(current.ReadPermission, resource));
            }
        }
        return result.OrderBy(source => source.Permission, StringComparer.Ordinal)
            .ThenBy(source => source.Resource.Type, StringComparer.Ordinal).ThenBy(source => source.Resource.Id, StringComparer.Ordinal)
            .ThenBy(source => CanonicalJson.Serialize(source.Resource), StringComparer.Ordinal).ToArray();
    }

    /// <summary>Explicit configured-domain legacy lookup. Raw text is data; no inferred bare-lot or cross-domain search.</summary>
    public async Task<IdentificationSubject> ResolveAuthorityAliasAsync(IKernelUnitOfWork uow, ForgePrincipal principal, string domain, string alias, CancellationToken ct)
    {
        string ns;
        try { ns = GetConfiguration().Namespace; }
        catch(IdentificationException error) { throw new CommandRejectedException(error.Code,error.Message); }
        if (!CanonicalIdentification.IsValidNamespace(domain) || string.IsNullOrEmpty(alias) || alias.Length > 1024 || alias.Any(char.IsControl))
            throw new CommandRejectedException("alias_key", "Use an explicit configured domain and bounded exact alias text.");
        var record = await uow.Store<IIdentificationPresentationStore>().FindActiveAliasAsync(ns,domain,alias,ct) ?? throw PresentationAccess.Missing();
        return await ResolveAuthorityPresentationAsync(uow,principal,record.Code,ct);
    }

    public async Task<ResolvedIdentification> ResolveAliasAsync(ForgePrincipal principal, string domain, string alias, CancellationToken ct)
    {
        _ = GetConfiguration();
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        try { return (await ResolveAuthorityAliasAsync(uow,principal,domain,alias,ct)).Display; }
        catch (ForgeAuthorizationException) { throw NotFound(); }
        catch (CommandRejectedException) { throw NotFound(); }
    }
}

public static class PresentationReadRules
{
    public static void RequireCurrentIdentity(LabelCopyView copy, IdentificationSubject current)
    {
        var recorded = copy.Snapshot.Display;
        if (copy.SubjectCode != current.CanonicalSubjectCode || recorded.PartNumber != current.Display.PartNumber || recorded.Revision != current.Display.Revision
            || recorded.SourceFingerprint is { } fingerprint && fingerprint != current.Display.SourceFingerprint)
            throw new CommandRejectedException("label_source_changed", "The printed source configuration changed. Review and authorize a replacement label.");
    }

    public static void RequireUsable(LabelCopyView copy, LabelTemplateView template, QmsAdoptedPolicy policy, IReadOnlyList<LabelPrintAttemptView> allAttempts)
    {
        var life = policy.Rules.LabelCopy?.Lifecycle ?? throw new CommandRejectedException("label_policy_incomplete", "Adopt explicit label lifecycle rules.");
        var kind = copy.Snapshot.PresentedKind ?? copy.Subject.Kind;
        if (copy.State != LabelCopyState.Active || template.State != LabelTemplateState.Approved
            || life.AllowedSubjectKinds?.Contains(kind, StringComparer.Ordinal) != true
            || !template.AllowedKinds.Contains(kind, StringComparer.Ordinal) || !template.AllowedSymbologies.Contains(copy.Symbology)
            || life.AllowedTemplates?.Any(reference => reference.Code == template.Code && reference.Version == template.Revision) != true
            || life.AllowedSymbologies?.Any(symbology => symbology.ToString() == copy.Symbology.ToString()) != true)
            throw new CommandRejectedException("label_inactive", "The copy or its current configuration is unavailable.");
        var confirmation = life.RequiredConfirmation ?? throw new CommandRejectedException("label_policy_incomplete", "Adopt explicit print confirmation rules.");
        var attempts = allAttempts.Where(row => row.LabelId == copy.LabelId).ToArray();
        if (attempts.Any(row => row.State is LabelPrintState.Requested or LabelPrintState.Sent or LabelPrintState.SpoolAccepted or LabelPrintState.Uncertain))
            throw new CommandRejectedException("print_reconciliation_required", "Resolve every uncertain print attempt before using this label.");
        if (confirmation != QmsLabelConfirmation.None && !attempts.Any(row => row.State == LabelPrintState.Confirmed && row.ObservedPrinted
            && (confirmation != QmsLabelConfirmation.OperatorObservedPrintingAndAttachment || row.ObservedAttached)))
            throw new CommandRejectedException("label_confirmation_required", "The adopted policy requires a recorded physical observation.");
    }
}
