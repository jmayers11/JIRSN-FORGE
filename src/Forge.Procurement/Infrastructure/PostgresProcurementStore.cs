using Forge.Kernel.Postgres;
using Forge.Procurement.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Forge.Procurement.Infrastructure;

internal sealed class PostgresProcurementStore(PostgresUnitOfWork uow) : IProcurementStore, IProcurementLookup
{
    private const string OrderSql = "SELECT po_number, supplier, status, created_by, created_at, approved_by, approved_at FROM procurement.purchase_order";
    private const string LineSql = "SELECT po_number, line_number, part_number, quantity, unit_of_measure, promised_date, status FROM procurement.purchase_order_line";

    public async Task<long> InsertOrderAsync(string supplier, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "INSERT INTO procurement.purchase_order (supplier, status, created_at, created_by) VALUES ($1, 'Draft', $2, $3) RETURNING po_number",
            supplier, at, actor);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<bool> InsertLineAsync(long poNumber, PurchaseLineInfo line, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(line);
        await using var cmd = uow.Command(
            "INSERT INTO procurement.purchase_order_line (po_number, line_number, part_number, quantity, unit_of_measure, promised_date, status) VALUES ($1, $2, $3, $4, $5, $6, $7) ON CONFLICT DO NOTHING",
            poNumber, line.LineNumber, line.PartNumber, line.Quantity, line.UnitOfMeasure, line.PromisedDate, line.Status.ToString());
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
        []);

    private static (long PoNumber, PurchaseLineInfo Line) ReadLine(NpgsqlDataReader r) => (
        r.GetInt64(0),
        new PurchaseLineInfo(r.GetInt32(1), r.GetString(2), (r.GetDecimal(3) / 1.000000000000000000000000000000000m), r.GetString(4), r.GetFieldValue<DateOnly>(5), Enum.Parse<PurchaseLineStatus>(r.GetString(6))));

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
        services.AddSingleton<PurchasingService>();
        return services;
    }
}
