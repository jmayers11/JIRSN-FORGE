using Forge.Inventory.Contracts;
using Forge.Kernel.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Forge.Inventory.Infrastructure;

internal sealed partial class PostgresInventoryStore(PostgresUnitOfWork uow) : IInventoryStore, IInventoryLookup
{
    private const string TransactionSql =
        "SELECT transaction_id, kind, part_number, quantity, from_location, from_status, to_location, to_status, lot_number, order_number, po_number, po_line, reason, at, actor, approved_by, request_id, shipment_id, work_order_id, part_revision, substitutes_for, alternate_id, primary_quantity, project_code, project_line FROM inventory.transaction";

    private const string ShipmentSql =
        "SELECT shipment_id, customer, reference, demand_id, ship_to, status, created_by, created_at, packed_by, packed_at, shipped_by, shipped_at, carrier, tracking, cancel_reason FROM inventory.shipment";

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
                order_number, po_number, po_line, reason, request_id, at, actor, approved_by, shipment_id, work_order_id, part_revision, substitutes_for, alternate_id, primary_quantity,
                project_code, project_line)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20, $21, $22, $23, $24)
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
            Text(transaction.ApprovedBy),
            Long(transaction.ShipmentId),
            Long(transaction.WorkOrderId),
            Text(transaction.PartRevision),
            Text(transaction.SubstitutesFor),
            Long(transaction.AlternateId),
            new NpgsqlParameter { Value = (object?)transaction.PrimaryQuantity ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Numeric },
            Text(transaction.ProjectCode),
            new NpgsqlParameter { Value = (object?)transaction.ProjectLine ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer });
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task EnsureLotAsync(string partNumber, string lotNumber, string? supplierLot, IdentityKind kind, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "INSERT INTO inventory.lot (part_number, lot_number, supplier_lot, first_received_at, created_by, identity_kind) VALUES ($1, $2, $3, $4, $5, $6) ON CONFLICT DO NOTHING",
            partNumber, lotNumber, Text(supplierLot), at, actor, kind.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertInstanceLinkAsync(InstanceLink link, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(link);
        await using var cmd = uow.Command(
            """
            INSERT INTO inventory.instance_link (relation, source_part, source_lot, result_part, result_lot, quantity, out_transaction_id, in_transaction_id, reason, at, actor)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
            """,
            link.Relation.ToString(), link.SourcePart, link.SourceLot, link.ResultPart, link.ResultLot, link.Quantity, link.OutTransactionId, link.InTransactionId,
            Text(link.Reason), link.At, link.Actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertMergePolicyAsync(MergePolicy policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);
        await using var cmd = uow.Command(
            """
            INSERT INTO inventory.merge_policy (part_number, allowed, same_revision_required, same_supplier_lot_required, reason, set_by, set_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            """,
            policy.PartNumber, policy.Allowed, policy.SameRevisionRequired, policy.SameSupplierLotRequired, policy.Reason, policy.SetBy, policy.SetAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task<IReadOnlyList<InstanceLink>> ListInstanceLinksAsync(string? partNumber, string? lotNumber, CancellationToken ct) =>
        ReadAsync(
            """
            SELECT link_id, relation, source_part, source_lot, result_part, result_lot, quantity, out_transaction_id, in_transaction_id, reason, at, actor
            FROM inventory.instance_link
            WHERE $1::text IS NULL
               OR (source_part = $1 AND ($2::text IS NULL OR source_lot = $2))
               OR (result_part = $1 AND ($2::text IS NULL OR result_lot = $2))
            ORDER BY link_id
            """,
            r => new InstanceLink(r.GetInt64(0), Enum.Parse<InstanceRelation>(r.GetString(1)), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), Dec(r, 6),
                r.GetInt64(7), r.GetInt64(8), NullableText(r, 9), r.GetFieldValue<DateTimeOffset>(10), r.GetString(11)),
            ct,
            Text(partNumber), Text(lotNumber));

    public async Task<MergePolicy?> FindMergePolicyAsync(string partNumber, CancellationToken ct) =>
        (await ReadAsync(
            """
            SELECT part_number, allowed, same_revision_required, same_supplier_lot_required, reason, set_by, set_at
            FROM inventory.merge_policy WHERE part_number = $1 ORDER BY policy_id DESC LIMIT 1
            """,
            r => new MergePolicy(r.GetString(0), r.GetBoolean(1), r.GetBoolean(2), r.GetBoolean(3), r.GetString(4), r.GetString(5), r.GetFieldValue<DateTimeOffset>(6)),
            ct,
            partNumber)).SingleOrDefault();

    public async Task SetLotRevisionAsync(string partNumber, string lotNumber, string revision, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE inventory.lot SET part_revision = $3 WHERE part_number = $1 AND lot_number = $2 AND part_revision IS NULL",
            partNumber, lotNumber, revision);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> InsertLocationAsync(LocationInfo location, DateTimeOffset at, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(location);
        await using var cmd = uow.Command(
            """
            INSERT INTO inventory.location (code, site_code, name, kind, active, created_at, created_by, parent_code, level, nettable, work_center_code, machine_code, custodian_supplier)
            VALUES ($1, $2, $3, $4, true, $5, $6, $7, $8, $9, $10, $11, $12) ON CONFLICT DO NOTHING
            """,
            location.Code, location.SiteCode, location.Name, location.Kind.ToString(), at, actor, Text(location.ParentCode), location.Level.ToString(), location.Nettable,
            Text(location.WorkCenterCode), Text(location.MachineCode), Text(location.CustodianSupplier));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task UpdateLocationAsync(LocationInfo location, DateTimeOffset at, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(location);
        await using var cmd = uow.Command(
            """
            UPDATE inventory.location SET name = $2, parent_code = $3, level = $4, nettable = $5, work_center_code = $6, machine_code = $7, custodian_supplier = $8,
                changed_at = $9, changed_by = $10
            WHERE code = $1
            """,
            location.Code, location.Name, Text(location.ParentCode), location.Level.ToString(), location.Nettable, Text(location.WorkCenterCode), Text(location.MachineCode),
            Text(location.CustodianSupplier), at, actor);
        await cmd.ExecuteNonQueryAsync(ct);
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
            "SELECT code, site_code, name, kind, active, parent_code, level, nettable, work_center_code, machine_code, custodian_supplier FROM inventory.location ORDER BY code",
            r => new LocationInfo(r.GetString(0), r.GetString(1), r.GetString(2), Enum.Parse<LocationKind>(r.GetString(3)), r.GetBoolean(4),
                NullableText(r, 5), Enum.Parse<LocationLevel>(r.GetString(6)), r.GetBoolean(7), NullableText(r, 8), NullableText(r, 9), NullableText(r, 10)),
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

    public Task<IReadOnlyList<InventoryTransaction>> ListTransactionsBetweenAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        ReadAsync(TransactionSql + " WHERE at >= $1 AND at < $2 ORDER BY at, transaction_id", ReadTransaction, ct, from, to);

    public Task<IReadOnlyList<ShipmentInfo>> ListShippedForDemandsAsync(IReadOnlyCollection<long> demandIds, CancellationToken ct) =>
        ShipmentsAsync(ShipmentSql + " WHERE status = 'Shipped' AND demand_id = ANY($1) ORDER BY shipped_at, shipment_id", ct, demandIds.ToArray());

    public Task<IReadOnlyList<LotInfo>> ListLotsAsync(string? partNumber, CancellationToken ct) =>
        ReadAsync(
            "SELECT part_number, lot_number, supplier_lot, first_received_at, part_revision, identity_kind FROM inventory.lot WHERE ($1::text IS NULL OR part_number = $1) ORDER BY part_number, first_received_at, lot_number",
            r => new LotInfo(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetFieldValue<DateTimeOffset>(3), r.IsDBNull(4) ? null : r.GetString(4), Enum.Parse<IdentityKind>(r.GetString(5))),
            ct,
            Text(partNumber));

    public Task<IReadOnlyList<OrderIssue>> ListOrderIssuesAsync(CancellationToken ct) =>
        ReadAsync(
            """
            SELECT order_number, COALESCE(substitutes_for, part_number), sum(CASE WHEN kind = 'Issue' THEN 1 ELSE -1 END * COALESCE(primary_quantity, quantity))
            FROM inventory.transaction WHERE kind IN ('Issue', 'Return') AND order_number IS NOT NULL
            GROUP BY 1, 2 ORDER BY 1, 2
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

    public async Task<long> InsertShipmentAsync(ShipmentInfo shipment, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(shipment);
        long id;
        await using (var cmd = uow.Command(
            "INSERT INTO inventory.shipment (customer, reference, demand_id, ship_to, status, created_by, created_at) VALUES ($1, $2, $3, $4, $5, $6, $7) RETURNING shipment_id",
            shipment.Customer, Text(shipment.Reference), Long(shipment.DemandId), Text(shipment.ShipTo), shipment.Status.ToString(), shipment.CreatedBy, shipment.CreatedAt))
        {
            id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }

        foreach (var line in shipment.Lines)
        {
            await using var cmd = uow.Command(
                "INSERT INTO inventory.shipment_line (shipment_id, line_number, part_number, quantity, location_code, lot_number) VALUES ($1, $2, $3, $4, $5, $6)",
                id, line.LineNumber, line.PartNumber, line.Quantity, line.LocationCode, Text(line.LotNumber));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        return id;
    }

    public async Task UpdateShipmentAsync(ShipmentInfo shipment, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(shipment);
        await using var cmd = uow.Command(
            "UPDATE inventory.shipment SET status = $2, packed_by = $3, packed_at = $4, shipped_by = $5, shipped_at = $6, carrier = $7, tracking = $8, cancel_reason = $9 WHERE shipment_id = $1",
            shipment.ShipmentId, shipment.Status.ToString(), Text(shipment.PackedBy), Time(shipment.PackedAt), Text(shipment.ShippedBy), Time(shipment.ShippedAt),
            Text(shipment.Carrier), Text(shipment.Tracking), Text(shipment.CancelReason));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<ShipmentInfo?> FindShipmentAsync(long shipmentId, CancellationToken ct) =>
        (await ShipmentsAsync(ShipmentSql + " WHERE shipment_id = $1", ct, shipmentId)).SingleOrDefault();

    public Task<IReadOnlyList<ShipmentInfo>> ListShipmentsAsync(bool includeClosed, CancellationToken ct) =>
        ShipmentsAsync(ShipmentSql + (includeClosed ? string.Empty : " WHERE status IN ('Open', 'Packed')") + " ORDER BY shipment_id DESC LIMIT 500", ct);

    public Task<IReadOnlyList<ProjectMaterial>> ListProjectMaterialAsync(string? projectCode, CancellationToken ct) =>
        ReadAsync(
            """
            SELECT project_code, project_line, part_number,
                   coalesce(sum(quantity) FILTER (WHERE kind = 'Receipt'), 0),
                   coalesce(sum(quantity) FILTER (WHERE kind = 'ProjectIssue'), 0) - coalesce(sum(quantity) FILTER (WHERE kind = 'ProjectReturn'), 0)
            FROM inventory.transaction WHERE project_code IS NOT NULL AND ($1::text IS NULL OR project_code = $1)
            GROUP BY project_code, project_line, part_number ORDER BY project_code, project_line, part_number
            """,
            r => new ProjectMaterial(r.GetString(0), r.GetInt32(1), r.GetString(2), Dec(r, 3), Dec(r, 4)),
            ct,
            Text(projectCode));

    public Task<IReadOnlyList<WorkOrderPart>> ListWorkOrderPartsAsync(long? workOrderId, CancellationToken ct) =>
        ReadAsync(
            """
            SELECT work_order_id, part_number, sum(CASE WHEN kind = 'MaintenanceIssue' THEN quantity ELSE -quantity END), substitutes_for,
                   sum(CASE WHEN kind = 'MaintenanceIssue' THEN primary_quantity ELSE -primary_quantity END)
            FROM inventory.transaction WHERE work_order_id IS NOT NULL AND ($1::bigint IS NULL OR work_order_id = $1)
            GROUP BY work_order_id, part_number, substitutes_for ORDER BY work_order_id, part_number, substitutes_for NULLS FIRST
            """,
            r => new WorkOrderPart(r.GetInt64(0), r.GetString(1), Dec(r, 2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : Dec(r, 4)),
            ct,
            Long(workOrderId));

    public Task<IReadOnlyList<DemandShipped>> ListShippedByDemandAsync(CancellationToken ct) =>
        ReadAsync(
            """
            SELECT s.demand_id, t.part_number, sum(t.quantity)
            FROM inventory.transaction t JOIN inventory.shipment s ON s.shipment_id = t.shipment_id
            WHERE t.kind = 'Shipment' AND s.demand_id IS NOT NULL
            GROUP BY s.demand_id, t.part_number ORDER BY s.demand_id, t.part_number
            """,
            r => new DemandShipped(r.GetInt64(0), r.GetString(1), Dec(r, 2)),
            ct);

    private async Task<IReadOnlyList<ShipmentInfo>> ShipmentsAsync(string sql, CancellationToken ct, params object?[] args)
    {
        var heads = await ReadAsync(
            sql,
            r => new ShipmentInfo(
                r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetInt64(3), r.IsDBNull(4) ? null : r.GetString(4),
                Enum.Parse<ShipmentStatus>(r.GetString(5)), r.GetString(6), r.GetFieldValue<DateTimeOffset>(7),
                r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9),
                r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11),
                r.IsDBNull(12) ? null : r.GetString(12), r.IsDBNull(13) ? null : r.GetString(13), r.IsDBNull(14) ? null : r.GetString(14), []),
            ct,
            args);
        if (heads.Count == 0)
        {
            return heads;
        }

        var lines = await ReadAsync(
            "SELECT shipment_id, line_number, part_number, quantity, location_code, lot_number FROM inventory.shipment_line WHERE shipment_id = ANY($1) ORDER BY shipment_id, line_number",
            r => (Id: r.GetInt64(0), Line: new ShipmentLineInfo(r.GetInt32(1), r.GetString(2), Dec(r, 3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5))),
            ct,
            heads.Select(h => h.ShipmentId).ToArray());
        var byId = lines.ToLookup(l => l.Id, l => l.Line);
        return heads.Select(h => h with { Lines = byId[h.ShipmentId].ToList() }).ToList();
    }

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
        r.IsDBNull(16) ? null : r.GetInt64(16),
        r.IsDBNull(17) ? null : r.GetInt64(17),
        r.IsDBNull(18) ? null : r.GetInt64(18),
        r.IsDBNull(19) ? null : r.GetString(19),
        r.IsDBNull(20) ? null : r.GetString(20),
        r.IsDBNull(21) ? null : r.GetInt64(21),
        r.IsDBNull(22) ? null : Dec(r, 22),
        r.IsDBNull(23) ? null : r.GetString(23),
        r.IsDBNull(24) ? null : r.GetInt32(24));

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

    private static string? NullableText(NpgsqlDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : r.GetString(ordinal);

    private static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text };

    private static NpgsqlParameter Time(DateTimeOffset? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz };

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
