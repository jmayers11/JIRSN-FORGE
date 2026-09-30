using Forge.Kernel.Access;
using Forge.Kernel.Audit;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using NpgsqlTypes;
using System.Text.Json;
using Npgsql;

namespace Forge.Kernel.Postgres;

internal sealed class PostgresPeopleStore(PostgresUnitOfWork uow) : IPeopleStore
{
    public async Task<PersonRecord?> FindAsync(PersonId id, CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT person_id, idp_subject, display_name, is_active, markings FROM kernel.person WHERE person_id = $1", id.Value);
        return await ReadPerson(cmd, ct);
    }

    public async Task<PersonRecord?> FindBySubjectAsync(string idpSubject, CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT person_id, idp_subject, display_name, is_active, markings FROM kernel.person WHERE idp_subject = $1", idpSubject);
        return await ReadPerson(cmd, ct);
    }

    public async Task InsertAsync(PersonRecord person, DateTimeOffset at, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(person);
        await using var cmd = uow.Command(
            "INSERT INTO kernel.person (person_id, idp_subject, display_name, is_active, markings, created_at, created_by) VALUES ($1, $2, $3, $4, $5, $6, $7)",
            person.Id.Value, person.IdpSubject, person.DisplayName, person.IsActive, (int)person.Markings, at, actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> AddRoleAsync(PersonId id, string role, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "INSERT INTO kernel.role_assignment (person_id, role, granted_at, granted_by) VALUES ($1, $2, $3, $4) ON CONFLICT DO NOTHING",
            id.Value, role, at, actor);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> RemoveRoleAsync(PersonId id, string role, CancellationToken ct)
    {
        await using var cmd = uow.Command("DELETE FROM kernel.role_assignment WHERE person_id = $1 AND role = $2", id.Value, role);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> AddProgramAccessAsync(PersonId id, ProgramId program, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "INSERT INTO kernel.program_access (person_id, program_id, granted_at, granted_by) VALUES ($1, $2, $3, $4) ON CONFLICT DO NOTHING",
            id.Value, program.Value, at, actor);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> RemoveProgramAccessAsync(PersonId id, ProgramId program, CancellationToken ct)
    {
        await using var cmd = uow.Command("DELETE FROM kernel.program_access WHERE person_id = $1 AND program_id = $2", id.Value, program.Value);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<ForgePrincipal?> LoadPrincipalAsync(PersonId id, CancellationToken ct)
    {
        var person = await FindAsync(id, ct);
        if (person is null)
        {
            return null;
        }

        var roles = new HashSet<string>(StringComparer.Ordinal);
        await using (var cmd = uow.Command("SELECT role FROM kernel.role_assignment WHERE person_id = $1", id.Value))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                roles.Add(reader.GetString(0));
            }
        }

        var programs = new HashSet<ProgramId>();
        await using (var cmd = uow.Command("SELECT program_id FROM kernel.program_access WHERE person_id = $1", id.Value))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                programs.Add(new ProgramId(reader.GetGuid(0)));
            }
        }

        return new ForgePrincipal(person.Id, person.DisplayName, person.IsActive, roles, programs, person.Markings);
    }

    public async Task<int> CountActiveWithRoleAsync(string role, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "SELECT count(*) FROM kernel.role_assignment r JOIN kernel.person p ON p.person_id = r.person_id WHERE r.role = $1 AND p.is_active",
            role);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<PersonRecord?> ReadPerson(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new PersonRecord(
            new PersonId(reader.GetGuid(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetBoolean(3),
            (DataMarking)reader.GetInt32(4));
    }
}

internal sealed class PostgresOutbox(PostgresUnitOfWork uow) : IEventOutbox
{
    public async Task EnqueueAsync(EventEnvelope envelope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        await using var cmd = uow.Command(
            "INSERT INTO kernel.outbox (event_id, envelope, enqueued_at) VALUES ($1, $2, $3)",
            envelope.EventId, EnvelopeSerializer.Serialize(envelope), envelope.RecordedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

internal sealed class PostgresAuditWriter(PostgresUnitOfWork uow) : IAuditWriter
{
    // Serializes audit appends across all transactions so the chain has exactly one tip.
    private const long ChainLock = 0x41_55_44_49_54; // "AUDIT"

    public async Task<ChainedAuditEntry> AppendAsync(AuditEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry = entry with { RecordedAt = Truncate(entry.RecordedAt) };

        await using (var lockCmd = uow.Command($"SELECT pg_advisory_xact_lock({ChainLock})"))
        {
            await lockCmd.ExecuteNonQueryAsync(ct);
        }

        string? previous;
        await using (var tip = uow.Command("SELECT hash FROM kernel.audit_log ORDER BY sequence DESC LIMIT 1"))
        {
            previous = await tip.ExecuteScalarAsync(ct) as string;
        }

        var hash = AuditChain.ComputeHash(previous, entry);
        await using var insert = uow.Command(
            """
            INSERT INTO kernel.audit_log (audit_id, recorded_at, actor, on_behalf_of, action, outcome, entity_type, entity_id,
                old_value, new_value, reason, source, correlation_id, program_id, markings, previous_hash, hash)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17)
            RETURNING sequence
            """,
            entry.AuditId, entry.RecordedAt, entry.Actor, entry.OnBehalfOf, entry.Action, entry.Outcome, entry.EntityType, entry.EntityId,
            entry.OldValue, entry.NewValue, entry.Reason, entry.Source, entry.CorrelationId, entry.ProgramId?.Value, (int)entry.Markings,
            previous, hash);
        var sequence = (long)(await insert.ExecuteScalarAsync(ct))!;
        return new ChainedAuditEntry(sequence, entry, previous, hash);
    }

    private static DateTimeOffset Truncate(DateTimeOffset value) =>
        new(value.UtcDateTime.Ticks / 10 * 10, TimeSpan.Zero);
}

internal sealed class PostgresIdempotencyStore(PostgresUnitOfWork uow) : IIdempotencyStore
{
    public async Task<CommandResult?> ReserveAsync(string key, string commandType, string requestHash, DateTimeOffset at, CancellationToken ct)
    {
        // If another transaction holds this key, the insert waits for it, then does nothing.
        await using (var insert = uow.Command(
            "INSERT INTO kernel.idempotency (idempotency_key, command_type, request_hash, created_at) VALUES ($1, $2, $3, $4) ON CONFLICT DO NOTHING",
            key, commandType, requestHash, at))
        {
            if (await insert.ExecuteNonQueryAsync(ct) == 1)
            {
                return null;
            }
        }

        await using var select = uow.Command("SELECT command_type, request_hash, result FROM kernel.idempotency WHERE idempotency_key = $1", key);
        await using var reader = await select.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException("Idempotency record vanished.");
        }

        if (reader.GetString(0) != commandType || reader.GetString(1) != requestHash)
        {
            throw new IdempotencyConflictException(key);
        }

        return reader.IsDBNull(2)
            ? throw new IdempotencyConflictException(key)
            : JsonSerializer.Deserialize<CommandResult>(reader.GetString(2), CanonicalJson.Options);
    }

    public async Task CompleteAsync(string key, CommandResult result, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE kernel.idempotency SET result = $2 WHERE idempotency_key = $1",
            key, new NpgsqlParameter { Value = CanonicalJson.Serialize(result), NpgsqlDbType = NpgsqlDbType.Text });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
