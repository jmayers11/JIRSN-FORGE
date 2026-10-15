using System.Globalization;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;
using Forge.Quality.Qms;

namespace Forge.Quality.Investigations;

public sealed record CreateMeasurementFinding(MeasurementFindingContent Content);
public sealed record EditMeasurementFinding(long FindingId,int ExpectedVersion,MeasurementFindingContent Content);
public sealed record TransitionMeasurementFinding(long FindingId,int ExpectedVersion,string Action);
public sealed record OpenTraceInvestigation(TraceInvestigationRoot Root,string Title,Guid OwnerPersonId,string TriggerEvidence);
public sealed record RefreshTraceInvestigation(long InvestigationId,int ExpectedVersion);
public sealed record ProposeTraceAssessment(long InvestigationId,int ExpectedVersion,int SnapshotNumber,string CandidateKey,TraceImpactDecision Decision,string Basis,string Evidence,string PlannedDisposition);
public sealed record TransitionTraceAssessment(long AssessmentId,int ExpectedVersion,string Action);
public sealed record LinkInvestigationHold(long InvestigationId,int ExpectedVersion,long AssessmentId,int ExpectedAssessmentVersion,long HoldId,int ExpectedHoldVersion,string Basis);
public sealed record CloseTraceInvestigation(long InvestigationId,int ExpectedVersion,bool AcknowledgedGaps,string Basis);
public sealed record InvestigationChanged(string RecordType,long RecordId,string Action,int Version):IDomainEvent
{public static string EventType=>nameof(InvestigationChanged);public static int SchemaVersion=>1;}

public sealed class InvestigationService(CommandPipeline pipeline,IKernelUnitOfWorkFactory factory,ITraceInvestigationResolver resolver,
 IForgeAuthorizer authorizer,QmsOptions options)
{
 public Task<CommandResult> CreateFindingAsync(CreateMeasurementFinding command,CommandContext context,CancellationToken ct)=>pipeline.ExecuteAsync(
  new("Quality",nameof(CreateMeasurementFinding),Permissions.QualityPlan,ResourceRef.Of("MeasurementFinding",command.Content.ToolCode),ReasonRequired:true),command,context,async(uow,now)=>
  {
   Configure();context=context with{Principal=await CurrentAsync(uow,context.Principal,ct)};InvestigationRules.Finding(command.Content,now);
   var sources=await resolver.FindingSourcesAsync(uow,context.Principal,command.Content,ct);Permit(context.Principal,Permissions.QualityPlan,sources,"MeasurementFinding",null);
   var record=new MeasurementResourceFinding(0,1,MeasurementFindingState.Draft,options.OrganizationCode!,command.Content with{ToolCode=BusinessCode.Require(command.Content.ToolCode,"Tool")},context.Actor,null,sources.SelectMany(source=>source.Resources.Select(resource=>new QmsPolicyReadSource(source.ReadPermission,resource))).ToArray(),[new("Create",context.Actor,now,context.Reason!,1)]);
   var id=await uow.Store<IQualityInvestigationStore>().InsertFindingAsync(record,ct);return Outcome("MeasurementFinding",id,"Create",1,record with{FindingId=id},sources,context);
  },ct,async(uow,now)=>{Configure();context=context with{Principal=await CurrentAsync(uow,context.Principal,ct)};InvestigationRules.Finding(command.Content,now);var sources=await resolver.FindingSourcesAsync(uow,context.Principal,command.Content,ct);Permit(context.Principal,Permissions.QualityPlan,sources,"MeasurementFinding",null);return Authority(sources,"MeasurementFinding",null,CanonicalJson.Fingerprint(command.Content));});
 public Task<CommandResult> EditFindingAsync(EditMeasurementFinding command,CommandContext context,CancellationToken ct)=>FindingCommandAsync(nameof(EditMeasurementFinding),command.FindingId,command,Permissions.QualityPlan,context,async(uow,record,sources,now)=>
 {
  context=context with{Principal=await CurrentAsync(uow,context.Principal,ct)};Version(record.Version,command.ExpectedVersion);if(record.State!=MeasurementFindingState.Draft)throw Reject("finding_immutable","Submitted observations/interval/source evidence are immutable.");
  InvestigationRules.Finding(command.Content,now);if(BusinessCode.Require(command.Content.ToolCode,"Tool")!=record.Content.ToolCode)throw Reject("finding_identity","Create a separate finding for another actual tool.");
  var current=await resolver.FindingSourcesAsync(uow,context.Principal,command.Content,ct);
  var next=record with{Version=record.Version+1,Content=command.Content with{ToolCode=record.Content.ToolCode},Sources=current.SelectMany(source=>source.Resources.Select(resource=>new QmsPolicyReadSource(source.ReadPermission,resource))).ToArray(),History=record.History.Append(new("Edit",context.Actor,now,context.Reason!,record.Version+1)).ToArray()};
  await uow.Store<IQualityInvestigationStore>().UpdateFindingAsync(next,record.Version,ct);return Outcome("MeasurementFinding",record.FindingId,"Edit",next.Version,next,current,context);
 },command.Content,ct);
 public Task<CommandResult> TransitionFindingAsync(TransitionMeasurementFinding command,CommandContext context,CancellationToken ct)
 {
  var action=BusinessCode.Require(command.Action,"Action");var permission=action=="APPROVE"?Permissions.QualityApprove:action=="WITHDRAW"?Permissions.QualityDisposition:Permissions.QualityPlan;
  return FindingCommandAsync(nameof(TransitionMeasurementFinding),command.FindingId,command,permission,context,async(uow,record,sources,now)=>
  {
   context=context with{Principal=await CurrentAsync(uow,context.Principal,ct)};Version(record.Version,command.ExpectedVersion);var state=action switch{"SUBMIT" when record.State==MeasurementFindingState.Draft=>MeasurementFindingState.Submitted,"APPROVE" when record.State==MeasurementFindingState.Submitted=>MeasurementFindingState.Approved,"WITHDRAW" when record.State!=MeasurementFindingState.Withdrawn=>MeasurementFindingState.Withdrawn,_=>throw Reject("finding_transition","Use the governed finding review lifecycle.")};
   if(state==MeasurementFindingState.Approved&&record.History.Any(change=>(change.Action is "Create" or "Edit")&&change.Actor==context.Actor))throw Reject("separation_of_duties","An observation/interval author cannot approve their own measurement finding.");
   var next=record with{Version=record.Version+1,State=state,ApprovedBy=state==MeasurementFindingState.Approved?context.Actor:record.ApprovedBy,History=record.History.Append(new(action,context.Actor,now,context.Reason!,record.Version+1)).ToArray()};await uow.Store<IQualityInvestigationStore>().UpdateFindingAsync(next,record.Version,ct);return Outcome("MeasurementFinding",record.FindingId,action,next.Version,next,sources,context);
  },null,ct);
 }
 private Task<CommandResult> FindingCommandAsync<T>(string type,long id,T command,string permission,CommandContext context,
  Func<IKernelUnitOfWork,MeasurementResourceFinding,IReadOnlyList<TraceSourceReference>,DateTimeOffset,Task<CommandOutcome>> body,MeasurementFindingContent? changed,CancellationToken ct)=>pipeline.ExecuteAsync(
  new("Quality",type,permission,ResourceRef.Of("MeasurementFinding",Text(id)),ReasonRequired:true),command,context,async(uow,now)=>
  {Configure();context=context with{Principal=await CurrentAsync(uow,context.Principal,ct)};await uow.Store<IQualityInvestigationStore>().LockFindingAsync(id,ct);var record=await FindingAsync(uow,id,ct);if(record.OrganizationCode!=options.OrganizationCode)throw Missing();var sources=await FindingReadAsync(uow,context.Principal,record,ct);Permit(context.Principal,permission,sources,"MeasurementFinding",id);return await body(uow,record,sources,now);},ct,async(uow,now)=>
  {Configure();context=context with{Principal=await CurrentAsync(uow,context.Principal,ct)};var record=await FindingAsync(uow,id,ct);if(record.OrganizationCode!=options.OrganizationCode)throw Missing();var sources=await FindingReadAsync(uow,context.Principal,record,ct);if(changed is not null){InvestigationRules.Finding(changed,now);sources=sources.Concat(await resolver.FindingSourcesAsync(uow,context.Principal,changed,ct)).Distinct().ToArray();}Permit(context.Principal,permission,sources,"MeasurementFinding",id);return Authority(sources,"MeasurementFinding",id,Text(id));});
 public Task<CommandResult> OpenAsync(OpenTraceInvestigation command,CommandContext context,CancellationToken ct)=>pipeline.ExecuteAsync(
  new("Quality",nameof(OpenTraceInvestigation),Permissions.QualityDisposition,ResourceRef.Of("TraceInvestigation"),ReasonRequired:true),command,context,async(uow,now)=>
  {
   Configure();InvestigationRules.Root(command.Root);InvestigationRules.Text(command.Title);InvestigationRules.Text(command.TriggerEvidence);context=context with{Principal=await CurrentAsync(uow,context.Principal,ct)};
   var owner=await uow.People.FindAsync(new PersonId(command.OwnerPersonId),ct);if(owner is null||!owner.IsActive)throw Reject("investigation_owner","Assign an actual active organization person.");
   var roots=await resolver.RootSourcesAsync(uow,context.Principal,command.Root,ct);Permit(context.Principal,Permissions.QualityDisposition,roots,"TraceInvestigation",null);
   var capture=await CaptureAsync(uow,context.Principal,command.Root,ct);
   var record=new TraceabilityInvestigation(0,1,TraceInvestigationState.Open,command.Root,options.OrganizationCode!,command.Title,command.OwnerPersonId,context.Actor,now,command.TriggerEvidence,1,roots,[new("Open",context.Actor,now,context.Reason!,1)]);
   var id=await uow.Store<IQualityInvestigationStore>().InsertInvestigationAsync(record,ct);await uow.Store<IQualityInvestigationStore>().InsertSnapshotAsync(new(id,1,context.Actor,now,capture),ct);return Outcome("TraceInvestigation",id,"Open",1,record with{InvestigationId=id},roots,context);
  },ct,async(uow,_)=>{Configure();context=context with{Principal=await CurrentAsync(uow,context.Principal,ct)};InvestigationRules.Root(command.Root);var roots=await resolver.RootSourcesAsync(uow,context.Principal,command.Root,ct);Permit(context.Principal,Permissions.QualityDisposition,roots,"TraceInvestigation",null);return Authority(roots,"TraceInvestigation",null,CanonicalJson.Fingerprint(command.Root));});
 public Task<CommandResult> RefreshAsync(RefreshTraceInvestigation command,CommandContext context,CancellationToken ct)=>CaseCommandAsync(nameof(RefreshTraceInvestigation),command.InvestigationId,command,Permissions.QualityDisposition,context,async(uow,record,roots,now)=>
 {
  context=context with{Principal=await CurrentAsync(uow,context.Principal,ct)};Version(record.Version,command.ExpectedVersion);Open(record);var capture=await CaptureAsync(uow,context.Principal,record.Root,ct);var next=record with{Version=record.Version+1,LatestSnapshot=record.LatestSnapshot+1,History=record.History.Append(new("Refresh",context.Actor,now,context.Reason!,record.Version+1)).ToArray()};
  await uow.Store<IQualityInvestigationStore>().InsertSnapshotAsync(new(record.InvestigationId,next.LatestSnapshot,context.Actor,now,capture),ct);await uow.Store<IQualityInvestigationStore>().UpdateInvestigationAsync(next,record.Version,ct);return Outcome("TraceInvestigation",record.InvestigationId,"Refresh",next.Version,next,roots,context);
 },null,ct);
 public Task<CommandResult> ProposeAsync(ProposeTraceAssessment command,CommandContext context,CancellationToken ct)=>CaseCommandAsync(nameof(ProposeTraceAssessment),command.InvestigationId,command,Permissions.QualityDisposition,context,async(uow,record,roots,now)=>
 {
  context=context with{Principal=await CurrentAsync(uow,context.Principal,ct)};Version(record.Version,command.ExpectedVersion);Open(record);InvestigationRules.Assessment(command.Decision,command.Basis,command.Evidence,command.PlannedDisposition);var candidate=await CandidateAsync(uow,context.Principal,record.InvestigationId,command.SnapshotNumber,command.CandidateKey,ct);
  await resolver.RequireCandidateActionAsync(uow,context.Principal,candidate,Permissions.QualityDisposition,ct);
  if((await uow.Store<IQualityInvestigationLookup>().AssessmentsAsync(record.InvestigationId,command.SnapshotNumber,ct)).Any(assessment=>assessment.CandidateKey==command.CandidateKey&&assessment.State!=TraceAssessmentState.Withdrawn))throw Reject("impact_assessment_exists","This exact retained candidate already has a current proposed/approved human assessment; retain its history.");
  var assessment=new TraceCandidateAssessment(0,record.InvestigationId,command.SnapshotNumber,command.CandidateKey,1,TraceAssessmentState.Proposed,command.Decision,command.Basis,command.Evidence,command.PlannedDisposition,context.Actor,now,null,null,[new("Propose",context.Actor,now,context.Reason!,1)]);
  var id=await uow.Store<IQualityInvestigationStore>().InsertAssessmentAsync(assessment,ct);return Outcome("TraceAssessment",id,"Propose",1,assessment with{AssessmentId=id},candidate.Sources,context);
 },async(uow,principal)=>{var candidate=await CandidateAsync(uow,principal,command.InvestigationId,command.SnapshotNumber,command.CandidateKey,ct);await resolver.RequireCandidateActionAsync(uow,principal,candidate,Permissions.QualityDisposition,ct);},ct);
 public Task<CommandResult> TransitionAssessmentAsync(TransitionTraceAssessment command,CommandContext context,CancellationToken ct)
 {
  var action=BusinessCode.Require(command.Action,"Action");var permission=action=="APPROVE"?Permissions.QualityApprove:Permissions.QualityDisposition;
  return pipeline.ExecuteAsync(new("Quality",nameof(TransitionTraceAssessment),permission,ResourceRef.Of("TraceAssessment",Text(command.AssessmentId)),ReasonRequired:true),command,context,async(uow,now)=>
  {
   Configure();context=context with{Principal=await CurrentAsync(uow,context.Principal,ct)};var assessment=await uow.Store<IQualityInvestigationLookup>().FindAssessmentAsync(command.AssessmentId,ct)??throw Missing();await uow.Store<IQualityInvestigationStore>().LockInvestigationAsync(assessment.InvestigationId,ct);var record=await CaseAsync(uow,assessment.InvestigationId,ct);await CaseReadAsync(uow,context.Principal,record,ct);Open(record);assessment=await uow.Store<IQualityInvestigationLookup>().FindAssessmentAsync(command.AssessmentId,ct)??throw Missing();Version(assessment.Version,command.ExpectedVersion);
   var candidate=await CandidateAsync(uow,context.Principal,assessment.InvestigationId,assessment.SnapshotNumber,assessment.CandidateKey,ct);await resolver.RequireCandidateActionAsync(uow,context.Principal,candidate,permission,ct);
   if(assessment.State!=TraceAssessmentState.Proposed||action is not("APPROVE" or "WITHDRAW"))throw Reject("assessment_transition","Only an unchanged proposed impact assessment can be independently approved or withdrawn.");
   if(action=="APPROVE"&&assessment.ProposedBy==context.Actor)throw Reject("separation_of_duties","The human impact proposer cannot approve their own decision.");
   var next=assessment with{Version=assessment.Version+1,State=action=="APPROVE"?TraceAssessmentState.Approved:TraceAssessmentState.Withdrawn,ApprovedBy=action=="APPROVE"?context.Actor:null,ApprovedAt=action=="APPROVE"?now:null,History=assessment.History.Append(new(action,context.Actor,now,context.Reason!,assessment.Version+1)).ToArray()};await uow.Store<IQualityInvestigationStore>().UpdateAssessmentAsync(next,assessment.Version,ct);return Outcome("TraceAssessment",next.AssessmentId,action,next.Version,next,candidate.Sources,context);
  },ct,async(uow,_)=>{Configure();context=context with{Principal=await CurrentAsync(uow,context.Principal,ct)};var assessment=await uow.Store<IQualityInvestigationLookup>().FindAssessmentAsync(command.AssessmentId,ct)??throw Missing();var record=await CaseAsync(uow,assessment.InvestigationId,ct);await CaseReadAsync(uow,context.Principal,record,ct);var candidate=await CandidateAsync(uow,context.Principal,record.InvestigationId,assessment.SnapshotNumber,assessment.CandidateKey,ct);await resolver.RequireCandidateActionAsync(uow,context.Principal,candidate,permission,ct);return Authority(candidate.Sources,"TraceAssessment",command.AssessmentId,Text(command.AssessmentId));});
 }
 public Task<CommandResult> LinkHoldAsync(LinkInvestigationHold command,CommandContext context,CancellationToken ct)=>CaseCommandAsync(nameof(LinkInvestigationHold),command.InvestigationId,command,Permissions.QualityDisposition,context,async(uow,record,roots,now)=>
 {
  context=context with{Principal=await CurrentAsync(uow,context.Principal,ct)};Version(record.Version,command.ExpectedVersion);Open(record);InvestigationRules.Text(command.Basis);var assessment=await uow.Store<IQualityInvestigationLookup>().FindAssessmentAsync(command.AssessmentId,ct)??throw Missing();Version(assessment.Version,command.ExpectedAssessmentVersion);
  if(assessment.InvestigationId!=record.InvestigationId||assessment.State!=TraceAssessmentState.Approved||assessment.Decision==TraceImpactDecision.NotAffected)throw Reject("approved_impact_required","Link actual containment to an independently approved potentially affected or undetermined candidate.");
  var candidate=await CandidateAsync(uow,context.Principal,record.InvestigationId,assessment.SnapshotNumber,assessment.CandidateKey,ct);await resolver.RequireCandidateActionAsync(uow,context.Principal,candidate,Permissions.HoldsPlace,ct);var hold=await resolver.RequireMatchingHoldAsync(uow,context.Principal,candidate,command.HoldId,command.ExpectedHoldVersion,ct);
  if((await uow.Store<IQualityInvestigationLookup>().HoldLinksAsync(record.InvestigationId,ct)).Any(link=>link.SnapshotNumber==assessment.SnapshotNumber&&link.CandidateKey==assessment.CandidateKey&&link.HoldId==hold.HoldId))throw Reject("hold_already_linked","The exact hold/candidate linkage is already retained.");
  var link=new InvestigationHoldLink(record.InvestigationId,assessment.SnapshotNumber,assessment.CandidateKey,assessment.AssessmentId,hold.HoldId,command.ExpectedHoldVersion,context.Actor,now,command.Basis);await uow.Store<IQualityInvestigationStore>().InsertHoldLinkAsync(link,ct);return Outcome("TraceInvestigation",record.InvestigationId,"LinkHold",record.Version,link,roots,context);
 },async(uow,principal)=>{var assessment=await uow.Store<IQualityInvestigationLookup>().FindAssessmentAsync(command.AssessmentId,ct)??throw Missing();var candidate=await CandidateAsync(uow,principal,command.InvestigationId,assessment.SnapshotNumber,assessment.CandidateKey,ct);await resolver.RequireCandidateActionAsync(uow,principal,candidate,Permissions.HoldsPlace,ct);await resolver.RequireMatchingHoldAsync(uow,principal,candidate,command.HoldId,command.ExpectedHoldVersion,ct);},ct);
 public Task<CommandResult> CloseAsync(CloseTraceInvestigation command,CommandContext context,CancellationToken ct)=>CaseCommandAsync(nameof(CloseTraceInvestigation),command.InvestigationId,command,Permissions.QualityApprove,context,async(uow,record,roots,now)=>
 {
  context=context with{Principal=await CurrentAsync(uow,context.Principal,ct)};Version(record.Version,command.ExpectedVersion);Open(record);InvestigationRules.Text(command.Basis);var snapshot=await uow.Store<IQualityInvestigationLookup>().FindSnapshotAsync(record.InvestigationId,record.LatestSnapshot,ct)??throw Missing();
  if(!command.AcknowledgedGaps&&!snapshot.Capture.Completeness.RecordedTraversalComplete||!command.AcknowledgedGaps&&snapshot.Capture.Completeness.Gaps.Count>0)throw Reject("trace_gaps_unacknowledged","A human closure must explicitly acknowledge recorded unknown, partial or restricted coverage.");
  var assessments=await uow.Store<IQualityInvestigationLookup>().AssessmentsAsync(record.InvestigationId,record.LatestSnapshot,ct);
  foreach(var candidate in snapshot.Capture.Candidates){await resolver.RequireCurrentReadsAsync(uow,context.Principal,candidate.Sources,ct);if(!assessments.Any(a=>a.CandidateKey==candidate.Key&&a.State==TraceAssessmentState.Approved))throw Reject("impact_review_incomplete","Each recorded candidate needs an independently approved human impact decision; closure does not release its holds.");}
  var next=record with{Version=record.Version+1,State=TraceInvestigationState.Closed,History=record.History.Append(new("Close",context.Actor,now,command.Basis+" | "+context.Reason,record.Version+1)).ToArray()};await uow.Store<IQualityInvestigationStore>().UpdateInvestigationAsync(next,record.Version,ct);return Outcome("TraceInvestigation",record.InvestigationId,"Close",next.Version,next,roots,context);
 },null,ct);
 private Task<CommandResult> CaseCommandAsync<T>(string type,long id,T command,string permission,CommandContext context,Func<IKernelUnitOfWork,TraceabilityInvestigation,IReadOnlyList<TraceSourceReference>,DateTimeOffset,Task<CommandOutcome>> body,Func<IKernelUnitOfWork,ForgePrincipal,Task>? specific,CancellationToken ct)=>pipeline.ExecuteAsync(
  new("Quality",type,permission,ResourceRef.Of("TraceInvestigation",Text(id)),ReasonRequired:true),command,context,async(uow,now)=>{Configure();context=context with{Principal=await CurrentAsync(uow,context.Principal,ct)};await uow.Store<IQualityInvestigationStore>().LockInvestigationAsync(id,ct);var record=await CaseAsync(uow,id,ct);if(record.OrganizationCode!=options.OrganizationCode)throw Missing();var roots=await CaseReadAsync(uow,context.Principal,record,ct);Permit(context.Principal,permission,roots,"TraceInvestigation",id);return await body(uow,record,roots,now);},ct,async(uow,_)=>{Configure();context=context with{Principal=await CurrentAsync(uow,context.Principal,ct)};var record=await CaseAsync(uow,id,ct);if(record.OrganizationCode!=options.OrganizationCode)throw Missing();var roots=await CaseReadAsync(uow,context.Principal,record,ct);Permit(context.Principal,permission,roots,"TraceInvestigation",id);if(specific is not null)await specific(uow,context.Principal);return Authority(roots,"TraceInvestigation",id,Text(id));});
 private async Task<IReadOnlyList<TraceSourceReference>> FindingReadAsync(IKernelUnitOfWork uow,ForgePrincipal principal,MeasurementResourceFinding finding,CancellationToken ct)
 {foreach(var source in finding.Sources)PermitOne(principal,source.Permission,source.Resource);var current=await resolver.FindingSourcesAsync(uow,principal,finding.Content,ct);return current.Concat(finding.Sources.Select(source=>new TraceSourceReference(source.Resource.Type,source.Resource.Id??throw Missing(),source.Permission,[source.Resource]))).Distinct().ToArray();}
 private async Task<TraceCapture> CaptureAsync(IKernelUnitOfWork commandUow,ForgePrincipal principal,TraceInvestigationRoot root,CancellationToken ct)
 {
  // A projection gets the existing repeatable-read owner snapshot. All scopes are rechecked in the command transaction before this immutable evidence is saved.
  if(root.FindingId is{} findingId)await commandUow.Store<IQualityInvestigationStore>().LockFindingAsync(findingId,ct);
  await using var snapshot=await factory.BeginSnapshotAsync(ct);var boundary=await snapshot.Store<IQualityInvestigationLookup>().CaptureBoundaryAsync(ct);var capture=await resolver.CaptureAsync(snapshot,principal,root,boundary,ct);
  await resolver.RootSourcesAsync(commandUow,principal,root,ct);
  await resolver.RequireCurrentReadsAsync(commandUow,principal,capture.RootSources,ct);
  foreach(var candidate in capture.Candidates)await resolver.RequireCurrentReadsAsync(commandUow,principal,candidate.Sources,ct);
  foreach(var link in capture.Links)await resolver.RequireCurrentReadsAsync(commandUow,principal,link.Evidence.Sources,ct);
  return capture;
 }
 private async Task<IReadOnlyList<TraceSourceReference>> CaseReadAsync(IKernelUnitOfWork uow,ForgePrincipal principal,TraceabilityInvestigation record,CancellationToken ct)
 {await resolver.RequireCurrentReadsAsync(uow,principal,record.RootSources,ct);return await resolver.CurrentContextsAsync(uow,record.RootSources,ct);}
 private async Task<TraceCandidate> CandidateAsync(IKernelUnitOfWork uow,ForgePrincipal principal,long id,int number,string key,CancellationToken ct)
 {var snapshot=await uow.Store<IQualityInvestigationLookup>().FindSnapshotAsync(id,number,ct)??throw Missing();var candidate=snapshot.Capture.Candidates.SingleOrDefault(c=>c.Key==key)??throw Missing();await resolver.RequireCurrentReadsAsync(uow,principal,candidate.Sources,ct);return candidate;}
 private static async Task<MeasurementResourceFinding> FindingAsync(IKernelUnitOfWork uow,long id,CancellationToken ct)=>await uow.Store<IQualityInvestigationLookup>().FindFindingAsync(id,ct)??throw Missing();
 private static async Task<TraceabilityInvestigation> CaseAsync(IKernelUnitOfWork uow,long id,CancellationToken ct)=>await uow.Store<IQualityInvestigationLookup>().FindInvestigationAsync(id,ct)??throw Missing();
 private static void Open(TraceabilityInvestigation record){if(record.State!=TraceInvestigationState.Open)throw Reject("investigation_closed","The closed investigation history is retained; open a new governed investigation for later work.");}
 private static void Version(int actual,int expected){if(actual!=expected)throw Reject("stale_version","Use the actual governed record version.");}
 private void Configure(){if(!options.Configured)throw Reject("qms_not_configured","Configure the actual organization before governed investigation records.");}
 private void Permit(ForgePrincipal principal,string permission,IReadOnlyList<TraceSourceReference> sources,string type,long? id){foreach(var resource in Resources(sources,type,id))PermitOne(principal,permission,resource);}
 private void PermitOne(ForgePrincipal principal,string permission,ResourceRef resource){var decision=authorizer.Authorize(principal,permission,resource);if(!decision.IsPermitted)throw new ForgeAuthorizationException(decision,resource);}
 public static IReadOnlyList<ResourceRef> Resources(IReadOnlyList<TraceSourceReference> sources,string type,long? id)
 {var all=sources.Where(source=>source.ActionScope).SelectMany(source=>source.Resources).Select(resource=>resource with{Type=type,Id=id is{} value?Text(value):null}).Distinct().ToArray();return all.Length==0?throw Missing():all;}
 private static CommandAuthority Authority(IReadOnlyList<TraceSourceReference> sources,string type,long? id,string fingerprint){var all=Resources(sources,type,id);return new(all[0],fingerprint,all.Skip(1).ToArray());}
 private CommandOutcome Outcome<T>(string type,long id,string action,int version,T record,IReadOnlyList<TraceSourceReference> sources,CommandContext context){var all=Resources(sources,type,id);var marks=all.Aggregate(DataMarking.None,(value,resource)=>value|resource.Markings);return new(CommandResult.Ok(Text(id),$"{type} {id}: {action}."),[pipeline.Event(new InvestigationChanged(type,id,action,version),context,"Quality",type,Text(id),all[0].Program,marks)],new AuditChange(type,Text(id),null,CanonicalJson.Serialize(record),all[0].Program,marks));}
 private static async Task<ForgePrincipal> CurrentAsync(IKernelUnitOfWork uow,ForgePrincipal principal,CancellationToken ct)=>await uow.People.LoadPrincipalAsync(principal.PersonId,ct)??principal with{IsActive=false};
 private static string Text(long id)=>id.ToString(CultureInfo.InvariantCulture);
 private static CommandRejectedException Missing()=>Reject("investigation_not_found","The governed investigation source is unavailable.");
 private static CommandRejectedException Reject(string code,string message)=>new(code,message);
}
