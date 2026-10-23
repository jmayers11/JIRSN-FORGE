using Forge.Inventory.Contracts;
using Npgsql;

namespace Forge.Inventory.Infrastructure;

/// <summary>Material tasks and cycle-count programs (V3.4).</summary>
internal sealed partial class PostgresInventoryStore
{
    private const string TaskSql =
        """
        SELECT task_id, kind, order_number, kit_code, part_number, lot_number, quantity, from_location, to_location, status, picked_quantity, reason,
               requested_by, requested_at, allocated_by, allocated_at, picked_by, picked_at, delivered_by, delivered_at, transaction_id, cancelled_by, cancelled_at, cancel_reason,
               transit_location, receiver_person_id, received_by, received_at, receipt_transaction_id, operation_sequence, requester_person_id, source_suspect_report_id,
               is_non_bom, reviewed_by, reviewed_at, review_note
        FROM inventory.material_task
        """;

    public async Task<long> InsertMaterialTaskAsync(MaterialTask task, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);
        await using var cmd = uow.Command(
            """
            INSERT INTO inventory.material_task (kind, order_number, kit_code, part_number, lot_number, quantity, from_location, to_location, status, reason, requested_by, requested_at,
                allocated_by, allocated_at, operation_sequence, requester_person_id, source_suspect_report_id, is_non_bom, reviewed_by, reviewed_at, review_note)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20, $21)
            ON CONFLICT (source_suspect_report_id) WHERE source_suspect_report_id IS NOT NULL DO NOTHING
            RETURNING task_id
            """,
            task.Kind.ToString(), Long(task.OrderNumber), Text(task.KitCode), task.PartNumber, Text(task.LotNumber), task.Quantity, Text(task.FromLocation), task.ToLocation,
            task.Status.ToString(), Text(task.Reason), task.RequestedBy, task.RequestedAt, Text(task.AllocatedBy), Time(task.AllocatedAt),
            new NpgsqlParameter { Value = (object?)task.OperationSequence ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer }, Uuid(task.RequesterPersonId),
            new NpgsqlParameter { Value = (object?)task.SourceSuspectReportId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint }, task.IsNonBom,
            Text(task.ReviewedBy), Time(task.ReviewedAt), Text(task.ReviewNote));
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is not long id)
            throw new Forge.Kernel.Commands.CommandRejectedException("replacement_already_requested", $"Suspect report {task.SourceSuspectReportId} already has a linked material request.");
        return id;
    }

    public async Task<bool> UpdateMaterialTaskAsync(MaterialTask task, MaterialTaskStatus expected, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);
        await using var cmd = uow.Command(
            """
            UPDATE inventory.material_task SET
                lot_number = $3, from_location = $4, status = $5, picked_quantity = $6, allocated_by = $7, allocated_at = $8, picked_by = $9, picked_at = $10,
                delivered_by = $11, delivered_at = $12, transaction_id = $13, cancelled_by = $14, cancelled_at = $15, cancel_reason = $16,
                transit_location = $17, receiver_person_id = $18, received_by = $19, received_at = $20, receipt_transaction_id = $21,
                is_non_bom = $22, reviewed_by = $23, reviewed_at = $24, review_note = $25
            WHERE task_id = $1 AND status = $2
            """,
            task.TaskId, expected.ToString(), Text(task.LotNumber), Text(task.FromLocation), task.Status.ToString(),
            new NpgsqlParameter { Value = (object?)task.PickedQuantity ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Numeric },
            Text(task.AllocatedBy), Time(task.AllocatedAt), Text(task.PickedBy), Time(task.PickedAt), Text(task.DeliveredBy), Time(task.DeliveredAt), Long(task.TransactionId),
            Text(task.CancelledBy), Time(task.CancelledAt), Text(task.CancelReason), Text(task.TransitLocation), Uuid(task.ReceiverPersonId),
            Text(task.ReceivedBy), Time(task.ReceivedAt), Long(task.ReceiptTransactionId), task.IsNonBom, Text(task.ReviewedBy), Time(task.ReviewedAt), Text(task.ReviewNote));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public Task<IReadOnlyList<MaterialTask>> ListMaterialTasksAsync(bool openOnly, long? orderNumber, CancellationToken ct) =>
        ReadAsync(
            TaskSql + " WHERE (NOT $1 OR status IN ('ApprovalNeeded', 'Requested', 'Allocated', 'Picked', 'InTransit')) AND ($2::bigint IS NULL OR order_number = $2) ORDER BY task_id",
            ReadTask,
            ct,
            openOnly, Long(orderNumber));

    public async Task<MaterialTask?> FindMaterialTaskAsync(long taskId, CancellationToken ct) =>
        (await ReadAsync(TaskSql + " WHERE task_id = $1", ReadTask, ct, taskId)).SingleOrDefault();

    public async Task<MaterialTask?> FindMaterialTaskBySuspectReportAsync(long reportId, CancellationToken ct) =>
        (await ReadAsync(TaskSql + " WHERE source_suspect_report_id = $1", ReadTask, ct, reportId)).SingleOrDefault();

    public async Task<long> InsertMaterialTaskCustodyEventAsync(MaterialTaskCustodyEvent custodyEvent, CancellationToken ct)
    {
        await using var cmd = uow.Command("""
            INSERT INTO inventory.material_task_custody_event
                (task_id, kind, recorded_at, recorded_by, person_id, location_code, transaction_id, authentication_method, reason)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9) RETURNING event_id
            """, custodyEvent.TaskId, custodyEvent.Kind.ToString(), custodyEvent.RecordedAt, custodyEvent.RecordedBy,
            Uuid(custodyEvent.PersonId), custodyEvent.LocationCode, Long(custodyEvent.TransactionId), Text(custodyEvent.AuthenticationMethod), Text(custodyEvent.Reason));
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public Task<IReadOnlyList<MaterialTaskCustodyEvent>> ListMaterialTaskCustodyEventsAsync(long taskId, CancellationToken ct) =>
        ReadAsync("""
            SELECT event_id, task_id, kind, recorded_at, recorded_by, person_id, location_code, transaction_id, authentication_method, reason
            FROM inventory.material_task_custody_event WHERE task_id = $1 ORDER BY event_id
            """, r => new MaterialTaskCustodyEvent(r.GetInt64(0), r.GetInt64(1), Enum.Parse<MaterialCustodyEventKind>(r.GetString(2)),
                r.GetFieldValue<DateTimeOffset>(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetGuid(5), r.GetString(6),
                r.IsDBNull(7) ? null : r.GetInt64(7), NullableText(r, 8), NullableText(r, 9)), ct, taskId);

    public async Task<bool> InsertCountProgramAsync(CycleCountProgram program, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(program);
        await using var cmd = uow.Command(
            """
            INSERT INTO inventory.cycle_count_program (program_code, name, location_code, part_number, frequency_days, active, created_by, created_at)
            VALUES ($1, $2, $3, $4, $5, true, $6, $7) ON CONFLICT DO NOTHING
            """,
            program.ProgramCode, program.Name, Text(program.LocationCode), Text(program.PartNumber), program.FrequencyDays, program.CreatedBy, program.CreatedAt);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task RetireCountProgramAsync(string programCode, string actor, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE inventory.cycle_count_program SET active = false, retired_by = $2, retired_at = $3 WHERE program_code = $1 AND active",
            programCode, actor, at);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task<IReadOnlyList<CycleCountProgram>> ListCountProgramsAsync(CancellationToken ct) =>
        ReadAsync(
            "SELECT program_code, name, location_code, part_number, frequency_days, active, created_by, created_at, retired_by, retired_at FROM inventory.cycle_count_program ORDER BY program_code",
            r => new CycleCountProgram(r.GetString(0), r.GetString(1), NullableText(r, 2), NullableText(r, 3), r.GetInt32(4), r.GetBoolean(5), r.GetString(6), r.GetFieldValue<DateTimeOffset>(7),
                NullableText(r, 8), r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9)),
            ct);

    public Task<IReadOnlyList<LastCount>> ListLastCountsAsync(CancellationToken ct) =>
        ReadAsync(
            """
            SELECT part_number, location_code, lot_number, max(requested_at)
            FROM inventory.adjustment_request WHERE kind = 'Count'
            GROUP BY part_number, location_code, lot_number
            """,
            r => new LastCount(r.GetString(0), r.GetString(1), NullableText(r, 2), r.GetFieldValue<DateTimeOffset>(3)),
            ct);

    private static MaterialTask ReadTask(NpgsqlDataReader r) => new(
        r.GetInt64(0),
        Enum.Parse<MaterialTaskKind>(r.GetString(1)),
        r.IsDBNull(2) ? null : r.GetInt64(2),
        NullableText(r, 3),
        r.GetString(4),
        NullableText(r, 5),
        Dec(r, 6),
        NullableText(r, 7),
        r.GetString(8),
        Enum.Parse<MaterialTaskStatus>(r.GetString(9)),
        r.IsDBNull(10) ? null : Dec(r, 10),
        NullableText(r, 11),
        r.GetString(12),
        r.GetFieldValue<DateTimeOffset>(13),
        NullableText(r, 14),
        r.IsDBNull(15) ? null : r.GetFieldValue<DateTimeOffset>(15),
        NullableText(r, 16),
        r.IsDBNull(17) ? null : r.GetFieldValue<DateTimeOffset>(17),
        NullableText(r, 18),
        r.IsDBNull(19) ? null : r.GetFieldValue<DateTimeOffset>(19),
        r.IsDBNull(20) ? null : r.GetInt64(20),
        NullableText(r, 21),
        r.IsDBNull(22) ? null : r.GetFieldValue<DateTimeOffset>(22),
        NullableText(r, 23),
        NullableText(r, 24),
        r.IsDBNull(25) ? null : r.GetGuid(25),
        NullableText(r, 26),
        r.IsDBNull(27) ? null : r.GetFieldValue<DateTimeOffset>(27),
        r.IsDBNull(28) ? null : r.GetInt64(28),
        r.IsDBNull(29) ? null : r.GetInt32(29),
        r.IsDBNull(30) ? null : r.GetGuid(30),
        r.IsDBNull(31) ? null : r.GetInt64(31),
        r.GetBoolean(32), NullableText(r, 33), r.IsDBNull(34) ? null : r.GetFieldValue<DateTimeOffset>(34), NullableText(r, 35));
}
