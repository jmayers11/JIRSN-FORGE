using Forge.Inventory.Contracts;
using Npgsql;

namespace Forge.Inventory.Infrastructure;

/// <summary>Transformation records and yield tolerances (V3.3), append-only.</summary>
internal sealed partial class PostgresInventoryStore
{
    public async Task<long> InsertTransformationAsync(TransformationInfo transformation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(transformation);
        long id;
        await using (var cmd = uow.Command(
            "INSERT INTO inventory.transformation (order_number, operation_sequence, reason, recorded_at, actor) VALUES ($1, $2, $3, $4, $5) RETURNING transformation_id",
            transformation.OrderNumber,
            new NpgsqlParameter { Value = (object?)transformation.OperationSequence ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer },
            Text(transformation.Reason),
            transformation.RecordedAt,
            transformation.Actor))
        {
            id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }

        foreach (var l in transformation.Lines)
        {
            await using var line = uow.Command(
                """
                INSERT INTO inventory.transformation_line (transformation_id, line_number, role, part_number, lot_number, source_part, source_lot, quantity, location_code, transaction_id)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
                """,
                id, l.LineNumber, l.Role.ToString(), l.PartNumber, Text(l.LotNumber), Text(l.SourcePart), Text(l.SourceLot), l.Quantity, Text(l.LocationCode), Long(l.TransactionId));
            await line.ExecuteNonQueryAsync(ct);
        }

        return id;
    }

    public async Task InsertYieldToleranceAsync(YieldTolerance tolerance, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tolerance);
        await using var cmd = uow.Command(
            "INSERT INTO inventory.yield_tolerance (part_number, percent, reason, set_by, set_at) VALUES ($1, $2, $3, $4, $5)",
            tolerance.PartNumber, tolerance.Percent, tolerance.Reason, tolerance.SetBy, tolerance.SetAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<TransformationInfo>> ListTransformationsAsync(long? orderNumber, string? partNumber, string? lotNumber, CancellationToken ct)
    {
        var headers = await ReadAsync(
            """
            SELECT t.transformation_id, t.order_number, t.operation_sequence, t.reason, t.recorded_at, t.actor
            FROM inventory.transformation t
            WHERE ($1::bigint IS NULL OR t.order_number = $1)
              AND ($2::text IS NULL OR EXISTS (
                    SELECT 1 FROM inventory.transformation_line l
                    WHERE l.transformation_id = t.transformation_id
                      AND ((l.part_number = $2 AND ($3::text IS NULL OR l.lot_number = $3)) OR (l.source_part = $2 AND ($3::text IS NULL OR l.source_lot = $3)))))
            ORDER BY t.transformation_id
            """,
            r => (Id: r.GetInt64(0), Order: r.GetInt64(1), Sequence: r.IsDBNull(2) ? (int?)null : r.GetInt32(2), Reason: NullableText(r, 3), At: r.GetFieldValue<DateTimeOffset>(4), Actor: r.GetString(5)),
            ct,
            Long(orderNumber), Text(partNumber), Text(lotNumber));
        if (headers.Count == 0)
        {
            return [];
        }

        var lines = (await ReadAsync(
            """
            SELECT transformation_id, line_number, role, part_number, lot_number, quantity, source_part, source_lot, location_code, transaction_id
            FROM inventory.transformation_line WHERE transformation_id = ANY($1) ORDER BY transformation_id, line_number
            """,
            r => (Id: r.GetInt64(0), Line: new TransformationLine(r.GetInt32(1), Enum.Parse<TransformationRole>(r.GetString(2)), r.GetString(3), NullableText(r, 4), Dec(r, 5),
                NullableText(r, 6), NullableText(r, 7), NullableText(r, 8), r.IsDBNull(9) ? null : r.GetInt64(9))),
            ct,
            headers.Select(h => h.Id).ToArray())).ToLookup(x => x.Id, x => x.Line);
        return headers.Select(h => new TransformationInfo(h.Id, h.Order, h.Sequence, h.Reason, h.At, h.Actor, lines[h.Id].ToList())).ToList();
    }

    public Task<IReadOnlyList<YieldTolerance>> ListYieldTolerancesAsync(CancellationToken ct) =>
        ReadAsync(
            """
            SELECT DISTINCT ON (part_number) part_number, percent, reason, set_by, set_at
            FROM inventory.yield_tolerance ORDER BY part_number, tolerance_id DESC
            """,
            r => new YieldTolerance(r.GetString(0), Dec(r, 1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateTimeOffset>(4)),
            ct);

    public Task<IReadOnlyList<InventoryTransaction>> ListGenealogyTransactionsAsync(CancellationToken ct) =>
        ReadAsync(
            TransactionSql + " WHERE kind IN ('Receipt', 'Issue', 'Return', 'ProductionReceipt', 'Remnant', 'Byproduct', 'Shipment', 'MaintenanceIssue') ORDER BY transaction_id",
            ReadTransaction,
            ct);
}
