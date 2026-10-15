using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Kernel.Identity;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;
using Forge.Quality.Planning;
using Forge.Quality.Qms;
using static Forge.Quality.Planning.QmsPlanningRules;
using static Forge.Quality.Planning.QmsPlanningService;
using static Forge.Quality.Audit.QmsAuditService;

namespace Forge.Quality.Audit;

public sealed record CreateGovernedCapa(string Title,string FacilityCode,Guid? ProgramId,DataMarking Markings,
    QmsRevisionReference Process,string ProblemStatement,Guid OwnerPersonId,DateOnly DueDate,
    string InvestigationMethod,string InvestigationCriteria,string EffectivenessCriteria,string RecurrenceMonitoring,
    IReadOnlyList<long> RelatedNcrIds,IReadOnlyList<QmsPlanningEvidenceReference> Evidence,long? ExistingCapaId=null,Guid? OriginFindingId=null);
public sealed record WriteCapaInvestigation(long CapaId,int ExpectedCaseVersion,int Generation,int? Revision,
    int? ExpectedVersion,QmsCapaInvestigationContent Content);
public sealed record DecideCapaInvestigation(long CapaId,int Generation,int Revision,int ExpectedVersion,string Action,string Note);
public sealed record CreateCapaAction(long CapaId,int ExpectedCaseVersion,int Generation,QmsCorrectiveActionKind Kind,
    string Description,Guid OwnerPersonId,DateTimeOffset DueAt,string Resources,string CompletionCriteria);
public sealed record DecideCapaAction(Guid Id,int ExpectedVersion,int Generation,string Action,string Result,IReadOnlyList<QmsPlanningEvidenceReference> Evidence,int ActionGeneration=1);
public sealed record RecordCapaEffectiveness(long CapaId,int ExpectedCaseVersion,int Generation,DateTimeOffset From,
    DateTimeOffset To,string Method,string Criteria,string ActualResults,QmsEffectivenessResult Result,string Limitations,
    string RecurrenceReview,string FollowUpDecision,IReadOnlyList<QmsPlanningEvidenceReference> Evidence,IReadOnlyList<Guid> VerifiedActionIds);
public sealed record DecideCapaEffectiveness(Guid Id,int ExpectedVersion,int Generation,string Action,string Note);
public sealed record DecideGovernedCapa(long CapaId,int ExpectedVersion,int Generation,string Action,string Note,
    Guid? EffectivenessId,IReadOnlyList<QmsPlanningEvidenceReference> Evidence);

public sealed class QmsCapaService(CommandPipeline pipeline,IKernelUnitOfWorkFactory factory,QmsOptions options,
    QmsPlanningService planning,QmsAuditService audit,IQmsPlanningEvidenceReader evidence)
{
    public async Task<object> ReadAsync(ForgePrincipal p,long id,CancellationToken ct)
    {
        await using var u=await factory.BeginSnapshotAsync(ct);var c=await Case(u,id,ct);await Authority(u,p,c,false,ct);
        var investigations=await u.Store<IAuditCapaLookup>().InvestigationsAsync(id,c.Generation,ct);
        var actions=await u.Store<IAuditCapaLookup>().ActionsAsync(id,c.Generation,ct);
        var results=await u.Store<IAuditCapaLookup>().EffectivenessAsync(id,c.Generation,ct);
        // Current source access covers every returned child, including unsuccessful or withdrawn judgments.
        foreach(var item in investigations)await audit.Current(u,p,item.Sources,false,false,ct);
        foreach(var item in actions)await audit.Current(u,p,item.Evidence,false,false,ct);
        foreach(var item in results)await audit.Current(u,p,item.Evidence,false,false,ct);
        return new{capa=c,core=await u.Store<IQualityStore>().FindCapaAsync(id,ct),investigations,actions,effectiveness=results};
    }
    public async Task<object> ListAsync(ForgePrincipal p,int take,string? after,CancellationToken ct)
    {
        Require(take is >0 and <=100,"capa_page","Choose 1–100 records.");await using var u=await factory.BeginSnapshotAsync(ct);
        var items=new List<QmsCapaCase>();string? cursor=after,last=null;
        while(true)
        {
            var page=await u.Store<IAuditCapaLookup>().CapasAsync(100,cursor,ct);
            foreach(var c in page.Items)try{await Authority(u,p,c,false,ct);if(items.Count==take)return new{items,nextCursor=last};items.Add(c);last=c.CapaId.ToString();}catch(ForgeAuthorizationException){}catch(CommandRejectedException){}
            if(page.NextCursor is null)return new{items,nextCursor=(string?)null};cursor=page.NextCursor;
        }
    }

    /// <summary>Returns overdue, unverified action records after applying current CAPA and evidence access.</summary>
    public async Task<IReadOnlyList<QmsCapaAnalystOverdueAction>> OverdueActionsForAnalystAsync(ForgePrincipal p,DateTimeOffset asOf,int take,CancellationToken ct)
    {
        Require(take is >0 and <=100,"capa_page","Choose 1–100 records.");await using var u=await factory.BeginSnapshotAsync(ct);
        var items=new List<QmsCapaAnalystOverdueAction>();string? cursor=null;
        while(true)
        {
            var page=await u.Store<IAuditCapaLookup>().CapasAsync(100,cursor,ct);
            foreach(var capa in page.Items)
            {
                try
                {
                    await Authority(u,p,capa,false,ct);
                    if(capa.State is QmsCapaState.Closed or QmsCapaState.Withdrawn)continue;
                    foreach(var action in await u.Store<IAuditCapaLookup>().ActionsAsync(capa.CapaId,capa.Generation,ct))
                    {
                        if(action.DueAt>=asOf||action.State is QmsCorrectiveActionState.Verified or QmsCorrectiveActionState.Withdrawn)continue;
                        await audit.Current(u,p,action.Evidence,false,false,ct);
                        items.Add(new(capa.CapaId,capa.Generation,capa.State,action.Id,action.Kind,action.State,
                            action.Description,action.DueAt,action.OwnerPersonId));
                        if(items.Count==take)return items;
                    }
                }
                catch(ForgeAuthorizationException){}
                catch(CommandRejectedException){}
            }
            if(page.NextCursor is null)return items;cursor=page.NextCursor;
        }
    }

    public Task<CommandResult> CreateAsync(CreateGovernedCapa c,CommandContext context,CancellationToken ct)
    {
        var facility=BusinessCode.Require(c.FacilityCode,"Facility");var resource=Scope("Capa",c.ExistingCapaId?.ToString()??"new",facility,c.ProgramId,c.Markings);
        return pipeline.ExecuteAsync(new("Quality",nameof(CreateGovernedCapa),Permissions.QualityDisposition,resource,true),c,context,async(u,now)=>
        {
            await ValidateCreate(u,context.Principal,c,now,ct);var sources=await CreateSources(u,context.Principal,c,now,ct);
            CapaInfo? legacy=null;long id;
            if(c.ExistingCapaId is{} existing)
            {
                await u.Store<IAuditCapaStore>().LockCapaAsync(existing,ct);Require(await u.Store<IAuditCapaLookup>().FindCapaAsync(existing,ct)is null,"capa_governed","This CAPA already has governed investigation.");
                legacy=await u.Store<IQualityStore>().FindCapaAsync(existing,ct)??throw Missing();id=existing;
            }
            else id=await u.Store<IAuditCapaStore>().InsertUnknownCapaAsync(c.RelatedNcrIds.Count>0&&c.RelatedNcrIds[0]>0?c.RelatedNcrIds[0]:null,c.Title,c.DueDate,context.Actor,now,ct);
            var record=new QmsCapaCase(id,1,1,QmsCapaState.Investigating,options.OrganizationCode!,facility,c.ProgramId,c.Markings,c.Process,c.ProblemStatement,c.OwnerPersonId,c.DueDate,QmsCauseKnowledge.Unknown,null,null,c.InvestigationMethod,c.InvestigationCriteria,c.EffectivenessCriteria,c.RecurrenceMonitoring,c.RelatedNcrIds,sources,context.Actor,now,null,null,Append([],legacy is null?"Create":"GovernedReopen",1,legacy?.Status.ToString()??"","Investigating",c.ProblemStatement,context,now),legacy);
            await u.Store<IAuditCapaStore>().SaveCapaAsync(record,null,ct);
            if(legacy is not null)await u.Store<IAuditCapaStore>().UpdateCoreCapaAsync(id,null,null,"Open",null,null,null,now,ct);
            return audit.Outcome(Resource(record),"Capa",id.ToString(),1,legacy is null?"Create":"GovernedReopen","Investigating",legacy,record,context,sources);
        },ct,async(u,now)=>
        {
            if(c.ExistingCapaId is{} id){await u.Store<IAuditCapaStore>().LockCapaAsync(id,ct);if(await u.Store<IAuditCapaLookup>().FindCapaAsync(id,ct)is{} record){AuditCapaRules.Generation(record.Generation,1);await Process(u,context.Principal,record.Process,true,now,ct);await CreateSources(u,context.Principal,c,now,ct);return await Authority(u,context.Principal,record,true,ct);}}
            await ValidateCreate(u,context.Principal,c,now,ct);var sources=await CreateSources(u,context.Principal,c,now,ct);return new(resource with{Markings=Marks(resource.Markings,sources)},options.OrganizationCode+":Capa:"+c.ExistingCapaId);
        });
    }
    public Task<CommandResult> InvestigateAsync(WriteCapaInvestigation c,CommandContext context,CancellationToken ct)=>Execute(nameof(WriteCapaInvestigation),c,c.CapaId,Permissions.QualityDisposition,context,async(u,old,now)=>
    {
        Version(old.Version,c.ExpectedCaseVersion);AuditCapaRules.Generation(old.Generation,c.Generation);Require(old.State is QmsCapaState.Investigating or QmsCapaState.Reopened,"capa_state","Investigate before a plan is independently approved.");
        AuditCapaRules.Investigation(c.Content);var children=await u.Store<IAuditCapaLookup>().InvestigationsAsync(old.CapaId,old.Generation,ct);QmsCapaInvestigation? prior=c.Revision is{} n?children.FirstOrDefault(x=>x.Revision==n)??throw Missing():null;
        if(prior is not null){Require(c.ExpectedVersion is{} v,"stale_version","Specify the draft version.");Version(prior.Version,c.ExpectedVersion!.Value);Require(prior.State==QmsState.Draft,"capa_state","Only draft investigation content may be edited.");}
        else Require(!children.Any(x=>x.State is QmsState.Draft or QmsState.Submitted or QmsState.Reviewed),"capa_open_revision","Resolve the open investigation proposal first.");
        var sources=await InvestigationSources(u,context.Principal,c.Content,ct);var rev=prior?.Revision??(children.Count==0?1:children.Max(x=>x.Revision)+1);
        var record=new QmsCapaInvestigation(old.CapaId,old.Generation,rev,prior is null?1:prior.Version+1,QmsState.Draft,c.Content,prior?.CreatedBy??context.Actor,context.Actor,null,null,sources,Append(prior?.History??[],prior is null?"Create":"Edit",prior is null?1:prior.Version+1,prior?.State.ToString()??"","Draft",c.Content.ActualInvestigation,context,now));
        await u.Store<IAuditCapaStore>().SaveInvestigationAsync(record,prior?.Version,ct);
        var next=old with{Version=old.Version+1,CauseKnowledge=c.Content.Proposals.Count>0?QmsCauseKnowledge.Proposed:QmsCauseKnowledge.Unknown,History=Append(old.History,"InvestigationDraft",old.Version+1,old.State.ToString(),old.State.ToString(),"Investigation "+rev,context,now)};
        await u.Store<IAuditCapaStore>().SaveCapaAsync(next,old.Version,ct);return audit.Outcome(Resource(old),"QmsCapaInvestigation",Key(record),record.Version,prior is null?"Create":"Edit","Draft",prior,new{investigation=record,capaVersion=next.Version},context,sources);
    },ct,async(u,old,now)=>{AuditCapaRules.Generation(old.Generation,c.Generation);await InvestigationSources(u,context.Principal,c.Content,ct);});
    public Task<CommandResult> DecideInvestigationAsync(DecideCapaInvestigation c,CommandContext context,CancellationToken ct)=>Execute("CapaInvestigation"+c.Action,c,c.CapaId,c.Action is "Review" or "Approve"?Permissions.QualityApprove:Permissions.QualityDisposition,context,async(u,old,now)=>
    {
        AuditCapaRules.Generation(old.Generation,c.Generation);var i=(await u.Store<IAuditCapaLookup>().InvestigationsAsync(c.CapaId,c.Generation,ct)).FirstOrDefault(x=>x.Revision==c.Revision)??throw Missing();Version(i.Version,c.ExpectedVersion);Text(c.Note,"Human investigation decision");
        Require(c.Action is "Submit" or "Review" or "Approve" or "Withdraw","capa_action","Use an investigation lifecycle decision.");Require(i.State!=QmsState.Approved,"capa_state","A confirmed investigation is retained; reopen the case for a new cause investigation.");var state=Transition(i.State,c.Action);
        var sources=await InvestigationSources(u,context.Principal,i.Content,ct);
        if(c.Action is "Review" or "Approve")Independent(context.Actor,InvestigationAuthors(i).Append(old.CreatedBy),sources.Concat(old.Sources));
        if(c.Action=="Approve")Require(i.Content.HumanDeterminedCause is not null,"capa_cause_unknown","A cause remains unknown. Continue investigation; do not approve a fabricated cause.");
        var next=i with{Version=i.Version+1,State=state,ReviewedBy=c.Action=="Review"?context.Actor:i.ReviewedBy,ApprovedBy=c.Action=="Approve"?context.Actor:i.ApprovedBy,Sources=sources,History=Append(i.History,c.Action,i.Version+1,i.State.ToString(),state.ToString(),c.Note,context,now)};
        await u.Store<IAuditCapaStore>().SaveInvestigationAsync(next,i.Version,ct);
        var capaState=c.Action switch{"Submit"=>QmsCapaState.InvestigationSubmitted,"Review"=>QmsCapaState.InvestigationReviewed,"Approve"=>QmsCapaState.PlanApproved,_=>QmsCapaState.Investigating};
        var record=old with{Version=old.Version+1,State=capaState,CauseKnowledge=c.Action=="Approve"?QmsCauseKnowledge.Confirmed:old.CauseKnowledge,ConfirmedCause=c.Action=="Approve"?i.Content.HumanDeterminedCause:old.ConfirmedCause,ApprovedPlan=c.Action=="Approve"?i.Content.ActionPlan:old.ApprovedPlan,ApprovedInvestigationRevision=c.Action=="Approve"?i.Revision:old.ApprovedInvestigationRevision,History=Append(old.History,"Investigation"+c.Action,old.Version+1,old.State.ToString(),capaState.ToString(),c.Note,context,now)};
        await u.Store<IAuditCapaStore>().SaveCapaAsync(record,old.Version,ct);
        if(c.Action=="Approve")await u.Store<IAuditCapaStore>().UpdateCoreCapaAsync(old.CapaId,i.Content.HumanDeterminedCause,i.Content.ActionPlan,"Open",null,null,null,now,ct);
        return audit.Outcome(Resource(old),"QmsCapaInvestigation",Key(next),next.Version,c.Action,state.ToString(),i,new{investigation=next,capa=record},context,sources);
    },ct,async(u,old,now)=>{AuditCapaRules.Generation(old.Generation,c.Generation);var i=(await u.Store<IAuditCapaLookup>().InvestigationsAsync(c.CapaId,c.Generation,ct)).FirstOrDefault(x=>x.Revision==c.Revision)??throw Missing();await audit.Current(u,context.Principal,i.Sources,true,true,ct);if(c.Action is "Review" or "Approve")Independent(context.Actor,InvestigationAuthors(i).Append(old.CreatedBy),i.Sources.Concat(old.Sources));});
    public Task<CommandResult> CreateActionAsync(CreateCapaAction c,CommandContext context,CancellationToken ct)=>Execute(nameof(CreateCapaAction),c,c.CapaId,Permissions.QualityDisposition,context,async(u,old,now)=>
    {
        Version(old.Version,c.ExpectedCaseVersion);AuditCapaRules.Generation(old.Generation,c.Generation);
        Require(Enum.IsDefined(c.Kind),"capa_action_kind","Distinguish correction, containment and corrective action.");
        Require(c.Kind!=QmsCorrectiveActionKind.CorrectiveAction||old.State is QmsCapaState.PlanApproved or QmsCapaState.Implementing,"capa_plan","Approve the actual investigation plan before corrective action.");
        Require(old.State is not (QmsCapaState.Closed or QmsCapaState.Withdrawn or QmsCapaState.EffectivenessSubmitted),"capa_state","This case does not accept new actions.");
        foreach(var t in new[]{c.Description,c.Resources,c.CompletionCriteria})Text(t,"Owned action");Instant(c.DueAt);await QmsAuditService.Active(u,c.OwnerPersonId,ct);
        var id=ForgeIds.New(pipeline.Time);var action=new QmsCapaAction(id,old.CapaId,old.Generation,1,c.Kind,QmsCorrectiveActionState.Open,c.Description,c.OwnerPersonId,c.DueAt,c.Resources,c.CompletionCriteria,"",context.Actor,now,null,null,null,null,[],Append([],"Create",1,"","Open",c.Description,context,now));
        await u.Store<IAuditCapaStore>().SaveActionAsync(action,null,ct);
        var next=old with{Version=old.Version+1,State=c.Kind==QmsCorrectiveActionKind.CorrectiveAction?QmsCapaState.Implementing:old.State,History=Append(old.History,"Action",old.Version+1,old.State.ToString(),c.Kind==QmsCorrectiveActionKind.CorrectiveAction?"Implementing":old.State.ToString(),id.ToString(),context,now)};await u.Store<IAuditCapaStore>().SaveCapaAsync(next,old.Version,ct);
        return audit.Outcome(Resource(old),"QmsCapaAction",id.ToString(),1,"Create","Open",null,new{action,capaVersion=next.Version},context,old.Sources);
    },ct,async(u,old,now)=>{AuditCapaRules.Generation(old.Generation,c.Generation);await QmsAuditService.Active(u,c.OwnerPersonId,ct);});
    public async Task<CommandResult> DecideActionAsync(DecideCapaAction c,CommandContext context,CancellationToken ct)
    {
        await using var snapshot=await factory.BeginSnapshotAsync(ct);var initial=await snapshot.Store<IAuditCapaLookup>().FindActionAsync(c.Id,ct)??throw Missing();
        return await Execute("CapaAction"+c.Action,c,initial.CapaId,c.Action=="Verify"?Permissions.QualityApprove:Permissions.QualityDisposition,context,async(u,old,now)=>
        {
            var a=await u.Store<IAuditCapaLookup>().FindActionAsync(c.Id,ct)??throw Missing();Version(a.Version,c.ExpectedVersion);AuditCapaRules.Generation(old.Generation,c.Generation);AuditCapaRules.Generation(old.Generation,a.Generation);AuditCapaRules.Generation(a.ActionGeneration,c.ActionGeneration);
            Require(old.State is not(QmsCapaState.Closed or QmsCapaState.Withdrawn),"capa_state","Reopen the case before changing its action evidence.");Text(c.Result,"Actual action result");
            var state=c.Action switch{"Complete" when a.State is QmsCorrectiveActionState.Open or QmsCorrectiveActionState.Reopened=>QmsCorrectiveActionState.Completed,"Verify" when a.State==QmsCorrectiveActionState.Completed=>QmsCorrectiveActionState.Verified,"Reopen" when a.State is QmsCorrectiveActionState.Completed or QmsCorrectiveActionState.Verified=>QmsCorrectiveActionState.Reopened,"Withdraw" when a.State is QmsCorrectiveActionState.Open or QmsCorrectiveActionState.Reopened=>QmsCorrectiveActionState.Withdrawn,_=>throw new CommandRejectedException("capa_action_state","The action decision is unavailable.")};
            var sources=await audit.Resolve(u,context.Principal,c.Evidence,ct);Require(sources.Count>0,"capa_evidence","Record actual action evidence.");
            if(c.Action=="Complete")Require(a.OwnerPersonId==context.Principal.PersonId.Value,"capa_action_owner","The actual assigned owner records completion.");
            if(c.Action=="Verify")Independent(context.Actor,[a.CreatedBy,"person:"+a.OwnerPersonId,a.CompletedBy??""],a.Evidence.Concat(sources));
            var next=a with{Version=a.Version+1,State=state,ActionGeneration=c.Action=="Reopen"?a.ActionGeneration+1:a.ActionGeneration,Result=c.Result,Evidence=a.Evidence.Concat(sources).DistinctBy(x=>x.Reference).ToArray(),CompletedBy=c.Action=="Complete"?context.Actor:c.Action=="Reopen"?null:a.CompletedBy,CompletedAt=c.Action=="Complete"?now:c.Action=="Reopen"?null:a.CompletedAt,VerifiedBy=c.Action=="Verify"?context.Actor:c.Action=="Reopen"?null:a.VerifiedBy,VerifiedAt=c.Action=="Verify"?now:c.Action=="Reopen"?null:a.VerifiedAt,History=Append(a.History,c.Action,a.Version+1,a.State.ToString(),state.ToString(),c.Result,context,now)};
            await u.Store<IAuditCapaStore>().SaveActionAsync(next,a.Version,ct);
            var invalidatesImplementation=c.Action=="Reopen"&&old.State is QmsCapaState.EffectivenessPending or QmsCapaState.EffectivenessSubmitted;
            var core=await u.Store<IQualityStore>().FindCapaAsync(old.CapaId,ct)??throw Missing();
            var changed=old with{Version=old.Version+1,State=invalidatesImplementation?QmsCapaState.Implementing:old.State,LatestEffectivenessId=invalidatesImplementation?null:old.LatestEffectivenessId,History=Append(old.History,"Action"+c.Action,old.Version+1,old.State.ToString(),invalidatesImplementation?"Implementing":old.State.ToString(),c.Id.ToString(),context,now)};await u.Store<IAuditCapaStore>().SaveCapaAsync(changed,old.Version,ct);
            if(invalidatesImplementation)await u.Store<IAuditCapaStore>().UpdateCoreCapaAsync(old.CapaId,old.ConfirmedCause,old.ApprovedPlan,"Open",null,null,null,now,ct);
            var outcome=audit.Outcome(Resource(old),"QmsCapaAction",c.Id.ToString(),next.Version,c.Action,state.ToString(),new{action=a,capa=old,core},new{action=next,capa=changed,core=await u.Store<IQualityStore>().FindCapaAsync(old.CapaId,ct)},context,next.Evidence);
            return invalidatesImplementation?outcome with{Events=outcome.Events.Append(pipeline.Event(new AuditCapaChanged("Capa",old.CapaId.ToString(),changed.Version,"ImplementationReopened","Implementing"),context,"Quality","Capa",old.CapaId.ToString(),Program(old.ProgramId),Marks(old.Markings,old.Sources))).ToArray()}:outcome;
        },ct,async(u,old,now)=>{AuditCapaRules.Generation(old.Generation,c.Generation);var a=await u.Store<IAuditCapaLookup>().FindActionAsync(c.Id,ct)??throw Missing();AuditCapaRules.Generation(a.ActionGeneration,c.ActionGeneration);await audit.Current(u,context.Principal,a.Evidence,true,true,ct);await audit.Resolve(u,context.Principal,c.Evidence,ct);if(c.Action=="Verify")Independent(context.Actor,[a.CreatedBy,"person:"+a.OwnerPersonId,a.CompletedBy??""],a.Evidence);});
    }
    public Task<CommandResult> EffectivenessAsync(RecordCapaEffectiveness c,CommandContext context,CancellationToken ct)=>Execute(nameof(RecordCapaEffectiveness),c,c.CapaId,Permissions.QualityDisposition,context,async(u,old,now)=>
    {
        Version(old.Version,c.ExpectedCaseVersion);AuditCapaRules.Generation(old.Generation,c.Generation);Require(old.State==QmsCapaState.EffectivenessPending,"capa_state","Complete verified implementation before evaluating effectiveness.");Range(c.From,c.To);Require(c.To<=now,"capa_period","The effectiveness window must have actually elapsed.");
        Require(Enum.IsDefined(c.Result),"capa_result","Record Effective, Ineffective or Inconclusive.");foreach(var t in new[]{c.Method,c.Criteria,c.ActualResults,c.Limitations,c.RecurrenceReview,c.FollowUpDecision})Text(t,"Actual effectiveness assessment");
        Require(c.Criteria==old.EffectivenessCriteria,"capa_effectiveness","Use the approved case effectiveness criteria.");var actions=await RequiredActions(u,context.Principal,old,ct);Require(c.VerifiedActionIds.Distinct().Count()==c.VerifiedActionIds.Count&&actions.Select(x=>x.Id).Order().SequenceEqual(c.VerifiedActionIds.Order()),"capa_action_manifest","Freeze every current verified required action in the exact effectiveness manifest.");
        var implementation=old.History.LastOrDefault(x=>x.Action=="Implemented")?.At??throw Missing();Require(c.From>=implementation&&actions.All(a=>a.VerifiedAt<=c.From),"capa_period","The actual effectiveness window must start after recorded implementation and verification, and must have elapsed.");
        var sources=await audit.Resolve(u,context.Principal,c.Evidence,ct);Require(sources.Count>0,"capa_evidence","Record actual effectiveness evidence.");var id=ForgeIds.New(pipeline.Time);
        var result=new QmsCapaEffectiveness(id,old.CapaId,old.Generation,1,QmsState.Draft,c.From,c.To,c.Method,c.Criteria,c.ActualResults,c.Result,c.Limitations,c.RecurrenceReview,c.FollowUpDecision,sources,c.VerifiedActionIds,context.Actor,null,null,Append([],"Create",1,"","Draft",c.ActualResults,context,now),actions.Select(a=>new QmsCapaActionEvidence(a.Id,a.Version,CanonicalJson.Fingerprint(a))).ToArray());
        await u.Store<IAuditCapaStore>().SaveEffectivenessAsync(result,null,ct);
        var next=old with{Version=old.Version+1,LatestEffectivenessId=id,History=Append(old.History,"EffectivenessDraft",old.Version+1,old.State.ToString(),old.State.ToString(),id.ToString(),context,now)};await u.Store<IAuditCapaStore>().SaveCapaAsync(next,old.Version,ct);
        return audit.Outcome(Resource(old),"QmsCapaEffectiveness",id.ToString(),1,"Create","Draft",null,new{effectiveness=result,capaVersion=next.Version},context,sources);
    },ct,async(u,old,now)=>{AuditCapaRules.Generation(old.Generation,c.Generation);await RequiredActions(u,context.Principal,old,ct);await audit.Resolve(u,context.Principal,c.Evidence,ct);});
    public async Task<CommandResult> DecideEffectivenessAsync(DecideCapaEffectiveness c,CommandContext context,CancellationToken ct)
    {
        await using var snap=await factory.BeginSnapshotAsync(ct);var initial=await snap.Store<IAuditCapaLookup>().FindEffectivenessAsync(c.Id,ct)??throw Missing();
        return await Execute("CapaEffectiveness"+c.Action,c,initial.CapaId,c.Action is "Review" or "Approve"?Permissions.QualityApprove:Permissions.QualityDisposition,context,async(u,old,now)=>
        {
            var e=await u.Store<IAuditCapaLookup>().FindEffectivenessAsync(c.Id,ct)??throw Missing();Version(e.Version,c.ExpectedVersion);AuditCapaRules.Generation(old.Generation,c.Generation);AuditCapaRules.Generation(old.Generation,e.Generation);
            Require(old.LatestEffectivenessId==e.Id,"capa_effectiveness","The case has a newer effectiveness assessment.");Text(c.Note,"Human effectiveness decision");Require(c.Action is "Submit" or "Review" or "Approve" or "Withdraw","capa_action","Use the effectiveness lifecycle.");
            var state=Transition(e.State,c.Action);var actions=await RequiredActions(u,context.Principal,old,ct);AuditCapaRules.ActionManifest(e,actions);await audit.Current(u,context.Principal,e.Evidence,true,true,ct);
            if(c.Action is "Review" or "Approve")Independent(context.Actor,EffectivenessAuthors(old,e,actions),e.Evidence.Concat(actions.SelectMany(x=>x.Evidence)));
            var next=e with{Version=e.Version+1,State=state,ReviewedBy=c.Action=="Review"?context.Actor:e.ReviewedBy,ApprovedBy=c.Action=="Approve"?context.Actor:e.ApprovedBy,History=Append(e.History,c.Action,e.Version+1,e.State.ToString(),state.ToString(),c.Note,context,now)};await u.Store<IAuditCapaStore>().SaveEffectivenessAsync(next,e.Version,ct);
            var changed=old with{Version=old.Version+1,State=c.Action=="Submit"?QmsCapaState.EffectivenessSubmitted:c.Action=="Withdraw"?QmsCapaState.EffectivenessPending:old.State,History=Append(old.History,"Effectiveness"+c.Action,old.Version+1,old.State.ToString(),c.Action=="Submit"?"EffectivenessSubmitted":old.State.ToString(),c.Note,context,now)};await u.Store<IAuditCapaStore>().SaveCapaAsync(changed,old.Version,ct);
            return audit.Outcome(Resource(old),"QmsCapaEffectiveness",c.Id.ToString(),next.Version,c.Action,state.ToString(),e,new{effectiveness=next,capaVersion=changed.Version},context,next.Evidence);
        },ct,async(u,old,now)=>{AuditCapaRules.Generation(old.Generation,c.Generation);var e=await u.Store<IAuditCapaLookup>().FindEffectivenessAsync(c.Id,ct)??throw Missing();Require(old.LatestEffectivenessId==e.Id,"capa_effectiveness","This is not the current assessment.");await audit.Current(u,context.Principal,e.Evidence,true,true,ct);var actions=await RequiredActions(u,context.Principal,old,ct);AuditCapaRules.ActionManifest(e,actions);if(c.Action is "Review" or "Approve")Independent(context.Actor,EffectivenessAuthors(old,e,actions),e.Evidence.Concat(actions.SelectMany(x=>x.Evidence)));});
    }
    public Task<CommandResult> DecideAsync(DecideGovernedCapa c,CommandContext context,CancellationToken ct)=>Execute("GovernedCapa"+c.Action,c,c.CapaId,c.Action is "Close" or "Reopen" or "Withdraw"?Permissions.QualityApprove:Permissions.QualityDisposition,context,async(u,old,now)=>
    {
        Version(old.Version,c.ExpectedVersion);AuditCapaRules.Generation(old.Generation,c.Generation);Text(c.Note,"Human case decision");var sources=await audit.Resolve(u,context.Principal,c.Evidence,ct);Require(sources.Count>0,"capa_evidence","Case decisions need actual evidence.");QmsCapaState state;
        if(c.Action=="Implemented")
        {
            Require(old.State is QmsCapaState.Implementing or QmsCapaState.PlanApproved,"capa_state","An approved plan and actual corrective action are required.");await RequiredActions(u,context.Principal,old,ct);state=QmsCapaState.EffectivenessPending;
            await u.Store<IAuditCapaStore>().UpdateCoreCapaAsync(old.CapaId,old.ConfirmedCause,old.ApprovedPlan,"Implemented",context.Actor,c.Note,null,now,ct);
        }
        else if(c.Action=="Close")
        {
            Require(old.State==QmsCapaState.EffectivenessSubmitted&&c.EffectivenessId==old.LatestEffectivenessId,"capa_effectiveness","Use the exact latest reviewed effectiveness result.");
            var e=await u.Store<IAuditCapaLookup>().FindEffectivenessAsync(c.EffectivenessId!.Value,ct)??throw Missing();AuditCapaRules.Generation(old.Generation,e.Generation);AuditCapaRules.CloseEffectiveness(e);var actions=await RequiredActions(u,context.Principal,old,ct);AuditCapaRules.ActionManifest(e,actions);Independent(context.Actor,EffectivenessAuthors(old,e,actions),e.Evidence.Concat(sources));
            var core=await u.Store<IQualityStore>().FindCapaAsync(old.CapaId,ct)??throw Missing();Require(core.ImplementedBy!=context.Actor,"separation_of_duties","The implementation actor cannot independently close this case.");state=QmsCapaState.Closed;
            await u.Store<IAuditCapaStore>().UpdateCoreCapaAsync(old.CapaId,old.ConfirmedCause,old.ApprovedPlan,"Verified",core.ImplementedBy,core.ImplementationNote,context.Actor,now,ct);
        }
        else if(c.Action=="Reopen")
        {
            Require(old.State==QmsCapaState.Closed||old.State is QmsCapaState.EffectivenessPending or QmsCapaState.EffectivenessSubmitted,"capa_state","Reopen a closed case or an unsuccessful effectiveness evaluation.");
            if(old.State!=QmsCapaState.Closed){var e=old.LatestEffectivenessId is{} id?await u.Store<IAuditCapaLookup>().FindEffectivenessAsync(id,ct):null;Require(e is{State:QmsState.Approved}&&e.Result!=QmsEffectivenessResult.Effective,"capa_reopen","An independently reviewed ineffective or inconclusive result is required for this generation.");}
            state=QmsCapaState.Reopened;await u.Store<IAuditCapaStore>().UpdateCoreCapaAsync(old.CapaId,null,null,"Open",null,null,null,now,ct);
        }
        else if(c.Action=="Withdraw"){Require(old.State is not(QmsCapaState.Closed or QmsCapaState.Withdrawn),"capa_state","Withdrawal is unavailable.");state=QmsCapaState.Withdrawn;}
        else throw new CommandRejectedException("capa_action","Unknown governed case decision.");
        var next=old with{Version=old.Version+1,State=state,Generation=c.Action=="Reopen"?old.Generation+1:old.Generation,CauseKnowledge=c.Action=="Reopen"?QmsCauseKnowledge.Unknown:old.CauseKnowledge,ConfirmedCause=c.Action=="Reopen"?null:old.ConfirmedCause,ApprovedPlan=c.Action=="Reopen"?null:old.ApprovedPlan,ApprovedInvestigationRevision=c.Action=="Reopen"?null:old.ApprovedInvestigationRevision,LatestEffectivenessId=c.Action=="Reopen"?null:old.LatestEffectivenessId,History=Append(old.History,c.Action,old.Version+1,old.State.ToString(),state.ToString(),c.Note,context,now)};
        await u.Store<IAuditCapaStore>().SaveCapaAsync(next,old.Version,ct);return audit.Outcome(Resource(old),"Capa",old.CapaId.ToString(),next.Version,c.Action,state.ToString(),old,next,context,old.Sources.Concat(sources));
    },ct,async(u,old,now)=>{AuditCapaRules.Generation(old.Generation,c.Generation);await audit.Resolve(u,context.Principal,c.Evidence,ct);if(c.Action=="Close"){Require(c.EffectivenessId==old.LatestEffectivenessId,"capa_effectiveness","Use the current result.");var e=await u.Store<IAuditCapaLookup>().FindEffectivenessAsync(c.EffectivenessId!.Value,ct)??throw Missing();await audit.Current(u,context.Principal,e.Evidence,true,true,ct);var actions=await RequiredActions(u,context.Principal,old,ct);AuditCapaRules.ActionManifest(e,actions);Independent(context.Actor,EffectivenessAuthors(old,e,actions),e.Evidence);}});

    internal async Task<QmsCapaCase> Case(IKernelUnitOfWork u,long id,CancellationToken ct)
    {Require(options.Configured,"qms_not_configured","Configure the explicit organization.");Require(await u.Store<IQualityStore>().FindCapaAsync(id,ct)is not null,"audit_not_found","The existing core CAPA is unavailable.");return await u.Store<IAuditCapaLookup>().FindCapaAsync(id,ct)??throw Missing();}
    private async Task<CommandAuthority> Authority(IKernelUnitOfWork u,ForgePrincipal p,QmsCapaCase c,bool locks,CancellationToken ct)
    {
        var sources=new List<QmsPlanningSourceSnapshot>(c.Sources);
        foreach(var i in await u.Store<IAuditCapaLookup>().InvestigationsAsync(c.CapaId,0,ct))sources.AddRange(i.Sources);
        foreach(var a in await u.Store<IAuditCapaLookup>().ActionsAsync(c.CapaId,0,ct))sources.AddRange(a.Evidence);
        foreach(var e in await u.Store<IAuditCapaLookup>().EffectivenessAsync(c.CapaId,0,ct))sources.AddRange(e.Evidence);
        return await audit.Authority(u,p,Resource(c),c.OrganizationCode,sources,locks,ct);
    }
    private Task<CommandResult> Execute<T>(string name,T command,long id,string permission,CommandContext context,Func<IKernelUnitOfWork,QmsCapaCase,DateTimeOffset,Task<CommandOutcome>> body,CancellationToken ct,Func<IKernelUnitOfWork,QmsCapaCase,DateTimeOffset,Task>? before=null)=>
        pipeline.ExecuteAsync(new("Quality",name,permission,ResourceRef.Of("Capa",id.ToString()),true),command,context,async(u,now)=>{var record=await Case(u,id,ct);return await body(u,record,now);},ct,async(u,now)=>
        {await u.Store<IAuditCapaStore>().LockCapaAsync(id,ct);var record=await Case(u,id,ct);var authority=await Authority(u,context.Principal,record,true,ct);await Process(u,context.Principal,record.Process,true,now,ct);await audit.Current(u,context.Principal,record.Sources,false,true,ct);if(before is not null)await before(u,record,now);if(command is DecideGovernedCapa{Action:"Implemented"}){var manifest=await RequiredActions(u,context.Principal,record,ct);authority=authority with{SubjectIdentity=authority.SubjectIdentity+":"+CanonicalJson.Fingerprint(manifest.OrderBy(a=>a.Id).Select(a=>new{a.Id,a.Version,a.ActionGeneration}))};}return authority;});
    private async Task ValidateCreate(IKernelUnitOfWork u,ForgePrincipal p,CreateGovernedCapa c,DateTimeOffset now,CancellationToken ct)
    {
        await planning.HeaderAsync(u,c.FacilityCode,c.ProgramId,c.Markings,ct);Text(c.Title,"CAPA title",200);foreach(var s in new[]{c.ProblemStatement,c.InvestigationMethod,c.InvestigationCriteria,c.EffectivenessCriteria,c.RecurrenceMonitoring})Text(s,"Actual CAPA governance");await QmsAuditService.Active(u,c.OwnerPersonId,ct);Require(c.DueDate!=default,"capa_date","Record an actual due date.");List(c.RelatedNcrIds,"Related NCRs",500);
        Require(c.RelatedNcrIds.All(x=>x>0)&&c.RelatedNcrIds.Distinct().Count()==c.RelatedNcrIds.Count,"capa_ncr","Use distinct actual NCR IDs.");
        await Process(u,p,c.Process,true,now,ct);
        if(c.ExistingCapaId is{} id){var core=await u.Store<IQualityStore>().FindCapaAsync(id,ct)??throw Missing();var scope=await QualityService.ResourceForCapaAsync(u,core,ct);planning.Permit(p,Permissions.QualityRead,scope);planning.Permit(p,Permissions.QualityApprove,scope);Require(core.NcrId is null||c.RelatedNcrIds.Contains(core.NcrId.Value),"capa_ncr","Preserve the original actual NCR link when governing an existing CAPA.");}
    }
    private async Task<QmsPlanningSourceSnapshot> Process(IKernelUnitOfWork u,ForgePrincipal p,QmsRevisionReference reference,bool effective,DateTimeOffset now,CancellationToken ct)
    {
        Reference(reference);var q=await u.Store<IQmsLookup>().FindAsync(reference.Code,ct)??throw Missing();var r=q.Revisions.FirstOrDefault(x=>x.Revision==reference.Revision)??throw Missing();
        Require(q.Kind==QmsRecordKind.Process,"capa_process","Select an actual controlled source-control process.");
        if(effective)Require(r.State==QmsState.Effective&&r.EffectiveFrom<=now&&(r.EffectiveTo is null||now<r.EffectiveTo),"capa_process_not_effective","The exact source-control process must currently be effective.");
        if(effective)await audit.ValidateControlledAsync(u,p,reference,QmsRecordKind.Process,now,true,ct);
        return await evidence.ResolveAsync(u,p,new("Quality","QmsRevision",reference.Code,reference.Revision.ToString()),true,ct);
    }
    private async Task<IReadOnlyList<QmsPlanningSourceSnapshot>> CreateSources(IKernelUnitOfWork u,ForgePrincipal p,CreateGovernedCapa c,DateTimeOffset now,CancellationToken ct)
    {
        var sources=new List<QmsPlanningSourceSnapshot>{await Process(u,p,c.Process,true,now,ct)};
        foreach(var id in c.RelatedNcrIds)sources.Add(await evidence.ResolveAsync(u,p,new("Quality","Ncr",id.ToString()),true,ct));
        if(c.OriginFindingId is{} finding)sources.Add(await evidence.ResolveAsync(u,p,new("Quality","QmsAuditFindingOrigin",finding.ToString()),true,ct));
        Require(!c.Evidence.Any(x=>x.Context=="Quality"&&x.Type=="QmsAuditFinding"),"capa_origin_reference","Use the explicit original finding ID for immutable audit finding origin evidence.");
        sources.AddRange(await audit.Resolve(u,p,c.Evidence,ct));return sources.DistinctBy(x=>x.Reference).ToArray();
    }
    private async Task<IReadOnlyList<QmsPlanningSourceSnapshot>> InvestigationSources(IKernelUnitOfWork u,ForgePrincipal p,QmsCapaInvestigationContent c,CancellationToken ct)
    {AuditCapaRules.Investigation(c);return await audit.Resolve(u,p,c.Evidence.Concat(c.Proposals.SelectMany(x=>x.Evidence)).Distinct().ToArray(),ct);}
    private async Task<IReadOnlyList<QmsCapaAction>> RequiredActions(IKernelUnitOfWork u,ForgePrincipal p,QmsCapaCase c,CancellationToken ct)
    {
        var actions=(await u.Store<IAuditCapaLookup>().ActionsAsync(c.CapaId,c.Generation,ct)).Where(x=>x.State!=QmsCorrectiveActionState.Withdrawn).ToArray();
        Require(actions.Any(x=>x.Kind==QmsCorrectiveActionKind.CorrectiveAction)&&actions.All(x=>x.State==QmsCorrectiveActionState.Verified),"capa_implementation","Correction or containment alone cannot satisfy corrective action; every required action must be independently verified.");
        foreach(var a in actions)await audit.Current(u,p,a.Evidence,true,true,ct);return actions;
    }
    private static IEnumerable<string> InvestigationAuthors(QmsCapaInvestigation i)=>new[]{i.CreatedBy,i.LastEditedBy}.Concat(i.History.Where(x=>x.Action is "Create" or "Edit").Select(x=>x.Actor));
    private static IEnumerable<string> EffectivenessAuthors(QmsCapaCase c,QmsCapaEffectiveness e,IEnumerable<QmsCapaAction> actions)=>new[]{c.CreatedBy,e.CreatedBy}.Concat(c.Sources.SelectMany(x=>x.Contributors)).Concat(actions.SelectMany(x=>new[]{x.CreatedBy,"person:"+x.OwnerPersonId,x.CompletedBy??""})).Concat(c.History.Where(x=>x.Action=="Implemented").Select(x=>x.Actor));
    private static string Key(QmsCapaInvestigation i)=>i.CapaId+"/"+i.Generation+"/"+i.Revision;
}
