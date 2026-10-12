using Forge.Kernel.Identity;
using Forge.Kernel.WorkAssignments;
using Npgsql;

namespace Forge.Kernel.Postgres;

internal sealed class PostgresWorkAssignmentStore(PostgresUnitOfWork uow) : IWorkAssignmentStore
{
    private const string Columns = "assignment_id,work_type,work_id,title,deep_link,status,assignee_person_id,assigned_by,assigned_at,updated_at,reason";
    public Task<IReadOnlyList<WorkAssignmentRecord>> ListAsync(PersonId assignee, CancellationToken ct) => ReadAsync($"SELECT {Columns} FROM kernel.work_assignment WHERE assignee_person_id=$1 ORDER BY updated_at DESC", ct, assignee.Value);
    public async Task<WorkAssignmentRecord?> FindAsync(Guid id, bool lockRow, CancellationToken ct) =>
        (await ReadAsync($"SELECT {Columns} FROM kernel.work_assignment WHERE assignment_id=$1" + (lockRow ? " FOR UPDATE" : string.Empty), ct, id)).SingleOrDefault();
    public async Task<WorkAssignmentRecord?> FindAsync(string type, string id, PersonId assignee, CancellationToken ct) =>
        (await ReadAsync($"SELECT {Columns} FROM kernel.work_assignment WHERE work_type=$1 AND work_id=$2 AND assignee_person_id=$3", ct, type, id, assignee.Value)).SingleOrDefault();
    public async Task<bool> InsertAsync(WorkAssignmentRecord record, CancellationToken ct)
    {
        await using var command = uow.Command("INSERT INTO kernel.work_assignment(assignment_id,work_type,work_id,title,deep_link,assignee_person_id,assigned_by,assigned_at,updated_at,reason) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$8,$9) ON CONFLICT(work_type,work_id,assignee_person_id) DO NOTHING",
            record.AssignmentId, record.WorkType, record.WorkId, record.Title, record.DeepLink, record.Assignee.Value, record.AssignedBy, record.AssignedAt, record.Reason);
        if (await command.ExecuteNonQueryAsync(ct) != 1) return false;
        await HistoryAsync(record.AssignmentId, null, "Assigned", record.AssignedAt, record.AssignedBy, record.Reason, ct);
        return true;
    }
    public async Task SetStatusAsync(Guid id, string oldStatus, string status, DateTimeOffset at, string actor, string reason, CancellationToken ct)
    {
        await using var command = uow.Command("UPDATE kernel.work_assignment SET status=$2,updated_at=$3,completed_at=CASE WHEN $2='Completed' THEN $3 ELSE NULL END WHERE assignment_id=$1", id, status, at);
        await command.ExecuteNonQueryAsync(ct);
        await HistoryAsync(id, oldStatus, status, at, actor, reason, ct);
    }
    private async Task HistoryAsync(Guid id, string? oldStatus, string status, DateTimeOffset at, string actor, string reason, CancellationToken ct)
    {
        await using var command = uow.Command("INSERT INTO kernel.work_assignment_history(assignment_id,old_status,new_status,changed_by,reason,changed_at) VALUES($1,$2,$3,$4,$5,$6)", id,
            new NpgsqlParameter { Value = (object?)oldStatus ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text }, status, actor, reason, at);
        await command.ExecuteNonQueryAsync(ct);
    }
    private async Task<IReadOnlyList<WorkAssignmentRecord>> ReadAsync(string sql, CancellationToken ct, params object[] args)
    {
        await using var command = uow.Command(sql, args);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<WorkAssignmentRecord>();
        while (await reader.ReadAsync(ct)) rows.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), new PersonId(reader.GetGuid(6)), reader.GetString(7), reader.GetFieldValue<DateTimeOffset>(8), reader.GetFieldValue<DateTimeOffset>(9), reader.GetString(10)));
        return rows;
    }
}
