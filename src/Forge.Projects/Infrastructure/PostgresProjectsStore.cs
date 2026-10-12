using Forge.Kernel.Postgres;
using Forge.Projects.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Projects.Infrastructure;

internal sealed class PostgresProjectsStore(PostgresUnitOfWork uow) : IProjectsStore, IProjectLookup
{
    private const string ProjectSql =
        "SELECT code, title, type, funding, description, owner, budget, start_date, end_date, financial_reference, status, created_by, created_at, approved_by, approved_at, closed_by, closed_at, close_reason FROM projects.project";

    private const string LineSql = "SELECT project_code, line_number, part_number, quantity, need_date, description, status FROM projects.line";

    private const string TaskSql = "SELECT task_id, project_code, title, due_date, milestone, completed_on, completed_by, created_by, created_at FROM projects.task";

    private const string ChangeSql =
        """
        SELECT change_id, project_code, kind, line_number, part_number, quantity, need_date, line_description, title, description, budget, start_date, end_date,
               financial_reference, reason, status, requested_by, requested_at, decided_by, decided_at, decision_reason
        FROM projects.change
        """;

    public async Task<bool> InsertProjectAsync(ProjectInfo project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        await using var cmd = uow.Command(
            """
            INSERT INTO projects.project (code, title, type, funding, description, owner, budget, start_date, end_date, financial_reference, status, created_by, created_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13) ON CONFLICT DO NOTHING
            """,
            project.Code, project.Title, project.Type.ToString(), project.Funding.ToString(), Text(project.Description), Text(project.Owner), Num(project.Budget),
            project.StartDate, project.EndDate, Text(project.FinancialReference), project.Status.ToString(), project.CreatedBy, project.CreatedAt);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task UpdateProjectAsync(ProjectInfo project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        await using var cmd = uow.Command(
            """
            UPDATE projects.project SET title = $2, description = $3, owner = $4, budget = $5, start_date = $6, end_date = $7, financial_reference = $8, status = $9,
                approved_by = $10, approved_at = $11, closed_by = $12, closed_at = $13, close_reason = $14
            WHERE code = $1
            """,
            project.Code, project.Title, Text(project.Description), Text(project.Owner), Num(project.Budget), project.StartDate, project.EndDate, Text(project.FinancialReference),
            project.Status.ToString(), Text(project.ApprovedBy), Time(project.ApprovedAt), Text(project.ClosedBy), Time(project.ClosedAt), Text(project.CloseReason));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task LockProjectAsync(string code, CancellationToken ct)
    {
        await using var command = uow.Command("SELECT code FROM projects.project WHERE code=$1 FOR UPDATE", code);
        await command.ExecuteScalarAsync(ct);
    }

    public async Task InsertLineAsync(ProjectLine line, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(line);
        await using var cmd = uow.Command(
            "INSERT INTO projects.line (project_code, line_number, part_number, quantity, need_date, description, status) VALUES ($1, $2, $3, $4, $5, $6, $7)",
            line.ProjectCode, line.LineNumber, line.PartNumber, line.Quantity, line.NeedDate, Text(line.Description), line.Status.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateLineAsync(ProjectLine line, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(line);
        await using var cmd = uow.Command(
            "UPDATE projects.line SET quantity = $3, need_date = $4, description = $5, status = $6 WHERE project_code = $1 AND line_number = $2",
            line.ProjectCode, line.LineNumber, line.Quantity, line.NeedDate, Text(line.Description), line.Status.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> InsertTaskAsync(ProjectTask task, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);
        await using var cmd = uow.Command(
            "INSERT INTO projects.task (project_code, title, due_date, milestone, created_by, created_at) VALUES ($1, $2, $3, $4, $5, $6) RETURNING task_id",
            task.ProjectCode, task.Title, task.DueDate, task.Milestone, task.CreatedBy, task.CreatedAt);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<ProjectTask?> FindTaskAsync(long taskId, CancellationToken ct) =>
        (await ReadAsync(TaskSql + " WHERE task_id = $1", ReadTask, ct, taskId)).SingleOrDefault();

    public async Task UpdateTaskAsync(ProjectTask task, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);
        await using var cmd = uow.Command(
            "UPDATE projects.task SET completed_on = $2, completed_by = $3 WHERE task_id = $1",
            task.TaskId, new NpgsqlParameter { Value = (object?)task.CompletedOn ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date }, Text(task.CompletedBy));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> InsertChangeAsync(ProjectChange change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        await using var cmd = uow.Command(
            """
            INSERT INTO projects.change (project_code, kind, line_number, part_number, quantity, need_date, line_description, title, description, budget, start_date, end_date,
                financial_reference, reason, status, requested_by, requested_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, 'Pending', $15, $16) RETURNING change_id
            """,
            change.ProjectCode, change.Kind.ToString(), Int(change.LineNumber), Text(change.PartNumber), Num(change.Quantity), Date(change.NeedDate), Text(change.LineDescription),
            Text(change.Title), Text(change.Description), Num(change.Budget), Date(change.StartDate), Date(change.EndDate), Text(change.FinancialReference), change.Reason,
            change.RequestedBy, change.RequestedAt);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<ProjectChange?> FindChangeAsync(long changeId, CancellationToken ct) =>
        (await ReadAsync(ChangeSql + " WHERE change_id = $1", ReadChange, ct, changeId)).SingleOrDefault();

    public async Task DecideChangeAsync(ProjectChange change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        await using var cmd = uow.Command(
            "UPDATE projects.change SET status = $2, decided_by = $3, decided_at = $4, decision_reason = $5 WHERE change_id = $1",
            change.ChangeId, change.Status.ToString(), Text(change.DecidedBy), Time(change.DecidedAt), Text(change.DecisionReason));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<ProjectInfo?> FindProjectAsync(string code, CancellationToken ct) =>
        (await ReadAsync(ProjectSql + " WHERE code = $1", ReadProject, ct, code)).SingleOrDefault();

    public Task<IReadOnlyList<ProjectInfo>> ListProjectsAsync(CancellationToken ct) =>
        ReadAsync(ProjectSql + " ORDER BY CASE status WHEN 'Approved' THEN 0 WHEN 'Draft' THEN 1 ELSE 2 END, start_date DESC, code", ReadProject, ct);

    public Task<IReadOnlyList<ProjectLine>> ListLinesAsync(string? projectCode, CancellationToken ct) =>
        ReadAsync(LineSql + " WHERE ($1::text IS NULL OR project_code = $1) ORDER BY project_code, line_number", ReadLine, ct, Text(projectCode));

    public Task<IReadOnlyList<ProjectTask>> ListTasksAsync(string projectCode, CancellationToken ct) =>
        ReadAsync(TaskSql + " WHERE project_code = $1 ORDER BY due_date, task_id", ReadTask, ct, projectCode);

    public Task<IReadOnlyList<ProjectChange>> ListChangesAsync(string? projectCode, bool pendingOnly, CancellationToken ct) =>
        ReadAsync(
            ChangeSql + " WHERE ($1::text IS NULL OR project_code = $1) AND (NOT $2 OR status = 'Pending') ORDER BY change_id DESC LIMIT 500",
            ReadChange,
            ct,
            Text(projectCode),
            pendingOnly);

    private static ProjectInfo ReadProject(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetString(1), Enum.Parse<ProjectType>(r.GetString(2)), Enum.Parse<ProjectFunding>(r.GetString(3)), NText(r, 4), NText(r, 5), NDec(r, 6),
        r.GetFieldValue<DateOnly>(7), r.GetFieldValue<DateOnly>(8), NText(r, 9), Enum.Parse<ProjectStatus>(r.GetString(10)), r.GetString(11), r.GetFieldValue<DateTimeOffset>(12),
        NText(r, 13), NTime(r, 14), NText(r, 15), NTime(r, 16), NText(r, 17));

    private static ProjectLine ReadLine(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetInt32(1), r.GetString(2), Dec(r, 3), r.GetFieldValue<DateOnly>(4), NText(r, 5), Enum.Parse<ProjectLineStatus>(r.GetString(6)));

    private static ProjectTask ReadTask(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetFieldValue<DateOnly>(3), r.GetBoolean(4), r.IsDBNull(5) ? null : r.GetFieldValue<DateOnly>(5), NText(r, 6), r.GetString(7),
        r.GetFieldValue<DateTimeOffset>(8));

    private static ProjectChange ReadChange(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetString(1), Enum.Parse<ProjectChangeKind>(r.GetString(2)), r.IsDBNull(3) ? null : r.GetInt32(3), NText(r, 4), NDec(r, 5),
        r.IsDBNull(6) ? null : r.GetFieldValue<DateOnly>(6), NText(r, 7), NText(r, 8), NText(r, 9), NDec(r, 10), r.IsDBNull(11) ? null : r.GetFieldValue<DateOnly>(11),
        r.IsDBNull(12) ? null : r.GetFieldValue<DateOnly>(12), NText(r, 13), r.GetString(14), Enum.Parse<ProjectChangeStatus>(r.GetString(15)), r.GetString(16),
        r.GetFieldValue<DateTimeOffset>(17), NText(r, 18), NTime(r, 19), NText(r, 20));

    private static decimal Normal(decimal value) => value / 1.000000000000000000000000000000000m;

    private static decimal Dec(NpgsqlDataReader r, int ordinal) => Normal(r.GetDecimal(ordinal));

    private static decimal? NDec(NpgsqlDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : Normal(r.GetDecimal(ordinal));

    private static string? NText(NpgsqlDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : r.GetString(ordinal);

    private static DateTimeOffset? NTime(NpgsqlDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : r.GetFieldValue<DateTimeOffset>(ordinal);

    private static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };

    private static NpgsqlParameter Num(decimal? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Numeric };

    private static NpgsqlParameter Int(int? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer };

    private static NpgsqlParameter Date(DateOnly? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date };

    private static NpgsqlParameter Time(DateTimeOffset? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz };

    private async Task<IReadOnlyList<T>> ReadAsync<T>(string sql, Func<NpgsqlDataReader, T> map, CancellationToken ct, params object?[] args)
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

public static class ProjectsRegistration
{
    public static IServiceCollection AddForgeProjects(this IServiceCollection services)
    {
        services.AddForgeMigrations(typeof(ProjectsRegistration).Assembly, "projects");
        services.AddForgeStore<IProjectsStore>(uow => new PostgresProjectsStore(uow));
        services.AddForgeStore<IProjectLookup>(uow => new PostgresProjectsStore(uow));
        services.AddSingleton<ProjectService>();
        return services;
    }
}
