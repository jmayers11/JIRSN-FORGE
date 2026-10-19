using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Planning;
using Forge.Quality.Contracts;
using Forge.Resources.Contracts;

namespace Forge.Host.Commercial;

public sealed partial class CommercialQualitySourceResolver
{
    public async Task<CommercialCustomerContext> CustomerConcessionContextAsync(IKernelUnitOfWork uow,ForgePrincipal principal,
        CustomerConcessionScope concession,CancellationToken ct)
    {
        var inspection=await uow.Store<IAcceptanceLookup>().FindInspectionForNcrAsync(concession.NcrId,ct)??throw Missing();
        var ncr=await uow.Store<IQualityLookup>().FindNcrAsync(concession.NcrId,ct)??throw Missing();
        if(inspection.Passed||inspection.Subject.Key!=concession.Subject||inspection.Subject.Revision!=concession.Revision
            ||concession.Quantity<=0||concession.Quantity>inspection.Quantity||concession.Quantity>ncr.Quantity)
            throw new CommandRejectedException("customer_concession_scope","The departure must name the exact failed inspection/NCR, revision and bounded quantity.");
        await services.GetRequiredService<IAcceptanceSourceAccess>().RequireNcrSourceReadAsync(uow,principal,ncr.NcrId,ct);
        var customerSources=new List<CommercialReference>();
        if(concession.Subject.ReturnCohortId is{} cohortId)
        {
            var cohort=await uow.Store<IReturnAcceptanceLookup>().FindCohortAsync(cohortId,ct)??throw Missing();
            if(cohort.Subject!=concession.Subject||cohort.Revision!=concession.Revision)throw Missing();
            var returned=await uow.Store<IReceivingLookup>().FindCustomerReturnAsync(cohort.RmaId,ct)??throw Missing();
            customerSources.Add(new(CommercialReferenceKind.Shipment,Number(returned.ShipmentId)));
            await AddShipmentContractAsync(returned.ShipmentId);
        }
        else
        {
            foreach(var number in inspection.Subject.OriginOrderNumbers)
            {
                var order=await uow.Store<IExecutionLookup>().FindOrderAsync(number,ct)??throw Missing();
                if(order.ContractId is{} contract)customerSources.Add(new(CommercialReferenceKind.Contract,Number(contract)));
            }
            // Existing shipment ownership supplies customer identity for actual delivered product without a contract.
            foreach(var shipment in (await uow.Store<IInventoryLookup>().ListShipmentsAsync(true,ct)).Where(s=>s.Status==ShipmentStatus.Shipped
                &&s.Lines.Any(l=>l.PartNumber==concession.Subject.PartNumber&&l.LotNumber==concession.Subject.LotNumber)))
            { customerSources.Add(new(CommercialReferenceKind.Shipment,Number(shipment.ShipmentId)));await AddShipmentContractAsync(shipment.ShipmentId); }
        }
        var all=customerSources.Append(new(CommercialReferenceKind.Ncr,Number(ncr.NcrId)))
            .Append(new(CommercialReferenceKind.AcceptanceInspection,Number(inspection.InspectionId))).Distinct().ToArray();
        var actual=await ResolveReferencesAsync(uow,principal,inspection.Subject.Source.Context.FacilityCode,all,ct);
        return new(concession,actual.FacilityCode,customerSources.Distinct().ToArray(),actual.Sources,actual.Ownership,
            CanonicalJson.Fingerprint(new{concession,Inspection=inspection.InspectionId,inspection.Subject.ConfigurationFingerprint,actual.Fingerprint,Customers=customerSources.Distinct().ToArray()}));

        async Task AddShipmentContractAsync(long shipmentId)
        {
            var shipment=await uow.Store<IInventoryLookup>().FindShipmentAsync(shipmentId,ct)??throw Missing();
            if(shipment.DemandId is not{} demandId)return;
            var demand=await uow.Store<IDemandStore>().FindAsync(demandId,ct)??throw Missing();
            if(demand.SourceKey?.StartsWith("delivery:",StringComparison.Ordinal)!=true)return;
            var delivery=await uow.Store<Forge.Programs.Contracts.IProgramsLookup>().FindDeliveryAsync(Number(demand.SourceKey[8..]),ct)??throw Missing();
            customerSources.Add(new(CommercialReferenceKind.Contract,Number(delivery.Delivery.ContractId)));
        }
    }
    private async Task ValidateReceivedCustomerApprovalAsync(IKernelUnitOfWork uow,ForgePrincipal principal,CommercialRevision record,
        IReadOnlyList<CommercialReference> evidence,DateTimeOffset at,CancellationToken ct)
    {
        var communication=record.Content.Communication;
        if(communication?.Concession is not{} concession||communication.Audience!=CommunicationAudience.Customer)
            throw new CommandRejectedException("customer_concession_scope","The received customer approval must bind an independently approved exact departure.");
        var context=await CustomerConcessionContextAsync(uow,principal,concession,ct);
        if(!context.CustomerSources.Contains(communication.PartySource))throw Missing();
        var party=await ResolveReferencesAsync(uow,principal,record.FacilityCode,[communication.PartySource],ct);
        var found=false;
        foreach(var reference in evidence.Where(r=>r.Kind==CommercialReferenceKind.DocumentRevision))
        {
            var document=await uow.Store<IDocumentLookup>().FindDocumentAsync(reference.Key,ct)??throw Missing();
            var revision=document.Revisions.FirstOrDefault(r=>r.Revision==reference.Revision)??throw Missing();
            var date=(await uow.SiteDateAsync(at, ct));
            if(document.Authority!=DocumentAuthority.External||document.ExternalAuthority!=party.Customer||string.IsNullOrWhiteSpace(document.ExternalNumber)
                ||document.Current?.Revision!=revision.Revision||revision.Status!=DocumentRevisionStatus.Applicable||revision.FileSha256 is null
                ||revision.EffectiveFrom is{} from&&date<from||revision.EffectiveTo is{} to&&date>=to)continue;
            if(await uow.Store<IDocumentLookup>().FindFileAsync(revision.FileSha256,ct) is null)throw Missing();
            found=true;
        }
        if(!found)throw new CommandRejectedException("customer_approval_document_required","Retain an actual currently applicable external document from this customer identifying the approved exact departure; internal QA approval is separate.");
    }
}
