using System.Globalization;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Resources.Contracts;

namespace Forge.Sales.Contracts;

// Getting paid (owner decisions 2026-11-01): billing clerks record customer payments against invoices. A customer over their credit
// limit, or with an invoice past due beyond the plant's grace days, is on credit hold: new sales orders are not approved and nothing
// ships until a sales manager or credit manager releases that order (or shipment) with a reason. Each company can turn this off.

public enum PaymentMethod
{
    Check,
    Ach,
    Wire,
    Card,
    Other,
}

public enum PaymentStatus
{
    Recorded,
    Voided,
}

public sealed record PaymentApplication(long InvoiceNumber, decimal Amount);

/// <param name="Unapplied">What is left on account after the applications (a credit the next invoices use).</param>
public sealed record PaymentInfo(
    long PaymentId,
    string CustomerCode,
    DateOnly ReceivedOn,
    decimal Amount,
    string Currency,
    PaymentMethod Method,
    string? Reference,
    string? Note,
    PaymentStatus Status,
    string RecordedBy,
    DateTimeOffset RecordedAt,
    string? VoidedBy,
    DateTimeOffset? VoidedAt,
    string? VoidReason,
    IReadOnlyList<PaymentApplication> Applications)
{
    public decimal Unapplied => Amount - Applications.Sum(a => a.Amount);
}

/// <summary>An issued invoice with what has been paid on it.</summary>
public sealed record InvoiceBalance(long InvoiceNumber, long OrderNumber, string CustomerCode, DateOnly InvoiceDate, DateOnly DueDate, string Currency, decimal Total, decimal Paid,
    decimal Balance, int DaysPastDue);

/// <param name="PastDueDays">Hold when an invoice is more than this many days past due; null never holds for lateness.</param>
/// <param name="IncludeOpenOrders">Count approved, not yet invoiced sales orders toward the credit limit.</param>
public sealed record CreditPolicy(int? PastDueDays, bool IncludeOpenOrders, string SetBy, DateTimeOffset SetAt)
{
    public static CreditPolicy Default { get; } = new(30, true, "system:default", DateTimeOffset.UnixEpoch);
}

/// <summary>A customer's credit limit; null means no limit (only lateness can hold them).</summary>
public sealed record CustomerCredit(string CustomerCode, decimal? CreditLimit, string UpdatedBy, DateTimeOffset UpdatedAt);

/// <summary>A sales manager or credit manager let one sales order (its approval and its shipments) or one shipment through a credit hold.</summary>
public sealed record CreditRelease(long ReleaseId, string CustomerCode, long? SalesOrderNumber, long? ShipmentId, string Reason, string ReleasedBy, DateTimeOffset ReleasedAt);

public sealed record AgingBuckets(decimal Current, decimal Days1To30, decimal Days31To60, decimal Days61To90, decimal Over90)
{
    public decimal Total => Current + Days1To30 + Days31To60 + Days61To90 + Over90;
}

/// <summary>Where a customer stands: what they owe, how late, what is on order, and whether they are on credit hold and why.</summary>
public sealed record CustomerCreditStatus(
    string CustomerCode,
    string CustomerName,
    string Currency,
    decimal OpenInvoices,
    decimal OnAccount,
    decimal OpenOrders,
    decimal Exposure,
    decimal? CreditLimit,
    int OldestDaysPastDue,
    AgingBuckets Aging,
    bool OnHold,
    IReadOnlyList<string> Reasons);

public interface IReceivablesLookup
{
    Task<IReadOnlyList<PaymentInfo>> ListPaymentsAsync(string? customerCode, CancellationToken ct);

    Task<PaymentInfo?> FindPaymentAsync(long paymentId, CancellationToken ct);

    Task<CreditPolicy> CreditPolicyAsync(CancellationToken ct);

    Task<IReadOnlyList<CustomerCredit>> ListCustomerCreditAsync(CancellationToken ct);

    Task<IReadOnlyList<CreditRelease>> ListCreditReleasesAsync(string? customerCode, CancellationToken ct);
}

/// <summary>Balances, aging and credit standing, computed from invoices and payments inside the caller's transaction.</summary>
public static class Receivables
{
    public static async Task<IReadOnlyList<InvoiceBalance>> BalancesAsync(IKernelUnitOfWork uow, DateOnly today, string? customerCode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var invoices = (await uow.Store<ISalesLookup>().ListInvoicesAsync(ct))
            .Where(i => i.Status == InvoiceStatus.Issued && (customerCode is null || i.CustomerCode == customerCode)).ToList();
        var paid = (await uow.Store<IReceivablesLookup>().ListPaymentsAsync(customerCode, ct)).Where(p => p.Status == PaymentStatus.Recorded)
            .SelectMany(p => p.Applications).GroupBy(a => a.InvoiceNumber).ToDictionary(g => g.Key, g => g.Sum(a => a.Amount));
        return invoices.Select(i =>
        {
            var p = paid.GetValueOrDefault(i.InvoiceNumber);
            var balance = i.Total - p;
            var late = balance > 0 && today > i.DueDate ? today.DayNumber - i.DueDate.DayNumber : 0;
            return new InvoiceBalance(i.InvoiceNumber, i.OrderNumber, i.CustomerCode, i.InvoiceDate, i.DueDate, i.Currency, i.Total, p, balance, late);
        }).OrderBy(b => b.DueDate).ThenBy(b => b.InvoiceNumber).ToList();
    }

    public static async Task<IReadOnlyList<CustomerCreditStatus>> StatusAsync(IKernelUnitOfWork uow, DateOnly today, string? customerCode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var sales = uow.Store<ISalesLookup>();
        var receivables = uow.Store<IReceivablesLookup>();
        var customers = (await sales.ListCustomersAsync(ct)).Where(c => customerCode is null || c.Code == customerCode).ToList();
        var balances = (await BalancesAsync(uow, today, customerCode, ct)).GroupBy(b => b.CustomerCode).ToDictionary(g => g.Key, g => g.ToList());
        var onAccount = (await receivables.ListPaymentsAsync(customerCode, ct)).Where(p => p.Status == PaymentStatus.Recorded)
            .GroupBy(p => p.CustomerCode).ToDictionary(g => g.Key, g => g.Sum(p => p.Unapplied));
        var invoices = await sales.ListInvoicesAsync(ct);
        var invoicedByOrder = invoices.Where(i => i.Status == InvoiceStatus.Issued).GroupBy(i => i.OrderNumber).ToDictionary(g => g.Key, g => g.Sum(i => i.Subtotal));
        var openOrders = (await sales.ListOrdersAsync(ct)).Where(o => o.Status == SalesOrderStatus.Approved)
            .GroupBy(o => o.CustomerCode).ToDictionary(g => g.Key, g => g.Sum(o => Math.Max(0, o.Total - invoicedByOrder.GetValueOrDefault(o.OrderNumber))));
        var limits = (await receivables.ListCustomerCreditAsync(ct)).ToDictionary(c => c.CustomerCode);
        var policy = await receivables.CreditPolicyAsync(ct);
        var result = new List<CustomerCreditStatus>();
        foreach (var c in customers)
        {
            var mine = balances.GetValueOrDefault(c.Code) ?? [];
            var open = mine.Sum(b => b.Balance);
            var credit = onAccount.GetValueOrDefault(c.Code);
            var orders = openOrders.GetValueOrDefault(c.Code);
            var exposure = open - credit + (policy.IncludeOpenOrders ? orders : 0);
            var limit = limits.GetValueOrDefault(c.Code)?.CreditLimit;
            var oldest = mine.Count == 0 ? 0 : mine.Max(b => b.DaysPastDue);
            var aging = new AgingBuckets(
                mine.Where(b => b.DaysPastDue == 0).Sum(b => b.Balance), mine.Where(b => b.DaysPastDue is >= 1 and <= 30).Sum(b => b.Balance),
                mine.Where(b => b.DaysPastDue is >= 31 and <= 60).Sum(b => b.Balance), mine.Where(b => b.DaysPastDue is >= 61 and <= 90).Sum(b => b.Balance),
                mine.Where(b => b.DaysPastDue > 90).Sum(b => b.Balance));
            var reasons = new List<string>();
            if (limit is { } l && exposure > l)
                reasons.Add(string.Create(CultureInfo.InvariantCulture, $"owes and has on order {exposure:N2} {c.Currency}, over the {l:N2} credit limit"));
            if (policy.PastDueDays is { } grace && oldest > grace)
                reasons.Add(string.Create(CultureInfo.InvariantCulture, $"an invoice is {oldest} days past due (the plant holds after {grace})"));
            result.Add(new CustomerCreditStatus(c.Code, c.Name, c.Currency, open, credit, orders, exposure, limit, oldest, aging, reasons.Count > 0, reasons));
        }

        return result;
    }
}

/// <summary>The credit hold rule sales order approval and shipping apply (owner decision 2026-11-01).</summary>
public static class CreditGate
{
    /// <summary>Refuses approving a sales order for a customer on credit hold (counting this order) unless the order was released.</summary>
    public static async Task RequireOrderAsync(IKernelUnitOfWork uow, string customerCode, long orderNumber, decimal orderValue, DateOnly today, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        if (!await uow.IsOnAsync(SiteFeatures.Receivables, ct)) return;
        var status = (await Receivables.StatusAsync(uow, today, customerCode, ct)).SingleOrDefault();
        if (status is null) return;
        var policy = await uow.Store<IReceivablesLookup>().CreditPolicyAsync(ct);
        var reasons = status.Reasons.ToList();
        if (!status.OnHold && status.CreditLimit is { } limit && policy.IncludeOpenOrders && status.Exposure + orderValue > limit)
            reasons.Add(string.Create(CultureInfo.InvariantCulture, $"this order would bring them to {status.Exposure + orderValue:N2} {status.Currency}, over the {limit:N2} credit limit"));
        if (reasons.Count == 0) return;
        if ((await uow.Store<IReceivablesLookup>().ListCreditReleasesAsync(customerCode, ct)).Any(r => r.SalesOrderNumber == orderNumber)) return;
        throw new CommandRejectedException("credit_hold",
            $"{status.CustomerName} is on credit hold: {string.Join("; ", reasons)}. A sales manager or credit manager can release sales order {orderNumber}.");
    }

    /// <summary>Refuses shipping to a customer on credit hold unless the shipment, or the sales order it ships, was released.</summary>
    public static async Task RequireShipmentAsync(IKernelUnitOfWork uow, string customerCode, long? salesOrderNumber, long shipmentId, DateOnly today, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        if (!await uow.IsOnAsync(SiteFeatures.Receivables, ct)) return;
        var status = (await Receivables.StatusAsync(uow, today, customerCode, ct)).SingleOrDefault();
        if (status is not { OnHold: true }) return;
        var releases = await uow.Store<IReceivablesLookup>().ListCreditReleasesAsync(customerCode, ct);
        if (releases.Any(r => r.ShipmentId == shipmentId || (salesOrderNumber is { } so && r.SalesOrderNumber == so))) return;
        throw new CommandRejectedException("credit_hold",
            $"{status.CustomerName} is on credit hold: {string.Join("; ", status.Reasons)}. A sales manager or credit manager can release shipment {shipmentId}{(salesOrderNumber is { } o ? $" or sales order {o}" : string.Empty)}.");
    }
}
