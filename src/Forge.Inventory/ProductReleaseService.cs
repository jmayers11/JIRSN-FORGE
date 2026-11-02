using System.Globalization;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;

namespace Forge.Inventory;

public sealed record ConsumeProductRelease(long DecisionId,int ExpectedVersion,decimal Quantity);

public sealed class ProductReleaseService(CommandPipeline pipeline,IInspectionSubjectResolver subjects,IQmsPolicyReader policies,IProductReleaseDecisionValidator acceptance,IForgeAuthorizer authorizer)
{
 public Task<CommandResult> ConsumeAsync(ConsumeProductRelease c,CommandContext context,CancellationToken ct)=>pipeline.ExecuteAsync(new("Inventory",nameof(ConsumeProductRelease),Permissions.InventoryTransact,ResourceRef.Of("ProductReleaseDecision",c.DecisionId.ToString(CultureInfo.InvariantCulture)),ReasonRequired:true),c,context,async(uow,now)=>
 {
  context=context with{Principal=await CurrentPrincipalAsync(uow,context.Principal,ct)};
  var record=await uow.Store<IAcceptanceLookup>().FindDecisionAsync(c.DecisionId,ct)??throw Missing();if(record.Subject.ReturnCohortId is not null)throw new CommandRejectedException("return_restock_required","A returned cohort is consumed only by the controlled CustomerReturn Restock command with current ownership requirements.");var subject=await subjects.ResolveAsync(uow,context.Principal,record.Subject,true,ct);
  Permit(context.Principal,subject,"ProductReleaseDecision",record.DecisionId);
  var policy=await policies.RequireAsync(uow,context.Principal,QmsPolicyPurpose.ProductRelease,subject.Source.Context,now,ct);await acceptance.ValidateCurrentDecisionAsync(uow,context.Principal,record,subject,policy,now,ct);
  if(c.ExpectedVersion!=record.Version)throw new CommandRejectedException("stale_version","The exact release version changed.");
  var used=await uow.Store<IProductReleaseInventoryLookup>().ConsumedAsync(record.DecisionId,ct);
  if(c.Quantity<=0||c.Quantity>subject.InspectionQuantity||used+c.Quantity>record.Quantity||subject.Serialized&&c.Quantity!=1)throw new CommandRejectedException("release_quantity_exhausted","Only unconsumed independently released Inspection quantity can be posted.");
  var id=await uow.Store<IInventoryStore>().InsertTransactionAsync(new(InventoryTransactionKind.QualityRelease,record.Subject.PartNumber,c.Quantity,record.Subject.LocationCode,StockStatus.Inspection,record.Subject.LocationCode,StockStatus.Released,record.Subject.LotNumber,Reason:"Independent product release "+record.DecisionId,ApprovedBy:record.ApprovedBy,PartRevision:record.Revision),now,context.Actor,ct);
  var consumption=new ProductReleaseConsumption(record.DecisionId,id,record.Subject.PartNumber,record.Subject.LotNumber,record.Subject.LocationCode,c.Quantity,context.Actor,now,policy.Fingerprint,record.EvidenceFingerprint);await uow.Store<IProductReleaseInventoryStore>().InsertConsumptionAsync(consumption,ct);
  var resource=AcceptanceSubjectResources.Of(subject,"InventoryTransaction",id);return new(CommandResult.Ok(id.ToString(CultureInfo.InvariantCulture),$"Consumed {c.Quantity} of independent product release {record.DecisionId}."),[pipeline.Event(new InventoryMoved(id,"QualityRelease",record.Subject.PartNumber,c.Quantity,record.Subject.LocationCode,record.Subject.LocationCode),context,"Inventory","InventoryTransaction",id.ToString(CultureInfo.InvariantCulture),resource.Program,resource.Markings)],new AuditChange("InventoryTransaction",id.ToString(CultureInfo.InvariantCulture),null,CanonicalJson.Serialize(consumption),resource.Program,resource.Markings));
 },ct,async(uow,now)=>
 {
  context=context with{Principal=await CurrentPrincipalAsync(uow,context.Principal,ct)};
  var record=await uow.Store<IAcceptanceLookup>().FindDecisionAsync(c.DecisionId,ct)??throw Missing();if(record.Subject.ReturnCohortId is not null)throw new CommandRejectedException("return_restock_required","A returned cohort is consumed only by the controlled CustomerReturn Restock command with current ownership requirements.");var subject=await subjects.ResolveAsync(uow,context.Principal,record.Subject,true,ct);Permit(context.Principal,subject,"ProductReleaseDecision",record.DecisionId);var policy=await policies.RequireAsync(uow,context.Principal,QmsPolicyPurpose.ProductRelease,subject.Source.Context,now,ct);await acceptance.ValidateCurrentDecisionAsync(uow,context.Principal,record,subject,policy,now,ct);var resources=AcceptanceSubjectResources.All(subject,"ProductReleaseDecision",record.DecisionId);return new CommandAuthority(resources[0],record.EvidenceFingerprint,resources.Skip(1).ToArray());
 });
 public async Task<QmsAdoptedPolicy?> OutputPolicyAsync(IKernelUnitOfWork uow,ForgePrincipal principal,string part,string? revision,string location,long? order,long? po,DateTimeOffset now,CancellationToken ct)
 {
  principal=await CurrentPrincipalAsync(uow,principal,ct);
  var actual=await subjects.OutputContextAsync(uow,principal,part,revision,location,order,po,ct);return await policies.TryAsync(uow,principal,QmsPolicyPurpose.ProductRelease,actual.Context,now,ct);
 }
 public async Task<bool> IsGovernedStockAsync(IKernelUnitOfWork uow,ForgePrincipal principal,string part,string? lot,string location,DateTimeOffset now,CancellationToken ct)
 {
  principal=await CurrentPrincipalAsync(uow,principal,ct);
  part=BusinessCode.Require(part,"Part");location=BusinessCode.Require(location,"Location");lot=string.IsNullOrWhiteSpace(lot)?null:AcceptanceSubjectReference.Normalize(new(part,lot,location)).LotNumber;
  var physical=lot is null?null:await uow.Store<IInventoryLookup>().FindLotAsync(part,lot.Trim().ToUpperInvariant(),ct);
  var preliminary=await subjects.OutputContextAsync(uow,principal,part,physical?.PartRevision,location,null,null,ct);
  try{if(await policies.TryAsync(uow,principal,QmsPolicyPurpose.ProductRelease,preliminary.Context,now,ct) is not null)return true;}
  catch(CommandRejectedException rejected)when(rejected.Code=="governed_policy_scope_unknown")
  {
   if(lot is null||physical?.PartRevision is null)throw;
   var actual=await subjects.ResolveAsync(uow,principal,new(part,lot,location),false,ct);if(await policies.TryAsync(uow,principal,QmsPolicyPurpose.ProductRelease,actual.Source.Context,now,ct) is not null)return true;
  }
  if(lot is null)return false;
  var connected=new HashSet<string>(StringComparer.Ordinal){lot.Trim().ToUpperInvariant()};var links=await uow.Store<IInventoryLookup>().ListInstanceLinksAsync(part,null,ct);bool changed;
  do{changed=false;foreach(var link in links.Where(l=>l.SourcePart==part&&l.ResultPart==part))if(connected.Contains(link.SourceLot)||connected.Contains(link.ResultLot)){changed|=connected.Add(link.SourceLot);changed|=connected.Add(link.ResultLot);}}while(changed);
  return(await uow.Store<IProductReleaseInventoryLookup>().CoverageAsync(part,connected.Order(StringComparer.Ordinal).ToArray(),ct)).Consumptions.Count>0;
 }
 public Task RequireReleasedScopeAsync(IKernelUnitOfWork uow,ForgePrincipal principal,string part,string? lot,string location,decimal quantity,DateTimeOffset now,CancellationToken ct)=>CheckCurrentScopeAsync(uow,principal,part,lot,location,quantity,true,now,ct);
 public Task RecheckReleasedScopeAsync(IKernelUnitOfWork uow,ForgePrincipal principal,string part,string? lot,string location,DateTimeOffset now,CancellationToken ct)=>CheckCurrentScopeAsync(uow,principal,part,lot,location,0,false,now,ct);
 private async Task CheckCurrentScopeAsync(IKernelUnitOfWork uow,ForgePrincipal principal,string part,string? lot,string location,decimal quantity,bool requireQuantity,DateTimeOffset now,CancellationToken ct)
 {
  principal=await CurrentPrincipalAsync(uow,principal,ct);
  part=BusinessCode.Require(part,"Part");location=BusinessCode.Require(location,"Location");lot=string.IsNullOrWhiteSpace(lot)?null:AcceptanceSubjectReference.Normalize(new(part,lot,location)).LotNumber;
  if(lot is null)
  {
   var untracked=await subjects.OutputContextAsync(uow,principal,part,null,location,null,null,ct);if(await policies.TryAsync(uow,principal,QmsPolicyPurpose.ProductRelease,untracked.Context,now,ct) is not null)throw new CommandRejectedException("identified_release_required","Governed stock requires exact recorded physical identity and configuration.");return;
  }
  var physical=await uow.Store<IInventoryLookup>().FindLotAsync(part,lot,ct)??throw Missing();
  var connected=new HashSet<string>(StringComparer.Ordinal){lot};var links=await uow.Store<IInventoryLookup>().ListInstanceLinksAsync(part,null,ct);bool changed;
  do{changed=false;foreach(var link in links.Where(l=>l.SourcePart==part&&l.ResultPart==part))if(connected.Contains(link.SourceLot)||connected.Contains(link.ResultLot)){changed|=connected.Add(link.SourceLot);changed|=connected.Add(link.ResultLot);}}while(changed);
  var coverage=await uow.Store<IProductReleaseInventoryLookup>().CoverageAsync(part,connected.Order(StringComparer.Ordinal).ToArray(),ct);
  var preliminary=await subjects.OutputContextAsync(uow,principal,part,physical.PartRevision,location,null,null,ct);
  QmsAdoptedPolicy? preliminaryPolicy=null;var scopeUnknown=false;
  try{preliminaryPolicy=await policies.TryAsync(uow,principal,QmsPolicyPurpose.ProductRelease,preliminary.Context,now,ct);}
  catch(CommandRejectedException rejected)when(rejected.Code=="governed_policy_scope_unknown"){scopeUnknown=true;}
  // A genuinely ungoverned physical source retains the existing V3 owner behavior and does not acquire new source-read obligations.
  if(preliminaryPolicy is null&&!scopeUnknown&&coverage.Consumptions.Count==0)return;
  if(physical.PartRevision is null)throw new CommandRejectedException("configuration_unknown","Governed release cannot infer the configuration/coverage of legacy stock.");
  var subject=await subjects.ResolveAsync(uow,principal,new(part,lot,location),false,ct);var current=await policies.TryAsync(uow,principal,QmsPolicyPurpose.ProductRelease,subject.Source.Context,now,ct);
  if(current is null&&coverage.Consumptions.Count==0)return;
  if(requireQuantity&&await uow.Store<IInventoryStore>().BalanceAsync(part,location,lot,StockStatus.Released,ct)<quantity)throw new CommandRejectedException("not_released","Only actual independently released quantity at this exact identity/location may be used or packed.");
  if(coverage.Consumptions.Count==0||requireQuantity&&(coverage.RemainingAuthorizedQuantity<quantity||coverage.ActualReleasedQuantity>coverage.RemainingAuthorizedQuantity))throw new CommandRejectedException("released_quantity_not_authorized","Actual Released stock exceeds independently approved remaining quantity; historical passes/adjustments do not create release authority.");
  foreach(var id in coverage.Consumptions.Select(c=>c.DecisionId).Distinct())
  {
   var decision=await uow.Store<IAcceptanceLookup>().FindDecisionAsync(id,ct)??throw Missing();var original=await subjects.ResolveAsync(uow,principal,decision.Subject,false,ct);
   var originalPolicy=await policies.RequireAsync(uow,principal,QmsPolicyPurpose.ProductRelease,original.Source.Context,now,ct);await acceptance.ValidateCurrentDecisionAsync(uow,principal,decision,original,originalPolicy,now,ct);
   if(current is not null&&current.Fingerprint!=originalPolicy.Fingerprint)throw new CommandRejectedException("release_policy_changed","The current applicable scope requires a new explicit release assessment.");
  }
 }
 private static CommandRejectedException Missing()=>new("product_release_unavailable","The exact independent product release is unavailable.");
 private void Permit(ForgePrincipal p,InspectionSubject subject,string type,long id){foreach(var resource in AcceptanceSubjectResources.All(subject,type,id)){var decision=authorizer.Authorize(p,Permissions.InventoryTransact,resource);if(!decision.IsPermitted)throw new ForgeAuthorizationException(decision,resource);}}
 // A system actor (no person, only system roles that cannot be granted) is Forge itself acting on a person's recorded work, such as a
 // backflush after a production report (owner decisions 2026-11-01); it has no person record to reload.
 private static async Task<ForgePrincipal> CurrentPrincipalAsync(IKernelUnitOfWork uow,ForgePrincipal p,CancellationToken ct)=>
  p.PersonId.Value==Guid.Empty&&p.Roles.Count>0&&p.Roles.All(r=>r.StartsWith("system.",StringComparison.Ordinal))?p
  :await uow.People.LoadPrincipalAsync(p.PersonId,ct)??p with{IsActive=false};
}

