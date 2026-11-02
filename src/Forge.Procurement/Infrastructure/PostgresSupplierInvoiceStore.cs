using Forge.Kernel.Postgres;
using Forge.Procurement.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Procurement.Infrastructure;

internal sealed class PostgresSupplierInvoiceStore(PostgresUnitOfWork uow) : ISupplierInvoiceLookup, ISupplierInvoiceStore
{
    private const string InvoiceSql =
        """
        SELECT invoice_id, supplier_code, supplier_name, supplier_invoice_number, po_number, invoice_date, due_date, currency, subtotal, freight, tax, total, variance,
               status, match_summary, entered_by, entered_at, decided_by, decided_at, decision_note, paid_on, payment_reference
        FROM procurement.supplier_invoice
        """;

    public async Task<IReadOnlyList<SupplierInvoiceInfo>> ListSupplierInvoicesAsync(CancellationToken ct) => await ReadAsync(InvoiceSql + " ORDER BY invoice_id DESC", ct);

    public async Task<SupplierInvoiceInfo?> FindSupplierInvoiceAsync(long invoiceId, CancellationToken ct) =>
        (await ReadAsync(InvoiceSql + " WHERE invoice_id = $1", ct, invoiceId)).SingleOrDefault();

    public async Task<InvoiceMatchPolicy> MatchPolicyAsync(CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT tolerance_percent, tolerance_amount, set_by, set_at FROM procurement.invoice_match_policy ORDER BY policy_id DESC LIMIT 1");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new InvoiceMatchPolicy(r.GetDecimal(0), r.GetDecimal(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3)) : InvoiceMatchPolicy.Default;
    }

    public async Task LockPurchaseOrderInvoicesAsync(long poNumber, CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT pg_advisory_xact_lock(hashtextextended('supplier-invoice:' || $1::text, 0))", poNumber);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> SupplierInvoiceNumberTakenAsync(string supplierCode, string number, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "SELECT EXISTS (SELECT 1 FROM procurement.supplier_invoice WHERE supplier_code = $1 AND upper(supplier_invoice_number) = upper($2) AND status <> 'Voided')", supplierCode, number);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<long> InsertSupplierInvoiceAsync(SupplierInvoiceInfo i, CancellationToken ct)
    {
        long id;
        await using (var cmd = uow.Command(
            """
            INSERT INTO procurement.supplier_invoice (supplier_code, supplier_name, supplier_invoice_number, po_number, invoice_date, due_date, currency, subtotal, freight, tax, total, variance,
                status, match_summary, entered_by, entered_at, decided_by, decided_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18) RETURNING invoice_id
            """,
            i.SupplierCode, i.SupplierName, i.SupplierInvoiceNumber, i.PoNumber, i.InvoiceDate, i.DueDate, i.Currency, i.Subtotal, i.Freight, i.Tax, i.Total, i.Variance,
            i.Status.ToString(), i.MatchSummary, i.EnteredBy, i.EnteredAt, Text(i.DecidedBy), Time(i.DecidedAt)))
        {
            id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }

        foreach (var l in i.Lines)
        {
            await using var line = uow.Command(
                """
                INSERT INTO procurement.supplier_invoice_line (invoice_id, line_number, po_line, part_number, quantity, unit_price, amount, po_unit_price, received_open, variance, problem)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
                """,
                id, l.LineNumber, l.PoLine, l.PartNumber, l.Quantity, l.UnitPrice, l.Amount, new NpgsqlParameter { Value = (object?)l.PoUnitPrice ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Numeric },
                l.ReceivedOpen, l.Variance, Text(l.Problem));
            await line.ExecuteNonQueryAsync(ct);
        }

        return id;
    }

    public async Task SetSupplierInvoiceStatusAsync(long invoiceId, SupplierInvoiceStatus status, string actor, DateTimeOffset at, string? note, CancellationToken ct)
    {
        await using var cmd = uow.Command("UPDATE procurement.supplier_invoice SET status = $2, decided_by = $3, decided_at = $4, decision_note = $5 WHERE invoice_id = $1",
            invoiceId, status.ToString(), actor, at, Text(note));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkSupplierInvoicePaidAsync(long invoiceId, DateOnly paidOn, string? reference, CancellationToken ct)
    {
        await using var cmd = uow.Command("UPDATE procurement.supplier_invoice SET paid_on = $2, payment_reference = $3 WHERE invoice_id = $1", invoiceId, paidOn, Text(reference));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertMatchPolicyAsync(InvoiceMatchPolicy p, CancellationToken ct)
    {
        await using var cmd = uow.Command("INSERT INTO procurement.invoice_match_policy (tolerance_percent, tolerance_amount, set_by, set_at) VALUES ($1, $2, $3, $4)",
            p.TolerancePercent, p.ToleranceAmount, p.SetBy, p.SetAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<IReadOnlyList<SupplierInvoiceInfo>> ReadAsync(string sql, CancellationToken ct, params object?[] args)
    {
        var heads = new List<SupplierInvoiceInfo>();
        await using (var cmd = uow.Command(sql, args))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                heads.Add(new SupplierInvoiceInfo(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetFieldValue<DateOnly>(5), r.GetFieldValue<DateOnly>(6),
                    r.GetString(7), r.GetDecimal(8), r.GetDecimal(9), r.GetDecimal(10), r.GetDecimal(11), r.GetDecimal(12), Enum.Parse<SupplierInvoiceStatus>(r.GetString(13)), r.GetString(14),
                    r.GetString(15), r.GetFieldValue<DateTimeOffset>(16), r.IsDBNull(17) ? null : r.GetString(17), r.IsDBNull(18) ? null : r.GetFieldValue<DateTimeOffset>(18),
                    r.IsDBNull(19) ? null : r.GetString(19), r.IsDBNull(20) ? null : r.GetFieldValue<DateOnly>(20), r.IsDBNull(21) ? null : r.GetString(21), []));
            }
        }

        if (heads.Count == 0) return heads;
        var lines = new Dictionary<long, List<SupplierInvoiceLine>>();
        await using (var cmd = uow.Command(
            "SELECT invoice_id, line_number, po_line, part_number, quantity, unit_price, amount, po_unit_price, received_open, variance, problem FROM procurement.supplier_invoice_line WHERE invoice_id = ANY($1) ORDER BY invoice_id, line_number",
            new NpgsqlParameter { Value = heads.Select(h => h.InvoiceId).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint }))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var id = r.GetInt64(0);
                if (!lines.TryGetValue(id, out var list)) lines[id] = list = [];
                list.Add(new SupplierInvoiceLine(r.GetInt32(1), r.GetInt32(2), r.GetString(3), r.GetDecimal(4), r.GetDecimal(5), r.GetDecimal(6), r.IsDBNull(7) ? null : r.GetDecimal(7),
                    r.GetDecimal(8), r.GetDecimal(9), r.IsDBNull(10) ? null : r.GetString(10)));
            }
        }

        return heads.Select(h => h with { Lines = lines.GetValueOrDefault(h.InvoiceId) ?? [] }).ToList();
    }

    private static NpgsqlParameter Text(string? v) => new() { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };

    private static NpgsqlParameter Time(DateTimeOffset? v) => new() { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz };
}

public static class SupplierInvoiceRegistration
{
    public static IServiceCollection AddForgeSupplierInvoices(this IServiceCollection services)
    {
        services.AddForgeStore<ISupplierInvoiceLookup>(uow => new PostgresSupplierInvoiceStore(uow));
        services.AddForgeStore<ISupplierInvoiceStore>(uow => new PostgresSupplierInvoiceStore(uow));
        services.AddSingleton<SupplierInvoiceService>();
        return services;
    }
}
