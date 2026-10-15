using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;
using Forge.Quality.Qms;
using Forge.Resources.Contracts;

namespace Forge.Quality.Acceptance;

public sealed record DefineAcceptanceCriteria(string Code,string FacilityCode,Guid? ProgramId,DataMarking Markings,AcceptanceCriteriaContent Content,int? ExpectedVersion=null);
public sealed record EditAcceptanceCriteria(string Code,int Revision,int ExpectedVersion,AcceptanceCriteriaContent Content);
public sealed record TransitionAcceptanceCriteria(string Code,int Revision,int ExpectedVersion,string Action,string Note);
public sealed record AcceptanceChanged(string Kind,string Key,string Action):IDomainEvent
{public static string EventType=>nameof(AcceptanceChanged);public static int SchemaVersion=>1;}

public sealed class AcceptanceCriteriaService(CommandPipeline pipeline,IKernelUnitOfWorkFactory factory,IForgeAuthorizer authorizer)
{
 public async Task<IReadOnlyList<AcceptanceCriteriaRevision>> ListAsync(ForgePrincipal principal,CancellationToken ct)
 {
  await using var uow=await factory.BeginSnapshotAsync(ct);var result=new List<AcceptanceCriteriaRevision>();
  foreach(var revision in await uow.Store<IAcceptanceLookup>().ListCriteriaAsync(ct))if(await CanReadAsync(uow,principal,revision,ct))result.Add(revision);return result;
 }
 public async Task<AcceptanceCriteriaRevision> ReadAsync(ForgePrincipal principal,string code,int revision,CancellationToken ct)
 {
  await using var uow=await factory.BeginSnapshotAsync(ct);var selected=await uow.Store<IAcceptanceLookup>().FindCriteriaAsync(BusinessCode.Require(code,"Criteria"),revision,ct)??throw Missing();
  return await CanReadAsync(uow,principal,selected,ct)?selected:throw Missing();
 }
 public async Task<bool> CanReadAsync(IKernelUnitOfWork uow,ForgePrincipal principal,AcceptanceCriteriaRevision revision,CancellationToken ct)
 {try{Permit(principal,Permissions.QualityRead,Resource(revision));await SourcesAsync(uow,principal,revision,false,false,pipeline.Time.GetUtcNow(),ct);return true;}catch(ForgeAuthorizationException){return false;}catch(CommandRejectedException){return false;}}
 public Task<CommandResult> DefineAsync(DefineAcceptanceCriteria c,CommandContext context,CancellationToken ct)
 {
  var code=BusinessCode.Require(c.Code,"Criteria");var facility=BusinessCode.Require(c.FacilityCode,"Facility");AcceptanceRules.Validate(c.Content);
  if(((int)c.Markings&~7)!=0)throw new CommandRejectedException("invalid_markings","Select actual supported handling markings.");
  var spec=new CommandSpec("Quality",nameof(DefineAcceptanceCriteria),Permissions.QualityPlan,new("AcceptanceCriteriaRevision",code,c.ProgramId is{} p?new ProgramId(p):null,c.Markings,FacilityId:facility),ReasonRequired:true);
  return pipeline.ExecuteAsync(spec,c,context,async(uow,now)=>
  {
   context=context with{Principal=await CurrentPrincipalAsync(uow,context.Principal,ct)};
   var store=uow.Store<IAcceptanceStore>();await store.LockCriteriaScopeAsync(facility,BusinessCode.Require(c.Content.PartNumber,"Part"),QmsContentRules.DocumentRevision(c.Content.Revision),false,ct);
   var previous=(await store.ListCriteriaAsync(ct)).Where(r=>r.Code==code).OrderBy(r=>r.Revision).ToArray();
   if(previous.Length==0&&c.ExpectedVersion is not null||previous.Length>0&&c.ExpectedVersion!=previous[^1].Version)throw new CommandRejectedException("stale_version","Read the current criterion revision before proposing its successor.");
   if(previous.Any(r=>r.State is AcceptanceCriteriaState.Draft or AcceptanceCriteriaState.Submitted or AcceptanceCriteriaState.Approved))throw new CommandRejectedException("open_revision","Complete or withdraw the existing proposal.");
   if(previous.Length>0&&(previous[0].FacilityCode!=facility||previous[0].ProgramId!=c.ProgramId||previous[0].Markings!=c.Markings))throw new CommandRejectedException("immutable_scope","Criteria preserve their actual facility/program/handling scope.");
   if(previous.Length>0&&(BusinessCode.Require(previous[^1].Content.PartNumber,"Part")!=BusinessCode.Require(c.Content.PartNumber,"Part")||QmsContentRules.DocumentRevision(previous[^1].Content.Revision)!=QmsContentRules.DocumentRevision(c.Content.Revision)||previous[^1].Content.OperationSequence!=c.Content.OperationSequence))throw new CommandRejectedException("immutable_scope","A criterion code retains its exact product/configuration/operation scope; another source uses a separate approved code.");
   var number=previous.Length==0?1:checked(previous[^1].Revision+1);
   var next=new AcceptanceCriteriaRevision(code,number,1,AcceptanceCriteriaState.Draft,facility,c.ProgramId,c.Markings,c.Content,context.Actor,context.Actor,null,null,null,[],[]);
   Permit(context.Principal,Permissions.QualityPlan,Resource(next));
   next=next with{Sources=await SourcesAsync(uow,context.Principal,next,false,true,now,ct),History=[new("Create",context.Actor,now,"Explicit acceptance arrangement proposed",context.Reason!,null,QmsState.Draft,1)]};
   await store.InsertCriteriaAsync(next,ct);return Outcome(next,null,"Create",context);
  },ct,async(uow,now)=>
  {
   context=context with{Principal=await CurrentPrincipalAsync(uow,context.Principal,ct)};
   await uow.Store<IAcceptanceStore>().LockCriteriaScopeAsync(facility,BusinessCode.Require(c.Content.PartNumber,"Part"),QmsContentRules.DocumentRevision(c.Content.Revision),false,ct);
   var current=(await uow.Store<IAcceptanceLookup>().ListCriteriaAsync(ct)).Where(r=>r.Code==code).OrderBy(r=>r.Revision).LastOrDefault();
   if(current is not null){Permit(context.Principal,Permissions.QualityPlan,Resource(current));await SourcesAsync(uow,context.Principal,current,false,true,now,ct);}
   var candidate=new AcceptanceCriteriaRevision(code,current?.Revision??1,1,AcceptanceCriteriaState.Draft,facility,c.ProgramId,c.Markings,c.Content,context.Actor,context.Actor,null,null,null,[],[]);
   Permit(context.Principal,Permissions.QualityPlan,Resource(candidate));
   await SourcesAsync(uow,context.Principal,candidate,false,true,now,ct);return new CommandAuthority(current is null?Resource(candidate):Resource(current),code);
  });
 }
 public Task<CommandResult> EditAsync(EditAcceptanceCriteria c,CommandContext context,CancellationToken ct)=>ChangeAsync(nameof(EditAcceptanceCriteria),c,c.Code,c.Revision,c.ExpectedVersion,Permissions.QualityPlan,context,async(uow,old,now)=>
 {
  if(old.State!=AcceptanceCriteriaState.Draft)throw new CommandRejectedException("immutable_revision","Submitted acceptance criteria cannot be edited.");AcceptanceRules.Validate(c.Content);
  if(BusinessCode.Require(c.Content.PartNumber,"Part")!=BusinessCode.Require(old.Content.PartNumber,"Part")||QmsContentRules.DocumentRevision(c.Content.Revision)!=QmsContentRules.DocumentRevision(old.Content.Revision))throw new CommandRejectedException("immutable_scope","A new product/configuration uses a separately governed criterion code.");
  var next=old with{Version=old.Version+1,Content=c.Content,LastEditedBy=context.Actor};return next with{Sources=await SourcesAsync(uow,context.Principal,next,false,true,now,ct),History=Append(next,"Edit",context,"Draft criteria revised",now)};
 },ct);
 public Task<CommandResult> TransitionAsync(TransitionAcceptanceCriteria c,CommandContext context,CancellationToken ct)
 {
  var permission=c.Action is"Approve" or"Effective" or"Withdraw"?Permissions.QualityApprove:Permissions.QualityPlan;
  return ChangeAsync("AcceptanceCriteria"+c.Action,c,c.Code,c.Revision,c.ExpectedVersion,permission,context,async(uow,old,now)=>
  {
   if(string.IsNullOrWhiteSpace(c.Note)||c.Note.Length>4000)throw new CommandRejectedException("evidence_basis","Record the actual approval/applicability basis.");
   var state=c.Action switch{"Submit" when old.State==AcceptanceCriteriaState.Draft=>AcceptanceCriteriaState.Submitted,"Approve" when old.State==AcceptanceCriteriaState.Submitted=>AcceptanceCriteriaState.Approved,"Effective" when old.State==AcceptanceCriteriaState.Approved=>AcceptanceCriteriaState.Effective,"Withdraw" when old.State is not(AcceptanceCriteriaState.Withdrawn or AcceptanceCriteriaState.Superseded)=>AcceptanceCriteriaState.Withdrawn,_=>throw new CommandRejectedException("invalid_status","Follow the controlled criteria lifecycle.")};
   if(c.Action=="Approve"&&(old.CreatedBy==context.Actor||old.LastEditedBy==context.Actor||old.History.Any(h=>h.Actor==context.Actor&&h.Action is"Create" or"Edit")))throw new CommandRejectedException("separation_of_duties","An acceptance arrangement author cannot approve their own criterion or sampling coverage.");
   await SourcesAsync(uow,context.Principal,old,c.Action!="Withdraw",true,now,ct);
   if(c.Action=="Effective")foreach(var prior in(await uow.Store<IAcceptanceLookup>().ListCriteriaAsync(ct)).Where(r=>r.Code==old.Code&&r.State==AcceptanceCriteriaState.Effective))
   {
    var closed=prior with{Version=prior.Version+1,State=AcceptanceCriteriaState.Superseded,EffectiveTo=now};closed=closed with{History=Append(closed,"Supersede",context,"Exact successor revision "+old.Revision.ToString(CultureInfo.InvariantCulture),now)};
    await uow.Store<IAcceptanceStore>().UpdateCriteriaAsync(closed,prior.Version,ct);
    await uow.Outbox.EnqueueAsync(pipeline.Event(new AcceptanceChanged("AcceptanceCriteriaRevision",Key(prior),"Supersede"),context,"Quality","AcceptanceCriteriaRevision",Key(prior),Program(prior.ProgramId),prior.Markings),ct);
   }
   var next=old with{Version=old.Version+1,State=state,ApprovedBy=c.Action=="Approve"?context.Actor:old.ApprovedBy,EffectiveFrom=c.Action=="Effective"?now:old.EffectiveFrom,EffectiveTo=c.Action=="Withdraw"&&old.EffectiveFrom is not null?now:old.EffectiveTo};
   return next with{History=Append(next,c.Action,context,c.Note,now)};
  },ct);
 }
 private Task<CommandResult> ChangeAsync<T>(string name,T command,string rawCode,int revision,int expected,string permission,CommandContext context,Func<IKernelUnitOfWork,AcceptanceCriteriaRevision,DateTimeOffset,Task<AcceptanceCriteriaRevision>> change,CancellationToken ct)
 {
  var code=BusinessCode.Require(rawCode,"Criteria");return pipeline.ExecuteAsync(new("Quality",name,permission,ResourceRef.Of("AcceptanceCriteriaRevision",code+"/"+revision),ReasonRequired:true),command,context,async(uow,now)=>
  {
   context=context with{Principal=await CurrentPrincipalAsync(uow,context.Principal,ct)};
   var store=uow.Store<IAcceptanceStore>();var old=await store.FindCriteriaAsync(code,revision,ct)??throw Missing();await store.LockCriteriaScopeAsync(old.FacilityCode,BusinessCode.Require(old.Content.PartNumber,"Part"),QmsContentRules.DocumentRevision(old.Content.Revision),false,ct);
   old=await store.FindCriteriaAsync(code,revision,ct)??throw Missing();if(old.Version!=expected)throw new CommandRejectedException("stale_version","The actual criteria changed.");Permit(context.Principal,permission,Resource(old));
   await SourcesAsync(uow,context.Principal,old,false,true,now,ct);var next=await change(uow,old,now);await store.UpdateCriteriaAsync(next,old.Version,ct);return Outcome(next,old,name,context);
  },ct,async(uow,now)=>
  {
   context=context with{Principal=await CurrentPrincipalAsync(uow,context.Principal,ct)};
   var old=await uow.Store<IAcceptanceLookup>().FindCriteriaAsync(code,revision,ct)??throw Missing();await uow.Store<IAcceptanceStore>().LockCriteriaScopeAsync(old.FacilityCode,BusinessCode.Require(old.Content.PartNumber,"Part"),QmsContentRules.DocumentRevision(old.Content.Revision),false,ct);
   Permit(context.Principal,permission,Resource(old));
   await SourcesAsync(uow,context.Principal,old,false,true,now,ct);return new CommandAuthority(Resource(old),code);
  });
 }
 public async Task<IReadOnlyList<AcceptanceCriteriaRevision>> RequiredAsync(IKernelUnitOfWork uow,ForgePrincipal principal,InspectionSubject subject,DateTimeOffset now,CancellationToken ct)
 {
  var store=uow.Store<IAcceptanceStore>();await store.LockCriteriaScopeAsync(subject.Source.Context.FacilityCode,subject.Key.PartNumber,subject.Revision,true,ct);
  bool Applies(AcceptanceCriteriaRevision r)=>r.FacilityCode==subject.Source.Context.FacilityCode&&(r.ProgramId is null||r.ProgramId==subject.Source.Context.ProgramId)&&BusinessCode.Require(r.Content.PartNumber,"Part")==subject.Key.PartNumber&&QmsContentRules.DocumentRevision(r.Content.Revision)==subject.Revision&&(r.Content.OperationSequence is null||subject.OperationSequences.Contains(r.Content.OperationSequence.Value));
  var recorded=await store.ListCriteriaAsync(ct);
  if(recorded.Any(r=>r.EffectiveFrom is not null&&r.FacilityCode==subject.Source.Context.FacilityCode&&BusinessCode.Require(r.Content.PartNumber,"Part")==subject.Key.PartNumber&&QmsContentRules.DocumentRevision(r.Content.Revision)==subject.Revision&&(r.ProgramId is null||subject.Source.Context.ProgramId is null||r.ProgramId==subject.Source.Context.ProgramId)&&(r.Content.OperationSequence is null||subject.OperationSequences.Count==0||subject.OperationSequences.Contains(r.Content.OperationSequence.Value))&&((r.ProgramId is not null&&subject.Source.Context.ProgramId is null)||(r.Content.OperationSequence is not null&&subject.OperationSequences.Count==0))))throw new CommandRejectedException("acceptance_scope_unknown","An unknown/mixed actual source could match a historically effective narrow acceptance arrangement.");
  var history=recorded.Where(r=>r.EffectiveFrom is not null&&Applies(r)).ToArray();var current=history.Where(r=>r.State==AcceptanceCriteriaState.Effective&&r.EffectiveFrom<=now&&(r.EffectiveTo is null||r.EffectiveTo>now)).ToArray();
  if(current.Length==0||history.Select(r=>r.Code).Distinct(StringComparer.Ordinal).Any(code=>!current.Any(r=>r.Code==code)))throw new CommandRejectedException("acceptance_arrangement_unavailable","Every required exact acceptance arrangement needs a current effective successor.");
  foreach(var revision in current){Permit(principal,Permissions.QualityRead,Resource(revision));await SourcesAsync(uow,principal,revision,true,true,now,ct);}return current;
 }
 internal async Task<IReadOnlyList<QmsSourceReference>> SourcesAsync(IKernelUnitOfWork uow,ForgePrincipal principal,AcceptanceCriteriaRevision revision,bool effective,bool locked,DateTimeOffset now,CancellationToken ct)
 {
  AcceptanceRules.Validate(revision.Content);if(!(await uow.Store<IResourcesLookup>().ListSitesAsync(ct)).Any(s=>s.Code==revision.FacilityCode))throw Missing();
  if(revision.ProgramId is{} program&&await uow.Store<Forge.Programs.Contracts.IProgramsLookup>().FindProgramAsync(program,ct) is null)throw Missing();
  var engineering=uow.Store<IEngineeringLookup>();var part=BusinessCode.Require(revision.Content.PartNumber,"Part");var configuration=QmsContentRules.DocumentRevision(revision.Content.Revision);
  if(await engineering.FindItemAsync(part,ct) is null||!(await engineering.ListRevisionsAsync(part,ct)).Any(r=>r.Revision==configuration))throw Missing();
  if(revision.Content.OperationSequence is{} sequence&&(await engineering.GetRevisionDetailAsync(part,configuration,ct))?.Routing.Any(r=>r.Sequence==sequence)!=true)throw Missing();
  var result=new List<QmsSourceReference>();foreach(var reference in revision.Content.Documents)
  {
   var number=QmsContentRules.DocumentNumber(reference.DocumentNumber);var numberRevision=QmsContentRules.DocumentRevision(reference.Revision);
   var recorded=revision.Sources.FirstOrDefault(s=>s.Key==number&&s.Revision==numberRevision);if(recorded is not null)Permit(principal,Permissions.EngineeringRead,new("DocumentRevision",number+"/"+numberRevision,Program(recorded.ProgramId),recorded.Markings));
   if(locked)await uow.Store<IQmsStore>().LockSourceAsync("DocumentRevision",number,numberRevision,ct);
   var document=await uow.Store<IDocumentLookup>().FindDocumentAsync(number,ct)??throw Missing();Permit(principal,Permissions.EngineeringRead,new("DocumentRevision",number+"/"+numberRevision,Program(document.ProgramId),document.Markings));
   var selected=document.Revisions.FirstOrDefault(r=>r.Revision==numberRevision)??throw Missing();var day=DateOnly.FromDateTime(now.UtcDateTime);
   if(effective&&(document.Authority==DocumentAuthority.Reference||selected.Status is not(DocumentRevisionStatus.Released or DocumentRevisionStatus.Applicable)||selected.EffectiveFrom is null||selected.EffectiveFrom>day||selected.EffectiveTo<=day))throw new CommandRejectedException("criteria_source_unavailable","The exact controlled acceptance source is no longer effective.");
   result.Add(new("DocumentRevision",number,numberRevision,document.ProgramId,document.Markings,null));
  }
  return result;
 }
 public static ResourceRef Resource(AcceptanceCriteriaRevision r)=>new("AcceptanceCriteriaRevision",Key(r),Program(r.ProgramId),r.Sources.Aggregate(r.Markings,(m,s)=>m|s.Markings),FacilityId:r.FacilityCode);
 private CommandOutcome Outcome(AcceptanceCriteriaRevision next,AcceptanceCriteriaRevision? old,string action,CommandContext context)=>new(CommandResult.Ok(Key(next),$"Acceptance arrangement {Key(next)}: {next.State}."),[pipeline.Event(new AcceptanceChanged("AcceptanceCriteriaRevision",Key(next),action),context,"Quality","AcceptanceCriteriaRevision",Key(next),Program(next.ProgramId),Resource(next).Markings)],new AuditChange("AcceptanceCriteriaRevision",Key(next),old is null?null:CanonicalJson.Serialize(old),CanonicalJson.Serialize(next),Program(next.ProgramId),Resource(next).Markings));
 private static IReadOnlyList<QmsTransition> Append(AcceptanceCriteriaRevision revision,string action,CommandContext context,string note,DateTimeOffset now)=>revision.History.Append(new(action,context.Actor,now,note,context.Reason!,null,Enum.Parse<QmsState>(revision.State.ToString()),revision.Version)).ToArray();
 private void Permit(ForgePrincipal p,string permission,ResourceRef resource){var decision=authorizer.Authorize(p,permission,resource);if(!decision.IsPermitted)throw new ForgeAuthorizationException(decision,resource);}
 private static ProgramId? Program(Guid? value)=>value is{} id?new ProgramId(id):null;
 private static string Key(AcceptanceCriteriaRevision r)=>r.Code+"/"+r.Revision.ToString(CultureInfo.InvariantCulture);
 private static CommandRejectedException Missing()=>new("acceptance_source_unavailable","The exact acceptance source is unavailable.");
 private static async Task<ForgePrincipal> CurrentPrincipalAsync(IKernelUnitOfWork uow,ForgePrincipal p,CancellationToken ct)=>await uow.People.LoadPrincipalAsync(p.PersonId,ct)??p with{IsActive=false};
}
