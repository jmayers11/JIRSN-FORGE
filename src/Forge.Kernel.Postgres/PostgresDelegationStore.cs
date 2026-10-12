using System.Text.Json;
using Forge.Kernel.Access;
using Forge.Kernel.Identity;
using Forge.Kernel.Serialization;
using Npgsql;

namespace Forge.Kernel.Postgres;

internal sealed class PostgresDelegationStore(PostgresUnitOfWork uow) : IDelegationStore
{
    private const string Columns = "delegation_id,grantor_id,recipient_id,permissions,scopes::text,starts_at,expires_at,status,proposed_at,reason,approver_id,approved_at,approval_reason,revoked_at,revocation_reason";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<AuthorityDelegation?> FindAsync(Guid id, CancellationToken ct)
    {
        await using var command = uow.Command($"SELECT {Columns} FROM kernel.authority_delegation WHERE delegation_id=$1", id);
        return (await ReadAsync(command, ct)).SingleOrDefault();
    }

    public async Task<IReadOnlyList<AuthorityDelegation>> ListAsync(PersonId? participant, CancellationToken ct)
    {
        await using var command = uow.Command($"SELECT {Columns} FROM kernel.authority_delegation WHERE ($1::uuid IS NULL OR grantor_id=$1 OR recipient_id=$1) ORDER BY proposed_at DESC,delegation_id", participant?.Value);
        return await ReadAsync(command, ct);
    }

    public async Task<IReadOnlyList<AuthorityDelegation>> ActiveForRecipientAsync(PersonId recipient, DateTimeOffset at, CancellationToken ct)
    {
        await using var command = uow.Command($"SELECT {Columns} FROM kernel.authority_delegation WHERE recipient_id=$1 AND status='Approved' AND starts_at <= $2 AND expires_at > $2 ORDER BY delegation_id", recipient.Value, at);
        return await ReadAsync(command, ct);
    }

    public async Task InsertAsync(AuthorityDelegation record, CancellationToken ct)
    {
        await using var command = uow.Command("INSERT INTO kernel.authority_delegation(delegation_id,grantor_id,recipient_id,permissions,scopes,starts_at,expires_at,status,proposed_at,reason) VALUES($1,$2,$3,$4,$5::jsonb,$6,$7,$8,$9,$10)",
            record.DelegationId, record.Grantor.Value, record.Recipient.Value, record.Permissions.ToArray(), CanonicalJson.Serialize(record.Scopes),
            record.StartsAt, record.ExpiresAt, record.Status.ToString(), record.ProposedAt, record.Reason);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> ApproveAsync(Guid id, PersonId approver, DateTimeOffset at, string reason, CancellationToken ct)
    {
        await using var command = uow.Command("UPDATE kernel.authority_delegation SET status='Approved',approver_id=$2,approved_at=$3,approval_reason=$4 WHERE delegation_id=$1 AND status='PendingApproval' AND expires_at>$3", id, approver.Value, at, reason);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> RevokeAsync(Guid id, DateTimeOffset at, string reason, CancellationToken ct)
    {
        await using var command = uow.Command("UPDATE kernel.authority_delegation SET status='Revoked',revoked_at=$2,revocation_reason=$3 WHERE delegation_id=$1 AND status<>'Revoked'", id, at, reason);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    private static async Task<IReadOnlyList<AuthorityDelegation>> ReadAsync(NpgsqlCommand command, CancellationToken ct)
    {
        var result = new List<AuthorityDelegation>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new(reader.GetGuid(0), new PersonId(reader.GetGuid(1)), new PersonId(reader.GetGuid(2)), reader.GetFieldValue<string[]>(3),
                JsonSerializer.Deserialize<DelegationScope[]>(reader.GetString(4), JsonOptions) ?? [],
                reader.GetFieldValue<DateTimeOffset>(5), reader.GetFieldValue<DateTimeOffset>(6), Enum.Parse<DelegationStatus>(reader.GetString(7)),
                reader.GetFieldValue<DateTimeOffset>(8), reader.GetString(9), reader.IsDBNull(10) ? null : new PersonId(reader.GetGuid(10)),
                reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11), reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13), reader.IsDBNull(14) ? null : reader.GetString(14)));
        }
        return result;
    }
}
