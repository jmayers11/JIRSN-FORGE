using System.Globalization;
using System.Text.Json;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Postgres;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Quality.Infrastructure;

internal sealed class PostgresQmsPolicyAdoptionStore(PostgresUnitOfWork uow) : IQmsPolicyAdoptionStore
{
    public async Task LockAsync(string code, CancellationToken ct)
    {
        await using var command = uow.Command("SELECT code FROM quality.qms_policy_adoption WHERE code=$1 FOR UPDATE", code);
        await command.ExecuteScalarAsync(ct);
    }
    public async Task LockScopeAsync(string organization, string facility, QmsPolicyPurpose purpose, bool shared, CancellationToken ct)
    {
        var sql = shared ? "SELECT pg_advisory_xact_lock_shared(hashtextextended($1,0))" : "SELECT pg_advisory_xact_lock(hashtextextended($1,0))";
        await using var command = uow.Command(sql, $"forge.qms.policy:{organization}:{facility}:{purpose}");
        await command.ExecuteScalarAsync(ct);
    }
    public async Task LockReadAsync(string code, int revision, CancellationToken ct)
    {
        if (uow.IsReadOnly) return;
        await using var command = uow.Command("SELECT h.code FROM quality.qms_policy_adoption h JOIN quality.qms_policy_adoption_revision r ON r.code=h.code WHERE h.code=$1 AND r.revision=$2 FOR SHARE OF h,r", code, revision);
        await command.ExecuteScalarAsync(ct);
    }
    public async Task<QmsPolicyAdoptionView?> FindAsync(string code, CancellationToken ct)
    {
        QmsPolicyAdoptionView header;
        await using (var command = uow.Command("SELECT organization_code,facility_code,program_id,markings,created_by,created_at FROM quality.qms_policy_adoption WHERE code=$1", code))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            header = new(code, reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), (DataMarking)reader.GetInt32(3), reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5), []);
        }
        var revisions = new List<QmsPolicyAdoptionRevision>();
        await using (var command = uow.Command("SELECT revision,version,state,content::text,created_by,last_edited_by,reviewed_by,approved_by,effective_from,effective_to FROM quality.qms_policy_adoption_revision WHERE code=$1 ORDER BY revision", code))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) revisions.Add(new(code, reader.GetInt32(0), reader.GetInt32(1), Enum.Parse<QmsState>(reader.GetString(2)), JsonSerializer.Deserialize<QmsPolicyAdoptionContent>(reader.GetString(3), CanonicalJson.Options)!,
                reader.GetString(4), reader.GetString(5), Text(reader,6), Text(reader,7), Time(reader,8), Time(reader,9), [], []));
        }
        for (var index=0; index<revisions.Count; index++)
        {
            var revision = revisions[index];
            var sources = new List<QmsSourceReference>();
            await using (var command = uow.Command("SELECT source_code,source_revision,program_id,markings,facility_code FROM quality.qms_policy_adoption_source WHERE code=$1 AND revision=$2 ORDER BY source_code,source_revision", code, revision.Revision))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct)) sources.Add(new("QmsRevision", reader.GetString(0), reader.GetInt32(1).ToString(CultureInfo.InvariantCulture), reader.IsDBNull(2) ? null : reader.GetGuid(2), (DataMarking)reader.GetInt32(3), reader.GetString(4)));
            }
            var history = new List<QmsTransition>();
            await using (var command = uow.Command("SELECT action,actor,at,note,reason,from_state,to_state,version FROM quality.qms_policy_adoption_transition WHERE code=$1 AND revision=$2 ORDER BY version", code, revision.Revision))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct)) history.Add(new(reader.GetString(0),reader.GetString(1),reader.GetFieldValue<DateTimeOffset>(2),reader.GetString(3),reader.GetString(4), reader.IsDBNull(5) ? null : Enum.Parse<QmsState>(reader.GetString(5)),Enum.Parse<QmsState>(reader.GetString(6)),reader.GetInt32(7)));
            }
            revisions[index] = revision with {Sources=sources,History=history};
        }
        return header with {Revisions=revisions};
    }
    public async Task<IReadOnlyList<QmsPolicyAdoptionView>> ListAsync(CancellationToken ct)
    {
        var codes = new List<string>();
        await using (var command = uow.Command("SELECT code FROM quality.qms_policy_adoption ORDER BY code"))
        await using (var reader = await command.ExecuteReaderAsync(ct)) { while(await reader.ReadAsync(ct)) codes.Add(reader.GetString(0)); }
        var list = new List<QmsPolicyAdoptionView>();
        foreach(var code in codes) list.Add((await FindAsync(code,ct))!);
        return list;
    }
    public async Task InsertAsync(QmsPolicyAdoptionView adoption, CancellationToken ct)
    {
        await using var command = uow.Command("INSERT INTO quality.qms_policy_adoption(code,organization_code,facility_code,program_id,purpose,markings,created_by,created_at) VALUES($1,$2,$3,$4,$5,$6,$7,$8)",
            adoption.Code,adoption.OrganizationCode,adoption.FacilityCode,Uuid(adoption.ProgramId),adoption.Revisions[0].Content.Purpose.ToString(),(int)adoption.Markings,adoption.CreatedBy,adoption.CreatedAt);
        await command.ExecuteNonQueryAsync(ct);
    }
    public async Task InsertRevisionAsync(QmsPolicyAdoptionRevision revision, CancellationToken ct)
    {
        await using(var command = uow.Command("INSERT INTO quality.qms_policy_adoption_revision(code,revision,version,state,content,policy_code,policy_revision,created_by,last_edited_by) VALUES($1,$2,1,'Draft',$3::jsonb,$4,$5,$6,$7)",
            revision.Code,revision.Revision,CanonicalJson.Serialize(revision.Content),BusinessCode.Require(revision.Content.PolicyCode,"Policy"),revision.Content.PolicyRevision,revision.CreatedBy,revision.LastEditedBy)) await command.ExecuteNonQueryAsync(ct);
        await SourcesAsync(revision,ct); await HistoryAsync(revision,ct);
    }
    public async Task UpdateAsync(QmsPolicyAdoptionRevision revision, int expectedVersion, bool replaceSources, CancellationToken ct)
    {
        await using(var command = uow.Command("UPDATE quality.qms_policy_adoption_revision SET version=$3,state=$4,content=$5::jsonb,policy_code=$6,policy_revision=$7,last_edited_by=$8,reviewed_by=$9,approved_by=$10,effective_from=$11,effective_to=$12 WHERE code=$1 AND revision=$2 AND version=$13",
            revision.Code,revision.Revision,revision.Version,revision.State.ToString(),CanonicalJson.Serialize(revision.Content),BusinessCode.Require(revision.Content.PolicyCode,"Policy"),revision.Content.PolicyRevision,revision.LastEditedBy,
            String(revision.ReviewedBy),String(revision.ApprovedBy),Timestamp(revision.EffectiveFrom),Timestamp(revision.EffectiveTo),expectedVersion))
            if(await command.ExecuteNonQueryAsync(ct)!=1) throw new CommandRejectedException("stale_version","The adoption changed; read its current version before deciding.");
        if(replaceSources)
        {
            await using var command=uow.Command("DELETE FROM quality.qms_policy_adoption_source WHERE code=$1 AND revision=$2",revision.Code,revision.Revision);
            await command.ExecuteNonQueryAsync(ct); await SourcesAsync(revision,ct);
        }
        await HistoryAsync(revision,ct);
    }
    private async Task SourcesAsync(QmsPolicyAdoptionRevision revision,CancellationToken ct)
    {
        foreach(var source in revision.Sources)
        {
            await using var command=uow.Command("INSERT INTO quality.qms_policy_adoption_source(code,revision,source_code,source_revision,program_id,markings,facility_code) VALUES($1,$2,$3,$4,$5,$6,$7)",
                revision.Code,revision.Revision,source.Key,int.Parse(source.Revision,CultureInfo.InvariantCulture),Uuid(source.ProgramId),(int)source.Markings,source.FacilityCode!);
            await command.ExecuteNonQueryAsync(ct);
        }
    }
    private async Task HistoryAsync(QmsPolicyAdoptionRevision revision,CancellationToken ct)
    {
        var last=revision.History[^1];
        await using var command=uow.Command("INSERT INTO quality.qms_policy_adoption_transition(code,revision,version,action,actor,at,note,reason,from_state,to_state) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)",
            revision.Code,revision.Revision,revision.Version,last.Action,last.Actor,last.At,last.Note,last.Reason,String(last.FromState?.ToString()),last.ToState.ToString());
        await command.ExecuteNonQueryAsync(ct);
    }
    private static string? Text(NpgsqlDataReader reader,int index)=>reader.IsDBNull(index)?null:reader.GetString(index);
    private static DateTimeOffset? Time(NpgsqlDataReader reader,int index)=>reader.IsDBNull(index)?null:reader.GetFieldValue<DateTimeOffset>(index);
    private static NpgsqlParameter String(string? value)=>new(){NpgsqlDbType=NpgsqlDbType.Text,Value=(object?)value??DBNull.Value};
    private static NpgsqlParameter Uuid(Guid? value)=>new(){NpgsqlDbType=NpgsqlDbType.Uuid,Value=(object?)value??DBNull.Value};
    private static NpgsqlParameter Timestamp(DateTimeOffset? value)=>new(){NpgsqlDbType=NpgsqlDbType.TimestampTz,Value=(object?)value??DBNull.Value};
}
