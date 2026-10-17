using Forge.Kernel.Access;
using Forge.Kernel.Audit;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Npgsql;

namespace Forge.Kernel.Postgres;

public sealed record PersonView(Guid PersonId, string IdpSubject, string DisplayName, bool IsActive, string Markings, IReadOnlyList<string> Roles, IReadOnlyList<Guid> Programs);
public sealed record RolePermissionScopeView(string ScopeType, string ScopeId);
public sealed record RolePermissionView(string Permission, IReadOnlyList<RolePermissionScopeView> Scopes);
public sealed record RolePolicyView(string Role, string DisplayName, IReadOnlyList<RolePermissionView> Permissions);
public sealed record SeparationPolicyView(string Policy, bool Enabled);
public sealed record ApprovalThresholdPolicyView(string Permission, decimal MaximumAmount, string Currency);
public sealed record AccessPolicyView(IReadOnlyList<RolePolicyView> Roles, IReadOnlyList<SeparationPolicyView> SeparationPolicies, IReadOnlyList<ApprovalThresholdPolicyView> ApprovalThresholds);

public sealed record AuditChainStatus(bool Intact, long EntriesChecked, long? FirstBrokenSequence);

/// <summary>Read-only queries for the kernel. Callers must authorize before calling.</summary>
public sealed class KernelReadStore(NpgsqlDataSource dataSource, IKernelUnitOfWorkFactory unitOfWork, TimeProvider time)
{
    public async Task<ForgePrincipal?> ResolvePrincipalBySubjectAsync(string idpSubject, CancellationToken ct)
    {
        await using var uow = await unitOfWork.BeginAsync(ct);
        var person = await uow.People.FindBySubjectAsync(idpSubject, ct);
        return person is null ? null : await uow.People.LoadPrincipalAsync(person.Id, ct);
    }

    public async Task<IReadOnlyList<PersonView>> ListPeopleAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            """
            SELECT p.person_id, p.idp_subject, p.display_name, p.is_active, p.markings,
                   COALESCE(array_agg(DISTINCT r.role) FILTER (WHERE r.role IS NOT NULL AND (r.expires_at IS NULL OR r.expires_at > $1)), '{}') AS roles,
                   COALESCE(array_agg(DISTINCT a.program_id) FILTER (WHERE a.program_id IS NOT NULL), '{}') AS programs
            FROM kernel.person p
            LEFT JOIN kernel.role_assignment r ON r.person_id = p.person_id
            LEFT JOIN kernel.program_access a ON a.person_id = p.person_id
            GROUP BY p.person_id
            ORDER BY p.display_name, p.person_id
            """);
        cmd.Parameters.AddWithValue(time.GetUtcNow());
        var people = new List<PersonView>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            people.Add(new PersonView(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                ((DataMarking)reader.GetInt32(4)).ToString(),
                reader.GetFieldValue<string[]>(5),
                reader.GetFieldValue<Guid[]>(6)));
        }

        return people;
    }

    public async Task<AccessPolicyView> ReadAccessPolicyAsync(CancellationToken ct)
    {
        var roleNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var permissions = new Dictionary<string, Dictionary<string, List<RolePermissionScopeView>>>(StringComparer.Ordinal);
        await using (var cmd = dataSource.CreateCommand(
            "SELECT rd.role_name, rd.display_name, rp.permission, rps.scope_type, rps.scope_id FROM kernel.role_definition rd LEFT JOIN kernel.role_permission rp ON rp.role_name = rd.role_name LEFT JOIN kernel.role_permission_scope rps ON rps.role_name = rp.role_name AND rps.permission = rp.permission WHERE rd.is_active ORDER BY rd.role_name, rp.permission, rps.scope_type, rps.scope_id"))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var role = reader.GetString(0);
                roleNames[role] = reader.GetString(1);
                if (reader.IsDBNull(2)) continue;
                if (!permissions.TryGetValue(role, out var byPermission)) permissions[role] = byPermission = new(StringComparer.Ordinal);
                var permission = reader.GetString(2);
                if (!byPermission.TryGetValue(permission, out var scopes)) byPermission[permission] = scopes = [];
                if (!reader.IsDBNull(3)) scopes.Add(new RolePermissionScopeView(reader.GetString(3), reader.GetString(4)));
            }
        }

        var roles = roleNames.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry =>
            new RolePolicyView(entry.Key, entry.Value,
                permissions.GetValueOrDefault(entry.Key)?.OrderBy(permission => permission.Key, StringComparer.Ordinal)
                    .Select(permission => new RolePermissionView(permission.Key, permission.Value.AsReadOnly())).ToArray() ?? []))
            .ToArray();

        var separation = new List<SeparationPolicyView>();
        await using (var cmd = dataSource.CreateCommand("SELECT policy_id, enabled FROM kernel.separation_policy ORDER BY policy_id"))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) separation.Add(new(reader.GetString(0), reader.GetBoolean(1)));

        var thresholds = new List<ApprovalThresholdPolicyView>();
        await using (var cmd = dataSource.CreateCommand("SELECT permission, maximum_amount, currency FROM kernel.approval_threshold_policy ORDER BY permission"))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) thresholds.Add(new(reader.GetString(0), reader.GetDecimal(1), reader.GetString(2)));

        return new(roles, separation, thresholds);
    }

    /// <summary>Audit entries, newest first. Entries tied to a program are returned only for the given programs.</summary>
    public async Task<IReadOnlyList<ChainedAuditEntry>> QueryAuditAsync(string? entityType, string? entityId, IReadOnlySet<ProgramId> visiblePrograms, int take, CancellationToken ct, DataMarking visibleMarkings = DataMarking.None)
    {
        ArgumentNullException.ThrowIfNull(visiblePrograms);
        await using var cmd = dataSource.CreateCommand(
            """
            SELECT sequence, audit_id, recorded_at, actor, on_behalf_of, action, outcome, entity_type, entity_id,
                   old_value, new_value, reason, source, correlation_id, program_id, markings, previous_hash, hash, owning_resource, context_fingerprint
            FROM kernel.audit_log
            WHERE ($1::text IS NULL OR entity_type = $1)
              AND ($2::text IS NULL OR entity_id = $2)
              AND (program_id IS NULL OR program_id = ANY($3))
              AND (markings & $5)=markings
            ORDER BY sequence DESC
            LIMIT $4
            """);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)entityType ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)entityId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(visiblePrograms.Select(p => p.Value).ToArray());
        cmd.Parameters.AddWithValue(Math.Clamp(take, 1, 500));
        cmd.Parameters.AddWithValue((int)visibleMarkings);
        return await ReadAudit(cmd, ct);
    }

    /// <summary>Successful changes to the given entity types recorded in (after, upTo], oldest first. Program-tied entries only for the given programs.</summary>
    public async Task<IReadOnlyList<ChainedAuditEntry>> QueryAuditBetweenAsync(DateTimeOffset after, DateTimeOffset upTo, IReadOnlyCollection<string> entityTypes, IReadOnlySet<ProgramId> visiblePrograms, int take, CancellationToken ct, DataMarking visibleMarkings = DataMarking.None)
    {
        ArgumentNullException.ThrowIfNull(entityTypes);
        ArgumentNullException.ThrowIfNull(visiblePrograms);
        await using var cmd = dataSource.CreateCommand(
            """
            SELECT sequence, audit_id, recorded_at, actor, on_behalf_of, action, outcome, entity_type, entity_id,
                   old_value, new_value, reason, source, correlation_id, program_id, markings, previous_hash, hash, owning_resource, context_fingerprint
            FROM kernel.audit_log
            WHERE recorded_at > $1 AND recorded_at <= $2 AND outcome = 'Succeeded'
              AND entity_type = ANY($3)
              AND (program_id IS NULL OR program_id = ANY($4))
              AND (markings & $6)=markings
            ORDER BY sequence
            LIMIT $5
            """);
        cmd.Parameters.AddWithValue(after);
        cmd.Parameters.AddWithValue(upTo);
        cmd.Parameters.AddWithValue(entityTypes.ToArray());
        cmd.Parameters.AddWithValue(visiblePrograms.Select(p => p.Value).ToArray());
        cmd.Parameters.AddWithValue(Math.Clamp(take, 1, 5000));
        cmd.Parameters.AddWithValue((int)visibleMarkings);
        return await ReadAudit(cmd, ct);
    }

    /// <summary>Recomputes the whole hash chain from the first entry. Any edit, deletion or reordering is detected.</summary>
    public async Task<AuditChainStatus> VerifyAuditChainAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            """
            SELECT sequence, audit_id, recorded_at, actor, on_behalf_of, action, outcome, entity_type, entity_id,
                   old_value, new_value, reason, source, correlation_id, program_id, markings, previous_hash, hash, owning_resource, context_fingerprint
            FROM kernel.audit_log ORDER BY sequence
            """);
        var entries = await ReadAudit(cmd, ct);
        if (entries.Count > 0 && entries[0].PreviousHash is not null)
        {
            return new AuditChainStatus(false, entries.Count, entries[0].Sequence);
        }

        var broken = AuditChain.FindFirstBreak(entries);
        return new AuditChainStatus(broken is null, entries.Count, broken);
    }

    private static async Task<List<ChainedAuditEntry>> ReadAudit(NpgsqlCommand cmd, CancellationToken ct)
    {
        var list = new List<ChainedAuditEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var entry = new AuditEntry
            {
                AuditId = reader.GetGuid(1),
                RecordedAt = reader.GetFieldValue<DateTimeOffset>(2),
                Actor = reader.GetString(3),
                OnBehalfOf = reader.IsDBNull(4) ? null : reader.GetString(4),
                Action = reader.GetString(5),
                Outcome = reader.GetString(6),
                EntityType = reader.GetString(7),
                EntityId = reader.GetString(8),
                OldValue = reader.IsDBNull(9) ? null : reader.GetString(9),
                NewValue = reader.IsDBNull(10) ? null : reader.GetString(10),
                Reason = reader.IsDBNull(11) ? null : reader.GetString(11),
                Source = reader.GetString(12),
                CorrelationId = reader.GetGuid(13),
                ProgramId = reader.IsDBNull(14) ? null : new ProgramId(reader.GetGuid(14)),
                Markings = (DataMarking)reader.GetInt32(15),
                OwningResource = reader.IsDBNull(18) ? null : System.Text.Json.JsonSerializer.Deserialize<ResourceRef>(reader.GetString(18), Forge.Kernel.Serialization.CanonicalJson.Options),
                ContextFingerprint = reader.IsDBNull(19) ? null : reader.GetString(19),
            };
            list.Add(new ChainedAuditEntry(
                reader.GetInt64(0),
                entry,
                reader.IsDBNull(16) ? null : reader.GetString(16),
                reader.GetString(17)));
        }

        return list;
    }
}
