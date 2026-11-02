using Forge.Inventory.Contracts;
using Forge.Kernel.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Inventory.Infrastructure;

internal sealed class PostgresAllocationStore(PostgresUnitOfWork uow) : IAllocationLookup, IAllocationStore
{
    public async Task<AllocationPolicy> AllocationPolicyAsync(CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT rules, set_by, set_at FROM inventory.allocation_policy ORDER BY policy_id DESC LIMIT 1");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new AllocationPolicy(r.GetFieldValue<string[]>(0).Select(Enum.Parse<AllocationRule>).ToList(), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2))
            : AllocationPolicy.Default;
    }

    public async Task<IReadOnlyList<ReservationAlternate>> ListReservationAlternatesAsync(CancellationToken ct)
    {
        var list = new List<ReservationAlternate>();
        await using var cmd = uow.Command("SELECT reservation_id, primary_part, factor, alternate_id FROM inventory.reservation_alternate");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new ReservationAlternate(r.GetInt64(0), r.GetString(1), r.GetDecimal(2), r.IsDBNull(3) ? null : r.GetInt64(3)));
        return list;
    }

    public async Task<IReadOnlyList<AlternateIssue>> ListAlternateIssuesAsync(CancellationToken ct)
    {
        var list = new List<AlternateIssue>();
        await using var cmd = uow.Command(
            """
            SELECT order_number, part_number, substitutes_for, sum(CASE WHEN kind = 'Issue' THEN 1 ELSE -1 END * quantity), sum(CASE WHEN kind = 'Issue' THEN 1 ELSE -1 END * COALESCE(primary_quantity, quantity))
            FROM inventory.transaction WHERE kind IN ('Issue', 'Return') AND order_number IS NOT NULL AND substitutes_for IS NOT NULL
            GROUP BY 1, 2, 3
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new AlternateIssue(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetDecimal(3), r.GetDecimal(4)));
        return list;
    }

    public async Task<IReadOnlyList<LotDimension>> ListLotDimensionsAsync(string? partNumber, CancellationToken ct)
    {
        var list = new List<LotDimension>();
        await using var cmd = uow.Command(
            "SELECT part_number, lot_number, length, width, thickness, unit, recorded_by, recorded_at FROM inventory.lot_dimension WHERE ($1::text IS NULL OR part_number = $1) ORDER BY part_number, lot_number",
            Text(partNumber));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new LotDimension(r.GetString(0), r.GetString(1), new PieceSize(Dec(r, 2), Dec(r, 3), Dec(r, 4), Enum.Parse<SizeUnit>(r.GetString(5))), r.GetString(6), r.GetFieldValue<DateTimeOffset>(7)));
        return list;
    }

    public async Task<IReadOnlyList<PieceSizeRule>> ListPieceSizeRulesAsync(CancellationToken ct)
    {
        var list = new List<PieceSizeRule>();
        await using var cmd = uow.Command(
            "SELECT parent_part, component_part, min_length, min_width, min_thickness, unit, set_by, set_at FROM inventory.piece_size_rule ORDER BY parent_part, component_part");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new PieceSizeRule(r.GetString(0), r.GetString(1), new PieceSize(Dec(r, 2), Dec(r, 3), Dec(r, 4), Enum.Parse<SizeUnit>(r.GetString(5))), r.GetString(6), r.GetFieldValue<DateTimeOffset>(7)));
        return list;
    }

    public async Task<IReadOnlyList<LotEarmark>> ListLotEarmarksAsync(bool activeOnly, CancellationToken ct)
    {
        var list = new List<LotEarmark>();
        await using var cmd = uow.Command(
            """
            SELECT e.earmark_id, e.order_number, e.part_number, e.lot_number, e.quantity,
                   COALESCE((SELECT sum(CASE WHEN t.kind = 'Issue' THEN t.quantity ELSE -t.quantity END) FROM inventory.transaction t
                             WHERE t.kind IN ('Issue', 'Return') AND t.order_number = e.order_number AND t.part_number = e.part_number AND t.lot_number = e.lot_number AND t.at >= e.created_at), 0),
                   e.created_by, e.created_at, e.released_at
            FROM inventory.lot_earmark e WHERE (NOT $1 OR e.released_at IS NULL) ORDER BY e.earmark_id
            """, activeOnly);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new LotEarmark(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetDecimal(4), Math.Max(0m, r.GetDecimal(5)), r.GetString(6), r.GetFieldValue<DateTimeOffset>(7),
                r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8)));
        return list;
    }

    public async Task<IReadOnlyList<WipAllocation>> ListWipAllocationsAsync(bool activeOnly, CancellationToken ct)
    {
        var list = new List<WipAllocation>();
        await using var cmd = uow.Command(
            """
            SELECT allocation_id, wip_order_number, consumer_order_number, part_number, quantity, fulfilled, status, why, approved_by, approved_at, closed_by, closed_at, close_reason
            FROM inventory.wip_allocation WHERE (NOT $1 OR status = 'Active') ORDER BY allocation_id
            """, activeOnly);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new WipAllocation(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetDecimal(4), r.GetDecimal(5), Enum.Parse<WipAllocationStatus>(r.GetString(6)), r.GetString(7),
                r.GetString(8), r.GetFieldValue<DateTimeOffset>(9), r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11), r.IsDBNull(12) ? null : r.GetString(12)));
        return list;
    }

    public async Task InsertAllocationPolicyAsync(AllocationPolicy p, CancellationToken ct)
    {
        await using var cmd = uow.Command("INSERT INTO inventory.allocation_policy (rules, set_by, set_at) VALUES ($1, $2, $3)",
            new NpgsqlParameter { Value = p.Rules.Select(x => x.ToString()).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text }, p.SetBy, p.SetAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertReservationAlternateAsync(ReservationAlternate a, CancellationToken ct)
    {
        await using var cmd = uow.Command("INSERT INTO inventory.reservation_alternate (reservation_id, primary_part, factor, alternate_id) VALUES ($1, $2, $3, $4)",
            a.ReservationId, a.PrimaryPart, a.Factor, new NpgsqlParameter { Value = (object?)a.AlternateId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertLotDimensionAsync(LotDimension d, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            """
            INSERT INTO inventory.lot_dimension (part_number, lot_number, length, width, thickness, unit, recorded_by, recorded_at) VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            ON CONFLICT (part_number, lot_number) DO UPDATE SET length = EXCLUDED.length, width = EXCLUDED.width, thickness = EXCLUDED.thickness, unit = EXCLUDED.unit,
                recorded_by = EXCLUDED.recorded_by, recorded_at = EXCLUDED.recorded_at
            """, d.PartNumber, d.LotNumber, Num(d.Size.Length), Num(d.Size.Width), Num(d.Size.Thickness), d.Size.Unit.ToString(), d.RecordedBy, d.RecordedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertPieceSizeRuleAsync(PieceSizeRule p, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            """
            INSERT INTO inventory.piece_size_rule (parent_part, component_part, min_length, min_width, min_thickness, unit, set_by, set_at) VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            ON CONFLICT (parent_part, component_part) DO UPDATE SET min_length = EXCLUDED.min_length, min_width = EXCLUDED.min_width, min_thickness = EXCLUDED.min_thickness,
                unit = EXCLUDED.unit, set_by = EXCLUDED.set_by, set_at = EXCLUDED.set_at
            """, p.ParentPart, p.ComponentPart, Num(p.Minimum.Length), Num(p.Minimum.Width), Num(p.Minimum.Thickness), p.Minimum.Unit.ToString(), p.SetBy, p.SetAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> DeletePieceSizeRuleAsync(string parentPart, string componentPart, CancellationToken ct)
    {
        await using var cmd = uow.Command("DELETE FROM inventory.piece_size_rule WHERE parent_part = $1 AND component_part = $2", parentPart, componentPart);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<long> InsertLotEarmarkAsync(LotEarmark e, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "INSERT INTO inventory.lot_earmark (order_number, part_number, lot_number, quantity, created_by, created_at) VALUES ($1, $2, $3, $4, $5, $6) RETURNING earmark_id",
            e.OrderNumber, e.PartNumber, e.LotNumber, e.Quantity, e.CreatedBy, e.CreatedAt);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<bool> ReleaseLotEarmarkAsync(long earmarkId, string actor, DateTimeOffset at, string reason, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE inventory.lot_earmark SET released_by = $2, released_at = $3, release_reason = $4 WHERE earmark_id = $1 AND released_at IS NULL", earmarkId, actor, at, reason);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<long> InsertWipAllocationAsync(WipAllocation w, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            """
            INSERT INTO inventory.wip_allocation (wip_order_number, consumer_order_number, part_number, quantity, fulfilled, status, why, approved_by, approved_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9) RETURNING allocation_id
            """, w.WipOrderNumber, w.ConsumerOrderNumber, w.PartNumber, w.Quantity, w.Fulfilled, w.Status.ToString(), w.Why, w.ApprovedBy, w.ApprovedAt);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task UpdateWipAllocationAsync(long allocationId, decimal fulfilled, WipAllocationStatus status, string? closedBy, DateTimeOffset? closedAt, string? reason, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE inventory.wip_allocation SET fulfilled = $2, status = $3, closed_by = $4, closed_at = $5, close_reason = $6 WHERE allocation_id = $1",
            allocationId, fulfilled, status.ToString(), Text(closedBy), new NpgsqlParameter { Value = (object?)closedAt ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz }, Text(reason));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static decimal? Dec(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDecimal(i);

    private static NpgsqlParameter Num(decimal? v) => new() { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Numeric };

    private static NpgsqlParameter Text(string? v) => new() { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };
}
