using Forge.Inventory.Contracts;
using Forge.Kernel.Postgres;
using Npgsql;

namespace Forge.Inventory.Infrastructure;

internal sealed class PostgresProductReleaseStore(PostgresUnitOfWork uow):IProductReleaseInventoryStore
{
 public async Task LockShipmentAsync(long id,CancellationToken ct){await using var command=uow.Command("SELECT shipment_id FROM inventory.shipment WHERE shipment_id=$1 FOR UPDATE",id);await command.ExecuteScalarAsync(ct);}
 public async Task LockContainerAsync(string code,CancellationToken ct){await using var command=uow.Command("SELECT code FROM inventory.container WHERE code=$1 FOR UPDATE",code);await command.ExecuteScalarAsync(ct);}
 public async Task<decimal> ConsumedAsync(long id,CancellationToken ct){await using var command=uow.Command("SELECT COALESCE(sum(quantity),0) FROM inventory.product_release_consumption WHERE decision_id=$1",id);return(decimal)(await command.ExecuteScalarAsync(ct))!;}
 public async Task InsertConsumptionAsync(ProductReleaseConsumption c,CancellationToken ct)
 {
  await using var command=uow.Command("INSERT INTO inventory.product_release_consumption(decision_id,transaction_id,part_number,lot_number,location_code,quantity,actor,at,policy_fingerprint,evidence_fingerprint) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)",c.DecisionId,c.TransactionId,c.PartNumber,c.LotNumber,c.LocationCode,c.Quantity,c.Actor,c.At,c.PolicyFingerprint,c.EvidenceFingerprint);await command.ExecuteNonQueryAsync(ct);
 }
 public async Task<ReleasedScopeCoverage> CoverageAsync(string part,IReadOnlyList<string> connectedLots,CancellationToken ct)
 {
  var consumptions=new List<ProductReleaseConsumption>();await using(var command=uow.Command("SELECT decision_id,transaction_id,part_number,lot_number,location_code,quantity,actor,at,policy_fingerprint,evidence_fingerprint FROM inventory.product_release_consumption WHERE part_number=$1 AND lot_number=ANY($2::text[]) ORDER BY transaction_id",part,connectedLots.ToArray()))
  await using(var reader=await command.ExecuteReaderAsync(ct))while(await reader.ReadAsync(ct))consumptions.Add(new(reader.GetInt64(0),reader.GetInt64(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetDecimal(5),reader.GetString(6),reader.GetFieldValue<DateTimeOffset>(7),reader.GetString(8),reader.GetString(9)));
  // Actual stock and net used/returned quantities come only from the existing ledger; positive adjustments never invent approval.
  await using var balance=uow.Command("SELECT COALESCE(sum(quantity),0) FROM inventory.balance WHERE part_number=$1 AND lot_number=ANY($2::text[]) AND status='Released'",part,connectedLots.ToArray());var released=(decimal)(await balance.ExecuteScalarAsync(ct))!;
  await using var used=uow.Command("SELECT COALESCE(sum(CASE WHEN from_status='Released' AND kind NOT IN('Transfer','Split','Merge') THEN quantity ELSE 0 END),0)-COALESCE(sum(CASE WHEN to_status='Released' AND kind IN('Return','MaintenanceReturn','ProjectReturn') THEN quantity ELSE 0 END),0) FROM inventory.transaction WHERE part_number=$1 AND lot_number=ANY($2::text[])",part,connectedLots.ToArray());var netUsed=(decimal)(await used.ExecuteScalarAsync(ct))!;
  return new(consumptions,consumptions.Sum(c=>c.Quantity)-netUsed,released);
 }
}
