using System.Text.Json;
using Forge.Execution.Contracts;
using Forge.Kernel.Postgres;
using Forge.Kernel.Serialization;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Execution.Infrastructure;

internal sealed class PostgresShiftContinuityStore(PostgresUnitOfWork uow) : IShiftContinuityStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<IReadOnlyList<ShiftChecklistItem>> ListChecklistAsync(string workCenter, CancellationToken ct) => Read(
        "SELECT code, description, required, sort_order, active FROM execution.shift_checklist_item WHERE work_center_code = $1 ORDER BY sort_order, code",
        r => new ShiftChecklistItem(r.GetString(0), r.GetString(1), r.GetBoolean(2), r.GetInt32(3), r.GetBoolean(4)), ct, workCenter);

    public async Task<bool> SaveChecklistItemAsync(string workCenter, ShiftChecklistItem item, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command("""
            INSERT INTO execution.shift_checklist_item(work_center_code, code, description, required, sort_order, active, changed_at, changed_by)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8)
            ON CONFLICT (work_center_code, code) DO UPDATE SET description=excluded.description, required=excluded.required,
              sort_order=excluded.sort_order, active=excluded.active, changed_at=excluded.changed_at, changed_by=excluded.changed_by
            """, workCenter, item.Code, item.Description, item.Required, item.SortOrder, item.Active, at, actor);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public Task<IReadOnlyList<ShiftHandoff>> ListHandoffsAsync(string? workCenter, int limit, CancellationToken ct) => Read(
        "SELECT handoff_id, work_center_code, shift_code, shift_from, shift_until, created_at, created_by, context, reviewed_at, reviewed_by, source_snapshot::text, checklist_snapshot::text FROM execution.shift_handoff WHERE ($1::text IS NULL OR work_center_code=$1) ORDER BY shift_from DESC, handoff_id DESC LIMIT $2",
        ReadHandoff, ct, (object?)workCenter ?? DBNull.Value, limit);

    public async Task<ShiftHandoff?> FindHandoffAsync(long handoffId, CancellationToken ct) =>
        (await Read("SELECT handoff_id, work_center_code, shift_code, shift_from, shift_until, created_at, created_by, context, reviewed_at, reviewed_by, source_snapshot::text, checklist_snapshot::text FROM execution.shift_handoff WHERE handoff_id=$1", ReadHandoff, ct, handoffId)).SingleOrDefault();

    public async Task<long?> InsertHandoffAsync(string workCenter, string shiftCode, DateTimeOffset from, DateTimeOffset until, DateTimeOffset at, string actor, string? context, ShiftCloseoutFacts snapshot, IReadOnlyList<ShiftChecklistEntry> checklist, CancellationToken ct)
    {
        await using var cmd = uow.Command("""
            INSERT INTO execution.shift_handoff(work_center_code, shift_code, shift_from, shift_until, created_at, created_by, context, source_snapshot, checklist_snapshot)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9)
            ON CONFLICT (work_center_code, shift_code, shift_from, shift_until) DO NOTHING RETURNING handoff_id
            """, workCenter, shiftCode, from, until, at, actor, Text(context), Jsonb(snapshot), Jsonb(checklist));
        return await cmd.ExecuteScalarAsync(ct) is long id ? id : null;
    }

    public async Task<bool> MarkReviewedAsync(long handoffId, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command("UPDATE execution.shift_handoff SET reviewed_at=$2, reviewed_by=$3 WHERE handoff_id=$1 AND reviewed_at IS NULL", handoffId, at, actor);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> InsertAcknowledgmentAsync(ShiftAcknowledgment acknowledgment, CancellationToken ct)
    {
        await using var cmd = uow.Command("INSERT INTO execution.shift_handoff_ack(handoff_id, person_id, acknowledged_at, note) VALUES ($1,$2,$3,$4) ON CONFLICT DO NOTHING",
            acknowledgment.HandoffId, acknowledgment.PersonId, acknowledgment.AcknowledgedAt, Text(acknowledgment.Note));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public Task<IReadOnlyList<ShiftAcknowledgment>> ListAcknowledgmentsAsync(long handoffId, CancellationToken ct) => Read(
        "SELECT handoff_id, person_id, acknowledged_at, note FROM execution.shift_handoff_ack WHERE handoff_id=$1 ORDER BY acknowledged_at, person_id",
        r => new ShiftAcknowledgment(r.GetInt64(0), r.GetGuid(1), r.GetFieldValue<DateTimeOffset>(2), S(r, 3)), ct, handoffId);

    public async Task<long> InsertActionAsync(long handoffId, string description, string? sourceKind, string? sourceReference, Guid assignee, Guid? programId, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command("""
            INSERT INTO execution.shift_handoff_action(handoff_id, description, source_kind, source_reference, assignee, program_id, status, created_at, created_by)
            VALUES ($1,$2,$3,$4,$5,$6,'Assigned',$7,$8) RETURNING action_id
            """, handoffId, description, Text(sourceKind), Text(sourceReference), assignee,
            new NpgsqlParameter { Value = (object?)programId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid }, at, actor);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<ShiftAction?> FindActionAsync(long actionId, CancellationToken ct) =>
        (await Read("SELECT action_id, handoff_id, description, source_kind, source_reference, assignee, status, created_at, created_by, acknowledged_at, acknowledged_by, in_progress_at, in_progress_by, completed_at, completed_by, program_id FROM execution.shift_handoff_action WHERE action_id=$1", ReadAction, ct, actionId)).SingleOrDefault();

    public async Task<bool> UpdateActionStatusAsync(long actionId, ShiftActionStatus expected, ShiftActionStatus status, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command("""
            UPDATE execution.shift_handoff_action SET status=$3,
              acknowledged_at=CASE WHEN $3='Acknowledged' AND acknowledged_at IS NULL THEN $4 ELSE acknowledged_at END,
              acknowledged_by=CASE WHEN $3='Acknowledged' AND acknowledged_at IS NULL THEN $5 ELSE acknowledged_by END,
              in_progress_at=CASE WHEN $3='InProgress' THEN $4 ELSE in_progress_at END,
              in_progress_by=CASE WHEN $3='InProgress' THEN $5 ELSE in_progress_by END,
              completed_at=CASE WHEN $3='Completed' THEN $4 ELSE completed_at END,
              completed_by=CASE WHEN $3='Completed' THEN $5 ELSE completed_by END
            WHERE action_id=$1 AND status=$2
            """, actionId, expected.ToString(), status.ToString(), at, actor);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public Task<IReadOnlyList<ShiftAction>> ListActionsAsync(long handoffId, CancellationToken ct) => Read(
        "SELECT action_id, handoff_id, description, source_kind, source_reference, assignee, status, created_at, created_by, acknowledged_at, acknowledged_by, in_progress_at, in_progress_by, completed_at, completed_by, program_id FROM execution.shift_handoff_action WHERE handoff_id=$1 ORDER BY action_id",
        ReadAction, ct, handoffId);

    public async Task<long?> InsertCloseoutAsync(string workCenter, string shiftCode, DateTimeOffset from, DateTimeOffset until, DateTimeOffset at, string actor, ShiftCloseoutFacts facts, string? note, CancellationToken ct)
    {
        await using var cmd = uow.Command("""
            INSERT INTO execution.shift_closeout(work_center_code, shift_code, shift_from, shift_until, closed_at, closed_by, facts, note)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8) ON CONFLICT (work_center_code, shift_code, shift_from, shift_until) DO NOTHING RETURNING closeout_id
            """, workCenter, shiftCode, from, until, at, actor, Jsonb(facts), Text(note));
        return await cmd.ExecuteScalarAsync(ct) is long id ? id : null;
    }

    public Task<IReadOnlyList<ShiftCloseout>> ListCloseoutsAsync(string? workCenter, int limit, CancellationToken ct) => Read(
        "SELECT closeout_id, work_center_code, shift_code, shift_from, shift_until, closed_at, closed_by, facts::text, note FROM execution.shift_closeout WHERE ($1::text IS NULL OR work_center_code=$1) ORDER BY shift_from DESC, closeout_id DESC LIMIT $2",
        r => new ShiftCloseout(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3), r.GetFieldValue<DateTimeOffset>(4), r.GetFieldValue<DateTimeOffset>(5), r.GetString(6), Parse<ShiftCloseoutFacts>(r.GetString(7)), S(r, 8)), ct,
        (object?)workCenter ?? DBNull.Value, limit);

    private async Task<IReadOnlyList<T>> Read<T>(string sql, Func<NpgsqlDataReader, T> map, CancellationToken ct, params object?[] args)
    {
        var rows = new List<T>();
        await using var cmd = uow.Command(sql, args);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(map(reader));
        return rows;
    }

    private static ShiftHandoff ReadHandoff(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3), r.GetFieldValue<DateTimeOffset>(4),
        r.GetFieldValue<DateTimeOffset>(5), r.GetString(6), S(r, 7), N(r, 8), S(r, 9), Parse<ShiftCloseoutFacts>(r.GetString(10)),
        Parse<IReadOnlyList<ShiftChecklistEntry>>(r.GetString(11)), Parse<ShiftCloseoutFacts>(r.GetString(10)).Facts);

    private static ShiftAction ReadAction(NpgsqlDataReader r) => new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), S(r, 3), S(r, 4), r.GetGuid(5),
        Enum.Parse<ShiftActionStatus>(r.GetString(6)), r.GetFieldValue<DateTimeOffset>(7), r.GetString(8), N(r, 9), S(r, 10), N(r, 11), S(r, 12), N(r, 13), S(r, 14), r.IsDBNull(15) ? null : r.GetGuid(15));
    private static string? S(NpgsqlDataReader r, int index) => r.IsDBNull(index) ? null : r.GetString(index);
    private static DateTimeOffset? N(NpgsqlDataReader r, int index) => r.IsDBNull(index) ? null : r.GetFieldValue<DateTimeOffset>(index);
    private static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };
    private static NpgsqlParameter Jsonb<T>(T value) => new() { Value = CanonicalJson.Serialize(value), NpgsqlDbType = NpgsqlDbType.Jsonb };
    private static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, Json) ?? throw new InvalidOperationException("Stored shift record is invalid.");
}
