using Forge.Execution.Contracts;
using Forge.Kernel.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Forge.Execution.Infrastructure;

internal sealed class PostgresExecutionStore(PostgresUnitOfWork uow) : IExecutionStore, IExecutionLookup, IShopFloorStore, IOrderCommitmentStore
{
    private const string OrderSql =
        "SELECT order_number, part_number, revision, quantity, due_date, priority, status, held_from, program_id, released_at, created_by, released_by, wbs_code, contract_id, contract_clin FROM execution.production_order";

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
                    required_qualifications, planned_minutes, crew_size, status, setup_minutes, setup_family, family_setup_minutes)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13)
                """,
                orderNumber, op.Sequence, op.Description, op.WorkCenterCode, op.ActivityCode, TextArray(op.EligibleMachines),
                TextArray(op.RequiredQualifications), op.PlannedMinutes, op.CrewSize, op.Status.ToString(),
                Numeric(op.SetupMinutes), new NpgsqlParameter { Value = (object?)op.SetupFamily ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text }, Numeric(op.FamilySetupMinutes));
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

    public async Task SetRevisionAsync(long orderNumber, string revision, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command("UPDATE execution.production_order SET revision = $2, updated_at = $3, updated_by = $4 WHERE order_number = $1", orderNumber, revision, at, actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ReplaceReleasedCopyAsync(long orderNumber, IReadOnlyList<OrderOperationInfo> operations, IReadOnlyList<OrderMaterialInfo> materials, CancellationToken ct)
    {
        await using (var started = uow.Command("SELECT count(*) FROM execution.order_operation WHERE order_number = $1 AND status <> 'Pending'", orderNumber))
        {
            if ((long)(await started.ExecuteScalarAsync(ct))! > 0)
            {
                throw new InvalidOperationException($"Order {orderNumber} has started work; its released copy is never replaced.");
            }
        }

        await using (var materialRows = uow.Command("DELETE FROM execution.order_material WHERE order_number = $1", orderNumber))
        {
            await materialRows.ExecuteNonQueryAsync(ct);
        }

        await using (var operationRows = uow.Command("DELETE FROM execution.order_operation WHERE order_number = $1", orderNumber))
        {
            await operationRows.ExecuteNonQueryAsync(ct);
        }

        foreach (var op in operations)
        {
            await using var cmd = uow.Command(
                """
                INSERT INTO execution.order_operation (order_number, sequence, description, work_center_code, activity_code, eligible_machines,
                    required_qualifications, planned_minutes, crew_size, status, setup_minutes, setup_family, family_setup_minutes)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13)
                """,
                orderNumber, op.Sequence, op.Description, op.WorkCenterCode, op.ActivityCode, TextArray(op.EligibleMachines),
                TextArray(op.RequiredQualifications), op.PlannedMinutes, op.CrewSize, op.Status.ToString(),
                Numeric(op.SetupMinutes), new NpgsqlParameter { Value = (object?)op.SetupFamily ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text }, Numeric(op.FamilySetupMinutes));
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

    public async Task SetWbsAsync(long orderNumber, string? wbsCode, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE execution.production_order SET wbs_code = $2, updated_at = $3, updated_by = $4 WHERE order_number = $1",
            orderNumber, new NpgsqlParameter { Value = (object?)wbsCode ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text }, at, actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> SetPriorityAsync(long orderNumber, int expectedPriority, int newPriority, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE execution.production_order SET priority = $3, updated_at = $4, updated_by = $5 WHERE order_number = $1 AND priority = $2 AND status NOT IN ('Cancelled', 'Completed')",
            orderNumber, expectedPriority, newPriority, at, actor);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public Task<IReadOnlyList<OrderInfo>> ListOrdersAsync(CancellationToken ct) => Read(OrderSql + " ORDER BY order_number DESC", ReadOrder, ct);

    public async Task SetAsync(long orderNumber, long? contractId, string? clin, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using (var change = uow.Command("UPDATE execution.production_order SET contract_id=$2,contract_clin=$3,updated_at=$4,updated_by=$5 WHERE order_number=$1", orderNumber,
            new NpgsqlParameter { Value = (object?)contractId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint },
            new NpgsqlParameter { Value = (object?)clin ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text }, at, actor))
            await change.ExecuteNonQueryAsync(ct);
        await using var history = uow.Command("INSERT INTO execution.order_contract_commitment(order_number,contract_id,clin,recorded_at,actor) VALUES($1,$2,$3,$4,$5)", orderNumber,
            new NpgsqlParameter { Value = (object?)contractId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint },
            new NpgsqlParameter { Value = (object?)clin ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text }, at, actor);
        await history.ExecuteNonQueryAsync(ct);
    }

    public async Task LockAsync(long orderNumber, CancellationToken ct)
    {
        await using var command = uow.Command("SELECT order_number FROM execution.production_order WHERE order_number=$1 FOR UPDATE", orderNumber);
        await command.ExecuteScalarAsync(ct);
    }

    public Task<IReadOnlyList<OrderCommitment>> AsOfAsync(DateTimeOffset at, CancellationToken ct) => Read(
        "SELECT DISTINCT ON(order_number) order_number,contract_id,clin,recorded_at,actor FROM execution.order_contract_commitment WHERE recorded_at <= $1 ORDER BY order_number,recorded_at DESC,commitment_id DESC",
        reader => new OrderCommitment(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3), reader.GetString(4)), ct, at);

    private const string OperationColumns =
        "sequence, description, work_center_code, activity_code, eligible_machines, required_qualifications, planned_minutes, crew_size, status, good_quantity, scrap_quantity, started_at, completed_at, setup_minutes, setup_family, family_setup_minutes";

    private static NpgsqlParameter Numeric(decimal? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Numeric };

    private static string Qualified(string alias) => string.Join(", ", OperationColumns.Split(", ").Select(c => alias + "." + c));

    private static OrderOperationInfo ReadOperation(NpgsqlDataReader r, int o) => new(
        r.GetInt32(o), r.GetString(o + 1), r.GetString(o + 2), r.GetString(o + 3), r.GetFieldValue<string[]>(o + 4), r.GetFieldValue<string[]>(o + 5),
        r.GetDecimal(o + 6), r.GetInt32(o + 7), Enum.Parse<OperationStatus>(r.GetString(o + 8)), r.GetDecimal(o + 9), r.GetDecimal(o + 10),
        r.IsDBNull(o + 11) ? null : r.GetFieldValue<DateTimeOffset>(o + 11), r.IsDBNull(o + 12) ? null : r.GetFieldValue<DateTimeOffset>(o + 12),
        r.IsDBNull(o + 13) ? null : r.GetDecimal(o + 13), r.IsDBNull(o + 14) ? null : r.GetString(o + 14), r.IsDBNull(o + 15) ? null : r.GetDecimal(o + 15));

    public Task<IReadOnlyList<OpenOperation>> ListOpenOperationsAsync(CancellationToken ct) => Read(
        $"""
        SELECT o.order_number, o.part_number, o.quantity, o.due_date, o.priority, o.program_id, {Qualified("op")}
        FROM execution.production_order o JOIN execution.order_operation op USING (order_number)
        WHERE o.status = 'Released' AND op.status <> 'Complete'
        ORDER BY o.order_number, op.sequence
        """,
        r => new OpenOperation(r.GetInt64(0), r.GetString(1), r.GetDecimal(2), r.GetFieldValue<DateOnly>(3), r.GetInt32(4), r.IsDBNull(5) ? null : r.GetGuid(5), ReadOperation(r, 6)),
        ct);

    private const string LaborSql = "SELECT labor_id, person_id, order_number, sequence, work_center_code, machine_code, role, started_at, ended_at, training_session_id, qualifications, phase FROM execution.labor";

    private static LaborEntry ReadLabor(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetGuid(1), r.GetInt64(2), r.GetInt32(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), Enum.Parse<LaborRole>(r.GetString(6)),
        r.GetFieldValue<DateTimeOffset>(7), r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8), r.IsDBNull(9) ? null : r.GetInt64(9),
        r.IsDBNull(10) ? null : r.GetFieldValue<string[]>(10), Enum.Parse<LaborPhase>(r.GetString(11)));

    public Task<IReadOnlyList<LaborEntry>> ListOpenLaborAsync(CancellationToken ct) => Read(LaborSql + " WHERE ended_at IS NULL ORDER BY labor_id", ReadLabor, ct);

    public Task<IReadOnlyList<LaborEntry>> ListLaborAsync(DateTimeOffset from, DateTimeOffset to, long? orderNumber, CancellationToken ct) => orderNumber is { } n
        ? Read(LaborSql + " WHERE order_number = $1 ORDER BY labor_id", ReadLabor, ct, n)
        : Read(LaborSql + " WHERE started_at < $2 AND (ended_at IS NULL OR ended_at > $1) ORDER BY labor_id", ReadLabor, ct, from, to);

    public async Task<LaborEntry?> FindOpenLaborAsync(Guid person, CancellationToken ct) =>
        (await Read(LaborSql + " WHERE person_id = $1 AND ended_at IS NULL", ReadLabor, ct, person)).SingleOrDefault();

    private const string ReportSql =
        "SELECT report_id, order_number, sequence, work_center_code, person_id, good_quantity, scrap_quantity, scrap_reason, completed_operation, final_operation, reported_at, scrap_reason_code FROM execution.production_report";

    public Task<IReadOnlyList<ProductionReport>> ListReportsAsync(DateTimeOffset from, DateTimeOffset to, long? orderNumber, CancellationToken ct)
    {
        static ProductionReport Map(NpgsqlDataReader r) => new(
            r.GetInt64(0), r.GetInt64(1), r.GetInt32(2), r.GetString(3), r.GetGuid(4), r.GetDecimal(5), r.GetDecimal(6), r.IsDBNull(7) ? null : r.GetString(7),
            r.GetBoolean(8), r.GetBoolean(9), r.GetFieldValue<DateTimeOffset>(10), r.IsDBNull(11) ? null : r.GetString(11));
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

    public async Task<long> InsertLaborAsync(Guid person, long orderNumber, int sequence, string workCenter, string? machine, LaborRole role, LaborPhase phase, long? sessionId, IReadOnlyList<string> qualifications, DateTimeOffset at, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(qualifications);
        await using var cmd = uow.Command(
            """
            INSERT INTO execution.labor (person_id, order_number, sequence, work_center_code, machine_code, role, phase, started_at, training_session_id, recorded_by, qualifications)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11) RETURNING labor_id
            """,
            person, orderNumber, sequence, workCenter, Text(machine), role.ToString(), phase.ToString(), at,
            new NpgsqlParameter { Value = (object?)sessionId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint }, actor, qualifications.Distinct(StringComparer.Ordinal).ToArray());
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlyList<LaborEntry>> EndLaborAsync(IReadOnlyCollection<long> laborIds, DateTimeOffset at, CancellationToken ct)
    {
        if (laborIds.Count == 0)
        {
            return [];
        }

        return await Read(
            "UPDATE execution.labor SET ended_at = $2 WHERE labor_id = ANY($1) AND ended_at IS NULL RETURNING labor_id, person_id, order_number, sequence, work_center_code, machine_code, role, started_at, ended_at, training_session_id, qualifications, phase",
            ReadLabor, ct, laborIds.ToArray(), at);
    }

    public async Task MarkOperationStartedAsync(long orderNumber, int sequence, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE execution.order_operation SET status = CASE WHEN status = 'Pending' THEN 'InProgress' ELSE status END, started_at = COALESCE(started_at, $3) WHERE order_number = $1 AND sequence = $2",
            orderNumber, sequence, at);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> InsertReportAsync(long orderNumber, int sequence, string workCenter, Guid person, decimal good, decimal scrap, string? reason, string? reasonCode, bool complete, bool final, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            """
            INSERT INTO execution.production_report (order_number, sequence, work_center_code, person_id, good_quantity, scrap_quantity, scrap_reason, completed_operation, final_operation, reported_at, recorded_by, scrap_reason_code)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12) RETURNING report_id
            """,
            orderNumber, sequence, workCenter, person, good, scrap, Text(reason), complete, final, at, actor, Text(reasonCode));
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

    public async Task<int?> EvaluateTrainingSessionAsync(long sessionId, TrainingEvaluation evaluation, string? notes, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE execution.training_session SET evaluation = $2, evaluated_at = $3, evaluation_notes = $4, version = version + 1 WHERE session_id = $1 AND evaluation IS NULL RETURNING version",
            sessionId, evaluation.ToString(), at, Text(notes));
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task InsertSignoffAsync(Guid person, string method, string signerRole, string assurance, int recordVersion, string action, string subject, string meaning, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "INSERT INTO execution.electronic_signoff (person_id, method, signer_role, assurance, record_version, action, subject, meaning, signed_at, recorded_by) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)",
            person, method, signerRole, assurance, recordVersion, action, subject, meaning, at, actor);
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
        r.IsDBNull(11) ? null : r.GetString(11),
        r.IsDBNull(12) ? null : r.GetString(12),
        r.IsDBNull(13) ? null : r.GetInt64(13),
        r.IsDBNull(14) ? null : r.GetString(14));

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

/// <summary>The as-built record (V2.11): append-only lines, read by order or by component lot or serial.</summary>
internal sealed class PostgresAsBuiltRecord(PostgresUnitOfWork uow) : IAsBuiltRecord
{
    private const string Sql =
        """
        SELECT as_built_id, order_number, transaction_id, kind, component_part, component_revision, lot_number, serial, quantity, substitutes_for, primary_quantity,
               alternate_id, alternate_kind, alternate_approved_by, alternate_approved_at, alternate_conditions, supplier_code, supplier_name, po_number, po_line,
               received_at, recorded_at, recorded_by
        FROM execution.as_built
        """;

    public async Task AppendAsync(AsBuiltLine line, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(line);
        await using var cmd = uow.Command(
            """
            INSERT INTO execution.as_built (order_number, transaction_id, kind, component_part, component_revision, lot_number, serial, quantity, substitutes_for, primary_quantity,
                alternate_id, alternate_kind, alternate_approved_by, alternate_approved_at, alternate_conditions, supplier_code, supplier_name, po_number, po_line, received_at,
                recorded_at, recorded_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20, $21, $22)
            """,
            line.OrderNumber, line.TransactionId, line.Kind.ToString(), line.ComponentPart, T(line.ComponentRevision), T(line.LotNumber), line.Serial, line.Quantity,
            T(line.SubstitutesFor), N(line.PrimaryQuantity), L(line.AlternateId), T(line.AlternateKind), T(line.AlternateApprovedBy), Ts(line.AlternateApprovedAt), T(line.AlternateConditions),
            T(line.SupplierCode), T(line.SupplierName), L(line.PoNumber), I(line.PoLine), Ts(line.ReceivedAt), line.RecordedAt, line.RecordedBy);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<AsBuiltLine>> ListAsync(long? orderNumber, string? componentPart, string? lotNumber, CancellationToken ct)
    {
        var list = new List<AsBuiltLine>();
        await using var cmd = uow.Command(
            Sql + " WHERE ($1::bigint IS NULL OR order_number = $1) AND ($2::text IS NULL OR component_part = $2) AND ($3::text IS NULL OR lot_number = $3) ORDER BY as_built_id",
            L(orderNumber), T(componentPart), T(lotNumber));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new AsBuiltLine(
                r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), Enum.Parse<AsBuiltKind>(r.GetString(3)), r.GetString(4), S(r, 5), S(r, 6), r.GetBoolean(7), D(r.GetDecimal(8)),
                S(r, 9), r.IsDBNull(10) ? null : D(r.GetDecimal(10)), r.IsDBNull(11) ? null : r.GetInt64(11), S(r, 12), S(r, 13), r.IsDBNull(14) ? null : r.GetFieldValue<DateTimeOffset>(14), S(r, 15),
                S(r, 16), S(r, 17), r.IsDBNull(18) ? null : r.GetInt64(18), r.IsDBNull(19) ? null : r.GetInt32(19), r.IsDBNull(20) ? null : r.GetFieldValue<DateTimeOffset>(20),
                r.GetFieldValue<DateTimeOffset>(21), r.GetString(22)));
        }

        return list;
    }

    private static decimal D(decimal value) => value / 1.000000000000000000000000000000000m;

    private static string? S(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static NpgsqlParameter T(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text };

    private static NpgsqlParameter N(decimal? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Numeric };

    private static NpgsqlParameter L(long? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint };

    private static NpgsqlParameter I(int? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer };

    private static NpgsqlParameter Ts(DateTimeOffset? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz };
}

public static class ExecutionRegistration
{
    public static IServiceCollection AddForgeExecution(this IServiceCollection services)
    {
        services.AddForgeMigrations(typeof(ExecutionRegistration).Assembly, "execution");
        services.AddForgeStore<IExecutionStore>(uow => new PostgresExecutionStore(uow));
        services.AddForgeStore<IExecutionLookup>(uow => new PostgresExecutionStore(uow));
        services.AddForgeStore<IOrderCommitmentLookup>(uow => new PostgresExecutionStore(uow));
        services.AddForgeStore<IOrderCommitmentStore>(uow => new PostgresExecutionStore(uow));
        services.AddForgeStore<IShopFloorStore>(uow => new PostgresExecutionStore(uow));
        services.AddForgeStore<IAsBuiltRecord>(uow => new PostgresAsBuiltRecord(uow));
        services.AddForgeStore<IShiftContinuityStore>(uow => new PostgresShiftContinuityStore(uow));
        services.AddForgeStore<Forge.Maintenance.Contracts.IMachineUsageLookup>(uow => new MachineUsage(uow));
        services.AddSingleton<ProductionOrderService>();
        services.AddSingleton<OrderCommitmentService>();
        services.AddSingleton<PriorityGovernanceService>();
        services.AddSingleton<ShiftContinuityService>();
        services.AddSingleton<Engines.OperatorAuthorizationEngine>();
        services.AddSingleton<ShopFloorService>();
        services.AddForgeStore<IProductionActualsLookup>(uow => new PostgresProductionActualsStore(uow));
        services.AddForgeStore<IProductionActualsStore>(uow => new PostgresProductionActualsStore(uow));
        services.AddSingleton<ProductionActualsService>();
        return services;
    }
}
