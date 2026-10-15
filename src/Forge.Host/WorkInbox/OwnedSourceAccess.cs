using System.Globalization;
using Forge.Host.Identification;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.WorkAssignments;
using Forge.Quality.Contracts;
using Forge.Quality.Qms;

namespace Forge.Host.WorkInbox;

/// <summary>Host owner checks are additional to the caller's audit, notification or business permission.</summary>
public interface IOwnedSourceResolver
{
    Task<bool> CanReadOwnedSourceAsync(IKernelUnitOfWork uow, ForgePrincipal principal, string type, string id, CancellationToken ct);
}

public static class OwnedSourceFamilies
{
    public static bool Contains(string type) => AcceptanceNcrSourceAccess.Contains(type) || AcceptanceSourceAccess.Contains(type) || type is "QmsPolicyAdoption" or "QmsPolicyAdoptionRevision"
        or "LabelTemplate" or "LabelIssuance" or "LabelCopy" or "LabelPrintAttempt" or "IdentificationAlias"
        or "ScanSession" or "ScanAcceptance";

    /// <summary>Recognized owners fail closed when their actual Host owner hook is unavailable.</summary>
    public static Task<bool> CanReadAsync(IKernelUnitOfWork uow, IWorkSourceResolver sources, ForgePrincipal principal,
        string type, string id, CancellationToken ct) => sources is IOwnedSourceResolver owner
        ? owner.CanReadOwnedSourceAsync(uow, principal, type, id, ct) : Task.FromResult(false);

    public static (string Code, int? Revision)? AdoptionKey(string type, string id)
    {
        if (type == "QmsPolicyAdoption") return string.IsNullOrWhiteSpace(id) ? null : (id, null);
        if (type != "QmsPolicyAdoptionRevision") return null;
        var separator = id.LastIndexOf('/');
        return separator > 0 && int.TryParse(id.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture,
            out var revision) && revision > 0 ? (id[..separator], revision) : null;
    }
}

/// <summary>
/// Resolves services only during a request, after the container exists. This avoids the
/// pipeline/source/presentation-service construction cycle. All owner work uses the supplied UOW.
/// </summary>
public sealed class OwnedSourceAccess(IServiceProvider services)
{
    public async Task<bool> CanReadAsync(IKernelUnitOfWork uow, ForgePrincipal principal, string type, string id, CancellationToken ct)
    {
        if (!OwnedSourceFamilies.Contains(type)) return false;
        principal = await uow.People.LoadPrincipalAsync(principal.PersonId, ct) is { IsActive: true } current ? current : principal with { IsActive = false };
        if (!principal.IsActive) return false;
        try
        {
            if (AcceptanceNcrSourceAccess.Contains(type))
                return await services.GetRequiredService<AcceptanceNcrSourceAccess>().CanReadAsync(uow, principal, type, id, ct);
            if (AcceptanceSourceAccess.Contains(type))
                return await services.GetRequiredService<AcceptanceSourceAccess>().CanReadAsync(uow, principal, type, id, ct);
            if (type is "QmsPolicyAdoption" or "QmsPolicyAdoptionRevision")
            {
                var key = OwnedSourceFamilies.AdoptionKey(type, id);
                if (key is null) return false;
                var adoption = await uow.Store<IQmsPolicyAdoptionLookup>().FindAsync(key.Value.Code, ct);
                return adoption is not null && await services.GetRequiredService<QmsPolicyAdoptionService>()
                    .CanReadAsync(uow, principal, adoption, ct, key.Value.Revision);
            }
            if (type is "ScanSession" or "ScanAcceptance")
            {
                var selected = await SessionAsync(uow, type, id, ct);
                if (selected is null) return false;
                // An acceptance checks its saved selections, never later unrelated staging.
                // The owner helper admits its principal or actual-scope AuditRead, then all source reads.
                return await services.GetRequiredService<ScanSessionAuthority>().CanReadAsync(uow, principal, selected, ct);
            }
            return await services.GetRequiredService<LabelService>().RequireSourceReadAsync(uow, principal, type, id, ct) is not null;
        }
        catch (ForgeAuthorizationException) { return false; }
        catch (CommandRejectedException) { return false; }
        catch (IdentificationException) { return false; }
    }

    /// <summary>Own contexts only; mixed linked-source read permissions are enforced by CanReadAsync.</summary>
    public async Task<WorkSource?> ResolveAsync(IKernelUnitOfWork uow, string type, string id, CancellationToken ct)
    {
        if (!OwnedSourceFamilies.Contains(type)) return null;
        try
        {
            if (AcceptanceNcrSourceAccess.Contains(type))
                return await services.GetRequiredService<AcceptanceNcrSourceAccess>().ResolveAsync(uow, type, id, ct);
            if (AcceptanceSourceAccess.Contains(type))
                return await services.GetRequiredService<AcceptanceSourceAccess>().ResolveAsync(uow, type, id, ct);
            if (type is "QmsPolicyAdoption" or "QmsPolicyAdoptionRevision")
            {
                var key = OwnedSourceFamilies.AdoptionKey(type, id);
                if (key is null) return null;
                var adoption = await uow.Store<IQmsPolicyAdoptionLookup>().FindAsync(key.Value.Code, ct);
                var options = services.GetRequiredService<QmsOptions>();
                if (adoption is null || !options.Configured || adoption.OrganizationCode != options.OrganizationCode) return null;
                var revisions = adoption.Revisions.Where(revision => key.Value.Revision is null || revision.Revision == key.Value.Revision).ToArray();
                if (revisions.Length == 0) return null;
                return new(Permissions.QmsRead, revisions.Select(revision => QmsPolicyAdoptionService.Resource(adoption, revision)
                    with { Type = type, Id = id }).Distinct().ToArray(), revisions.Any(revision => revision.State is not (QmsState.Withdrawn or QmsState.Superseded)));
            }
            if (type is "ScanSession" or "ScanAcceptance")
            {
                var session = await SessionAsync(uow, type, id, ct);
                if (session is null) return null;
                var resolver = services.GetRequiredService<IWorkSourceResolver>();
                var operationId = session.OrderNumber.ToString(CultureInfo.InvariantCulture) + "/" + session.Sequence.ToString(CultureInfo.InvariantCulture);
                var current = await resolver.ResolveAsync(uow, "OrderOperation", operationId, ct);
                if (current is null || current.Resources.Count == 0) return null;
                var markings = session.Sources.Concat(session.Selections.SelectMany(selection => selection.Sources))
                    .Aggregate(session.Markings, (marks, source) => marks | source.Resource.Markings);
                var recorded = new ResourceRef(type, id, session.ProgramId is { } program ? new ProgramId(program) : null,
                    markings, FacilityId: session.FacilityCode, WorkCenterId: session.WorkCenterCode);
                var recordedOperations = session.Sources.Where(source => source.Permission == Permissions.OrdersRead
                    && source.Resource.Type == "OrderOperation" && source.Resource.Id == operationId).Select(source => source.Resource with
                    { Type = type, Id = id, Markings = source.Resource.Markings | markings }).ToArray();
                var scopes = current.Resources.Select(resource => resource with { Type = type, Id = id, Markings = resource.Markings | markings })
                    .Concat(recordedOperations.Length == 0 ? [recorded] : recordedOperations).Distinct().ToArray();
                return new(Permissions.OrdersRead, scopes, type == "ScanSession" && session.State == ScanSessionState.Active);
            }
            return await PresentationAsync(uow, type, id, ct);
        }
        catch (CommandRejectedException) { return null; }
        catch (IdentificationException) { return null; }
    }

    private static async Task<ScanSessionView?> SessionAsync(IKernelUnitOfWork uow, string type, string id, CancellationToken ct)
    {
        if (!Guid.TryParseExact(id, "D", out var parsed)) return null;
        var store = uow.Store<IScanSessionStore>();
        if (type == "ScanSession") return await store.FindAsync(parsed, ct);
        var accepted = await store.FindAcceptedAsync(parsed, ct);
        if (accepted is null) return null;
        var session = await store.FindAsync(accepted.SessionId, ct);
        return session is null ? null : session with { Selections = accepted.Entry.Selections, Accepted = [accepted.Entry] };
    }

    private async Task<WorkSource?> PresentationAsync(IKernelUnitOfWork uow, string type, string id, CancellationToken ct)
    {
        var store = uow.Store<IIdentificationPresentationStore>();
        if (type == "LabelTemplate")
        {
            var record = (await store.FindAsync<LabelTemplateView>("Template", id, ct))?.Record;
            return record is null ? null : new(Permissions.IdentificationRead,
                [new(type, id, Markings: record.Sources.Aggregate(DataMarking.None, (marks, source) => marks | source.Resource.Markings), FacilityId: record.FacilityCode)],
                record.State != LabelTemplateState.Withdrawn);
        }
        if (!Guid.TryParseExact(id, "D", out var parsed)) return null;
        LabelSubjectSnapshot? snapshot;
        IReadOnlyList<PresentationReadSource> captured;
        string facility;
        bool active;
        if (type == "IdentificationAlias")
        {
            var record = (await store.FindAsync<IdentificationAliasView>("Alias", id, ct))?.Record;
            if (record is null) return null;
            snapshot = record.Snapshot; captured = record.Sources; facility = record.FacilityCode; active = record.State == IdentificationAliasState.Active;
        }
        else if (type == "LabelIssuance")
        {
            var record = (await store.FindAsync<LabelIssuanceView>("Issuance", id, ct))?.Record;
            if (record is null) return null;
            snapshot = record.Snapshot; captured = record.Sources ?? []; facility = record.FacilityCode; active = true;
        }
        else
        {
            var copyId = parsed;
            LabelPrintAttemptView? attempt = null;
            if (type == "LabelPrintAttempt")
            {
                attempt = (await store.FindAsync<LabelPrintAttemptView>("PrintAttempt", id, ct))?.Record;
                if (attempt is null) return null;
                copyId = attempt.LabelId;
            }
            else if (type != "LabelCopy") return null;
            var copy = (await store.FindAsync<LabelCopyView>("Copy", copyId.ToString("D"), ct))?.Record;
            if (copy is null) return null;
            snapshot = attempt?.Snapshot ?? copy.Snapshot; captured = attempt?.Sources ?? copy.Sources;
            facility = copy.FacilityCode; active = copy.State is LabelCopyState.Active or LabelCopyState.PendingApproval;
        }
        if (snapshot is null) return null;
        var current = await services.GetRequiredService<IdentificationResolver>().ResolveSubjectFactsAsync(uow,
            snapshot.PresentationCode ?? snapshot.CanonicalSubjectCode, ct);
        var markings = captured.Aggregate(DataMarking.None, (marks, source) => marks | source.Resource.Markings);
        var resources = current.Resources.Concat(snapshot.Resources).Select(resource => PresentationAccess.OwnResource(resource, facility, type, id)
            with { Markings = resource.Markings | markings }).Distinct().ToArray();
        return resources.Length == 0 ? null : new(Permissions.IdentificationRead, resources, active);
    }
}
