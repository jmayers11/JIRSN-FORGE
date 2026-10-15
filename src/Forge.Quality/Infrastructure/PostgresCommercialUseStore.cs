using System.Text.Json;
using Forge.Kernel.Postgres;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;

namespace Forge.Quality.Infrastructure;

internal sealed class PostgresCommercialUseStore(PostgresUnitOfWork uow) : ICommercialUseStore
{
    public async Task<CommercialAcceptedUse?> FindAsync(Guid useId,CancellationToken ct)
    {
        await using var command=uow.Command("SELECT payload::text FROM quality.commercial_use WHERE use_id=$1",useId);
        return await command.ExecuteScalarAsync(ct) is string json?JsonSerializer.Deserialize<CommercialAcceptedUse>(json,CanonicalJson.Options):null;
    }
    public async Task<CommercialAcceptedUse?> LatestForEntityAsync(string type,string id,string activity,CancellationToken ct)
    {
        await using var command=uow.Command("SELECT payload::text FROM quality.commercial_use WHERE payload->>'entityType'=$1 AND payload->>'entityId'=$2 AND payload#>>'{evidence,activity}'=$3 ORDER BY payload->>'at' DESC,use_id DESC LIMIT 1",type,id,activity);
        return await command.ExecuteScalarAsync(ct) is string json?JsonSerializer.Deserialize<CommercialAcceptedUse>(json,CanonicalJson.Options):null;
    }
    public async Task<CommercialAcceptedUse?> FindForAuditAsync(Guid auditId, CancellationToken ct)
    {
        await using var command=uow.Command("SELECT payload::text FROM quality.commercial_use WHERE audit_id=$1",auditId);
        return await command.ExecuteScalarAsync(ct) is string json ? JsonSerializer.Deserialize<CommercialAcceptedUse>(json,CanonicalJson.Options) : null;
    }
    public async Task InsertAsync(CommercialAcceptedUse use,CancellationToken ct)
    {
        await using var command=uow.Command("INSERT INTO quality.commercial_use(use_id,audit_id,context_fingerprint,component_fingerprint,payload) VALUES($1,$2,$3,$4,$5::jsonb)",use.UseId,use.AuditId,use.ContextFingerprint,use.ComponentFingerprint,CanonicalJson.Serialize(use));
        await command.ExecuteNonQueryAsync(ct);
    }
}
