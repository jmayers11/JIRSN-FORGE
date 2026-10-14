using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;

namespace Forge.Quality.Qms;

/// <summary>Current source access is separate from immutable adoption evidence and historical approval.</summary>
internal sealed class QmsPolicyCurrentAccess(QmsService qms, IForgeAuthorizer authorizer)
{
    public async Task<(QmsRecordView Record,QmsRevisionView Revision,IReadOnlyList<QmsPolicyReadSource> Sources)> RequireAsync(
        IKernelUnitOfWork uow, ForgePrincipal principal, string rawCode, int number, QmsRecordKind kind,
        bool requireEffective, bool lockSources, DateTimeOffset at, CancellationToken ct)
    {
        var code=BusinessCode.Require(rawCode,"QMS source");
        if(lockSources) await uow.Store<IQmsStore>().LockSourceAsync("QmsRevision",code,number.ToString(CultureInfo.InvariantCulture),ct);
        var record=await uow.Store<IQmsLookup>().FindAsync(code,ct)??throw Unavailable();
        var revision=record.Revisions.FirstOrDefault(r=>r.Revision==number)??throw Unavailable();
        Permit(principal,Permissions.QmsRead,QmsService.Resource(record));
        if(record.Kind!=kind) throw Unavailable();
        if(requireEffective) Effective(revision,at);
        var sources=new List<QmsPolicyReadSource>{new(Permissions.QmsRead,QmsService.Resource(record) with {Type="QmsRevision",Id=code+"/"+number.ToString(CultureInfo.InvariantCulture)})};
        await CollectAsync(uow,principal,revision,requireEffective,lockSources,at,new HashSet<string>(StringComparer.Ordinal){code+"/"+number.ToString(CultureInfo.InvariantCulture)},sources,ct);
        if(!await qms.CanReadAsync(uow,principal,record,ct,number)) throw Unavailable();
        return(record,revision,sources.Distinct().ToArray());
    }

    private async Task CollectAsync(IKernelUnitOfWork uow,ForgePrincipal principal,QmsRevisionView revision,bool requireEffective,
        bool lockSources,DateTimeOffset at,HashSet<string> visited,List<QmsPolicyReadSource> sources,CancellationToken ct)
    {
        foreach(var source in revision.Sources)
        {
            var permission=source.Kind=="DocumentRevision"?Permissions.EngineeringRead:Permissions.QmsRead;
            var recorded=new ResourceRef(source.Kind,source.Key+"/"+source.Revision,Program(source.ProgramId),source.Markings,FacilityId:source.FacilityCode);
            Permit(principal,permission,recorded); sources.Add(new(permission,recorded));
            if(lockSources) await uow.Store<IQmsStore>().LockSourceAsync(source.Kind,source.Key,source.Revision,ct);
            if(source.Kind=="DocumentRevision")
            {
                var document=await uow.Store<IDocumentLookup>().FindDocumentAsync(source.Key,ct)??throw Unavailable();
                var current=new ResourceRef("DocumentRevision",source.Key+"/"+source.Revision,Program(document.ProgramId),document.Markings);
                Permit(principal,Permissions.EngineeringRead,current); sources.Add(new(Permissions.EngineeringRead,current));
                var documentRevision=document.Revisions.FirstOrDefault(r=>r.Revision==source.Revision)??throw Unavailable();
                if(requireEffective)
                {
                    var day=DateOnly.FromDateTime(at.UtcDateTime);
                    if(document.Authority==DocumentAuthority.Reference || documentRevision.Status is not(DocumentRevisionStatus.Released or DocumentRevisionStatus.Applicable)
                        || documentRevision.EffectiveFrom is null || documentRevision.EffectiveFrom>day || documentRevision.EffectiveTo is{} end && end<=day) throw Unavailable();
                }
            }
            else if(source.Kind=="QmsRevision")
            {
                var linked=await uow.Store<IQmsLookup>().FindAsync(source.Key,ct)??throw Unavailable();
                var number=int.Parse(source.Revision,CultureInfo.InvariantCulture);
                var selected=linked.Revisions.FirstOrDefault(r=>r.Revision==number)??throw Unavailable();
                Permit(principal,Permissions.QmsRead,QmsService.Resource(linked));
                sources.Add(new(Permissions.QmsRead,QmsService.Resource(linked) with {Type="QmsRevision",Id=source.Key+"/"+source.Revision}));
                if(requireEffective) Effective(selected,at);
                if(visited.Add(source.Key+"/"+source.Revision)) await CollectAsync(uow,principal,selected,requireEffective,lockSources,at,visited,sources,ct);
                if(!await qms.CanReadAsync(uow,principal,linked,ct,number)) throw Unavailable();
            }
            else throw Unavailable();
        }
    }

    private static void Effective(QmsRevisionView revision,DateTimeOffset at)
    {
        if(revision.State!=QmsState.Effective || revision.EffectiveFrom is null || revision.EffectiveFrom>at || revision.EffectiveTo is{} end && end<=at
            || revision.ReviewedBy is null || revision.ApprovedBy is null
            || !revision.History.Any(h=>h.Action=="Review"&&h.Actor==revision.ReviewedBy)
            || !revision.History.Any(h=>h.Action=="Approve"&&h.Actor==revision.ApprovedBy)) throw Unavailable();
    }
    private void Permit(ForgePrincipal principal,string permission,ResourceRef resource)
    {
        var decision=authorizer.Authorize(principal,permission,resource);
        if(!decision.IsPermitted) throw new ForgeAuthorizationException(decision,resource);
    }
    private static ProgramId? Program(Guid? id)=>id is{} value?new ProgramId(value):null;
    private static CommandRejectedException Unavailable()=>new("governed_policy_source_unavailable","A currently authorized effective governance source is unavailable.");
}
