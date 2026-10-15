using Forge.Kernel.Postgres;
using Forge.Procurement.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Forge.Procurement.Infrastructure;

internal sealed class PostgresProcurementStore(PostgresUnitOfWork uow) : IProcurementStore, IProcurementLookup, ITechnicalPackageLookup, IRequisitionLookup
{
    public async Task LockPurchaseOrderAsync(long poNumber,CancellationToken ct)
    {
        await using var command=uow.Command("SELECT po_number FROM procurement.purchase_order WHERE po_number=$1 FOR UPDATE",poNumber);
        await command.ExecuteScalarAsync(ct);
    }
    private const string OrderSql = "SELECT po_number, supplier, status, created_by, created_at, approved_by, approved_at, supplier_code FROM procurement.purchase_order";
    private const string LineSql = "SELECT po_number, line_number, part_number, quantity, unit_of_measure, promised_date, status, requisition_id, source_kind, source_key, source_label FROM procurement.purchase_order_line";

    private const string RequisitionSql =
        """
        SELECT requisition_id, part_number, quantity, need_date, source_kind, source_key, source_label, demand_id, planned_order, required_revision, supplier_code, program_id,
               status, requested_by, requested_at, po_number, po_line, ordered_by, ordered_at, cancelled_by, cancelled_at, cancelled_reason
        FROM procurement.requisition
        """;

    public async Task<long> InsertOrderAsync(SupplierInfo supplier, DateTimeOffset at, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(supplier);
        await using var cmd = uow.Command(
            "INSERT INTO procurement.purchase_order (supplier, supplier_code, status, created_at, created_by) VALUES ($1, $2, 'Draft', $3, $4) RETURNING po_number",
            supplier.Name, supplier.Code, at, actor);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<bool> InsertLineAsync(long poNumber, PurchaseLineInfo line, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(line);
        await using var cmd = uow.Command(
            """
            INSERT INTO procurement.purchase_order_line (po_number, line_number, part_number, quantity, unit_of_measure, promised_date, status, requisition_id, source_kind, source_key, source_label)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11) ON CONFLICT DO NOTHING
            """,
            poNumber, line.LineNumber, line.PartNumber, line.Quantity, line.UnitOfMeasure, line.PromisedDate, line.Status.ToString(),
            new NpgsqlParameter { Value = (object?)line.RequisitionId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint },
            Text(line.SourceKind?.ToString()), Text(line.SourceKey), Text(line.SourceLabel));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task SetStatusAsync(long poNumber, PurchaseOrderStatus status, DateTimeOffset at, string actor, CancellationToken ct)
    {
        var sql = status switch
        {
            PurchaseOrderStatus.Approved => "UPDATE procurement.purchase_order SET status = $2, approved_at = $3, approved_by = $4 WHERE po_number = $1",
            _ => "UPDATE procurement.purchase_order SET status = $2, closed_at = $3, closed_by = $4 WHERE po_number = $1",
        };
        await using var cmd = uow.Command(sql, poNumber, status.ToString(), at, actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateLineAsync(long poNumber, PurchaseLineInfo line, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(line);
        await using var cmd = uow.Command(
            "UPDATE procurement.purchase_order_line SET promised_date = $3, status = $4 WHERE po_number = $1 AND line_number = $2",
            poNumber, line.LineNumber, line.PromisedDate, line.Status.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<PurchaseOrderInfo>> ListPurchaseOrdersAsync(CancellationToken ct)
    {
        var orders = await ReadAsync(OrderSql + " ORDER BY po_number DESC", ReadOrder, ct);
        var lines = (await ReadAsync(LineSql + " ORDER BY po_number, line_number", ReadLine, ct)).ToLookup(l => l.PoNumber, l => l.Line);
        return orders.Select(o => o with { Lines = lines[o.PoNumber].ToList() }).ToList();
    }

    public async Task<PurchaseOrderInfo?> FindPurchaseOrderAsync(long poNumber, CancellationToken ct)
    {
        var order = (await ReadAsync(OrderSql + " WHERE po_number = $1", ReadOrder, ct, poNumber)).SingleOrDefault();
        if (order is null)
        {
            return null;
        }

        var lines = await ReadAsync(LineSql + " WHERE po_number = $1 ORDER BY line_number", ReadLine, ct, poNumber);
        return order with { Lines = lines.Select(l => l.Line).ToList() };
    }

    private static PurchaseOrderInfo ReadOrder(NpgsqlDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        Enum.Parse<PurchaseOrderStatus>(r.GetString(2)),
        r.GetString(3),
        r.GetFieldValue<DateTimeOffset>(4),
        r.IsDBNull(5) ? null : r.GetString(5),
        r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6),
        [],
        r.GetString(7));

    private const string SupplierSql = "SELECT code, name, status, notes, created_by, created_at FROM procurement.supplier";

    public Task<IReadOnlyList<SupplierInfo>> ListSuppliersAsync(CancellationToken ct) =>
        ReadAsync(SupplierSql + " ORDER BY lower(name), code", ReadSupplier, ct);

    public async Task<SupplierInfo?> FindSupplierAsync(string code, CancellationToken ct) =>
        (await ReadAsync(SupplierSql + " WHERE code = $1", ReadSupplier, ct, code)).SingleOrDefault();

    public Task<IReadOnlyList<ApprovedSourceInfo>> ListApprovedSourcesAsync(string? partNumber, CancellationToken ct) =>
        ReadAsync(
            "SELECT part_number, supplier_code, note, approved_by, approved_at FROM procurement.approved_source WHERE ($1::text IS NULL OR part_number = $1) ORDER BY part_number, supplier_code",
            r => new ApprovedSourceInfo(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetFieldValue<DateTimeOffset>(4)),
            ct,
            Text(partNumber));

    public async Task<bool> InsertSupplierAsync(SupplierInfo supplier, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(supplier);
        await using var cmd = uow.Command(
            "INSERT INTO procurement.supplier (code, name, status, notes, created_at, created_by) VALUES ($1, $2, $3, $4, $5, $6) ON CONFLICT DO NOTHING",
            supplier.Code, supplier.Name, supplier.Status.ToString(), Text(supplier.Notes), supplier.CreatedAt, supplier.CreatedBy);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> NameTakenAsync(string name, string exceptCode, CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT EXISTS (SELECT 1 FROM procurement.supplier WHERE lower(name) = lower($1) AND code <> $2)", name, exceptCode);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task UpdateSupplierAsync(SupplierInfo supplier, DateTimeOffset at, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(supplier);
        await using var cmd = uow.Command(
            "UPDATE procurement.supplier SET name = $2, status = $3, notes = $4, changed_at = $5, changed_by = $6 WHERE code = $1",
            supplier.Code, supplier.Name, supplier.Status.ToString(), Text(supplier.Notes), at, actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> InsertApprovedSourceAsync(ApprovedSourceInfo source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        await using var cmd = uow.Command(
            "INSERT INTO procurement.approved_source (part_number, supplier_code, note, approved_by, approved_at) VALUES ($1, $2, $3, $4, $5) ON CONFLICT DO NOTHING",
            source.PartNumber, source.SupplierCode, Text(source.Note), source.ApprovedBy, source.ApprovedAt);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> DeleteApprovedSourceAsync(string partNumber, string supplierCode, CancellationToken ct)
    {
        await using var cmd = uow.Command("DELETE FROM procurement.approved_source WHERE part_number = $1 AND supplier_code = $2", partNumber, supplierCode);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task InsertPackageAsync(TechnicalPackage package, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(package);
        await using (var cmd = uow.Command(
            "INSERT INTO procurement.line_package (po_number, line_number, part_number, item_revision, summary, recorded_at, recorded_by) VALUES ($1, $2, $3, $4, $5, $6, $7)",
            package.PoNumber, package.LineNumber, package.PartNumber, Text(package.ItemRevision), package.Summary, package.RecordedAt, package.RecordedBy))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var position = 0;
        foreach (var d in package.Documents)
        {
            await using var cmd = uow.Command(
                """
                INSERT INTO procurement.line_package_document (po_number, line_number, position, document_number, title, revision, state, source, file_sha256, file_name)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
                """,
                package.PoNumber, package.LineNumber, ++position, d.DocumentNumber, d.Title, Text(d.Revision), d.State, d.Source, Text(d.FileSha256), Text(d.FileName));
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<TechnicalPackage?> FindPackageAsync(long poNumber, int lineNumber, CancellationToken ct) =>
        (await PackagesAsync(" WHERE p.po_number = $1 AND p.line_number = $2", ct, poNumber, lineNumber)).SingleOrDefault();

    public Task<IReadOnlyList<TechnicalPackage>> ListPackagesAsync(IReadOnlyCollection<string> partNumbers, CancellationToken ct) =>
        PackagesAsync(" WHERE p.part_number = ANY($1)", ct, new object?[] { partNumbers.ToArray() });

    private async Task<IReadOnlyList<TechnicalPackage>> PackagesAsync(string where, CancellationToken ct, params object?[] args)
    {
        var packages = await ReadAsync(
            "SELECT p.po_number, p.line_number, p.part_number, p.item_revision, p.summary, p.recorded_at, p.recorded_by FROM procurement.line_package p" + where + " ORDER BY p.po_number, p.line_number",
            r => new TechnicalPackage(r.GetInt64(0), r.GetInt32(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetFieldValue<DateTimeOffset>(5), r.GetString(6), []),
            ct,
            args);
        if (packages.Count == 0)
        {
            return packages;
        }

        var documents = (await ReadAsync(
            "SELECT d.po_number, d.line_number, d.document_number, d.title, d.revision, d.state, d.source, d.file_sha256, d.file_name FROM procurement.line_package_document d JOIN procurement.line_package p USING (po_number, line_number)" + where + " ORDER BY d.po_number, d.line_number, d.position",
            r => (Key: (r.GetInt64(0), r.GetInt32(1)), Document: new PackageDocument(r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8))),
            ct,
            args)).ToLookup(x => x.Key, x => x.Document);
        return packages.Select(p => p with { Documents = documents[(p.PoNumber, p.LineNumber)].ToList() }).ToList();
    }

    private static SupplierInfo ReadSupplier(NpgsqlDataReader r) =>
        new(r.GetString(0), r.GetString(1), Enum.Parse<SupplierStatus>(r.GetString(2)), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetFieldValue<DateTimeOffset>(5));

    private static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text };

    private static (long PoNumber, PurchaseLineInfo Line) ReadLine(NpgsqlDataReader r) => (
        r.GetInt64(0),
        new PurchaseLineInfo(r.GetInt32(1), r.GetString(2), (r.GetDecimal(3) / 1.000000000000000000000000000000000m), r.GetString(4), r.GetFieldValue<DateOnly>(5), Enum.Parse<PurchaseLineStatus>(r.GetString(6)),
            r.IsDBNull(7) ? null : r.GetInt64(7), r.IsDBNull(8) ? null : Enum.Parse<PurchaseSourceKind>(r.GetString(8)), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10)));

    public async Task<long> InsertRequisitionAsync(RequisitionInfo q, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(q);
        await using var cmd = uow.Command(
            """
            INSERT INTO procurement.requisition (part_number, quantity, need_date, source_kind, source_key, source_label, demand_id, planned_order, required_revision, supplier_code,
                program_id, status, requested_by, requested_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, 'Open', $12, $13)
            RETURNING requisition_id
            """,
            q.PartNumber, q.Quantity, q.NeedDate, q.SourceKind.ToString(), q.SourceKey, q.SourceLabel,
            new NpgsqlParameter { Value = (object?)q.DemandId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint },
            Text(q.PlannedOrder), Text(q.RequiredRevision), Text(q.SupplierCode),
            new NpgsqlParameter { Value = (object?)q.ProgramId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid },
            q.RequestedBy, q.RequestedAt);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task SetRequisitionOrderedAsync(long requisitionId, long? poNumber, int? poLine, string? actor, DateTimeOffset? at, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE procurement.requisition SET status = CASE WHEN $2::bigint IS NULL THEN 'Open' ELSE 'Ordered' END, po_number = $2, po_line = $3, ordered_by = $4, ordered_at = $5 WHERE requisition_id = $1",
            requisitionId,
            new NpgsqlParameter { Value = (object?)poNumber ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint },
            new NpgsqlParameter { Value = (object?)poLine ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer },
            Text(actor),
            new NpgsqlParameter { Value = (object?)at ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task CancelRequisitionAsync(long requisitionId, string actor, DateTimeOffset at, string reason, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE procurement.requisition SET status = 'Cancelled', cancelled_by = $2, cancelled_at = $3, cancelled_reason = $4 WHERE requisition_id = $1 AND status = 'Open'",
            requisitionId, actor, at, reason);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task<IReadOnlyList<RequisitionInfo>> ListRequisitionsAsync(bool openOnly, CancellationToken ct) =>
        ReadAsync(RequisitionSql + (openOnly ? " WHERE status = 'Open'" : string.Empty) + " ORDER BY need_date, requisition_id", ReadRequisition, ct);

    public async Task<RequisitionInfo?> FindRequisitionAsync(long requisitionId, CancellationToken ct) =>
        (await ReadAsync(RequisitionSql + " WHERE requisition_id = $1", ReadRequisition, ct, requisitionId)).SingleOrDefault();

    private static RequisitionInfo ReadRequisition(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetDecimal(2) / 1.000000000000000000000000000000000m, r.GetFieldValue<DateOnly>(3), Enum.Parse<PurchaseSourceKind>(r.GetString(4)), r.GetString(5), r.GetString(6),
        r.IsDBNull(7) ? null : r.GetInt64(7), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetGuid(11),
        Enum.Parse<RequisitionStatus>(r.GetString(12)), r.GetString(13), r.GetFieldValue<DateTimeOffset>(14), r.IsDBNull(15) ? null : r.GetInt64(15), r.IsDBNull(16) ? null : r.GetInt32(16),
        r.IsDBNull(17) ? null : r.GetString(17), r.IsDBNull(18) ? null : r.GetFieldValue<DateTimeOffset>(18), r.IsDBNull(19) ? null : r.GetString(19),
        r.IsDBNull(20) ? null : r.GetFieldValue<DateTimeOffset>(20), r.IsDBNull(21) ? null : r.GetString(21));

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

public static class ProcurementRegistration
{
    public static IServiceCollection AddForgeProcurement(this IServiceCollection services)
    {
        services.AddForgeMigrations(typeof(ProcurementRegistration).Assembly, "procurement");
        services.AddForgeStore<IProcurementStore>(uow => new PostgresProcurementStore(uow));
        services.AddForgeStore<IProcurementLookup>(uow => new PostgresProcurementStore(uow));
        services.AddForgeStore<ITechnicalPackageLookup>(uow => new PostgresProcurementStore(uow));
        services.AddForgeStore<IRequisitionLookup>(uow => new PostgresProcurementStore(uow));
        services.AddSingleton<RequisitionService>();
        services.AddSingleton<PurchasingService>();
        services.AddSingleton<SupplierService>();
        services.AddForgeStore<ISourcingLookup>(uow => new PostgresSourcingStore(uow));
        services.AddForgeStore<ISourcingStore>(uow => new PostgresSourcingStore(uow));
        services.AddSingleton<SourcingService>();
        return services;
    }
}
