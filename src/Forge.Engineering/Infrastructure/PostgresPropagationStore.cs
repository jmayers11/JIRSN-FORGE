using Forge.Engineering.Contracts;
using Forge.Kernel.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Engineering.Infrastructure;

internal sealed class PostgresPropagationStore(PostgresUnitOfWork uow) : IPropagationStore, IPropagationLookup
{
    private const string PropagationSql =
        "SELECT change_number, status, correlation_id, runs, started_at, last_run_at, completed_at, last_error FROM engineering.change_propagation";

    private const string StepSql =
        "SELECT change_number, step_key, kind, target, part_number, detail, status, attempts, error, planned_at, done_at FROM engineering.propagation_step";

    private const string ActionSql =
        """
        SELECT action_id, change_number, kind, target, part_number, document_number, revision, person_id, program_id, detail, status, resolution, notes,
               raised_by, raised_at, resolved_by, resolved_at
        FROM engineering.change_action
        """;

    public async Task StartAsync(string changeNumber, Guid correlationId, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "INSERT INTO engineering.change_propagation (change_number, status, correlation_id, runs, started_at) VALUES ($1, 'Running', $2, 0, $3) ON CONFLICT DO NOTHING",
            changeNumber, correlationId, at);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task PlanStepAsync(PropagationStepInfo step, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(step);
        await using var cmd = uow.Command(
            """
            INSERT INTO engineering.propagation_step (change_number, step_key, kind, target, part_number, detail, status, planned_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8) ON CONFLICT DO NOTHING
            """,
            step.ChangeNumber, step.StepKey, step.Kind.ToString(), step.Target, Text(step.PartNumber), step.Detail, step.Status.ToString(), step.PlannedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetStepAsync(string changeNumber, string stepKey, PropagationStepStatus status, string detail, string? error, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            """
            UPDATE engineering.propagation_step SET status = $3, detail = $4, error = $5, attempts = attempts + 1, done_at = CASE WHEN $3 = 'Done' THEN $6 ELSE NULL END
            WHERE change_number = $1 AND step_key = $2 AND status <> 'Done'
            """,
            changeNumber, stepKey, status.ToString(), detail, Text(error), at);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task FinishRunAsync(string changeNumber, PropagationStatus status, string? error, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            """
            UPDATE engineering.change_propagation SET status = $2, last_error = $3, runs = runs + 1, last_run_at = $4, completed_at = CASE WHEN $2 = 'Complete' THEN $4 ELSE NULL END
            WHERE change_number = $1
            """,
            changeNumber, status.ToString(), Text(error), at);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long?> InsertActionAsync(ChangeActionInfo a, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(a);
        await using var cmd = uow.Command(
            """
            INSERT INTO engineering.change_action (change_number, kind, target, part_number, document_number, revision, person_id, program_id, detail, status, raised_by, raised_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, 'Open', $10, $11) ON CONFLICT (change_number, kind, target) DO NOTHING
            RETURNING action_id
            """,
            a.ChangeNumber, a.Kind.ToString(), a.Target, Text(a.PartNumber), Text(a.DocumentNumber), Text(a.Revision), Uuid(a.PersonId), Uuid(a.ProgramId), a.Detail, a.RaisedBy, a.RaisedAt);
        return await cmd.ExecuteScalarAsync(ct) is long id ? id : null;
    }

    public async Task ResolveActionAsync(long actionId, string resolution, string? notes, string actor, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE engineering.change_action SET status = 'Resolved', resolution = $2, notes = $3, resolved_by = $4, resolved_at = $5 WHERE action_id = $1 AND status = 'Open'",
            actionId, resolution, Text(notes), actor, at);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<ChangePropagationInfo?> FindPropagationAsync(string changeNumber, CancellationToken ct)
    {
        var propagation = (await Read(PropagationSql + " WHERE change_number = $1", ReadPropagation, ct, changeNumber)).SingleOrDefault();
        return propagation is null ? null : propagation with { Steps = await Read(StepSql + " WHERE change_number = $1 ORDER BY kind, step_key", ReadStep, ct, changeNumber) };
    }

    public async Task<IReadOnlyList<ChangePropagationInfo>> ListIncompleteAsync(CancellationToken ct)
    {
        var list = new List<ChangePropagationInfo>();
        foreach (var p in await Read(PropagationSql + " WHERE status <> 'Complete' ORDER BY started_at", ReadPropagation, ct))
        {
            list.Add(p with { Steps = await Read(StepSql + " WHERE change_number = $1 ORDER BY kind, step_key", ReadStep, ct, p.ChangeNumber) });
        }

        return list;
    }

    public Task<IReadOnlyList<ChangeActionInfo>> ListActionsAsync(string? changeNumber, bool openOnly, CancellationToken ct) =>
        Read(ActionSql + " WHERE ($1::text IS NULL OR change_number = $1) AND (NOT $2 OR status = 'Open') ORDER BY action_id", ReadAction, ct, Text(changeNumber), openOnly);

    public async Task<ChangeActionInfo?> FindActionAsync(long actionId, CancellationToken ct) =>
        (await Read(ActionSql + " WHERE action_id = $1", ReadAction, ct, actionId)).SingleOrDefault();

    private static ChangePropagationInfo ReadPropagation(NpgsqlDataReader r) => new(
        r.GetString(0), Enum.Parse<PropagationStatus>(r.GetString(1)), r.GetGuid(2), r.GetInt32(3), r.GetFieldValue<DateTimeOffset>(4), OptTime(r, 5), OptTime(r, 6), Opt(r, 7), []);

    private static PropagationStepInfo ReadStep(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetString(1), Enum.Parse<PropagationStepKind>(r.GetString(2)), r.GetString(3), Opt(r, 4), r.GetString(5), Enum.Parse<PropagationStepStatus>(r.GetString(6)),
        r.GetInt32(7), Opt(r, 8), r.GetFieldValue<DateTimeOffset>(9), OptTime(r, 10));

    private static ChangeActionInfo ReadAction(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetString(1), Enum.Parse<ChangeActionKind>(r.GetString(2)), r.GetString(3), Opt(r, 4), Opt(r, 5), Opt(r, 6), r.IsDBNull(7) ? null : r.GetGuid(7),
        r.IsDBNull(8) ? null : r.GetGuid(8), r.GetString(9), Enum.Parse<ChangeActionStatus>(r.GetString(10)), Opt(r, 11), Opt(r, 12), r.GetString(13), r.GetFieldValue<DateTimeOffset>(14),
        Opt(r, 15), OptTime(r, 16));

    private static string? Opt(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static DateTimeOffset? OptTime(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);

    private static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };

    private static NpgsqlParameter Uuid(Guid? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid };

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
