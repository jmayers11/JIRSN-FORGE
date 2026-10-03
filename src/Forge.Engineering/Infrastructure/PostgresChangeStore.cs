using Forge.Engineering.Contracts;
using Forge.Kernel.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Engineering.Infrastructure;

internal sealed class PostgresChangeStore(PostgresUnitOfWork uow) : IChangeStore, IChangeLookup
{
    private const string ChangeSql =
        """
        SELECT change_number, title, change_class, change_type, priority, reason, description, program_id, customer, requested_effective, status, review_round,
               originator, created_at, submitted_at, approved_by, approved_at, released_by, released_at, effective_from, closed_by, closed_at, closed_reason
        FROM engineering.change
        """;

    private const string LineSql =
        "SELECT change_number, line_number, kind, document_number, part_number, from_revision, to_revision, note, added_by, added_at, removed_by, removed_at FROM engineering.change_line";

    private const string ReviewSql =
        "SELECT change_number, round, review_kind, required_by, reviewer, decision, notes, decided_at FROM engineering.change_review";

    public async Task<string> NextNumberAsync(CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT nextval('engineering.change_number_seq')");
        var n = (long)(await cmd.ExecuteScalarAsync(ct))!;
        return $"ECN-{n:D4}";
    }

    public async Task InsertChangeAsync(ChangeInfo c, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        await using var cmd = uow.Command(
            """
            INSERT INTO engineering.change (change_number, title, change_class, change_type, priority, reason, description, program_id, customer, requested_effective, status, originator, created_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13)
            """,
            c.Number, c.Title, c.Class.ToString(), c.Type.ToString(), c.Priority.ToString(), c.Reason, Text(c.Description), Uuid(c.ProgramId), Text(c.Customer), Date(c.RequestedEffective),
            c.Status.ToString(), c.Originator, c.CreatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateChangeAsync(ChangeInfo c, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        await using var cmd = uow.Command(
            """
            UPDATE engineering.change SET title = $2, change_class = $3, change_type = $4, priority = $5, reason = $6, description = $7, customer = $8, requested_effective = $9,
                status = $10, review_round = $11, submitted_at = $12, approved_by = $13, approved_at = $14, released_by = $15, released_at = $16, effective_from = $17,
                closed_by = $18, closed_at = $19, closed_reason = $20
            WHERE change_number = $1
            """,
            c.Number, c.Title, c.Class.ToString(), c.Type.ToString(), c.Priority.ToString(), c.Reason, Text(c.Description), Text(c.Customer), Date(c.RequestedEffective),
            c.Status.ToString(), c.ReviewRound, Time(c.SubmittedAt), Text(c.ApprovedBy), Time(c.ApprovedAt), Text(c.ReleasedBy), Time(c.ReleasedAt), Date(c.EffectiveFrom),
            Text(c.ClosedBy), Time(c.ClosedAt), Text(c.ClosedReason));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertLineAsync(ChangeLineInfo l, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(l);
        await using var cmd = uow.Command(
            """
            INSERT INTO engineering.change_line (change_number, line_number, kind, document_number, part_number, from_revision, to_revision, note, added_by, added_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
            """,
            l.ChangeNumber, l.LineNumber, l.Kind.ToString(), Text(l.DocumentNumber), Text(l.PartNumber), Text(l.FromRevision), l.ToRevision, Text(l.Note), l.AddedBy, l.AddedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RemoveLineAsync(string number, int line, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE engineering.change_line SET removed_at = $3, removed_by = $4 WHERE change_number = $1 AND line_number = $2 AND removed_at IS NULL",
            number, line, at, actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertReviewAsync(ChangeReviewInfo r, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        await using var cmd = uow.Command(
            "INSERT INTO engineering.change_review (change_number, round, review_kind, required_by) VALUES ($1, $2, $3, $4)",
            r.ChangeNumber, r.Round, r.Kind.ToString(), r.RequiredBy);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DecideReviewAsync(ChangeReviewInfo r, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        await using var cmd = uow.Command(
            "UPDATE engineering.change_review SET reviewer = $4, decision = $5, notes = $6, decided_at = $7 WHERE change_number = $1 AND round = $2 AND review_kind = $3",
            r.ChangeNumber, r.Round, r.Kind.ToString(), Text(r.Reviewer), Text(r.Decision?.ToString()), Text(r.Notes), Time(r.DecidedAt));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> InsertPolicyAsync(ReviewPolicyInfo p, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(p);
        await using var cmd = uow.Command(
            """
            INSERT INTO engineering.review_policy (name, change_class, change_type, item_class, program_id, customer, required_reviews, combine_approval_release, created_by, created_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10) RETURNING policy_id
            """,
            p.Name, Text(p.ChangeClass?.ToString()), Text(p.ChangeType?.ToString()), Text(p.ItemClass?.ToString()), Uuid(p.ProgramId), Text(p.Customer),
            new NpgsqlParameter { Value = p.RequiredReviews.Select(k => k.ToString()).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text },
            p.CombineApprovalRelease, p.CreatedBy, p.CreatedAt);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task WithdrawPolicyAsync(long policyId, DateTimeOffset at, string actor, string reason, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE engineering.review_policy SET withdrawn_at = $2, withdrawn_by = $3, withdrawn_reason = $4 WHERE policy_id = $1 AND withdrawn_at IS NULL",
            policyId, at, actor, reason);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<ChangeInfo>> ListChangesAsync(CancellationToken ct)
    {
        var changes = await Read(ChangeSql + " ORDER BY created_at DESC, change_number DESC", ReadChange, ct);
        var lines = (await Read(LineSql + " ORDER BY change_number, line_number", ReadLine, ct)).ToLookup(l => l.ChangeNumber);
        var reviews = (await Read(ReviewSql + " ORDER BY change_number, round, review_kind", ReadReview, ct)).ToLookup(r => r.ChangeNumber);
        return changes.Select(c => c with { Lines = lines[c.Number].ToList(), Reviews = reviews[c.Number].ToList() }).ToList();
    }

    public async Task<ChangeInfo?> FindChangeAsync(string number, CancellationToken ct)
    {
        var change = (await Read(ChangeSql + " WHERE change_number = $1", ReadChange, ct, number)).SingleOrDefault();
        return change is null
            ? null
            : change with
            {
                Lines = await Read(LineSql + " WHERE change_number = $1 ORDER BY line_number", ReadLine, ct, number),
                Reviews = await Read(ReviewSql + " WHERE change_number = $1 ORDER BY round, review_kind", ReadReview, ct, number),
            };
    }

    public Task<IReadOnlyList<ReviewPolicyInfo>> ListReviewPoliciesAsync(bool includeWithdrawn, CancellationToken ct) =>
        Read(
            """
            SELECT policy_id, name, change_class, change_type, item_class, program_id, customer, required_reviews, combine_approval_release, created_by, created_at, withdrawn_by, withdrawn_at, withdrawn_reason
            FROM engineering.review_policy WHERE ($1 OR withdrawn_at IS NULL) ORDER BY policy_id
            """,
            r => new ReviewPolicyInfo(
                r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : Enum.Parse<ChangeClass>(r.GetString(2)), r.IsDBNull(3) ? null : Enum.Parse<ChangeType>(r.GetString(3)),
                r.IsDBNull(4) ? null : Enum.Parse<ItemClass>(r.GetString(4)), r.IsDBNull(5) ? null : r.GetGuid(5), Opt(r, 6),
                r.GetFieldValue<string[]>(7).Select(k => Enum.Parse<ReviewKind>(k)).ToList(), r.GetBoolean(8), r.GetString(9), r.GetFieldValue<DateTimeOffset>(10), Opt(r, 11), OptTime(r, 12), Opt(r, 13)),
            ct,
            includeWithdrawn);

    private static ChangeInfo ReadChange(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetString(1), Enum.Parse<ChangeClass>(r.GetString(2)), Enum.Parse<ChangeType>(r.GetString(3)), Enum.Parse<ChangePriority>(r.GetString(4)), r.GetString(5), Opt(r, 6),
        r.IsDBNull(7) ? null : r.GetGuid(7), Opt(r, 8), r.IsDBNull(9) ? null : r.GetFieldValue<DateOnly>(9), Enum.Parse<ChangeStatus>(r.GetString(10)), r.GetInt32(11),
        r.GetString(12), r.GetFieldValue<DateTimeOffset>(13), OptTime(r, 14), Opt(r, 15), OptTime(r, 16), Opt(r, 17), OptTime(r, 18),
        r.IsDBNull(19) ? null : r.GetFieldValue<DateOnly>(19), Opt(r, 20), OptTime(r, 21), Opt(r, 22), [], []);

    private static ChangeLineInfo ReadLine(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetInt32(1), Enum.Parse<ChangeLineKind>(r.GetString(2)), Opt(r, 3), Opt(r, 4), Opt(r, 5), r.GetString(6), Opt(r, 7), r.GetString(8), r.GetFieldValue<DateTimeOffset>(9), Opt(r, 10), OptTime(r, 11));

    private static ChangeReviewInfo ReadReview(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetInt32(1), Enum.Parse<ReviewKind>(r.GetString(2)), r.GetString(3), Opt(r, 4), r.IsDBNull(5) ? null : Enum.Parse<ReviewDecision>(r.GetString(5)), Opt(r, 6), OptTime(r, 7));

    private static string? Opt(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static DateTimeOffset? OptTime(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);

    private static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };

    private static NpgsqlParameter Uuid(Guid? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid };

    private static NpgsqlParameter Date(DateOnly? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date };

    private static NpgsqlParameter Time(DateTimeOffset? value) =>
        new() { Value = (object?)value?.ToUniversalTime() ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz };

    private async Task<IReadOnlyList<T>> Read<T>(string sql, Func<NpgsqlDataReader, T> map, CancellationToken ct, params object?[] args)
    {
        var list = new List<T>();
        await using var cmd = uow.Command(sql, args);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(map(reader));
        }

        return list;
    }
}
