using Forge.Inventory.Contracts;
using Forge.Kernel.Postgres;

namespace Forge.Inventory.Infrastructure;

internal sealed class PostgresReturnAcceptanceStore(PostgresUnitOfWork uow):IReturnAcceptanceInventoryStore
{
 public async Task LockRmaAsync(long id,CancellationToken ct)
 {await using var command=uow.Command("SELECT rma_id FROM inventory.customer_return WHERE rma_id=$1 FOR UPDATE",id);await command.ExecuteScalarAsync(ct);}
 public Task<decimal> PreparedForRmaAsync(long id,CancellationToken ct)=>SumAsync("SELECT COALESCE(sum(quantity),0) FROM quality.return_inspection_cohort WHERE rma_id=$1",id,ct);
 public Task<decimal> ConsumedForRmaAsync(long id,CancellationToken ct)=>SumAsync("SELECT COALESCE(sum(quantity),0) FROM inventory.return_release_consumption WHERE rma_id=$1",id,ct);
 public Task<decimal> ConsumedForCohortAsync(long id,CancellationToken ct)=>SumAsync("SELECT COALESCE(sum(quantity),0) FROM inventory.return_release_consumption WHERE cohort_id=$1",id,ct);
 public Task<decimal> RejectedForCohortAsync(long id,CancellationToken ct)=>SumAsync("SELECT COALESCE(sum(quantity),0) FROM quality.acceptance_inspection WHERE return_cohort_id=$1 AND NOT passed",id,ct);
 private async Task<decimal> SumAsync(string sql,long id,CancellationToken ct)
 {await using var command=uow.Command(sql,id);return(decimal)(await command.ExecuteScalarAsync(ct))!;}
 public async Task InsertCohortConsumptionAsync(long cohort,long rma,long decision,long transaction,decimal quantity,CancellationToken ct)
 {await using var command=uow.Command("INSERT INTO inventory.return_release_consumption(cohort_id,rma_id,decision_id,transaction_id,quantity) VALUES($1,$2,$3,$4,$5)",cohort,rma,decision,transaction,quantity);await command.ExecuteNonQueryAsync(ct);}
}
