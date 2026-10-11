using Forge.Engineering.Contracts;
using Forge.Kernel.Access;
using Forge.Procurement.Contracts;
using Forge.Quality.Contracts;

namespace Forge.Inventory.Contracts;

/// <summary>Where receipts into inspection stand (V3.11), for Inventory and for readers in other contexts.</summary>
public static class ReceivingQueries
{
    /// <summary>Where a receipt into inspection stands, with its supplier from the purchase order.</summary>
    public static async Task<ReceiptInspectionState> ReceiptStateAsync(IKernelUnitOfWork uow, InventoryTransaction receipt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(receipt);
        var inspections = await uow.Store<IReceivingLookup>().ListReceivingInspectionsAsync(receipt.TransactionId, ct);
        var supplier = receipt.PoNumber is { } po ? (await uow.Store<IProcurementLookup>().FindPurchaseOrderAsync(po, ct))?.SupplierCode : null;
        return new ReceiptInspectionState(receipt, supplier, inspections.Sum(i => i.Accepted), inspections.Sum(i => i.Rejected), inspections);
    }

    /// <summary>The FAI status of the received part revision from its supplier (not applicable without a supplier).</summary>
    public static async Task<(FaiStatus Status, string Subject)> FaiStatusAsync(IKernelUnitOfWork uow, InventoryTransaction receipt, string? supplierCode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(receipt);
        if (supplierCode is null)
        {
            return (FaiStatus.NotApplicable, receipt.PartNumber);
        }

        var revision = receipt.PartRevision
            ?? (await uow.Store<IEngineeringLookup>().ListRevisionsAsync(receipt.PartNumber, ct)).Where(r => r.Status == RevisionStatus.Released).Select(r => r.Revision).LastOrDefault()
            ?? "-";
        var subject = Fai.Subject(receipt.PartNumber, revision, supplierCode);
        var determinations = uow.Store<IDeterminationLookup>();
        var decisions = await determinations.ListDeterminationsAsync(DeterminationQuestion.Fai, subject, ct);
        var determination = decisions.Count == 0 ? null : decisions[0];
        return (Fai.StatusOf(supplierCode, determination, await determinations.ListFaisAsync(subject, ct)), subject);
    }

}
