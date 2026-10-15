using System.Globalization;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Holds;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;

namespace Forge.Inventory;

/// <summary>Called by the normal controlled CustomerReturn commands in their same UOW, never as a second pipeline or ledger.</summary>
public sealed class ReturnAcceptanceService(IInspectionSubjectResolver subjects,IReturnAcceptanceSourceResolver returnSources,
 IQmsPolicyReader policies,IProductReleaseDecisionValidator acceptance,IForgeAuthorizer authorizer):IReturnAcceptanceBridge
{
 public async Task<ReturnAcceptanceAuthority> PreparationAuthorityAsync(IKernelUnitOfWork uow,ForgePrincipal principal,long rmaId,string? locationCode,DateTimeOffset at,CancellationToken ct)
 {
  principal=await CurrentAsync(uow,principal,ct);
  var authority=await AuthorityAsync(uow,principal,rmaId,null,null,at,ct)??throw new CommandRejectedException("return_policy_required","Adopt explicit current product-release controls before preparing governed return reinspection.");
  var rma=await RmaAsync(uow,rmaId,ct);var from=await uow.Store<IInventoryLookup>().FindLocationAsync(rma.LocationCode,ct)??throw Missing();
  var target=await uow.Store<IInventoryLookup>().FindLocationAsync(BusinessCode.Require(locationCode??rma.LocationCode,"Location"),ct)??throw Missing();
  if(!target.Active||!target.InCompanyCustody||target.SiteCode!=from.SiteCode)throw new CommandRejectedException("return_inspection_location","Use a real active location in this return's facility and company custody.");
  var read=new ResourceRef("Location",target.Code,FacilityId:target.SiteCode,WorkCenterId:target.WorkCenterCode);Permit(principal,Permissions.InventoryRead,read);
  var resources=authority.Resources.Concat(authority.Resources.Select(resource=>resource with{FacilityId=target.SiteCode,WorkCenterId=target.WorkCenterCode??resource.WorkCenterId})).Distinct().ToArray();
  foreach(var resource in resources){Permit(principal,Permissions.QualityDisposition,resource);Permit(principal,Permissions.InventoryTransact,resource);}
  return authority with{Resources=resources,Sources=authority.Sources.Append(new(Permissions.InventoryRead,read)).Distinct().ToArray(),SubjectIdentity=CanonicalJson.Fingerprint(new{authority.SubjectIdentity,Target=read})};
 }
 public async Task<ReturnAcceptanceAuthority?> AuthorityAsync(IKernelUnitOfWork uow,ForgePrincipal principal,long rmaId,long? decisionId,int? expectedDecisionVersion,DateTimeOffset at,CancellationToken ct)
 {
  principal=await CurrentAsync(uow,principal,ct);await uow.Store<IReturnAcceptanceInventoryLock>().LockRmaAsync(rmaId,ct);
  var rma=await RmaAsync(uow,rmaId,ct);var location=await uow.Store<IInventoryLookup>().FindLocationAsync(rma.LocationCode,ct)??throw Missing();
  var lot=rma.LotNumber is{} identity?await uow.Store<IInventoryLookup>().FindLotAsync(rma.PartNumber,identity,ct):null;
  var context=await subjects.OutputContextAsync(uow,principal,rma.PartNumber,lot?.PartRevision,rma.LocationCode,null,null,ct);
  var metadata=await returnSources.ResourceContextsAsync(uow,rmaId,location.SiteCode,ct);var programs=metadata.Select(resource=>resource.Program?.Value).Distinct().ToArray();
  context=context with{Context=context.Context with{ProgramId=programs.Length==1?programs[0]:null}};
  QmsAdoptedPolicy? policy;
  try{policy=await policies.TryAsync(uow,principal,QmsPolicyPurpose.ProductRelease,context.Context,at,ct);}
  catch(CommandRejectedException failure)when(failure.Code=="governed_policy_scope_unknown")
  {
   if(rma.LotNumber is null||lot?.PartRevision is null)throw;
   var exact=await subjects.ResolveAsync(uow,principal,new(rma.PartNumber,rma.LotNumber,rma.LocationCode),true,ct);
   policy=await policies.TryAsync(uow,principal,QmsPolicyPurpose.ProductRelease,exact.Source.Context,at,ct);
  }
  var previous=await uow.Store<IReturnAcceptanceLookup>().CohortsForRmaAsync(rmaId,ct);
  if(policy is null&&previous.Count==0)return null;
  var actual=await returnSources.ResolveAsync(uow,principal,rmaId,location.SiteCode,ct);
  if(rma.LotNumber is null||lot?.PartRevision is null)throw new CommandRejectedException("return_configuration_unknown","Governed reinspection requires the actual returned lot/serial and recorded revision.");
  var original=await subjects.ResolveAsync(uow,principal,new(rma.PartNumber,rma.LotNumber,rma.LocationCode),true,ct);
  policy=await policies.RequireAsync(uow,principal,QmsPolicyPurpose.ProductRelease,original.Source.Context,at,ct);
  var resources=AcceptanceSubjectResources.All(original,"CustomerReturn",rmaId).Concat(actual.Resources.Select(resource=>resource with{Type="CustomerReturn",Id=Text(rmaId)})).Distinct().ToArray();
  foreach(var resource in resources)Permit(principal,Permissions.QualityDisposition,resource);
  var captured=original.Source.Sources.Concat(actual.Sources).Concat(policy.Sources).Distinct().ToArray();
  foreach(var source in captured)Permit(principal,source.Permission,source.Resource);
  await RequireHoldsAsync(uow,original,rma,decisionId is null?HoldActivity.Receive:HoldActivity.Ship,ct);
  if(rma.Disposition is CustomerReturnDisposition.Rework or CustomerReturnDisposition.Scrap)throw new CommandRejectedException("return_disposition_changed","The actual return disposition no longer permits this reinspection/restock path.");
  if(decisionId is{} id)
  {
   var decision=await uow.Store<IAcceptanceLookup>().FindDecisionAsync(id,ct)??throw Missing();
   if(decision.Subject.ReturnCohortId is not{} cohortId)throw new CommandRejectedException("return_release_required","Restock requires independent release evidence for this exact return cohort, not the original release.");
   var cohort=await uow.Store<IReturnAcceptanceLookup>().FindCohortAsync(cohortId,ct)??throw Missing();
   if(cohort.RmaId!=rmaId||cohort.OriginalReturnTransactionId!=rma.ReceiptTransactionId||cohort.OriginalReturnFingerprint!=ReturnCohortIdentity.Fingerprint(rma))throw new CommandRejectedException("return_cohort_mismatch","Use this exact recorded return cohort and original return source.");
   if(expectedDecisionVersion!=decision.Version)throw new CommandRejectedException("stale_version","The exact return release version changed.");
   var selected=await subjects.ResolveAsync(uow,principal,decision.Subject,true,ct);
   var selectedPolicy=await policies.RequireAsync(uow,principal,QmsPolicyPurpose.ProductRelease,selected.Source.Context,at,ct);
   await acceptance.ValidateCurrentDecisionAsync(uow,principal,decision,selected,selectedPolicy,at,ct);
   resources=resources.Concat(AcceptanceSubjectResources.All(selected,"CustomerReturn",rmaId)).Distinct().ToArray();
   foreach(var resource in resources)Permit(principal,Permissions.InventoryTransact,resource);
  }
  return new(policy,resources,captured,CanonicalJson.Fingerprint(new{Rma=ReturnCohortIdentity.Fingerprint(rma),decisionId}));
 }
 public async Task<ReturnInspectionPrepared> PrepareAsync(IKernelUnitOfWork uow,CommandContext context,long rmaId,decimal quantity,string? locationCode,DateTimeOffset at,CancellationToken ct)
 {
  context=context with{Principal=await CurrentAsync(uow,context.Principal,ct)};
  var authority=await PreparationAuthorityAsync(uow,context.Principal,rmaId,locationCode,at,ct);
  var rma=await RmaAsync(uow,rmaId,ct);var inventory=uow.Store<IInventoryStore>();await inventory.LockPartAsync(rma.PartNumber,ct);
  if(rma.Disposition is not null)throw new CommandRejectedException("already_dispositioned","The original customer return is already complete.");
  var original=await uow.Store<IReceivingLookup>().FindTransactionAsync(rma.ReceiptTransactionId,ct)??throw Missing();
  if(original.Kind!=InventoryTransactionKind.CustomerReturn||original.PartNumber!=rma.PartNumber||original.LotNumber!=rma.LotNumber||original.Quantity!=rma.Quantity||original.ToStatus!=StockStatus.Hold||original.ToLocation!=rma.LocationCode||original.ShipmentId!=rma.ShipmentId)throw Missing();
  var item=await uow.Store<Forge.Engineering.Contracts.IEngineeringLookup>().FindItemAsync(rma.PartNumber,ct)??throw Missing();
  if(quantity<=0||item.Serialized&&quantity!=1||await uow.Store<IReturnAcceptanceInventoryLookup>().PreparedForRmaAsync(rmaId,ct)+quantity>rma.Quantity)throw new CommandRejectedException("return_quantity_exhausted","Only this return's unprepared actual quantity can enter a new reinspection cohort.");
  var target=await uow.Store<IInventoryLookup>().FindLocationAsync(BusinessCode.Require(locationCode??rma.LocationCode,"Location"),ct)??throw Missing();
  var sourceLocation=await uow.Store<IInventoryLookup>().FindLocationAsync(rma.LocationCode,ct)??throw Missing();
  if(!target.Active||!target.InCompanyCustody||target.SiteCode!=sourceLocation.SiteCode)throw new CommandRejectedException("return_inspection_location","Use a real active location in this return's facility and company custody.");
  Permit(context.Principal,Permissions.InventoryRead,new("Location",target.Code,FacilityId:target.SiteCode,WorkCenterId:target.WorkCenterCode));
  foreach(var resource in authority.Resources)Permit(context.Principal,Permissions.InventoryTransact,resource);
  if(await inventory.BalanceAsync(rma.PartNumber,rma.LocationCode,rma.LotNumber,StockStatus.Hold,ct)<quantity)throw new CommandRejectedException("insufficient_stock","The exact returned Hold quantity is no longer present.");
  var physical=await uow.Store<IInventoryLookup>().FindLotAsync(rma.PartNumber,rma.LotNumber!,ct)??throw Missing();
  var writer=uow.Store<IReturnCohortWriter>();var cohortId=await writer.AllocateCohortIdAsync(ct);var key=new AcceptanceSubjectKey(rma.PartNumber,rma.LotNumber!,target.Code,cohortId);
  var transaction=await inventory.InsertTransactionAsync(new(InventoryTransactionKind.Transfer,rma.PartNumber,quantity,rma.LocationCode,StockStatus.Hold,target.Code,StockStatus.Inspection,rma.LotNumber,Reason:$"Return {rmaId} reinspection cohort {cohortId}: {context.Reason}",PartRevision:physical.PartRevision),at,context.Actor,ct);
  var cohort=new ReturnInspectionCohort(cohortId,1,rmaId,ReturnCohortIdentity.Fingerprint(rma),rma.ReceiptTransactionId,transaction,key,physical.PartRevision!,quantity,context.Actor,at,context.Reason!,authority.Sources,string.Empty);
  cohort=cohort with{Fingerprint=CanonicalJson.Fingerprint(cohort with{Fingerprint=string.Empty})};await writer.InsertCohortAsync(cohort,ct);
  return new(cohortId,1,rmaId,rma.ReceiptTransactionId,transaction,key,cohort.Revision,quantity,target.Code);
 }
 public async Task<ReturnReleaseApplied> ConsumeReturnReleaseAsync(IKernelUnitOfWork uow,CommandContext context,long rmaId,long decisionId,int expectedVersion,decimal quantity,DateTimeOffset at,CancellationToken ct)
 {
  context=context with{Principal=await CurrentAsync(uow,context.Principal,ct)};
  _=await AuthorityAsync(uow,context.Principal,rmaId,decisionId,expectedVersion,at,ct)??throw Missing();
  var rma=await RmaAsync(uow,rmaId,ct);var decision=await uow.Store<IAcceptanceLookup>().FindDecisionAsync(decisionId,ct)??throw Missing();
  var cohort=await uow.Store<IReturnAcceptanceLookup>().FindCohortAsync(decision.Subject.ReturnCohortId!.Value,ct)??throw Missing();
  var subject=await subjects.ResolveAsync(uow,context.Principal,decision.Subject,true,ct);var policy=await policies.RequireAsync(uow,context.Principal,QmsPolicyPurpose.ProductRelease,subject.Source.Context,at,ct);
  var totals=uow.Store<IReturnAcceptanceInventoryLookup>();var consumed=await uow.Store<IProductReleaseInventoryLookup>().ConsumedAsync(decisionId,ct);
  var total=await totals.ConsumedForRmaAsync(rmaId,ct);var cohortConsumed=await totals.ConsumedForCohortAsync(cohort.CohortId,ct);
  if(quantity<=0||subject.Serialized&&quantity!=1||quantity>subject.InspectionQuantity||consumed+quantity>decision.Quantity||cohortConsumed+quantity>cohort.Quantity||total+quantity>rma.Quantity)throw new CommandRejectedException("return_quantity_exhausted","Only unconsumed accepted and independently released quantity of this exact returned cohort may be restocked.");
  if(rma.Disposition is not null)throw new CommandRejectedException("already_dispositioned","The original return is already complete.");
  var transaction=await uow.Store<IInventoryStore>().InsertTransactionAsync(new(InventoryTransactionKind.QualityRelease,subject.Key.PartNumber,quantity,subject.Key.LocationCode,StockStatus.Inspection,subject.Key.LocationCode,StockStatus.Released,subject.Key.LotNumber,Reason:$"Independent return cohort {cohort.CohortId} product release {decisionId}: {context.Reason}",ApprovedBy:decision.ApprovedBy,PartRevision:subject.Revision),at,context.Actor,ct);
  await uow.Store<IProductReleaseInventoryStore>().InsertConsumptionAsync(new(decisionId,transaction,subject.Key.PartNumber,subject.Key.LotNumber,subject.Key.LocationCode,quantity,context.Actor,at,policy.Fingerprint,decision.EvidenceFingerprint),ct);
  await uow.Store<IReturnAcceptanceInventoryStore>().InsertCohortConsumptionAsync(cohort.CohortId,rmaId,decisionId,transaction,quantity,ct);
  return new(cohort.CohortId,decisionId,transaction,rmaId,quantity,total+quantity,rma.Quantity,total+quantity==rma.Quantity);
 }
 private static async Task<CustomerReturnInfo> RmaAsync(IKernelUnitOfWork uow,long id,CancellationToken ct)=>await uow.Store<IReceivingLookup>().FindCustomerReturnAsync(id,ct)??throw Missing();
 private static async Task RequireHoldsAsync(IKernelUnitOfWork uow,InspectionSubject subject,CustomerReturnInfo rma,HoldActivity activity,CancellationToken ct)
 {
  var lots=subject.PhysicalSourceLots.Append(new(subject.Key.PartNumber,subject.Key.LotNumber)).Distinct().ToArray();
  await HoldGate.RequireNoneAsync(uow,new(activity,Parts:[subject.Key.PartNumber],LotList:lots,WorkCenterCode:subject.Source.Context.WorkCenterCode,MachineCode:subject.Source.Context.MachineCode,ShipmentId:rma.ShipmentId,ProgramId:subject.Source.Context.ProgramId),ct);
  foreach(var order in subject.OriginOrderNumbers)await HoldGate.RequireNoneAsync(uow,new(activity,OrderNumber:order,Parts:[subject.Key.PartNumber],LotList:lots,ShipmentId:rma.ShipmentId),ct);
  foreach(var source in subject.Source.Sources)
  {
   if(source.Resource.Program is{} program)await HoldGate.RequireNoneAsync(uow,new(activity,ProgramId:program.Value,Parts:[subject.Key.PartNumber],LotList:lots,ShipmentId:rma.ShipmentId),ct);
   if(source.Permission==Permissions.PurchasingRead&&long.TryParse(source.Resource.Id,NumberStyles.None,CultureInfo.InvariantCulture,out var po))await HoldGate.RequireNoneAsync(uow,new(activity,PoNumber:po,Parts:[subject.Key.PartNumber],LotList:lots,ShipmentId:rma.ShipmentId),ct);
  }
 }
 private void Permit(ForgePrincipal principal,string permission,ResourceRef resource){var decision=authorizer.Authorize(principal,permission,resource);if(!decision.IsPermitted)throw new ForgeAuthorizationException(decision,resource);}
 private static async Task<ForgePrincipal> CurrentAsync(IKernelUnitOfWork uow,ForgePrincipal principal,CancellationToken ct)=>await uow.People.LoadPrincipalAsync(principal.PersonId,ct)??principal with{IsActive=false};
 private static string Text(long value)=>value.ToString(CultureInfo.InvariantCulture);
 private static CommandRejectedException Missing()=>new("return_source_unavailable","The exact returned product source/cohort is unavailable.");
}
