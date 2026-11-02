using System.Globalization;
using System.Text;
using Forge.Kernel.Access;
using Forge.Kernel.Security;
using Forge.Procurement.Contracts;
using Forge.Resources.Contracts;
using Forge.Sales.Contracts;

namespace Forge.Host.Sales;

/// <summary>
/// Owner decisions 2026-11-01: customer payments and approved supplier invoices go to the accounting system with the rest of the
/// export. Cash receipts debit cash and credit receivables on the day received (voids reversed on the day voided); supplier invoices
/// credit payables and clear received-not-invoiced at the PO price, with price variance, freight and tax on their own accounts.
/// </summary>
public sealed class FinanceExport(IKernelUnitOfWorkFactory unitOfWork, AccountingExportService accounting, IConfiguration configuration)
{
    private static readonly (string Key, string Code, string Name)[] Defaults =
    [
        ("Cash", "1000", "Cash"),
        ("AccountsPayable", "2000", "Accounts payable"),
        ("PurchasePriceVariance", "5300", "Purchase price variance"),
        ("FreightIn", "5400", "Freight in"),
        ("PurchaseTax", "5410", "Purchase tax"),
    ];

    public static readonly IReadOnlyList<string> Files = ["cash-receipts", "cash-journal", "supplier-invoices", "payables-journal"];

    private (string Code, string Name) Account(string key)
    {
        var d = Defaults.FirstOrDefault(x => x.Key == key);
        if (d.Key is not null) return (configuration[$"Forge:Accounting:Accounts:{key}"] ?? d.Code, d.Name);
        return (accounting.Accounts[key], key switch { "AccountsReceivable" => "Accounts receivable", "ReceivedNotInvoiced" => "Received not invoiced", _ => key });
    }

    private JournalLine Line(DateOnly date, string entry, string key, decimal debit, decimal credit, string currency, string source, string memo)
    {
        var (code, name) = Account(key);
        return new JournalLine(date, entry, code, name, debit, credit, currency, source, null, null, memo);
    }

    public async Task<string> CsvAsync(ForgePrincipal principal, IForgeAuthorizer authorizer, string file, DateOnly from, DateOnly to, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(authorizer);
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var zone = await uow.SiteTimeZoneAsync(ct);
        DateOnly Day(DateTimeOffset at) => SiteTime.DateAtOrUtc(at, zone);
        bool InPeriod(DateOnly day) => day >= from && day <= to;
        if (file is "cash-receipts" or "cash-journal")
        {
            var payments = await uow.Store<IReceivablesLookup>().ListPaymentsAsync(null, ct);
            var customers = (await uow.Store<ISalesLookup>().ListCustomersAsync(ct)).ToDictionary(c => c.Code, c => c.Name);
            if (file == "cash-receipts")
            {
                return Csv(["payment_id", "customer_code", "customer_name", "received_on", "amount", "currency", "method", "reference", "status", "applied_to", "on_account"],
                    payments.Where(p => InPeriod(p.ReceivedOn) || (p.VoidedAt is { } v && InPeriod(Day(v)))).OrderBy(p => p.PaymentId).Select(p => new[]
                    {
                        S(p.PaymentId), p.CustomerCode, customers.GetValueOrDefault(p.CustomerCode), S(p.ReceivedOn), S(p.Amount), p.Currency, p.Method.ToString(), p.Reference, p.Status.ToString(),
                        string.Join(" ", p.Applications.Select(a => string.Create(CultureInfo.InvariantCulture, $"{a.InvoiceNumber}:{a.Amount:0.00}"))), S(p.Unapplied),
                    }));
            }

            var lines = new List<JournalLine>();
            foreach (var p in payments.OrderBy(p => p.PaymentId))
            {
                var entry = "PAY-" + S(p.PaymentId);
                var memo = $"Payment {p.PaymentId} {customers.GetValueOrDefault(p.CustomerCode)}{(p.Reference is null ? string.Empty : " " + p.Reference)}";
                if (InPeriod(p.ReceivedOn))
                {
                    lines.Add(Line(p.ReceivedOn, entry, "Cash", p.Amount, 0, p.Currency, "Payment", memo));
                    lines.Add(Line(p.ReceivedOn, entry, "AccountsReceivable", 0, p.Amount, p.Currency, "Payment", memo));
                }

                if (p is { Status: PaymentStatus.Voided, VoidedAt: { } at } && InPeriod(Day(at)))
                {
                    lines.Add(Line(Day(at), entry + "-VOID", "AccountsReceivable", p.Amount, 0, p.Currency, "Payment void", memo));
                    lines.Add(Line(Day(at), entry + "-VOID", "Cash", 0, p.Amount, p.Currency, "Payment void", memo));
                }
            }

            return AccountingExportService.JournalCsv(lines);
        }

        var invoices = (await uow.Store<ISupplierInvoiceLookup>().ListSupplierInvoicesAsync(ct))
            .Where(i => i.Status is SupplierInvoiceStatus.Approved or SupplierInvoiceStatus.Paid && i.DecidedAt is { } d && InPeriod(Day(d))).OrderBy(i => i.InvoiceId).ToList();
        if (file == "supplier-invoices")
        {
            return Csv(["invoice_id", "supplier_code", "supplier_name", "supplier_invoice_number", "po_number", "invoice_date", "due_date", "currency", "subtotal", "freight", "tax", "total",
                    "variance", "status", "approved_by", "paid_on", "payment_reference"],
                invoices.Select(i => new[]
                {
                    S(i.InvoiceId), i.SupplierCode, i.SupplierName, i.SupplierInvoiceNumber, S(i.PoNumber), S(i.InvoiceDate), S(i.DueDate), i.Currency, S(i.Subtotal), S(i.Freight), S(i.Tax),
                    S(i.Total), S(i.Variance), i.Status.ToString(), i.DecidedBy, i.PaidOn is { } paid ? S(paid) : null, i.PaymentReference,
                }));
        }

        var journal = new List<JournalLine>();
        foreach (var i in invoices)
        {
            var day = Day(i.DecidedAt!.Value);
            var entry = "AP-" + S(i.InvoiceId);
            var memo = $"{i.SupplierName} invoice {i.SupplierInvoiceNumber} PO {i.PoNumber}";
            var atPo = i.Subtotal - i.Variance;
            if (atPo != 0) journal.Add(Line(day, entry, "ReceivedNotInvoiced", Math.Max(atPo, 0), Math.Max(-atPo, 0), i.Currency, "Supplier invoice", memo));
            if (i.Variance != 0) journal.Add(Line(day, entry, "PurchasePriceVariance", Math.Max(i.Variance, 0), Math.Max(-i.Variance, 0), i.Currency, "Supplier invoice", memo));
            if (i.Freight > 0) journal.Add(Line(day, entry, "FreightIn", i.Freight, 0, i.Currency, "Supplier invoice", memo));
            if (i.Tax > 0) journal.Add(Line(day, entry, "PurchaseTax", i.Tax, 0, i.Currency, "Supplier invoice", memo));
            journal.Add(Line(day, entry, "AccountsPayable", 0, i.Total, i.Currency, "Supplier invoice", memo));
        }

        return AccountingExportService.JournalCsv(journal);
    }

    private static string S(long v) => v.ToString(CultureInfo.InvariantCulture);

    private static string S(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    private static string S(DateOnly v) => v.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Csv(IReadOnlyList<string> header, IEnumerable<string?[]> rows)
    {
        static string Cell(string? v)
        {
            if (v is null) return string.Empty;
            if (v.Length > 0 && "=+-@".Contains(v[0], StringComparison.Ordinal) && !decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out _)) v = "'" + v;
            return v.Contains(',', StringComparison.Ordinal) || v.Contains('"', StringComparison.Ordinal) || v.Contains('\n', StringComparison.Ordinal) ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : v;
        }

        var b = new StringBuilder();
        b.Append(string.Join(",", header)).Append("\r\n");
        foreach (var row in rows) b.Append(string.Join(",", row.Select(Cell))).Append("\r\n");
        return b.ToString();
    }
}
