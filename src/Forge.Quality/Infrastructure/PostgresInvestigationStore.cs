using System.Text.Json;
using Forge.Kernel.Commands;
using Forge.Kernel.Postgres;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Quality.Infrastructure;

internal sealed class PostgresInvestigationStore(PostgresUnitOfWork uow):IQualityInvestigationStore
{
 public async Task<TraceSnapshotBoundary> CaptureBoundaryAsync(CancellationToken ct)
 {
  if(!uow.IsReadOnly)throw new CommandRejectedException("trace_snapshot_required","Record the actual read-only repeatable-read source snapshot.");
  await using var command=uow.Command("SELECT pg_current_snapshot()::text,clock_timestamp(),transaction_timestamp()");await using var reader=await command.ExecuteReaderAsync(ct);await reader.ReadAsync(ct);return new(reader.GetString(0),reader.GetFieldValue<DateTimeOffset>(1),reader.GetFieldValue<DateTimeOffset>(2));
 }
 public async Task LockFindingAsync(long id,CancellationToken ct){await using var command=uow.Command("SELECT finding_id FROM quality.measurement_finding WHERE finding_id=$1 FOR UPDATE",id);await command.ExecuteScalarAsync(ct);}
 public async Task LockInvestigationAsync(long id,CancellationToken ct){await using var command=uow.Command("SELECT investigation_id FROM quality.trace_investigation WHERE investigation_id=$1 FOR UPDATE",id);await command.ExecuteScalarAsync(ct);}
 public async Task<MeasurementResourceFinding?> FindFindingAsync(long id,CancellationToken ct)=>(await ReadAsync<MeasurementResourceFinding>("SELECT payload::text FROM quality.measurement_finding WHERE finding_id=$1",ct,id)).SingleOrDefault();
 public Task<IReadOnlyList<MeasurementResourceFinding>> ListFindingsAsync(CancellationToken ct)=>ReadAsync<MeasurementResourceFinding>("SELECT payload::text FROM quality.measurement_finding ORDER BY finding_id",ct);
 public async Task<TraceabilityInvestigation?> FindInvestigationAsync(long id,CancellationToken ct)=>(await ReadAsync<TraceabilityInvestigation>("SELECT payload::text FROM quality.trace_investigation WHERE investigation_id=$1",ct,id)).SingleOrDefault();
 public Task<IReadOnlyList<TraceabilityInvestigation>> ListInvestigationsAsync(CancellationToken ct)=>ReadAsync<TraceabilityInvestigation>("SELECT payload::text FROM quality.trace_investigation ORDER BY investigation_id",ct);
 public async Task<TraceInvestigationSnapshot?> FindSnapshotAsync(long id,int number,CancellationToken ct)=>(await ReadAsync<TraceInvestigationSnapshot>("SELECT payload::text FROM quality.trace_snapshot WHERE investigation_id=$1 AND number=$2",ct,id,number)).SingleOrDefault();
 public Task<IReadOnlyList<TraceCandidateAssessment>> AssessmentsAsync(long id,int number,CancellationToken ct)=>ReadAsync<TraceCandidateAssessment>("SELECT payload::text FROM quality.trace_assessment WHERE investigation_id=$1 AND snapshot_number=$2 ORDER BY assessment_id",ct,id,number);
 public async Task<TraceCandidateAssessment?> FindAssessmentAsync(long id,CancellationToken ct)=>(await ReadAsync<TraceCandidateAssessment>("SELECT payload::text FROM quality.trace_assessment WHERE assessment_id=$1",ct,id)).SingleOrDefault();
 public Task<IReadOnlyList<InvestigationHoldLink>> HoldLinksAsync(long id,CancellationToken ct)=>ReadAsync<InvestigationHoldLink>("SELECT payload::text FROM quality.trace_hold_link WHERE investigation_id=$1 ORDER BY snapshot_number,candidate_key,hold_id",ct,id);
 public async Task<long> InsertFindingAsync(MeasurementResourceFinding finding,CancellationToken ct)
 {var id=await AllocateAsync("quality.measurement_finding","finding_id",ct);var record=finding with{FindingId=id};await using var command=uow.Command("INSERT INTO quality.measurement_finding(finding_id,version,state,tool_code,payload) OVERRIDING SYSTEM VALUE VALUES($1,$2,$3,$4,$5::jsonb)",id,record.Version,record.State.ToString(),record.Content.ToolCode,CanonicalJson.Serialize(record));await command.ExecuteNonQueryAsync(ct);return id;}
 public Task UpdateFindingAsync(MeasurementResourceFinding record,int expected,CancellationToken ct)=>UpdateAsync("UPDATE quality.measurement_finding SET version=$2,state=$3,payload=$4::jsonb WHERE finding_id=$1 AND version=$5",record.FindingId,record.Version,record.State.ToString(),CanonicalJson.Serialize(record),expected,ct);
 public async Task<long> InsertInvestigationAsync(TraceabilityInvestigation investigation,CancellationToken ct)
 {var id=await AllocateAsync("quality.trace_investigation","investigation_id",ct);var record=investigation with{InvestigationId=id};await using var command=uow.Command("INSERT INTO quality.trace_investigation(investigation_id,version,state,payload) OVERRIDING SYSTEM VALUE VALUES($1,$2,$3,$4::jsonb)",id,record.Version,record.State.ToString(),CanonicalJson.Serialize(record));await command.ExecuteNonQueryAsync(ct);return id;}
 public Task UpdateInvestigationAsync(TraceabilityInvestigation record,int expected,CancellationToken ct)=>UpdateAsync("UPDATE quality.trace_investigation SET version=$2,state=$3,payload=$4::jsonb WHERE investigation_id=$1 AND version=$5",record.InvestigationId,record.Version,record.State.ToString(),CanonicalJson.Serialize(record),expected,ct);
 public async Task InsertSnapshotAsync(TraceInvestigationSnapshot snapshot,CancellationToken ct)
 {await using var command=uow.Command("INSERT INTO quality.trace_snapshot(investigation_id,number,payload) VALUES($1,$2,$3::jsonb)",snapshot.InvestigationId,snapshot.Number,CanonicalJson.Serialize(snapshot));await command.ExecuteNonQueryAsync(ct);}
 public async Task<long> InsertAssessmentAsync(TraceCandidateAssessment assessment,CancellationToken ct)
 {var id=await AllocateAsync("quality.trace_assessment","assessment_id",ct);var record=assessment with{AssessmentId=id};await using var command=uow.Command("INSERT INTO quality.trace_assessment(assessment_id,investigation_id,snapshot_number,candidate_key,version,state,payload) OVERRIDING SYSTEM VALUE VALUES($1,$2,$3,$4,$5,$6,$7::jsonb)",id,record.InvestigationId,record.SnapshotNumber,record.CandidateKey,record.Version,record.State.ToString(),CanonicalJson.Serialize(record));await command.ExecuteNonQueryAsync(ct);return id;}
 public Task UpdateAssessmentAsync(TraceCandidateAssessment record,int expected,CancellationToken ct)=>UpdateAsync("UPDATE quality.trace_assessment SET version=$2,state=$3,payload=$4::jsonb WHERE assessment_id=$1 AND version=$5",record.AssessmentId,record.Version,record.State.ToString(),CanonicalJson.Serialize(record),expected,ct);
 public async Task InsertHoldLinkAsync(InvestigationHoldLink link,CancellationToken ct)
 {await using var command=uow.Command("INSERT INTO quality.trace_hold_link(investigation_id,snapshot_number,candidate_key,assessment_id,hold_id,payload) VALUES($1,$2,$3,$4,$5,$6::jsonb)",link.InvestigationId,link.SnapshotNumber,link.CandidateKey,link.AssessmentId,link.HoldId,CanonicalJson.Serialize(link));await command.ExecuteNonQueryAsync(ct);}
 public async Task<IReadOnlyList<long>> LegacyInspectionIdsForToolAsync(string tool,DateTimeOffset? from,DateTimeOffset? to,DateTimeOffset cutoff,long after,int page,CancellationToken ct)
 {var ids=new List<long>();await using var command=uow.Command("SELECT inspection_id FROM quality.inspection WHERE gauge_code=$1 AND ($2::timestamptz IS NULL OR at>=$2) AND ($3::timestamptz IS NULL OR at<=$3) AND at<=$4 AND inspection_id>$5 ORDER BY inspection_id LIMIT $6",tool,Time(from),Time(to),cutoff,after,Math.Clamp(page,1,1000));await using var reader=await command.ExecuteReaderAsync(ct);while(await reader.ReadAsync(ct))ids.Add(reader.GetInt64(0));return ids;}
 public Task<IReadOnlyList<AcceptanceInspection>> AcceptanceInspectionsForToolAsync(string tool,DateTimeOffset? from,DateTimeOffset? to,DateTimeOffset cutoff,long after,int page,CancellationToken ct)=>ReadAsync<AcceptanceInspection>("SELECT payload::text FROM quality.acceptance_inspection WHERE EXISTS(SELECT 1 FROM jsonb_array_elements(payload->'results') r WHERE r->>'gaugeCode'=$1) AND ($2::timestamptz IS NULL OR (payload->>'at')::timestamptz>=$2) AND ($3::timestamptz IS NULL OR (payload->>'at')::timestamptz<=$3) AND (payload->>'at')::timestamptz<=$4 AND inspection_id>$5 ORDER BY inspection_id LIMIT $6",ct,tool,Time(from),Time(to),cutoff,after,Math.Clamp(page,1,1000));
 private async Task<long> AllocateAsync(string table,string column,CancellationToken ct)
 {await using var command=uow.Command("SELECT nextval(pg_get_serial_sequence($1,$2))",table,column);return(long)(await command.ExecuteScalarAsync(ct))!;}
 private async Task UpdateAsync(string sql,long id,int version,string state,string json,int expected,CancellationToken ct)
 {await using var command=uow.Command(sql,id,version,state,json,expected);if(await command.ExecuteNonQueryAsync(ct)!=1)throw new CommandRejectedException("stale_version","The governed investigation record changed.");}
 private async Task<IReadOnlyList<T>> ReadAsync<T>(string sql,CancellationToken ct,params object?[] parameters)
 {var records=new List<T>();await using var command=uow.Command(sql,parameters);await using var reader=await command.ExecuteReaderAsync(ct);while(await reader.ReadAsync(ct))records.Add(JsonSerializer.Deserialize<T>(reader.GetString(0),CanonicalJson.Options)!);return records;}
 private static NpgsqlParameter Time(DateTimeOffset? at)=>new(){NpgsqlDbType=NpgsqlDbType.TimestampTz,Value=(object?)at??DBNull.Value};
}
