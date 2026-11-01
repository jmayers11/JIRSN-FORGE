using Forge.Kernel.Events;
using Forge.Sales.Contracts;

namespace Forge.Sales;

public sealed record CustomerChanged(string Code, string Change) : IDomainEvent
{
    public static string EventType => nameof(CustomerChanged);

    public static int SchemaVersion => 1;
}

public sealed record QuoteChanged(long QuoteNumber, string Change) : IDomainEvent
{
    public static string EventType => nameof(QuoteChanged);

    public static int SchemaVersion => 1;
}

/// <summary>A sales order changed. Planning brings the order's customer-order demand in step with it.</summary>
public sealed record SalesOrderChanged(long OrderNumber, string Change) : IDomainEvent
{
    public static string EventType => nameof(SalesOrderChanged);

    public static int SchemaVersion => 1;
}

public sealed record InvoiceChanged(long InvoiceNumber, long OrderNumber, string Change) : IDomainEvent
{
    public static string EventType => nameof(InvoiceChanged);

    public static int SchemaVersion => 1;
}

public interface ISalesStore
{
    Task<bool> InsertCustomerAsync(CustomerInfo customer, CancellationToken ct);

    Task<bool> CustomerNameTakenAsync(string name, string exceptCode, CancellationToken ct);

    Task UpdateCustomerAsync(CustomerInfo customer, DateTimeOffset at, string actor, CancellationToken ct);

    Task LockQuoteAsync(long quoteNumber, CancellationToken ct);

    Task<long> InsertQuoteAsync(QuoteInfo quote, CancellationToken ct);

    Task InsertQuoteLineAsync(long quoteNumber, QuoteLineInfo line, CancellationToken ct);

    Task DeleteQuoteLineAsync(long quoteNumber, int lineNumber, CancellationToken ct);

    Task SetQuoteStatusAsync(long quoteNumber, QuoteStatus status, DateTimeOffset at, string actor, long? salesOrderNumber, CancellationToken ct);

    Task LockOrderAsync(long orderNumber, CancellationToken ct);

    Task<long> InsertOrderAsync(SalesOrderInfo order, CancellationToken ct);

    Task InsertOrderLineAsync(long orderNumber, SalesOrderLineInfo line, CancellationToken ct);

    Task UpdateOrderLineAsync(long orderNumber, SalesOrderLineInfo line, CancellationToken ct);

    Task SetOrderStatusAsync(long orderNumber, SalesOrderStatus status, DateTimeOffset at, string actor, CancellationToken ct);

    /// <summary>The shipments already billed on issued invoices of the order.</summary>
    Task<IReadOnlySet<long>> InvoicedShipmentsAsync(long orderNumber, CancellationToken ct);

    Task<long> InsertInvoiceAsync(InvoiceInfo invoice, CancellationToken ct);

    Task VoidInvoiceAsync(long invoiceNumber, DateTimeOffset at, string actor, string reason, CancellationToken ct);
}
