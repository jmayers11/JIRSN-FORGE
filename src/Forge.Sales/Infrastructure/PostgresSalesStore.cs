using Forge.Kernel.Postgres;
using Forge.Sales.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Sales.Infrastructure;

internal sealed class PostgresSalesStore(PostgresUnitOfWork uow) : ISalesStore, ISalesLookup
{
    private const string CustomerSql =
        "SELECT code, name, status, bill_to, ship_to, payment_terms_days, tax_rate_percent, tax_exempt_certificate, currency, notes, created_by, created_at FROM sales.customer";

    private const string QuoteSql =
        "SELECT quote_number, customer_code, status, valid_until, currency, notes, created_by, created_at, approved_by, approved_at, closed_by, closed_at, sales_order_number FROM sales.quote";

    private const string QuoteLineSql = "SELECT quote_number, line_number, part_number, quantity, unit_price, lead_time_days, description FROM sales.quote_line";

    private const string OrderSql =
        "SELECT order_number, customer_code, customer_name, customer_po, quote_number, ship_to, currency, status, created_by, created_at, approved_by, approved_at, closed_by, closed_at FROM sales.sales_order";

    private const string OrderLineSql = "SELECT order_number, line_number, part_number, quantity, unit_of_measure, unit_price, due_date, status FROM sales.sales_order_line";

    private const string InvoiceSql =
        """
        SELECT invoice_number, order_number, customer_code, customer_name, customer_po, status, invoice_date, due_date, currency, subtotal, tax_rate_percent,
               tax_exempt_certificate, tax, total, issued_by, issued_at, voided_by, voided_at, void_reason
        FROM sales.invoice
        """;

    private const string InvoiceLineSql = "SELECT invoice_number, line_number, order_line, shipment_id, part_number, quantity, unit_price, amount FROM sales.invoice_line";

    public async Task<bool> InsertCustomerAsync(CustomerInfo customer, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            """
            INSERT INTO sales.customer (code, name, status, bill_to, ship_to, payment_terms_days, tax_rate_percent, tax_exempt_certificate, currency, notes, created_at, created_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12) ON CONFLICT DO NOTHING
            """,
            customer.Code, customer.Name, customer.Status.ToString(), Text(customer.BillTo), Text(customer.ShipTo), customer.PaymentTermsDays, customer.TaxRatePercent,
            Text(customer.TaxExemptCertificate), customer.Currency, Text(customer.Notes), customer.CreatedAt, customer.CreatedBy);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> CustomerNameTakenAsync(string name, string exceptCode, CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT EXISTS (SELECT 1 FROM sales.customer WHERE lower(name) = lower($1) AND code <> $2)", name, exceptCode);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task UpdateCustomerAsync(CustomerInfo customer, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            """
            UPDATE sales.customer SET name = $2, status = $3, bill_to = $4, ship_to = $5, payment_terms_days = $6, tax_rate_percent = $7, tax_exempt_certificate = $8,
                   notes = $9, changed_at = $10, changed_by = $11
            WHERE code = $1
            """,
            customer.Code, customer.Name, customer.Status.ToString(), Text(customer.BillTo), Text(customer.ShipTo), customer.PaymentTermsDays, customer.TaxRatePercent,
            Text(customer.TaxExemptCertificate), Text(customer.Notes), at, actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task LockQuoteAsync(long quoteNumber, CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT quote_number FROM sales.quote WHERE quote_number = $1 FOR UPDATE", quoteNumber);
        await cmd.ExecuteScalarAsync(ct);
    }

    public async Task<long> InsertQuoteAsync(QuoteInfo quote, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "INSERT INTO sales.quote (customer_code, status, valid_until, currency, notes, created_at, created_by) VALUES ($1, 'Draft', $2, $3, $4, $5, $6) RETURNING quote_number",
            quote.CustomerCode, quote.ValidUntil, quote.Currency, Text(quote.Notes), quote.CreatedAt, quote.CreatedBy);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task InsertQuoteLineAsync(long quoteNumber, QuoteLineInfo line, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "INSERT INTO sales.quote_line (quote_number, line_number, part_number, quantity, unit_price, lead_time_days, description) VALUES ($1, $2, $3, $4, $5, $6, $7)",
            quoteNumber, line.LineNumber, line.PartNumber, line.Quantity, line.UnitPrice, line.LeadTimeDays, Text(line.Description));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteQuoteLineAsync(long quoteNumber, int lineNumber, CancellationToken ct)
    {
        await using var cmd = uow.Command("DELETE FROM sales.quote_line WHERE quote_number = $1 AND line_number = $2", quoteNumber, lineNumber);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetQuoteStatusAsync(long quoteNumber, QuoteStatus status, DateTimeOffset at, string actor, long? salesOrderNumber, CancellationToken ct)
    {
        var sql = status == QuoteStatus.Approved
            ? "UPDATE sales.quote SET status = $2, approved_at = $3, approved_by = $4 WHERE quote_number = $1"
            : "UPDATE sales.quote SET status = $2, closed_at = $3, closed_by = $4, sales_order_number = $5 WHERE quote_number = $1";
        await using var cmd = status == QuoteStatus.Approved
            ? uow.Command(sql, quoteNumber, status.ToString(), at, actor)
            : uow.Command(sql, quoteNumber, status.ToString(), at, actor, Long(salesOrderNumber));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task LockOrderAsync(long orderNumber, CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT order_number FROM sales.sales_order WHERE order_number = $1 FOR UPDATE", orderNumber);
        await cmd.ExecuteScalarAsync(ct);
    }

    public async Task<long> InsertOrderAsync(SalesOrderInfo order, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            """
            INSERT INTO sales.sales_order (customer_code, customer_name, customer_po, quote_number, ship_to, currency, status, created_at, created_by)
            VALUES ($1, $2, $3, $4, $5, $6, 'Draft', $7, $8) RETURNING order_number
            """,
            order.CustomerCode, order.CustomerName, Text(order.CustomerPo), Long(order.QuoteNumber), Text(order.ShipTo), order.Currency, order.CreatedAt, order.CreatedBy);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task InsertOrderLineAsync(long orderNumber, SalesOrderLineInfo line, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "INSERT INTO sales.sales_order_line (order_number, line_number, part_number, quantity, unit_of_measure, unit_price, due_date, status) VALUES ($1, $2, $3, $4, $5, $6, $7, $8)",
            orderNumber, line.LineNumber, line.PartNumber, line.Quantity, line.UnitOfMeasure, line.UnitPrice, line.DueDate, line.Status.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateOrderLineAsync(long orderNumber, SalesOrderLineInfo line, CancellationToken ct)
    {
        await using var cmd = uow.Command("UPDATE sales.sales_order_line SET due_date = $3, status = $4 WHERE order_number = $1 AND line_number = $2",
            orderNumber, line.LineNumber, line.DueDate, line.Status.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetOrderStatusAsync(long orderNumber, SalesOrderStatus status, DateTimeOffset at, string actor, CancellationToken ct)
    {
        var sql = status == SalesOrderStatus.Approved
            ? "UPDATE sales.sales_order SET status = $2, approved_at = $3, approved_by = $4 WHERE order_number = $1"
            : "UPDATE sales.sales_order SET status = $2, closed_at = $3, closed_by = $4 WHERE order_number = $1";
        await using var cmd = uow.Command(sql, orderNumber, status.ToString(), at, actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlySet<long>> InvoicedShipmentsAsync(long orderNumber, CancellationToken ct)
    {
        var ids = await ReadAsync("SELECT DISTINCT shipment_id FROM sales.invoice_line WHERE order_number = $1 AND NOT voided", r => r.GetInt64(0), ct, orderNumber);
        return ids.ToHashSet();
    }

    public async Task<long> InsertInvoiceAsync(InvoiceInfo invoice, CancellationToken ct)
    {
        long number;
        await using (var cmd = uow.Command(
            """
            INSERT INTO sales.invoice (order_number, customer_code, customer_name, customer_po, status, invoice_date, due_date, currency, subtotal, tax_rate_percent,
                                       tax_exempt_certificate, tax, total, issued_at, issued_by)
            VALUES ($1, $2, $3, $4, 'Issued', $5, $6, $7, $8, $9, $10, $11, $12, $13, $14) RETURNING invoice_number
            """,
            invoice.OrderNumber, invoice.CustomerCode, invoice.CustomerName, Text(invoice.CustomerPo), invoice.InvoiceDate, invoice.DueDate, invoice.Currency, invoice.Subtotal,
            invoice.TaxRatePercent, Text(invoice.TaxExemptCertificate), invoice.Tax, invoice.Total, invoice.IssuedAt, invoice.IssuedBy))
        {
            number = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }

        foreach (var line in invoice.Lines)
        {
            await using var cmd = uow.Command(
                """
                INSERT INTO sales.invoice_line (invoice_number, line_number, order_number, order_line, shipment_id, part_number, quantity, unit_price, amount)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
                """,
                number, line.LineNumber, invoice.OrderNumber, line.OrderLine, line.ShipmentId, line.PartNumber, line.Quantity, line.UnitPrice, line.Amount);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        return number;
    }

    public async Task VoidInvoiceAsync(long invoiceNumber, DateTimeOffset at, string actor, string reason, CancellationToken ct)
    {
        await using (var cmd = uow.Command("UPDATE sales.invoice SET status = 'Voided', voided_at = $2, voided_by = $3, void_reason = $4 WHERE invoice_number = $1", invoiceNumber, at, actor, reason))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using var lines = uow.Command("UPDATE sales.invoice_line SET voided = true WHERE invoice_number = $1", invoiceNumber);
        await lines.ExecuteNonQueryAsync(ct);
    }

    public Task<IReadOnlyList<CustomerInfo>> ListCustomersAsync(CancellationToken ct) => ReadAsync(CustomerSql + " ORDER BY name", ReadCustomer, ct);

    public async Task<CustomerInfo?> FindCustomerAsync(string code, CancellationToken ct) =>
        (await ReadAsync(CustomerSql + " WHERE code = $1", ReadCustomer, ct, code)).SingleOrDefault();

    public async Task<IReadOnlyList<QuoteInfo>> ListQuotesAsync(CancellationToken ct) =>
        await WithQuoteLinesAsync(await ReadAsync(QuoteSql + " ORDER BY quote_number DESC", ReadQuote, ct), null, ct);

    public async Task<QuoteInfo?> FindQuoteAsync(long quoteNumber, CancellationToken ct) =>
        (await WithQuoteLinesAsync(await ReadAsync(QuoteSql + " WHERE quote_number = $1", ReadQuote, ct, quoteNumber), quoteNumber, ct)).SingleOrDefault();

    public async Task<IReadOnlyList<SalesOrderInfo>> ListOrdersAsync(CancellationToken ct) =>
        await WithOrderLinesAsync(await ReadAsync(OrderSql + " ORDER BY order_number DESC", ReadOrder, ct), null, ct);

    public async Task<SalesOrderInfo?> FindOrderAsync(long orderNumber, CancellationToken ct) =>
        (await WithOrderLinesAsync(await ReadAsync(OrderSql + " WHERE order_number = $1", ReadOrder, ct, orderNumber), orderNumber, ct)).SingleOrDefault();

    public async Task<IReadOnlyList<InvoiceInfo>> ListInvoicesAsync(CancellationToken ct) =>
        await WithInvoiceLinesAsync(await ReadAsync(InvoiceSql + " ORDER BY invoice_number DESC", ReadInvoice, ct), null, ct);

    public async Task<InvoiceInfo?> FindInvoiceAsync(long invoiceNumber, CancellationToken ct) =>
        (await WithInvoiceLinesAsync(await ReadAsync(InvoiceSql + " WHERE invoice_number = $1", ReadInvoice, ct, invoiceNumber), invoiceNumber, ct)).SingleOrDefault();

    private async Task<IReadOnlyList<QuoteInfo>> WithQuoteLinesAsync(IReadOnlyList<QuoteInfo> quotes, long? only, CancellationToken ct)
    {
        if (quotes.Count == 0) return quotes;
        var lines = (only is { } n
                ? await ReadAsync(QuoteLineSql + " WHERE quote_number = $1 ORDER BY line_number", ReadQuoteLine, ct, n)
                : await ReadAsync(QuoteLineSql + " ORDER BY quote_number, line_number", ReadQuoteLine, ct))
            .ToLookup(x => x.Number, x => x.Line);
        return quotes.Select(q => q with { Lines = lines[q.QuoteNumber].ToList() }).ToList();
    }

    private async Task<IReadOnlyList<SalesOrderInfo>> WithOrderLinesAsync(IReadOnlyList<SalesOrderInfo> orders, long? only, CancellationToken ct)
    {
        if (orders.Count == 0) return orders;
        var lines = (only is { } n
                ? await ReadAsync(OrderLineSql + " WHERE order_number = $1 ORDER BY line_number", ReadOrderLine, ct, n)
                : await ReadAsync(OrderLineSql + " ORDER BY order_number, line_number", ReadOrderLine, ct))
            .ToLookup(x => x.Number, x => x.Line);
        return orders.Select(o => o with { Lines = lines[o.OrderNumber].ToList() }).ToList();
    }

    private async Task<IReadOnlyList<InvoiceInfo>> WithInvoiceLinesAsync(IReadOnlyList<InvoiceInfo> invoices, long? only, CancellationToken ct)
    {
        if (invoices.Count == 0) return invoices;
        var lines = (only is { } n
                ? await ReadAsync(InvoiceLineSql + " WHERE invoice_number = $1 ORDER BY line_number", ReadInvoiceLine, ct, n)
                : await ReadAsync(InvoiceLineSql + " ORDER BY invoice_number, line_number", ReadInvoiceLine, ct))
            .ToLookup(x => x.Number, x => x.Line);
        return invoices.Select(i => i with { Lines = lines[i.InvoiceNumber].ToList() }).ToList();
    }

    private static CustomerInfo ReadCustomer(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetString(1), Enum.Parse<CustomerStatus>(r.GetString(2)), Str(r, 3), Str(r, 4), r.GetInt32(5), Dec(r, 6), Str(r, 7), r.GetString(8), Str(r, 9),
        r.GetString(10), r.GetFieldValue<DateTimeOffset>(11));

    private static QuoteInfo ReadQuote(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetString(1), Enum.Parse<QuoteStatus>(r.GetString(2)), r.GetFieldValue<DateOnly>(3), r.GetString(4), Str(r, 5), r.GetString(6),
        r.GetFieldValue<DateTimeOffset>(7), Str(r, 8), At(r, 9), Str(r, 10), At(r, 11), r.IsDBNull(12) ? null : r.GetInt64(12), []);

    private static (long Number, QuoteLineInfo Line) ReadQuoteLine(NpgsqlDataReader r) =>
        (r.GetInt64(0), new QuoteLineInfo(r.GetInt32(1), r.GetString(2), Dec(r, 3), Dec(r, 4), r.GetInt32(5), Str(r, 6)));

    private static SalesOrderInfo ReadOrder(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), Str(r, 3), r.IsDBNull(4) ? null : r.GetInt64(4), Str(r, 5), r.GetString(6), Enum.Parse<SalesOrderStatus>(r.GetString(7)),
        r.GetString(8), r.GetFieldValue<DateTimeOffset>(9), Str(r, 10), At(r, 11), Str(r, 12), At(r, 13), []);

    private static (long Number, SalesOrderLineInfo Line) ReadOrderLine(NpgsqlDataReader r) =>
        (r.GetInt64(0), new SalesOrderLineInfo(r.GetInt32(1), r.GetString(2), Dec(r, 3), r.GetString(4), Dec(r, 5), r.GetFieldValue<DateOnly>(6), Enum.Parse<SalesLineStatus>(r.GetString(7))));

    private static InvoiceInfo ReadInvoice(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), Str(r, 4), Enum.Parse<InvoiceStatus>(r.GetString(5)), r.GetFieldValue<DateOnly>(6), r.GetFieldValue<DateOnly>(7),
        r.GetString(8), Dec(r, 9), Dec(r, 10), Str(r, 11), Dec(r, 12), Dec(r, 13), r.GetString(14), r.GetFieldValue<DateTimeOffset>(15), Str(r, 16), At(r, 17), Str(r, 18), []);

    private static (long Number, InvoiceLineInfo Line) ReadInvoiceLine(NpgsqlDataReader r) =>
        (r.GetInt64(0), new InvoiceLineInfo(r.GetInt32(1), r.GetInt32(2), r.GetInt64(3), r.GetString(4), Dec(r, 5), Dec(r, 6), Dec(r, 7)));

    // numeric columns come back with their scale (2.500000); dividing by 1.000… drops trailing zeros so values compare and print cleanly.
    private static decimal Dec(NpgsqlDataReader r, int i) => r.GetDecimal(i) / 1.000000000000000000000000000000000m;

    private static string? Str(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static DateTimeOffset? At(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);

    private static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };

    private static NpgsqlParameter Long(long? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint };

    private async Task<IReadOnlyList<T>> ReadAsync<T>(string sql, Func<NpgsqlDataReader, T> map, CancellationToken ct, params object?[] args)
    {
        var list = new List<T>();
        await using var cmd = uow.Command(sql, args);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(map(reader));
        return list;
    }
}

public static class SalesRegistration
{
    public static IServiceCollection AddForgeSales(this IServiceCollection services)
    {
        services.AddForgeMigrations(typeof(SalesRegistration).Assembly, "sales");
        services.AddForgeStore<ISalesStore>(uow => new PostgresSalesStore(uow));
        services.AddForgeStore<ISalesLookup>(uow => new PostgresSalesStore(uow));
        services.AddSingleton<CustomerService>();
        services.AddSingleton<QuoteService>();
        services.AddSingleton<SalesOrderService>();
        services.AddSingleton<InvoiceService>();
        services.AddForgeStore<ICustomerPartStore>(uow => new PostgresCustomerPartStore(uow));
        services.AddForgeStore<ICustomerPartLookup>(uow => new PostgresCustomerPartStore(uow));
        return services;
    }
}
