using System.Text.Json;
using Forge.Kernel.Commands;
using Forge.Kernel.Postgres;
using Forge.Kernel.Serialization;
using Forge.Quality.Commercial;
using Forge.Quality.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Quality.Infrastructure;

internal sealed class PostgresCommercialQualityStore(PostgresUnitOfWork uow) : ICommercialQualityStore
{
    public async Task LockCodeAsync(CommercialRecordKind kind, string code, CancellationToken ct)
    {
        await using var command = uow.Command("SELECT pg_advisory_xact_lock(hashtextextended($1,0))", "commercial-code:" + kind + ":" + code);
        await command.ExecuteNonQueryAsync(ct);
    }
    public async Task LockScopeAsync(CommercialRecordKind kind, string scope, bool shared, CancellationToken ct)
    {
        await using var command = uow.Command(shared ? "SELECT pg_advisory_xact_lock_shared(hashtextextended($1,0))" : "SELECT pg_advisory_xact_lock(hashtextextended($1,0))", "commercial-scope:" + kind + ":" + scope);
        await command.ExecuteNonQueryAsync(ct);
    }
    public async Task<CommercialRevision?> FindAsync(CommercialRecordKind kind, string code, int revision, CancellationToken ct)
    {
        await using var command = uow.Command("SELECT payload::text FROM quality.commercial_revision WHERE kind=$1 AND code=$2 AND revision=$3", kind.ToString(), code, revision);
        return await command.ExecuteScalarAsync(ct) is string json ? JsonSerializer.Deserialize<CommercialRevision>(json, CanonicalJson.Options) : null;
    }
    public async Task<CommercialRevision?> FindVersionAsync(CommercialRecordKind kind, string code, int revision, int version, CancellationToken ct)
    {
        await using var command=uow.Command("SELECT payload::text FROM quality.commercial_version WHERE kind=$1 AND code=$2 AND revision=$3 AND version=$4",kind.ToString(),code,revision,version);
        return await command.ExecuteScalarAsync(ct) is string json?JsonSerializer.Deserialize<CommercialRevision>(json,CanonicalJson.Options):null;
    }
    public Task<IReadOnlyList<CommercialRevision>> RevisionsAsync(CommercialRecordKind kind, string code, CancellationToken ct) =>
        ReadAsync("SELECT payload::text FROM quality.commercial_revision WHERE kind=$1 AND code=$2 ORDER BY revision", [kind.ToString(), code], ct);
    public Task<IReadOnlyList<CommercialRevision>> ListAsync(CommercialRecordKind? kind, int take, string? after, CancellationToken ct) =>
        ReadAsync("SELECT payload::text FROM quality.commercial_revision WHERE ($1::text IS NULL OR kind=$1) AND ($2::text IS NULL OR kind||'/'||code||'/'||lpad(revision::text,10,'0')>$2) ORDER BY kind,code,revision LIMIT $3", [Text(kind?.ToString()), Text(after), Math.Clamp(take, 1, 100)], ct);
    public Task<IReadOnlyList<CommercialRevision>> EffectiveAsync(CommercialRecordKind kind, CancellationToken ct) =>
        ReadAsync("SELECT payload::text FROM quality.commercial_revision WHERE kind=$1 AND state='Effective' ORDER BY code,revision", [kind.ToString()], ct);
    public Task<IReadOnlyList<CommercialRevision>> EverEffectiveAsync(CommercialRecordKind kind, CancellationToken ct) =>
        ReadAsync("SELECT payload::text FROM quality.commercial_revision WHERE kind=$1 AND effective_from IS NOT NULL ORDER BY code,revision", [kind.ToString()], ct);
    public async Task InsertAsync(CommercialRevision record, CancellationToken ct)
    {
        await using (var command = uow.Command("INSERT INTO quality.commercial_revision(kind,code,revision,version,state,organization_code,facility_code,effective_from,effective_to,payload) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10::jsonb)", record.Kind.ToString(), record.Code, record.Revision, record.Version, record.State.ToString(), record.OrganizationCode, record.FacilityCode, Time(record.EffectiveFrom), Time(record.EffectiveTo), CanonicalJson.Serialize(record)))
            await command.ExecuteNonQueryAsync(ct);
        await VersionAsync(record, ct);
    }
    public async Task UpdateAsync(CommercialRevision record, int expectedVersion, CancellationToken ct)
    {
        await using (var command = uow.Command("UPDATE quality.commercial_revision SET version=$4,state=$5,effective_from=$6,effective_to=$7,payload=$8::jsonb WHERE kind=$1 AND code=$2 AND revision=$3 AND version=$9", record.Kind.ToString(), record.Code, record.Revision, record.Version, record.State.ToString(), Time(record.EffectiveFrom), Time(record.EffectiveTo), CanonicalJson.Serialize(record), expectedVersion))
            if (await command.ExecuteNonQueryAsync(ct) != 1) throw new CommandRejectedException("stale_version", "Read the current commercial revision before deciding.");
        await VersionAsync(record, ct);
    }
    private async Task VersionAsync(CommercialRevision record, CancellationToken ct)
    {
        await using var command = uow.Command("INSERT INTO quality.commercial_version(kind,code,revision,version,payload) VALUES($1,$2,$3,$4,$5::jsonb)", record.Kind.ToString(), record.Code, record.Revision, record.Version, CanonicalJson.Serialize(record));
        await command.ExecuteNonQueryAsync(ct);
    }
    private async Task<IReadOnlyList<CommercialRevision>> ReadAsync(string sql, object[] parameters, CancellationToken ct)
    {
        var records = new List<CommercialRevision>();
        await using var command = uow.Command(sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) records.Add(JsonSerializer.Deserialize<CommercialRevision>(reader.GetString(0), CanonicalJson.Options)!);
        return records;
    }
    private static NpgsqlParameter Text(string? value) => new() { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)value ?? DBNull.Value };
    private static NpgsqlParameter Time(DateTimeOffset? value) => new() { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = (object?)value ?? DBNull.Value };
}

public static class CommercialQualityRegistration
{
    public static IServiceCollection AddForgeCommercialQuality(this IServiceCollection services)
    {
        services.AddForgeStore<ICommercialQualityLookup>(uow => new PostgresCommercialQualityStore(uow));
        services.AddForgeStore<ICommercialQualityStore>(uow => new PostgresCommercialQualityStore(uow));
        services.AddSingleton<CommercialQualityService>();
        services.AddSingleton<ICommercialQualityAccess>(sp => sp.GetRequiredService<CommercialQualityService>());
        services.AddForgeStore<ICommercialUseLookup>(uow => new PostgresCommercialUseStore(uow));
        services.AddForgeStore<ICommercialCaseLookup>(uow => new PostgresCommercialCaseStore(uow));
        services.AddForgeStore<ICommercialUseStore>(uow => new PostgresCommercialUseStore(uow));
        services.AddSingleton<ICommercialUseAccess>(sp => sp.GetRequiredService<CommercialQualityService>());
        services.AddSingleton<ICommercialCustomerApproval>(sp => sp.GetRequiredService<CommercialQualityService>());
        return services;
    }
}
