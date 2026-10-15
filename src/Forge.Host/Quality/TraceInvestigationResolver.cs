using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Holds;
using Forge.Kernel.Security;
using Forge.Kernel.WorkAssignments;
using Forge.Quality.Acceptance;
using Forge.Quality.Contracts;
using Forge.Quality.Investigations;
using Forge.Resources.Contracts;
using Forge.Planning;
using Forge.Kernel.Identity;
using Forge.Workforce.Contracts;

namespace Forge.Host.Quality;

/// <summary>Owner metadata is used to traverse privately; each returned source is checked against current and captured scopes.</summary>
public sealed partial class TraceInvestigationResolver(IWorkSourceResolver workSources,IForgeAuthorizer authorizer,IServiceProvider services,TimeProvider time):ITraceInvestigationResolver
{
 public async Task<IReadOnlyList<TraceSourceReference>> FindingSourcesAsync(IKernelUnitOfWork uow,ForgePrincipal principal,MeasurementFindingContent content,CancellationToken ct)
 {
  var code=Forge.Kernel.Identity.BusinessCode.Require(content.ToolCode,"Tool");var tool=await uow.Store<IToolLookup>().FindToolAsync(code,ct)??throw Missing();
  var result=new List<TraceSourceReference>{new("Tool",tool.Code,Permissions.ResourcesRead,[ResourceRef.Of("Tool",tool.Code)])};
  if(content.CalibrationEventId is{} eventId)
  {
   var recorded=(await uow.Store<IToolLookup>().ListToolEventsAsync(code,ct)).SingleOrDefault(entry=>entry.EventId==eventId);
   if(recorded is null||recorded.Kind is not(ToolEventKind.Calibrated or ToolEventKind.CalibrationFailed))throw new CommandRejectedException("calibration_event_not_found","Select the actual calibration/verification event for this measuring resource.");
   result.Add(new("ToolEvent",code+"/"+Text(eventId),Permissions.ResourcesRead,[ResourceRef.Of("Tool",code)],false));
  }
  await RequireCurrentReadsAsync(uow,principal,result,ct);return result;
 }
 public async Task<IReadOnlyList<TraceSourceReference>> RootSourcesAsync(IKernelUnitOfWork uow,ForgePrincipal principal,TraceInvestigationRoot root,CancellationToken ct)
 {
  InvestigationRules.Root(root);IReadOnlyList<TraceSourceReference> result;
  if(root.Kind==TraceTriggerKind.SupplierLot)result=await LotSourcesAsync(uow,root.PartNumber!,root.LotNumber!,ct);
  else
  {
   var finding=await uow.Store<IQualityInvestigationLookup>().FindFindingAsync(root.FindingId!.Value,ct)??throw Missing();
   if(finding.State!=MeasurementFindingState.Approved)throw new CommandRejectedException("approved_finding_required","Use an independently reviewed measurement finding and its actual suspected interval.");
   var resources=InvestigationService.Resources(await FindingSourcesAsync(uow,principal,finding.Content,ct),"MeasurementFinding",finding.FindingId);
   result=[new("MeasurementFinding",Text(finding.FindingId),Permissions.QualityRead,resources)];
  }
  await RequireCurrentReadsAsync(uow,principal,result,ct);return result;
 }
 public async Task RequireCurrentReadsAsync(IKernelUnitOfWork uow,ForgePrincipal principal,IReadOnlyList<TraceSourceReference> sources,CancellationToken ct)
 {
  principal=await uow.People.LoadPrincipalAsync(principal.PersonId,ct)??principal with{IsActive=false};
  foreach(var reference in sources)
  {
   if(reference.Resources.Count==0)throw Missing();foreach(var resource in reference.Resources)Permit(principal,reference.ReadPermission,resource);
   var current=await CurrentSourcesAsync(uow,reference.Type,reference.Id,ct);if(current.Count==0)throw Missing();
   foreach(var source in current)foreach(var resource in source.Resources)Permit(principal,source.ReadPermission,resource);
   if(reference.Type is "AcceptanceInspection" or "ProductReleaseDecision")
   {if(!await services.GetRequiredService<ProductAcceptanceService>().CanReadAsync(uow,principal,Number(reference.Id),reference.Type=="ProductReleaseDecision",ct))throw Missing();}
   if(reference.Type=="MeasurementFinding"&&!await services.GetRequiredService<InvestigationReadService>().CanReadFindingAsync(uow,principal,Number(reference.Id),ct))throw Missing();
  }
 }
 public async Task<bool> CanReadAsync(IKernelUnitOfWork uow,ForgePrincipal principal,IReadOnlyList<TraceSourceReference> sources,CancellationToken ct)
 {try{await RequireCurrentReadsAsync(uow,principal,sources,ct);return true;}catch(ForgeAuthorizationException){return false;}catch(CommandRejectedException){return false;}}
 public async Task<IReadOnlyList<TraceSourceReference>> CurrentContextsAsync(IKernelUnitOfWork uow,IReadOnlyList<TraceSourceReference> sources,CancellationToken ct)
 {var current=new List<TraceSourceReference>();foreach(var reference in sources){var rows=await CurrentSourcesAsync(uow,reference.Type,reference.Id,ct);current.AddRange(rows.Select(row=>row with{ActionScope=reference.ActionScope&&row.ActionScope}));}return current.Concat(sources).Distinct().ToArray();}
 public async Task RequireCandidateActionAsync(IKernelUnitOfWork uow,ForgePrincipal principal,TraceCandidate candidate,string permission,CancellationToken ct)
 {
  principal=await uow.People.LoadPrincipalAsync(principal.PersonId,ct)??principal with{IsActive=false};await RequireCurrentReadsAsync(uow,principal,candidate.Sources,ct);
  foreach(var resource in candidate.ActionResources)Permit(principal,permission,resource);
  foreach(var source in candidate.Sources.Where(source=>source.ActionScope))foreach(var current in await CurrentSourcesAsync(uow,source.Type,source.Id,ct))foreach(var resource in current.Resources)Permit(principal,permission,resource with{Type=candidate.RecordType,Id=candidate.RecordId});
 }
 public async Task<Hold> RequireMatchingHoldAsync(IKernelUnitOfWork uow,ForgePrincipal principal,TraceCandidate candidate,long holdId,int expectedVersion,CancellationToken ct)
 {
  var hold=await uow.Store<IHoldStore>().LockHoldAsync(holdId,ct)??throw Missing();
  if(!hold.Active||hold.RecordVersion!=expectedVersion||!candidate.HoldTargets.Any(target=>target.Type==hold.TargetType&&target.Id==hold.TargetId&&(target.Type!=HoldTargetType.Lot||target.Part==hold.TargetPart)))throw new CommandRejectedException("hold_scope_mismatch","Link the actual active hold/version on this exact recorded candidate; linking never creates or releases a hold.");
  var source=await workSources.ResolveAsync(uow,"Hold",Text(holdId),ct)??throw Missing();foreach(var resource in source.Resources)Permit(principal,Permissions.HoldsRead,resource);return hold;
 }
 private async Task<IReadOnlyList<TraceSourceReference>> CurrentSourcesAsync(IKernelUnitOfWork uow,string type,string id,CancellationToken ct)
 {
  if(type=="PartRevision")
  {var key=Physical(id);if(await uow.Store<IEngineeringLookup>().GetRevisionDetailAsync(key.Part,key.Lot,ct) is null)throw Missing();return[new(type,id,Permissions.EngineeringRead,[ResourceRef.Of(type,id)],false)];}
  if(type=="Worker")
  {
   if(!Guid.TryParse(id,out var workerId)||await uow.People.FindAsync(new PersonId(workerId),ct) is null||!await uow.Store<IWorkforceLookup>().WorkerExistsAsync(workerId,ct))throw Missing();
   var worker=(await uow.Store<IWorkforceLookup>().GetWorkerStatesAsync(time.GetUtcNow(),ct)).SingleOrDefault(row=>row.PersonId==workerId);
   if(worker is null)throw Missing();var center=await uow.Store<IResourcesLookup>().FindRecordedWorkCenterAsync(worker.HomeWorkCenterCode,ct)??throw Missing();var area=await uow.Store<IResourcesLookup>().FindRecordedSafetyAreaAsync(center.SafetyAreaCode,ct)??throw Missing();
   return[new(type,id,Permissions.WorkforceRead,[new(type,id,FacilityId:area.SiteCode,WorkCenterId:center.Code)])];
  }
  if(type=="Demand")
  {
   var demand=await uow.Store<IDemandStore>().FindAsync(Number(id),ct)??throw Missing();
   return[new(type,id,Permissions.MaterialPlanRead,[new(type,id,demand.ProgramId is{} program?new ProgramId(program):null,CustomerId:demand.Customer)])];
  }
  if(type=="Lot"){var key=Physical(id);return await LotSourcesAsync(uow,key.Part,key.Lot,ct);}
  if(type=="Tool")
  {if(await uow.Store<IToolLookup>().FindToolAsync(id,ct) is null)throw Missing();return[new(type,id,Permissions.ResourcesRead,[ResourceRef.Of(type,id)])];}
  if(type=="ToolEvent")
  {var values=id.Split('/');if(values.Length!=2||(await uow.Store<IToolLookup>().ListToolEventsAsync(values[0],ct)).All(entry=>entry.EventId!=Number(values[1])))throw Missing();return[new("Tool",values[0],Permissions.ResourcesRead,[ResourceRef.Of("Tool",values[0])],false)];}
  if(type=="Location")
  {var location=await uow.Store<IInventoryLookup>().FindLocationAsync(id,ct)??throw Missing();return[new(type,id,Permissions.InventoryRead,[new(type,id,FacilityId:location.SiteCode,WorkCenterId:location.WorkCenterCode)])];}
  if(type=="InventoryTransaction")
  {var transaction=await uow.Store<IReceivingLookup>().FindTransactionAsync(Number(id),ct)??throw Missing();return await TransactionSourcesAsync(uow,transaction,ct);}
  if(type=="Transformation")
  {var transformation=(await uow.Store<IInventoryLookup>().ListTransformationsAsync(null,null,null,ct)).SingleOrDefault(record=>record.TransformationId==Number(id))??throw Missing();var order=await ExistingAsync(uow,"Order",Text(transformation.OrderNumber),ct);return[new(type,id,Permissions.InventoryRead,order.Resources.Select(resource=>resource with{Type=type,Id=id}).ToArray()),order with{ActionScope=false}];}
  if(type=="Shipment")
  {var shipment=await uow.Store<IInventoryLookup>().FindShipmentAsync(Number(id),ct)??throw Missing();return await ShipmentSourcesAsync(uow,shipment,ct);}
  if(type=="Container")
  {var container=await uow.Store<IInventoryLookup>().FindContainerAsync(id,ct)??throw Missing();var all=new List<TraceSourceReference>();foreach(var line in container.Contents){if(line.LotNumber is null)throw Missing();all.AddRange(await LotSourcesAsync(uow,line.PartNumber,line.LotNumber,ct));}var scopes=all.Where(source=>source.ActionScope).SelectMany(source=>source.Resources).Select(resource=>resource with{Type=type,Id=id}).Distinct().ToArray();return[new(type,id,Permissions.InventoryRead,scopes),..all.Select(source=>source with{ActionScope=false})];}
  if(type is "AcceptanceInspection" or "ProductReleaseDecision")
  {
   var lookup=uow.Store<IAcceptanceLookup>();InspectionSubject subject;
   if(type=="AcceptanceInspection")subject=(await lookup.FindInspectionAsync(Number(id),ct)??throw Missing()).Subject;
   else{var decision=await lookup.FindDecisionAsync(Number(id),ct)??throw Missing();var inspection=await lookup.FindInspectionAsync(decision.InspectionIds[0],ct)??throw Missing();subject=inspection.Subject;}
   var current=await services.GetRequiredService<IInspectionSubjectResolver>().ResourceContextsAsync(uow,subject,type,Number(id),ct);
   return[new(type,id,Permissions.QualityRead,current),..subject.Source.Sources.Select(source=>new TraceSourceReference(source.Resource.Type,source.Resource.Id??throw Missing(),source.Permission,[source.Resource],false))];
  }
  if(type=="MeasurementFinding")
  {var finding=await uow.Store<IQualityInvestigationLookup>().FindFindingAsync(Number(id),ct)??throw Missing();var resources=finding.Sources.Select(source=>source.Resource with{Type=type,Id=id}).ToArray();return[new(type,id,Permissions.QualityRead,resources)];}
  var owner=await workSources.ResolveAsync(uow,type,id,ct);
  if(owner is not null&&owner.Resources.Count>0)return[new(type,id,owner.ReadPermission,owner.Resources)];
  // Controlled documents/policy sources use their existing current owner resource registry. Unknown source types do not become public.
  throw Missing();
 }
 private async Task<TraceSourceReference> ExistingAsync(IKernelUnitOfWork uow,string type,string id,CancellationToken ct)
 {var owner=await workSources.ResolveAsync(uow,type,id,ct)??throw Missing();if(owner.Resources.Count==0)throw Missing();return new(type,id,owner.ReadPermission,owner.Resources);}
 private async Task<IReadOnlyList<TraceSourceReference>> LotSourcesAsync(IKernelUnitOfWork uow,string part,string lot,CancellationToken ct)
 {
  part=Forge.Kernel.Identity.BusinessCode.Require(part,"Part");lot=AcceptanceSubjectReference.Normalize(new(part,lot,"TRACE")).LotNumber;
  var physical=await uow.Store<IInventoryLookup>().FindLotAsync(part,lot,ct)??throw Missing();
  var allLinks=await uow.Store<IInventoryLookup>().ListInstanceLinksAsync(null,null,ct);var ancestors=new HashSet<(string Part,string Lot)>();var queue=new Queue<(string Part,string Lot)>([(part,lot)]);
  while(queue.TryDequeue(out var key))if(ancestors.Add(key))foreach(var link in allLinks.Where(link=>link.ResultPart==key.Part&&link.ResultLot==key.Lot))queue.Enqueue((link.SourcePart,link.SourceLot));
  var transactions=await uow.Store<ITraceInventoryLookup>().TransactionsAtCutoffAsync(DateTimeOffset.MaxValue,ct);var sources=new List<TraceSourceReference>();
  foreach(var transaction in transactions.Where(transaction=>transaction.LotNumber is{} identity&&ancestors.Contains((transaction.PartNumber,identity))&&(transaction.Kind is InventoryTransactionKind.Receipt or InventoryTransactionKind.ProductionReceipt or InventoryTransactionKind.Remnant or InventoryTransactionKind.Byproduct)))
  {if(transaction.OrderNumber is{} order)sources.Add((await ExistingAsync(uow,"Order",Text(order),ct)) with{ActionScope=false});if(transaction.PoNumber is{} po)sources.Add((await ExistingAsync(uow,"PurchaseOrder",Text(po),ct)) with{ActionScope=false});}
  var id=PhysicalId(part,lot);var inherited=sources.SelectMany(source=>source.Resources).Select(resource=>resource with{Type="Lot",Id=id}).Distinct().ToArray();if(inherited.Length==0)inherited=[ResourceRef.Of("Lot",id)];
  if(physical.PartRevision is{} revision)sources.Add(new("PartRevision",PhysicalId(part,revision),Permissions.EngineeringRead,[ResourceRef.Of("PartRevision",PhysicalId(part,revision))],false));
  return[new("Lot",id,Permissions.InventoryRead,inherited),..sources.Distinct()];
 }
 private async Task<IReadOnlyList<TraceSourceReference>> TransactionSourcesAsync(IKernelUnitOfWork uow,InventoryTransaction transaction,CancellationToken ct)
 {
  var related=new List<TraceSourceReference>();if(transaction.LotNumber is{} lot)related.AddRange(await LotSourcesAsync(uow,transaction.PartNumber,lot,ct));
  if(transaction.OrderNumber is{} order)related.Add(await ExistingAsync(uow,"Order",Text(order),ct));if(transaction.PoNumber is{} po)related.Add(await ExistingAsync(uow,"PurchaseOrder",Text(po),ct));
  var id=Text(transaction.TransactionId);var scopes=related.Where(source=>source.ActionScope).SelectMany(source=>source.Resources).Select(resource=>resource with{Type="InventoryTransaction",Id=id}).Distinct().ToArray();if(scopes.Length==0)scopes=[ResourceRef.Of("InventoryTransaction",id)];
  return[new("InventoryTransaction",id,Permissions.InventoryRead,scopes),..related.Select(source=>source with{ActionScope=false})];
 }
 private async Task<IReadOnlyList<TraceSourceReference>> ShipmentSourcesAsync(IKernelUnitOfWork uow,ShipmentInfo shipment,CancellationToken ct)
 {
  var sources=new List<TraceSourceReference>();foreach(var line in shipment.Lines)if(line.LotNumber is{} lot)sources.AddRange(await LotSourcesAsync(uow,line.PartNumber,lot,ct));
  var id=Text(shipment.ShipmentId);var scopes=sources.Where(source=>source.ActionScope).SelectMany(source=>source.Resources).Select(resource=>resource with{Type="Shipment",Id=id,CustomerId=shipment.Customer}).Distinct().ToArray();if(scopes.Length==0)scopes=[new("Shipment",id,CustomerId:shipment.Customer)];
  return[new("Shipment",id,Permissions.InventoryRead,scopes),..sources.Select(source=>source with{ActionScope=false})];
 }
 private void Permit(ForgePrincipal principal,string permission,ResourceRef resource){var decision=authorizer.Authorize(principal,permission,resource);if(!decision.IsPermitted)throw new ForgeAuthorizationException(decision,resource);}
 private static string PhysicalId(string part,string lot)=>Uri.EscapeDataString(part)+"/"+Uri.EscapeDataString(lot);
 private static (string Part,string Lot) Physical(string id){var slash=id.IndexOf('/');if(slash<=0||slash==id.Length-1)throw Missing();return(Uri.UnescapeDataString(id[..slash]),Uri.UnescapeDataString(id[(slash+1)..]));}
 private static long Number(string id)=>long.TryParse(id,NumberStyles.None,CultureInfo.InvariantCulture,out var value)&&value>0?value:throw Missing();
 private static string Text(long id)=>id.ToString(CultureInfo.InvariantCulture);
 private static CommandRejectedException Missing()=>new("investigation_source_unavailable","The exact recorded investigation source is unavailable.");
}
