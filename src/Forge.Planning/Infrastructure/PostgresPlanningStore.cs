using System.Text.Json;
using Forge.Kernel.Postgres;
using Forge.Kernel.Serialization;
using Forge.Planning.Engines;
using Npgsql;

namespace Forge.Planning.Infrastructure;

internal sealed class PostgresPlanningStore(PostgresUnitOfWork uow) : IPlanningStore
{
    private const string VersionColumns =
        "version_id, status, horizon_from, horizon_to, engine_version, input_fingerprint, output_fingerprint, summary, proposed_by, proposed_at, approved_by, approved_at";

    private const string ConstraintColumns =
        """
        constraint_id, finding_key, category, subject, status, finding, evidence, affected_orders, affected_operations, alternatives,
        window_from, window_until, first_seen, last_seen, owner, resolution, resolved_by, verified_by, reopened_count, resolved_at
        """;

    public async Task<ScheduleVersion?> FindVersionAsync(long versionId, bool includeResult, CancellationToken ct) =>
        (await ReadVersions($"SELECT {VersionColumns}, {(includeResult ? "result" : "NULL")} FROM planning.schedule_version WHERE version_id = $1", ct, versionId)).SingleOrDefault();

    public async Task<ScheduleVersion?> LatestAsync(ScheduleStatus status, bool includeResult, CancellationToken ct) =>
        (await ReadVersions($"SELECT {VersionColumns}, {(includeResult ? "result" : "NULL")} FROM planning.schedule_version WHERE status = $1 ORDER BY version_id DESC LIMIT 1", ct, status.ToString())).SingleOrDefault();

    public async Task<ScheduleVersion?> ApprovedAsOfAsync(DateTimeOffset at, CancellationToken ct) =>
        (await ReadVersions($"SELECT {VersionColumns}, result FROM planning.schedule_version WHERE approved_at IS NOT NULL AND approved_at <= $1 ORDER BY approved_at DESC, version_id DESC LIMIT 1", ct, at)).SingleOrDefault();

    public async Task<IReadOnlyList<ScheduleVersion>> ListApprovedBetweenAsync(DateTimeOffset after, DateTimeOffset upTo, CancellationToken ct) =>
        await ReadVersions($"SELECT {VersionColumns}, NULL FROM planning.schedule_version WHERE approved_at IS NOT NULL AND approved_at > $1 AND approved_at <= $2 ORDER BY approved_at, version_id", ct, after, upTo);

    public async Task<long> InsertProposalAsync(ScheduleVersion version, string resultJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(version);
        await using var cmd = uow.Command(
            """
            INSERT INTO planning.schedule_version (status, horizon_from, horizon_to, engine_version, input_fingerprint, output_fingerprint, result, summary, proposed_by, proposed_at)
            VALUES ('Proposed', $1, $2, $3, $4, $5, $6, $7, $8, $9) RETURNING version_id
            """,
            version.From, version.To, version.EngineVersion, version.InputFingerprint, version.OutputFingerprint, resultJson, version.Summary, version.ProposedBy, version.ProposedAt);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task SetStatusAsync(long versionId, ScheduleStatus status, string? approvedBy, DateTimeOffset? approvedAt, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE planning.schedule_version SET status = $2, approved_by = COALESCE($3, approved_by), approved_at = COALESCE($4, approved_at) WHERE version_id = $1",
            versionId, status.ToString(), Text(approvedBy), Time(approvedAt));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task<IReadOnlyList<ConstraintRecord>> ListConstraintsAsync(bool includeClosed, CancellationToken ct) =>
        ReadConstraints(
            $"SELECT {ConstraintColumns} FROM planning.constraint_finding {(includeClosed ? string.Empty : "WHERE status IN ('Open', 'Investigating')")} ORDER BY window_from, constraint_id",
            ct);

    public async Task<ConstraintRecord?> FindConstraintAsync(long constraintId, CancellationToken ct) =>
        (await ReadConstraints($"SELECT {ConstraintColumns} FROM planning.constraint_finding WHERE constraint_id = $1", ct, constraintId)).SingleOrDefault();

    public async Task UpsertFindingAsync(ConstraintFinding finding, long versionId, DateTimeOffset at, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(finding);
        await using var cmd = uow.Command(
            """
            INSERT INTO planning.constraint_finding (finding_key, category, subject, status, finding, evidence, affected_orders, affected_operations, alternatives,
                window_from, window_until, first_seen, last_seen, last_version_id)
            VALUES ($1, $2, $3, 'Open', $4, $5, $6, $7, $8, $9, $10, $11, $11, $12)
            ON CONFLICT (finding_key) DO UPDATE SET
                finding = EXCLUDED.finding, evidence = EXCLUDED.evidence, affected_orders = EXCLUDED.affected_orders,
                affected_operations = EXCLUDED.affected_operations, alternatives = EXCLUDED.alternatives,
                window_from = EXCLUDED.window_from, window_until = EXCLUDED.window_until, last_seen = EXCLUDED.last_seen,
                last_version_id = EXCLUDED.last_version_id,
                status = CASE WHEN planning.constraint_finding.status IN ('Resolved', 'Verified') THEN 'Open' ELSE planning.constraint_finding.status END,
                reopened_count = planning.constraint_finding.reopened_count + CASE WHEN planning.constraint_finding.status IN ('Resolved', 'Verified') THEN 1 ELSE 0 END,
                resolution = CASE WHEN planning.constraint_finding.status IN ('Resolved', 'Verified') THEN NULL ELSE planning.constraint_finding.resolution END,
                resolved_by = CASE WHEN planning.constraint_finding.status IN ('Resolved', 'Verified') THEN NULL ELSE planning.constraint_finding.resolved_by END,
                resolved_at = CASE WHEN planning.constraint_finding.status IN ('Resolved', 'Verified') THEN NULL ELSE planning.constraint_finding.resolved_at END,
                verified_by = CASE WHEN planning.constraint_finding.status IN ('Resolved', 'Verified') THEN NULL ELSE planning.constraint_finding.verified_by END
            """,
            finding.Key, finding.Category, finding.Subject, finding.Finding, finding.Evidence,
            finding.AffectedOrders.ToArray(), TextArray(finding.AffectedOperations), TextArray(finding.Alternatives),
            finding.From, finding.Until, at, versionId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> ResolveMissingAsync(IReadOnlyCollection<string> presentKeys, long versionId, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            """
            UPDATE planning.constraint_finding
            SET status = 'Resolved', resolution = 'No longer found by schedule version ' || $2::text, resolved_by = 'system:scheduler', resolved_at = $3
            WHERE status IN ('Open', 'Investigating') AND NOT (finding_key = ANY ($1))
            """,
            TextArray(presentKeys), versionId, at);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateLifecycleAsync(long constraintId, ConstraintStatus status, string? owner, string? resolution, string? resolvedBy, DateTimeOffset? resolvedAt, string? verifiedBy, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE planning.constraint_finding SET status = $2, owner = $3, resolution = $4, resolved_by = $5, resolved_at = COALESCE($6, resolved_at), verified_by = $7 WHERE constraint_id = $1",
            constraintId, status.ToString(), Text(owner), Text(resolution), Text(resolvedBy), Time(resolvedAt), Text(verifiedBy));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<IReadOnlyList<ScheduleVersion>> ReadVersions(string sql, CancellationToken ct, params object?[] args)
    {
        var list = new List<ScheduleVersion>();
        await using var cmd = uow.Command(sql, args);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new ScheduleVersion(
                r.GetInt64(0),
                Enum.Parse<ScheduleStatus>(r.GetString(1)),
                r.GetFieldValue<DateTimeOffset>(2),
                r.GetFieldValue<DateTimeOffset>(3),
                r.GetString(4),
                r.GetString(5),
                r.GetString(6),
                r.GetString(7),
                r.GetString(8),
                r.GetFieldValue<DateTimeOffset>(9),
                r.IsDBNull(10) ? null : r.GetString(10),
                r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11),
                r.IsDBNull(12) ? null : JsonSerializer.Deserialize<ScheduleResult>(r.GetString(12), CanonicalJson.Options)));
        }

        return list;
    }

    private async Task<IReadOnlyList<ConstraintRecord>> ReadConstraints(string sql, CancellationToken ct, params object?[] args)
    {
        var list = new List<ConstraintRecord>();
        await using var cmd = uow.Command(sql, args);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new ConstraintRecord(
                r.GetInt64(0),
                r.GetString(1),
                r.GetString(2),
                r.GetString(3),
                Enum.Parse<ConstraintStatus>(r.GetString(4)),
                r.GetString(5),
                r.GetString(6),
                r.GetFieldValue<long[]>(7),
                r.GetFieldValue<string[]>(8),
                r.GetFieldValue<string[]>(9),
                r.GetFieldValue<DateTimeOffset>(10),
                r.GetFieldValue<DateTimeOffset>(11),
                r.GetFieldValue<DateTimeOffset>(12),
                r.GetFieldValue<DateTimeOffset>(13),
                r.IsDBNull(14) ? null : r.GetString(14),
                r.IsDBNull(15) ? null : r.GetString(15),
                r.IsDBNull(16) ? null : r.GetString(16),
                r.IsDBNull(17) ? null : r.GetString(17),
                r.GetInt32(18),
                r.IsDBNull(19) ? null : r.GetFieldValue<DateTimeOffset>(19)));
        }

        return list;
    }

    private static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text };

    private static NpgsqlParameter Time(DateTimeOffset? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz };

    private static NpgsqlParameter TextArray(IEnumerable<string> values) =>
        new() { Value = values.ToArray(), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text };
}
