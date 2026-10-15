using System.Text.Json;
using Forge.Kernel.Postgres;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;

namespace Forge.Quality.Infrastructure;

internal sealed class PostgresReturnCohortStore(PostgresUnitOfWork uow):IReturnAcceptanceLookup,IReturnCohortWriter
{
 public async Task<long> AllocateCohortIdAsync(CancellationToken ct)
 {await using var command=uow.Command("SELECT nextval(pg_get_serial_sequence('quality.return_inspection_cohort','cohort_id'))");return(long)(await command.ExecuteScalarAsync(ct))!;}
 public async Task InsertCohortAsync(ReturnInspectionCohort cohort,CancellationToken ct)
 {
  await using var command=uow.Command("INSERT INTO quality.return_inspection_cohort(cohort_id,rma_id,return_transaction_id,preparation_transaction_id,quantity,payload) OVERRIDING SYSTEM VALUE VALUES($1,$2,$3,$4,$5,$6::jsonb)",cohort.CohortId,cohort.RmaId,cohort.OriginalReturnTransactionId,cohort.PreparationTransactionId,cohort.Quantity,CanonicalJson.Serialize(cohort));await command.ExecuteNonQueryAsync(ct);
 }
 public async Task<ReturnInspectionCohort?> FindCohortAsync(long id,CancellationToken ct)
 {await using var command=uow.Command("SELECT payload::text FROM quality.return_inspection_cohort WHERE cohort_id=$1",id);var json=await command.ExecuteScalarAsync(ct);return json is string text?JsonSerializer.Deserialize<ReturnInspectionCohort>(text,CanonicalJson.Options):null;}
 public async Task<IReadOnlyList<ReturnInspectionCohort>> CohortsForRmaAsync(long rmaId,CancellationToken ct)
 {var rows=new List<ReturnInspectionCohort>();await using var command=uow.Command("SELECT payload::text FROM quality.return_inspection_cohort WHERE rma_id=$1 ORDER BY cohort_id",rmaId);await using var reader=await command.ExecuteReaderAsync(ct);while(await reader.ReadAsync(ct))rows.Add(JsonSerializer.Deserialize<ReturnInspectionCohort>(reader.GetString(0),CanonicalJson.Options)!);return rows;}
}
