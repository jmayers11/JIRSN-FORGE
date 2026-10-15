using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;
using Forge.Quality.Commercial;

namespace Forge.Host.Commercial;

public sealed partial class CommercialQualitySourceResolver
{
    public async Task<CommercialCaseEvidence> ShipmentEvidenceAsync(IKernelUnitOfWork uow,ForgePrincipal principal,long shipmentId,int? line,CancellationToken ct)
    {
        var inventory=uow.Store<IInventoryLookup>();
        var shipment=await inventory.FindShipmentAsync(shipmentId,ct)??throw Missing();
        if(shipment.Status!=ShipmentStatus.Shipped)throw new CommandRejectedException("shipment_not_delivered","Post-delivery evidence requires an actual shipped shipment.");
        var root=new CommercialReference(CommercialReferenceKind.Shipment,Number(shipmentId),line);
        var scope=await ResolveReferencesAsync(uow,principal,(await inventory.FindLocationAsync(shipment.Lines[0].LocationCode,ct))?.SiteCode??throw Missing(),[root],ct);
        var references=new List<CommercialReference>{root};var gaps=new List<string>();
        var transactions=await inventory.ListGenealogyTransactionsAsync(ct);
        var links=await inventory.ListInstanceLinksAsync(null,null,ct);
        var subjects=new HashSet<(string Part,string Lot)>();var orders=new HashSet<long>();
        var queue=new Queue<(string Part,string Lot)>();
        foreach(var row in shipment.Lines.Where(row=>line is null||row.LineNumber==line))
            if(row.LotNumber is{} lot)queue.Enqueue((row.PartNumber,lot));else gaps.Add("A shipped line has no recorded physical lot/serial genealogy.");
        while(queue.TryDequeue(out var subject))
        {
            if(!subjects.Add(subject))continue;
            references.Add(new(CommercialReferenceKind.Lot,subject.Part+"/"+subject.Lot));
            foreach(var link in links.Where(link=>link.ResultPart==subject.Part&&link.ResultLot==subject.Lot))queue.Enqueue((link.SourcePart,link.SourceLot));
            foreach(var transaction in transactions.Where(t=>t.PartNumber==subject.Part&&t.LotNumber==subject.Lot))
            {
                references.Add(new(CommercialReferenceKind.InventoryTransaction,Number(transaction.TransactionId)));
                if(transaction.OrderNumber is{} order&&orders.Add(order))
                {
                    references.Add(new(CommercialReferenceKind.Order,Number(order)));
                    foreach(var input in transactions.Where(t=>t.OrderNumber==order&&t.Kind==InventoryTransactionKind.Issue&&t.LotNumber is not null))
                        queue.Enqueue((input.PartNumber,input.LotNumber!));
                }
                if(transaction.PoNumber is{} po)references.Add(new(CommercialReferenceKind.PurchaseOrder,Number(po),transaction.PoLine));
            }
        }
        var quality=await uow.Store<ICommercialCaseLookup>().QualityForAsync(orders,subjects.Select(s=>s.Part+"/"+s.Lot).ToArray(),ct);
        var facts=scope.Facts.ToList();var reads=scope.Sources.ToList();var visited=new HashSet<string>(StringComparer.Ordinal);
        foreach(var reference in references.Distinct())await ReferenceAsync(uow,principal,reference,facts,reads,visited,ct);
        foreach(var id in quality.InspectionIds)
        {
            var inspection=await uow.Store<IQualityLookup>().FindInspectionAsync(id,ct)??throw Missing();
            var owner=facts.First(f=>f.Reference.Kind==CommercialReferenceKind.Order&&f.Reference.Key==Number(inspection.OrderNumber));
            var resources=owner.Resources.Select(r=>r with{Type="Inspection",Id=Number(id)}).ToArray();
            foreach(var resource in resources)Permit(principal,Permissions.QualityRead,resource);
            var reference=new CommercialReference(CommercialReferenceKind.Inspection,Number(id));references.Add(reference);
            facts.Add(new(reference, "Inspection "+Number(id),resources,CanonicalJson.Fingerprint(inspection)));
        }
        foreach(var id in quality.NcrIds) { var reference=new CommercialReference(CommercialReferenceKind.Ncr,Number(id));references.Add(reference);await ReferenceAsync(uow,principal,reference,facts,reads,visited,ct); }
        foreach(var id in quality.CapaIds) { var reference=new CommercialReference(CommercialReferenceKind.Capa,Number(id));references.Add(reference);await ReferenceAsync(uow,principal,reference,facts,reads,visited,ct); }
        foreach(var returned in await uow.Store<IReceivingLookup>().ListCustomerReturnsAsync(shipmentId,ct))
        {
            var reference=new CommercialReference(CommercialReferenceKind.CustomerReturn,Number(returned.RmaId));references.Add(reference);
            await ReferenceAsync(uow,principal,reference,facts,reads,visited,ct);
        }
        foreach(var subject in subjects)
        {
            var locations=transactions.Where(t=>t.PartNumber==subject.Part&&t.LotNumber==subject.Lot).SelectMany(t=>new[]{t.FromLocation,t.ToLocation})
                .Where(l=>l is not null).Cast<string>().Distinct(StringComparer.Ordinal);
            foreach(var location in locations)
                foreach(var inspection in await uow.Store<IAcceptanceLookup>().InspectionsForAsync(new(subject.Part,subject.Lot,location),ct))
                {
                    var reference=new CommercialReference(CommercialReferenceKind.AcceptanceInspection,Number(inspection.InspectionId));
                    references.Add(reference);await ReferenceAsync(uow,principal,reference,facts,reads,visited,ct);
                }
        }
        if(orders.Count>0&&quality.InspectionIds.Count==0&&!references.Any(r=>r.Kind==CommercialReferenceKind.AcceptanceInspection))
            gaps.Add("No recorded inspection evidence was found for the recorded production/material trace.");
        return new(root,facts.Distinct().ToArray(),references.Distinct().ToArray(),gaps.Count==0,gaps.Distinct(StringComparer.Ordinal).ToArray());
    }
}

