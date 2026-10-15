using System.Globalization;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Holds;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;
using Forge.Quality.Commercial;
using Forge.Quality.Qms;
using Forge.Resources.Contracts;

namespace Forge.Quality.Acceptance;

public sealed record RecordAcceptanceInspection(AcceptanceSubjectKey Subject,string CriteriaCode,int CriteriaRevision,int ExpectedCriteriaVersion,
 decimal Quantity,decimal SampleQuantity,IReadOnlyList<AcceptanceFinding> Findings,string Evidence,string? QuarantineLocation);
public sealed record DecideProductRelease(AcceptanceSubjectKey Subject,decimal Quantity,IReadOnlyList<long> InspectionIds,int ExpectedInspectionVersion,string Basis);
public sealed record WithdrawProductRelease(long DecisionId,int ExpectedVersion,string Basis);

/// <summary>Accepted evidence and an independent release are separate immutable decisions; no stock is created here.</summary>
public sealed class ProductAcceptanceService(CommandPipeline pipeline,IKernelUnitOfWorkFactory factory,IInspectionSubjectResolver subjects,
 AcceptanceCriteriaService criteria,IQmsPolicyReader policies,QmsService qms,IForgeAuthorizer authorizer,ICommercialUseAccess commercial):IProductReleaseDecisionValidator,IAcceptanceSourceAccess
{
 private readonly QmsPolicyCurrentAccess policyAccess=new(qms,authorizer);
 public Task<CommandResult> InspectAsync(RecordAcceptanceInspection c,CommandContext context,CancellationToken ct)
 {
  return pipeline.ExecuteAsync(new("Quality",nameof(RecordAcceptanceInspection),Permissions.QualityInspect,ResourceRef.Of("AcceptanceSubject",AcceptanceSubjectReference.Format(c.Subject)),ReasonRequired:true),c,context,async(uow,now)=>
  {
   context=context with{Principal=await CurrentPrincipalAsync(uow,context.Principal,ct)};
   var subject=await subjects.ResolveAsync(uow,context.Principal,c.Subject,true,ct);var policy=await policies.RequireAsync(uow,context.Principal,QmsPolicyPurpose.ProductRelease,subject.Source.Context,now,ct);
   var design=await commercial.RequireDesignAsync(uow,context.Principal,subject.Key.PartNumber,subject.Revision,now,ct);
   PermitSubject(context.Principal,Permissions.QualityInspect,subject,"AcceptanceInspection",null);
   var arrangement=await CurrentInspectionArrangementAsync(uow,context.Principal,subject,c,now,ct);
   subject=WithDesignEvidence(WithEvidenceSources(subject,policy,[arrangement]),design);
   
   if(subject.InspectionQuantity<c.Quantity)throw new CommandRejectedException("insufficient_stock","The actual identified Inspection quantity is no longer present.");
   if(subject.Source.ProductionActors.Contains(context.Actor,StringComparer.Ordinal))throw new CommandRejectedException("separation_of_duties","A person who received/produced this identified scope cannot inspect their own output.");
   var findings=AcceptanceRules.Findings(arrangement.Content,c.Quantity,c.SampleQuantity,subject.Serialized,c.Findings);
   if(string.IsNullOrWhiteSpace(c.Evidence)||c.Evidence.Length>4000)throw new CommandRejectedException("inspection_evidence","Record the actual observation/evidence basis.");
   var evidence=new Dictionary<string,(ToolValidity Validity,long ToolUseEventId)>(StringComparer.Ordinal);
   foreach(var code in findings.Where(f=>f.Finding.GaugeCode is not null).Select(f=>BusinessCode.Require(f.Finding.GaugeCode,"Gauge")).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
   {
    var tool=await uow.Store<IToolLookup>().FindToolAsync(code,ct)??throw new CommandRejectedException("gauge_not_found","The actual measuring resource is unavailable.");
    if(!(await uow.Store<IToolLookup>().ListToolTypesAsync(ct)).Any(t=>t.Code==tool.TypeCode&&t.Kind==ToolKind.Gauge))throw new CommandRejectedException("gauge_required","A measured acceptance characteristic requires an actual Gauge-kind resource.");
    Permit(context.Principal,Permissions.ResourcesRead,ResourceRef.Of("Tool",code));
    evidence.Add(code,await ToolUse.RecordEvidenceAsync(uow,code,null,null,"Exact acceptance "+context.CorrelationId.ToString("D"),now,context.Actor,ct));
   }
   var results=findings.Select(f=>
   {
    var gauge=f.Finding.GaugeCode is{} raw?BusinessCode.Require(raw,"Gauge"):null;
    return new AcceptanceResult(f.Criterion,f.Finding.SampleNumber,f.Finding.Measured,f.Finding.Accepted,f.Pass,gauge,gauge is null?null:evidence[gauge].ToolUseEventId,gauge is null?null:evidence[gauge].Validity);
   }).ToArray();
   var passed=results.All(r=>r.Passed);long? ncr=null;long? heldTransaction=null;
   if(!passed)
   {
    if(string.IsNullOrWhiteSpace(c.QuarantineLocation))throw new CommandRejectedException("quarantine_required","Identify the actual quarantine destination for this rejected physical scope.");
    heldTransaction=await subjects.HoldRejectedAsync(uow,subject,c.Quantity,c.QuarantineLocation,context.Actor,c.Evidence,now,ct);
    // The named child/serial is held; no whole-order/parent-lot defect is silently asserted.
    ncr=await uow.Store<IQualityStore>().InsertNcrAsync(new(0,subject.Key.PartNumber,null,null,subject.Key.LotNumber,c.Quantity,"Exact acceptance failed",c.Evidence,NcrStatus.Open,null,null,context.Actor,now,null,null,null,null,null),ct);
   }
   var inspection=new AcceptanceInspection(0,1,subject,arrangement.Code,arrangement.Revision,arrangement.Version,arrangement.Content,c.Quantity,c.SampleQuantity,passed,context.Actor,now,c.Evidence,results,ncr,heldTransaction,policy.Fingerprint,policy);
   var id=await uow.Store<IAcceptanceStore>().InsertInspectionAsync(inspection,ct);inspection=inspection with{InspectionId=id};
   return Outcome("AcceptanceInspection",id,"Inspected",inspection,subject,context);
  },ct,async(uow,now)=>
  {
   context=context with{Principal=await CurrentPrincipalAsync(uow,context.Principal,ct)};
   var subject=await subjects.ResolveAsync(uow,context.Principal,c.Subject,true,ct);var policy=await policies.RequireAsync(uow,context.Principal,QmsPolicyPurpose.ProductRelease,subject.Source.Context,now,ct);
   var design=await commercial.RequireDesignAsync(uow,context.Principal,subject.Key.PartNumber,subject.Revision,now,ct);
   PermitSubject(context.Principal,Permissions.QualityInspect,subject,"AcceptanceInspection",null);
   var arrangement=await CurrentInspectionArrangementAsync(uow,context.Principal,subject,c,now,ct);subject=WithDesignEvidence(WithEvidenceSources(subject,policy,[arrangement]),design);foreach(var gauge in (c.Findings??[]).Where(f=>f.GaugeCode is not null).Select(f=>BusinessCode.Require(f.GaugeCode,"Gauge")).Distinct(StringComparer.Ordinal)){if(await uow.Store<IToolLookup>().FindToolAsync(gauge,ct) is null)throw new CommandRejectedException("gauge_not_found","The actual measuring resource is unavailable.");Permit(context.Principal,Permissions.ResourcesRead,ResourceRef.Of("Tool",gauge));}return Authority(subject,"AcceptanceInspection",null,subject.ConfigurationFingerprint);
  });
 }
 public Task<CommandResult> ReleaseAsync(DecideProductRelease c,CommandContext context,CancellationToken ct)=>pipeline.ExecuteAsync(
  new("Quality",nameof(DecideProductRelease),Permissions.QualityProductRelease,ResourceRef.Of("AcceptanceSubject",AcceptanceSubjectReference.Format(c.Subject)),ReasonRequired:true),c,context,async(uow,now)=>
  {
   context=context with{Principal=await CurrentPrincipalAsync(uow,context.Principal,ct)};
   var subject=await subjects.ResolveAsync(uow,context.Principal,c.Subject,true,ct);PermitSubject(context.Principal,Permissions.QualityProductRelease,subject,"ProductReleaseDecision",null);
   var policy=await policies.RequireAsync(uow,context.Principal,QmsPolicyPurpose.ProductRelease,subject.Source.Context,now,ct);
   var design=await commercial.RequireDesignAsync(uow,context.Principal,subject.Key.PartNumber,subject.Revision,now,ct);var store=uow.Store<IAcceptanceStore>();
   var required=await criteria.RequiredAsync(uow,context.Principal,subject,now,ct);
   subject=WithDesignEvidence(WithEvidenceSources(subject,policy,required),design);
   if(c.Quantity<=0||c.Quantity>subject.InspectionQuantity||subject.Serialized&&c.Quantity!=1||string.IsNullOrWhiteSpace(c.Basis)||c.Basis.Length>4000)throw new CommandRejectedException("release_scope","Release only the actual identified inspected quantity with a human decision basis.");
   var accepted=await CurrentReleaseEvidenceAsync(uow,context.Principal,context.Actor,c,subject,policy,required,ct);
   await RequireNoBlockersAsync(uow,subject,ct);
   var reserved=0m;foreach(var decision in await store.DecisionsForAsync(subject.Key,ct))if(await store.FindWithdrawalAsync(decision.DecisionId,ct) is null)reserved+=decision.Quantity;
   if(reserved+c.Quantity>accepted.Min(i=>i.Quantity))throw new CommandRejectedException("over_release","This exact accepted quantity already has a release decision; independent keys cannot authorize it twice.");
   var record=new ProductReleaseDecision(0,1,accepted.Select(i=>i.InspectionId).Order().ToArray(),subject.Key,subject.Revision,c.Quantity,context.Actor,now,c.Basis,CanonicalJson.Fingerprint(accepted),policy.Fingerprint,policy);
   var number=await store.InsertDecisionAsync(record,ct);return Outcome("ProductReleaseDecision",number,"Authorized",record with{DecisionId=number},subject,context);
  },ct,async(uow,now)=>
  {
   context=context with{Principal=await CurrentPrincipalAsync(uow,context.Principal,ct)};
   var subject=await subjects.ResolveAsync(uow,context.Principal,c.Subject,true,ct);var policy=await policies.RequireAsync(uow,context.Principal,QmsPolicyPurpose.ProductRelease,subject.Source.Context,now,ct);
   PermitSubject(context.Principal,Permissions.QualityProductRelease,subject,"ProductReleaseDecision",null);
   var design=await commercial.RequireDesignAsync(uow,context.Principal,subject.Key.PartNumber,subject.Revision,now,ct);
   var required=await criteria.RequiredAsync(uow,context.Principal,subject,now,ct);subject=WithDesignEvidence(WithEvidenceSources(subject,policy,required),design);await CurrentReleaseEvidenceAsync(uow,context.Principal,context.Actor,c,subject,policy,required,ct);await RequireNoBlockersAsync(uow,subject,ct);return Authority(subject,"ProductReleaseDecision",null,subject.ConfigurationFingerprint);
  });
 public Task<CommandResult> WithdrawAsync(WithdrawProductRelease c,CommandContext context,CancellationToken ct)=>pipeline.ExecuteAsync(new("Quality",nameof(WithdrawProductRelease),Permissions.QualityProductRelease,ResourceRef.Of("ProductReleaseDecision",Text(c.DecisionId)),ReasonRequired:true),c,context,async(uow,now)=>
 {
  context=context with{Principal=await CurrentPrincipalAsync(uow,context.Principal,ct)};
  var decision=await uow.Store<IAcceptanceLookup>().FindDecisionAsync(c.DecisionId,ct)??throw Missing();var subject=await subjects.ResolveAsync(uow,context.Principal,decision.Subject,true,ct);
  PermitSubject(context.Principal,Permissions.QualityProductRelease,subject,"ProductReleaseDecision",decision.DecisionId);
  if(c.ExpectedVersion!=decision.Version||await uow.Store<IAcceptanceLookup>().FindWithdrawalAsync(c.DecisionId,ct) is not null)throw new CommandRejectedException("stale_version","The release decision has already changed.");
  if(await uow.Store<IProductReleaseConsumptionLookup>().ConsumedAsync(c.DecisionId,ct)>0)throw new CommandRejectedException("release_consumed","Consumed stock requires an explicit hold/disposition; the historic release/ledger is preserved.");
  if(string.IsNullOrWhiteSpace(c.Basis)||c.Basis.Length>4000)throw new CommandRejectedException("evidence_basis","Record the actual withdrawal basis.");
  var withdrawal=new ProductReleaseWithdrawal(c.DecisionId,2,context.Actor,now,c.Basis);await uow.Store<IAcceptanceStore>().InsertWithdrawalAsync(withdrawal,ct);return Outcome("ProductReleaseDecision",c.DecisionId,"Withdrawn",withdrawal,subject,context);
 },ct,async(uow,_)=>
 {
  context=context with{Principal=await CurrentPrincipalAsync(uow,context.Principal,ct)};
  var decision=await uow.Store<IAcceptanceLookup>().FindDecisionAsync(c.DecisionId,ct)??throw Missing();var subject=await subjects.ResolveAsync(uow,context.Principal,decision.Subject,true,ct);PermitSubject(context.Principal,Permissions.QualityProductRelease,subject,"ProductReleaseDecision",decision.DecisionId);await HistoricPolicyAsync(uow,context.Principal,decision.Policy,ct);return Authority(subject,"ProductReleaseDecision",c.DecisionId,subject.ConfigurationFingerprint);
 });
 public async Task<T> ReadAsync<T>(ForgePrincipal principal,long id,bool release,CancellationToken ct) where T:class
 {
  await using var uow=await factory.BeginSnapshotAsync(ct);object record;AcceptanceSubjectKey key;QmsAdoptedPolicy policy;
  if(release){var decision=await uow.Store<IAcceptanceLookup>().FindDecisionAsync(id,ct)??throw Missing();record=decision;key=decision.Subject;policy=decision.Policy;}
  else{var inspection=await uow.Store<IAcceptanceLookup>().FindInspectionAsync(id,ct)??throw Missing();record=inspection;key=inspection.Subject.Key;policy=inspection.Policy;}
  var subject=await subjects.ResolveAsync(uow,principal,key,false,ct);PermitSubject(principal,Permissions.QualityRead,subject,release?"ProductReleaseDecision":"AcceptanceInspection",id);await HistoricPolicyAsync(uow,principal,policy,ct);
  if(release)foreach(var inspectionId in ((ProductReleaseDecision)record).InspectionIds)await RequireHistoricInspectionReadAsync(uow,principal,inspectionId,ct);
  else await RequireHistoricInspectionReadAsync(uow,principal,((AcceptanceInspection)record).InspectionId,ct);
  return(T)record;
 }
 public async Task ValidateCurrentDecisionAsync(IKernelUnitOfWork uow,ForgePrincipal principal,ProductReleaseDecision decision,InspectionSubject subject,QmsAdoptedPolicy policy,DateTimeOffset now,CancellationToken ct)
 {
  if(decision.Subject!=subject.Key||decision.Revision!=subject.Revision||decision.PolicyFingerprint!=policy.Fingerprint||await uow.Store<IAcceptanceLookup>().FindWithdrawalAsync(decision.DecisionId,ct) is not null)throw new CommandRejectedException("release_not_current","The exact independent product release is unavailable under current governed controls.");
 subject=WithDesignEvidence(subject,await commercial.RequireDesignAsync(uow,principal,subject.Key.PartNumber,subject.Revision,now,ct));
  await HistoricPolicyAsync(uow,principal,decision.Policy,ct);var required=await criteria.RequiredAsync(uow,principal,subject,now,ct);var inspections=new List<AcceptanceInspection>();
  foreach(var id in decision.InspectionIds){await RequireHistoricInspectionReadAsync(uow,principal,id,ct);var inspection=await uow.Store<IAcceptanceLookup>().FindInspectionAsync(id,ct)??throw Missing();if(!inspection.Passed||inspection.Subject.ConfigurationFingerprint!=subject.ConfigurationFingerprint||!required.Any(r=>r.Code==inspection.CriteriaCode&&r.Revision==inspection.CriteriaRevision&&r.Version==inspection.CriteriaVersion))throw new CommandRejectedException("acceptance_not_complete","A current required acceptance arrangement or exact evidence is unavailable.");inspections.Add(inspection);}
  if(inspections.Count!=required.Count||CanonicalJson.Fingerprint(inspections)!=decision.EvidenceFingerprint)throw new CommandRejectedException("release_evidence_changed","The recorded exact release evidence cannot be replaced.");await RequireNoBlockersAsync(uow,subject,ct);
 }
 public async Task<bool> CanReadAsync(IKernelUnitOfWork uow,ForgePrincipal principal,long id,bool release,CancellationToken ct)
 {
  try{var decision=release?await uow.Store<IAcceptanceLookup>().FindDecisionAsync(id,ct):null;var inspection=release?null:await uow.Store<IAcceptanceLookup>().FindInspectionAsync(id,ct);var key=decision?.Subject??inspection?.Subject.Key;if(key is null)return false;var subject=await subjects.ResolveAsync(uow,principal,key,false,ct);PermitSubject(principal,Permissions.QualityRead,subject,release?"ProductReleaseDecision":"AcceptanceInspection",id);await HistoricPolicyAsync(uow,principal,decision?.Policy??inspection!.Policy,ct);if(decision is not null)foreach(var inspectionId in decision.InspectionIds)await RequireHistoricInspectionReadAsync(uow,principal,inspectionId,ct);else await RequireHistoricInspectionReadAsync(uow,principal,inspection!.InspectionId,ct);return true;}catch(ForgeAuthorizationException){return false;}catch(CommandRejectedException){return false;}
 }
 public async Task<IReadOnlyList<ResourceRef>?> ResourcesForNcrAsync(IKernelUnitOfWork uow,long ncrId,string type,long? id,CancellationToken ct)
 {var inspection=await uow.Store<IAcceptanceLookup>().FindInspectionForNcrAsync(ncrId,ct);return inspection is null?null:await subjects.ResourceContextsAsync(uow,inspection.Subject,type,id,ct);}
 public async Task RequireNcrSourceReadAsync(IKernelUnitOfWork uow,ForgePrincipal principal,long ncrId,CancellationToken ct)
 {
  var inspection=await uow.Store<IAcceptanceLookup>().FindInspectionForNcrAsync(ncrId,ct);if(inspection is null)return;
  principal=await CurrentPrincipalAsync(uow,principal,ct);PermitSubject(principal,Permissions.QualityRead,inspection.Subject,"Ncr",ncrId);
  var actual=await subjects.ResolveAsync(uow,principal,inspection.Subject.Key,false,ct);PermitSubject(principal,Permissions.QualityRead,actual,"Ncr",ncrId);
  await RequireHistoricInspectionReadAsync(uow,principal,inspection.InspectionId,ct);await HistoricPolicyAsync(uow,principal,inspection.Policy,ct);
 }
 public async Task<bool> CanReadNcrSourceAsync(IKernelUnitOfWork uow,ForgePrincipal principal,long ncrId,CancellationToken ct)
 {try{await RequireNcrSourceReadAsync(uow,principal,ncrId,ct);return true;}catch(ForgeAuthorizationException){return false;}catch(CommandRejectedException){return false;}}
 private async Task<AcceptanceCriteriaRevision> CurrentInspectionArrangementAsync(IKernelUnitOfWork uow,ForgePrincipal principal,InspectionSubject subject,RecordAcceptanceInspection command,DateTimeOffset now,CancellationToken ct)
 {
  var required=await criteria.RequiredAsync(uow,principal,subject,now,ct);var code=BusinessCode.Require(command.CriteriaCode,"Criteria");
  var arrangement=required.SingleOrDefault(r=>r.Code==code&&r.Revision==command.CriteriaRevision)??throw new CommandRejectedException("criteria_not_current","Record the exact current required acceptance arrangement.");
  if(arrangement.Version!=command.ExpectedCriteriaVersion)throw new CommandRejectedException("stale_version","Acceptance criteria changed before inspection.");return arrangement;
 }
 private async Task<IReadOnlyList<AcceptanceInspection>> CurrentReleaseEvidenceAsync(IKernelUnitOfWork uow,ForgePrincipal principal,string actor,DecideProductRelease command,InspectionSubject subject,QmsAdoptedPolicy policy,IReadOnlyList<AcceptanceCriteriaRevision> required,CancellationToken ct)
 {
  if(command.InspectionIds is null||command.InspectionIds.Count!=required.Count||command.InspectionIds.Distinct().Count()!=command.InspectionIds.Count)throw new CommandRejectedException("required_evidence","Provide one current complete inspection for each actual required acceptance arrangement.");
  var accepted=new List<AcceptanceInspection>();foreach(var id in command.InspectionIds.Order())
  {
   await uow.Store<IAcceptanceStore>().LockInspectionAsync(id,ct);var inspection=await uow.Store<IAcceptanceLookup>().FindInspectionAsync(id,ct)??throw Missing();await RequireHistoricInspectionReadAsync(uow,principal,id,ct);
   var arrangement=required.SingleOrDefault(r=>r.Code==inspection.CriteriaCode&&r.Revision==inspection.CriteriaRevision&&r.Version==inspection.CriteriaVersion)??throw new CommandRejectedException("criteria_not_current","A frozen inspection must match the actual current required acceptance criteria.");
   if(inspection.Version!=command.ExpectedInspectionVersion||inspection.Subject.Key!=subject.Key||inspection.Subject.Revision!=subject.Revision||inspection.Subject.ConfigurationFingerprint!=subject.ConfigurationFingerprint||!inspection.Passed||inspection.Quantity<command.Quantity||inspection.PolicyFingerprint!=policy.Fingerprint)throw new CommandRejectedException("acceptance_not_complete","Exact configuration, current policy and approved inspected coverage are required; a legacy pass is insufficient.");
   if(inspection.Inspector==actor||subject.Source.ProductionActors.Contains(actor,StringComparer.Ordinal))throw new CommandRejectedException("separation_of_duties","The product release signer must be independent of this inspection and production/receipt actors.");
   if(accepted.Any(i=>i.CriteriaCode==arrangement.Code))throw new CommandRejectedException("required_evidence","Evidence must cover every required arrangement once.");accepted.Add(inspection);
  }
  return accepted;
 }
 private async Task RequireHistoricInspectionReadAsync(IKernelUnitOfWork uow,ForgePrincipal principal,long id,CancellationToken ct)
 {
  var inspection=await uow.Store<IAcceptanceLookup>().FindInspectionAsync(id,ct)??throw Missing();
  foreach(var source in inspection.Subject.Source.Sources)Permit(principal,source.Permission,source.Resource);
  await subjects.ResolveAsync(uow,principal,inspection.Subject.Key,false,ct);
  var arrangement=await uow.Store<IAcceptanceLookup>().FindCriteriaAsync(inspection.CriteriaCode,inspection.CriteriaRevision,ct)??throw Missing();
  if(!await criteria.CanReadAsync(uow,principal,arrangement,ct))throw Missing();
  foreach(var code in inspection.Results.Where(r=>r.GaugeCode is not null).Select(r=>r.GaugeCode!).Distinct(StringComparer.Ordinal))Permit(principal,Permissions.ResourcesRead,ResourceRef.Of("Tool",code));
 }
 private async Task HistoricPolicyAsync(IKernelUnitOfWork uow,ForgePrincipal p,QmsAdoptedPolicy policy,CancellationToken ct)
 {
  foreach(var source in policy.Sources)Permit(p,source.Permission,source.Resource);
  var adoption=await uow.Store<IQmsPolicyAdoptionLookup>().FindAsync(policy.AdoptionCode,ct)??throw Missing();var selected=adoption.Revisions.FirstOrDefault(revision=>revision.Revision==policy.AdoptionRevision)??throw Missing();Permit(p,Permissions.QmsRead,QmsPolicyAdoptionService.Resource(adoption,selected));
  await policyAccess.RequireAsync(uow,p,policy.PolicyCode,policy.PolicyRevision,QmsRecordKind.Policy,false,false,pipeline.Time.GetUtcNow(),ct);
  if(policy.Process is{} process)await policyAccess.RequireAsync(uow,p,process.Code,process.Revision,QmsRecordKind.Process,false,false,pipeline.Time.GetUtcNow(),ct);
 }
 private static async Task RequireNoBlockersAsync(IKernelUnitOfWork uow,InspectionSubject subject,CancellationToken ct)
 {
  var lots=subject.PhysicalSourceLots.Append(new(subject.Key.PartNumber,subject.Key.LotNumber)).Distinct().ToArray();
  await HoldGate.RequireNoneAsync(uow,new(HoldActivity.Ship,Parts:[subject.Key.PartNumber],LotList:lots,WorkCenterCode:subject.Source.Context.WorkCenterCode,ProgramId:subject.Source.Context.ProgramId),ct);
  foreach(var order in subject.OriginOrderNumbers)await HoldGate.RequireNoneAsync(uow,new(HoldActivity.Ship,OrderNumber:order,Parts:[subject.Key.PartNumber],LotList:lots),ct);
  foreach(var program in subject.Source.Sources.Where(s=>s.Resource.Program is not null).Select(s=>s.Resource.Program!.Value.Value).Distinct())await HoldGate.RequireNoneAsync(uow,new(HoldActivity.Ship,ProgramId:program,Parts:[subject.Key.PartNumber],LotList:lots),ct);
  if(await uow.Store<IAcceptanceLookup>().HasOpenNcrAsync(subject.Key,ct))throw new CommandRejectedException("ncr_open","The exact physical scope remains on an unresolved nonconformance.");
 }
 public static ResourceRef Resource(InspectionSubject subject,string type,long? id)=>AcceptanceSubjectResources.Of(subject,type,id);
 private static InspectionSubject WithEvidenceSources(InspectionSubject subject,QmsAdoptedPolicy policy,IReadOnlyList<AcceptanceCriteriaRevision> arrangements)
 {
  var sources=subject.Source.Sources.Concat(policy.Sources).Concat(arrangements.Select(arrangement=>new QmsPolicyReadSource(Permissions.QualityRead,AcceptanceCriteriaService.Resource(arrangement)))).Concat(arrangements.SelectMany(arrangement=>arrangement.Sources.Select(source=>new QmsPolicyReadSource(Permissions.EngineeringRead,new ResourceRef("DocumentRevision",source.Key+"/"+source.Revision,source.ProgramId is{} program?new ProgramId(program):null,source.Markings))))).Distinct().ToArray();
  return subject with{Source=subject.Source with{Sources=sources}};
 }
 private static InspectionSubject WithDesignEvidence(InspectionSubject subject,CommercialUseEvidence? design)
 {
  if(design is null)return subject;
  var sources=subject.Source.Sources.Concat(design.Sources.Select(source=>new QmsPolicyReadSource(source.Permission,source.Resource)))
   .Concat(design.Records.SelectMany(record=>record.Ownership.Select(resource=>new QmsPolicyReadSource(Permissions.QualityRead,
    resource with{Type="CommercialQuality",Id=CommercialQualityService.Key(record.Kind,record.Code,record.Revision),FacilityId=resource.FacilityId??record.FacilityCode}))))
   .Distinct().ToArray();
  return subject with{Source=subject.Source with{Sources=sources},ConfigurationFingerprint=CanonicalJson.Fingerprint(new{subject.ConfigurationFingerprint,DesignApplicability=design.Fingerprint})};
 }
 private static CommandAuthority Authority(InspectionSubject subject,string type,long? id,string identity){var resources=AcceptanceSubjectResources.All(subject,type,id);return new(resources[0],identity,resources.Skip(1).ToArray());}
 private void PermitSubject(ForgePrincipal p,string permission,InspectionSubject subject,string type,long? id){foreach(var resource in AcceptanceSubjectResources.All(subject,type,id))Permit(p,permission,resource);}
 private CommandOutcome Outcome<T>(string type,long id,string action,T record,InspectionSubject subject,CommandContext context){var resource=Resource(subject,type,id);return new(CommandResult.Ok(Text(id),$"{type} {id}: {action}."),[pipeline.Event(new AcceptanceChanged(type,Text(id),action),context,"Quality",type,Text(id),resource.Program,resource.Markings)],new AuditChange(type,Text(id),null,CanonicalJson.Serialize(record),resource.Program,resource.Markings));}
 private void Permit(ForgePrincipal p,string permission,ResourceRef resource){var decision=authorizer.Authorize(p,permission,resource);if(!decision.IsPermitted)throw new ForgeAuthorizationException(decision,resource);}
 private static async Task<ForgePrincipal> CurrentPrincipalAsync(IKernelUnitOfWork uow,ForgePrincipal p,CancellationToken ct)=>await uow.People.LoadPrincipalAsync(p.PersonId,ct)??p with{IsActive=false};
 private static string Text(long id)=>id.ToString(CultureInfo.InvariantCulture);
 private static CommandRejectedException Missing()=>new("acceptance_source_unavailable","The exact acceptance/release evidence is unavailable.");
}


