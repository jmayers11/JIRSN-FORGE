using System.Globalization;
using Forge.Competency.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Postgres;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;
using Forge.Quality.Audit;
using Forge.Quality.Qms;
using static Forge.Quality.Planning.QmsPlanningRules;

namespace Forge.Host.Qms;

/// <summary>Loads complete actual owner IDs and mixed sources in the calling UOW. The supplied callback avoids reader/service DI cycles.</summary>
public sealed class AuditCapaOwnerEvidenceResolver(IForgeAuthorizer authorizer,QmsOptions options,TimeProvider time):IQmsPlanningOwnerEvidenceResolver
{
    private static readonly string[] Types=["QmsAuditProgramRevision","QmsAuditorCompetence","QmsAudit","QmsAuditFinding","QmsAuditFindingOrigin","QmsCapaInvestigation","QmsCapaAction","QmsCapaEffectiveness","Capa"];
    public bool Owns(string context,string type)=>context=="Quality"&&Types.Contains(type,StringComparer.Ordinal)||context=="Competency"&&type=="PersonQualification";
    public async Task<QmsPlanningSourceSnapshot> ResolveAsync(IKernelUnitOfWork uow,ForgePrincipal principal,QmsPlanningEvidenceReference reference,bool lockSource,
        Func<QmsPlanningEvidenceReference,bool,Task<QmsPlanningSourceSnapshot>> resolveSource,CancellationToken ct)
    {
        var pg=uow as PostgresUnitOfWork??throw QmsAuditService.Missing();object content;ResourceRef resource;
        var saved=new List<QmsPlanningSourceSnapshot>();var links=new List<QmsPlanningEvidenceReference>();var authors=new List<string>();
        DateTimeOffset? created=null,approved=null,closed=null;var permission=Permissions.QualityRead;var licensed=false;
        if(reference.Context=="Competency")
        {
            var parts=reference.Key.Split(':',2);Require(parts.Length==2,"audit_qualification","Use an actual person:qualification owner key.");var person=Uuid(parts[0]);Require(parts[1]==BusinessCode.Require(parts[1],"Qualification"),"audit_qualification","Use the exact canonical qualification key.");
            if(lockSource){await using var row=pg.Command("SELECT code FROM competency.person_qualification WHERE person_id=$1 AND code=$2 FOR SHARE",person,parts[1]);await row.ExecuteScalarAsync(ct);}
            var record=(await uow.Store<ICompetencyLookup>().GetQualificationsAsync([new PersonId(person)],ct)).SingleOrDefault(x=>x.Code==parts[1])??throw QmsAuditService.Missing();content=record;resource=ResourceRef.Of("PersonQualification",reference.Key);permission=Permissions.CompetencyRead;created=record.CertifiedAt;
            await using var actor=pg.Command("SELECT updated_by FROM competency.person_qualification WHERE person_id=$1 AND code=$2",person,parts[1]);if(await actor.ExecuteScalarAsync(ct)is string who)authors.Add(who);
        }
        else
        {
            Require(options.Configured,"qms_not_configured","Configure the explicit organization.");var lookup=uow.Store<IAuditCapaLookup>();
            if(lockSource){await using var row=pg.Command("SELECT entity_id FROM quality.audit_head WHERE entity_type=$1 AND entity_id=$2 FOR SHARE",StorageType(reference.Type),reference.Revision is null?reference.Key:reference.Key+"/"+reference.Revision);await row.ExecuteScalarAsync(ct);}
            if(reference.Type=="QmsAuditProgramRevision")
            {
                Require(int.TryParse(reference.Revision,out var revision)&&revision>0&&reference.Revision==revision.ToString(CultureInfo.InvariantCulture)&&reference.Key==BusinessCode.Require(reference.Key,"Audit program"),"audit_revision","Identify the exact canonical program and revision.");var program=await lookup.FindProgramAsync(reference.Key,ct)??throw QmsAuditService.Missing();var r=program.Revisions.SingleOrDefault(x=>x.Revision==revision)??throw QmsAuditService.Missing();
                Org(program.OrganizationCode);content=r;resource=QmsAuditService.Resource(program) with{Type=reference.Type,Id=reference.Key+"/"+reference.Revision};saved.AddRange(r.Sources);authors.AddRange(new[]{r.CreatedBy,r.LastEditedBy}.Concat(r.History.Where(x=>x.Action is "Create" or "Edit").Select(x=>x.Actor)));created=program.CreatedAt;approved=r.History.LastOrDefault(x=>x.Action=="Approve")?.At;licensed=r.RequiresLicensedVerification;
            }
            else if(reference.Type=="QmsAuditorCompetence")
            {
                var r=await lookup.FindCompetenceAsync(Uuid(reference.Key),ct)??throw QmsAuditService.Missing();var program=await lookup.FindProgramAsync(r.AuditProgram.Code,ct)??throw QmsAuditService.Missing();Org(program.OrganizationCode);content=r;resource=QmsAuditService.Resource(program) with{Type=reference.Type,Id=reference.Key};saved.AddRange(r.Evidence);links.Add(new("Quality","QmsAuditProgramRevision",r.AuditProgram.Code,r.AuditProgram.Revision.ToString()));authors.Add(r.AssessedBy);created=r.History[0].At;approved=r.ApprovedAt;
            }
            else if(reference.Type is "QmsAudit" or "QmsAuditFinding" or "QmsAuditFindingOrigin")
            {
                QmsAuditFinding? finding=reference.Type=="QmsAuditFinding"?await lookup.FindFindingAsync(Uuid(reference.Key),ct)??throw QmsAuditService.Missing():reference.Type=="QmsAuditFindingOrigin"?await lookup.FindFindingOriginAsync(Uuid(reference.Key),ct)??throw QmsAuditService.Missing():null;
                var run=await lookup.FindAuditAsync(finding?.AuditId??Uuid(reference.Key),ct)??throw QmsAuditService.Missing();Org(run.OrganizationCode);resource=QmsAuditService.Resource(run) with{Type=reference.Type,Id=reference.Key};links.Add(new("Quality","QmsAuditProgramRevision",run.AuditProgram.Code,run.AuditProgram.Revision.ToString()));
                if(finding is null){var versions=await lookup.AuditVersionsAsync(run.Id,ct);var findings=await lookup.FindingsAsync(run.Id,ct);content=new{run,versions,findings};foreach(var version in versions){saved.AddRange(version.Sources);saved.AddRange(version.Report?.Evidence??[]);}links.AddRange(findings.Select(f=>new QmsPlanningEvidenceReference("Quality","QmsAuditFinding",f.Id.ToString())));authors.AddRange(run.Auditors.Select(x=>"person:"+x.PersonId));authors.Add(run.CreatedBy);if(run.Report is{} report)authors.Add(report.AuthoredBy);created=run.CreatedAt;approved=run.Report?.ApprovedAt;closed=run.CompletedAt;}
                else{content=finding;saved.AddRange(finding.Evidence);authors.Add(finding.RecordedBy);created=finding.RecordedAt;approved=finding.VerifiedAt;if(reference.Type!="QmsAuditFindingOrigin"){links.AddRange(finding.CapaIds.Select(x=>new QmsPlanningEvidenceReference("Quality","Capa",x.ToString())));links.AddRange(finding.PlanningTaskIds.Select(x=>new QmsPlanningEvidenceReference("Quality","QmsPlanningTask",x.ToString())));}}
            }
            else
            {
                QmsCapaCase capa;content=null!;
                if(reference.Type=="Capa")
                {
                    Require(long.TryParse(reference.Key,out var id)&&id>0&&reference.Key==id.ToString(CultureInfo.InvariantCulture),"audit_capa","Use the exact original core CAPA ID.");
                    if(lockSource){await using var row=pg.Command("SELECT capa_id FROM quality.capa WHERE capa_id=$1 FOR SHARE",id);await row.ExecuteScalarAsync(ct);}
                    var core=await uow.Store<Forge.Quality.IQualityStore>().FindCapaAsync(id,ct)??throw QmsAuditService.Missing();var governed=await lookup.FindCapaAsync(id,ct);
                    if(governed is null)
                    {
                        Require(!core.Governed,"capa_governance_unavailable","The governed owner metadata is unavailable.");resource=await Forge.Quality.QualityService.ResourceForCapaAsync(uow,core,ct);content=core;created=core.OpenedAt;approved=core.VerifiedAt;closed=core.VerifiedAt;authors.Add(core.OpenedBy);if(core.ImplementedBy is{} impl)authors.Add(impl);
                        if(core.NcrId is{} ncr)links.Add(new("Quality","Ncr",ncr.ToString()));
                        return await Snapshot();
                    }
                    capa=governed;content=new{core,capa};created=capa.CreatedAt;approved=core.VerifiedAt;closed=capa.State==QmsCapaState.Closed?core.VerifiedAt:null;
                    // Whole governed CAPA visibility covers original historic child sources, without presentation caps.
                    foreach(var i in await lookup.InvestigationsAsync(id,0,ct)){saved.AddRange(i.Sources);authors.AddRange(i.History.Where(x=>x.Action is "Create" or "Edit").Select(x=>x.Actor));}
                    foreach(var a in await lookup.ActionsAsync(id,0,ct)){saved.AddRange(a.Evidence);authors.Add(a.CreatedBy);if(a.CompletedBy is{} by)authors.Add(by);}
                    foreach(var e in await lookup.EffectivenessAsync(id,0,ct)){saved.AddRange(e.Evidence);authors.Add(e.CreatedBy);}
                }
                else if(reference.Type=="QmsCapaInvestigation")
                {
                    var parts=reference.Key.Split('/');Require(parts.Length==2&&long.TryParse(parts[0],out var owner)&&owner>0&&parts[0]==owner.ToString(CultureInfo.InvariantCulture)&&int.TryParse(parts[1],out var generation)&&generation>0&&parts[1]==generation.ToString(CultureInfo.InvariantCulture)&&int.TryParse(reference.Revision,out var revision)&&revision>0&&reference.Revision==revision.ToString(CultureInfo.InvariantCulture),"audit_capa","Use canonical original CAPA/generation and exact investigation revision.");
                    var id=long.Parse(parts[0]);var i=(await lookup.InvestigationsAsync(id,int.Parse(parts[1]),ct)).SingleOrDefault(x=>x.Revision.ToString()==reference.Revision)??throw QmsAuditService.Missing();capa=await lookup.FindCapaAsync(id,ct)??throw QmsAuditService.Missing();content=i;saved.AddRange(i.Sources);authors.AddRange(i.History.Where(x=>x.Action is "Create" or "Edit").Select(x=>x.Actor));created=i.History[0].At;approved=i.History.LastOrDefault(x=>x.Action=="Approve")?.At;
                }
                else if(reference.Type=="QmsCapaAction")
                {
                    var a=await lookup.FindActionAsync(Uuid(reference.Key),ct)??throw QmsAuditService.Missing();capa=await lookup.FindCapaAsync(a.CapaId,ct)??throw QmsAuditService.Missing();content=a;saved.AddRange(a.Evidence);authors.AddRange(new[]{a.CreatedBy,"person:"+a.OwnerPersonId,a.CompletedBy??""});created=a.CreatedAt;approved=a.VerifiedAt;closed=a.State==QmsCorrectiveActionState.Verified?a.VerifiedAt:null;
                }
                else
                {
                    var e=await lookup.FindEffectivenessAsync(Uuid(reference.Key),ct)??throw QmsAuditService.Missing();capa=await lookup.FindCapaAsync(e.CapaId,ct)??throw QmsAuditService.Missing();content=e;saved.AddRange(e.Evidence);authors.Add(e.CreatedBy);created=e.History[0].At;approved=e.History.LastOrDefault(x=>x.Action=="Approve")?.At;
                    links.AddRange(e.VerifiedActionIds.Select(x=>new QmsPlanningEvidenceReference("Quality","QmsCapaAction",x.ToString())));
                }
                Org(capa.OrganizationCode);resource=QmsAuditService.Resource(capa) with{Type=reference.Type,Id=reference.Revision is null?reference.Key:reference.Key+"/"+reference.Revision};saved.AddRange(capa.Sources);authors.Add(capa.CreatedBy);
            }
        }
        return await Snapshot();
        async Task<QmsPlanningSourceSnapshot> Snapshot()
        {
            var sources=new List<QmsPlanningReadSource>{new(permission,resource)};
            foreach(var old in saved.DistinctBy(x=>x.Reference)){foreach(var read in old.Sources)Permit(principal,read);sources.AddRange(old.Sources);var current=await resolveSource(old.Reference,lockSource);sources.AddRange(current.Sources);licensed|=current.RequiresLicensedVerification;}
            foreach(var link in links.Distinct()){var actual=await resolveSource(link,lockSource);sources.AddRange(actual.Sources);licensed|=actual.RequiresLicensedVerification;}
            foreach(var read in sources)Permit(principal,read);
            var captured=new{content,sources=sources.Distinct().OrderBy(x=>x.Permission).ThenBy(x=>x.Resource.Type).ThenBy(x=>x.Resource.Id).ToArray()};
            return new(reference,reference.Context,reference.Type,reference.Key,reference.Revision,CanonicalJson.Fingerprint(captured),time.GetUtcNow(),created,approved,closed,null,authors.Where(x=>x.Length>0).Distinct().ToArray(),sources.Distinct().ToArray(),licensed,CanonicalJson.Serialize(captured));
        }
    }
    private static string StorageType(string type)=>type switch{"QmsAuditProgramRevision"=>"ProgramRevision","QmsAuditorCompetence"=>"Competence","QmsAudit"=>"Audit","QmsAuditFinding" or "QmsAuditFindingOrigin"=>"Finding","QmsCapaInvestigation"=>"Investigation","QmsCapaAction"=>"Action","QmsCapaEffectiveness"=>"Effectiveness",_=>"Capa"};
    private void Org(string org)=>Require(org==options.OrganizationCode,"audit_not_found","The owner source is unavailable.");
    private static Guid Uuid(string value){Require(Guid.TryParse(value,out var id)&&id!=Guid.Empty&&value==id.ToString(),"audit_not_found","The canonical actual owner source is unavailable.");return id;}
    private void Permit(ForgePrincipal p,QmsPlanningReadSource source){var result=authorizer.Authorize(p,source.Permission,source.Resource);if(!result.IsPermitted)throw new ForgeAuthorizationException(result);}
}

public sealed class AuditorQualificationReader(IQmsPlanningEvidenceReader reader):IAuditorQualificationReader
{
    public async Task ValidateCodesAsync(IKernelUnitOfWork uow,IReadOnlyList<string> codes,bool lockSources,CancellationToken ct)
    {
        foreach(var code in codes)
        {
            Require(code==BusinessCode.Require(code,"Qualification"),"audit_qualification","Use the canonical actual qualification code.");
            if(lockSources){var pg=uow as PostgresUnitOfWork??throw QmsAuditService.Missing();await using var row=pg.Command("SELECT code FROM competency.qualification_type WHERE code=$1 FOR SHARE",code);await row.ExecuteScalarAsync(ct);}
            Require(await uow.Store<ICompetencyLookup>().QualificationTypeExistsAsync(code,ct),"audit_qualification","Use an actual configured qualification type.");
        }
    }
    public async Task<IReadOnlyList<QmsAuditQualificationFact>> QualificationsAsync(IKernelUnitOfWork uow,ForgePrincipal principal,Guid personId,
        IReadOnlyList<string> codes,DateTimeOffset asOf,bool lockSources,CancellationToken ct)
    {
        var result=new List<QmsAuditQualificationFact>();
        foreach(var code in codes)
        {
            Require(await uow.Store<ICompetencyLookup>().QualificationTypeExistsAsync(code,ct),"audit_qualification","Use an actual configured qualification type.");
            var current=await reader.ResolveAsync(uow,principal,new("Competency","PersonQualification",personId+":"+code),lockSources,ct);
            var record=(await uow.Store<ICompetencyLookup>().GetQualificationsAsync([new PersonId(personId)],ct)).Single(x=>x.Code==code);
            result.Add(new(code,record.Status.ToString(),record.CertifiedAt,record.ExpiresAt,AuditCapaRules.QualifiedAsOf(record.Status.ToString(),record.CertifiedAt,record.ExpiresAt,asOf),current));
        }
        return result;
    }
}
