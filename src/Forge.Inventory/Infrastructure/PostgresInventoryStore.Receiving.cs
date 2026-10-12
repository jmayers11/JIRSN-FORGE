using System.Text.Json;
using Forge.Inventory.Contracts;
using Forge.Quality.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Inventory.Infrastructure;

/// <summary>Receiving inspections, supplier rejections, returns to vendor and customer returns (V3.11).</summary>
internal sealed partial class PostgresInventoryStore : IReceivingLookup, IReceivingStore
{
    private static readonly JsonSerializerOptions ResultJson = new(JsonSerializerDefaults.Web);

    private const string InspectionSql =
        """
        SELECT inspection_id, receipt_transaction_id, part_number, lot_number, location_code, po_number, supplier_code, accepted, rejected, results::text, fai_status, inspector, at
        FROM inventory.receiving_inspection
        """;

    private const string RejectionSql =
        """
        SELECT rejection_id, inspection_id, part_number, lot_number, location_code, supplier_code, po_number, quantity, defect, disposition, dispositioned_by, dispositioned_at, approved_by, approved_at
        FROM inventory.supplier_rejection
        """;

    private const string RtvSql =
        """
        SELECT rtv_id, rejection_id, supplier_code, part_number, lot_number, location_code, quantity, for_rework, status, shipped_transaction_id, shipped_by, shipped_at,
               replacement_lot, replacement_transaction_id, closed_by, closed_at, note
        FROM inventory.rtv
        """;

    private const string ReturnSql =
        """
        SELECT rma_id, shipment_id, customer, part_number, lot_number, quantity, location_code, reason, receipt_transaction_id, received_by, received_at,
               disposition, dispositioned_by, dispositioned_at, disposition_transaction_id
        FROM inventory.customer_return
        """;

    public async Task<InventoryTransaction?> FindTransactionAsync(long transactionId, CancellationToken ct) =>
        (await ReadAsync(TransactionSql + " WHERE transaction_id = $1", ReadTransaction, ct, transactionId)).SingleOrDefault();

    public Task<IReadOnlyList<ReceivingInspection>> ListReceivingInspectionsAsync(long? receiptTransactionId, CancellationToken ct) =>
        ReadAsync(InspectionSql + " WHERE $1::bigint IS NULL OR receipt_transaction_id = $1 ORDER BY inspection_id", ReadInspection, ct, Long(receiptTransactionId));

    public Task<IReadOnlyList<InventoryTransaction>> ListInspectionReceiptsAsync(int take, CancellationToken ct) =>
        ReadAsync(TransactionSql + " WHERE (kind = 'Receipt' OR transaction_id IN (SELECT transaction_id FROM inventory.outside_process_return)) AND to_status = 'Inspection' ORDER BY transaction_id DESC LIMIT $1", ReadTransaction, ct, Math.Clamp(take, 1, 1000));

    public Task<IReadOnlyList<SupplierRejection>> ListRejectionsAsync(bool openOnly, CancellationToken ct) =>
        ReadAsync(RejectionSql + " WHERE NOT $1 OR disposition = 'Pending' OR (disposition = 'UseAsIs' AND approved_by IS NULL) ORDER BY rejection_id", ReadRejection, ct, openOnly);

    public Task<IReadOnlyList<RtvInfo>> ListRtvsAsync(bool openOnly, CancellationToken ct) =>
        ReadAsync(RtvSql + " WHERE NOT $1 OR status IN ('Open', 'Shipped') ORDER BY rtv_id", ReadRtv, ct, openOnly);

    public Task<IReadOnlyList<CustomerReturnInfo>> ListCustomerReturnsAsync(long? shipmentId, CancellationToken ct) =>
        ReadAsync(ReturnSql + " WHERE $1::bigint IS NULL OR shipment_id = $1 ORDER BY rma_id", ReadReturn, ct, Long(shipmentId));

    public async Task<long> InsertReceivingInspectionAsync(ReceivingInspection inspection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        await using var cmd = uow.Command(
            """
            INSERT INTO inventory.receiving_inspection (receipt_transaction_id, part_number, lot_number, location_code, po_number, supplier_code, accepted, rejected, results, fai_status, inspector, at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12) RETURNING inspection_id
            """,
            inspection.ReceiptTransactionId, inspection.PartNumber, Text(inspection.LotNumber), inspection.LocationCode, Long(inspection.PoNumber), Text(inspection.SupplierCode),
            inspection.Accepted, inspection.Rejected, new NpgsqlParameter { Value = JsonSerializer.Serialize(inspection.Results, ResultJson), NpgsqlDbType = NpgsqlDbType.Jsonb },
            inspection.FaiStatus.ToString(), inspection.Inspector, inspection.At);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<long> InsertRejectionAsync(SupplierRejection rejection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rejection);
        await using var cmd = uow.Command(
            """
            INSERT INTO inventory.supplier_rejection (inspection_id, part_number, lot_number, location_code, supplier_code, po_number, quantity, defect, disposition)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, 'Pending') RETURNING rejection_id
            """,
            rejection.InspectionId, rejection.PartNumber, Text(rejection.LotNumber), rejection.LocationCode, Text(rejection.SupplierCode), Long(rejection.PoNumber), rejection.Quantity, rejection.Defect);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task UpdateRejectionAsync(SupplierRejection rejection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rejection);
        await using var cmd = uow.Command(
            "UPDATE inventory.supplier_rejection SET disposition = $2, dispositioned_by = $3, dispositioned_at = $4, approved_by = $5, approved_at = $6 WHERE rejection_id = $1",
            rejection.RejectionId, rejection.Disposition.ToString(), Text(rejection.DispositionedBy), Time(rejection.DispositionedAt), Text(rejection.ApprovedBy), Time(rejection.ApprovedAt));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> InsertRtvAsync(RtvInfo rtv, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rtv);
        await using var cmd = uow.Command(
            """
            INSERT INTO inventory.rtv (rejection_id, supplier_code, part_number, lot_number, location_code, quantity, for_rework, status)
            VALUES ($1, $2, $3, $4, $5, $6, $7, 'Open') RETURNING rtv_id
            """,
            rtv.RejectionId, Text(rtv.SupplierCode), rtv.PartNumber, Text(rtv.LotNumber), rtv.LocationCode, rtv.Quantity, rtv.ForRework);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task UpdateRtvAsync(RtvInfo rtv, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rtv);
        await using var cmd = uow.Command(
            """
            UPDATE inventory.rtv SET status = $2, shipped_transaction_id = $3, shipped_by = $4, shipped_at = $5, replacement_lot = $6, replacement_transaction_id = $7,
                closed_by = $8, closed_at = $9, note = $10
            WHERE rtv_id = $1
            """,
            rtv.RtvId, rtv.Status.ToString(), Long(rtv.ShippedTransactionId), Text(rtv.ShippedBy), Time(rtv.ShippedAt), Text(rtv.ReplacementLot), Long(rtv.ReplacementTransactionId),
            Text(rtv.ClosedBy), Time(rtv.ClosedAt), Text(rtv.Note));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> InsertCustomerReturnAsync(CustomerReturnInfo customerReturn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(customerReturn);
        await using var cmd = uow.Command(
            """
            INSERT INTO inventory.customer_return (shipment_id, customer, part_number, lot_number, quantity, location_code, reason, receipt_transaction_id, received_by, received_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10) RETURNING rma_id
            """,
            customerReturn.ShipmentId, customerReturn.Customer, customerReturn.PartNumber, Text(customerReturn.LotNumber), customerReturn.Quantity, customerReturn.LocationCode,
            customerReturn.Reason, customerReturn.ReceiptTransactionId, customerReturn.ReceivedBy, customerReturn.ReceivedAt);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task UpdateCustomerReturnAsync(CustomerReturnInfo customerReturn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(customerReturn);
        await using var cmd = uow.Command(
            "UPDATE inventory.customer_return SET disposition = $2, dispositioned_by = $3, dispositioned_at = $4, disposition_transaction_id = $5 WHERE rma_id = $1 AND disposition IS NULL",
            customerReturn.RmaId, Text(customerReturn.Disposition?.ToString()), Text(customerReturn.DispositionedBy), Time(customerReturn.DispositionedAt), Long(customerReturn.DispositionTransactionId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static ReceivingInspection ReadInspection(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetString(2), NullableText(r, 3), r.GetString(4), r.IsDBNull(5) ? null : r.GetInt64(5), NullableText(r, 6),
        r.GetDecimal(7) / 1.0000000000000000000000000000m, r.GetDecimal(8) / 1.0000000000000000000000000000m,
        JsonSerializer.Deserialize<List<ReceivingCharacteristicResult>>(r.GetString(9), ResultJson) ?? [], Enum.Parse<FaiStatus>(r.GetString(10)), r.GetString(11), r.GetFieldValue<DateTimeOffset>(12));

    private static SupplierRejection ReadRejection(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetString(2), NullableText(r, 3), r.GetString(4), NullableText(r, 5), r.IsDBNull(6) ? null : r.GetInt64(6),
        r.GetDecimal(7) / 1.0000000000000000000000000000m, r.GetString(8), Enum.Parse<SupplierDisposition>(r.GetString(9)), NullableText(r, 10),
        r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11), NullableText(r, 12), r.IsDBNull(13) ? null : r.GetFieldValue<DateTimeOffset>(13));

    private static RtvInfo ReadRtv(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), NullableText(r, 2), r.GetString(3), NullableText(r, 4), r.GetString(5), r.GetDecimal(6) / 1.0000000000000000000000000000m, r.GetBoolean(7),
        Enum.Parse<RtvStatus>(r.GetString(8)), r.IsDBNull(9) ? null : r.GetInt64(9), NullableText(r, 10), r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11),
        NullableText(r, 12), r.IsDBNull(13) ? null : r.GetInt64(13), NullableText(r, 14), r.IsDBNull(15) ? null : r.GetFieldValue<DateTimeOffset>(15), NullableText(r, 16));

    private static CustomerReturnInfo ReadReturn(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), NullableText(r, 4), r.GetDecimal(5) / 1.0000000000000000000000000000m, r.GetString(6), r.GetString(7),
        r.GetInt64(8), r.GetString(9), r.GetFieldValue<DateTimeOffset>(10), r.IsDBNull(11) ? null : Enum.Parse<CustomerReturnDisposition>(r.GetString(11)), NullableText(r, 12),
        r.IsDBNull(13) ? null : r.GetFieldValue<DateTimeOffset>(13), r.IsDBNull(14) ? null : r.GetInt64(14));
}

