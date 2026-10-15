using System.Globalization;
using Forge.Execution.Contracts;
using Forge.Inventory;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Holds;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Kernel.WorkAssignments;
using Forge.Quality.Contracts;
using Forge.Resources.Contracts;

namespace Forge.Host.Quality;

/// <summary>Exact current owner sources; no guessed order from a display label and no fixed-depth/row-cap ancestry.</summary>
public sealed class InspectionSubjectResolver(IWorkSourceResolver sources,IForgeAuthorizer authorizer,IReturnAcceptanceSourceResolver returnedSources):IInspectionSubjectResolver
{
 public async Task<IReadOnlyList<ResourceRef>> ResourceContextsAsync(IKernelUnitOfWork uow,InspectionSubject recorded,string type,long? id,CancellationToken ct)
 {
  var key=AcceptanceSubjectReference.Normalize(recorded.Key);var location=await uow.Store<IInventoryLookup>().FindLocationAsync(key.LocationCode,ct)??throw Missing();
  var target=id?.ToString(CultureInfo.InvariantCulture);var saved=AcceptanceSubjectResources.All(recorded,type,id).Select(resource=>resource with{Type=type,Id=target}).ToList();
  foreach(var originId in recorded.OriginTransactionIds)
  {
   var origin=await uow.Store<IReceivingLookup>().FindTransactionAsync(originId,ct)??throw Missing();
   string? sourceType=null;string? sourceId=null;string? workCenter=location.WorkCenterCode;
   if(origin.OrderNumber is{} orderNumber){sourceType="Order";sourceId=orderNumber.ToString(CultureInfo.InvariantCulture);var order=await uow.Store<IExecutionLookup>().FindOrderAsync(orderNumber,ct)??throw Missing();workCenter=order.Operations.OrderBy(operation=>operation.Sequence).LastOrDefault()?.WorkCenterCode;}
   else if(origin.PoNumber is{} purchaseOrder){sourceType="PurchaseOrder";sourceId=purchaseOrder.ToString(CultureInfo.InvariantCulture);}
   if(sourceType is null){saved.Add(new(type,target,Markings:saved.Aggregate(DataMarking.None,(marks,resource)=>marks|resource.Markings),FacilityId:location.SiteCode,WorkCenterId:workCenter));continue;}
   var current=await sources.ResolveAsync(uow,sourceType,sourceId!,ct)??throw Missing();if(current.Resources.Count==0)throw Missing();
   foreach(var resource in current.Resources)saved.Add(resource with{Type=type,Id=target,FacilityId=location.SiteCode,WorkCenterId=workCenter??resource.WorkCenterId,Markings=resource.Markings|saved.Aggregate(DataMarking.None,(marks,source)=>marks|source.Markings)});
  }
  if(recorded.Key.ReturnCohortId is{} cohortId)
  {
   var cohort=await uow.Store<IReturnAcceptanceLookup>().FindCohortAsync(cohortId,ct)??throw Missing();
   var current=await returnedSources.ResourceContextsAsync(uow,cohort.RmaId,location.SiteCode,ct);
   saved.AddRange(current.Select(resource=>resource with{Type=type,Id=target}));
  }
  return saved.Distinct().ToArray();
 }
 public async Task<InspectionSubject> ResolveAsync(IKernelUnitOfWork uow,ForgePrincipal principal,AcceptanceSubjectKey key,bool lockStock,CancellationToken ct)
 {
  key=AcceptanceSubjectReference.Normalize(key);
  if(key.ReturnCohortId is{} returnedCohort)return await ResolveReturnAsync(uow,principal,key,returnedCohort,lockStock,ct);
  if(lockStock)await uow.Store<IInventoryStore>().LockPartAsync(key.PartNumber,ct);
  var lookup=uow.Store<IInventoryLookup>();var lot=await lookup.FindLotAsync(key.PartNumber,key.LotNumber,ct)??throw Missing();
  if(lot.PartRevision is null)throw new CommandRejectedException("configuration_unknown","This physical source has no recorded revision; historical coverage is not invented.");
  var location=await lookup.FindLocationAsync(key.LocationCode,ct)??throw Missing();
  var links=await lookup.ListInstanceLinksAsync(null,null,ct);var ancestors=new HashSet<(string Part,string Lot)>();var queue=new Queue<(string Part,string Lot)>([(key.PartNumber,key.LotNumber)]);var usedLinks=new List<InstanceLink>();
  while(queue.TryDequeue(out var at))if(ancestors.Add(at))foreach(var link in links.Where(l=>l.ResultPart==at.Part&&l.ResultLot==at.Lot)){usedLinks.Add(link);queue.Enqueue((link.SourcePart,link.SourceLot));}
  var origins=(await lookup.ListGenealogyTransactionsAsync(ct)).Where(t=>t.LotNumber is{} physical&&ancestors.Contains((t.PartNumber,physical))&&t.Kind is InventoryTransactionKind.Receipt or InventoryTransactionKind.ProductionReceipt or InventoryTransactionKind.Remnant or InventoryTransactionKind.Byproduct).ToArray();
  if(origins.Length==0)throw new CommandRejectedException("subject_origin_unknown","Identify actual original stock sources before recording governed acceptance.");
  var permissionSources=new List<QmsPolicyReadSource>();var actors=new HashSet<string>(StringComparer.Ordinal);var contexts=new List<QmsPolicyContext>();var operations=new HashSet<int>();
  foreach(var origin in origins)
  {
   var context=await OutputContextAsync(uow,principal,key.PartNumber,lot.PartRevision,key.LocationCode,origin.OrderNumber,origin.PoNumber,ct);
   permissionSources.AddRange(context.Sources);actors.UnionWith(context.ProductionActors);actors.Add(origin.Actor);contexts.Add(context.Context);
   if(origin.OrderNumber is{} orderNumber)
   {
    var order=await uow.Store<IExecutionLookup>().FindOrderAsync(orderNumber,ct)??throw Missing();operations.UnionWith(order.Operations.Select(o=>o.Sequence));
   }
  }
  var programs=contexts.Select(c=>c.ProgramId).Distinct().ToArray();var workCenters=contexts.Select(c=>c.WorkCenterCode).Distinct().ToArray();var sequences=contexts.Select(c=>c.OperationSequence).Distinct().ToArray();var activities=contexts.Select(c=>c.ActivityCode).Distinct().ToArray();var machines=contexts.Select(c=>c.MachineCode).Distinct().ToArray();
  var actualContext=new QmsPolicyContext(location.SiteCode,programs.Length==1?programs[0]:null,workCenters.Length==1?workCenters[0]:null,
   ActivityCode:activities.Length==1?activities[0]:null,MachineCode:machines.Length==1?machines[0]:null,
   OperationSequence:sequences.Length==1?sequences[0]:null,PartNumber:key.PartNumber,PartRevision:lot.PartRevision,SubjectKind:lot.IdentityKind==IdentityKind.Serial?"serial":"material");
  // Mixed-source products retain every source permission; a missing common program never makes those sources global.
  var source=new InspectionSourceContext(actualContext,permissionSources.Distinct().ToArray(),actors.Order(StringComparer.Ordinal).ToArray());
  var stock=uow.Store<IInventoryStore>();var inspection=await stock.BalanceAsync(key.PartNumber,key.LocationCode,key.LotNumber,StockStatus.Inspection,ct);var held=await stock.BalanceAsync(key.PartNumber,key.LocationCode,key.LotNumber,StockStatus.Hold,ct);
  var item=await uow.Store<Forge.Engineering.Contracts.IEngineeringLookup>().FindItemAsync(key.PartNumber,ct)??throw Missing();
  return new(key,lot.PartRevision,item.UnitOfMeasure,inspection,held,item.Serialized,source,operations.Order().ToArray(),origins.Select(t=>t.TransactionId).Order().ToArray(),usedLinks.Select(l=>l.LinkId).Distinct().Order().ToArray(),
   ancestors.OrderBy(a=>a.Part,StringComparer.Ordinal).ThenBy(a=>a.Lot,StringComparer.Ordinal).Select(a=>new HoldLot(a.Part,a.Lot)).ToArray(),origins.Where(t=>t.OrderNumber is not null).Select(t=>t.OrderNumber!.Value).Distinct().Order().ToArray(),
   CanonicalJson.Fingerprint(new{key,lot.PartRevision,lot.IdentityKind,origins=origins.Select(t=>new{t.TransactionId,t.OrderNumber,t.PoNumber,t.PartRevision}).OrderBy(t=>t.TransactionId),links=usedLinks.Select(l=>l.LinkId).Distinct().Order(),source.Context}));
 }
 private async Task<InspectionSubject> ResolveReturnAsync(IKernelUnitOfWork uow,ForgePrincipal principal,AcceptanceSubjectKey key,long cohortId,bool lockStock,CancellationToken ct)
 {
  var cohort=await uow.Store<IReturnAcceptanceLookup>().FindCohortAsync(cohortId,ct)??throw Missing();
  if(cohort.Subject!=key||cohort.Version!=1||cohort.Fingerprint!=CanonicalJson.Fingerprint(cohort with{Fingerprint=string.Empty}))throw Missing();
  if(lockStock)await uow.Store<IReturnAcceptanceInventoryLock>().LockRmaAsync(cohort.RmaId,ct);
  var rma=await uow.Store<IReceivingLookup>().FindCustomerReturnAsync(cohort.RmaId,ct)??throw Missing();
  if(rma.ReceiptTransactionId!=cohort.OriginalReturnTransactionId||ReturnCohortIdentity.Fingerprint(rma)!=cohort.OriginalReturnFingerprint||rma.PartNumber!=key.PartNumber||rma.LotNumber!=key.LotNumber||rma.Disposition is CustomerReturnDisposition.Rework or CustomerReturnDisposition.Scrap)throw Missing();
  var preparation=await uow.Store<IReceivingLookup>().FindTransactionAsync(cohort.PreparationTransactionId,ct)??throw Missing();
  if(preparation.Kind!=InventoryTransactionKind.Transfer||preparation.PartNumber!=key.PartNumber||preparation.LotNumber!=key.LotNumber||preparation.Quantity!=cohort.Quantity||preparation.FromStatus!=StockStatus.Hold||preparation.FromLocation!=rma.LocationCode||preparation.ToStatus!=StockStatus.Inspection||preparation.ToLocation!=key.LocationCode||preparation.PartRevision!=cohort.Revision)throw Missing();
  var original=await ResolveAsync(uow,principal,key with{ReturnCohortId=null},lockStock,ct);
  if(original.Revision!=cohort.Revision)throw Missing();
  var current=await returnedSources.ResolveAsync(uow,principal,rma.RmaId,original.Source.Context.FacilityCode,ct);
  foreach(var source in cohort.Sources)Permit(principal,source.Permission,source.Resource);
  var allSources=original.Source.Sources.Concat(current.Sources).Concat(cohort.Sources).Distinct().ToArray();
  var read=uow.Store<IReturnAcceptanceInventoryLookup>();var consumed=await read.ConsumedForCohortAsync(cohortId,ct);var rejected=await read.RejectedForCohortAsync(cohortId,ct);
  var available=cohort.Quantity-consumed-rejected;if(available<0)throw Missing();
  return original with{Key=key,InspectionQuantity=Math.Min(original.InspectionQuantity,available),HoldQuantity=rejected,
   Source=original.Source with{Sources=allSources,ActionResources=current.Resources,ProductionActors=original.Source.ProductionActors.Append(rma.ReceivedBy).Append(cohort.PreparedBy).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()},
   ConfigurationFingerprint=CanonicalJson.Fingerprint(new{Original=original.ConfigurationFingerprint,Cohort=cohort.Fingerprint})};
 }
 public async Task<InspectionSourceContext> OutputContextAsync(IKernelUnitOfWork uow,ForgePrincipal principal,string part,string? revision,string location,long? order,long? purchaseOrder,CancellationToken ct)
 {
  part=BusinessCode.Require(part,"Part");
  var locationRecord=await uow.Store<IInventoryLookup>().FindLocationAsync(BusinessCode.Require(location,"Location"),ct)??throw Missing();
  var reads=new List<QmsPolicyReadSource>();var locationResource=new ResourceRef("Location",locationRecord.Code,FacilityId:locationRecord.SiteCode,WorkCenterId:locationRecord.WorkCenterCode);
  Permit(principal,Permissions.InventoryRead,locationResource);reads.Add(new(Permissions.InventoryRead,locationResource));
  Guid? program=null;string? workCenter=locationRecord.WorkCenterCode;int? sequence=null;string? activity=null;string? machine=null;var actors=new HashSet<string>(StringComparer.Ordinal);
  if(order is{} number)
  {
   var owner=await uow.Store<IExecutionLookup>().FindOrderAsync(number,ct)??throw Missing();
   await SourceAsync(uow,principal,"Order",number.ToString(CultureInfo.InvariantCulture),reads,ct);program=owner.ProgramId;
   var last=owner.Operations.OrderBy(o=>o.Sequence).LastOrDefault();workCenter=last?.WorkCenterCode;sequence=last?.Sequence;activity=last?.ActivityCode;
   var actualMachines=(await uow.Store<IExecutionLookup>().ListLaborAsync(DateTimeOffset.MinValue,DateTimeOffset.MaxValue,number,ct)).Where(l=>l.Sequence==sequence&&l.MachineCode is not null).Select(l=>l.MachineCode!).Distinct(StringComparer.Ordinal).ToArray();
   if(actualMachines.Length==1){machine=actualMachines[0];await SourceAsync(uow,principal,"Machine",machine,reads,ct);}
   foreach(var report in await uow.Store<IExecutionLookup>().ListReportsAsync(DateTimeOffset.MinValue,DateTimeOffset.MaxValue,number,ct))actors.Add("person:"+report.PersonId);
  }
  if(purchaseOrder is{} po)
  {
   await SourceAsync(uow,principal,"PurchaseOrder",po.ToString(CultureInfo.InvariantCulture),reads,ct);
   var owning=await sources.ResolveAsync(uow,"PurchaseOrder",po.ToString(CultureInfo.InvariantCulture),ct)??throw Missing();
   var programs=owning.Resources.Where(r=>r.Program is not null).Select(r=>r.Program!.Value.Value).Distinct().ToArray();program=programs.Length==1?programs[0]:null;
  }
  return new(new(locationRecord.SiteCode,program,workCenter,activity,MachineCode:machine,OperationSequence:sequence,PartNumber:part,PartRevision:revision),reads.Distinct().ToArray(),actors.Order(StringComparer.Ordinal).ToArray());
 }
 public async Task<long> HoldRejectedAsync(IKernelUnitOfWork uow,InspectionSubject subject,decimal quantity,string quarantineLocation,string actor,string reason,DateTimeOffset at,CancellationToken ct)
 {
  var quarantine=await uow.Store<IInventoryLookup>().FindLocationAsync(BusinessCode.Require(quarantineLocation,"Quarantine location"),ct)??throw Missing();
  if(!quarantine.Active||!quarantine.InCompanyCustody||quarantine.Kind!=LocationKind.Quarantine||quarantine.SiteCode!=subject.Source.Context.FacilityCode)throw new CommandRejectedException("quarantine_required","Rejected identified output requires a real active quarantine location in its facility.");
  var store=uow.Store<IInventoryStore>();if(await store.BalanceAsync(subject.Key.PartNumber,subject.Key.LocationCode,subject.Key.LotNumber,StockStatus.Inspection,ct)<quantity)throw new CommandRejectedException("insufficient_stock","The exact Inspection quantity is no longer present.");
  return await store.InsertTransactionAsync(new(InventoryTransactionKind.QualityHold,subject.Key.PartNumber,quantity,subject.Key.LocationCode,StockStatus.Inspection,quarantine.Code,StockStatus.Hold,subject.Key.LotNumber,Reason:reason,PartRevision:subject.Revision),at,actor,ct);
 }
 private async Task SourceAsync(IKernelUnitOfWork uow,ForgePrincipal p,string type,string id,List<QmsPolicyReadSource> target,CancellationToken ct)
 {
  var source=await sources.ResolveAsync(uow,type,id,ct)??throw Missing();if(source.Resources.Count==0)throw Missing();foreach(var resource in source.Resources){Permit(p,source.ReadPermission,resource);target.Add(new(source.ReadPermission,resource));}
 }
 private void Permit(ForgePrincipal principal,string permission,ResourceRef resource){var decision=authorizer.Authorize(principal,permission,resource);if(!decision.IsPermitted)throw new ForgeAuthorizationException(decision,resource);}
 private static CommandRejectedException Missing()=>new("inspection_subject_unavailable","The exact current physical subject/source is unavailable.");
}
