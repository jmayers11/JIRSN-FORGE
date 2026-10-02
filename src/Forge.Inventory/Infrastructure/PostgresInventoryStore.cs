using Forge.Inventory.Contracts;
using Forge.Kernel.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Forge.Inventory.Infrastructure;

internal sealed class PostgresInventoryStore(PostgresUnitOfWork uow) : IInventoryStore, IInventoryLookup
{
    private const string TransactionSql =
        "SELECT transaction_id, kind, part_number, quantity, from_location, from_status, to_location, to_status, lot_number, order_number, po_number, po_line, reason, at, actor, approved_by, request_id FROM inventory.transaction";

    private const string RequestSql =
        "SELECT request_id, kind, part_number, location_code, lot_number, status, quantity, system_quantity, reason, requested_by, requested_at, state, decided_by, decided_at, transaction_id FROM inventory.adjustment_request";

    public async Task LockPartAsync(string partNumber, CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT pg_advisory_xact_lock(hashtextextended('forge.inventory:' || $1, 0))", partNumber);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<decimal> BalanceAsync(string partNumber, string locationCode, string? lotNumber, StockStatus status, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "SELECT COALESCE(sum(quantity), 0) FROM inventory.balance WHERE part_number = $1 AND location_code = $2 AND lot_number IS NOT DISTINCT FROM $3 AND status = $4",
            partNumber, locationCode, Text(lotNumber), status.ToString());
        return Normal((decimal)(await cmd.ExecuteScalarAsync(ct))!);
    }

    public async Task<long> InsertTransactionAsync(NewTransaction transaction, DateTimeOffset at, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        await using var cmd = uow.Command(
            """
            INSERT INTO inventory.transaction (kind, part_number, quantity, from_location, from_status, to_location, to_status, lot_number,
                order_number, po_number, po_line, reason, request_id, at, actor, approved_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16)
            RETURNING transaction_id
            """,
            transaction.Kind.ToString(),
            transaction.PartNumber,
            transaction.Quantity,
            Text(transaction.FromLocation),
            Text(transaction.FromStatus?.ToString()),
            Text(transaction.ToLocation),
            Text(transaction.ToStatus?.ToString()),
            Text(transaction.LotNumber),
            Long(transaction.OrderNumber),
            Long(transaction.PoNumber),
            new NpgsqlParameter { Value = (object?)transaction.PoLine ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer },
            Text(transaction.Reason),
            Long(transaction.RequestId),
            at,
            actor,
            Text(transaction.ApprovedBy));
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task EnsureLotAsync(string partNumber, string lotNumber, string? supplierLot, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "INSERT INTO inventory.lot (part_number, lot_number, supplier_lot, first_received_at, created_by) VALUES ($1, $2, $3, $4, $5) ON CONFLICT DO NOTHING",
            partNumber, lotNumber, Text(supplierLot), at, actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> InsertLocationAsync(LocationInfo location, DateTimeOffset at, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(location);
        await using var cmd = uow.Command(
            "INSERT INTO inventory.location (code, site_code, name, kind, active, created_at, created_by) VALUES ($1, $2, $3, $4, true, $5, $6) ON CONFLICT DO NOTHING",
            location.Code, location.SiteCode, location.Name, location.Kind.ToString(), at, actor);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task SetLocationActiveAsync(string code, bool active, CancellationToken ct)
    {
        await using var cmd = uow.Command("UPDATE inventory.location SET active = $2 WHERE code = $1", code, active);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> InsertRequestAsync(AdjustmentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var cmd = uow.Command(
            """
            INSERT INTO inventory.adjustment_request (kind, part_number, location_code, lot_number, status, quantity, system_quantity, reason, requested_by, requested_at, state)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11) RETURNING request_id
            """,
            request.Kind.ToString(), request.PartNumber, request.LocationCode, Text(request.LotNumber), request.Status.ToString(),
            request.Quantity, request.SystemQuantity, request.Reason, request.RequestedBy, request.RequestedAt, request.State.ToString());
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<AdjustmentRequest?> FindRequestAsync(long requestId, CancellationToken ct) =>
        (await ReadAsync(RequestSql + " WHERE request_id = $1", ReadRequest, ct, requestId)).SingleOrDefault();

    public async Task DecideRequestAsync(long requestId, AdjustmentState state, string actor, DateTimeOffset at, long? transactionId, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE inventory.adjustment_request SET state = $2, decided_by = $3, decided_at = $4, transaction_id = $5 WHERE request_id = $1",
            requestId, state.ToString(), actor, at, Long(transactionId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> LastPlacedIntoAsync(string partNumber, string locationCode, string? lotNumber, StockStatus status, CancellationToken ct) =>
        (await ReadAsync(
            "SELECT actor FROM inventory.transaction WHERE part_number = $1 AND to_location = $2 AND lot_number IS NOT DISTINCT FROM $3 AND to_status = $4 ORDER BY transaction_id DESC LIMIT 1",
            r => r.GetString(0),
            ct,
            partNumber,
            locationCode,
            Text(lotNumber),
            status.ToString())).SingleOrDefault();

    public Task<IReadOnlyList<LocationInfo>> ListLocationsAsync(CancellationToken ct) =>
        ReadAsync(
            "SELECT code, site_code, name, kind, active FROM inventory.location ORDER BY code",
            r => new LocationInfo(r.GetString(0), r.GetString(1), r.GetString(2), Enum.Parse<LocationKind>(r.GetString(3)), r.GetBoolean(4)),
            ct);

    public Task<IReadOnlyList<StockBalance>> ListBalancesAsync(string? partNumber, CancellationToken ct) =>
        ReadAsync(
            "SELECT part_number, location_code, lot_number, status, quantity FROM inventory.balance WHERE quantity <> 0 AND ($1::text IS NULL OR part_number = $1) ORDER BY part_number, location_code, lot_number NULLS FIRST, status",
            r => new StockBalance(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), Enum.Parse<StockStatus>(r.GetString(3)), Dec(r, 4)),
            ct,
            Text(partNumber));

    public Task<IReadOnlyList<InventoryTransaction>> ListTransactionsAsync(string? partNumber, long? orderNumber, int take, CancellationToken ct) =>
        ReadAsync(
            TransactionSql + " WHERE ($1::text IS NULL OR part_number = $1) AND ($2::bigint IS NULL OR order_number = $2) ORDER BY transaction_id DESC LIMIT $3",
            ReadTransaction,
            ct,
            Text(partNumber),
            Long(orderNumber),
            Math.Clamp(take, 1, 1000));

    public Task<IReadOnlyList<LotInfo>> ListLotsAsync(string? partNumber, CancellationToken ct) =>
        ReadAsync(
            "SELECT part_number, lot_number, supplier_lot, first_received_at FROM inventory.lot WHERE ($1::text IS NULL OR part_number = $1) ORDER BY part_number, first_received_at, lot_number",
            r => new LotInfo(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetFieldValue<DateTimeOffset>(3)),
            ct,
            Text(partNumber));

    public Task<IReadOnlyList<OrderIssue>> ListOrderIssuesAsync(CancellationToken ct) =>
        ReadAsync(
            """
            SELECT order_number, part_number, sum(CASE WHEN kind = 'Issue' THEN quantity ELSE -quantity END)
            FROM inventory.transaction WHERE kind IN ('Issue', 'Return') AND order_number IS NOT NULL
            GROUP BY order_number, part_number ORDER BY order_number, part_number
            """,
            r => new OrderIssue(r.GetInt64(0), r.GetString(1), Dec(r, 2)),
            ct);

    public Task<IReadOnlyList<PurchaseReceipt>> ListPurchaseReceiptsAsync(CancellationToken ct) =>
        ReadAsync(
            "SELECT po_number, po_line, sum(quantity) FROM inventory.transaction WHERE kind = 'Receipt' AND po_number IS NOT NULL GROUP BY po_number, po_line ORDER BY po_number, po_line",
            r => new PurchaseReceipt(r.GetInt64(0), r.GetInt32(1), Dec(r, 2)),
            ct);

    public Task<IReadOnlyList<ProductionReceipt>> ListProductionReceiptsAsync(CancellationToken ct) =>
        ReadAsync(
            "SELECT order_number, sum(quantity) FROM inventory.transaction WHERE kind = 'ProductionReceipt' GROUP BY order_number ORDER BY order_number",
            r => new ProductionReceipt(r.GetInt64(0), Dec(r, 1)),
            ct);

    public Task<IReadOnlyList<AdjustmentRequest>> ListRequestsAsync(bool pendingOnly, CancellationToken ct) =>
        ReadAsync(RequestSql + (pendingOnly ? " WHERE state = 'Pending'" : string.Empty) + " ORDER BY request_id DESC LIMIT 500", ReadRequest, ct);

    private static InventoryTransaction ReadTransaction(NpgsqlDataReader r) => new(
        r.GetInt64(0),
        Enum.Parse<InventoryTransactionKind>(r.GetString(1)),
        r.GetString(2),
        Dec(r, 3),
        r.IsDBNull(4) ? null : r.GetString(4),
        r.IsDBNull(5) ? null : Enum.Parse<StockStatus>(r.GetString(5)),
        r.IsDBNull(6) ? null : r.GetString(6),
        r.IsDBNull(7) ? null : Enum.Parse<StockStatus>(r.GetString(7)),
        r.IsDBNull(8) ? null : r.GetString(8),
        r.IsDBNull(9) ? null : r.GetInt64(9),
        r.IsDBNull(10) ? null : r.GetInt64(10),
        r.IsDBNull(11) ? null : r.GetInt32(11),
        r.IsDBNull(12) ? null : r.GetString(12),
        r.GetFieldValue<DateTimeOffset>(13),
        r.GetString(14),
        r.IsDBNull(15) ? null : r.GetString(15),
        r.IsDBNull(16) ? null : r.GetInt64(16));

    private static AdjustmentRequest ReadRequest(NpgsqlDataReader r) => new(
        r.GetInt64(0),
        Enum.Parse<AdjustmentKind>(r.GetString(1)),
        r.GetString(2),
        r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4),
        Enum.Parse<StockStatus>(r.GetString(5)),
        Dec(r, 6),
        Dec(r, 7),
        r.GetString(8),
        r.GetString(9),
        r.GetFieldValue<DateTimeOffset>(10),
        Enum.Parse<AdjustmentState>(r.GetString(11)),
        r.IsDBNull(12) ? null : r.GetString(12),
        r.IsDBNull(13) ? null : r.GetFieldValue<DateTimeOffset>(13),
        r.IsDBNull(14) ? null : r.GetInt64(14));

    private static decimal Normal(decimal value) => value / 1.000000000000000000000000000000000m;

    private static decimal Dec(NpgsqlDataReader r, int ordinal) => Normal(r.GetDecimal(ordinal));

    private static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text };

    private static NpgsqlParameter Long(long? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint };

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

public static class InventoryRegistration
{
    public static IServiceCollection AddForgeInventory(this IServiceCollection services)
    {
        services.AddForgeMigrations(typeof(InventoryRegistration).Assembly, "inventory");
        services.AddForgeStore<IInventoryStore>(uow => new PostgresInventoryStore(uow));
        services.AddForgeStore<IInventoryLookup>(uow => new PostgresInventoryStore(uow));
        services.AddSingleton<InventoryService>();
        return services;
    }
}
