using Forge.Kernel.Access;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;

namespace Forge.Quality.Commercial;

public sealed partial class CommercialQualityService
{
    public static Task RecordAcceptedUseAsync(IKernelUnitOfWork uow,CommercialAcceptedUse use,CancellationToken ct)=>uow.Store<ICommercialUseStore>().InsertAsync(use,ct);
    public async Task<bool> CanReadAuditAsync(IKernelUnitOfWork uow,ForgePrincipal principal,Guid auditId,CancellationToken ct)
    {
        var use=await uow.Store<ICommercialUseLookup>().FindForAuditAsync(auditId,ct);
        if(use is null)return true;
        try
        {
            await sources.RequireCapturedAsync(uow,principal,use.Evidence.Sources,ct);
            foreach(var record in use.Evidence.Records)
            {
                var saved=await uow.Store<ICommercialQualityLookup>().FindVersionAsync(record.Kind,record.Code,record.Revision,record.Version,ct);
                if(saved is null||Forge.Kernel.Serialization.CanonicalJson.Fingerprint(saved)!=Forge.Kernel.Serialization.CanonicalJson.Fingerprint(record))return false;
                await AuthorizedSnapshotSourcesAsync(uow,principal,record,ct);
            }
            return true;
        }
        catch(ForgeAuthorizationException){return false;}
        catch(Forge.Kernel.Commands.CommandRejectedException){return false;}
    }
    public async Task<IReadOnlyList<CommercialReadSource>> AuthorizedSnapshotSourcesAsync(IKernelUnitOfWork uow,ForgePrincipal principal,
        CommercialRevision snapshot,CancellationToken ct)
    {
        principal=await CurrentPrincipalAsync(uow,principal,ct);
        var actual=await uow.Store<ICommercialQualityLookup>().FindAsync(snapshot.Kind,snapshot.Code,snapshot.Revision,ct);
        if(actual is null||actual.OrganizationCode!=options.OrganizationCode||actual.Content!=snapshot.Content
            &&Forge.Kernel.Serialization.CanonicalJson.Fingerprint(actual.Content)!=Forge.Kernel.Serialization.CanonicalJson.Fingerprint(snapshot.Content))throw Missing();
        var current=await sources.ResolveAsync(uow,principal,snapshot.FacilityCode,snapshot.Kind,snapshot.Content,ct);
        var result=current.Sources.Concat(snapshot.Sources).Concat(snapshot.Observations.SelectMany(o=>o.Sources)).ToList();
        var ownership=current.Ownership.Concat(snapshot.Ownership).Concat(snapshot.Observations.SelectMany(o=>o.Ownership)).ToList();
        foreach(var observation in snapshot.Observations)
        {
            var currentObservation=await sources.ResolveReferencesAsync(uow,principal,snapshot.FacilityCode,observation.Evidence,ct);
            result.AddRange(currentObservation.Sources);ownership.AddRange(currentObservation.Ownership);
        }
        await sources.RequireCapturedAsync(uow,principal,result.Distinct().ToArray(),ct);
        var own=Own(ownership,Key(snapshot.Kind,snapshot.Code,snapshot.Revision),snapshot.FacilityCode);
        PermitAll(principal,Permissions.QualityRead,own);
        result.AddRange(own.Select(r=>new CommercialReadSource(Permissions.QualityRead,r)));
        return result.Distinct().ToArray();
    }
}

