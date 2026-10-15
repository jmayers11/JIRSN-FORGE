using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;
using Forge.Resources.Contracts;

namespace Forge.Host.Commercial;

public sealed partial class CommercialQualitySourceResolver
{
    public async Task ValidateObservationAsync(IKernelUnitOfWork uow,ForgePrincipal principal,CommercialRevision record,
        CommercialObservationKind observation,IReadOnlyList<CommercialReference> evidence,DateTimeOffset at,CancellationToken ct)
    {
        await ResolveReferencesAsync(uow,principal,record.FacilityCode,evidence,ct);
        if(observation==CommercialObservationKind.CustomerApprovalReceived)
            await ValidateReceivedCustomerApprovalAsync(uow,principal,record,evidence,at,ct);
        if(record.Content.Complaint is not null||record.Content.PostDelivery is not null)
        {
            var shipment=record.Content.Complaint?.ShipmentId??record.Content.PostDelivery!.ShipmentId;
            foreach(var reference in evidence.Where(r=>r.Kind==CommercialReferenceKind.CustomerReturn))
                if((await uow.Store<IReceivingLookup>().FindCustomerReturnAsync(Number(reference.Key),ct))?.ShipmentId!=shipment)
                    throw new CommandRejectedException("case_source_mismatch","Return evidence must belong to this actual original shipment.");
        }
        if(record.Content.PropertyAgreement is not{} agreement)return;
        if(observation is not(CommercialObservationKind.PropertyReceipt or CommercialObservationKind.PropertyUse or CommercialObservationKind.PropertyReturn))return;
        var matched=false;
        foreach(var reference in evidence)
        {
            if(agreement.Subject.Kind==PropertyUseKind.Tool&&reference.Kind==CommercialReferenceKind.ToolEvent)
            {
                var fields=reference.Key.Split('/');
                if(fields.Length!=2||fields[0]!=agreement.Subject.Key)continue;
                var actual=(await uow.Store<IToolLookup>().ListToolEventsAsync(fields[0],ct)).FirstOrDefault(e=>e.EventId==Number(fields[1]));
                if(actual is null||actual.At>at)continue;
                matched|=observation switch
                {
                    CommercialObservationKind.PropertyUse=>actual.Kind==ToolEventKind.Used,
                    CommercialObservationKind.PropertyReceipt=>actual.Kind is ToolEventKind.Registered or ToolEventKind.Moved or ToolEventKind.Returned,
                    CommercialObservationKind.PropertyReturn=>actual.Kind is ToolEventKind.Moved or ToolEventKind.SentForService,
                    _=>false
                };
            }
            if(agreement.Subject.Kind==PropertyUseKind.Material&&reference.Kind is CommercialReferenceKind.InventoryTransaction or CommercialReferenceKind.Receipt)
            {
                var actual=await uow.Store<IReceivingLookup>().FindTransactionAsync(Number(reference.Key),ct);
                if(actual is null||actual.PartNumber!=agreement.Subject.PartNumber||actual.LotNumber!=agreement.Subject.Key||actual.At>at)continue;
                matched|=observation switch
                {
                    CommercialObservationKind.PropertyReceipt=>actual.Kind is InventoryTransactionKind.Receipt or InventoryTransactionKind.CustomerReturn or InventoryTransactionKind.Transfer,
                    CommercialObservationKind.PropertyUse=>actual.Kind is InventoryTransactionKind.Issue or InventoryTransactionKind.MaintenanceIssue or InventoryTransactionKind.ProjectIssue,
                    CommercialObservationKind.PropertyReturn=>actual.Kind is InventoryTransactionKind.Shipment or InventoryTransactionKind.Transfer,
                    _=>false
                };
            }
            if(agreement.Subject.Kind==PropertyUseKind.Machine&&reference.Kind==CommercialReferenceKind.Machine&&reference.Key==agreement.Subject.Key)
                matched=true; // Machine registry is the actual owner; the observed physical receipt/return remains signed human evidence, not a custody mutation.
        }
        if(!matched)throw new CommandRejectedException("property_event_mismatch","Link the exact existing physical subject's recorded owning event and observed basis.");
    }
}

