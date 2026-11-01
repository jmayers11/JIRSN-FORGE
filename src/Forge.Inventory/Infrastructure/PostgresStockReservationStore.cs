using Forge.Inventory.Contracts;
using Forge.Kernel.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Inventory.Infrastructure;

internal sealed class PostgresStockReservationStore(PostgresUnitOfWork uow) : IStockReservationStore, IStockReservationLookup
{
    private const string Sql =
        "SELECT reservation_id, target, order_number, demand_id, part_number, quantity, status, created_by, created_at, released_by, released_at, release_reason FROM inventory.stock_reservation";

    public async Task LockPartAsync(string partNumber, CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT pg_advisory_xact_lock(hashtextextended('forge.inventory:' || $1, 0))", partNumber);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> InsertReservationAsync(StockReservation r, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "INSERT INTO inventory.stock_reservation (target, order_number, demand_id, part_number, quantity, status, created_at, created_by) VALUES ($1, $2, $3, $4, $5, 'Active', $6, $7) RETURNING reservation_id",
            r.Target.ToString(), Long(r.OrderNumber), Long(r.DemandId), r.PartNumber, r.Quantity, r.CreatedAt, r.CreatedBy);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task SetReservationQuantityAsync(long reservationId, decimal quantity, CancellationToken ct)
    {
        await using var cmd = uow.Command("UPDATE inventory.stock_reservation SET quantity = $2 WHERE reservation_id = $1 AND status = 'Active'", reservationId, quantity);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> ReleaseReservationAsync(long reservationId, DateTimeOffset at, string actor, string reason, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE inventory.stock_reservation SET status = 'Released', released_at = $2, released_by = $3, release_reason = $4 WHERE reservation_id = $1 AND status = 'Active'",
            reservationId, at, actor, reason);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<StockReservation?> FindReservationAsync(long reservationId, CancellationToken ct) =>
        (await ReadAsync(Sql + " WHERE reservation_id = $1", ct, reservationId)).SingleOrDefault();

    public Task<IReadOnlyList<StockReservation>> ListActiveReservationsAsync(string? partNumber, CancellationToken ct) => partNumber is null
        ? ReadAsync(Sql + " WHERE status = 'Active' ORDER BY reservation_id", ct)
        : ReadAsync(Sql + " WHERE status = 'Active' AND part_number = $1 ORDER BY reservation_id", ct, partNumber);

    public Task<IReadOnlyList<StockReservation>> ListReservationsAsync(ReservationTarget target, long key, CancellationToken ct) =>
        ReadAsync(Sql + (target == ReservationTarget.ProductionOrder ? " WHERE target = 'ProductionOrder' AND order_number = $1" : " WHERE target = 'Demand' AND demand_id = $1") + " ORDER BY reservation_id", ct, key);

    private static NpgsqlParameter Long(long? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint };

    private async Task<IReadOnlyList<StockReservation>> ReadAsync(string sql, CancellationToken ct, params object?[] args)
    {
        var list = new List<StockReservation>();
        await using var cmd = uow.Command(sql, args);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new StockReservation(r.GetInt64(0), Enum.Parse<ReservationTarget>(r.GetString(1)), r.IsDBNull(2) ? null : r.GetInt64(2), r.IsDBNull(3) ? null : r.GetInt64(3),
                r.GetString(4), r.GetDecimal(5) / 1.000000000000000000000000000000000m, Enum.Parse<ReservationStatus>(r.GetString(6)), r.GetString(7), r.GetFieldValue<DateTimeOffset>(8),
                r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10), r.IsDBNull(11) ? null : r.GetString(11)));
        }

        return list;
    }
}
