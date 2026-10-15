using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Holds;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Planning;
using Forge.Quality.Contracts;
using Forge.Quality.Investigations;

namespace Forge.Host.Quality;

public sealed partial class TraceInvestigationResolver
{
 private sealed record Node(string Type,string Id,string Category,string? Part=null,string? Lot=null,long? Order=null,int? Sequence=null,
  long? Shipment=null,string? Location=null,string? Status=null,decimal? Quantity=null,string? Unit=null,string? Customer=null,string? Revision=null);
 public async Task<TraceCapture> CaptureAsync(IKernelUnitOfWork uow,ForgePrincipal principal,TraceInvestigationRoot root,TraceSnapshotBoundary boundary,CancellationToken ct)
 {
  var cutoff=boundary.CapturedAt;
  var rootSources=await RootSourcesAsync(uow,principal,root,ct);var inventory=uow.Store<IInventoryLookup>();var quality=uow.Store<IQualityInvestigationLookup>();
  var transactions=await uow.Store<ITraceInventoryLookup>().TransactionsAtCutoffAsync(cutoff,ct);
  var links=(await inventory.ListInstanceLinksAsync(null,null,ct)).Where(link=>link.At<=cutoff).ToArray();
  var transformations=(await inventory.ListTransformationsAsync(null,null,null,ct)).Where(record=>record.RecordedAt<=cutoff).ToArray();
  var nodes=new Dictionary<string,Node>(StringComparer.Ordinal);var edges=new List<TraceGraphLink>();var gaps=new List<TraceGap>();var roots=new List<string>();var retainedOwnerValues=new Dictionary<(string Type,string Id),string>();
  string Add(Node node){var key=node.Type+":"+node.Id;nodes.TryAdd(key,node);return key;}
  string Lot(string part,string lot)=>Add(new("Lot",PhysicalId(part,lot),"PhysicalIdentity",part,lot));
  string Order(long order)=>Add(new("Order",Text(order),"Wip",Order:order));
  void Edge(string from,string to,string type,string id,string relationship,decimal? quantity,DateTimeOffset? at,string basis)=>edges.Add(new(from,to,new(type,id,relationship,quantity,null,at,basis,[])));
  foreach(var link in links)Edge(Lot(link.SourcePart,link.SourceLot),Lot(link.ResultPart,link.ResultLot),"InstanceLink",Text(link.LinkId),link.Relation.ToString(),link.Quantity,link.At,"Exact immutable source/result and paired ledger transaction IDs.");
  foreach(var issued in transactions.Where(transaction=>transaction.OrderNumber is not null&&transaction.LotNumber is not null&&transaction.Kind==InventoryTransactionKind.Issue).GroupBy(transaction=>(Order:transaction.OrderNumber!.Value,Part:transaction.PartNumber,Lot:transaction.LotNumber!)))
  {
   var net=issued.Sum(transaction=>transaction.Quantity)-transactions.Where(transaction=>transaction.Kind==InventoryTransactionKind.Return&&transaction.OrderNumber==issued.Key.Order&&transaction.PartNumber==issued.Key.Part&&transaction.LotNumber==issued.Key.Lot).Sum(transaction=>transaction.Quantity);
   if(net>0)foreach(var transaction in issued)Edge(Lot(transaction.PartNumber,transaction.LotNumber!),Order(transaction.OrderNumber!.Value),"InventoryTransaction",Text(transaction.TransactionId),"ActualIssueToOrder",transaction.Quantity,transaction.At,"Actual issue; aggregate net issue remains positive after recorded returns. Potential downstream association does not assert a defect or quantity conversion.");
  }
  foreach(var transaction in transactions.Where(transaction=>transaction.Kind is InventoryTransactionKind.ProductionReceipt or InventoryTransactionKind.Remnant or InventoryTransactionKind.Byproduct&&transaction.OrderNumber is not null))
  {
   var from=Order(transaction.OrderNumber!.Value);var to=transaction.LotNumber is{} lot?Lot(transaction.PartNumber,lot):Add(new("InventoryTransaction",Text(transaction.TransactionId),"UnidentifiedOutput",transaction.PartNumber,Order:transaction.OrderNumber,Location:transaction.ToLocation,Quantity:transaction.Quantity));
   Edge(from,to,"InventoryTransaction",Text(transaction.TransactionId),transaction.Kind.ToString(),transaction.Quantity,transaction.At,"Actual recorded output; it is a potential trace candidate, not an automatic nonconformance.");
   if(transaction.LotNumber is null)gaps.Add(new("unidentified_output","Some recorded output has no physical lot/serial binding; an exact product cohort cannot be invented."));
  }
  foreach(var transformation in transformations)foreach(var line in transformation.Lines.Where(line=>line.Role is TransformationRole.Remnant or TransformationRole.Byproduct&&line.LotNumber is not null))
  {var to=Lot(line.PartNumber,line.LotNumber!);Edge(Order(transformation.OrderNumber),to,"Transformation",Text(transformation.TransformationId),line.Role.ToString(),line.Quantity,transformation.RecordedAt,"Actual transformation line "+line.LineNumber+"; recorded output configuration remains authoritative.");if(line.SourcePart is{} part&&line.SourceLot is{} lot)Edge(Lot(part,lot),to,"Transformation",Text(transformation.TransformationId),"RecordedTransformationSource",line.Quantity,transformation.RecordedAt,"Exact explicit transformation source; no inferred consumption mass conversion.");}
  var intervalKnown=true;
  if(root.Kind==TraceTriggerKind.SupplierLot)roots.Add(Lot(root.PartNumber!,root.LotNumber!));
  else
  {
   var finding=await quality.FindFindingAsync(root.FindingId!.Value,ct)??throw Missing();var measurement=finding.Content;intervalKnown=measurement.SuspectedFrom is not null&&measurement.SuspectedTo is not null;
   if(!intervalKnown)gaps.Add(new("suspected_interval_unknown","A suspected interval bound is unknown; every recorded matching measurement up to the cutoff is conservatively considered, without claiming the interval is complete."));
   var trigger=Add(new("MeasurementFinding",Text(finding.FindingId),"Trigger"));roots.Add(trigger);long after=0;
   while(true)
   {
    var page=await quality.LegacyInspectionIdsForToolAsync(measurement.ToolCode,measurement.SuspectedFrom,measurement.SuspectedTo,cutoff,after,1000,ct);if(page.Count==0)break;
    foreach(var id in page){var inspection=await uow.Store<IQualityLookup>().FindInspectionAsync(id,ct)??throw Missing();retainedOwnerValues[("Inspection",Text(id))]=CanonicalJson.Serialize(inspection);var node=Add(new("Inspection",Text(id),"Inspection",inspection.PartNumber,Order:inspection.OrderNumber,Sequence:inspection.Sequence,Status:inspection.Result.ToString(),Quantity:inspection.QuantityInspected));Edge(trigger,node,"Inspection",Text(id),"RecordedGaugeInspection",inspection.QuantityInspected,inspection.At,"Actual legacy inspection/gauge/time and frozen observed limits; nominal, unit, physical cohort/sample and typed use-event binding may be unknown.");Edge(node,Order(inspection.OrderNumber),"Inspection",Text(id),"InspectionForOrder",inspection.QuantityInspected,inspection.At,"Potential order output; this does not declare every produced/shipped unit defective.");}
    after=page[^1];
   }
   after=0;
   while(true)
   {
    var page=await quality.AcceptanceInspectionsForToolAsync(measurement.ToolCode,measurement.SuspectedFrom,measurement.SuspectedTo,cutoff,after,1000,ct);if(page.Count==0)break;
    foreach(var inspection in page)
    {retainedOwnerValues[("AcceptanceInspection",Text(inspection.InspectionId))]=CanonicalJson.Serialize(inspection);var node=Add(new("AcceptanceInspection",Text(inspection.InspectionId),"Inspection",inspection.Subject.Key.PartNumber,inspection.Subject.Key.LotNumber,Location:inspection.Subject.Key.LocationCode,Status:inspection.Passed?"Passed":"Failed",Quantity:inspection.Quantity,Unit:inspection.Subject.Unit));Edge(trigger,node,"AcceptanceInspection",Text(inspection.InspectionId),"RecordedGaugeInspection",inspection.Quantity,inspection.At,"Exact frozen acceptance arrangement, sample/represented coverage, observed values/unit/nominal/limits and original as-of validity remain unchanged.");Edge(node,Lot(inspection.Subject.Key.PartNumber,inspection.Subject.Key.LotNumber),"AcceptanceInspection",Text(inspection.InspectionId),"InspectedPhysicalCohort",inspection.Quantity,inspection.At,"Exact inspection physical binding; potentially associated output requires human impact disposition.");foreach(var result in inspection.Results.Where(result=>result.GaugeCode==measurement.ToolCode&&result.ToolUseEventId is not null))Edge(node,Add(new("ToolEvent",measurement.ToolCode+"/"+Text(result.ToolUseEventId!.Value),"MeasurementEvidence")),"ToolEvent",Text(result.ToolUseEventId!.Value),"RecordedMeasuringUse",null,result.HistoricValidity?.AsOf,"Actual returned immutable ToolUse event ID, separate from later calibration failure.");}
    after=page[^1].InspectionId;
   }
   if(edges.All(edge=>edge.From!=trigger))gaps.Add(new("no_recorded_measurement_binding","No matching product measurement binding is recorded; unrecorded physical use is unknown, not a proof that no product was affected."));
   if(nodes.Values.Any(node=>node.Type=="Inspection"))gaps.Add(new("legacy_measurement_coverage_unknown","Legacy measurements may lack nominal/unit, sampling, exact lot/serial and typed measuring-use evidence; their original limits/results are preserved."));
  }
  var traversal=TraceGraphEngine.Forward(roots,edges);var relevantLots=traversal.Nodes.Where(key=>nodes[key].Type=="Lot").Select(key=>nodes[key]).ToArray();
  var tasks=await inventory.ListMaterialTasksAsync(false,null,ct);
  foreach(var task in tasks.Where(task=>task.RequestedAt<=cutoff&&task.LotNumber is not null&&relevantLots.Any(lot=>lot.Part==task.PartNumber&&lot.Lot==task.LotNumber)))
  {var node=Add(new("MaterialTask",Text(task.TaskId),task.Status is MaterialTaskStatus.Allocated or MaterialTaskStatus.Picked?"AllocatedOrStaged":"MaterialWork",task.PartNumber,task.LotNumber,task.OrderNumber,Location:task.ToLocation,Status:task.Status.ToString(),Quantity:task.PickedQuantity??task.Quantity));Edge(Lot(task.PartNumber,task.LotNumber!),node,"MaterialTask",Text(task.TaskId),task.Status.ToString(),task.PickedQuantity??task.Quantity,task.PickedAt??task.AllocatedAt??task.RequestedAt,"Actual task/lot/order/source/destination; current task status is observed, not backdated to the cutoff.");}
  foreach(var group in relevantLots.GroupBy(lot=>lot.Part!))
  {
   var packing=await inventory.PackingForLotsAsync(group.Key,group.Select(lot=>lot.Lot!).Distinct(StringComparer.Ordinal).ToArray(),ct);
   foreach(var container in packing.Containers.Where(container=>container.CreatedAt<=cutoff))foreach(var line in container.Contents.Where(line=>line.LotNumber is not null&&group.Any(lot=>lot.Lot==line.LotNumber)))
   {var key=Add(new("Container",container.Code,"PackedProduct",line.PartNumber,line.LotNumber,Shipment:container.ShipmentId,Location:line.LocationCode,Status:container.Status.ToString(),Quantity:line.Quantity));Edge(Lot(line.PartNumber,line.LotNumber!),key,"Container",container.Code,"RecordedPhysicalPacking",line.Quantity,container.CreatedAt,"Actual uncapped owner packing lookup. Current container state is observed; closure is not inferred from stock.");}
   foreach(var shipment in packing.Shipments.Where(shipment=>shipment.CreatedAt<=cutoff))
   {
    var key=Add(new("Shipment",Text(shipment.ShipmentId),shipment.Status==ShipmentStatus.Shipped?"ShippedProduct":"PlannedShipment",Shipment:shipment.ShipmentId,Status:shipment.Status.ToString(),Customer:shipment.Customer));
    foreach(var line in shipment.Lines.Where(line=>line.LotNumber is not null&&line.PartNumber==group.Key&&group.Any(lot=>lot.Lot==line.LotNumber)))Edge(Lot(line.PartNumber,line.LotNumber!),key,"Shipment",Text(shipment.ShipmentId),shipment.Status==ShipmentStatus.Shipped?"ActualCustomerShipment":"RecordedShipmentAllocation",line.Quantity,shipment.ShippedAt??shipment.CreatedAt,"Actual shipment/line/customer association; an open allocation is not an actual shipment.");
    if(shipment.DemandId is{} demandId)
    {var demand=await uow.Store<IDemandStore>().FindAsync(demandId,ct);if(demand is null)gaps.Add(new("customer_order_source_missing","A recorded shipment demand reference is unavailable; no customer order is invented."));else{var demandKey=Add(new("Demand",Text(demandId),demand.Kind==DemandKind.CustomerOrder||demand.Kind==DemandKind.ContractDelivery?"CustomerOrder":"Demand",demand.PartNumber,Status:demand.Status.ToString(),Quantity:demand.Quantity,Customer:demand.Customer));Edge(key,demandKey,"Shipment",Text(shipment.ShipmentId),"ExactRecordedDemand",null,shipment.CreatedAt,"Actual shipment DemandId, not a customer-name/text match.");}}
   }
  }
  var relatedOrders=traversal.Nodes.Select(key=>nodes[key]).Where(node=>node.Order is not null).Select(node=>node.Order!.Value).Distinct().ToArray();
  foreach(var orderId in relatedOrders)
  {
   var order=await uow.Store<IExecutionLookup>().FindOrderAsync(orderId,ct)??throw Missing();nodes[Order(orderId)]=new("Order",Text(orderId),"Wip",order.PartNumber,Order:orderId,Status:order.Status.ToString(),Quantity:order.Quantity);
   foreach(var operation in order.Operations.Where(operation=>operation.StartedAt is{} started&&started<=cutoff))
   {var key=Add(new("OrderOperation",Text(orderId)+"/"+operation.Sequence,"Operation",order.PartNumber,Order:orderId,Sequence:operation.Sequence,Status:operation.Status.ToString(),Quantity:operation.GoodQuantity));Edge(Order(orderId),key,"OrderOperation",Text(orderId)+"/"+operation.Sequence,"ActualCopiedOperation",operation.GoodQuantity,operation.StartedAt,"Released copied operation/revision; current aggregate state is observed separately from historic source events.");}
   foreach(var labor in (await uow.Store<IExecutionLookup>().ListLaborAsync(DateTimeOffset.MinValue,cutoff.AddTicks(1),orderId,ct)).Where(labor=>labor.StartedAt<=cutoff))
   {
    var operation=Add(new("OrderOperation",Text(orderId)+"/"+labor.Sequence,"Operation",order.PartNumber,Order:orderId,Sequence:labor.Sequence));Edge(Order(orderId),operation,"Labor",Text(labor.LaborId),"ActualOperationLabor",null,labor.StartedAt,"Actual labor record; routing eligibility is not an actual machine assignment.");
    var person=Add(new("Worker",labor.PersonId.ToString(),"OperatorEvidence"));Edge(operation,person,"Labor",Text(labor.LaborId),"RecordedOperator",null,labor.StartedAt,"Actual worker/labor ID and role, not shift/presence inferred as labor.");
    if(labor.MachineCode is{} machine){var equipment=Add(new("Machine",machine,"EquipmentEvidence"));Edge(operation,equipment,"Labor",Text(labor.LaborId),"RecordedMachine",null,labor.StartedAt,"Exact actual labor MachineCode; alternative eligible machines are not reported as used.");}
   }
  }
  traversal=TraceGraphEngine.Forward(roots,edges);var captured=new List<TraceCandidate>();var readable=new HashSet<string>(StringComparer.Ordinal);var restricted=false;
  foreach(var key in traversal.Nodes.Order(StringComparer.Ordinal))
  {
   var node=nodes[key];if(node.Category=="Trigger")continue;if(node.Type=="Lot"){var physical=await inventory.FindLotAsync(node.Part!,node.Lot!,ct);var item=await uow.Store<IEngineeringLookup>().FindItemAsync(node.Part!,ct);node=node with{Revision=physical?.PartRevision,Unit=item?.UnitOfMeasure};if(node.Revision is null)gaps.Add(new("configuration_unknown","A recorded physical identity lacks its historic configuration; no revision is inferred."));}IReadOnlyList<TraceSourceReference> refs;
   try
   {
    refs=await CurrentSourcesAsync(uow,node.Type,node.Id,ct);
    if(node.Type is "Worker" or "Machine" or "ToolEvent" or "Demand")
    {
     var contexts=new List<TraceSourceReference>();foreach(var parent in traversal.Edges.Where(edge=>edge.To==key).Select(edge=>nodes[edge.From]).Where(parent=>parent.Type is "Order" or "OrderOperation" or "AcceptanceInspection" or "Shipment").Distinct())contexts.AddRange(await CurrentSourcesAsync(uow,parent.Type,parent.Id,ct));
     refs=refs.Concat(contexts).Distinct().ToArray();
    }
   }
   catch(CommandRejectedException){gaps.Add(new("recorded_source_unavailable","A reachable owner source is unavailable; its missing details/coverage are not invented."));continue;}
   if(!await CanReadAsync(uow,principal,refs,ct)){restricted=true;continue;}
   readable.Add(key);var holdTargets=new List<TraceHoldTarget>();if(node.Type=="Lot")holdTargets.Add(new(HoldTargetType.Lot,node.Lot!,node.Part));if(node.Type=="Order")holdTargets.Add(new(HoldTargetType.Order,node.Id,null));if(node.Type=="OrderOperation")holdTargets.Add(new(HoldTargetType.Operation,node.Id,null));if(node.Type=="Shipment")holdTargets.Add(new(HoldTargetType.Shipment,node.Id,null));
   captured.Add(new(key,node.Category,node.Type,node.Id,node.Part,node.Lot,node.Order,node.Sequence,node.Shipment,node.Location,node.Status,node.Quantity,node.Unit,node.Customer,[],refs,InvestigationService.Resources(refs,node.Type,long.TryParse(node.Id,out var numeric)?numeric:null).Select(resource=>resource with{Id=node.Id}).ToArray(),holdTargets,node.Revision));
   if(node.Type=="Lot")foreach(var balance in (await inventory.ListBalancesAsync(node.Part,ct)).Where(balance=>balance.LotNumber==node.Lot))
   {var location=await CurrentSourcesAsync(uow,"Location",balance.LocationCode,ct);var stockRefs=refs.Concat(location.Select(reference=>reference with{ActionScope=false})).ToArray();if(!await CanReadAsync(uow,principal,stockRefs,ct)){restricted=true;continue;}var stockKey=key+"/"+Uri.EscapeDataString(balance.LocationCode)+"/"+balance.Status;captured.Add(new(stockKey,"CurrentStock","StockBalance",stockKey,balance.PartNumber,balance.LotNumber,null,null,null,balance.LocationCode,balance.Status.ToString(),balance.Quantity,null,null,[],stockRefs,InvestigationService.Resources(refs,"Lot",null).Select(resource=>resource with{Id=node.Id}).ToArray(),[new(HoldTargetType.Lot,node.Lot!,node.Part)],node.Revision));}
  }
  var projectedLinks=new List<TraceGraphLink>();
  foreach(var link in traversal.Edges.Where(link=>readable.Contains(link.To)&&(readable.Contains(link.From)||roots.Contains(link.From,StringComparer.Ordinal))))
  {var linkSources=(captured.FirstOrDefault(candidate=>candidate.Key==link.From)?.Sources??rootSources).Concat(captured.First(candidate=>candidate.Key==link.To).Sources).Distinct().ToArray();if(await CanReadAsync(uow,principal,linkSources,ct))projectedLinks.Add(link with{Evidence=link.Evidence with{Sources=linkSources,CapturedCanonicalJson=retainedOwnerValues.GetValueOrDefault((link.Evidence.Type,link.Evidence.Id))}});else restricted=true;}
  captured=captured.Select(candidate=>candidate with{Paths=projectedLinks.Where(link=>link.To==candidate.Key).Select(link=>link.Evidence).ToArray()}).ToList();
  if(restricted)gaps.Add(new("restricted_sources","Some reachable evidence is inaccessible; no hidden identifiers, descriptions or counts are disclosed."));
  var consistent=uow is Forge.Kernel.Postgres.PostgresUnitOfWork{IsReadOnly:true};
  if(!consistent)gaps.Add(new("concurrent_snapshot_uncertain","The supplied owner UOW is not the actual repeatable-read source snapshot; concurrent mutable records or late backdated evidence cannot be excluded."));
  gaps.Add(new("unrecorded_physical_use_unknown","The graph covers recorded genealogy and bindings. Unrecorded physical use, missing production-tool bindings and legacy cohort identity remain unknown."));
  var physicalKnown=!gaps.Any(gap=>gap.Code is "unidentified_output" or "legacy_measurement_coverage_unknown" or "recorded_source_unavailable" or "configuration_unknown");
  var complete=consistent&&!restricted&&!gaps.Any(gap=>gap.Code=="recorded_source_unavailable");
  var capture=new TraceCapture(cutoff,"complete-owner-trace-v1","Immutable ledger/link/inspection times bounded by the actual first-query cutoff; mutable balances/task/container/shipment/order states are observed in the same recorded PostgreSQL snapshot, not backdated. Current source access is rechecked before save and every later read.",boundary,captured,new(complete,intervalKnown,physicalKnown,restricted,!consistent,gaps.Distinct().ToArray()),string.Empty,rootSources,projectedLinks);
  return capture with{SourceFingerprint=CanonicalJson.Fingerprint(capture)};
 }
}
