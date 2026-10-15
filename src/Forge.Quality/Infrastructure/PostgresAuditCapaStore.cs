using System.Text.Json;
using Forge.Kernel.Commands;
using Forge.Kernel.Postgres;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;
using Forge.Quality.Audit;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Quality.Infrastructure;

internal sealed class PostgresAuditCapaStore(PostgresUnitOfWork uow) : IAuditCapaStore
{
    public async Task LockAsync(string type,string key,CancellationToken ct)
    {
        await using var cmd=uow.Command("SELECT pg_advisory_xact_lock(hashtextextended($1,0))","audit:"+type+":"+key);
        await cmd.ExecuteScalarAsync(ct);
    }
    public async Task LockCapaAsync(long id,CancellationToken ct)
    {
        await LockAsync("Capa",id.ToString(System.Globalization.CultureInfo.InvariantCulture),ct);
        await using var cmd=uow.Command("SELECT capa_id FROM quality.capa WHERE capa_id=$1 FOR UPDATE",id);
        await cmd.ExecuteScalarAsync(ct);
    }
    private async Task<T?> Read<T>(string type,string id,CancellationToken ct) where T:class
    {
        await using var cmd=uow.Command("SELECT v.data::text FROM quality.audit_head h JOIN quality.audit_version v USING(entity_type,entity_id,version) WHERE h.entity_type=$1 AND h.entity_id=$2",type,id);
        var json=await cmd.ExecuteScalarAsync(ct) as string;
        return json is null?null:JsonSerializer.Deserialize<T>(json,CanonicalJson.Options);
    }
    private async Task<IReadOnlyList<T>> Children<T>(string type,string owner,CancellationToken ct) where T:class
    {
        var rows=new List<T>();
        await using var cmd=uow.Command("SELECT v.data::text FROM quality.audit_head h JOIN quality.audit_version v USING(entity_type,entity_id,version) WHERE h.entity_type=$1 AND h.owner_key=$2 ORDER BY h.entity_id",type,owner);
        await using var reader=await cmd.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))rows.Add(JsonSerializer.Deserialize<T>(reader.GetString(0),CanonicalJson.Options)!);
        return rows;
    }
    private async Task<QmsPlanningPage<T>> Page<T>(string type,int take,string? after,CancellationToken ct) where T:class
    {
        var rows=new List<T>();string? last=null;
        await using var cmd=uow.Command("SELECT h.entity_id,v.data::text FROM quality.audit_head h JOIN quality.audit_version v USING(entity_type,entity_id,version) WHERE h.entity_type=$1 AND h.entity_id>$2 ORDER BY h.entity_id LIMIT $3",type,after??"",take+1);
        await using var reader=await cmd.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))
        {
            if(rows.Count==take)return new(rows,last);
            last=reader.GetString(0);rows.Add(JsonSerializer.Deserialize<T>(reader.GetString(1),CanonicalJson.Options)!);
        }
        return new(rows,null);
    }
    private async Task Save<T>(string type,string id,string owner,int version,int? expected,T record,CancellationToken ct)
    {
        await LockAsync(type,id,ct);
        if(expected is null)
        {
            if(version!=1)throw new CommandRejectedException("audit_version","A new owner starts at version one.");
            await using var insert=uow.Command("INSERT INTO quality.audit_head(entity_type,entity_id,owner_key,version) VALUES($1,$2,$3,$4) ON CONFLICT DO NOTHING",type,id,owner,version);
            if(await insert.ExecuteNonQueryAsync(ct)!=1)throw new CommandRejectedException("audit_exists","This owner record already exists.");
        }
        else
        {
            if(version!=checked(expected.Value+1))throw new CommandRejectedException("stale_version","Read the current owner version.");
            await using var update=uow.Command("UPDATE quality.audit_head SET version=$3 WHERE entity_type=$1 AND entity_id=$2 AND version=$4",type,id,version,expected.Value);
            if(await update.ExecuteNonQueryAsync(ct)!=1)throw new CommandRejectedException("stale_version","The owner changed; read its current version.");
        }
        await using var write=uow.Command("INSERT INTO quality.audit_version(entity_type,entity_id,version,data) VALUES($1,$2,$3,$4::jsonb)",type,id,version,CanonicalJson.Serialize(record));
        await write.ExecuteNonQueryAsync(ct);
    }
    public async Task<QmsAuditProgram?> FindProgramAsync(string code,CancellationToken ct)
    {
        var header=await Read<QmsAuditProgram>("Program",code,ct);
        return header is null?null:header with{Revisions=(await Children<QmsAuditProgramRevision>("ProgramRevision",code,ct)).OrderBy(x=>x.Revision).ToArray()};
    }
    public async Task<QmsPlanningPage<QmsAuditProgram>> ProgramsAsync(int take,string? after,CancellationToken ct)
    {
        var page=await Page<QmsAuditProgram>("Program",take,after,ct);var items=new List<QmsAuditProgram>();
        foreach(var item in page.Items)items.Add((await FindProgramAsync(item.Code,ct))!);return new(items,page.NextCursor);
    }
    public Task SaveProgramAsync(QmsAuditProgram p,CancellationToken ct)=>Save("Program",p.Code,p.Code,1,null,p with{Revisions=[]},ct);
    public Task SaveProgramRevisionAsync(QmsAuditProgramRevision r,int? expected,CancellationToken ct)=>Save("ProgramRevision",r.Code+"/"+r.Revision,r.Code,r.Version,expected,r,ct);
    public Task<QmsAuditorCompetenceAssessment?> FindCompetenceAsync(Guid id,CancellationToken ct)=>Read<QmsAuditorCompetenceAssessment>("Competence",id.ToString(),ct);
    public Task SaveCompetenceAsync(QmsAuditorCompetenceAssessment r,int? expected,CancellationToken ct)=>Save("Competence",r.Id.ToString(),r.AuditProgram.Code,r.Version,expected,r,ct);
    public Task<QmsAuditRun?> FindAuditAsync(Guid id,CancellationToken ct)=>Read<QmsAuditRun>("Audit",id.ToString(),ct);
    public async Task<IReadOnlyList<QmsAuditRun>> AuditVersionsAsync(Guid id,CancellationToken ct)
    {
        var rows=new List<QmsAuditRun>();await using var cmd=uow.Command("SELECT data::text FROM quality.audit_version WHERE entity_type='Audit' AND entity_id=$1 ORDER BY version",id.ToString());await using var reader=await cmd.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))rows.Add(JsonSerializer.Deserialize<QmsAuditRun>(reader.GetString(0),CanonicalJson.Options)!);return rows;
    }
    public Task<QmsPlanningPage<QmsAuditRun>> AuditsAsync(int take,string? after,CancellationToken ct)=>Page<QmsAuditRun>("Audit",take,after,ct);
    public Task SaveAuditAsync(QmsAuditRun r,int? expected,CancellationToken ct)=>Save("Audit",r.Id.ToString(),r.AuditProgram.Code,r.Version,expected,r,ct);
    public Task<QmsAuditFinding?> FindFindingAsync(Guid id,CancellationToken ct)=>Read<QmsAuditFinding>("Finding",id.ToString(),ct);
    public async Task<QmsAuditFinding?> FindFindingOriginAsync(Guid id,CancellationToken ct)
    {
        await using var cmd=uow.Command("SELECT data::text FROM quality.audit_version WHERE entity_type='Finding' AND entity_id=$1 AND version=1",id.ToString());
        var json=await cmd.ExecuteScalarAsync(ct) as string;return json is null?null:JsonSerializer.Deserialize<QmsAuditFinding>(json,CanonicalJson.Options);
    }
    public Task<IReadOnlyList<QmsAuditFinding>> FindingsAsync(Guid audit,CancellationToken ct)=>Children<QmsAuditFinding>("Finding",audit.ToString(),ct);
    public Task SaveFindingAsync(QmsAuditFinding r,int? expected,CancellationToken ct)=>Save("Finding",r.Id.ToString(),r.AuditId.ToString(),r.Version,expected,r,ct);
    public Task<QmsCapaCase?> FindCapaAsync(long id,CancellationToken ct)=>Read<QmsCapaCase>("Capa",id.ToString(System.Globalization.CultureInfo.InvariantCulture),ct);
    public Task<QmsPlanningPage<QmsCapaCase>> CapasAsync(int take,string? after,CancellationToken ct)=>Page<QmsCapaCase>("Capa",take,after,ct);
    public async Task SaveCapaAsync(QmsCapaCase r,int? expected,CancellationToken ct)
    {
        await LockCapaAsync(r.CapaId,ct);
        await Save("Capa",r.CapaId.ToString(System.Globalization.CultureInfo.InvariantCulture),r.CapaId.ToString(System.Globalization.CultureInfo.InvariantCulture),r.Version,expected,r,ct);
        if(expected is null)
        {
            await using var cmd=uow.Command("INSERT INTO quality.capa_governance(capa_id,entity_id) VALUES($1,$2)",r.CapaId,r.CapaId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
    public async Task<IReadOnlyList<QmsCapaInvestigation>> InvestigationsAsync(long id,int generation,CancellationToken ct)=>
        (await Children<QmsCapaInvestigation>("Investigation",id.ToString(System.Globalization.CultureInfo.InvariantCulture),ct)).Where(x=>generation==0||x.Generation==generation).OrderBy(x=>x.Generation).ThenBy(x=>x.Revision).ToArray();
    public Task SaveInvestigationAsync(QmsCapaInvestigation r,int? expected,CancellationToken ct)=>Save("Investigation",r.CapaId+"/"+r.Generation+"/"+r.Revision,r.CapaId.ToString(System.Globalization.CultureInfo.InvariantCulture),r.Version,expected,r,ct);
    public Task<QmsCapaAction?> FindActionAsync(Guid id,CancellationToken ct)=>Read<QmsCapaAction>("Action",id.ToString(),ct);
    public async Task<IReadOnlyList<QmsCapaAction>> ActionsAsync(long id,int generation,CancellationToken ct)=>
        (await Children<QmsCapaAction>("Action",id.ToString(System.Globalization.CultureInfo.InvariantCulture),ct)).Where(x=>generation==0||x.Generation==generation).ToArray();
    public Task SaveActionAsync(QmsCapaAction r,int? expected,CancellationToken ct)=>Save("Action",r.Id.ToString(),r.CapaId.ToString(System.Globalization.CultureInfo.InvariantCulture),r.Version,expected,r,ct);
    public Task<QmsCapaEffectiveness?> FindEffectivenessAsync(Guid id,CancellationToken ct)=>Read<QmsCapaEffectiveness>("Effectiveness",id.ToString(),ct);
    public async Task<IReadOnlyList<QmsCapaEffectiveness>> EffectivenessAsync(long id,int generation,CancellationToken ct)=>
        (await Children<QmsCapaEffectiveness>("Effectiveness",id.ToString(System.Globalization.CultureInfo.InvariantCulture),ct)).Where(x=>generation==0||x.Generation==generation).ToArray();
    public Task SaveEffectivenessAsync(QmsCapaEffectiveness r,int? expected,CancellationToken ct)=>Save("Effectiveness",r.Id.ToString(),r.CapaId.ToString(System.Globalization.CultureInfo.InvariantCulture),r.Version,expected,r,ct);
    public async Task<long> InsertUnknownCapaAsync(long? ncrId,string title,DateOnly dueDate,string actor,DateTimeOffset at,CancellationToken ct)
    {
        await using var cmd=uow.Command("INSERT INTO quality.capa(ncr_id,title,root_cause,action,due_date,status,opened_by,opened_at) VALUES($1,$2,NULL,NULL,$3,'Open',$4,$5) RETURNING capa_id",
            new NpgsqlParameter{Value=(object?)ncrId??DBNull.Value,NpgsqlDbType=NpgsqlDbType.Bigint},title,dueDate,actor,at);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }
    public async Task UpdateCoreCapaAsync(long id,string? cause,string? action,string status,string? implementedBy,string? implementationNote,string? verifiedBy,DateTimeOffset at,CancellationToken ct)
    {
        await using var cmd=uow.Command("UPDATE quality.capa SET root_cause=$2,action=$3,status=$4,implemented_by=$5,implementation_note=$6,verified_by=$7,verified_at=CASE WHEN $4='Verified' THEN $8 ELSE NULL END,implemented_at=CASE WHEN $4 IN ('Implemented','Verified') THEN COALESCE(implemented_at,$8) ELSE NULL END WHERE capa_id=$1",id,Text(cause),Text(action),status,Text(implementedBy),Text(implementationNote),Text(verifiedBy),at);
        if(await cmd.ExecuteNonQueryAsync(ct)!=1)throw new CommandRejectedException("audit_not_found","The existing CAPA is unavailable.");
    }
    private static NpgsqlParameter Text(string? v)=>new(){Value=(object?)v??DBNull.Value,NpgsqlDbType=NpgsqlDbType.Text};
}
public static class AuditCapaRegistration
{
    public static IServiceCollection AddForgeAuditCapa(this IServiceCollection services)
    {
        services.AddForgeStore<IAuditCapaLookup>(uow=>new PostgresAuditCapaStore(uow));
        services.AddForgeStore<IAuditCapaStore>(uow=>new PostgresAuditCapaStore(uow));
        services.AddSingleton<QmsAuditService>();services.AddSingleton<QmsCapaService>();
        services.AddSingleton<IAuditorCompetenceValidator>(p=>p.GetRequiredService<QmsAuditService>());
        services.AddSingleton<ICapaLegacyGovernanceGuard,CapaLegacyGovernanceGuard>();
        return services;
    }
}
