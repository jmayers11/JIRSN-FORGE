using Forge.Kernel.Access;
using Forge.Kernel.Postgres;
using Forge.Quality.Contracts;

namespace Forge.Host.Commercial;

public sealed partial class CommercialQualitySourceResolver
{
    private static async Task LockReferenceAsync(IKernelUnitOfWork uow,CommercialReference reference,CancellationToken ct)
    {
        // This bridge only locks owner rows; all facts/classification still come from their typed Contracts lookups.
        if(uow is not PostgresUnitOfWork pg||pg.IsReadOnly)return;
        string? sql=null;object[] values=[reference.Key];
        switch(reference.Kind)
        {
            case CommercialReferenceKind.Program:sql="SELECT program_id FROM programs.program WHERE program_id=$1::uuid FOR SHARE";break;
            case CommercialReferenceKind.Supplier:sql="SELECT code FROM procurement.supplier WHERE code=$1 FOR SHARE";break;
            case CommercialReferenceKind.Contract:sql="SELECT contract_id FROM programs.contract WHERE contract_id=$1::bigint FOR SHARE";break;
            case CommercialReferenceKind.Delivery:sql="SELECT d.delivery_id FROM programs.delivery d JOIN programs.contract c USING(contract_id) WHERE d.delivery_id=$1::bigint FOR SHARE OF d,c";break;
            case CommercialReferenceKind.Order:sql="SELECT order_number FROM execution.production_order WHERE order_number=$1::bigint FOR SHARE";break;
            case CommercialReferenceKind.PurchaseOrder:sql="SELECT po_number FROM procurement.purchase_order WHERE po_number=$1::bigint FOR UPDATE";break;
            case CommercialReferenceKind.Item:sql="SELECT part_number FROM engineering.item WHERE part_number=$1 FOR SHARE";break;
            case CommercialReferenceKind.PartRevision:sql="SELECT part_number FROM engineering.revision WHERE part_number=$1 AND revision=$2 FOR SHARE";values=[reference.Key,reference.Revision??string.Empty];break;
            case CommercialReferenceKind.DocumentRevision:sql="SELECT d.number FROM engineering.document d JOIN engineering.document_revision r ON r.document_number=d.number WHERE d.number=$1 AND r.revision=$2 FOR SHARE OF d,r";values=[reference.Key,reference.Revision??string.Empty];break;
            case CommercialReferenceKind.Tool:sql="SELECT code FROM resources.tool WHERE code=$1 FOR SHARE";break;
            case CommercialReferenceKind.Machine:sql="SELECT code FROM resources.machine WHERE code=$1 FOR SHARE";break;
            case CommercialReferenceKind.WorkCenter:sql="SELECT code FROM resources.work_center WHERE code=$1 FOR SHARE";break;
            case CommercialReferenceKind.Location:sql="SELECT code FROM inventory.location WHERE code=$1 FOR SHARE";break;
            case CommercialReferenceKind.Shipment:sql="SELECT shipment_id FROM inventory.shipment WHERE shipment_id=$1::bigint FOR SHARE";break;
            case CommercialReferenceKind.CustomerReturn:sql="SELECT rma_id FROM inventory.customer_return WHERE rma_id=$1::bigint FOR SHARE";break;
            case CommercialReferenceKind.QmsRevision:sql="SELECT d.code FROM quality.qms_record d JOIN quality.qms_revision r USING(code) WHERE d.code=$1 AND ($2::text='' OR r.revision::text=$2) FOR SHARE OF d,r";values=[reference.Key,reference.Revision??string.Empty];break;
        }
        if(sql is null)return;
        await using var command=pg.Command(sql,values);await command.ExecuteNonQueryAsync(ct);
    }
}

