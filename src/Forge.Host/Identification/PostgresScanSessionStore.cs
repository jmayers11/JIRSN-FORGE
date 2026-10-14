using System.Text.Json;
using System.Text.Json.Serialization;
using Forge.Kernel.Commands;
using Forge.Kernel.Postgres;
using NpgsqlTypes;

namespace Forge.Host.Identification;

public sealed class PostgresScanSessionStore(PostgresUnitOfWork uow) : IScanSessionStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };

    public async Task LockChannelAsync(string channel, CancellationToken ct)
    {
        await using var command = uow.Command("SELECT pg_advisory_xact_lock(hashtextextended($1, 41))", "forge.scan.channel:" + channel);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<Guid?> CurrentAsync(string channel, CancellationToken ct)
    {
        await using var command = uow.Command("SELECT session_id FROM kernel.identification_scan_channel WHERE channel_hash=$1", channel);
        return await command.ExecuteScalarAsync(ct) is Guid id ? id : null;
    }

    public async Task SetCurrentAsync(string channel, Guid session, CancellationToken ct)
    {
        await using var command = uow.Command("""
            INSERT INTO kernel.identification_scan_channel(channel_hash,session_id) VALUES($1,$2)
            ON CONFLICT(channel_hash) DO UPDATE SET session_id=EXCLUDED.session_id
            """, channel, session);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<ScanSessionView?> FindAsync(Guid id, CancellationToken ct)
    {
        await using var command = uow.Command("SELECT payload::text FROM kernel.identification_scan_session WHERE session_id=$1", id);
        return await command.ExecuteScalarAsync(ct) is string payload ? JsonSerializer.Deserialize<ScanSessionView>(payload, Json) : null;
    }

    public async Task SaveAsync(ScanSessionView session, int? expectedVersion, CancellationToken ct)
    {
        var payload = new Npgsql.NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = JsonSerializer.Serialize(session with { Accepted = null }, Json) };
        await using var command = expectedVersion is null
            ? uow.Command("""
                INSERT INTO kernel.identification_scan_session(session_id,channel_hash,version,state,principal_person_id,operator_person_id,
                    source_type,source_id,facility_code,program_id,markings,created_at,expires_at,payload)
                VALUES($1,$2,$3,$4,$5,$6,'OrderOperation',$7,$8,$9,$10,$11,$12,$13)
                """, session.Id, session.ChannelHash, session.Version, session.State.ToString(), session.PrincipalPersonId,
                session.OperatorPersonId, session.OrderNumber + "/" + session.Sequence, session.FacilityCode, session.ProgramId,
                (int)session.Markings, session.CreatedAt, session.ExpiresAt, payload)
            : uow.Command("""
                UPDATE kernel.identification_scan_session SET version=$1,state=$2,payload=$3
                WHERE session_id=$4 AND version=$5
                """, session.Version, session.State.ToString(), payload, session.Id, expectedVersion.Value);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new CommandRejectedException("scan_session_version_conflict", "The session changed. Reload its current version.");
    }

    public async Task AppendAcceptedAsync(Guid session, ScanAccepted accepted, CancellationToken ct)
    {
        await using var command = uow.Command("""
            INSERT INTO kernel.identification_scan_acceptance(acceptance_id,session_id,correlation_id,audit_id,command_type,
                entity_type,entity_id,operator_person_id,recorded_at,payload) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)
            """, accepted.AcceptanceId, session, accepted.CorrelationId, accepted.AuditId, accepted.CommandType,
            accepted.EntityType, accepted.EntityId, accepted.OperatorPersonId, accepted.RecordedAt,
            new Npgsql.NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = JsonSerializer.Serialize(accepted, Json) });
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<ScanAccepted>> AcceptedAsync(Guid session, CancellationToken ct)
    {
        await using var command = uow.Command("SELECT payload::text FROM kernel.identification_scan_acceptance WHERE session_id=$1 ORDER BY recorded_at,acceptance_id", session);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<ScanAccepted>();
        while (await reader.ReadAsync(ct)) rows.Add(JsonSerializer.Deserialize<ScanAccepted>(reader.GetString(0), Json)!);
        return rows;
    }

    public async Task<ScanAcceptedRecord?> FindAcceptedAsync(Guid acceptanceId, CancellationToken ct)
    {
        await using var command = uow.Command("SELECT session_id,payload::text FROM kernel.identification_scan_acceptance WHERE acceptance_id=$1", acceptanceId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(reader.GetGuid(0), JsonSerializer.Deserialize<ScanAccepted>(reader.GetString(1), Json)!) : null;
    }
}
