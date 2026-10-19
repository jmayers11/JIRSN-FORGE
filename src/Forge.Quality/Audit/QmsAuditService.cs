using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;
using Forge.Quality.Planning;
using Forge.Quality.Qms;
using Forge.Engineering.Contracts;
using static Forge.Quality.Planning.QmsPlanningRules;
using static Forge.Quality.Planning.QmsPlanningService;
using Forge.Resources.Contracts;

namespace Forge.Quality.Audit;

public sealed record DefineAuditProgram(string Code,string FacilityCode,Guid? ProgramId,DataMarking Markings,QmsAuditProgramContent Content);
public sealed record ReviseAuditProgram(string Code,int? Revision,int ExpectedVersion,QmsAuditProgramContent Content);
public sealed record AuditProgramDecision(string Code,int Revision,int ExpectedVersion,string Action,string Note);
public sealed record AssessAuditor(Guid PersonId,QmsRevisionReference AuditProgram,QmsAuditCompetenceDecision Decision,
    DateTimeOffset ValidFrom,DateTimeOffset ValidTo,string Criteria,string ActualAssessment,string Limitations,IReadOnlyList<QmsPlanningEvidenceReference> Evidence);
public sealed record ApproveAuditorAssessment(Guid Id,int ExpectedVersion,string Note);
public sealed record ScheduleAudit(QmsRevisionReference AuditProgram,string Scope,DateTimeOffset From,DateTimeOffset To,
    IReadOnlyList<QmsAuditAssignment> Auditors,IReadOnlyList<QmsAuditorValidation> Competence);
public sealed record DecideAudit(Guid Id,int ExpectedVersion,int Generation,string Action,string Note);
public sealed record RecordAuditFinding(Guid AuditId,int ExpectedAuditVersion,int Generation,string CriterionCode,
    QmsAuditFindingKind Kind,string Statement,string ActualObservation,QmsAuditAdequacy Adequacy,string AdequacyBasis,
    string Limitations,bool? FollowUpRequired,IReadOnlyList<QmsPlanningEvidenceReference> Evidence,IReadOnlyList<long> CapaIds,IReadOnlyList<Guid> PlanningTaskIds);
public sealed record FollowUpAuditFinding(Guid Id,int ExpectedVersion,int Generation,string Action,string Result,
    IReadOnlyList<QmsPlanningEvidenceReference> Evidence,IReadOnlyList<long> CapaIds,IReadOnlyList<Guid> PlanningTaskIds);
public sealed record WriteAuditReport(Guid Id,int ExpectedVersion,int Generation,string Summary,string ScopePerformed,
    string EvidenceLimitations,string HumanAssessment,IReadOnlyList<Guid> FindingIds,IReadOnlyList<QmsPlanningEvidenceReference> Evidence,
    IReadOnlyList<QmsAuditChecklistAnswer>? ChecklistAnswers=null);
public sealed record AuditCapaChanged(string EntityType,string EntityId,int Version,string Action,string State):IDomainEvent
{
    public static string EventType=>nameof(AuditCapaChanged);
    public static int SchemaVersion=>1;
}

public sealed class QmsAuditService(CommandPipeline pipeline,IKernelUnitOfWorkFactory factory,QmsOptions options,
    QmsPlanningService planning,IQmsPlanningEvidenceReader evidence,IAuditorQualificationReader qualifications):IAuditorCompetenceValidator
{
    public object Status=>new{configured=options.Configured,organizationCode=options.OrganizationCode};
    public async Task<object> ReadAsync(ForgePrincipal p,string type,string key,CancellationToken ct)
    {
        await using var u=await factory.BeginSnapshotAsync(ct);
        if(type=="Program"){var r=await ProgramAsync(u,key,ct);await Authority(u,p,Resource(r),r.OrganizationCode,r.Revisions.SelectMany(x=>x.Sources),false,ct);return r;}
        if(type=="Competence"){var r=await u.Store<IAuditCapaLookup>().FindCompetenceAsync(Guid.Parse(key),ct)??throw Missing();var program=await ProgramAsync(u,r.AuditProgram.Code,ct);await Authority(u,p,Resource(program),program.OrganizationCode,r.Evidence,false,ct);return r;}
        var run=await RunAsync(u,Guid.Parse(key),ct);await RunAuthority(u,p,run,false,ct);
        var findings=await u.Store<IAuditCapaLookup>().FindingsAsync(run.Id,ct);
        return new{audit=run,findings,versions=await u.Store<IAuditCapaLookup>().AuditVersionsAsync(run.Id,ct)};
    }
    public async Task<object> ListAsync(ForgePrincipal p,string type,int take,string? after,CancellationToken ct)
    {
        Require(take is >0 and <=100,"audit_page","Choose 1–100 records.");Configured();
        await using var u=await factory.BeginSnapshotAsync(ct);var items=new List<object>();string? cursor=after,last=null;
        while(true)
        {
            if(type=="Program")
            {
                var page=await u.Store<IAuditCapaLookup>().ProgramsAsync(100,cursor,ct);
                foreach(var r in page.Items)try{await Authority(u,p,Resource(r),r.OrganizationCode,r.Revisions.SelectMany(x=>x.Sources),false,ct);if(items.Count==take)return new{items,nextCursor=last};items.Add(r);last=r.Code;}catch(ForgeAuthorizationException){}catch(CommandRejectedException){}
                if(page.NextCursor is null)return new{items,nextCursor=(string?)null};cursor=page.NextCursor;
            }
            else
            {
                var page=await u.Store<IAuditCapaLookup>().AuditsAsync(100,cursor,ct);
                foreach(var r in page.Items)try{await RunAuthority(u,p,r,false,ct);if(items.Count==take)return new{items,nextCursor=last};items.Add(r);last=r.Id.ToString();}catch(ForgeAuthorizationException){}catch(CommandRejectedException){}
                if(page.NextCursor is null)return new{items,nextCursor=(string?)null};cursor=page.NextCursor;
            }
        }
    }

    /// <summary>Returns only current-generation findings from audits the caller may currently read.</summary>
    public async Task<IReadOnlyList<QmsAuditAnalystFinding>> OpenFindingsForAnalystAsync(ForgePrincipal p,int take,CancellationToken ct)
    {
        Require(take is >0 and <=100,"audit_page","Choose 1–100 records.");Configured();
        await using var u=await factory.BeginSnapshotAsync(ct);var items=new List<QmsAuditAnalystFinding>();string? cursor=null;
        while(true)
        {
            var page=await u.Store<IAuditCapaLookup>().AuditsAsync(100,cursor,ct);
            foreach(var audit in page.Items)
            {
                try
                {
                    await RunAuthority(u,p,audit,false,ct);
                    foreach(var finding in await u.Store<IAuditCapaLookup>().FindingsAsync(audit.Id,ct))
                    {
                        if(finding.AuditGeneration!=audit.Generation||finding.State==QmsAuditFindingState.Closed)continue;
                        items.Add(new(audit.Id,audit.Generation,audit.State.ToString(),finding.Id,finding.CriterionCode,
                            finding.Kind,finding.State,finding.Statement,finding.FollowUpRequired,finding.RecordedAt));
                        if(items.Count==take)return items;
                    }
                }
                catch(ForgeAuthorizationException){}
                catch(CommandRejectedException){}
            }
            if(page.NextCursor is null)return items;cursor=page.NextCursor;
        }
    }

    public Task<CommandResult> DefineAsync(DefineAuditProgram c,CommandContext context,CancellationToken ct)
    {
        var code=BusinessCode.Require(c.Code,"Audit program");var facility=BusinessCode.Require(c.FacilityCode,"Facility");var resource=Scope("QmsAuditProgram",code,facility,c.ProgramId,c.Markings);
        return pipeline.ExecuteAsync(new("Quality",nameof(DefineAuditProgram),AuditPermissions.Manage,resource,true),c,context,async(u,now)=>
        {
            await u.Store<IAuditCapaStore>().LockAsync("Program",code,ct);Require(await u.Store<IAuditCapaLookup>().FindProgramAsync(code,ct)is null,"audit_exists","This audit program exists.");
            await planning.HeaderAsync(u,facility,c.ProgramId,c.Markings,ct);var sources=await ProgramSources(u,context.Principal,code,c.Content,false,now,ct);
            var revision=DraftProgram(code,1,c.Content,sources,context,now);var program=new QmsAuditProgram(code,options.OrganizationCode!,facility,c.ProgramId,c.Markings,context.Actor,now,[revision]);
            await u.Store<IAuditCapaStore>().SaveProgramAsync(program,ct);await u.Store<IAuditCapaStore>().SaveProgramRevisionAsync(revision,null,ct);
            return Outcome(resource,"QmsAuditProgramRevision",code+"/1",1,"Create","Draft",null,revision,context,sources);
        },ct,async(u,now)=>
        {
            Configured();await u.Store<IAuditCapaStore>().LockAsync("Program",code,ct);
            if(await u.Store<IAuditCapaLookup>().FindProgramAsync(code,ct)is{} existing)return await Authority(u,context.Principal,Resource(existing),existing.OrganizationCode,existing.Revisions.SelectMany(x=>x.Sources),true,ct);
            await planning.HeaderAsync(u,facility,c.ProgramId,c.Markings,ct);var sources=await ProgramSources(u,context.Principal,code,c.Content,false,now,ct);
            return new(resource with{Markings=Marks(c.Markings,sources)},options.OrganizationCode+":"+code);
        });
    }
    public Task<CommandResult> ReviseAsync(ReviseAuditProgram c,CommandContext context,CancellationToken ct)=>ProgramCommand(nameof(ReviseAuditProgram),c,c.Code,AuditPermissions.Manage,context,async(u,p,now)=>
    {
        var old=c.Revision is{} n?Revision(p,n):p.Revisions[^1];Version(old.Version,c.ExpectedVersion);var edit=c.Revision is not null;
        Require(edit?old.State==QmsState.Draft:!p.Revisions.Any(x=>x.State is QmsState.Draft or QmsState.Submitted or QmsState.Reviewed or QmsState.Approved),"audit_open_revision","Complete or withdraw the open draft before another revision.");
        var sources=await ProgramSources(u,context.Principal,p.Code,c.Content,false,now,ct);
        var next=edit?old with{Version=old.Version+1,Content=c.Content,LastEditedBy=context.Actor,Sources=sources,RequiresLicensedVerification=sources.Any(x=>x.RequiresLicensedVerification),History=Append(old.History,"Edit",old.Version+1,"Draft","Draft","Human content revision",context,now)}:DraftProgram(p.Code,old.Revision+1,c.Content,sources,context,now);
        await u.Store<IAuditCapaStore>().SaveProgramRevisionAsync(next,edit?old.Version:null,ct);
        return Outcome(Resource(p),"QmsAuditProgramRevision",p.Code+"/"+next.Revision,next.Version,edit?"Edit":"NewRevision","Draft",edit?old:null,next,context,sources);
    },ct,async(u,p,now)=>{await ProgramSources(u,context.Principal,p.Code,c.Content,false,now,ct);});
    public Task<CommandResult> DecideProgramAsync(AuditProgramDecision c,CommandContext context,CancellationToken ct)=>ProgramCommand("AuditProgram"+c.Action,c,c.Code,c.Action=="Submit"?AuditPermissions.Manage:AuditPermissions.Approve,context,async(u,p,now)=>
    {
        var old=Revision(p,c.Revision);Version(old.Version,c.ExpectedVersion);Text(c.Note,"Decision");var state=Transition(old.State,c.Action);
        var sources=await ProgramSources(u,context.Principal,p.Code,old.Content,c.Action!="Withdraw",now,ct);
        if(c.Action is "Review" or "Approve")Independent(context.Actor,Authors(old),sources);
        var next=old with{Version=old.Version+1,State=state,Sources=sources,ReviewedBy=c.Action=="Review"?context.Actor:old.ReviewedBy,ApprovedBy=c.Action=="Approve"?context.Actor:old.ApprovedBy,EffectiveFrom=c.Action=="Effective"?now:old.EffectiveFrom,EffectiveTo=c.Action=="Withdraw"&&old.EffectiveFrom is not null?now:old.EffectiveTo,RequiresLicensedVerification=sources.Any(x=>x.RequiresLicensedVerification),History=Append(old.History,c.Action,old.Version+1,old.State.ToString(),state.ToString(),c.Note,context,now)};
        var superseded=new List<QmsAuditProgramRevision>();
        if(c.Action=="Effective")foreach(var r in p.Revisions.Where(x=>x.State==QmsState.Effective))
        {
            var closed=r with{Version=r.Version+1,State=QmsState.Superseded,EffectiveTo=now,History=Append(r.History,"Supersede",r.Version+1,"Effective","Superseded","Successor revision "+next.Revision,context,now)};
            await u.Store<IAuditCapaStore>().SaveProgramRevisionAsync(closed,r.Version,ct);superseded.Add(closed);
            await u.Outbox.EnqueueAsync(pipeline.Event(new AuditCapaChanged("QmsAuditProgramRevision",p.Code+"/"+r.Revision,closed.Version,"Supersede","Superseded"),context,"Quality","QmsAuditProgramRevision",p.Code+"/"+r.Revision,Program(p.ProgramId),Marks(p.Markings,r.Sources)),ct);
        }
        await u.Store<IAuditCapaStore>().SaveProgramRevisionAsync(next,old.Version,ct);
        return Outcome(Resource(p),"QmsAuditProgramRevision",p.Code+"/"+next.Revision,next.Version,c.Action,state.ToString(),old,new{revision=next,superseded},context,sources);
    },ct,async(u,p,now)=>{var r=Revision(p,c.Revision);await ProgramSources(u,context.Principal,p.Code,r.Content,c.Action!="Withdraw",now,ct);if(c.Action is "Review" or "Approve")Independent(context.Actor,Authors(r),r.Sources);});
    public Task<CommandResult> AssessAsync(AssessAuditor c,CommandContext context,CancellationToken ct)=>ProgramCommand(nameof(AssessAuditor),c,c.AuditProgram.Code,AuditPermissions.Conduct,context,async(u,p,now)=>
    {
        var r=Revision(p,c.AuditProgram.Revision);RequireEffective(r,now);await Active(u,c.PersonId,ct);Range(c.ValidFrom,c.ValidTo);
        Require(Enum.IsDefined(c.Decision),"audit_competence","Record the human competence determination.");foreach(var s in new[]{c.Criteria,c.ActualAssessment,c.Limitations})Text(s,"Competence assessment");Require(c.Criteria==r.Content.Competence.CompetenceCriteria,"audit_competence","Use the exact approved competence criteria.");
        var sources=await Resolve(u,context.Principal,c.Evidence,ct);Require(sources.Count>0,"audit_competence","A competence assessment needs actual evidence.");
        var id=ForgeIds.New(pipeline.Time);var record=new QmsAuditorCompetenceAssessment(id,1,c.PersonId,c.AuditProgram,c.Decision,c.ValidFrom,c.ValidTo,c.Criteria,c.ActualAssessment,c.Limitations,context.Actor,null,null,sources,Append([],"Assess",1,"","PendingApproval",c.ActualAssessment,context,now));
        await u.Store<IAuditCapaStore>().SaveCompetenceAsync(record,null,ct);return Outcome(Resource(p),"QmsAuditorCompetence",id.ToString(),1,"Assess","PendingApproval",null,record,context,sources);
    },ct,async(u,p,now)=>{RequireEffective(Revision(p,c.AuditProgram.Revision),now);await Resolve(u,context.Principal,c.Evidence,ct);});
    public async Task<CommandResult> ApproveCompetenceAsync(ApproveAuditorAssessment c,CommandContext context,CancellationToken ct)
    {
        await using var read=await factory.BeginSnapshotAsync(ct);var initial=await read.Store<IAuditCapaLookup>().FindCompetenceAsync(c.Id,ct)??throw Missing();
        return await ProgramCommand(nameof(ApproveAuditorAssessment),c,initial.AuditProgram.Code,AuditPermissions.Approve,context,async(u,p,now)=>
        {
            await u.Store<IAuditCapaStore>().LockAsync("Competence",c.Id.ToString(),ct);var old=await u.Store<IAuditCapaLookup>().FindCompetenceAsync(c.Id,ct)??throw Missing();Version(old.Version,c.ExpectedVersion);Require(old.ApprovedBy is null,"audit_competence","The competence decision is already approved.");Text(c.Note,"Approval basis");
            await Current(u,context.Principal,old.Evidence,true,true,ct);Independent(context.Actor,[old.AssessedBy,"person:"+old.PersonId],old.Evidence);
            var next=old with{Version=old.Version+1,ApprovedBy=context.Actor,ApprovedAt=now,History=Append(old.History,"Approve",old.Version+1,"PendingApproval","Approved",c.Note,context,now)};
            await u.Store<IAuditCapaStore>().SaveCompetenceAsync(next,old.Version,ct);return Outcome(Resource(p),"QmsAuditorCompetence",c.Id.ToString(),next.Version,"Approve","Approved",old,next,context,next.Evidence);
        },ct,async(u,p,now)=>{var a=await u.Store<IAuditCapaLookup>().FindCompetenceAsync(c.Id,ct)??throw Missing();RequireEffective(Revision(p,a.AuditProgram.Revision),now);await Current(u,context.Principal,a.Evidence,true,true,ct);Independent(context.Actor,[a.AssessedBy,"person:"+a.PersonId],a.Evidence);});
    }
    public async Task<QmsAuditorValidationResult> ValidateAsync(IKernelUnitOfWork uow,ForgePrincipal principal,QmsAuditorValidation query,bool lockSources,CancellationToken ct)
    {
        Require(query.AsOf<=pipeline.Time.GetUtcNow(),"audit_as_of","A future time is not actual competence evidence.");Instant(query.AsOf);Text(query.ImpartialityBasis,"Human impartiality basis");
        var p=await ProgramAsync(uow,query.AuditProgram.Code,ct);var r=Revision(p,query.AuditProgram.Revision);RequireEffective(r,pipeline.Time.GetUtcNow());await Authority(uow,principal,Resource(p),p.OrganizationCode,r.Sources,lockSources,ct);
        if(lockSources)await ProgramSources(uow,principal,p.Code,r.Content,true,pipeline.Time.GetUtcNow(),ct);
        else
        {
            foreach(var reference in r.Content.Processes.Concat(r.Content.Criteria.SelectMany(x=>x.Requirements)).Distinct())await ValidateControlledAsync(uow,principal,reference,null,pipeline.Time.GetUtcNow(),false,ct);
            foreach(var document in r.Content.Documents.Concat(r.Content.Criteria.SelectMany(x=>x.Documents)).Distinct())await ValidateDocument(uow,principal,document,pipeline.Time.GetUtcNow(),false,ct);
        }
        var criteria=r.Content.Criteria.SelectMany(x=>x.Requirements).ToArray();var docs=r.Content.Criteria.SelectMany(x=>x.Documents).Concat(r.Content.Documents).ToArray();
        foreach(var required in query.RequiredCriteria??[])Require(criteria.Contains(required),"audit_competence_scope","The approved program does not cover an exact required criterion revision.");
        foreach(var required in query.RequiredStandardReferences??[])Require(docs.Contains(required),"audit_competence_scope","The approved program does not cover an exact required standard reference.");
        await Active(uow,query.PersonId,ct);var sources=new List<QmsPlanningSourceSnapshot>(r.Sources);var facts=await qualifications.QualificationsAsync(uow,principal,query.PersonId,r.Content.Competence.QualificationCodes,query.AsOf,lockSources,ct);
        if(r.Content.Competence.RequireFormalQualifications==true)Require(r.Content.Competence.QualificationCodes.All(code=>facts.Any(x=>x.Code==code&&x.QualifiedAsOf)),"audit_not_competent","Every required qualification must be actual and valid as of the assessment.");
        sources.AddRange(facts.Select(x=>x.Source));
        if(r.Content.Competence.RequireIndependentCompetenceAssessment==true)
        {
            Require(query.CompetenceAssessmentId is not null,"audit_not_competent","An independently approved competence assessment is required.");
            var a=await uow.Store<IAuditCapaLookup>().FindCompetenceAsync(query.CompetenceAssessmentId!.Value,ct)??throw Missing();
            Require(a.PersonId==query.PersonId&&a.AuditProgram==query.AuditProgram&&a.Criteria==r.Content.Competence.CompetenceCriteria&&a.Decision==QmsAuditCompetenceDecision.Competent&&a.ApprovedBy is not null&&a.ApprovedAt<=query.AsOf&&a.ValidFrom<=query.AsOf&&query.AsOf<a.ValidTo,"audit_not_competent","The approved actual competence assessment is outside its exact scope or period.");
            Independent(a.ApprovedBy!,[a.AssessedBy,"person:"+a.PersonId],a.Evidence);await Current(uow,principal,a.Evidence,true,lockSources,ct);sources.AddRange(a.Evidence);
        }
        var excluded=new List<string>();foreach(var process in r.Content.Processes)
        {
            var owner=await uow.Store<IQmsLookup>().FindAsync(process.Code,ct)??throw Missing();var rev=owner.Revisions.FirstOrDefault(x=>x.Revision==process.Revision)??throw Missing();
            if(r.Content.Impartiality.ProhibitAuditingOwnedProcesses==true&&rev.Content.Process is{} content)excluded.Add("person:"+content.OwnerPersonId);
        }
        if(r.Content.Impartiality.ProhibitAuditingContributedEvidence==true)excluded.AddRange(r.Sources.SelectMany(x=>x.Contributors));
        Require(!excluded.Contains("person:"+query.PersonId,StringComparer.Ordinal),"audit_impartiality","This auditor owns a prohibited process or contributed prohibited evidence.");
        var snapshot=new QmsAuditCompetenceSnapshot(query.PersonId,query.AsOf,facts,query.CompetenceAssessmentId,query.ImpartialityBasis,excluded.Distinct().ToArray(),CanonicalJson.Fingerprint(new{query.PersonId,query.AsOf,query.AuditProgram,facts,assessment=query.CompetenceAssessmentId,excluded}));
        return new(query.AuditProgram,r.Content.Scope,snapshot,sources.DistinctBy(x=>x.Reference).ToArray());
    }
    public Task<CommandResult> ScheduleAsync(ScheduleAudit c,CommandContext context,CancellationToken ct)=>ProgramCommand(nameof(ScheduleAudit),c,c.AuditProgram.Code,AuditPermissions.Manage,context,async(u,p,now)=>
    {
        var r=Revision(p,c.AuditProgram.Revision);RequireEffective(r,now);Text(c.Scope,"Actual audit scope");Range(c.From,c.To);List(c.Auditors,"Auditor assignments",100);List(c.Competence,"Competence declarations",100);
        Require(c.Auditors.Count>0&&c.Auditors.Select(x=>x.PersonId).Distinct().Count()==c.Auditors.Count&&c.Auditors.Count==c.Competence.Count,"audit_assignments","Each distinct auditor needs one exact competence declaration.");
        var snapshots=new List<QmsAuditCompetenceSnapshot>();var sources=new List<QmsPlanningSourceSnapshot>(r.Sources);
        foreach(var a in c.Auditors){Text(a.Responsibility,"Audit responsibility");var q=c.Competence.SingleOrDefault(x=>x.PersonId==a.PersonId)??throw Missing();Require(q.AuditProgram==c.AuditProgram,"audit_competence_scope","Auditor competence must cite this exact program.");var verified=await ValidateAsync(u,context.Principal,q with{AsOf=now},true,ct);snapshots.Add(verified.Competence);sources.AddRange(verified.Sources);}
        var id=ForgeIds.New(pipeline.Time);var record=new QmsAuditRun(id,1,1,QmsAuditState.Scheduled,p.OrganizationCode,p.FacilityCode,p.ProgramId,p.Markings,c.AuditProgram,r.Version,c.Scope,c.From,c.To,c.Auditors,snapshots,context.Actor,now,null,null,null,sources.DistinctBy(x=>x.Reference).ToArray(),Append([],"Schedule",1,"","Scheduled",c.Scope,context,now));
        await u.Store<IAuditCapaStore>().SaveAuditAsync(record,null,ct);return Outcome(Resource(record),"QmsAudit",id.ToString(),1,"Schedule","Scheduled",null,record,context,record.Sources);
    },ct,async(u,p,now)=>{RequireEffective(Revision(p,c.AuditProgram.Revision),now);foreach(var q in c.Competence)await ValidateAsync(u,context.Principal,q with{AsOf=now},true,ct);});
    public Task<CommandResult> DecideAsync(DecideAudit c,CommandContext context,CancellationToken ct)=>RunCommand("Audit"+c.Action,c,c.Id,c.Action is "ApproveReport" or "Close"?AuditPermissions.Approve:AuditPermissions.Conduct,context,async(u,old,p,r,now)=>
    {
        Version(old.Version,c.ExpectedVersion);AuditCapaRules.Generation(old.Generation,c.Generation);Text(c.Note,"Actual decision");
        var state=c.Action switch
        {
            "Start" when old.State is QmsAuditState.Scheduled or QmsAuditState.Reopened=>QmsAuditState.InProgress,
            "SubmitReport" when old.State==QmsAuditState.ReportDraft=>QmsAuditState.ReportSubmitted,
            "ApproveReport" when old.State==QmsAuditState.ReportSubmitted=>QmsAuditState.ReportApproved,
            "FollowUp" when old.State==QmsAuditState.ReportApproved=>QmsAuditState.FollowUp,
            "Close" when old.State is QmsAuditState.ReportApproved or QmsAuditState.FollowUp=>QmsAuditState.Closed,
            "Reopen" when old.State==QmsAuditState.Closed=>QmsAuditState.Reopened,
            "Cancel" when old.State is QmsAuditState.Scheduled or QmsAuditState.Reopened=>QmsAuditState.Cancelled,
            _=>throw new CommandRejectedException("audit_state","The decision is unavailable in this audit state.")
        };
        if(c.Action is "Start" or "SubmitReport")Assigned(context,old);
        if(c.Action=="ApproveReport")
        {
            Require(old.Report is not null,"audit_report","Record an actual audit report.");
            if(r.Content.Impartiality.RequireIndependentReportApproval==true)Independent(context.Actor,old.Auditors.Select(x=>"person:"+x.PersonId).Append(old.Report!.AuthoredBy),old.Report.Evidence.Concat((await u.Store<IAuditCapaLookup>().FindingsAsync(old.Id,ct)).Where(x=>x.AuditGeneration==old.Generation).SelectMany(x=>x.Evidence)));
        }
        if(c.Action=="Close")
        {
            Require(old.Report?.ApprovedBy is not null,"audit_report","The actual report must be approved.");
            foreach(var f in (await u.Store<IAuditCapaLookup>().FindingsAsync(old.Id,ct)).Where(x=>x.AuditGeneration==old.Generation))
            {
                Require(f.FollowUpRequired!=true||f.State==QmsAuditFindingState.FollowUpVerified,"audit_follow_up","Every required follow-up needs independent actual verification.");
                await Current(u,context.Principal,f.Evidence,true,true,ct);
                if(f.FollowUpRequired==true)await FollowUpSources(u,context.Principal,f.CapaIds,f.PlanningTaskIds,true,ct);
            }
        }
        var competence=old.Competence;
        if(c.Action=="Start"){var list=new List<QmsAuditCompetenceSnapshot>();foreach(var saved in old.Competence)list.Add((await ValidateAsync(u,context.Principal,new(saved.PersonId,now,old.AuditProgram,saved.ApprovedAssessmentId,saved.ImpartialityBasis),true,ct)).Competence);competence=list;}
        var next=old with{Version=old.Version+1,State=state,Generation=c.Action=="Reopen"?old.Generation+1:old.Generation,Report=c.Action=="Reopen"?null:c.Action=="ApproveReport"?old.Report! with{ApprovedBy=context.Actor,ApprovedAt=now}:old.Report,Competence=competence,StartedAt=c.Action=="Start"?now:old.StartedAt,CompletedAt=c.Action=="Close"?now:c.Action=="Reopen"?null:old.CompletedAt,History=Append(old.History,c.Action,old.Version+1,old.State.ToString(),state.ToString(),c.Note,context,now)};
        await u.Store<IAuditCapaStore>().SaveAuditAsync(next,old.Version,ct);return Outcome(Resource(old),"QmsAudit",old.Id.ToString(),next.Version,c.Action,state.ToString(),old,next,context,next.Sources);
    },ct,async(u,a,p,r,now)=>{AuditCapaRules.Generation(a.Generation,c.Generation);if(c.Action is "Start" or "SubmitReport"){Assigned(context,a);await ValidateActingAuditor(u,context,a,now,ct);}if(c.Action is "ApproveReport" or "Close"){var report=a.Report??throw Missing();await Current(u,context.Principal,report.Evidence,true,true,ct);var findings=(await u.Store<IAuditCapaLookup>().FindingsAsync(a.Id,ct)).Where(f=>f.AuditGeneration==a.Generation).ToArray();foreach(var finding in findings){await Current(u,context.Principal,finding.Evidence,true,true,ct);if(c.Action=="Close"&&finding.FollowUpRequired==true)await FollowUpSources(u,context.Principal,finding.CapaIds,finding.PlanningTaskIds,true,ct);}if(c.Action=="ApproveReport"&&r.Content.Impartiality.RequireIndependentReportApproval==true)Independent(context.Actor,a.Auditors.Select(x=>"person:"+x.PersonId).Append(report.AuthoredBy).Concat(findings.Select(f=>f.RecordedBy)),report.Evidence.Concat(findings.SelectMany(f=>f.Evidence)));}});
    public Task<CommandResult> FindingAsync(RecordAuditFinding c,CommandContext context,CancellationToken ct)=>RunCommand(nameof(RecordAuditFinding),c,c.AuditId,AuditPermissions.Conduct,context,async(u,a,p,r,now)=>
    {
        Version(a.Version,c.ExpectedAuditVersion);AuditCapaRules.Generation(a.Generation,c.Generation);Require(a.State==QmsAuditState.InProgress,"audit_state","Record findings during actual audit work.");Assigned(context,a);
        AuditCapaRules.Finding(r.Content,c.CriterionCode,c.Kind,c.Adequacy,c.Statement,c.ActualObservation,c.AdequacyBasis,c.Limitations,c.FollowUpRequired);
        var sources=await Resolve(u,context.Principal,c.Evidence,ct);AuditCapaRules.AdequacyEvidence(r.Content.Criteria.Single(x=>x.Code==c.CriterionCode),c.Adequacy,sources);await FollowUpSources(u,context.Principal,c.CapaIds,c.PlanningTaskIds,false,ct);
        if(r.Content.Impartiality.ProhibitAuditingContributedEvidence==true)Independent(context.Actor,[],sources);
        var id=ForgeIds.New(pipeline.Time);var f=new QmsAuditFinding(id,a.Id,a.Generation,1,c.FollowUpRequired==true?QmsAuditFindingState.FollowUpRequired:QmsAuditFindingState.Recorded,c.CriterionCode,c.Kind,c.Statement,c.ActualObservation,c.Adequacy,c.AdequacyBasis,c.Limitations,c.FollowUpRequired,sources,c.CapaIds,c.PlanningTaskIds,context.Actor,now,null,null,"",Append([],"Record",1,"","Recorded",c.Statement,context,now));
        await u.Store<IAuditCapaStore>().SaveFindingAsync(f,null,ct);var next=a with{Version=a.Version+1,History=Append(a.History,"Finding",a.Version+1,a.State.ToString(),a.State.ToString(),"Finding "+id,context,now)};await u.Store<IAuditCapaStore>().SaveAuditAsync(next,a.Version,ct);
        var outcome=Outcome(Resource(a),"QmsAuditFinding",id.ToString(),1,"Record",f.State.ToString(),null,new{finding=f,auditVersion=next.Version},context,sources);
        return outcome with{Events=outcome.Events.Append(pipeline.Event(new AuditCapaChanged("QmsAuditFindingOrigin",id.ToString(),1,"Record","Recorded"),context,"Quality","QmsAuditFindingOrigin",id.ToString(),Program(a.ProgramId),Marks(a.Markings,sources))).ToArray()};
    },ct,async(u,a,p,r,now)=>{AuditCapaRules.Generation(a.Generation,c.Generation);await ValidateActingAuditor(u,context,a,now,ct);await Resolve(u,context.Principal,c.Evidence,ct);await FollowUpSources(u,context.Principal,c.CapaIds,c.PlanningTaskIds,false,ct);});
    public async Task<CommandResult> FollowUpAsync(FollowUpAuditFinding c,CommandContext context,CancellationToken ct)
    {
        await using var snapshot=await factory.BeginSnapshotAsync(ct);var initial=await snapshot.Store<IAuditCapaLookup>().FindFindingAsync(c.Id,ct)??throw Missing();
        return await RunCommand("Finding"+c.Action,c,initial.AuditId,c.Action=="Verify"?AuditPermissions.Approve:AuditPermissions.Conduct,context,async(u,a,p,r,now)=>
        {
            var old=await u.Store<IAuditCapaLookup>().FindFindingAsync(c.Id,ct)??throw Missing();Version(old.Version,c.ExpectedVersion);AuditCapaRules.Generation(a.Generation,c.Generation);AuditCapaRules.Generation(a.Generation,old.AuditGeneration);Text(c.Result,"Actual follow-up result");
            Require(a.State is QmsAuditState.InProgress or QmsAuditState.ReportDraft or QmsAuditState.ReportApproved or QmsAuditState.FollowUp,"audit_state","Follow-up is unavailable in this audit state.");
            var humanSources=await Resolve(u,context.Principal,c.Evidence,ct);Require(humanSources.Count>0,"audit_follow_up","Follow-up requires actual implementation evidence.");
            var sources=new List<QmsPlanningSourceSnapshot>(humanSources);
            foreach(var capa in c.CapaIds)sources.Add(await evidence.ResolveAsync(u,context.Principal,new("Quality","Capa",capa.ToString()),true,ct));
            foreach(var task in c.PlanningTaskIds)sources.Add(await evidence.ResolveAsync(u,context.Principal,new("Quality","QmsPlanningTask",task.ToString()),true,ct));
            await FollowUpSources(u,context.Principal,c.CapaIds,c.PlanningTaskIds,c.Action=="Verify",ct);
            Require(old.Kind!=QmsAuditFindingKind.Nonconformity||c.CapaIds.Count>0,"audit_follow_up","A nonconformity requires an actual existing CAPA.");
            Require(c.Action is "Record" or "Verify","audit_action","Record or independently verify follow-up.");
            if(c.Action=="Verify"){Independent(context.Actor,old.History.Where(x=>x.Action is "Record" or "FollowUp").Select(x=>x.Actor),old.Evidence.Concat(sources));Require(old.FollowUpResult.Length>0,"audit_follow_up","Record the implementation result before independent verification.");}
            var next=old with{Version=old.Version+1,State=c.Action=="Verify"?QmsAuditFindingState.FollowUpVerified:QmsAuditFindingState.FollowUpRequired,FollowUpResult=c.Result,Evidence=old.Evidence.Concat(sources).GroupBy(x=>x.Reference).Select(g=>g.Last()).ToArray(),CapaIds=c.CapaIds,PlanningTaskIds=c.PlanningTaskIds,VerifiedBy=c.Action=="Verify"?context.Actor:null,VerifiedAt=c.Action=="Verify"?now:null,History=Append(old.History,c.Action=="Verify"?"Verify":"FollowUp",old.Version+1,old.State.ToString(),c.Action=="Verify"?"FollowUpVerified":"FollowUpRequired",c.Result,context,now)};
            await u.Store<IAuditCapaStore>().SaveFindingAsync(next,old.Version,ct);return Outcome(Resource(a),"QmsAuditFinding",c.Id.ToString(),next.Version,c.Action,next.State.ToString(),old,next,context,next.Evidence);
        },ct,async(u,a,p,r,now)=>{AuditCapaRules.Generation(a.Generation,c.Generation);var f=await u.Store<IAuditCapaLookup>().FindFindingAsync(c.Id,ct)??throw Missing();var saved=f.Evidence.Where(x=>f.State==QmsAuditFindingState.FollowUpVerified||x.Reference.Context!="Quality"||x.Reference.Type is not("Capa" or "QmsPlanningTask"));await Current(u,context.Principal,saved,c.Action=="Verify",true,ct);await Resolve(u,context.Principal,c.Evidence,ct);await FollowUpSources(u,context.Principal,c.CapaIds,c.PlanningTaskIds,c.Action=="Verify",ct);});
    }
    public Task<CommandResult> ReportAsync(WriteAuditReport c,CommandContext context,CancellationToken ct)=>RunCommand(nameof(WriteAuditReport),c,c.Id,AuditPermissions.Conduct,context,async(u,a,p,r,now)=>
    {
        Version(a.Version,c.ExpectedVersion);AuditCapaRules.Generation(a.Generation,c.Generation);Require(a.State is QmsAuditState.InProgress or QmsAuditState.ReportDraft,"audit_state","A report may be drafted during current audit work.");Assigned(context,a);
        foreach(var text in new[]{c.Summary,c.ScopePerformed,c.EvidenceLimitations,c.HumanAssessment})Text(text,"Human report assessment");List(c.FindingIds,"Exact finding manifest",1000);
        var findings=(await u.Store<IAuditCapaLookup>().FindingsAsync(a.Id,ct)).Where(x=>x.AuditGeneration==a.Generation).ToArray();Require(c.FindingIds.Distinct().Count()==c.FindingIds.Count&&findings.Select(x=>x.Id).Order().SequenceEqual(c.FindingIds.Order()),"audit_report_manifest","The report must include every actual finding in this generation.");
        var answers=c.ChecklistAnswers??[];List(answers,"Actual checklist answers",500);Require(answers.Select(x=>x.ChecklistCode).Distinct().Count()==answers.Count&&r.Content.Checklist.Select(x=>x.Code).Order().SequenceEqual(answers.Select(x=>x.ChecklistCode).Order()),"audit_checklist_manifest","Record a qualified human answer for every actual approved checklist question, including explicit gaps.");
        var sources=new List<QmsPlanningSourceSnapshot>(await Resolve(u,context.Principal,c.Evidence,ct));var capturedAnswers=new List<QmsAuditChecklistAnswerView>();
        foreach(var answer in answers){foreach(var value in new[]{answer.ActualObservation,answer.AdequacyBasis,answer.Limitations})Text(value,"Actual checklist answer");var checklist=r.Content.Checklist.Single(x=>x.Code==answer.ChecklistCode);var actual=await Resolve(u,context.Principal,answer.Evidence,ct);AuditCapaRules.AdequacyEvidence(r.Content.Criteria.Single(x=>x.Code==checklist.CriterionCode),answer.Adequacy,actual);capturedAnswers.Add(new(answer.ChecklistCode,answer.ActualObservation,answer.Adequacy,answer.AdequacyBasis,answer.Limitations,actual));sources.AddRange(actual);}
        foreach(var f in findings)await Current(u,context.Principal,f.Evidence,true,true,ct);
        var report=new QmsAuditReport(c.Summary,c.ScopePerformed,c.EvidenceLimitations,c.HumanAssessment,c.FindingIds,sources.DistinctBy(x=>x.Reference).ToArray(),context.Actor,now,null,null,capturedAnswers);
        var next=a with{Version=a.Version+1,State=QmsAuditState.ReportDraft,Report=report,History=Append(a.History,"Report",a.Version+1,a.State.ToString(),"ReportDraft",c.HumanAssessment,context,now)};
        await u.Store<IAuditCapaStore>().SaveAuditAsync(next,a.Version,ct);return Outcome(Resource(a),"QmsAudit",a.Id.ToString(),next.Version,"Report","ReportDraft",a,next,context,sources.Concat(findings.SelectMany(x=>x.Evidence)));
    },ct,async(u,a,p,r,now)=>{AuditCapaRules.Generation(a.Generation,c.Generation);await ValidateActingAuditor(u,context,a,now,ct);await Resolve(u,context.Principal,c.Evidence.Concat((c.ChecklistAnswers??[]).SelectMany(x=>x.Evidence)).ToArray(),ct);});

    internal async Task<QmsAuditProgram> ProgramAsync(IKernelUnitOfWork u,string code,CancellationToken ct)
    {Configured();var p=await u.Store<IAuditCapaLookup>().FindProgramAsync(BusinessCode.Require(code,"Audit program"),ct)??throw Missing();Require(p.OrganizationCode==options.OrganizationCode,"audit_not_found","The audit program is unavailable.");return p;}
    internal static QmsAuditProgramRevision Revision(QmsAuditProgram p,int revision)=>p.Revisions.FirstOrDefault(x=>x.Revision==revision)??throw Missing();
    internal static void RequireEffective(QmsAuditProgramRevision r,DateTimeOffset now)=>Require(r.State==QmsState.Effective&&r.EffectiveFrom<=now&&(r.EffectiveTo is null||now<r.EffectiveTo),"audit_program_not_effective","The exact audit program revision must currently be effective.");
    internal async Task<IReadOnlyList<QmsPlanningSourceSnapshot>> Resolve(IKernelUnitOfWork u,ForgePrincipal p,IReadOnlyList<QmsPlanningEvidenceReference> refs,CancellationToken ct)
    {List(refs,"Actual evidence references",1000);var result=new List<QmsPlanningSourceSnapshot>();foreach(var r in refs.Distinct()){EvidenceReference(r);result.Add(await evidence.ResolveAsync(u,p,r,true,ct));}return result;}
    internal Task<IReadOnlyList<QmsPlanningSourceSnapshot>> Current(IKernelUnitOfWork u,ForgePrincipal p,IEnumerable<QmsPlanningSourceSnapshot> refs,bool same,bool locks,CancellationToken ct)=>planning.CurrentEvidenceAsync(u,p,refs,same,ct,locks);
    internal async Task<CommandAuthority> Authority(IKernelUnitOfWork u,ForgePrincipal p,ResourceRef scope,string org,IEnumerable<QmsPlanningSourceSnapshot> sources,bool locks,CancellationToken ct)
    {Configured();Require(org==options.OrganizationCode,"audit_not_found","This owner record is unavailable.");planning.Permit(p,Permissions.QualityRead,scope);var recorded=sources.ToArray();var current=await Current(u,p,recorded,false,locks,ct);return new(scope with{Markings=Marks(scope.Markings,recorded.Concat(current))},org+":"+scope.Type+":"+scope.Id);}
    internal static async Task Active(IKernelUnitOfWork u,Guid id,CancellationToken ct)
    {Person(id);Require(await u.People.LoadPrincipalAsync(new PersonId(id),ct)is{IsActive:true},"audit_person","Select an actual active person.");}
    private async Task<IReadOnlyList<QmsPlanningSourceSnapshot>> ProgramSources(IKernelUnitOfWork u,ForgePrincipal p,string code,QmsAuditProgramContent content,bool effective,DateTimeOffset now,CancellationToken ct)
    {
        AuditCapaRules.Program(content);await Active(u,content.OwnerPersonId,ct);await qualifications.ValidateCodesAsync(u,content.Competence.QualificationCodes,true,ct);
        // Reuse exact QMS/source rules; a process and a requirement never substitute for each other.
        var fake=new QmsPlanningContent(null,null,null,null,new QmsChangeContent("Audit scope","Audit program","Evaluate evidence",content.Processes,[],"Audit resources","Audit ownership","Independent approvals","Audit plan","Audit verification","Human adequacy",content.OwnerPersonId,now,[]),null,null,
            content.Documents.Concat(content.Criteria.SelectMany(x=>x.Documents)).Distinct().ToArray(),content.Criteria.SelectMany(x=>x.Requirements).Distinct().ToArray(),content.Evidence);
        var sources=await planning.SourcesAsync(u,p,code,QmsPlanningKind.Change,fake,effective,now,ct);
        if(effective)foreach(var reference in content.Processes.Concat(content.Criteria.SelectMany(x=>x.Requirements)).Distinct())await ValidateControlledAsync(u,p,reference,null,now,true,ct);
        return sources;
    }
    public async Task ValidateControlledAsync(IKernelUnitOfWork u,ForgePrincipal p,QmsRevisionReference reference,QmsRecordKind? kind,DateTimeOffset now,bool locks,CancellationToken ct,HashSet<string>? path=null)
    {
        path??=new(StringComparer.Ordinal);var id=reference.Code+"/"+reference.Revision;Require(path.Add(id),"audit_source_cycle","Controlled source dependencies contain a cycle.");
        try
        {
            await evidence.ResolveAsync(u,p,new("Quality","QmsRevision",reference.Code,reference.Revision.ToString()),locks,ct);
            var owner=await u.Store<IQmsLookup>().FindAsync(reference.Code,ct)??throw Missing();var r=owner.Revisions.FirstOrDefault(x=>x.Revision==reference.Revision)??throw Missing();
            Require(kind is null||owner.Kind==kind,"audit_source_kind","Use the exact declared controlled owner kind.");Require(r.State==QmsState.Effective&&r.EffectiveFrom<=now&&(r.EffectiveTo is null||now<r.EffectiveTo),"audit_source_not_effective","Every exact governing controlled source must currently be effective.");
            foreach(var source in r.Sources)
            {
                if(source.Kind=="QmsRevision"){Require(int.TryParse(source.Revision,out var rev)&&rev>0,"audit_source_revision","Use an actual exact controlled source revision.");await ValidateControlledAsync(u,p,new(source.Key,rev),null,now,locks,ct,path);}
                else if(source.Kind=="DocumentRevision")
                {
                    await ValidateDocument(u,p,new(source.Key,source.Revision),now,locks,ct);
                }
            }
        }
        finally{path.Remove(id);}
    }
    private async Task ValidateDocument(IKernelUnitOfWork u,ForgePrincipal p,QmsDocumentReference source,DateTimeOffset now,bool locks,CancellationToken ct)
    {
        await evidence.ResolveAsync(u,p,new("Engineering","DocumentRevision",source.DocumentNumber,source.Revision),locks,ct);
        var document=await u.Store<IDocumentLookup>().FindDocumentAsync(source.DocumentNumber,ct)??throw Missing();var revision=document.Revisions.FirstOrDefault(x=>x.Revision==source.Revision)??throw Missing();var day=(await u.SiteDateAsync(now, ct));
        Require(document.Current?.Revision==source.Revision&&revision.Status is DocumentRevisionStatus.Released or DocumentRevisionStatus.Applicable&&(revision.EffectiveFrom is null||revision.EffectiveFrom<=day)&&(revision.EffectiveTo is null||day<=revision.EffectiveTo),"audit_document_not_current","Use exact currently applicable controlled governing documents.");
    }
    private static async Task<QmsAuditRun> RunAsync(IKernelUnitOfWork u,Guid id,CancellationToken ct)=>await u.Store<IAuditCapaLookup>().FindAuditAsync(id,ct)??throw Missing();
    private async Task<CommandAuthority> RunAuthority(IKernelUnitOfWork u,ForgePrincipal p,QmsAuditRun a,bool locks,CancellationToken ct)
    {
        var sources=(await u.Store<IAuditCapaLookup>().AuditVersionsAsync(a.Id,ct)).SelectMany(x=>x.Sources.Concat(x.Report?.Evidence??[])).ToList();
        foreach(var finding in await u.Store<IAuditCapaLookup>().FindingsAsync(a.Id,ct))sources.Add(await evidence.ResolveAsync(u,p,new("Quality","QmsAuditFinding",finding.Id.ToString()),locks,ct));
        return await Authority(u,p,Resource(a),a.OrganizationCode,sources,locks,ct);
    }
    private async Task ValidateActingAuditor(IKernelUnitOfWork u,CommandContext c,QmsAuditRun a,DateTimeOffset now,CancellationToken ct)
    {
        Assigned(c,a);var saved=a.Competence.FirstOrDefault(x=>x.PersonId==c.Principal.PersonId.Value)??throw Missing();
        await ValidateAsync(u,c.Principal,new(saved.PersonId,now,a.AuditProgram,saved.ApprovedAssessmentId,saved.ImpartialityBasis),true,ct);
    }
    private static void Assigned(CommandContext c,QmsAuditRun a)=>Require(a.Auditors.Any(x=>x.PersonId==c.Principal.PersonId.Value),"audit_assignment","Only an assigned actual auditor can conduct this audit.");
    private async Task FollowUpSources(IKernelUnitOfWork u,ForgePrincipal p,IReadOnlyList<long> capas,IReadOnlyList<Guid> tasks,bool completed,CancellationToken ct)
    {
        List(capas,"CAPA links",500);List(tasks,"Owned action links",500);Require(capas.Distinct().Count()==capas.Count&&tasks.Distinct().Count()==tasks.Count,"audit_follow_up","Use distinct exact owner links.");
        foreach(var id in capas)
        {
            var core=await u.Store<IQualityStore>().FindCapaAsync(id,ct)??throw Missing();var saved=await evidence.ResolveAsync(u,p,new("Quality","Capa",id.ToString()),true,ct);
            if(completed){Require(core.Status==CapaStatus.Verified,"audit_follow_up","The linked actual CAPA must be independently verified.");if(await u.Store<IAuditCapaLookup>().FindCapaAsync(id,ct)is{} governed)Require(governed.State==QmsCapaState.Closed,"audit_follow_up","The current governed CAPA must be closed.");}
        }
        foreach(var id in tasks){var t=await u.Store<IQmsPlanningLookup>().FindTaskAsync(id,ct)??throw Missing();await evidence.ResolveAsync(u,p,new("Quality","QmsPlanningTask",id.ToString()),true,ct);if(completed)Require(t.State==QmsTaskState.Verified,"audit_follow_up","Every linked owned task needs actual independent verification.");}
    }
    private Task<CommandResult> ProgramCommand<T>(string name,T c,string code,string permission,CommandContext context,Func<IKernelUnitOfWork,QmsAuditProgram,DateTimeOffset,Task<CommandOutcome>> body,CancellationToken ct,Func<IKernelUnitOfWork,QmsAuditProgram,DateTimeOffset,Task>? before=null)=>
        pipeline.ExecuteAsync(new("Quality",name,permission,ResourceRef.Of("QmsAuditProgram",code),true),c,context,async(u,now)=>{var p=await ProgramAsync(u,code,ct);return await body(u,p,now);},ct,async(u,now)=>{await u.Store<IAuditCapaStore>().LockAsync("Program",BusinessCode.Require(code,"Audit program"),ct);var p=await ProgramAsync(u,code,ct);var authority=await Authority(u,context.Principal,Resource(p),p.OrganizationCode,p.Revisions.SelectMany(x=>x.Sources),true,ct);if(before is not null)await before(u,p,now);return authority;});
    private Task<CommandResult> RunCommand<T>(string name,T c,Guid id,string permission,CommandContext context,Func<IKernelUnitOfWork,QmsAuditRun,QmsAuditProgram,QmsAuditProgramRevision,DateTimeOffset,Task<CommandOutcome>> body,CancellationToken ct,Func<IKernelUnitOfWork,QmsAuditRun,QmsAuditProgram,QmsAuditProgramRevision,DateTimeOffset,Task>? before=null)=>
        pipeline.ExecuteAsync(new("Quality",name,permission,ResourceRef.Of("QmsAudit",id.ToString()),true),c,context,async(u,now)=>{var a=await RunAsync(u,id,ct);var p=await ProgramAsync(u,a.AuditProgram.Code,ct);return await body(u,a,p,Revision(p,a.AuditProgram.Revision),now);},ct,async(u,now)=>
        {
            await u.Store<IAuditCapaStore>().LockAsync("Audit",id.ToString(),ct);var a=await RunAsync(u,id,ct);await u.Store<IAuditCapaStore>().LockAsync("Program",a.AuditProgram.Code,ct);var p=await ProgramAsync(u,a.AuditProgram.Code,ct);var r=Revision(p,a.AuditProgram.Revision);RequireEffective(r,now);await ProgramSources(u,context.Principal,p.Code,r.Content,true,now,ct);await Current(u,context.Principal,a.Sources,true,true,ct);
            var authority=await RunAuthority(u,context.Principal,a,true,ct);if(before is not null)await before(u,a,p,r,now);return authority;
        });
    internal CommandOutcome Outcome(ResourceRef scope,string type,string id,int version,string action,string state,object? old,object next,CommandContext context,IEnumerable<QmsPlanningSourceSnapshot> sources)=>
        new(CommandResult.Ok(id,type+": "+state+"."),[pipeline.Event(new AuditCapaChanged(type,id,version,action,state),context,"Quality",type,id,scope.Program,Marks(scope.Markings,sources))],new(type,id,old is null?null:CanonicalJson.Serialize(old),CanonicalJson.Serialize(next),scope.Program,Marks(scope.Markings,sources)));
    public static ResourceRef Scope(string type,string id,string facility,Guid? program,DataMarking marks)=>new(type,id,Program(program),marks,FacilityId:facility);
    public static ResourceRef Resource(QmsAuditProgram p)=>Scope("QmsAuditProgram",p.Code,p.FacilityCode,p.ProgramId,p.Markings);
    public static ResourceRef Resource(QmsAuditRun p)=>Scope("QmsAudit",p.Id.ToString(),p.FacilityCode,p.ProgramId,p.Markings);
    public static ResourceRef Resource(QmsCapaCase p)=>Scope("Capa",p.CapaId.ToString(),p.FacilityCode,p.ProgramId,p.Markings);
    internal static IEnumerable<string> Authors(QmsAuditProgramRevision r)=>new[]{r.CreatedBy,r.LastEditedBy}.Concat(r.History.Where(x=>x.Action is "Create" or "Edit").Select(x=>x.Actor));
    private static QmsAuditProgramRevision DraftProgram(string code,int revision,QmsAuditProgramContent content,IReadOnlyList<QmsPlanningSourceSnapshot> sources,CommandContext c,DateTimeOffset now)=>
        new(code,revision,1,QmsState.Draft,content,c.Actor,c.Actor,null,null,null,null,sources.Any(x=>x.RequiresLicensedVerification),sources,Append([],"Create",1,"","Draft","Human organization-defined program",c,now));
    private void Configured()=>Require(options.Configured,"qms_not_configured","Configure the explicit QMS organization.");
    public static CommandRejectedException Missing()=>new("audit_not_found","The audit/CAPA owner source is unavailable.");
}
