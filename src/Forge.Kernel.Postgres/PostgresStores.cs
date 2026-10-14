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

    public async Task<bool> SetActiveAsync(PersonId id, bool active, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE kernel.person SET is_active = $2, version = version + 1 WHERE person_id = $1 AND is_active <> $2",
            id.Value, active);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> AddRoleAsync(PersonId id, string role, DateTimeOffset at, string actor, DateTimeOffset? expiresAt, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "INSERT INTO kernel.role_assignment (person_id, role, granted_at, granted_by, expires_at) VALUES ($1, $2, $3, $4, $5) ON CONFLICT (person_id, role) DO UPDATE SET granted_at = EXCLUDED.granted_at, granted_by = EXCLUDED.granted_by, expires_at = EXCLUDED.expires_at WHERE kernel.role_assignment.expires_at IS NOT NULL AND kernel.role_assignment.expires_at <= $3",
            id.Value, role, at, actor, expiresAt);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> CreateRoleAsync(string role, string displayName, IReadOnlyList<string> permissions, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var roleCommand = uow.Command(
            "INSERT INTO kernel.role_definition (role_name, display_name, created_at, updated_at) VALUES ($1, $2, $3, $3) ON CONFLICT DO NOTHING",
            role, displayName, at);
        if (await roleCommand.ExecuteNonQueryAsync(ct) == 0)
        {
            return false;
        }

        foreach (var permission in permissions.Distinct(StringComparer.Ordinal))
        {
            await AddRolePermissionAsync(role, permission, at, actor, ct);
        }

        return true;
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
        var principal = await LoadBasePrincipalAsync(id, ct);
        if (principal is null) return null;
        var delegated = new List<DelegatedPermission>();
        var store = new PostgresDelegationStore(uow);
        foreach (var record in await store.ActiveForRecipientAsync(id, uow.Now, ct))
        {
            var grantor = await LoadBasePrincipalAsync(record.Grantor, ct);
            if (grantor is null) continue;
            foreach (var permission in record.Permissions)
            {
                if (DelegationAuthority.CanLend(grantor, permission, record.Scopes))
                    delegated.Add(new(record.DelegationId, permission, record.Scopes, record.StartsAt, record.ExpiresAt,
                        grantor.ApprovalThresholds?.FirstOrDefault(threshold => threshold.Permission == permission)));
            }
        }
        // Thresholds remain action policy even when the action is received temporarily.
        var thresholds = principal.ApprovalThresholds?.ToHashSet() ?? [];
        if (delegated.Count > 0)
        {
            await using var cmd = uow.Command("SELECT permission, maximum_amount, currency FROM kernel.approval_threshold_policy WHERE permission = ANY($1)", (object)delegated.Select(grant => grant.Permission).Distinct().ToArray());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) thresholds.Add(new(reader.GetString(0), reader.GetDecimal(1), reader.GetString(2)));
        }
        return principal with { DelegatedPermissions = delegated, ApprovalThresholds = thresholds.ToList() };
    }

    private async Task<ForgePrincipal?> LoadBasePrincipalAsync(PersonId id, CancellationToken ct)
    {
        var person = await FindAsync(id, ct);
        if (person is null)
        {
            return null;
        }

        var roles = new HashSet<string>(StringComparer.Ordinal);
        await using (var cmd = uow.Command("SELECT role FROM kernel.role_assignment WHERE person_id = $1 AND (expires_at IS NULL OR expires_at > $2)", id.Value, uow.Now))
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

        var permissions = new HashSet<string>(StringComparer.Ordinal);
        var scopedPermissions = new List<ScopedPermission>();
        var approvalThresholds = new HashSet<ApprovalThreshold>();
        await using (var cmd = uow.Command(
            "SELECT rp.permission, rps.scope_type, rps.scope_id, atp.maximum_amount, atp.currency FROM kernel.role_assignment ra JOIN kernel.role_permission rp ON rp.role_name = ra.role JOIN kernel.role_definition rd ON rd.role_name = ra.role LEFT JOIN kernel.role_permission_scope rps ON rps.role_name = rp.role_name AND rps.permission = rp.permission LEFT JOIN kernel.approval_threshold_policy atp ON atp.permission = rp.permission WHERE ra.person_id = $1 AND (ra.expires_at IS NULL OR ra.expires_at > $2) AND rd.is_active AND (rps.scope_type IS NOT NULL OR NOT EXISTS (SELECT 1 FROM kernel.role_permission_scope s WHERE s.role_name = rp.role_name AND s.permission = rp.permission))",
            id.Value, uow.Now))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                if (reader.IsDBNull(1))
                {
                    permissions.Add(reader.GetString(0));
                }
                else
                {
                    scopedPermissions.Add(new ScopedPermission(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
                }

                if (!reader.IsDBNull(3))
                {
                    approvalThresholds.Add(new ApprovalThreshold(reader.GetString(0), reader.GetDecimal(3), reader.GetString(4)));
                }
            }
        }

        return new ForgePrincipal(person.Id, person.DisplayName, person.IsActive, roles, programs, person.Markings, permissions, scopedPermissions, approvalThresholds.ToList());
    }

    public async Task<int> CountActiveWithRoleAsync(string role, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "SELECT count(*) FROM kernel.role_assignment r JOIN kernel.person p ON p.person_id = r.person_id WHERE r.role = $1 AND p.is_active AND (r.expires_at IS NULL OR r.expires_at > $2)",
            role, uow.Now);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<bool> HasRoleAsync(string role, CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT EXISTS (SELECT 1 FROM kernel.role_definition WHERE role_name = $1 AND is_active)", role);
        return (bool)(await cmd.ExecuteScalarAsync(ct) ?? false);
    }

    public async Task<bool> HasPermissionAsync(string permission, CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT EXISTS (SELECT 1 FROM kernel.permission_catalog WHERE permission = $1)", permission);
        return (bool)(await cmd.ExecuteScalarAsync(ct) ?? false);
    }

    public async Task<bool> AddRolePermissionAsync(string role, string permission, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "INSERT INTO kernel.role_permission (role_name, permission, granted_at, granted_by) VALUES ($1, $2, $3, $4) ON CONFLICT DO NOTHING",
            role, permission, at, actor);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> RemoveRolePermissionAsync(string role, string permission, CancellationToken ct)
    {
        await using var cmd = uow.Command("DELETE FROM kernel.role_permission WHERE role_name = $1 AND permission = $2", role, permission);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> AddRolePermissionScopeAsync(string role, string permission, string scopeType, string scopeId, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "INSERT INTO kernel.role_permission_scope (role_name, permission, scope_type, scope_id) VALUES ($1, $2, $3, $4) ON CONFLICT DO NOTHING",
            role, permission, scopeType, scopeId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> RemoveRolePermissionScopeAsync(string role, string permission, string scopeType, string scopeId, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "DELETE FROM kernel.role_permission_scope WHERE role_name = $1 AND permission = $2 AND scope_type = $3 AND scope_id = $4",
            role, permission, scopeType, scopeId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<int> CountRolePermissionScopesAsync(string role, string permission, CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT count(*) FROM kernel.role_permission_scope WHERE role_name = $1 AND permission = $2", role, permission);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<bool> IsSeparationPolicyEnabledAsync(string policy, CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT enabled FROM kernel.separation_policy WHERE policy_id = $1", policy);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is bool enabled && enabled;
    }

    public async Task<bool> SetSeparationPolicyAsync(string policy, bool enabled, CancellationToken ct)
    {
        await using var cmd = uow.Command("UPDATE kernel.separation_policy SET enabled = $2, updated_at = now() WHERE policy_id = $1 AND enabled <> $2", policy, enabled);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> SetApprovalThresholdAsync(string permission, decimal? maximumAmount, string? currency, DateTimeOffset at, string actor, CancellationToken ct)
    {
        if (maximumAmount is null)
        {
            await using var delete = uow.Command("DELETE FROM kernel.approval_threshold_policy WHERE permission = $1", permission);
            return await delete.ExecuteNonQueryAsync(ct) == 1;
        }

        await using var cmd = uow.Command(
            "INSERT INTO kernel.approval_threshold_policy (permission, maximum_amount, currency, updated_at, updated_by) VALUES ($1, $2, $3, $4, $5) ON CONFLICT (permission) DO UPDATE SET maximum_amount = EXCLUDED.maximum_amount, currency = EXCLUDED.currency, updated_at = EXCLUDED.updated_at, updated_by = EXCLUDED.updated_by WHERE kernel.approval_threshold_policy.maximum_amount <> EXCLUDED.maximum_amount OR kernel.approval_threshold_policy.currency <> EXCLUDED.currency",
            permission, maximumAmount.Value, currency!, at, actor);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
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
                old_value, new_value, reason, source, correlation_id, program_id, markings, previous_hash, hash, owning_resource, context_fingerprint)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19)
            RETURNING sequence
            """,
            entry.AuditId, entry.RecordedAt, entry.Actor, entry.OnBehalfOf, entry.Action, entry.Outcome, entry.EntityType, entry.EntityId,
            entry.OldValue, entry.NewValue, entry.Reason, entry.Source, entry.CorrelationId, entry.ProgramId?.Value, (int)entry.Markings,
            previous, hash, new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb,
                Value = entry.OwningResource is null ? DBNull.Value : CanonicalJson.Serialize(entry.OwningResource),
            }, entry.ContextFingerprint);
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
