using System.Text.Json;
using Forge.Kernel.Postgres;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;
using Forge.Quality.Qms;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Quality.Infrastructure;

internal sealed class PostgresQmsStore(PostgresUnitOfWork uow) : IQmsStore
{
    public async Task LockAsync(string code, CancellationToken ct)
    {
        await using var command = uow.Command("SELECT code FROM quality.qms_record WHERE code=$1 FOR UPDATE", code);
        await command.ExecuteScalarAsync(ct);
    }

    public async Task LockSourceAsync(string kind, string code, string revision, CancellationToken ct)
    {
        if (uow.IsReadOnly) return;
        var sql = kind == "DocumentRevision"
            ? "SELECT d.number FROM engineering.document d JOIN engineering.document_revision r ON r.document_number=d.number WHERE d.number=$1 AND r.revision=$2 FOR SHARE OF d,r"
            : "SELECT d.code FROM quality.qms_record d JOIN quality.qms_revision r ON r.code=d.code WHERE d.code=$1 AND r.revision::text=$2 FOR SHARE OF d,r";
        await using var command = uow.Command(sql, code, revision);
        await command.ExecuteScalarAsync(ct);
    }

    public async Task<QmsRecordView?> FindAsync(string code, CancellationToken ct)
    {
        QmsRecordView? record;
        await using (var command = uow.Command("SELECT code,kind,organization_code,facility_code,program_id,markings,created_by,created_at FROM quality.qms_record WHERE code=$1", code))
        await using (var r = await command.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return null;
            record = new(r.GetString(0), Enum.Parse<QmsRecordKind>(r.GetString(1)), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetGuid(4), (DataMarking)r.GetInt32(5), r.GetString(6), r.GetFieldValue<DateTimeOffset>(7), []);
        }
        var revisions = new List<QmsRevisionView>();
        await using (var command = uow.Command("SELECT revision,version,state,content::text,created_by,last_edited_by,reviewed_by,approved_by,effective_from,effective_to,requires_licensed_verification FROM quality.qms_revision WHERE code=$1 ORDER BY revision", code))
        await using (var r = await command.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) revisions.Add(new(code, r.GetInt32(0), r.GetInt32(1), Enum.Parse<QmsState>(r.GetString(2)),
                JsonSerializer.Deserialize<QmsContent>(r.GetString(3), CanonicalJson.Options)!, r.GetString(4), r.GetString(5),
                String(r, 6), String(r, 7), Time(r, 8), Time(r, 9), r.GetBoolean(10), [], []));
        }
        for (var i = 0; i < revisions.Count; i++)
        {
            var revision = revisions[i];
            var sources = new List<QmsSourceReference>();
            await using (var command = uow.Command("SELECT kind,source_key,source_revision,program_id,markings,facility_code FROM quality.qms_revision_source WHERE code=$1 AND revision=$2 ORDER BY kind,source_key,source_revision", code, revision.Revision))
            await using (var r = await command.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct)) sources.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetGuid(3), (DataMarking)r.GetInt32(4), String(r, 5)));
            }
            var history = new List<QmsTransition>();
            await using (var command = uow.Command("SELECT action,actor,at,note,reason,from_state,to_state,version FROM quality.qms_transition WHERE code=$1 AND revision=$2 ORDER BY version", code, revision.Revision))
            await using (var r = await command.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct)) history.Add(new(r.GetString(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : Enum.Parse<QmsState>(r.GetString(5)), Enum.Parse<QmsState>(r.GetString(6)), r.GetInt32(7)));
            }
            revisions[i] = revision with { Sources = sources, History = history };
        }
        return record with { Revisions = revisions };
    }

    public async Task<IReadOnlyList<QmsRecordView>> ListAsync(QmsRecordKind? kind, CancellationToken ct)
    {
        var codes = new List<string>();
        await using (var command = uow.Command("SELECT code FROM quality.qms_record WHERE $1::text IS NULL OR kind=$1 ORDER BY code", Text(kind?.ToString())))
        await using (var r = await command.ExecuteReaderAsync(ct)) { while (await r.ReadAsync(ct)) codes.Add(r.GetString(0)); }
        var records = new List<QmsRecordView>();
        foreach (var code in codes) records.Add((await FindAsync(code, ct))!);
        return records;
    }

    public async Task InsertRecordAsync(QmsRecordView record, CancellationToken ct)
    {
        await using var command = uow.Command("INSERT INTO quality.qms_record(code,kind,organization_code,facility_code,program_id,markings,created_by,created_at) VALUES($1,$2,$3,$4,$5,$6,$7,$8)",
            record.Code, record.Kind.ToString(), record.OrganizationCode, record.FacilityCode, Uuid(record.ProgramId), (int)record.Markings, record.CreatedBy, record.CreatedAt);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertRevisionAsync(QmsRevisionView revision, CancellationToken ct)
    {
        await using (var command = uow.Command("INSERT INTO quality.qms_revision(code,revision,version,state,content,created_by,last_edited_by,requires_licensed_verification) VALUES($1,$2,1,'Draft',$3::jsonb,$4,$5,$6)",
            revision.Code, revision.Revision, CanonicalJson.Serialize(revision.Content), revision.CreatedBy, revision.LastEditedBy, revision.RequiresLicensedVerification))
            await command.ExecuteNonQueryAsync(ct);
        await SourcesAsync(revision, ct);
        await HistoryAsync(revision, ct);
    }

    public async Task UpdateRevisionAsync(QmsRevisionView revision, int expectedVersion, bool replaceSources, CancellationToken ct)
    {
        await using (var command = uow.Command("UPDATE quality.qms_revision SET version=$3,state=$4,content=$5::jsonb,last_edited_by=$6,reviewed_by=$7,approved_by=$8,effective_from=$9,effective_to=$10,requires_licensed_verification=$11 WHERE code=$1 AND revision=$2 AND version=$12",
            revision.Code, revision.Revision, revision.Version, revision.State.ToString(), CanonicalJson.Serialize(revision.Content), revision.LastEditedBy,
            Text(revision.ReviewedBy), Text(revision.ApprovedBy), Timestamp(revision.EffectiveFrom), Timestamp(revision.EffectiveTo), revision.RequiresLicensedVerification, expectedVersion))
        {
            if (await command.ExecuteNonQueryAsync(ct) != 1) throw new Forge.Kernel.Commands.CommandRejectedException("stale_version", "The QMS revision changed; read its current version before deciding.");
        }
        if (replaceSources)
        {
            await using var command = uow.Command("DELETE FROM quality.qms_revision_source WHERE code=$1 AND revision=$2", revision.Code, revision.Revision);
            await command.ExecuteNonQueryAsync(ct);
            await SourcesAsync(revision, ct);
        }
        await HistoryAsync(revision, ct);
    }

    private async Task SourcesAsync(QmsRevisionView revision, CancellationToken ct)
    {
        foreach (var source in revision.Sources)
        {
            var document = source.Kind == "DocumentRevision";
            await using var command = uow.Command("INSERT INTO quality.qms_revision_source(code,revision,kind,source_key,source_revision,program_id,markings,facility_code,document_number,document_revision,qms_code,qms_revision) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)",
                revision.Code, revision.Revision, source.Kind, source.Key, source.Revision, Uuid(source.ProgramId), (int)source.Markings, Text(source.FacilityCode),
                Text(document ? source.Key : null), Text(document ? source.Revision : null), Text(document ? null : source.Key), Integer(document ? null : int.Parse(source.Revision, System.Globalization.CultureInfo.InvariantCulture)));
            await command.ExecuteNonQueryAsync(ct);
        }
    }
    private async Task HistoryAsync(QmsRevisionView revision, CancellationToken ct)
    {
        var history = revision.History[^1];
        await using var command = uow.Command("INSERT INTO quality.qms_transition(code,revision,version,action,actor,at,note,reason,from_state,to_state) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)",
            revision.Code, revision.Revision, revision.Version, history.Action, history.Actor, history.At, history.Note, history.Reason, Text(history.FromState?.ToString()), history.ToState.ToString());
        await command.ExecuteNonQueryAsync(ct);
    }
    private static string? String(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static DateTimeOffset? Time(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);
    private static NpgsqlParameter Text(string? value) => new() { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)value ?? DBNull.Value };
    private static NpgsqlParameter Uuid(Guid? value) => new() { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)value ?? DBNull.Value };
    private static NpgsqlParameter Timestamp(DateTimeOffset? value) => new() { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = (object?)value ?? DBNull.Value };
    private static NpgsqlParameter Integer(int? value) => new() { NpgsqlDbType = NpgsqlDbType.Integer, Value = (object?)value ?? DBNull.Value };
}

public static class QmsRegistration
{
    public static IServiceCollection AddForgeQms(this IServiceCollection services, string? organizationCode)
    {
        string? code = null;
        if (!string.IsNullOrWhiteSpace(organizationCode))
        {
            try { code = Forge.Kernel.Identity.BusinessCode.Require(organizationCode, "Organization"); }
            catch (Forge.Kernel.Commands.CommandRejectedException) { /* Explicitly unavailable; no inferred organization. */ }
        }
        services.AddSingleton(new QmsOptions(code));
        services.AddForgeStore<IQmsStore>(uow => new PostgresQmsStore(uow));
        services.AddForgeStore<IQmsLookup>(uow => new PostgresQmsStore(uow));
        services.AddSingleton<QmsService>();
        services.AddForgeStore<IQmsPolicyAdoptionStore>(uow => new PostgresQmsPolicyAdoptionStore(uow));
        services.AddForgeStore<IQmsPolicyAdoptionLookup>(uow => new PostgresQmsPolicyAdoptionStore(uow));
        services.AddSingleton<QmsPolicyAdoptionService>();
        services.AddSingleton<QmsPolicyReader>();
        services.AddSingleton<IQmsPolicyReader>(sp => sp.GetRequiredService<QmsPolicyReader>());
        // AddForgeQuality already embeds/discovers this assembly's additive migrations.
        return services;
    }
}
