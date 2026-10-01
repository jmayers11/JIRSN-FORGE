using Forge.Execution.Contracts;
using Forge.Kernel.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Forge.Execution.Infrastructure;

internal sealed class PostgresExecutionStore(PostgresUnitOfWork uow) : IExecutionStore, IExecutionLookup
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
            """
            SELECT sequence, description, work_center_code, activity_code, eligible_machines, required_qualifications, planned_minutes, crew_size, status
            FROM execution.order_operation WHERE order_number = $1 ORDER BY sequence
            """,
            r => new OrderOperationInfo(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetFieldValue<string[]>(4), r.GetFieldValue<string[]>(5), r.GetDecimal(6), r.GetInt32(7), Enum.Parse<OperationStatus>(r.GetString(8))),
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
        services.AddSingleton<ProductionOrderService>();
        return services;
    }
}
