namespace Forge.Sales.Contracts;

// Quote to cash (owner decisions 2026-10-31): a Sales Representative keeps customers and drafts quotes and sales orders; a Sales
// Manager, never the author, approves them; a Billing Clerk invoices shipped quantities only. Each customer carries a sales-tax
// rate or a tax-exemption certificate. Forge keeps no general ledger: the accounting system takes Forge's CSV export.

public enum CustomerStatus
{
    Active,

    /// <summary>No new quotes or orders and no approvals until the hold is lifted; shipped work can still be invoiced.</summary>
    OnHold,

    /// <summary>No longer used. Kept for history.</summary>
    Inactive,
}

/// <param name="TaxRatePercent">Sales tax charged on invoices, as a percent (6.25 = 6.25 %). Zero when <paramref name="TaxExemptCertificate"/> is recorded.</param>
/// <param name="TaxExemptCertificate">The customer's resale or exemption certificate number; while recorded, invoices charge no tax.</param>
/// <param name="PaymentTermsDays">Days from the invoice date to its due date (net terms).</param>
public sealed record CustomerInfo(
    string Code,
    string Name,
    CustomerStatus Status,
    string? BillTo,
    string? ShipTo,
    int PaymentTermsDays,
    decimal TaxRatePercent,
    string? TaxExemptCertificate,
    string Currency,
    string? Notes,
    string CreatedBy,
    DateTimeOffset CreatedAt);

public enum QuoteStatus
{
    /// <summary>Being written; lines can change.</summary>
    Draft,

    /// <summary>Approved by someone other than its author; may be sent and turned into a sales order until it expires.</summary>
    Approved,

    /// <summary>The customer accepted it; it became a sales order.</summary>
    Accepted,

    /// <summary>The customer declined it, or it lapsed.</summary>
    Declined,

    /// <summary>Withdrawn before approval.</summary>
    Cancelled,
}

/// <param name="LeadTimeDays">Days from order to delivery the quote promises; the sales order's due date starts from it.</param>
public sealed record QuoteLineInfo(int LineNumber, string PartNumber, decimal Quantity, decimal UnitPrice, int LeadTimeDays, string? Description);

public sealed record QuoteInfo(
    long QuoteNumber,
    string CustomerCode,
    QuoteStatus Status,
    DateOnly ValidUntil,
    string Currency,
    string? Notes,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    string? ClosedBy,
    DateTimeOffset? ClosedAt,
    long? SalesOrderNumber,
    IReadOnlyList<QuoteLineInfo> Lines)
{
    public decimal Total => Lines.Sum(l => Money.Round(l.Quantity * l.UnitPrice));
}

public enum SalesOrderStatus
{
    /// <summary>Being written. Not a commitment; planning does not see it.</summary>
    Draft,

    /// <summary>Approved by someone other than its author. Its open lines are customer-order demand.</summary>
    Approved,

    /// <summary>Nothing more will ship; its demand is closed.</summary>
    Closed,

    Cancelled,
}

public enum SalesLineStatus
{
    Open,
    Cancelled,
}

public sealed record SalesOrderLineInfo(int LineNumber, string PartNumber, decimal Quantity, string UnitOfMeasure, decimal UnitPrice, DateOnly DueDate, SalesLineStatus Status)
{
    public decimal Amount => Money.Round(Quantity * UnitPrice);
}

/// <param name="CustomerPo">The customer's own purchase order number.</param>
/// <param name="QuoteNumber">The approved quote the order was made from, if any.</param>
public sealed record SalesOrderInfo(
    long OrderNumber,
    string CustomerCode,
    string CustomerName,
    string? CustomerPo,
    long? QuoteNumber,
    string? ShipTo,
    string Currency,
    SalesOrderStatus Status,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    string? ClosedBy,
    DateTimeOffset? ClosedAt,
    IReadOnlyList<SalesOrderLineInfo> Lines)
{
    /// <summary>The value of the open lines.</summary>
    public decimal Total => Lines.Where(l => l.Status == SalesLineStatus.Open).Sum(l => l.Amount);

    /// <summary>Where this order line's customer-order demand comes from in planning.</summary>
    public static string DemandSource(long orderNumber, int lineNumber) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"sales:{orderNumber}/{lineNumber}");

    /// <summary>The order and line a demand source names, or null when it is not a sales order line.</summary>
    public static (long OrderNumber, int LineNumber)? ParseDemandSource(string? source)
    {
        if (source is null || !source.StartsWith("sales:", StringComparison.Ordinal)) return null;
        var parts = source[6..].Split('/');
        return parts.Length == 2
            && long.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var order)
            && int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var line)
            ? (order, line) : null;
    }
}

public enum InvoiceStatus
{
    Issued,

    /// <summary>Withdrawn with a reason; its shipments can be invoiced again.</summary>
    Voided,
}

/// <param name="ShipmentId">The confirmed shipment the line bills; a shipment is billed once, whole.</param>
public sealed record InvoiceLineInfo(int LineNumber, int OrderLine, long ShipmentId, string PartNumber, decimal Quantity, decimal UnitPrice, decimal Amount);

public sealed record InvoiceInfo(
    long InvoiceNumber,
    long OrderNumber,
    string CustomerCode,
    string CustomerName,
    string? CustomerPo,
    InvoiceStatus Status,
    DateOnly InvoiceDate,
    DateOnly DueDate,
    string Currency,
    decimal Subtotal,
    decimal TaxRatePercent,
    string? TaxExemptCertificate,
    decimal Tax,
    decimal Total,
    string IssuedBy,
    DateTimeOffset IssuedAt,
    string? VoidedBy,
    DateTimeOffset? VoidedAt,
    string? VoidReason,
    IReadOnlyList<InvoiceLineInfo> Lines);

/// <summary>A confirmed shipment against one sales order line: what an invoice may bill.</summary>
public sealed record SalesShipment(long ShipmentId, int OrderLine, string PartNumber, decimal Quantity, DateTimeOffset ShippedAt, string? Carrier, string? Tracking);

/// <summary>What other contexts may ask Sales.</summary>
public interface ISalesLookup
{
    Task<IReadOnlyList<CustomerInfo>> ListCustomersAsync(CancellationToken ct);

    Task<CustomerInfo?> FindCustomerAsync(string code, CancellationToken ct);

    /// <summary>Quotes, newest first.</summary>
    Task<IReadOnlyList<QuoteInfo>> ListQuotesAsync(CancellationToken ct);

    Task<QuoteInfo?> FindQuoteAsync(long quoteNumber, CancellationToken ct);

    /// <summary>Sales orders, newest first.</summary>
    Task<IReadOnlyList<SalesOrderInfo>> ListOrdersAsync(CancellationToken ct);

    Task<SalesOrderInfo?> FindOrderAsync(long orderNumber, CancellationToken ct);

    /// <summary>Invoices, newest first.</summary>
    Task<IReadOnlyList<InvoiceInfo>> ListInvoicesAsync(CancellationToken ct);

    Task<InvoiceInfo?> FindInvoiceAsync(long invoiceNumber, CancellationToken ct);
}

/// <summary>
/// Finds the confirmed shipments of a sales order inside the caller's transaction. Shipments belong to Inventory and the demand that
/// links them to an order line belongs to Planning, so the host, which can see both, implements this.
/// </summary>
public interface ISalesShipmentResolver
{
    Task<IReadOnlyList<SalesShipment>> ShippedAsync(Kernel.Access.IKernelUnitOfWork uow, long orderNumber, CancellationToken ct);
}

public static class Money
{
    public static readonly IReadOnlyList<string> Currencies = ["USD", "EUR", "GBP", "CAD"];

    /// <summary>Amounts are kept to the cent, half away from zero.</summary>
    public static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    /// <summary>Sales tax on an amount at a percent rate.</summary>
    public static decimal Tax(decimal subtotal, decimal ratePercent) => Round(subtotal * ratePercent / 100m);
}
