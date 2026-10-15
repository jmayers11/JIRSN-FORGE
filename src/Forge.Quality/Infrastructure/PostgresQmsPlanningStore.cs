using System.Text.Json;
using Forge.Kernel.Commands;
using Forge.Kernel.Postgres;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;
using Forge.Quality.Planning;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Quality.Infrastructure;

/// <summary>All versions are append-only; heads are compare-and-swap pointers, never audit/evidence replacement.</summary>
internal sealed class PostgresQmsPlanningStore(PostgresUnitOfWork uow) : IQmsPlanningStore
{
    public Task LockAsync(string code, CancellationToken ct) => LockEvidenceAsync("planning:" + code, ct);
    public async Task LockEvidenceAsync(string key, CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT pg_advisory_xact_lock(hashtextextended($1,0))", key);
        await cmd.ExecuteScalarAsync(ct);
    }
    private async Task<T?> Read<T>(string type, string id, CancellationToken ct) where T : class
    {
        await using var cmd = uow.Command("SELECT v.data::text FROM quality.planning_head h JOIN quality.planning_version v USING(entity_type,entity_id,version) WHERE h.entity_type=$1 AND h.entity_id=$2", type, id);
        var json = await cmd.ExecuteScalarAsync(ct) as string;
        return json is null ? null : JsonSerializer.Deserialize<T>(json, CanonicalJson.Options);
    }
    private async Task<IReadOnlyList<T>> Children<T>(string type, string code, CancellationToken ct) where T : class
    {
        var rows = new List<T>();
        await using var cmd = uow.Command("SELECT v.data::text FROM quality.planning_head h JOIN quality.planning_version v USING(entity_type,entity_id,version) WHERE h.entity_type=$1 AND h.code=$2 ORDER BY h.entity_id", type, code);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), CanonicalJson.Options)!);
        return rows;
    }
    private async Task<QmsPlanningPage<T>> Page<T>(string type, int take, string? after, string? kind, CancellationToken ct) where T : class
    {
        var rows = new List<T>(); string? last = null;
        await using var cmd = uow.Command("SELECT h.entity_id,v.data::text FROM quality.planning_head h JOIN quality.planning_version v USING(entity_type,entity_id,version) WHERE h.entity_type=$1 AND h.entity_id>COALESCE($2::text,'') AND ($3::text='' OR h.kind=$3) ORDER BY h.entity_id LIMIT $4", type, after ?? "", kind ?? "", take + 1);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (rows.Count == take) return new(rows, last);
            last = reader.GetString(0); rows.Add(JsonSerializer.Deserialize<T>(reader.GetString(1), CanonicalJson.Options)!);
        }
        return new(rows, null);
    }
    private async Task Save<T>(string type, string id, string code, string kind, int version, int? expected, T data, CancellationToken ct)
    {
        await LockEvidenceAsync(type + ":" + id, ct);
        if (expected is null)
        {
            await using var insert = uow.Command("INSERT INTO quality.planning_head(entity_type,entity_id,version,code,kind) VALUES($1,$2,$3,$4,$5) ON CONFLICT DO NOTHING", type,id,version,code,kind);
            if (await insert.ExecuteNonQueryAsync(ct) != 1) throw new CommandRejectedException("planning_exists", "This record already exists.");
        }
        else
        {
            if (version != checked(expected.Value + 1)) throw new CommandRejectedException("stale_version", "The proposed version does not follow its expected version.");
            await using var update = uow.Command("UPDATE quality.planning_head SET version=$3 WHERE entity_type=$1 AND entity_id=$2 AND version=$4",type,id,version,expected.Value);
            if (await update.ExecuteNonQueryAsync(ct) != 1) throw new CommandRejectedException("stale_version", "The record changed; read its current version.");
        }
        await using var cmd = uow.Command("INSERT INTO quality.planning_version(entity_type,entity_id,version,data) VALUES($1,$2,$3,$4::jsonb)",type,id,version,CanonicalJson.Serialize(data));
        await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task<QmsPlanningRecord?> FindAsync(string code, CancellationToken ct)
    {
        var header = await Read<QmsPlanningRecord>("Record",code,ct);
        return header is null ? null : header with { Revisions = (await Children<QmsPlanningRevision>("Revision",code,ct)).OrderBy(x=>x.Revision).ToArray() };
    }
    public async Task<QmsPlanningPage<QmsPlanningRecord>> ListAsync(QmsPlanningKind? kind,int take,string? after,CancellationToken ct)
    {
        var page = await Page<QmsPlanningRecord>("Record",take,after,kind?.ToString(),ct);
        var result = new List<QmsPlanningRecord>(); foreach(var header in page.Items) result.Add((await FindAsync(header.Code,ct))!);
        return new(result,page.NextCursor);
    }
    public Task InsertAsync(QmsPlanningRecord r,CancellationToken ct) => Save("Record",r.Code,r.Code,r.Kind.ToString(),1,null,r with {Revisions=[]},ct);
    public Task SaveRevisionAsync(QmsPlanningRevision r,int? expected,CancellationToken ct) => Save("Revision",r.Code+"/"+r.Revision,r.Code,"",r.Version,expected,r,ct);
    public Task<QmsPlanningAssessment?> FindAssessmentAsync(Guid id,CancellationToken ct) => Read<QmsPlanningAssessment>("Assessment",id.ToString(),ct);
    public async Task<IReadOnlyList<QmsPlanningAssessment>> AssessmentsAsync(string code,int revision,CancellationToken ct) => (await Children<QmsPlanningAssessment>("Assessment",code,ct)).Where(x=>x.DefinitionRevision==revision).ToArray();
    public Task SaveAssessmentAsync(QmsPlanningAssessment r,int? expected,CancellationToken ct) => Save("Assessment",r.Id.ToString(),r.Code,r.Kind.ToString(),r.Version,expected,r,ct);
    public Task<QmsPlanningTask?> FindTaskAsync(Guid id,CancellationToken ct) => Read<QmsPlanningTask>("Task",id.ToString(),ct);
    public async Task<IReadOnlyList<QmsPlanningTask>> TasksAsync(string code,int revision,CancellationToken ct) => (await Children<QmsPlanningTask>("Task",code,ct)).Where(x=>x.DefinitionRevision==revision).ToArray();
    public Task SaveTaskAsync(QmsPlanningTask r,int? expected,CancellationToken ct) => Save("Task",r.Id.ToString(),r.Code,"",r.Version,expected,r,ct);
    public Task<QmsPlanningCase?> FindCaseAsync(Guid id,CancellationToken ct) => Read<QmsPlanningCase>("Case",id.ToString(),ct);
    public Task<IReadOnlyList<QmsPlanningCase>> CasesAsync(string code,CancellationToken ct) => Children<QmsPlanningCase>("Case",code,ct);
    public Task SaveCaseAsync(QmsPlanningCase r,int? expected,CancellationToken ct) => Save("Case",r.Id.ToString(),r.Code,"",r.Version,expected,r,ct);
    public Task<QmsRetentionBinding?> FindBindingAsync(Guid id,CancellationToken ct) => Read<QmsRetentionBinding>("Binding",id.ToString(),ct);
    public async Task<IReadOnlyList<QmsRetentionBinding>> BindingsForSourceAsync(QmsPlanningEvidenceReference subject,CancellationToken ct) =>
        (await Children<QmsRetentionBinding>("Binding",subject.Key,ct)).Where(x=>x.Subject.Reference.Context==subject.Context&&x.Subject.Reference.Type==subject.Type&&x.Subject.Reference.Key==subject.Key&&(x.Subject.Reference.Revision is null||x.Subject.Reference.Revision==subject.Revision)).ToArray();
    public Task<QmsPlanningPage<QmsRetentionBinding>> BindingsAsync(int take,string? after,CancellationToken ct) => Page<QmsRetentionBinding>("Binding",take,after,null,ct);
    public Task SaveBindingAsync(QmsRetentionBinding r,int? expected,CancellationToken ct) => Save("Binding",r.Id.ToString(),r.Subject.Reference.Key,"",r.Version,expected,r,ct);
    public Task<QmsRetentionHold?> FindHoldAsync(Guid id,CancellationToken ct) => Read<QmsRetentionHold>("Hold",id.ToString(),ct);
    public async Task<IReadOnlyList<QmsRetentionHold>> HoldsAsync(QmsPlanningEvidenceReference subject,CancellationToken ct) =>
        (await Children<QmsRetentionHold>("Hold",subject.Key,ct)).Where(x=>x.Subject.Reference.Context==subject.Context&&x.Subject.Reference.Type==subject.Type&&(x.Subject.Reference.Revision is null||x.Subject.Reference.Revision==subject.Revision)).ToArray();
    public Task SaveHoldAsync(QmsRetentionHold r,int? expected,CancellationToken ct) => Save("Hold",r.Id.ToString(),r.Subject.Reference.Key,"",r.Version,expected,r,ct);
    public Task<QmsDispositionRecord?> FindDispositionAsync(Guid id,CancellationToken ct) => Read<QmsDispositionRecord>("Disposition",id.ToString(),ct);
    public Task<QmsPlanningPage<QmsDispositionRecord>> DispositionsAsync(int take,string? after,CancellationToken ct) => Page<QmsDispositionRecord>("Disposition",take,after,null,ct);
    public Task SaveDispositionAsync(QmsDispositionRecord r,int? expected,CancellationToken ct) => Save("Disposition",r.Id.ToString(),r.Id.ToString(),"",r.Version,expected,r,ct);
    public async Task AppendManifestAsync(Guid id,QmsDispositionManifest manifest,CancellationToken ct)
    {
        await using var cmd=uow.Command("INSERT INTO quality.disposition_manifest(disposition_id,manifest_id,version,fingerprint,data) VALUES($1,$2,$3,$4,$5::jsonb)",id,manifest.Id,manifest.Version,manifest.Fingerprint,CanonicalJson.Serialize(manifest));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

public static class QmsPlanningRegistration
{
    public static IServiceCollection AddForgeQmsPlanning(this IServiceCollection services)
    {
        services.AddForgeStore<IQmsPlanningLookup>(uow=>new PostgresQmsPlanningStore(uow));
        services.AddForgeStore<IQmsPlanningStore>(uow=>new PostgresQmsPlanningStore(uow));
        services.AddSingleton<QmsPlanningService>(); services.AddSingleton<QmsPlanningWorkService>(); services.AddSingleton<QmsRetentionService>();
        return services;
    }
}
