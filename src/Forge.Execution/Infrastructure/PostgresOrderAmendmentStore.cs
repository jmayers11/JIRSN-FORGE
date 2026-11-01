using System.Text.Json;
using Forge.Execution.Contracts;
using Forge.Kernel.Commands;
using Forge.Kernel.Postgres;
using Forge.Kernel.Serialization;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Execution.Infrastructure;

internal sealed class PostgresOrderAmendmentStore(PostgresUnitOfWork uow) : IOrderAmendmentStore
{
    private const string Sql =
        "SELECT amendment_id, order_number, status, changes::text, rationale, basis, proposed_by, proposed_at, decided_by, decided_at, decision_note FROM execution.order_amendment";

    public async Task<long> InsertAmendmentAsync(OrderAmendment amendment, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "INSERT INTO execution.order_amendment (order_number, status, changes, rationale, basis, proposed_at, proposed_by) VALUES ($1, 'Proposed', $2, $3, $4, $5, $6) RETURNING amendment_id",
            amendment.OrderNumber, new NpgsqlParameter { Value = CanonicalJson.Serialize(amendment.Changes), NpgsqlDbType = NpgsqlDbType.Jsonb }, amendment.Rationale, amendment.Basis,
            amendment.ProposedAt, amendment.ProposedBy);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<OrderAmendment?> FindAmendmentAsync(long amendmentId, CancellationToken ct) =>
        (await ReadAsync(Sql + " WHERE amendment_id = $1", ct, amendmentId)).SingleOrDefault();

    public Task<IReadOnlyList<OrderAmendment>> ListAmendmentsAsync(long? orderNumber, CancellationToken ct) => orderNumber is { } n
        ? ReadAsync(Sql + " WHERE order_number = $1 ORDER BY amendment_id DESC", ct, n)
        : ReadAsync(Sql + " ORDER BY amendment_id DESC LIMIT 500", ct);

    public async Task<bool> DecideAmendmentAsync(long amendmentId, AmendmentStatus status, DateTimeOffset at, string actor, string? note, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE execution.order_amendment SET status = $2, decided_at = $3, decided_by = $4, decision_note = $5 WHERE amendment_id = $1 AND status = 'Proposed'",
            amendmentId, status.ToString(), at, actor, new NpgsqlParameter { Value = (object?)note ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task LockOrderAsync(long orderNumber, CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT order_number FROM execution.production_order WHERE order_number = $1 FOR UPDATE", orderNumber);
        await cmd.ExecuteScalarAsync(ct);
    }

    public async Task DeletePendingOperationAsync(long orderNumber, int sequence, CancellationToken ct)
    {
        try
        {
            await using var cmd = uow.Command("DELETE FROM execution.order_operation WHERE order_number = $1 AND sequence = $2 AND status = 'Pending'", orderNumber, sequence);
            if (await cmd.ExecuteNonQueryAsync(ct) != 1)
                throw new CommandRejectedException("operation_started", $"Operation {sequence} is no longer pending.");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            throw new CommandRejectedException("operation_referenced", $"Operation {sequence} already has records against it (labor, reports, rework or material requests); it is kept.");
        }
    }

    public async Task InsertOperationAsync(long orderNumber, OrderOperationInfo op, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            """
            INSERT INTO execution.order_operation (order_number, sequence, description, work_center_code, activity_code, eligible_machines,
                required_qualifications, planned_minutes, crew_size, status, setup_minutes)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, 'Pending', $10)
            """,
            orderNumber, op.Sequence, op.Description, op.WorkCenterCode, op.ActivityCode, TextArray(op.EligibleMachines), TextArray(op.RequiredQualifications),
            op.PlannedMinutes, op.CrewSize, new NpgsqlParameter { Value = (object?)op.SetupMinutes ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Numeric });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetPlannedMinutesAsync(long orderNumber, int sequence, decimal plannedMinutes, CancellationToken ct)
    {
        await using var cmd = uow.Command("UPDATE execution.order_operation SET planned_minutes = $3 WHERE order_number = $1 AND sequence = $2 AND status = 'Pending'", orderNumber, sequence, plannedMinutes);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteMaterialLineAsync(long orderNumber, int lineNumber, CancellationToken ct)
    {
        try
        {
            await using var cmd = uow.Command("DELETE FROM execution.order_material WHERE order_number = $1 AND line_number = $2", orderNumber, lineNumber);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            throw new CommandRejectedException("material_referenced", $"Material line {lineNumber} already has records against it; change its quantity instead.");
        }
    }

    public async Task InsertMaterialAsync(long orderNumber, OrderMaterialInfo m, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "INSERT INTO execution.order_material (order_number, line_number, component, component_revision, required_quantity, unit_of_measure, consumed_at_sequence) VALUES ($1, $2, $3, $4, $5, $6, $7)",
            orderNumber, m.LineNumber, m.ComponentPartNumber, new NpgsqlParameter { Value = (object?)m.ComponentRevision ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text },
            m.RequiredQuantity, m.UnitOfMeasure, new NpgsqlParameter { Value = (object?)m.ConsumedAtSequence ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetMaterialQuantityAsync(long orderNumber, int lineNumber, decimal requiredQuantity, CancellationToken ct)
    {
        await using var cmd = uow.Command("UPDATE execution.order_material SET required_quantity = $3 WHERE order_number = $1 AND line_number = $2", orderNumber, lineNumber, requiredQuantity);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetQuantityDueAndPriorityAsync(long orderNumber, decimal quantity, DateOnly dueDate, int priority, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE execution.production_order SET quantity = $2, due_date = $3, priority = $4, updated_at = $5, updated_by = $6 WHERE order_number = $1",
            orderNumber, quantity, dueDate, priority, at, actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static NpgsqlParameter TextArray(IEnumerable<string> values) => new() { Value = values.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text };

    private async Task<IReadOnlyList<OrderAmendment>> ReadAsync(string sql, CancellationToken ct, params object?[] args)
    {
        var list = new List<OrderAmendment>();
        await using var cmd = uow.Command(sql, args);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new OrderAmendment(r.GetInt64(0), r.GetInt64(1), Enum.Parse<AmendmentStatus>(r.GetString(2)),
                JsonSerializer.Deserialize<AmendmentChanges>(r.GetString(3), CanonicalJson.Options)!, r.GetString(4), r.GetString(5), r.GetString(6),
                r.GetFieldValue<DateTimeOffset>(7), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9), r.IsDBNull(10) ? null : r.GetString(10)));
        }

        return list;
    }
}
