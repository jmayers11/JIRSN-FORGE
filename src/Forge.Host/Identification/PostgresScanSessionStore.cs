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
        var payload = new Npgsql.NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = JsonSerializer.Serialize(session with { Accepted = null, Attempts = null }, Json) };
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

    public async Task AppendAttemptAsync(ScanAttempt attempt, ScanAttemptRetentionPolicy retention, CancellationToken ct)
    {
        await using (var sessionLock = uow.Command("SELECT pg_advisory_xact_lock(hashtextextended($1, 42))", attempt.SessionId.ToString("D")))
            await sessionLock.ExecuteNonQueryAsync(ct);

        await using (var insert = uow.Command("""
            INSERT INTO kernel.identification_scan_attempt(attempt_id,session_id,session_version,command_type,outcome,
                validation_code,recorded_at,correlation_id,audit_id,operator_person_id,authenticated_actor,
                assurance_summary,workstation_binding_hash,entity_type,entity_id)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15)
            """, attempt.AttemptId, attempt.SessionId, attempt.SessionVersion, attempt.CommandType, attempt.Outcome,
            attempt.ValidationCode, attempt.RecordedAt, attempt.CorrelationId, attempt.AuditId, attempt.OperatorPersonId,
            attempt.AuthenticatedActor, attempt.AssuranceSummary, attempt.WorkstationBindingHash, attempt.EntityType, attempt.EntityId))
            await insert.ExecuteNonQueryAsync(ct);

        await using (var expire = uow.Command("""
            DELETE FROM kernel.identification_scan_attempt
            WHERE session_id=$1 AND recorded_at < $2::timestamptz - make_interval(days => $3)
            """, attempt.SessionId, attempt.RecordedAt, retention.RetentionDays))
            await expire.ExecuteNonQueryAsync(ct);

        await using var cap = uow.Command("""
            DELETE FROM kernel.identification_scan_attempt
            WHERE session_id=$1 AND attempt_id IN (
                SELECT attempt_id FROM (
                    SELECT attempt_id, row_number() OVER (ORDER BY attempt_sequence DESC) AS position
                    FROM kernel.identification_scan_attempt WHERE session_id=$1
                ) ranked WHERE position > $2
            )
            """, attempt.SessionId, retention.MaxPerSession);
        await cap.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<ScanAttempt>> AttemptsAsync(Guid session, CancellationToken ct)
    {
        await using var command = uow.Command("""
            SELECT attempt_id,session_id,session_version,command_type,outcome,validation_code,recorded_at,
                correlation_id,audit_id,operator_person_id,authenticated_actor,assurance_summary,
                workstation_binding_hash,entity_type,entity_id
            FROM kernel.identification_scan_attempt WHERE session_id=$1 ORDER BY attempt_sequence
            """, session);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<ScanAttempt>();
        while (await reader.ReadAsync(ct)) rows.Add(new(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetInt32(2), reader.GetString(3), reader.GetString(4),
            reader.GetString(5), reader.GetFieldValue<DateTimeOffset>(6), reader.GetGuid(7), reader.GetGuid(8),
            reader.GetGuid(9), reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14)));
        return rows;
    }
}
