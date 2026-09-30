using Forge.Kernel.Access;
using Forge.Kernel.Audit;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Npgsql;

namespace Forge.Kernel.Postgres;

public sealed record PersonView(Guid PersonId, string IdpSubject, string DisplayName, bool IsActive, string Markings, IReadOnlyList<string> Roles, IReadOnlyList<Guid> Programs);

public sealed record AuditChainStatus(bool Intact, long EntriesChecked, long? FirstBrokenSequence);

/// <summary>Read-only queries for the kernel. Callers must authorize before calling.</summary>
public sealed class KernelReadStore(NpgsqlDataSource dataSource, IKernelUnitOfWorkFactory unitOfWork)
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
                   COALESCE(array_agg(DISTINCT r.role) FILTER (WHERE r.role IS NOT NULL), '{}') AS roles,
                   COALESCE(array_agg(DISTINCT a.program_id) FILTER (WHERE a.program_id IS NOT NULL), '{}') AS programs
            FROM kernel.person p
            LEFT JOIN kernel.role_assignment r ON r.person_id = p.person_id
            LEFT JOIN kernel.program_access a ON a.person_id = p.person_id
            GROUP BY p.person_id
            ORDER BY p.display_name, p.person_id
            """);
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

    /// <summary>Audit entries, newest first. Entries tied to a program are returned only for the given programs.</summary>
    public async Task<IReadOnlyList<ChainedAuditEntry>> QueryAuditAsync(string? entityType, string? entityId, IReadOnlySet<ProgramId> visiblePrograms, int take, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(visiblePrograms);
        await using var cmd = dataSource.CreateCommand(
            """
            SELECT sequence, audit_id, recorded_at, actor, on_behalf_of, action, outcome, entity_type, entity_id,
                   old_value, new_value, reason, source, correlation_id, program_id, markings, previous_hash, hash
            FROM kernel.audit_log
            WHERE ($1::text IS NULL OR entity_type = $1)
              AND ($2::text IS NULL OR entity_id = $2)
              AND (program_id IS NULL OR program_id = ANY($3))
            ORDER BY sequence DESC
            LIMIT $4
            """);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)entityType ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)entityId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(visiblePrograms.Select(p => p.Value).ToArray());
        cmd.Parameters.AddWithValue(Math.Clamp(take, 1, 500));
        return await ReadAudit(cmd, ct);
    }

    /// <summary>Recomputes the whole hash chain from the first entry. Any edit, deletion or reordering is detected.</summary>
    public async Task<AuditChainStatus> VerifyAuditChainAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            """
            SELECT sequence, audit_id, recorded_at, actor, on_behalf_of, action, outcome, entity_type, entity_id,
                   old_value, new_value, reason, source, correlation_id, program_id, markings, previous_hash, hash
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
