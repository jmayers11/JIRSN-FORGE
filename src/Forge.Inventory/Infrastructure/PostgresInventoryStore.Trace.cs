using Forge.Inventory.Contracts;

namespace Forge.Inventory.Infrastructure;

internal sealed partial class PostgresInventoryStore:ITraceInventoryLookup
{
 public Task<IReadOnlyList<InventoryTransaction>> TransactionsAtCutoffAsync(DateTimeOffset cutoff,CancellationToken ct)=>ReadAsync(TransactionSql+" WHERE at<=$1 ORDER BY transaction_id",ReadTransaction,ct,cutoff);
}
