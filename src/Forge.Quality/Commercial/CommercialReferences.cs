using System.Globalization;
using Forge.Quality.Contracts;

namespace Forge.Quality.Commercial;

public static class CommercialReferences
{
    public static IReadOnlyList<CommercialReference> Of(CommercialContent content)
    {
        var references = new List<CommercialReference>();
        void Add(CommercialReferenceKind kind, long number) => references.Add(new(kind, number.ToString(CultureInfo.InvariantCulture)));
        void Part(string? part, string? revision) { if (part is not null) references.Add(revision is null ? new(CommercialReferenceKind.Item,part) : new(CommercialReferenceKind.PartRevision, part, Revision: revision)); }
        if (content.CustomerRequirements is { } c) { if(c.ContractId is{} contract)Add(CommercialReferenceKind.Contract,contract); if(c.CustomerSource is{} customer)references.Add(customer); if(c.DeliveryId is{} delivery)Add(CommercialReferenceKind.Delivery,delivery); Part(c.PartNumber,c.PartRevision); references.AddRange(c.Applicability); references.AddRange(c.Requirements.SelectMany(r=>r.Evidence)); }
        if (content.DesignApplicability is { } d) { Part(d.PartNumber,d.Revision); references.AddRange(d.Evidence); references.AddRange(d.Controls.SelectMany(r=>r.Evidence)); }
        if (content.ProviderEvaluation is { } p) { references.Add(new(CommercialReferenceKind.Supplier,p.SupplierCode)); Part(p.Scope.PartNumber,p.Scope.PartRevision); references.AddRange(p.Evidence); if(p.Scope.ProcessCode is{} process)references.Add(new(CommercialReferenceKind.QmsRevision,process)); if(p.Scope.WorkCenterCode is{} center)references.Add(new(CommercialReferenceKind.WorkCenter,center)); }
        if (content.PropertyAgreement is { } a)
        {
            references.Add(a.Subject.Kind switch { PropertyUseKind.Tool=>new(CommercialReferenceKind.Tool,a.Subject.Key), PropertyUseKind.Machine=>new(CommercialReferenceKind.Machine,a.Subject.Key), _=>new(CommercialReferenceKind.Lot,a.Subject.PartNumber+"/"+a.Subject.Key) });
            references.Add(a.LegalOwner); references.AddRange(a.Evidence); references.AddRange(a.Preservation.SelectMany(r=>r.Evidence)); foreach(var order in a.PermittedOrders)Add(CommercialReferenceKind.Order,order); foreach(var center in a.PermittedWorkCenters)references.Add(new(CommercialReferenceKind.WorkCenter,center));
        }
        if (content.PropertyIncident is { } i) { references.Add(new(CommercialReferenceKind.CommercialRecord,"PropertyAgreement/"+i.AgreementCode,Revision:i.AgreementRevision.ToString(CultureInfo.InvariantCulture))); references.AddRange(i.Evidence); }
        if (content.Complaint is { } complaint) { references.Add(new(CommercialReferenceKind.Shipment,complaint.ShipmentId.ToString(CultureInfo.InvariantCulture),complaint.ShipmentLine)); references.AddRange(complaint.Evidence); }
        if (content.CustomerFeedback is { } f) { references.Add(f.CustomerSource); references.AddRange(f.Evidence); }
        if (content.PostDelivery is { } post) { Add(CommercialReferenceKind.Shipment,post.ShipmentId); references.AddRange(post.RelatedEvidence); }
        if (content.Communication is { } message) { references.Add(message.PartySource); references.Add(message.RelatedCase); references.AddRange(message.Evidence); if(message.Concession is{} concession){Add(CommercialReferenceKind.Ncr,concession.NcrId);references.Add(new(CommercialReferenceKind.Lot,concession.Subject.PartNumber+"/"+concession.Subject.LotNumber));references.Add(new(CommercialReferenceKind.Location,concession.Subject.LocationCode));} }
        return references.Distinct().ToArray();
    }
}

