using Forge.Execution.Contracts;
using Forge.Kernel.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Execution.Infrastructure;

/// <summary>Production holds (V3.9).</summary>
internal sealed class PostgresHoldStore(PostgresUnitOfWork uow) : IProductionHoldLookup, IProductionHoldStore
{
    public async Task<IReadOnlyList<ProductionHold>> ListProductionHoldsAsync(bool includeReleased, CancellationToken ct)
    {
        var list = new List<ProductionHold>();
        await using var cmd = uow.Command(
            """
            SELECT hold_id, scope, order_number, sequence, part_number, work_center_code, machine_code, reason, placed_at, placed_by, released_at, released_by, release_reason
            FROM execution.production_hold WHERE $1 OR released_at IS NULL ORDER BY hold_id DESC
            """,
            includeReleased);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new ProductionHold(
                r.GetInt64(0), Enum.Parse<HoldScope>(r.GetString(1)), r.IsDBNull(2) ? null : r.GetInt64(2), r.IsDBNull(3) ? null : r.GetInt32(3),
                S(r, 4), S(r, 5), S(r, 6), r.GetString(7), r.GetFieldValue<DateTimeOffset>(8), r.GetString(9),
                r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10), S(r, 11), S(r, 12)));
        }

        return list;
    }

    public async Task<long> InsertProductionHoldAsync(ProductionHold hold, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(hold);
        await using var cmd = uow.Command(
            """
            INSERT INTO execution.production_hold (scope, order_number, sequence, part_number, work_center_code, machine_code, reason, placed_at, placed_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9) RETURNING hold_id
            """,
            hold.Scope.ToString(),
            new NpgsqlParameter { Value = (object?)hold.OrderNumber ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint },
            new NpgsqlParameter { Value = (object?)hold.Sequence ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer },
            Text(hold.PartNumber),
            Text(hold.WorkCenterCode),
            Text(hold.MachineCode),
            hold.Reason,
            hold.PlacedAt,
            hold.PlacedBy);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task ReleaseProductionHoldAsync(long holdId, DateTimeOffset at, string actor, string reason, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE execution.production_hold SET released_at = $2, released_by = $3, release_reason = $4 WHERE hold_id = $1 AND released_at IS NULL",
            holdId, at, actor, reason);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string? S(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };
}
