using Forge.Kernel.Holds;
using Forge.Kernel.Serialization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Kernel.Postgres;

/// <summary>Generalized holds and what each kind blocks (V3.10).</summary>
internal sealed class PostgresHoldStore(PostgresUnitOfWork uow) : IHoldLookup, IHoldStore
{
    public async Task<IReadOnlyList<Hold>> ListHoldsAsync(bool includeReleased, CancellationToken ct)
    {
        var list = new List<Hold>();
        await using var cmd = uow.Command(
            """
            SELECT hold_id, kind, target_type, target_id, target_part, source, reason, evidence, affected_records, required_resolution,
                   placed_at, placed_by, released_at, released_by, release_reason
            FROM kernel.hold WHERE $1 OR released_at IS NULL ORDER BY hold_id DESC
            """,
            includeReleased);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new Hold(
                r.GetInt64(0), Enum.Parse<HoldKind>(r.GetString(1)), Enum.Parse<HoldTargetType>(r.GetString(2)), r.GetString(3), S(r, 4), r.GetString(5), r.GetString(6), S(r, 7),
                r.GetFieldValue<string[]>(8), r.GetString(9), r.GetFieldValue<DateTimeOffset>(10), r.GetString(11),
                r.IsDBNull(12) ? null : r.GetFieldValue<DateTimeOffset>(12), S(r, 13), S(r, 14)));
        }

        return list;
    }

    public async Task<IReadOnlyList<HoldKindPolicy>> ListKindPoliciesAsync(CancellationToken ct)
    {
        var list = new List<HoldKindPolicy>();
        await using var cmd = uow.Command("SELECT kind, blocks, release_permission, changed_at, changed_by, require_fresh_assurance, require_signature, signature_meaning, propagate_downstream FROM kernel.hold_kind ORDER BY kind");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new HoldKindPolicy(
                Enum.Parse<HoldKind>(r.GetString(0)), r.GetFieldValue<string[]>(1).Select(x => Enum.Parse<HoldActivity>(x)).ToList(), r.GetString(2),
                r.GetFieldValue<DateTimeOffset>(3), r.GetString(4), r.GetBoolean(5), r.GetBoolean(6), r.GetString(7), r.GetBoolean(8)));
        }

        return list;
    }

    public async Task<long> InsertHoldAsync(Hold hold, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(hold);
        await using var cmd = uow.Command(
            """
            INSERT INTO kernel.hold (kind, target_type, target_id, target_part, source, reason, evidence, affected_records, required_resolution, placed_at, placed_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11) RETURNING hold_id
            """,
            hold.Kind.ToString(), hold.TargetType.ToString(), hold.TargetId, Text(hold.TargetPart), hold.Source, hold.Reason, Text(hold.Evidence),
            new NpgsqlParameter { Value = hold.AffectedRecords.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text },
            hold.RequiredResolution, hold.PlacedAt, hold.PlacedBy);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task ReleaseHoldAsync(long holdId, DateTimeOffset at, string actor, string reason, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE kernel.hold SET released_at = $2, released_by = $3, release_reason = $4 WHERE hold_id = $1 AND released_at IS NULL",
            holdId, at, actor, reason);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetKindPolicyAsync(HoldKindPolicy policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);
        await using var cmd = uow.Command(
            "UPDATE kernel.hold_kind SET blocks = $2, changed_at = $3, changed_by = $4, require_fresh_assurance = $5, require_signature = $6, signature_meaning = $7, propagate_downstream = $8 WHERE kind = $1",
            policy.Kind.ToString(),
            new NpgsqlParameter { Value = policy.Blocks.Select(b => b.ToString()).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text },
            (object?)policy.ChangedAt ?? DBNull.Value,
            (object?)policy.ChangedBy ?? DBNull.Value, policy.RequireFreshAssurance, policy.RequireSignature, policy.SignatureMeaning, policy.PropagateDownstream);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<Hold?> LockHoldAsync(long holdId, CancellationToken ct)
    {
        await using (var cmd = uow.Command("SELECT hold_id FROM kernel.hold WHERE hold_id = $1 FOR UPDATE", holdId))
            if (await cmd.ExecuteScalarAsync(ct) is null) return null;
        return (await ListHoldsAsync(true, ct)).FirstOrDefault(h => h.HoldId == holdId);
    }

    public async Task InsertReleaseSignoffAsync(HoldReleaseSignoff signoff, CancellationToken ct)
    {
        await using var cmd = uow.Command("INSERT INTO kernel.hold_release_signoff (hold_id, person_id, signed_at, record_version, record_fingerprint, policy_fingerprint, evidence) VALUES ($1,$2,$3,$4,$5,$6,$7)",
            signoff.HoldId, signoff.PersonId, signoff.SignedAt, signoff.RecordVersion, signoff.RecordFingerprint, signoff.PolicyFingerprint, CanonicalJson.Serialize(signoff));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<HoldReleaseSignoff?> FindReleaseSignoffAsync(long holdId, CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT evidence FROM kernel.hold_release_signoff WHERE hold_id = $1", holdId);
        var json = await cmd.ExecuteScalarAsync(ct) as string;
        return json is null ? null : JsonSerializer.Deserialize<HoldReleaseSignoff>(json, CanonicalJson.Options);
    }

    private static string? S(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };
}
