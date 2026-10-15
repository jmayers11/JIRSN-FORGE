using System.Globalization;
using System.Text.Json;
using Forge.Kernel.Commands;
using Forge.Kernel.Postgres;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;

namespace Forge.Quality.Infrastructure;

internal sealed class PostgresQmsReviewStore(PostgresUnitOfWork uow) : IQmsReviewStore
{
    public async Task<QmsReviewSnapshotIdentity> SnapshotIdentityAsync(CancellationToken ct)
    {
        if (!uow.IsReadOnly) throw new InvalidOperationException("Freeze review inputs in a repeatable read snapshot.");
        await using var command = uow.Command("SELECT clock_timestamp(),pg_current_snapshot()::text");
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new(reader.GetFieldValue<DateTimeOffset>(0), reader.GetString(1));
    }
    public async Task LockAsync(string key, CancellationToken ct)
    {
        await using var command = uow.Command("SELECT pg_advisory_xact_lock(hashtextextended($1,0))", "forge.qms.review:" + key);
        await command.ExecuteScalarAsync(ct);
    }
    private async Task<T?> ReadAsync<T>(string type, string id, CancellationToken ct) where T : class
    {
        await using var command = uow.Command("SELECT v.data::text FROM quality.review_head h JOIN quality.review_version v USING(entity_type,entity_id,version) WHERE h.entity_type=$1 AND h.entity_id=$2", type, id);
        return await command.ExecuteScalarAsync(ct) is string json ? JsonSerializer.Deserialize<T>(json, CanonicalJson.Options) : null;
    }
    private async Task<IReadOnlyList<T>> ChildrenAsync<T>(string type, string code, CancellationToken ct) where T : class
    {
        var rows = new List<T>();
        await using var command = uow.Command("SELECT v.data::text FROM quality.review_head h JOIN quality.review_version v USING(entity_type,entity_id,version) WHERE h.entity_type=$1 AND h.code=$2 ORDER BY h.entity_id", type, code);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), CanonicalJson.Options)!);
        return rows;
    }
    private async Task<QmsReviewPage<T>> PageAsync<T>(string type, int take, string? after, string? kind, string? code, CancellationToken ct) where T : class
    {
        if (take is < 1 or > 100) throw new CommandRejectedException("review_page", "Choose a page size from 1 to 100.");
        var rows = new List<T>(); string? last = null;
        await using var command = uow.Command("SELECT h.entity_id,v.data::text FROM quality.review_head h JOIN quality.review_version v USING(entity_type,entity_id,version) WHERE h.entity_type=$1 AND h.entity_id>COALESCE($2::text,'') AND ($3::text='' OR h.kind=$3) AND ($4::text='' OR h.code=$4) ORDER BY h.entity_id LIMIT $5", type, after ?? "", kind ?? "", code ?? "", take + 1);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (rows.Count == take) return new(rows, last);
            last = reader.GetString(0); rows.Add(JsonSerializer.Deserialize<T>(reader.GetString(1), CanonicalJson.Options)!);
        }
        return new(rows, null);
    }
    private async Task SaveAsync<T>(string type, string id, string code, string kind, int version, int? expected, T data, CancellationToken ct)
    {
        await LockAsync(type + ":" + id, ct);
        if (expected is null)
        {
            if (version != 1) throw new CommandRejectedException("review_initial_version", "A new record starts at version one.");
            await using var insert = uow.Command("INSERT INTO quality.review_head(entity_type,entity_id,version,code,kind) VALUES($1,$2,$3,$4,$5) ON CONFLICT DO NOTHING", type, id, version, code, kind);
            if (await insert.ExecuteNonQueryAsync(ct) != 1) throw new CommandRejectedException("review_exists", "This record already exists.");
        }
        else
        {
            if (version != checked(expected.Value + 1)) throw new CommandRejectedException("stale_version", "The new version must follow the expected version.");
            await using var update = uow.Command("UPDATE quality.review_head SET version=$3 WHERE entity_type=$1 AND entity_id=$2 AND version=$4", type, id, version, expected.Value);
            if (await update.ExecuteNonQueryAsync(ct) != 1) throw new CommandRejectedException("stale_version", "The review source changed; read its current version.");
        }
        await using var append = uow.Command("INSERT INTO quality.review_version(entity_type,entity_id,version,data) VALUES($1,$2,$3,$4::jsonb)", type, id, version, CanonicalJson.Serialize(data));
        await append.ExecuteNonQueryAsync(ct);
    }
    public async Task<QmsReviewDefinition?> FindDefinitionAsync(string code, CancellationToken ct)
    {
        var header = await ReadAsync<QmsReviewDefinition>("Definition", code, ct);
        return header is null ? null : header with { Revisions = (await ChildrenAsync<QmsReviewDefinitionRevision>("Revision", code, ct)).OrderBy(revision => revision.Revision).ToArray() };
    }
    public async Task<QmsReviewPage<QmsReviewDefinition>> DefinitionsAsync(QmsReviewDefinitionKind? kind, int take, string? after, CancellationToken ct)
    {
        var page = await PageAsync<QmsReviewDefinition>("Definition", take, after, kind?.ToString(), null, ct);
        var result = new List<QmsReviewDefinition>();
        foreach (var header in page.Items) result.Add((await FindDefinitionAsync(header.Code, ct))!);
        return new(result, page.NextCursor);
    }
    public Task InsertDefinitionAsync(QmsReviewDefinition definition, CancellationToken ct) =>
        SaveAsync("Definition", definition.Code, definition.Code, definition.Kind.ToString(), 1, null, definition with { Revisions = [] }, ct);
    public Task SaveDefinitionRevisionAsync(QmsReviewDefinitionRevision revision, int? expectedVersion, CancellationToken ct) =>
        SaveAsync("Revision", revision.Code + "/" + revision.Revision.ToString(CultureInfo.InvariantCulture), revision.Code, "", revision.Version, expectedVersion, revision, ct);
    public Task<QmsManagementReviewRun?> FindRunAsync(Guid id, CancellationToken ct) => ReadAsync<QmsManagementReviewRun>("Run", id.ToString("D"), ct);
    public Task<QmsReviewPage<QmsManagementReviewRun>> RunsAsync(string? programCode, int take, string? after, CancellationToken ct) =>
        PageAsync<QmsManagementReviewRun>("Run", take, after, null, programCode, ct);
    public Task SaveRunAsync(QmsManagementReviewRun run, int? expectedVersion, CancellationToken ct) =>
        SaveAsync("Run", run.Id.ToString("D"), run.ProgramCode, "", run.Version, expectedVersion, run, ct);
    public async Task AppendManifestAsync(QmsReviewInputManifest manifest, CancellationToken ct)
    {
        await using var command = uow.Command("INSERT INTO quality.review_input_manifest(manifest_id,review_id,fingerprint,data_cutoff,data) VALUES($1,$2,$3,$4,$5::jsonb)", manifest.ManifestId, manifest.ReviewId, manifest.Fingerprint, manifest.DataCutoff, CanonicalJson.Serialize(manifest));
        await command.ExecuteNonQueryAsync(ct);
    }
    public Task<QmsReviewAction?> FindActionAsync(Guid id, CancellationToken ct) => ReadAsync<QmsReviewAction>("Action", id.ToString("D"), ct);
    public Task<IReadOnlyList<QmsReviewAction>> ActionsAsync(Guid reviewId, CancellationToken ct) => ChildrenAsync<QmsReviewAction>("Action", reviewId.ToString("D"), ct);
    public Task SaveActionAsync(QmsReviewAction action, int? expectedVersion, CancellationToken ct) =>
        SaveAsync("Action", action.Id.ToString("D"), action.ReviewId.ToString("D"), "", action.Version, expectedVersion, action, ct);
    public Task<QmsMappingEvidenceAssessment?> FindMappingAssessmentAsync(Guid id, CancellationToken ct) => ReadAsync<QmsMappingEvidenceAssessment>("MappingAssessment", id.ToString("D"), ct);
    public Task<QmsReviewPage<QmsMappingEvidenceAssessment>> MappingAssessmentsAsync(string? code, int take, string? after, CancellationToken ct) =>
        PageAsync<QmsMappingEvidenceAssessment>("MappingAssessment", take, after, null, code, ct);
    public Task SaveMappingAssessmentAsync(QmsMappingEvidenceAssessment assessment, int? expectedVersion, CancellationToken ct) =>
        SaveAsync("MappingAssessment", assessment.Id.ToString("D"), assessment.MappingCode, "", assessment.Version, expectedVersion, assessment, ct);
    public Task<QmsEditionMappingAssessment?> FindEditionAssessmentAsync(Guid id, CancellationToken ct) => ReadAsync<QmsEditionMappingAssessment>("EditionAssessment", id.ToString("D"), ct);
    public Task<QmsReviewPage<QmsEditionMappingAssessment>> EditionAssessmentsAsync(int take, string? after, CancellationToken ct) =>
        PageAsync<QmsEditionMappingAssessment>("EditionAssessment", take, after, null, null, ct);
    public Task SaveEditionAssessmentAsync(QmsEditionMappingAssessment assessment, int? expectedVersion, CancellationToken ct) =>
        SaveAsync("EditionAssessment", assessment.Id.ToString("D"), "", "", assessment.Version, expectedVersion, assessment, ct);
}
