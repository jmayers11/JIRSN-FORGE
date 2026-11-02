using Forge.Kernel.Access;

namespace Forge.Procurement.Contracts;

// Paying suppliers (owner decisions 2026-11-01): an Accounts Payable Clerk enters each supplier invoice against its purchase order. Forge
// matches it three ways: the invoice against the PO price and against what was received and not yet invoiced. Within the plant's
// tolerance it is approved for payment; otherwise a purchasing manager (not the person who entered it) decides. Approved invoices go
// to the accounting system in the payables export; Forge keeps no ledger and pays nobody itself.

public enum SupplierInvoiceStatus
{
    /// <summary>Matched within tolerance, or approved by a purchasing manager: ready for the accounting system to pay.</summary>
    Approved,

    /// <summary>Outside tolerance (price, or billed for more than was received): waits for a purchasing manager.</summary>
    Exception,

    /// <summary>A purchasing manager refused it; the supplier is asked for a corrected invoice.</summary>
    Rejected,

    /// <summary>Entered in error and withdrawn.</summary>
    Voided,

    /// <summary>The accounting system paid it.</summary>
    Paid,
}

/// <param name="PoUnitPrice">The purchase order's price for the line.</param>
/// <param name="ReceivedOpen">Received on the PO line and not already on another live invoice, when the invoice was entered.</param>
/// <param name="Variance">Amount billed above (or below) PO price × the received quantity it may bill.</param>
public sealed record SupplierInvoiceLine(int LineNumber, int PoLine, string PartNumber, decimal Quantity, decimal UnitPrice, decimal Amount, decimal? PoUnitPrice, decimal ReceivedOpen,
    decimal Variance, string? Problem);

public sealed record SupplierInvoiceInfo(
    long InvoiceId,
    string SupplierCode,
    string SupplierName,
    string SupplierInvoiceNumber,
    long PoNumber,
    DateOnly InvoiceDate,
    DateOnly DueDate,
    string Currency,
    decimal Subtotal,
    decimal Freight,
    decimal Tax,
    decimal Total,
    decimal Variance,
    SupplierInvoiceStatus Status,
    string MatchSummary,
    string EnteredBy,
    DateTimeOffset EnteredAt,
    string? DecidedBy,
    DateTimeOffset? DecidedAt,
    string? DecisionNote,
    DateOnly? PaidOn,
    string? PaymentReference,
    IReadOnlyList<SupplierInvoiceLine> Lines);

/// <summary>The plant's three-way match tolerance: an invoice is within it when its variance is no more than both limits.</summary>
public sealed record InvoiceMatchPolicy(decimal TolerancePercent, decimal ToleranceAmount, string SetBy, DateTimeOffset SetAt)
{
    public static InvoiceMatchPolicy Default { get; } = new(2m, 50m, "system:default", DateTimeOffset.UnixEpoch);

    /// <summary>Whether a variance on an expected amount is within tolerance: at most the percent of it and at most the amount.</summary>
    public bool Within(decimal variance, decimal expected) =>
        Math.Abs(variance) <= ToleranceAmount && Math.Abs(variance) <= Math.Abs(expected) * TolerancePercent / 100m;
}

public interface ISupplierInvoiceLookup
{
    Task<IReadOnlyList<SupplierInvoiceInfo>> ListSupplierInvoicesAsync(CancellationToken ct);

    Task<SupplierInvoiceInfo?> FindSupplierInvoiceAsync(long invoiceId, CancellationToken ct);

    Task<InvoiceMatchPolicy> MatchPolicyAsync(CancellationToken ct);
}

/// <summary>
/// Received quantities per purchase order line, read inside the caller's transaction. Receipts belong to Inventory, which depends on
/// Procurement, so the host implements this.
/// </summary>
public interface IPurchaseReceiptSource
{
    Task<IReadOnlyDictionary<(long PoNumber, int LineNumber), decimal>> ReceivedAsync(IKernelUnitOfWork uow, long poNumber, CancellationToken ct);
}
