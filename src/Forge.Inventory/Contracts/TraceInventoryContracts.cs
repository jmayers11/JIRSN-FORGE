namespace Forge.Inventory.Contracts;

/// <summary>Complete immutable ledger interval, inclusive at the declared cutoff; not a capped display list.</summary>
public interface ITraceInventoryLookup
{
    Task<IReadOnlyList<InventoryTransaction>> TransactionsAtCutoffAsync(DateTimeOffset cutoff,CancellationToken ct);
}
