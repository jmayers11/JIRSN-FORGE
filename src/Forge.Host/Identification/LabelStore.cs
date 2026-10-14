using System.Text.Json;
using Forge.Kernel.Postgres;
using Forge.Kernel.Serialization;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Host.Identification;

/// <summary>Versioned presentation metadata in the existing shared transaction. Primitive subjects remain with their owners.</summary>
public sealed class LabelStore(PostgresUnitOfWork uow) : IIdentificationPresentationStore
{
    public async Task LockAsync(string key, CancellationToken ct)
    {
        await using var command = uow.Command("SELECT pg_advisory_xact_lock(hashtextextended($1,0))", "forge:presentation:" + key);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<PresentationEnvelope<T>?> FindAsync<T>(string kind, string key, CancellationToken ct)
    {
        T? record = default;
        var transitions = new List<LabelTransition>();
        await using var command = uow.Command("SELECT v.record::text,v.transition::text,v.version=h.version FROM kernel.identification_presentation h JOIN kernel.identification_presentation_version v USING(kind,key) WHERE h.kind=$1 AND h.key=$2 ORDER BY v.version", kind, key);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var found = false;
        while (await reader.ReadAsync(ct))
        {
            transitions.Add(JsonSerializer.Deserialize<LabelTransition>(reader.GetString(1), CanonicalJson.Options)!);
            if (reader.GetBoolean(2)) { record = JsonSerializer.Deserialize<T>(reader.GetString(0), CanonicalJson.Options); found = true; }
        }
        return found ? new(record!, transitions) : null;
    }

    public async Task<IReadOnlyList<T>> ListAsync<T>(string kind, string? subjectCode, int take, string? afterKey, CancellationToken ct)
    {
        await using var command = uow.Command("SELECT v.record::text FROM kernel.identification_presentation h JOIN kernel.identification_presentation_version v USING(kind,key,version) WHERE h.kind=$1 AND ($2::text IS NULL OR h.subject_code=$2) AND ($3::text IS NULL OR h.key>$3) ORDER BY h.key LIMIT $4", kind, Text(subjectCode), Text(afterKey), Math.Clamp(take, 1, 200));
        return await RecordsAsync<T>(command, ct);
    }

    public async Task<IReadOnlyList<T>> HistoryAsync<T>(string kind, string key, CancellationToken ct)
    {
        await using var command = uow.Command("SELECT record::text FROM kernel.identification_presentation_version WHERE kind=$1 AND key=$2 ORDER BY version", kind, key);
        return await RecordsAsync<T>(command, ct);
    }

    public async Task<IReadOnlyList<T>> SubjectRecordsAsync<T>(string kind, string subjectCode, CancellationToken ct)
    {
        // Copy limits and histories use the complete subject population, independent of screen pagination.
        await using var command = uow.Command("SELECT v.record::text FROM kernel.identification_presentation h JOIN kernel.identification_presentation_version v USING(kind,key,version) WHERE h.kind=$1 AND h.subject_code=$2 ORDER BY h.key", kind, subjectCode);
        return await RecordsAsync<T>(command, ct);
    }

    public async Task<IdentificationAliasView?> FindActiveAliasAsync(string ns, string domain, string alias, CancellationToken ct)
    {
        await using var command = uow.Command("SELECT v.record::text FROM kernel.identification_presentation h JOIN kernel.identification_presentation_version v USING(kind,key,version) WHERE h.kind='Alias' AND h.namespace=$1 AND h.alias_domain=$2 AND h.alias_value=$3 AND h.state='Active'", ns, domain, alias);
        return (await RecordsAsync<IdentificationAliasView>(command, ct)).SingleOrDefault();
    }

    public async Task<IReadOnlyList<LabelPrintAttemptView>> FindArtifactOwnersAsync(string sha256, CancellationToken ct)
    {
        await using var command = uow.Command("SELECT v.record::text FROM kernel.identification_presentation h JOIN kernel.identification_presentation_version v USING(kind,key,version) WHERE h.kind='PrintAttempt' AND v.record->>'artifactSha256'=$1 ORDER BY h.key", sha256);
        return await RecordsAsync<LabelPrintAttemptView>(command, ct);
    }

    public async Task AppendAsync<T>(string kind, string key, string ns, string? subjectCode, int version, string state,
        T record, LabelTransition transition, CancellationToken ct, string? aliasDomain = null, string? aliasValue = null)
    {
        await using var head = version == 1
            ? uow.Command("INSERT INTO kernel.identification_presentation(kind,key,namespace,subject_code,version,state,alias_domain,alias_value) VALUES($1,$2,$3,$4,$5,$6,$7,$8)", kind, key, ns, Text(subjectCode), version, state, Text(aliasDomain), Text(aliasValue))
            : uow.Command("UPDATE kernel.identification_presentation SET version=$3,state=$4 WHERE kind=$1 AND key=$2 AND version=$5", kind, key, version, state, version - 1);
        if (await head.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Presentation changed concurrently.");
        await using var history = uow.Command("INSERT INTO kernel.identification_presentation_version(kind,key,version,record,transition) VALUES($1,$2,$3,$4,$5)", kind, key, version, Json(record), Json(transition));
        await history.ExecuteNonQueryAsync(ct);
    }

    private static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };
    private static NpgsqlParameter Json<T>(T value) => new() { Value = CanonicalJson.Serialize(value), NpgsqlDbType = NpgsqlDbType.Jsonb };
    private static async Task<IReadOnlyList<T>> RecordsAsync<T>(NpgsqlCommand command, CancellationToken ct)
    {
        var result = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), CanonicalJson.Options)!);
        return result;
    }
}
