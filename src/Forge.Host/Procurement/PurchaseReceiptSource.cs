using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Procurement.Contracts;

namespace Forge.Host.Procurement;

/// <summary>Received quantity per purchase order line, from Inventory, for the three-way match (owner decisions 2026-11-01).</summary>
public sealed class PurchaseReceiptSource : IPurchaseReceiptSource
{
    public async Task<IReadOnlyDictionary<(long PoNumber, int LineNumber), decimal>> ReceivedAsync(IKernelUnitOfWork uow, long poNumber, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        return (await uow.Store<IInventoryLookup>().ListPurchaseReceiptsAsync(ct)).Where(r => r.PoNumber == poNumber)
            .GroupBy(r => (r.PoNumber, r.LineNumber)).ToDictionary(g => g.Key, g => g.Sum(r => r.Received));
    }
}
