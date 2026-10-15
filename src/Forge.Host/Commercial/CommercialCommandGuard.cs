using Forge.Execution;
using Forge.Execution.Contracts;
using Forge.Host.WorkInbox;
using Forge.Inventory;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Serialization;
using Forge.Kernel.Security;
using Forge.Planning;
using Forge.Procurement;
using Forge.Procurement.Contracts;
using Forge.Programs.Contracts;
using Forge.Quality;
using Forge.Quality.Acceptance;
using Forge.Quality.Commercial;
using Forge.Quality.Contracts;
using Forge.Resources;
using Forge.Resources.Contracts;

namespace Forge.Host.Commercial;

/// <summary>Composed by Host, no new stock/actor truth. All checks and locks run before replay in the owner UOW.</summary>
public sealed class CommercialCommandGuard(IServiceProvider services)
{
    private CommercialQualityService Quality=>services.GetRequiredService<CommercialQualityService>();
    public async Task<CommandTransactionEvidence?> ValidateAsync(IKernelUnitOfWork uow,CommandSpec spec,object command,CommandContext context,
        CommandAuthority authority,DateTimeOffset now,CancellationToken ct)
    {
        var evidence=new List<CommercialUseEvidence?>();
        switch(command)
        {
            case ReleaseProductionOrder release:
                var order=await uow.Store<IExecutionLookup>().FindOrderAsync(release.OrderNumber,ct)??throw Missing();
                evidence.Add(await Quality.RequireDesignAsync(uow,context.Principal,order.PartNumber,order.Revision,now,ct));
                if(order.ContractId is{} contract)evidence.Add(await Quality.RequireCustomerAsync(uow,context.Principal,contract,order.PartNumber,order.Revision,false,now,ct));
                break;
            case ApprovePurchaseOrder purchase:
                var po=await uow.Store<IProcurementLookup>().FindPurchaseOrderAsync(purchase.PoNumber,ct)??throw Missing();
                if(po.SupplierCode is{} actualSupplier)await CommercialQualityService.LockProviderScopeAsync(uow,actualSupplier,ct);
                await uow.Store<IProcurementStore>().LockPurchaseOrderAsync(po.PoNumber,ct);
                po=await uow.Store<IProcurementLookup>().FindPurchaseOrderAsync(purchase.PoNumber,ct)??throw Missing();
                var selected=await SelectionAsync(uow,context,purchase.QualityProcessCode,purchase.QualityWorkCenterCode,po,ct);
                if(po.SupplierCode is{} supplier)
                    foreach(var line in po.Lines.Where(l=>l.Status==PurchaseLineStatus.Open))
                    {
                        var package=await uow.Store<ITechnicalPackageLookup>().FindPackageAsync(po.PoNumber,line.LineNumber,ct);
                        var revision=package?.ItemRevision??(await uow.Store<Forge.Engineering.Contracts.IEngineeringLookup>().ListRevisionsAsync(line.PartNumber,ct)).FirstOrDefault(r=>r.IsEffectiveAt(now))?.Revision;
                        var qualification=await Quality.RequireProviderAsync(uow,context.Principal,supplier,line.PartNumber,revision,purchase.QualityProcessCode,purchase.QualityWorkCenterCode,false,now,ct);
                        if(qualification is not null)qualification=qualification with{Sources=qualification.Sources.Concat(selected.Sources).Distinct().ToArray(),
                            Contexts=[new("PurchaseOrder",po.PoNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),line.LineNumber,purchase.QualityProcessCode,purchase.QualityWorkCenterCode)]};
                        evidence.Add(qualification);
                    }
                break;
            case DispatchOutsideProcess dispatch:
                var processor=await uow.Store<IInventoryLookup>().FindLocationAsync(dispatch.ProcessorLocation,ct)??throw Missing();
                var processPo=await uow.Store<IProcurementLookup>().FindPurchaseOrderAsync(dispatch.PurchaseOrder,ct)??throw Missing();
                var serviceLine=processPo.Lines.FirstOrDefault(l=>l.LineNumber==dispatch.PurchaseOrderLine)??throw Missing();
                var material=await uow.Store<IInventoryLookup>().FindLotAsync(dispatch.PartNumber,dispatch.LotNumber,ct)??throw Missing();
                var approved=await uow.Store<ICommercialUseLookup>().LatestForEntityAsync("PurchaseOrder",processPo.PoNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),nameof(ApprovePurchaseOrder),ct);
                var approvedContext=approved?.Evidence.Contexts?.FirstOrDefault(c=>c.LineNumber==serviceLine.LineNumber);
                if(approved is not null)await services.GetRequiredService<ICommercialQualitySourceResolver>().RequireCapturedAsync(uow,context.Principal,approved.Evidence.Sources,ct);
                evidence.Add(await Quality.RequireProviderAsync(uow,context.Principal,processPo.SupplierCode??processor.CustodianSupplier??throw Missing(),
                    serviceLine.PartNumber,(await uow.Store<ITechnicalPackageLookup>().FindPackageAsync(processPo.PoNumber,serviceLine.LineNumber,ct))?.ItemRevision,
                    approvedContext?.ProcessCode,approvedContext?.WorkCenterCode??processor.WorkCenterCode,true,now,ct));
                evidence.Add(await Quality.RequirePropertyAsync(uow,context.Principal,new(new(PropertyUseKind.Material,material.LotNumber,material.PartNumber),
                    "ExternalDispatch",processor.WorkCenterCode,null,false,(await uow.Store<IInventoryLookup>().FindLocationAsync(dispatch.FromLocation,ct))?.SiteCode),now,ct));
                break;
            case PackShipment pack: await ShipmentAsync(uow,context,pack.ShipmentId,now,evidence,ct);break;
            case ShipShipment ship: await ShipmentAsync(uow,context,ship.ShipmentId,now,evidence,ct);break;
            case IssueToOrder issue:
                var target=await uow.Store<IExecutionLookup>().FindOrderAsync(issue.OrderNumber,ct)??throw Missing();
                if(issue.LotNumber is{} issuedLot)evidence.Add(await Quality.RequirePropertyAsync(uow,context.Principal,
                    new(new(PropertyUseKind.Material,issuedLot,issue.PartNumber),"ProductionIssue",target.Operations.Select(o=>o.WorkCenterCode).Distinct(StringComparer.Ordinal).Count()==1?target.Operations[0].WorkCenterCode:null,target.OrderNumber,true,
                        (await uow.Store<IInventoryLookup>().FindLocationAsync(issue.LocationCode,ct))?.SiteCode),now,ct));
                break;
            case StartOperation start:
                var work=await uow.Store<IExecutionLookup>().FindOrderAsync(start.OrderNumber,ct)??throw Missing();
                var operation=work.Operations.FirstOrDefault(o=>o.Sequence==start.Sequence)??throw Missing();
                if(start.MachineCode is{} machine)evidence.Add(await Quality.RequirePropertyAsync(uow,context.Principal,
                    new(new(PropertyUseKind.Machine,machine),operation.ActivityCode,operation.WorkCenterCode,work.OrderNumber,false,await FacilityForCenterAsync(uow,operation.WorkCenterCode,ct)),now,ct));
                if(start.ToolCode is{} tool)evidence.Add(await Quality.RequirePropertyAsync(uow,context.Principal,
                    new(new(PropertyUseKind.Tool,tool),operation.ActivityCode,operation.WorkCenterCode,work.OrderNumber,false,await FacilityForCenterAsync(uow,operation.WorkCenterCode,ct)),now,ct));
                break;
            case UseTool use:
                var machineScope=use.MachineCode is{} machineKey?await uow.Store<IResourcesLookup>().FindRecordedMachineAsync(machineKey,ct):null;
                evidence.Add(await Quality.RequirePropertyAsync(uow,context.Principal,
                    new(new(PropertyUseKind.Tool,use.Code),use.ActivityCode??"ToolUse",machineScope?.WorkCenterCode,null,false,
                        machineScope is null?null:await FacilityForCenterAsync(uow,machineScope.WorkCenterCode,ct)),now,ct));break;
            case RecordInspection inspection when inspection.GaugeCode is{} gauge:
                var inspectionOrder=await uow.Store<IExecutionLookup>().FindOrderAsync(inspection.OrderNumber,ct)??throw Missing();
                var inspectionOperation=inspectionOrder.Operations.FirstOrDefault(o=>o.Sequence==inspection.Sequence)??throw Missing();
                evidence.Add(await Quality.RequirePropertyAsync(uow,context.Principal,new(new(PropertyUseKind.Tool,gauge),"Inspection",
                    inspectionOperation.WorkCenterCode,inspection.OrderNumber,false,await FacilityForCenterAsync(uow,inspectionOperation.WorkCenterCode,ct)),now,ct));break;
            case RecordAcceptanceInspection acceptanceInspection:
                var subject=await services.GetRequiredService<IInspectionSubjectResolver>().ResolveAsync(uow,context.Principal,acceptanceInspection.Subject,false,ct);
                foreach(var gaugeCode in acceptanceInspection.Findings.Select(f=>f.GaugeCode).Where(g=>g is not null).Cast<string>().Distinct(StringComparer.Ordinal))
                    evidence.Add(await Quality.RequirePropertyAsync(uow,context.Principal,new(new(PropertyUseKind.Tool,gaugeCode),"Inspection",
                        subject.Source.Context.WorkCenterCode,subject.OriginOrderNumbers.Count==1?subject.OriginOrderNumbers[0]:null,false,subject.Source.Context.FacilityCode),now,ct));
                break;
            case DispositionCustomerReturn disposition:
                var returned=await uow.Store<IReceivingLookup>().FindCustomerReturnAsync(disposition.RmaId,ct)??throw Missing();
                if(returned.LotNumber is{} returnedLot)evidence.Add(await Quality.RequirePropertyAsync(uow,context.Principal,
                    new(new(PropertyUseKind.Material,returnedLot,returned.PartNumber),"ReturnDisposition",null,null,
                        disposition.Disposition==CustomerReturnDisposition.Scrap,(await uow.Store<IInventoryLookup>().FindLocationAsync(returned.LocationCode,ct))?.SiteCode),now,ct));break;
        }
        var combined=CommercialQualityService.Combine(spec.CommandType,evidence);
        if(combined.Records.Count==0)return null;
        var principal=await uow.People.LoadPrincipalAsync(context.Principal.PersonId,ct)??throw new ForgeAuthorizationException(new(false,["A current registered person is required."]),authority.Resource);
        var authorizer=services.GetRequiredService<IForgeAuthorizer>();
        foreach(var resource in new[]{authority.Resource}.Concat(authority.AdditionalResources??[]))
        { var decision=authorizer.Authorize(principal,spec.Permission,resource);if(!decision.IsPermitted)throw new ForgeAuthorizationException(decision,resource); }
        return new("CommercialQuality",combined.Fingerprint,combined);
    }
    private async Task ShipmentAsync(IKernelUnitOfWork uow,CommandContext context,long id,DateTimeOffset now,List<CommercialUseEvidence?> evidence,CancellationToken ct)
    {
        var shipment=await uow.Store<IInventoryLookup>().FindShipmentAsync(id,ct)??throw Missing();
        long? contract=null;
        if(shipment.DemandId is{} demandId)
        {
            var demand=await uow.Store<IDemandStore>().FindAsync(demandId,ct)??throw Missing();
            if(demand.SourceKey?.StartsWith("delivery:",StringComparison.Ordinal)==true)
            {
                var delivery=await uow.Store<IProgramsLookup>().FindDeliveryAsync(long.Parse(demand.SourceKey["delivery:".Length..],System.Globalization.CultureInfo.InvariantCulture),ct)??throw Missing();
                contract=delivery.Delivery.ContractId;
            }
        }
        foreach(var line in shipment.Lines)
        {
            var lot=line.LotNumber is{} lotKey?await uow.Store<IInventoryLookup>().FindLotAsync(line.PartNumber,lotKey,ct):null;
            if(contract is{} actual)evidence.Add(await Quality.RequireCustomerAsync(uow,context.Principal,actual,line.PartNumber,lot?.PartRevision??string.Empty,true,now,ct));
            if(lot is not null)evidence.Add(await Quality.RequirePropertyAsync(uow,context.Principal,new(new(PropertyUseKind.Material,lot.LotNumber,lot.PartNumber),"Shipment",null,null,true,
                (await uow.Store<IInventoryLookup>().FindLocationAsync(line.LocationCode,ct))?.SiteCode),now,ct));
        }
    }
    private static async Task<string?> FacilityForCenterAsync(IKernelUnitOfWork uow,string center,CancellationToken ct)
    {
        var actual=await uow.Store<IResourcesLookup>().FindRecordedWorkCenterAsync(center,ct);
        return actual is null?null:(await uow.Store<IResourcesLookup>().FindRecordedSafetyAreaAsync(actual.SafetyAreaCode,ct))?.SiteCode;
    }
    private async Task<CommercialScope> SelectionAsync(IKernelUnitOfWork uow,CommandContext context,string? process,string? center,PurchaseOrderInfo po,CancellationToken ct)
    {
        var refs=new List<CommercialReference>();
        if(process is not null)refs.Add(new(CommercialReferenceKind.QmsRevision,process));
        if(center is not null)refs.Add(new(CommercialReferenceKind.WorkCenter,center));
        var facility=center is not null?(await uow.Store<IResourcesLookup>().FindRecordedSafetyAreaAsync((await uow.Store<IResourcesLookup>().FindRecordedWorkCenterAsync(center,ct))?.SafetyAreaCode??string.Empty,ct))?.SiteCode
            :process is not null?(await uow.Store<IQmsLookup>().FindAsync(process,ct))?.FacilityCode:null;
        if(refs.Count==0)return new(string.Empty,string.Empty,null,null,[],[],[],string.Empty,[]);
        if(facility is null)throw Missing();
        var scope=await services.GetRequiredService<ICommercialQualitySourceResolver>().ResolveReferencesAsync(uow,context.Principal,facility,refs,ct);
        var authorizer=services.GetRequiredService<IForgeAuthorizer>();
        foreach(var source in await PurchaseOrderAuthority.ResourcesAsync(uow,po,ct))
        {
            var resource=source with{FacilityId=facility,WorkCenterId=center};
            var decision=authorizer.Authorize(context.Principal,Permissions.PurchasingApprove,resource);
            if(!decision.IsPermitted)throw new ForgeAuthorizationException(decision,resource);
        }
        return scope;
    }
    public async Task RecordAcceptedAsync(IKernelUnitOfWork uow,CommandSpec spec,CommandContext context,CommandOutcome outcome,Guid? auditId,
        CommandTransactionEvidence component,string contextFingerprint,DateTimeOffset now,CancellationToken ct)
    {
        if(component.Kind!="CommercialQuality"||component.Data is not CommercialUseEvidence evidence||component.Fingerprint!=evidence.Fingerprint)
            throw new CommandRejectedException("commercial_context_changed","The accepted commercial component does not match its validated proof.");
        if(outcome.Change is null)return;
        if(auditId is null)throw new CommandRejectedException("commercial_audit_required","Commercial usage binds the actual successful owner audit.");
        await CommercialQualityService.RecordAcceptedUseAsync(uow,new(ForgeIds.New(services.GetRequiredService<TimeProvider>()),auditId.Value,contextFingerprint,
            component.Fingerprint,outcome.Change.EntityType,outcome.Change.EntityId,context.Actor,now,evidence),ct);
    }
    private static CommandRejectedException Missing()=>new("commercial_source_not_found","The actual consuming source is unavailable.");
}

