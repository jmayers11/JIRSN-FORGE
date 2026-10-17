using System.Text.Json;
using Forge.Kernel.Commands;
using Forge.Kernel.Postgres;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Quality.Infrastructure;

internal sealed class PostgresAcceptanceStore(PostgresUnitOfWork uow):IAcceptanceStore
{
 public async Task LockCriteriaScopeAsync(string facility,string part,string revision,bool shared,CancellationToken ct)
 {
  await using var command=uow.Command(shared?"SELECT pg_advisory_xact_lock_shared(hashtextextended($1,0))":"SELECT pg_advisory_xact_lock(hashtextextended($1,0))",$"forge.acceptance:{facility}:{part}:{revision}");await command.ExecuteScalarAsync(ct);
 }
 public async Task LockInspectionAsync(long inspectionId,CancellationToken ct){await using var command=uow.Command("SELECT inspection_id FROM quality.acceptance_inspection WHERE inspection_id=$1 FOR UPDATE",inspectionId);await command.ExecuteScalarAsync(ct);}
 public Task<IReadOnlyList<AcceptanceCriteriaRevision>> ListCriteriaAsync(CancellationToken ct)=>ReadAsync<AcceptanceCriteriaRevision>("SELECT jsonb_build_object('code',code,'revision',revision,'version',version,'state',state,'facilityCode',facility_code,'programId',program_id,'markings',markings,'content',content,'createdBy',created_by,'lastEditedBy',last_edited_by,'approvedBy',approved_by,'effectiveFrom',effective_from,'effectiveTo',effective_to,'sources',sources,'history',history)::text FROM quality.acceptance_criteria ORDER BY code,revision",ct);
 public async Task<AcceptanceCriteriaRevision?> FindCriteriaAsync(string code,int revision,CancellationToken ct)=>(await ListCriteriaAsync(ct)).FirstOrDefault(c=>c.Code==code&&c.Revision==revision);
 public async Task InsertCriteriaAsync(AcceptanceCriteriaRevision c,CancellationToken ct)
 {
  await using var command=uow.Command("INSERT INTO quality.acceptance_criteria(code,revision,version,state,facility_code,program_id,markings,content,created_by,last_edited_by,sources,history) VALUES($1,$2,1,'Draft',$3,$4,$5,$6::jsonb,$7,$8,$9::jsonb,$10::jsonb)",c.Code,c.Revision,c.FacilityCode,Uuid(c.ProgramId),(int)c.Markings,CanonicalJson.Serialize(c.Content),c.CreatedBy,c.LastEditedBy,CanonicalJson.Serialize(c.Sources),CanonicalJson.Serialize(c.History));await command.ExecuteNonQueryAsync(ct);
 }
 public async Task UpdateCriteriaAsync(AcceptanceCriteriaRevision c,int expectedVersion,CancellationToken ct)
 {
  await using var command=uow.Command("UPDATE quality.acceptance_criteria SET version=$3,state=$4,content=$5::jsonb,last_edited_by=$6,approved_by=$7,effective_from=$8,effective_to=$9,sources=$10::jsonb,history=$11::jsonb WHERE code=$1 AND revision=$2 AND version=$12",c.Code,c.Revision,c.Version,c.State.ToString(),CanonicalJson.Serialize(c.Content),c.LastEditedBy,Text(c.ApprovedBy),Time(c.EffectiveFrom),Time(c.EffectiveTo),CanonicalJson.Serialize(c.Sources),CanonicalJson.Serialize(c.History),expectedVersion);
  if(await command.ExecuteNonQueryAsync(ct)!=1)throw new CommandRejectedException("stale_version","The acceptance arrangement changed.");
 }
 public async Task<AcceptanceInspection?> FindInspectionAsync(long id,CancellationToken ct)=>(await ReadAsync<AcceptanceInspection>("SELECT payload::text FROM quality.acceptance_inspection WHERE inspection_id=$1",ct,id)).SingleOrDefault();
 public Task<IReadOnlyList<AcceptanceInspection>> ListInspectionsAsync(int take,CancellationToken ct)=>ReadAsync<AcceptanceInspection>("SELECT payload::text FROM quality.acceptance_inspection ORDER BY inspection_id DESC LIMIT $1",ct,take);
 public async Task<AcceptanceInspection?> FindInspectionForNcrAsync(long ncrId,CancellationToken ct)=>(await ReadAsync<AcceptanceInspection>("SELECT payload::text FROM quality.acceptance_inspection WHERE ncr_id=$1",ct,ncrId)).SingleOrDefault();
 public Task<IReadOnlyList<AcceptanceInspection>> InspectionsForAsync(AcceptanceSubjectKey key,CancellationToken ct)=>ReadAsync<AcceptanceInspection>("SELECT payload::text FROM quality.acceptance_inspection WHERE part_number=$1 AND lot_number=$2 AND location_code=$3 AND return_cohort_id IS NOT DISTINCT FROM $4::bigint ORDER BY inspection_id",ct,key.PartNumber,key.LotNumber,key.LocationCode,Long(key.ReturnCohortId));
 public async Task<ProductReleaseDecision?> FindDecisionAsync(long id,CancellationToken ct)=>(await ReadAsync<ProductReleaseDecision>("SELECT payload::text FROM quality.product_release_decision WHERE decision_id=$1",ct,id)).SingleOrDefault();
 public Task<IReadOnlyList<ProductReleaseDecision>> ListDecisionsAsync(int take,CancellationToken ct)=>ReadAsync<ProductReleaseDecision>("SELECT payload::text FROM quality.product_release_decision ORDER BY decision_id DESC LIMIT $1",ct,take);
 public Task<IReadOnlyList<ProductReleaseDecision>> DecisionsForAsync(AcceptanceSubjectKey key,CancellationToken ct)=>ReadAsync<ProductReleaseDecision>("SELECT payload::text FROM quality.product_release_decision WHERE part_number=$1 AND lot_number=$2 AND location_code=$3 AND return_cohort_id IS NOT DISTINCT FROM $4::bigint ORDER BY decision_id",ct,key.PartNumber,key.LotNumber,key.LocationCode,Long(key.ReturnCohortId));
 public async Task<ProductReleaseWithdrawal?> FindWithdrawalAsync(long id,CancellationToken ct)=>(await ReadAsync<ProductReleaseWithdrawal>("SELECT payload::text FROM quality.product_release_withdrawal WHERE decision_id=$1",ct,id)).SingleOrDefault();
 public async Task<bool> HasOpenNcrAsync(AcceptanceSubjectKey key,CancellationToken ct){await using var command=uow.Command("SELECT EXISTS(SELECT 1 FROM quality.ncr n LEFT JOIN quality.acceptance_inspection i ON i.ncr_id=n.ncr_id WHERE n.part_number=$1 AND n.lot_number=$2 AND n.status<>'Closed' AND (i.inspection_id IS NULL OR i.return_cohort_id IS NULL OR i.return_cohort_id=$3::bigint))",key.PartNumber,key.LotNumber,Long(key.ReturnCohortId));return(bool)(await command.ExecuteScalarAsync(ct))!;}
 public async Task InsertWithdrawalAsync(ProductReleaseWithdrawal withdrawal,CancellationToken ct){await using var command=uow.Command("INSERT INTO quality.product_release_withdrawal(decision_id,payload) VALUES($1,$2::jsonb)",withdrawal.DecisionId,CanonicalJson.Serialize(withdrawal));await command.ExecuteNonQueryAsync(ct);}
 public async Task<long> InsertInspectionAsync(AcceptanceInspection inspection,CancellationToken ct)
 {
  // Allocate first, then write the immutable final payload; no placeholder positive evidence row.
  await using var allocate=uow.Command("SELECT nextval(pg_get_serial_sequence('quality.acceptance_inspection','inspection_id'))");var id=(long)(await allocate.ExecuteScalarAsync(ct))!;
  var record=inspection with{InspectionId=id};await using var command=uow.Command("INSERT INTO quality.acceptance_inspection(inspection_id,part_number,lot_number,location_code,criteria_code,criteria_revision,quantity,passed,ncr_id,return_cohort_id,payload) OVERRIDING SYSTEM VALUE VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11::jsonb)",id,record.Subject.Key.PartNumber,record.Subject.Key.LotNumber,record.Subject.Key.LocationCode,record.CriteriaCode,record.CriteriaRevision,record.Quantity,record.Passed,new NpgsqlParameter{NpgsqlDbType=NpgsqlDbType.Bigint,Value=(object?)record.NcrId??DBNull.Value},Long(record.Subject.Key.ReturnCohortId),CanonicalJson.Serialize(record));await command.ExecuteNonQueryAsync(ct);return id;
 }
 public async Task<long> InsertDecisionAsync(ProductReleaseDecision decision,CancellationToken ct)
 {
  await using var allocate=uow.Command("SELECT nextval(pg_get_serial_sequence('quality.product_release_decision','decision_id'))");var id=(long)(await allocate.ExecuteScalarAsync(ct))!;var record=decision with{DecisionId=id};
  await using var command=uow.Command("INSERT INTO quality.product_release_decision(decision_id,part_number,lot_number,location_code,quantity,return_cohort_id,payload) OVERRIDING SYSTEM VALUE VALUES($1,$2,$3,$4,$5,$6,$7::jsonb)",id,record.Subject.PartNumber,record.Subject.LotNumber,record.Subject.LocationCode,record.Quantity,Long(record.Subject.ReturnCohortId),CanonicalJson.Serialize(record));await command.ExecuteNonQueryAsync(ct);
  foreach(var evidence in record.InspectionIds){await using var link=uow.Command("INSERT INTO quality.product_release_evidence(decision_id,inspection_id) VALUES($1,$2)",id,evidence);await link.ExecuteNonQueryAsync(ct);}return id;
 }
 private async Task<IReadOnlyList<T>> ReadAsync<T>(string sql,CancellationToken ct,params object?[] args)
 {
  var list=new List<T>();await using var command=uow.Command(sql,args);await using var reader=await command.ExecuteReaderAsync(ct);while(await reader.ReadAsync(ct))list.Add(JsonSerializer.Deserialize<T>(reader.GetString(0),CanonicalJson.Options)!);return list;
 }
 private static NpgsqlParameter Long(long? value)=>new(){NpgsqlDbType=NpgsqlDbType.Bigint,Value=(object?)value??DBNull.Value};
 private static NpgsqlParameter Uuid(Guid? value)=>new(){NpgsqlDbType=NpgsqlDbType.Uuid,Value=(object?)value??DBNull.Value};
 private static NpgsqlParameter Text(string? value)=>new(){NpgsqlDbType=NpgsqlDbType.Text,Value=(object?)value??DBNull.Value};
 private static NpgsqlParameter Time(DateTimeOffset? value)=>new(){NpgsqlDbType=NpgsqlDbType.TimestampTz,Value=(object?)value?.ToUniversalTime()??DBNull.Value};
}
