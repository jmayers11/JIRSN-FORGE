using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Host.WorkInbox;
using Forge.Host.Identification;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Holds;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Kernel.WorkAssignments;
using Forge.Procurement.Contracts;
using Forge.Programs.Contracts;
using Forge.Planning;
using Forge.Quality.Acceptance;
using Forge.Quality.Commercial;
using Forge.Quality.Contracts;
using Forge.Quality.Qms;
using Forge.Resources.Contracts;

namespace Forge.Host.Commercial;

/// <summary>Owner contracts, one UOW, and no barcode namespace dependency or parallel party/stock identity.</summary>
public sealed partial class CommercialQualitySourceResolver(IServiceProvider services, IForgeAuthorizer authorizer, QmsOptions options) : ICommercialQualitySourceResolver
{
    public async Task<CommercialScope> ResolveAsync(IKernelUnitOfWork uow, ForgePrincipal principal, string facility,
        CommercialRecordKind kind, CommercialContent content, CancellationToken ct)
    {
        CommercialRules.Validate(kind,content);
        if(content.Complaint is{} complaint && (await uow.Store<IInventoryLookup>().FindShipmentAsync(complaint.ShipmentId,ct))?.Status!=ShipmentStatus.Shipped
            ||content.PostDelivery is{} post && (await uow.Store<IInventoryLookup>().FindShipmentAsync(post.ShipmentId,ct))?.Status!=ShipmentStatus.Shipped)
            throw new CommandRejectedException("shipment_not_delivered","Post-delivery cases reference actual shipped product.");
        var scope=await ResolveReferencesAsync(uow,principal,facility,CommercialReferences.Of(content),ct);
        if(content.CustomerRequirements is{} requirement && requirement.DeliveryId is{} delivery)
        {
            var actual=await uow.Store<IProgramsLookup>().FindDeliveryAsync(delivery,ct)??throw Missing();
            if(actual.Delivery.ContractId!=requirement.ContractId || requirement.PartNumber is{} part && actual.PartNumber!=part)
                throw new CommandRejectedException("requirements_context","The delivery and part must belong to the actual contract.");
        }
        if(content.ProviderEvaluation?.Scope.WorkCenterCode is{} center && await uow.Store<IResourcesLookup>().FindRecordedWorkCenterAsync(center,ct) is null)throw Missing();
        if(content.ProviderEvaluation?.Scope.ProcessCode is{} processCode)
        {
            var process=await uow.Store<IQmsLookup>().FindAsync(processCode,ct);
            if(process is null||process.Kind!=QmsRecordKind.Process||process.OrganizationCode!=options.OrganizationCode
                ||!process.Revisions.Any(revision=>revision.State==QmsState.Effective))throw Missing();
        }
        if(content.PropertyAgreement is{} agreement)
            foreach(var permittedCenter in agreement.PermittedWorkCenters)
                if(await uow.Store<IResourcesLookup>().FindRecordedWorkCenterAsync(permittedCenter,ct) is null)throw Missing();
        if(content.Communication is{} communication)
        {
            var party=await ResolveReferencesAsync(uow,principal,facility,[communication.PartySource],ct);
            var related=await ResolveReferencesAsync(uow,principal,facility,[communication.RelatedCase],ct);
            if(communication.Audience==CommunicationAudience.Customer && (party.Customer is null || !related.Sources.Any(s=>s.Resource.CustomerId==party.Customer)))
                throw new CommandRejectedException("communication_party_mismatch","The proposal must address the actual customer of its recorded case.");
            if(communication.Audience==CommunicationAudience.Supplier && !related.Facts.Any(f=>f.Reference.Kind==CommercialReferenceKind.Supplier&&f.Reference.Key==communication.PartySource.Key))
                throw new CommandRejectedException("communication_party_mismatch","The proposal must address the actual supplier of its recorded case.");
            if(communication.Concession is{} concession)
            {
                var context=await CustomerConcessionContextAsync(uow,principal,concession,ct);
                if(!context.CustomerSources.Contains(communication.PartySource))throw new CommandRejectedException("customer_concession_source","The actual failed physical subject and customer must agree with the concession proposal.");
            }
        }
        return scope;
    }
    public async Task<CommercialScope> ResolveReferencesAsync(IKernelUnitOfWork uow,ForgePrincipal principal,string facility,
        IReadOnlyList<CommercialReference> references,CancellationToken ct)
    {
        facility=BusinessCode.Require(facility,"Facility");
        if(!(await uow.Store<IResourcesLookup>().ListSitesAsync(ct)).Any(site=>site.Code==facility))throw Missing();
        var current=await uow.People.LoadPrincipalAsync(principal.PersonId,ct);
        if(current is not{IsActive:true})throw new ForgeAuthorizationException(new(false,["The current manufacturing person is unavailable."]),new("CommercialQuality",FacilityId:facility));
        CommercialRules.References(references);
        var facts=new List<CommercialSourceFact>();var reads=new List<CommercialReadSource>();var visited=new HashSet<string>(StringComparer.Ordinal);
        foreach(var reference in references)await ReferenceAsync(uow,current,reference,facts,reads,visited,ct);
        var ownership=reads.Select(s=>s.Resource).Distinct().ToArray();
        if(ownership.Length==0)ownership=[new("CommercialQuality",FacilityId:facility)];
        return new(options.OrganizationCode??string.Empty,facility,reads.Select(s=>s.Resource.CustomerId).FirstOrDefault(customer=>customer is not null),
            references.FirstOrDefault(r=>r.Kind==CommercialReferenceKind.Supplier)?.Key,ownership,reads.Distinct().ToArray(),facts.Distinct().ToArray(),
            CanonicalJson.Fingerprint(facts.OrderBy(f=>CanonicalJson.Serialize(f.Reference),StringComparer.Ordinal)),[]);
    }
    public async Task RequireCapturedAsync(IKernelUnitOfWork uow,ForgePrincipal principal,IReadOnlyList<CommercialReadSource> captured,CancellationToken ct)
    {
        var current=await uow.People.LoadPrincipalAsync(principal.PersonId,ct);
        if(current is not{IsActive:true})throw new ForgeAuthorizationException(new(false,["The current manufacturing person is unavailable."]));
        var facts=new List<CommercialSourceFact>();var reads=new List<CommercialReadSource>();var visited=new HashSet<string>(StringComparer.Ordinal);
        foreach(var source in captured)
        {
            Permit(current,source.Permission,source.Resource);
            var reference=ReferenceFor(source.Resource);
            if(reference is not null)await ReferenceAsync(uow,current,reference,facts,reads,visited,ct);
            else
            {
                var owner=await SourceResolver.ResolveAsync(uow,source.Resource.Type,source.Resource.Id??string.Empty,ct)??throw Missing();
                foreach(var resource in owner.Resources)Permit(current,source.Permission,resource);
                if(OwnedSourceFamilies.Contains(source.Resource.Type) && SourceResolver is IOwnedSourceResolver guarded && !await guarded.CanReadOwnedSourceAsync(uow,current,source.Resource.Type,source.Resource.Id??string.Empty,ct))
                    throw new ForgeAuthorizationException(new(false,["Current owner sources are unavailable."]),source.Resource);
            }
        }
    }
    private IWorkSourceResolver SourceResolver=>services.GetRequiredService<IWorkSourceResolver>();
    public async Task<IReadOnlyList<ResourceRef>> ResourceContextsAsync(IKernelUnitOfWork uow,string facility,IReadOnlyList<CommercialReference> references,CancellationToken ct)
    {
        var facts=new List<CommercialSourceFact>();var reads=new List<CommercialReadSource>();var visited=new HashSet<string>(StringComparer.Ordinal);
        foreach(var reference in references)await ReferenceAsync(uow,null,reference,facts,reads,visited,ct);
        return reads.Count==0?[new("CommercialQuality",FacilityId:facility)]:reads.Select(source=>source.Resource).Distinct().ToArray();
    }
    private async Task ReferenceAsync(IKernelUnitOfWork uow,ForgePrincipal? principal,CommercialReference reference,List<CommercialSourceFact> facts,
        List<CommercialReadSource> reads,HashSet<string> visited,CancellationToken ct)
    {
        CommercialRules.Reference(reference);
        var identity=CanonicalJson.Serialize(reference);
        if(!visited.Add(identity))return;
        await LockReferenceAsync(uow,reference,ct);
        ResourceRef[] resources;string permission;object? value;
        var key=reference.Key;
        switch(reference.Kind)
        {
            case CommercialReferenceKind.Program:
                if(!Guid.TryParseExact(key,"D",out var programId)||programId.ToString("D")!=key)throw Missing();
                var program=await uow.Store<IProgramsLookup>().FindProgramAsync(programId,ct)??throw Missing();
                value=program;permission=Permissions.ProgramsRead;resources=[new("Program",key,new ProgramId(program.ProgramId),CustomerId:program.Customer)];break;
            case CommercialReferenceKind.Supplier:
                value=await uow.Store<IProcurementLookup>().FindSupplierAsync(BusinessCode.Require(key,"Supplier"),ct)??throw Missing();
                permission=Permissions.PurchasingRead;resources=[new("Supplier",key)];break;
            case CommercialReferenceKind.Contract:
                var contract=await uow.Store<IProgramsLookup>().FindContractAsync(Number(key),ct)??throw Missing();
                value=contract;permission=Permissions.ProgramsRead;resources=[new("Contract",key,new ProgramId(contract.ProgramId),ContractId:key,CustomerId:contract.Customer)];break;
            case CommercialReferenceKind.Delivery:
                var delivery=await uow.Store<IProgramsLookup>().FindDeliveryAsync(Number(key),ct)??throw Missing();
                value=delivery;permission=Permissions.ProgramsRead;resources=[new("Delivery",key,new ProgramId(delivery.ProgramId),ContractId:Number(delivery.Delivery.ContractId),CustomerId:delivery.Customer)];break;
            case CommercialReferenceKind.PartRevision:
                var revision=(await uow.Store<IEngineeringLookup>().ListRevisionsAsync(key,ct)).FirstOrDefault(r=>r.Revision==reference.Revision)??throw Missing();
                value=revision;permission=Permissions.EngineeringRead;resources=[new("ItemRevision",key+"/"+revision.Revision)];break;
            case CommercialReferenceKind.Item:
                value=await uow.Store<IEngineeringLookup>().FindItemAsync(key,ct)??throw Missing();
                permission=Permissions.EngineeringRead;resources=[new("Item",key)];break;
            case CommercialReferenceKind.WorkCenter:
                var workCenter=await uow.Store<IResourcesLookup>().FindRecordedWorkCenterAsync(key,ct)??throw Missing();
                var workArea=await uow.Store<IResourcesLookup>().FindRecordedSafetyAreaAsync(workCenter.SafetyAreaCode,ct)??throw Missing();
                value=workCenter;permission=Permissions.ResourcesRead;resources=[new("WorkCenter",key,FacilityId:workArea.SiteCode,WorkCenterId:key)];break;
            case CommercialReferenceKind.QmsRevision:
                var qms=await uow.Store<IQmsLookup>().FindAsync(key,ct)??throw Missing();
                var qmsRevision=reference.Revision is null?qms.Revisions.SingleOrDefault(r=>r.State==QmsState.Effective)
                    :qms.Revisions.FirstOrDefault(r=>r.Revision.ToString(CultureInfo.InvariantCulture)==reference.Revision);
                if(qmsRevision is null||qms.OrganizationCode!=options.OrganizationCode)throw Missing();
                if(principal is not null&&!await services.GetRequiredService<QmsService>().CanReadAsync(uow,principal,qms,ct,qmsRevision.Revision))throw Missing();
                value=new{qms.Code,qms.Kind,qmsRevision.Revision,qmsRevision.Content};permission=Permissions.QmsRead;
                resources=[QmsService.Resource(qms) with{Type="QmsRevision",Id=key+"/"+qmsRevision.Revision.ToString(CultureInfo.InvariantCulture)}];
                foreach(var dependency in qmsRevision.Sources)
                {
                    var readPermission=dependency.Kind=="DocumentRevision"?Permissions.EngineeringRead:Permissions.QmsRead;
                    var resource=new ResourceRef(dependency.Kind,dependency.Key+"/"+dependency.Revision,Program(dependency.ProgramId),dependency.Markings,FacilityId:dependency.FacilityCode);
                    Permit(principal,readPermission,resource);reads.Add(new(readPermission,resource));
                }
                break;
            case CommercialReferenceKind.DocumentRevision:
                var document=await uow.Store<IDocumentLookup>().FindDocumentAsync(key,ct)??throw Missing();
                var documentRevision=document.Revisions.FirstOrDefault(r=>r.Revision==reference.Revision)??throw Missing();
                value=new{document.Number,document.Authority,documentRevision};
                permission=Permissions.EngineeringRead;resources=[new("DocumentRevision",key+"/"+documentRevision.Revision,Program(document.ProgramId),document.Markings)];break;
            case CommercialReferenceKind.EngineeringChange:
                var change=await uow.Store<IChangeLookup>().FindChangeAsync(key,ct)??throw Missing();
                value=change;permission=Permissions.EngineeringRead;resources=[new("EngineeringChange",key,Program(change.ProgramId),CustomerId:change.Customer)];break;
            case CommercialReferenceKind.PurchaseOrder:
                var po=await uow.Store<IProcurementLookup>().FindPurchaseOrderAsync(Number(key),ct)??throw Missing();
                if(reference.LineNumber is{} line&&!po.Lines.Any(l=>l.LineNumber==line))throw Missing();
                value=po;permission=Permissions.PurchasingRead;resources=(await PurchaseOrderAuthority.ResourcesAsync(uow,po,ct)).ToArray();break;
            case CommercialReferenceKind.Receipt:
            case CommercialReferenceKind.InventoryTransaction:
                var receipt=await uow.Store<IReceivingLookup>().FindTransactionAsync(Number(key),ct)??throw Missing();
                if(reference.Kind==CommercialReferenceKind.Receipt && receipt.Kind is not(InventoryTransactionKind.Receipt or InventoryTransactionKind.ProductionReceipt or InventoryTransactionKind.CustomerReturn))throw Missing();
                value=receipt;permission=Permissions.InventoryRead;resources=await TransactionResourcesAsync(uow,principal,receipt,facts,reads,visited,ct);break;
            case CommercialReferenceKind.Shipment:
                var shipment=await uow.Store<IInventoryLookup>().FindShipmentAsync(Number(key),ct)??throw Missing();
                if(reference.LineNumber is{} shipmentLine&&!shipment.Lines.Any(l=>l.LineNumber==shipmentLine))throw Missing();
                value=shipment;permission=Permissions.InventoryRead;
                var scopes=new List<ResourceRef>();
                var selected=shipment.Lines.Where(l=>reference.LineNumber is null||l.LineNumber==reference.LineNumber).ToArray();
                foreach(var shipmentRow in selected)scopes.Add(await LocationAsync(uow,shipmentRow.LocationCode,ct));
                if(shipment.DemandId is{} demandId)
                {
                    var demand=await uow.Store<IDemandStore>().FindAsync(demandId,ct);
                    if(demand?.SourceKey?.StartsWith("delivery:",StringComparison.Ordinal)==true)
                    {
                        var deliverySource=new CommercialReference(CommercialReferenceKind.Delivery,demand.SourceKey["delivery:".Length..]);
                        await ReferenceAsync(uow,principal,deliverySource,facts,reads,visited,ct);
                        scopes.AddRange(facts.Where(f=>f.Reference==deliverySource).SelectMany(f=>f.Resources));
                    }
                }
                // Exact lot origins carry current production/procurement classifications into shipment evidence.
                var transactions=await uow.Store<IInventoryLookup>().ListGenealogyTransactionsAsync(ct);
                var links=await uow.Store<IInventoryLookup>().ListInstanceLinksAsync(null,null,ct);
                var queue=new Queue<(string Part,string Lot)>();var seen=new HashSet<(string,string)>();
                foreach(var row in selected)if(row.LotNumber is{} lot)queue.Enqueue((row.PartNumber,lot));
                while(queue.TryDequeue(out var tuple))
                {
                    if(!seen.Add(tuple))continue;
                    foreach(var link in links.Where(l=>l.ResultPart==tuple.Part&&l.ResultLot==tuple.Lot))queue.Enqueue((link.SourcePart,link.SourceLot));
                    foreach(var transaction in transactions.Where(t=>t.PartNumber==tuple.Part&&t.LotNumber==tuple.Lot))
                    {
                        if(transaction.OrderNumber is{} orderNumber&&transaction.Kind is InventoryTransactionKind.ProductionReceipt or InventoryTransactionKind.Remnant or InventoryTransactionKind.Byproduct)
                        {
                            var orderReference=new CommercialReference(CommercialReferenceKind.Order,Number(orderNumber));
                            await ReferenceAsync(uow,principal,orderReference,facts,reads,visited,ct);
                            scopes.AddRange(facts.Where(f=>f.Reference==orderReference).SelectMany(f=>f.Resources));
                            foreach(var input in transactions.Where(t=>t.OrderNumber==orderNumber&&t.Kind==InventoryTransactionKind.Issue&&t.LotNumber is not null))
                                queue.Enqueue((input.PartNumber,input.LotNumber!));
                        }
                        if(transaction.PoNumber is{} purchaseNumber&&transaction.Kind is InventoryTransactionKind.Receipt or InventoryTransactionKind.CustomerReturn)
                        {
                            var poReference=new CommercialReference(CommercialReferenceKind.PurchaseOrder,Number(purchaseNumber));
                            await ReferenceAsync(uow,principal,poReference,facts,reads,visited,ct);
                            scopes.AddRange(facts.Where(f=>f.Reference==poReference).SelectMany(f=>f.Resources));
                        }
                    }
                }
                if(scopes.Count==0)scopes.Add(new("Shipment",key,CustomerId:shipment.Customer));
                resources=scopes.Select(r=>r with{Type="Shipment",Id=key,CustomerId=shipment.Customer}).Distinct().ToArray();break;
            case CommercialReferenceKind.CustomerReturn:
                var rma=await uow.Store<IReceivingLookup>().FindCustomerReturnAsync(Number(key),ct)??throw Missing();
                await ReferenceAsync(uow,principal,new(CommercialReferenceKind.Shipment,Number(rma.ShipmentId)),facts,reads,visited,ct);
                value=rma;permission=Permissions.InventoryRead;
                resources=facts.Where(f=>f.Reference.Kind==CommercialReferenceKind.Shipment&&f.Reference.Key==Number(rma.ShipmentId)).SelectMany(f=>f.Resources)
                    .Append(await LocationAsync(uow,rma.LocationCode,ct)).Select(r=>r with{Type="CustomerReturn",Id=key,CustomerId=rma.Customer}).Distinct().ToArray();break;
            case CommercialReferenceKind.Order:
                var order=await uow.Store<IExecutionLookup>().FindOrderAsync(Number(key),ct)??throw Missing();
                value=new{order.OrderNumber,order.PartNumber,order.Revision,order.Quantity,order.ProgramId,order.ContractId,order.ContractClin};permission=Permissions.OrdersRead;resources=[new("Order",key,Program(order.ProgramId),ContractId:order.ContractId is{} contractId?Number(contractId):null)];break;
            case CommercialReferenceKind.Lot:
                var parts=key.Split('/',2);
                if(parts.Length!=2)throw Missing();
                value=await uow.Store<IInventoryLookup>().FindLotAsync(parts[0],parts[1],ct)??throw Missing();
                permission=Permissions.InventoryRead;resources=[new("Lot",CanonicalIdentification.EncodeKey(parts[0])+":"+CanonicalIdentification.EncodeKey(parts[1]))];break;
            case CommercialReferenceKind.Tool:
                var ownedTool=await uow.Store<IToolLookup>().FindToolAsync(key,ct)??throw Missing();
                value=new{ownedTool.Code,ownedTool.TypeCode,ownedTool.Serial,ownedTool.EligibleActivities,ownedTool.EligibleMachines};
                permission=Permissions.ResourcesRead;resources=[new("Tool",key)];break;
            case CommercialReferenceKind.ToolEvent:
                var toolEventKey=key.Split('/');
                if(toolEventKey.Length!=2)throw Missing();
                value=(await uow.Store<IToolLookup>().ListToolEventsAsync(toolEventKey[0],ct)).FirstOrDefault(e=>e.EventId==Number(toolEventKey[1]))??throw Missing();
                permission=Permissions.ResourcesRead;resources=[new("Tool",toolEventKey[0])];break;
            case CommercialReferenceKind.Location:
                value=await uow.Store<IInventoryLookup>().FindLocationAsync(key,ct)??throw Missing();
                permission=Permissions.InventoryRead;resources=[await LocationAsync(uow,key,ct)];break;
            case CommercialReferenceKind.Machine:
                var machine=await uow.Store<IResourcesLookup>().FindRecordedMachineAsync(key,ct)??throw Missing();
                var center=await uow.Store<IResourcesLookup>().FindRecordedWorkCenterAsync(machine.WorkCenterCode,ct)??throw Missing();
                var area=await uow.Store<IResourcesLookup>().FindRecordedSafetyAreaAsync(center.SafetyAreaCode,ct)??throw Missing();
                value=machine;permission=Permissions.ResourcesRead;resources=[new("Machine",key,FacilityId:area.SiteCode,WorkCenterId:center.Code)];break;
            case CommercialReferenceKind.AcceptanceInspection:
            case CommercialReferenceKind.ProductReleaseDecision:
                var release=reference.Kind==CommercialReferenceKind.ProductReleaseDecision;
                var id=Number(key);var lookup=uow.Store<IAcceptanceLookup>();
                if(principal is not null&&!await services.GetRequiredService<ProductAcceptanceService>().CanReadAsync(uow,principal,id,release,ct))throw Missing();
                var inspection=release?null:await lookup.FindInspectionAsync(id,ct);
                var decision=release?await lookup.FindDecisionAsync(id,ct):null;
                var first=inspection??(decision is null?null:await lookup.FindInspectionAsync(decision.InspectionIds[0],ct))??throw Missing();
                value=(object?)decision??first;permission=Permissions.QualityRead;
                resources=(await services.GetRequiredService<IInspectionSubjectResolver>().ResourceContextsAsync(uow,first.Subject,release?"ProductReleaseDecision":"AcceptanceInspection",id,ct)).ToArray();
                reads.AddRange(first.Subject.Source.Sources.Select(s=>new CommercialReadSource(s.Permission,s.Resource)));
                reads.AddRange(first.Policy.Sources.Select(s=>new CommercialReadSource(s.Permission,s.Resource)));break;
            case CommercialReferenceKind.Inspection:
                var legacyInspection=await uow.Store<IQualityLookup>().FindInspectionAsync(Number(key),ct)??throw Missing();
                value=legacyInspection;permission=Permissions.QualityRead;
                var inspectionOwner=await SourceResolver.ResolveAsync(uow,"OrderOperation",Number(legacyInspection.OrderNumber)+"/"+legacyInspection.Sequence.ToString(CultureInfo.InvariantCulture),ct)??throw Missing();
                resources=inspectionOwner.Resources.Select(r=>r with{Type="Inspection",Id=key}).ToArray();
                foreach(var resource in inspectionOwner.Resources){Permit(principal,Permissions.OrdersRead,resource);reads.Add(new(Permissions.OrdersRead,resource));}break;
            case CommercialReferenceKind.Hold:
                var hold=(await uow.Store<IHoldLookup>().ListHoldsAsync(true,ct)).FirstOrDefault(h=>h.HoldId==Number(key))??throw Missing();
                var holdSource=await SourceResolver.ResolveAsync(uow,"Hold",key,ct)??throw Missing();
                var held=await services.GetRequiredService<IHoldTargetResolver>().ResolveAsync(uow,hold.TargetType,hold.TargetId,hold.TargetPart,ct)??throw Missing();
                value=hold;permission=Permissions.HoldsRead;resources=holdSource.Resources.ToArray();
                var heldType=held.Type==HoldTargetType.Operation?"OrderOperation":held.Type==HoldTargetType.Part?"Item":held.Type.ToString();
                var targetPermission=held.Type switch{HoldTargetType.Order or HoldTargetType.Operation=>Permissions.OrdersRead,
                    HoldTargetType.Part=>Permissions.EngineeringRead,HoldTargetType.Supplier or HoldTargetType.PurchaseOrder=>Permissions.PurchasingRead,
                    HoldTargetType.Machine or HoldTargetType.WorkCenter=>Permissions.ResourcesRead,HoldTargetType.Program=>Permissions.ProgramsRead,_=>Permissions.InventoryRead};
                var targetOwner=await SourceResolver.ResolveAsync(uow,heldType,held.Id,ct);
                foreach(var resource in targetOwner?.Resources??resources.Select(r=>r with{Type=heldType,Id=held.Id}).ToArray())
                {Permit(principal,targetPermission,resource);reads.Add(new(targetPermission,resource));}break;
            case CommercialReferenceKind.Ncr:
            case CommercialReferenceKind.Capa:
                var sourceType=reference.Kind==CommercialReferenceKind.Ncr?"Ncr":"Capa";
                var owner=await SourceResolver.ResolveAsync(uow,sourceType,key,ct)??throw Missing();
                if(principal is not null&&SourceResolver is IOwnedSourceResolver extra&&!await extra.CanReadOwnedSourceAsync(uow,principal,sourceType,key,ct))throw Missing();
                value=reference.Kind==CommercialReferenceKind.Ncr?(object?)await uow.Store<IQualityLookup>().FindNcrAsync(Number(key),ct)
                    :await uow.Store<Forge.Quality.IQualityStore>().FindCapaAsync(Number(key),ct);
                if(value is null)throw Missing();
                permission=Permissions.QualityRead;resources=owner.Resources.ToArray();break;
            case CommercialReferenceKind.CommercialRecord:
                if(!Enum.TryParse<CommercialRecordKind>(key.Split('/')[0],out var recordKind)||!int.TryParse(reference.Revision,NumberStyles.None,CultureInfo.InvariantCulture,out var number))throw Missing();
                var recordFields=key.Split('/');
                if(recordFields.Length!=2)throw Missing();
                var record=await uow.Store<ICommercialQualityLookup>().FindAsync(recordKind,recordFields[1],number,ct)??throw Missing();
                if(record.OrganizationCode!=options.OrganizationCode)throw Missing();
                value=new{record.Kind,record.Code,record.Revision,record.SourceFingerprint};
                permission=Permissions.QualityRead;resources=record.Ownership.Select(r=>r with{Type="CommercialQuality",Id=CommercialQualityService.Key(record.Kind,record.Code,record.Revision),FacilityId=r.FacilityId??record.FacilityCode}).ToArray();
                // A reference selects the immutable definition. Later observations are separately selected evidence.
                foreach(var captured in record.Sources) { Permit(principal,captured.Permission,captured.Resource);reads.Add(captured); }
                foreach(var nested in CommercialReferences.Of(record.Content))
                    await ReferenceAsync(uow,principal,nested,facts,reads,visited,ct);
                break;
            default:throw Missing();
        }
        foreach(var resource in resources){Permit(principal,permission,resource);reads.Add(new(permission,resource));}
        facts.Add(new(reference,reference.Kind+" "+reference.Key,resources,CanonicalJson.Fingerprint(value)));
    }
    private async Task<ResourceRef[]> TransactionResourcesAsync(IKernelUnitOfWork uow,ForgePrincipal? principal,InventoryTransaction transaction,
        List<CommercialSourceFact> facts,List<CommercialReadSource> reads,HashSet<string> visited,CancellationToken ct)
    {
        var result=new List<ResourceRef>();
        if(transaction.OrderNumber is{} order) { var reference=new CommercialReference(CommercialReferenceKind.Order,Number(order)); await ReferenceAsync(uow,principal,reference,facts,reads,visited,ct);result.AddRange(facts.Where(f=>f.Reference==reference).SelectMany(f=>f.Resources)); }
        if(transaction.PoNumber is{} po) { var reference=new CommercialReference(CommercialReferenceKind.PurchaseOrder,Number(po)); await ReferenceAsync(uow,principal,reference,facts,reads,visited,ct);result.AddRange(facts.Where(f=>f.Reference==reference).SelectMany(f=>f.Resources)); }
        if(transaction.ToLocation is{} location)result.Add(await LocationAsync(uow,location,ct));
        if(result.Count==0)result.Add(new("InventoryTransaction",Number(transaction.TransactionId)));
        return result.Select(r=>r with{Type="InventoryTransaction",Id=Number(transaction.TransactionId)}).Distinct().ToArray();
    }
    private static async Task<ResourceRef> LocationAsync(IKernelUnitOfWork uow,string code,CancellationToken ct)
    {
        var location=await uow.Store<IInventoryLookup>().FindLocationAsync(code,ct)??throw Missing();
        var center=location.WorkCenterCode;
        if(center is null&&location.MachineCode is{} machine)center=(await uow.Store<IResourcesLookup>().FindRecordedMachineAsync(machine,ct))?.WorkCenterCode;
        return new("Location",code,FacilityId:location.SiteCode,WorkCenterId:center);
    }
    private static CommercialReference? ReferenceFor(ResourceRef resource)=>resource.Id is not{} key?null:resource.Type switch
    {
        "Supplier"=>new(CommercialReferenceKind.Supplier,key),"Contract"=>new(CommercialReferenceKind.Contract,key),"Delivery"=>new(CommercialReferenceKind.Delivery,key),
        "Order" or "ProductionOrder"=>new(CommercialReferenceKind.Order,key),"PurchaseOrder"=>new(CommercialReferenceKind.PurchaseOrder,key),
        "InventoryTransaction"=>new(CommercialReferenceKind.InventoryTransaction,key),"Shipment"=>new(CommercialReferenceKind.Shipment,key),
        "CustomerReturn"=>new(CommercialReferenceKind.CustomerReturn,key),"Lot"=>LotReference(key),"Program"=>new(CommercialReferenceKind.Program,key),
        "Tool"=>new(CommercialReferenceKind.Tool,key),"Machine"=>new(CommercialReferenceKind.Machine,key),"Location"=>new(CommercialReferenceKind.Location,key),
        "EngineeringChange"=>new(CommercialReferenceKind.EngineeringChange,key),
        "Item"=>new(CommercialReferenceKind.Item,key),"WorkCenter"=>new(CommercialReferenceKind.WorkCenter,key),"QmsRevision"=>SplitRevision(key,CommercialReferenceKind.QmsRevision),
        "DocumentRevision"=>SplitRevision(key,CommercialReferenceKind.DocumentRevision),
        "ItemRevision"=>SplitRevision(key,CommercialReferenceKind.PartRevision),
        "Ncr" or "NCR"=>new(CommercialReferenceKind.Ncr,key),"Capa" or "CAPA"=>new(CommercialReferenceKind.Capa,key),
        "AcceptanceInspection"=>new(CommercialReferenceKind.AcceptanceInspection,key),"ProductReleaseDecision"=>new(CommercialReferenceKind.ProductReleaseDecision,key),
        "Inspection"=>new(CommercialReferenceKind.Inspection,key),"Hold"=>new(CommercialReferenceKind.Hold,key),
        "CommercialQuality"=>SplitRevision(key,CommercialReferenceKind.CommercialRecord),_=>null
    };
    private static CommercialReference? SplitRevision(string key,CommercialReferenceKind kind)
    { var split=key.LastIndexOf('/');return split<=0?null:new(kind,key[..split],Revision:key[(split+1)..]); }
    private static CommercialReference? LotReference(string key)
    {
        var fields=key.Split(':');if(fields.Length!=2)return null;
        try
        {
            var part=Uri.UnescapeDataString(fields[0]);var lot=Uri.UnescapeDataString(fields[1]);
            return CanonicalIdentification.EncodeKey(part)==fields[0]&&CanonicalIdentification.EncodeKey(lot)==fields[1]?new(CommercialReferenceKind.Lot,part+"/"+lot):null;
        }
        catch(UriFormatException){return null;}
    }
    private void Permit(ForgePrincipal? principal,string permission,ResourceRef resource)
    { if(principal is null)return;var result=authorizer.Authorize(principal,permission,resource);if(!result.IsPermitted)throw new ForgeAuthorizationException(result,resource); }
    private static long Number(string key)
    { if(!long.TryParse(key,NumberStyles.None,CultureInfo.InvariantCulture,out var number)||number<=0||Number(number)!=key)throw Missing();return number; }
    private static string Number(long number)=>number.ToString(CultureInfo.InvariantCulture);
    private static ProgramId? Program(Guid? id)=>id is{} actual?new ProgramId(actual):null;
    private static CommandRejectedException Missing()=>new("commercial_source_not_found","An actual commercial evidence source is unavailable.");
}

