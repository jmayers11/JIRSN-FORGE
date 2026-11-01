using System.Globalization;
using System.Text;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Security;
using Forge.Resources.Contracts;
using Forge.Sales.Contracts;

namespace Forge.Host.Sales;

/// <summary>One journal line for the accounting system. Every entry's debits equal its credits.</summary>
public sealed record JournalLine(DateOnly Date, string Entry, string Account, string AccountName, decimal Debit, decimal Credit, string Currency, string Source,
    string? PartNumber, decimal? Quantity, string Memo);

/// <summary>Inventory movements of one kind in the period, and how they were treated.</summary>
public sealed record InventoryKindSummary(string Kind, int Movements, int Valued, decimal Value, string Treatment);

public sealed record AccountingExportSummary(
    DateOnly From,
    DateOnly To,
    int Invoices,
    int VoidedInPeriod,
    decimal InvoicedSubtotal,
    decimal InvoicedTax,
    decimal InvoicedTotal,
    bool SalesJournalBalances,
    bool InventoryIncluded,
    bool? InventoryJournalBalances,
    IReadOnlyList<InventoryKindSummary> InventoryKinds,
    IReadOnlyList<string> PartsWithoutStandardCost,
    IReadOnlyDictionary<string, string> Accounts,
    IReadOnlyList<string> Notes);

public sealed record AccountingExport(AccountingExportSummary Summary, IReadOnlyList<InvoiceInfo> Invoices, IReadOnlyList<JournalLine> SalesJournal, IReadOnlyList<JournalLine> InventoryJournal);

/// <summary>
/// The accounting export (owner decision 2026-10-31): Forge keeps no general ledger, so it hands the accounting system generic CSV files
/// any system can import: the invoice register, invoice lines, a sales journal (receivables, revenue, sales tax, voids reversed on their
/// void date) and an inventory journal valuing every movement into or out of stock at the part's standard cost when it happened.
/// Account codes are configured under Forge:Accounting:Accounts.
/// </summary>
public sealed class AccountingExportService(IKernelUnitOfWorkFactory unitOfWork, IForgeAuthorizer authorizer, IConfiguration configuration)
{
    private static readonly (string Key, string Code, string Name)[] Defaults =
    [
        ("AccountsReceivable", "1200", "Accounts receivable"),
        ("Inventory", "1300", "Inventory at standard cost"),
        ("WorkInProcess", "1400", "Work in process"),
        ("ReceivedNotInvoiced", "2100", "Received not invoiced"),
        ("SalesTaxPayable", "2300", "Sales tax payable"),
        ("SalesRevenue", "4000", "Sales revenue"),
        ("CostOfGoodsSold", "5000", "Cost of goods sold"),
        ("Scrap", "5100", "Scrap"),
        ("InventoryAdjustment", "5200", "Inventory adjustments"),
        ("MaintenanceExpense", "6100", "Maintenance parts"),
        ("ProjectCosts", "6200", "Facility and capital projects"),
    ];

    public IReadOnlyDictionary<string, string> Accounts =>
        Defaults.ToDictionary(d => d.Key, d => configuration[$"Forge:Accounting:Accounts:{d.Key}"] ?? d.Code, StringComparer.Ordinal);

    public async Task<AccountingExport> BuildAsync(ForgePrincipal principal, DateOnly from, DateOnly to, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var accounts = Accounts;
        var names = Defaults.ToDictionary(d => d.Key, d => d.Name, StringComparer.Ordinal);
        JournalLine Line(DateOnly date, string entry, string key, decimal debit, decimal credit, string currency, string source, string? part, decimal? quantity, string memo) =>
            new(date, entry, accounts[key], names[key], debit, credit, currency, source, part, quantity, memo);

        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var zone = await uow.SiteTimeZoneAsync(ct);
        DateOnly Day(DateTimeOffset at) => SiteTime.DateAtOrUtc(at, zone);
        bool InPeriod(DateOnly day) => day >= from && day <= to;

        // Sales journal: issued invoices on their invoice date; voids reversed on the day they were voided.
        var all = await uow.Store<ISalesLookup>().ListInvoicesAsync(ct);
        var issued = all.Where(i => InPeriod(i.InvoiceDate)).OrderBy(i => i.InvoiceNumber).ToList();
        var voided = all.Where(i => i is { Status: InvoiceStatus.Voided, VoidedAt: { } at } && InPeriod(Day(at))).OrderBy(i => i.InvoiceNumber).ToList();
        var sales = new List<JournalLine>();
        foreach (var invoice in issued)
        {
            var entry = $"INV-{invoice.InvoiceNumber.ToString(CultureInfo.InvariantCulture)}";
            var memo = $"Invoice {invoice.InvoiceNumber} {invoice.CustomerName} SO {invoice.OrderNumber}{(invoice.CustomerPo is null ? string.Empty : $" PO {invoice.CustomerPo}")}";
            sales.Add(Line(invoice.InvoiceDate, entry, "AccountsReceivable", invoice.Total, 0, invoice.Currency, "Invoice", null, null, memo));
            sales.Add(Line(invoice.InvoiceDate, entry, "SalesRevenue", 0, invoice.Subtotal, invoice.Currency, "Invoice", null, null, memo));
            if (invoice.Tax > 0) sales.Add(Line(invoice.InvoiceDate, entry, "SalesTaxPayable", 0, invoice.Tax, invoice.Currency, "Invoice", null, null, memo));
        }

        foreach (var invoice in voided)
        {
            var day = Day(invoice.VoidedAt!.Value);
            var entry = $"INV-{invoice.InvoiceNumber.ToString(CultureInfo.InvariantCulture)}-VOID";
            var memo = $"Void of invoice {invoice.InvoiceNumber}: {invoice.VoidReason}";
            sales.Add(Line(day, entry, "SalesRevenue", invoice.Subtotal, 0, invoice.Currency, "InvoiceVoid", null, null, memo));
            if (invoice.Tax > 0) sales.Add(Line(day, entry, "SalesTaxPayable", invoice.Tax, 0, invoice.Currency, "InvoiceVoid", null, null, memo));
            sales.Add(Line(day, entry, "AccountsReceivable", 0, invoice.Total, invoice.Currency, "InvoiceVoid", null, null, memo));
        }

        var notes = new List<string>
        {
            "Forge keeps no general ledger; import these entries into the accounting system, which stays the book of record and the authority on tax.",
            "Invoices bill confirmed shipments only. A voided invoice is reversed on the day it was voided; its original entry stays in its own period.",
        };

        // Inventory journal: only for readers of costs, valued at the standard cost in force at each movement.
        var inventory = new List<JournalLine>();
        var kinds = new List<InventoryKindSummary>();
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var costsVisible = authorizer.Authorize(principal, Permissions.CostingRead, ResourceRef.Of(Permissions.CostingRead)).IsPermitted;
        if (costsVisible)
        {
            var costs = await uow.Store<ICostingLookup>().ListStandardCostsAsync(null, ct);
            var start = SiteTime.StartOfOrUtc(from, zone);
            var end = SiteTime.StartOfOrUtc(to.AddDays(1), zone);
            var movements = await uow.Store<IInventoryLookup>().ListTransactionsBetweenAsync(start, end, ct);
            foreach (var group in movements.Where(m => m.At >= start && m.At < end).GroupBy(m => m.Kind).OrderBy(g => g.Key.ToString(), StringComparer.Ordinal))
            {
                var valued = 0;
                var value = 0m;
                var treatment = Treatment(group.Key);
                foreach (var m in group.OrderBy(m => m.TransactionId))
                {
                    var into = m.ToLocation is not null && m.FromLocation is null;
                    var outOf = m.FromLocation is not null && m.ToLocation is null;
                    if (treatment is null || !(into || outOf)) continue; // a move between locations or statuses changes no value
                    var standard = Forge.Inventory.Contracts.Costing.StandardAt(costs, m.PartNumber, m.At);
                    if (standard is null) { missing.Add(m.PartNumber); continue; }
                    var amount = Math.Round(m.Quantity * standard.Total, 2, MidpointRounding.AwayFromZero);
                    var counter = Counter(m.Kind);
                    var entry = $"INV-TXN-{m.TransactionId.ToString(CultureInfo.InvariantCulture)}";
                    var memo = $"{m.Kind} {m.Quantity.ToString("0.######", CultureInfo.InvariantCulture)} {m.PartNumber}{Reference(m)} at standard {standard.Total.ToString("0.####", CultureInfo.InvariantCulture)}";
                    var day = Day(m.At);
                    inventory.Add(into
                        ? Line(day, entry, "Inventory", amount, 0, standard.Currency, m.Kind.ToString(), m.PartNumber, m.Quantity, memo)
                        : Line(day, entry, counter, amount, 0, standard.Currency, m.Kind.ToString(), m.PartNumber, m.Quantity, memo));
                    inventory.Add(into
                        ? Line(day, entry, counter, 0, amount, standard.Currency, m.Kind.ToString(), m.PartNumber, m.Quantity, memo)
                        : Line(day, entry, "Inventory", 0, amount, standard.Currency, m.Kind.ToString(), m.PartNumber, m.Quantity, memo));
                    valued++;
                    value += amount;
                }

                kinds.Add(new InventoryKindSummary(group.Key.ToString(), group.Count(), valued, value, treatment ?? "Not posted: moves stock between locations or statuses, or between instances, without changing its value."));
            }

            notes.Add("Inventory is valued at standard cost (material + labor + overhead) in force when each movement happened. Purchase receipts post at standard to Received not invoiced; purchase price variance is recognized when supplier invoices are matched in the accounting system.");
            if (missing.Count > 0)
                notes.Add($"{missing.Count} part(s) moved without a standard cost and are not in the inventory journal: {string.Join(", ", missing)}. Set their standard costs and export again.");
        }
        else
        {
            notes.Add("The inventory journal needs permission to read costs; it is not included.");
        }

        static bool Balanced(IEnumerable<JournalLine> lines) => lines.GroupBy(l => (l.Entry, l.Currency)).All(g => g.Sum(l => l.Debit) == g.Sum(l => l.Credit));
        var summary = new AccountingExportSummary(from, to, issued.Count, voided.Count, issued.Sum(i => i.Subtotal), issued.Sum(i => i.Tax), issued.Sum(i => i.Total),
            Balanced(sales), costsVisible, costsVisible ? Balanced(inventory) : null, kinds, missing.ToList(), accounts, notes);
        return new AccountingExport(summary, issued, sales, inventory);
    }

    /// <summary>The account on the other side of Inventory for a movement of this kind, as text for the summary; null when it is not posted.</summary>
    private static string? Treatment(InventoryTransactionKind kind) => kind switch
    {
        InventoryTransactionKind.Transfer or InventoryTransactionKind.QualityHold or InventoryTransactionKind.QualityRelease
            or InventoryTransactionKind.Split or InventoryTransactionKind.Merge => null,
        _ => $"Inventory against {Counter(kind)}.",
    };

    private static string Counter(InventoryTransactionKind kind) => kind switch
    {
        InventoryTransactionKind.Receipt or InventoryTransactionKind.ReturnToVendor => "ReceivedNotInvoiced",
        InventoryTransactionKind.Issue or InventoryTransactionKind.Return or InventoryTransactionKind.ProductionReceipt
            or InventoryTransactionKind.Remnant or InventoryTransactionKind.Byproduct => "WorkInProcess",
        InventoryTransactionKind.Shipment or InventoryTransactionKind.CustomerReturn => "CostOfGoodsSold",
        InventoryTransactionKind.Scrap => "Scrap",
        InventoryTransactionKind.MaintenanceIssue or InventoryTransactionKind.MaintenanceReturn => "MaintenanceExpense",
        InventoryTransactionKind.ProjectIssue or InventoryTransactionKind.ProjectReturn => "ProjectCosts",
        _ => "InventoryAdjustment",
    };

    private static string Reference(InventoryTransaction m) =>
        m.ShipmentId is { } s ? $" shipment {s}"
        : m.OrderNumber is { } o ? $" order {o}"
        : m.PoNumber is { } po ? $" PO {po}"
        : m.WorkOrderId is { } w ? $" work order {w}"
        : string.Empty;

    public static string InvoicesCsv(IReadOnlyList<InvoiceInfo> invoices) => Csv(
        ["InvoiceNumber", "InvoiceDate", "DueDate", "CustomerCode", "CustomerName", "CustomerPO", "SalesOrder", "Status", "Currency", "Subtotal", "TaxRatePercent", "TaxExemptCertificate", "Tax", "Total"],
        invoices.Select(i => new[]
        {
            N(i.InvoiceNumber), D(i.InvoiceDate), D(i.DueDate), i.CustomerCode, i.CustomerName, i.CustomerPo, N(i.OrderNumber), i.Status.ToString(), i.Currency,
            M(i.Subtotal), i.TaxRatePercent.ToString("0.###", CultureInfo.InvariantCulture), i.TaxExemptCertificate, M(i.Tax), M(i.Total),
        }));

    public static string InvoiceLinesCsv(IReadOnlyList<InvoiceInfo> invoices) => Csv(
        ["InvoiceNumber", "Line", "SalesOrder", "OrderLine", "Shipment", "PartNumber", "Quantity", "UnitPrice", "Amount", "Currency"],
        invoices.SelectMany(i => i.Lines.Select(l => new[]
        {
            N(i.InvoiceNumber), N(l.LineNumber), N(i.OrderNumber), N(l.OrderLine), N(l.ShipmentId), l.PartNumber, l.Quantity.ToString("0.######", CultureInfo.InvariantCulture),
            l.UnitPrice.ToString("0.####", CultureInfo.InvariantCulture), M(l.Amount), i.Currency,
        })));

    public static string JournalCsv(IReadOnlyList<JournalLine> lines) => Csv(
        ["Date", "Entry", "Account", "AccountName", "Debit", "Credit", "Currency", "Source", "PartNumber", "Quantity", "Memo"],
        lines.Select(l => new[]
        {
            D(l.Date), l.Entry, l.Account, l.AccountName, M(l.Debit), M(l.Credit), l.Currency, l.Source, l.PartNumber,
            l.Quantity?.ToString("0.######", CultureInfo.InvariantCulture), l.Memo,
        }));

    private static string N(long n) => n.ToString(CultureInfo.InvariantCulture);

    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string M(decimal m) => m.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>RFC 4180: comma-separated, CRLF rows, fields quoted when they contain a comma, quote or line break.</summary>
    private static string Csv(IReadOnlyList<string> header, IEnumerable<string?[]> rows)
    {
        var text = new StringBuilder();
        void Row(IEnumerable<string?> fields) => text.Append(string.Join(',', fields.Select(Field))).Append("\r\n");
        Row(header);
        foreach (var row in rows) Row(row);
        return text.ToString();
    }

    private static string Field(string? value)
    {
        var v = value ?? string.Empty;
        // A leading =, + or @ is kept as text so a spreadsheet does not run it as a formula.
        if (v.Length > 0 && v[0] is '=' or '+' or '@' && !decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out _)) v = "'" + v;
        return v.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : v;
    }
}
