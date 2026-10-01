using Forge.Execution.Contracts;
using Forge.Kernel.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Forge.Execution.Infrastructure;

internal sealed class PostgresExecutionStore(PostgresUnitOfWork uow) : IExecutionStore, IExecutionLookup, IShopFloorStore
{
    private const string OrderSql =
        "SELECT order_number, part_number, revision, quantity, due_date, priority, status, held_from, program_id, released_at, created_by, released_by FROM execution.production_order";

    public async Task<long> InsertOrderAsync(string partNumber, string revision, decimal quantity, DateOnly dueDate, int priority, Guid? programId, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            """
            INSERT INTO execution.production_order (part_number, revision, quantity, due_date, priority, status, program_id, created_at, created_by, updated_at, updated_by)
            VALUES ($1, $2, $3, $4, $5, 'Planned', $6, $7, $8, $7, $8) RETURNING order_number
            """,
            partNumber, revision, quantity, dueDate, priority,
            new NpgsqlParameter { Value = (object?)programId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid },
            at, actor);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task SetStatusAsync(long orderNumber, OrderStatus status, OrderStatus? heldFrom, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE execution.production_order SET status = $2, held_from = $3, updated_at = $4, updated_by = $5 WHERE order_number = $1",
            orderNumber, status.ToString(),
            new NpgsqlParameter { Value = (object?)heldFrom?.ToString() ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text },
            at, actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveReleaseAsync(long orderNumber, IReadOnlyList<OrderOperationInfo> operations, IReadOnlyList<OrderMaterialInfo> materials, DateTimeOffset at, string actor, CancellationToken ct)
    {
        foreach (var op in operations)
        {
            await using var cmd = uow.Command(
                """
                INSERT INTO execution.order_operation (order_number, sequence, description, work_center_code, activity_code, eligible_machines,
                    required_qualifications, planned_minutes, crew_size, status)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
                """,
                orderNumber, op.Sequence, op.Description, op.WorkCenterCode, op.ActivityCode, TextArray(op.EligibleMachines),
                TextArray(op.RequiredQualifications), op.PlannedMinutes, op.CrewSize, op.Status.ToString());
            await cmd.ExecuteNonQueryAsync(ct);
        }

        foreach (var m in materials)
        {
            await using var cmd = uow.Command(
                "INSERT INTO execution.order_material (order_number, line_number, component, component_revision, required_quantity, unit_of_measure) VALUES ($1, $2, $3, $4, $5, $6)",
                orderNumber, m.LineNumber, m.ComponentPartNumber,
                new NpgsqlParameter { Value = (object?)m.ComponentRevision ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text },
                m.RequiredQuantity, m.UnitOfMeasure);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using var stamp = uow.Command("UPDATE execution.production_order SET released_at = $2, released_by = $3 WHERE order_number = $1", orderNumber, at, actor);
        await stamp.ExecuteNonQueryAsync(ct);
    }

    public async Task<OrderInfo?> FindOrderAsync(long orderNumber, CancellationToken ct)
    {
        var order = (await Read(OrderSql + " WHERE order_number = $1", ReadOrder, ct, orderNumber)).SingleOrDefault();
        if (order is null)
        {
            return null;
        }

        var operations = await Read(
            $"""
            SELECT {OperationColumns}
            FROM execution.order_operation WHERE order_number = $1 ORDER BY sequence
            """,
            r => ReadOperation(r, 0),
            ct,
            orderNumber);
        var materials = await Read(
            "SELECT line_number, component, component_revision, required_quantity, unit_of_measure FROM execution.order_material WHERE order_number = $1 ORDER BY line_number",
            r => new OrderMaterialInfo(r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetDecimal(3), r.GetString(4)),
            ct,
            orderNumber);
        return order with { Operations = operations, Materials = materials };
    }

    public Task<IReadOnlyList<OrderInfo>> ListOrdersAsync(CancellationToken ct) => Read(OrderSql + " ORDER BY order_number DESC", ReadOrder, ct);

    private const string OperationColumns =
        "sequence, description, work_center_code, activity_code, eligible_machines, required_qualifications, planned_minutes, crew_size, status, good_quantity, scrap_quantity, started_at, completed_at";

    private static string Qualified(string alias) => string.Join(", ", OperationColumns.Split(", ").Select(c => alias + "." + c));

    private static OrderOperationInfo ReadOperation(NpgsqlDataReader r, int o) => new(
        r.GetInt32(o), r.GetString(o + 1), r.GetString(o + 2), r.GetString(o + 3), r.GetFieldValue<string[]>(o + 4), r.GetFieldValue<string[]>(o + 5),
        r.GetDecimal(o + 6), r.GetInt32(o + 7), Enum.Parse<OperationStatus>(r.GetString(o + 8)), r.GetDecimal(o + 9), r.GetDecimal(o + 10),
        r.IsDBNull(o + 11) ? null : r.GetFieldValue<DateTimeOffset>(o + 11), r.IsDBNull(o + 12) ? null : r.GetFieldValue<DateTimeOffset>(o + 12));

    public Task<IReadOnlyList<OpenOperation>> ListOpenOperationsAsync(CancellationToken ct) => Read(
        $"""
        SELECT o.order_number, o.part_number, o.quantity, o.due_date, o.priority, o.program_id, {Qualified("op")}
        FROM execution.production_order o JOIN execution.order_operation op USING (order_number)
        WHERE o.status = 'Released' AND op.status <> 'Complete'
        ORDER BY o.order_number, op.sequence
        """,
        r => new OpenOperation(r.GetInt64(0), r.GetString(1), r.GetDecimal(2), r.GetFieldValue<DateOnly>(3), r.GetInt32(4), r.IsDBNull(5) ? null : r.GetGuid(5), ReadOperation(r, 6)),
        ct);

    private const string LaborSql = "SELECT labor_id, person_id, order_number, sequence, work_center_code, machine_code, role, started_at, ended_at, training_session_id FROM execution.labor";

    private static LaborEntry ReadLabor(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetGuid(1), r.GetInt64(2), r.GetInt32(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), Enum.Parse<LaborRole>(r.GetString(6)),
        r.GetFieldValue<DateTimeOffset>(7), r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8), r.IsDBNull(9) ? null : r.GetInt64(9));

    public Task<IReadOnlyList<LaborEntry>> ListOpenLaborAsync(CancellationToken ct) => Read(LaborSql + " WHERE ended_at IS NULL ORDER BY labor_id", ReadLabor, ct);

    public Task<IReadOnlyList<LaborEntry>> ListLaborAsync(DateTimeOffset from, DateTimeOffset to, long? orderNumber, CancellationToken ct) => orderNumber is { } n
        ? Read(LaborSql + " WHERE order_number = $1 ORDER BY labor_id", ReadLabor, ct, n)
        : Read(LaborSql + " WHERE started_at < $2 AND (ended_at IS NULL OR ended_at > $1) ORDER BY labor_id", ReadLabor, ct, from, to);

    public async Task<LaborEntry?> FindOpenLaborAsync(Guid person, CancellationToken ct) =>
        (await Read(LaborSql + " WHERE person_id = $1 AND ended_at IS NULL", ReadLabor, ct, person)).SingleOrDefault();

    private const string ReportSql =
        "SELECT report_id, order_number, sequence, work_center_code, person_id, good_quantity, scrap_quantity, scrap_reason, completed_operation, final_operation, reported_at FROM execution.production_report";

    public Task<IReadOnlyList<ProductionReport>> ListReportsAsync(DateTimeOffset from, DateTimeOffset to, long? orderNumber, CancellationToken ct)
    {
        static ProductionReport Map(NpgsqlDataReader r) => new(
            r.GetInt64(0), r.GetInt64(1), r.GetInt32(2), r.GetString(3), r.GetGuid(4), r.GetDecimal(5), r.GetDecimal(6), r.IsDBNull(7) ? null : r.GetString(7),
            r.GetBoolean(8), r.GetBoolean(9), r.GetFieldValue<DateTimeOffset>(10));
        return orderNumber is { } n
            ? Read(ReportSql + " WHERE order_number = $1 ORDER BY report_id", Map, ct, n)
            : Read(ReportSql + " WHERE reported_at >= $1 AND reported_at < $2 ORDER BY report_id", Map, ct, from, to);
    }

    private const string SessionSql =
        "SELECT session_id, trainee_id, trainer_id, qualifications, order_number, sequence, machine_code, started_at, ended_at, evaluation, evaluated_at, evaluation_notes FROM execution.training_session";

    private static TrainingSession ReadSession(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetGuid(1), r.GetGuid(2), r.GetFieldValue<string[]>(3), r.GetInt64(4), r.GetInt32(5), r.IsDBNull(6) ? null : r.GetString(6),
        r.GetFieldValue<DateTimeOffset>(7), r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8), r.IsDBNull(9) ? null : Enum.Parse<TrainingEvaluation>(r.GetString(9)),
        r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10), r.IsDBNull(11) ? null : r.GetString(11));

    public async Task<TrainingSession?> FindTrainingSessionAsync(long sessionId, CancellationToken ct) =>
        (await Read(SessionSql + " WHERE session_id = $1", ReadSession, ct, sessionId)).SingleOrDefault();

    public Task<IReadOnlyList<TrainingSession>> ListTrainingSessionsAsync(DateTimeOffset from, DateTimeOffset to, long? orderNumber, CancellationToken ct) => orderNumber is { } n
        ? Read(SessionSql + " WHERE order_number = $1 ORDER BY session_id", ReadSession, ct, n)
        : Read(SessionSql + " WHERE started_at < $2 AND (ended_at IS NULL OR ended_at > $1) ORDER BY session_id", ReadSession, ct, from, to);

    public async Task<long> InsertLaborAsync(Guid person, long orderNumber, int sequence, string workCenter, string? machine, LaborRole role, long? sessionId, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            """
            INSERT INTO execution.labor (person_id, order_number, sequence, work_center_code, machine_code, role, started_at, training_session_id, recorded_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9) RETURNING labor_id
            """,
            person, orderNumber, sequence, workCenter, Text(machine), role.ToString(), at,
            new NpgsqlParameter { Value = (object?)sessionId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint }, actor);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlyList<LaborEntry>> EndLaborAsync(IReadOnlyCollection<long> laborIds, DateTimeOffset at, CancellationToken ct)
    {
        if (laborIds.Count == 0)
        {
            return [];
        }

        return await Read(
            "UPDATE execution.labor SET ended_at = $2 WHERE labor_id = ANY($1) AND ended_at IS NULL RETURNING labor_id, person_id, order_number, sequence, work_center_code, machine_code, role, started_at, ended_at, training_session_id",
            ReadLabor, ct, laborIds.ToArray(), at);
    }

    public async Task MarkOperationStartedAsync(long orderNumber, int sequence, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE execution.order_operation SET status = CASE WHEN status = 'Pending' THEN 'InProgress' ELSE status END, started_at = COALESCE(started_at, $3) WHERE order_number = $1 AND sequence = $2",
            orderNumber, sequence, at);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> InsertReportAsync(long orderNumber, int sequence, string workCenter, Guid person, decimal good, decimal scrap, string? reason, bool complete, bool final, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            """
            INSERT INTO execution.production_report (order_number, sequence, work_center_code, person_id, good_quantity, scrap_quantity, scrap_reason, completed_operation, final_operation, reported_at, recorded_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11) RETURNING report_id
            """,
            orderNumber, sequence, workCenter, person, good, scrap, Text(reason), complete, final, at, actor);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task AddQuantitiesAsync(long orderNumber, int sequence, decimal good, decimal scrap, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE execution.order_operation SET good_quantity = good_quantity + $3, scrap_quantity = scrap_quantity + $4 WHERE order_number = $1 AND sequence = $2",
            orderNumber, sequence, good, scrap);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task CompleteOperationAsync(long orderNumber, int sequence, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE execution.order_operation SET status = 'Complete', started_at = COALESCE(started_at, $3), completed_at = $3 WHERE order_number = $1 AND sequence = $2",
            orderNumber, sequence, at);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task CompleteOrderAsync(long orderNumber, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE execution.production_order SET status = 'Completed', completed_at = $2, updated_at = $2, updated_by = $3 WHERE order_number = $1",
            orderNumber, at, actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> InsertTrainingSessionAsync(Guid trainee, Guid trainer, IReadOnlyList<string> qualifications, long orderNumber, int sequence, string? machine, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "INSERT INTO execution.training_session (trainee_id, trainer_id, qualifications, order_number, sequence, machine_code, started_at) VALUES ($1, $2, $3, $4, $5, $6, $7) RETURNING session_id",
            trainee, trainer, TextArray(qualifications), orderNumber, sequence, Text(machine), at);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task EndTrainingSessionAsync(long sessionId, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = uow.Command("UPDATE execution.training_session SET ended_at = $2 WHERE session_id = $1 AND ended_at IS NULL", sessionId, at);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task EvaluateTrainingSessionAsync(long sessionId, TrainingEvaluation evaluation, string? notes, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE execution.training_session SET evaluation = $2, evaluated_at = $3, evaluation_notes = $4 WHERE session_id = $1",
            sessionId, evaluation.ToString(), at, Text(notes));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertSignoffAsync(Guid person, string method, string action, string subject, string meaning, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "INSERT INTO execution.electronic_signoff (person_id, method, action, subject, meaning, signed_at, recorded_by) VALUES ($1, $2, $3, $4, $5, $6, $7)",
            person, method, action, subject, meaning, at, actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text };

    private static OrderInfo ReadOrder(NpgsqlDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        r.GetString(2),
        r.GetDecimal(3),
        r.GetFieldValue<DateOnly>(4),
        r.GetInt32(5),
        Enum.Parse<OrderStatus>(r.GetString(6)),
        r.IsDBNull(7) ? null : Enum.Parse<OrderStatus>(r.GetString(7)),
        r.IsDBNull(8) ? null : r.GetGuid(8),
        r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9),
        [],
        [],
        r.GetString(10),
        r.IsDBNull(11) ? null : r.GetString(11));

    private static NpgsqlParameter TextArray(IEnumerable<string> values) =>
        new() { Value = values.ToArray(), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text };

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

public static class ExecutionRegistration
{
    public static IServiceCollection AddForgeExecution(this IServiceCollection services)
    {
        services.AddForgeMigrations(typeof(ExecutionRegistration).Assembly, "execution");
        services.AddForgeStore<IExecutionStore>(uow => new PostgresExecutionStore(uow));
        services.AddForgeStore<IExecutionLookup>(uow => new PostgresExecutionStore(uow));
        services.AddForgeStore<IShopFloorStore>(uow => new PostgresExecutionStore(uow));
        services.AddSingleton<ProductionOrderService>();
        services.AddSingleton<Engines.OperatorAuthorizationEngine>();
        services.AddSingleton<ShopFloorService>();
        return services;
    }
}
