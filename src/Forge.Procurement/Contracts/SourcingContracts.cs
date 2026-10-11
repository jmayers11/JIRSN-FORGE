using Forge.Kernel.Access;

namespace Forge.Procurement.Contracts;

public enum RfqStatus { Draft, Released, Selected, Cancelled }

/// <summary>The existing requisition remains the single sourcing need; RFQ does not create projected supply.</summary>
public sealed record RfqInfo(long RfqId, long RequisitionId, string Title, IReadOnlyList<string> SupplierCodes, RfqStatus Status,
    string CreatedBy, DateTimeOffset CreatedAt, string? ReleasedBy, DateTimeOffset? ReleasedAt,
    SourcingPackage? Package, long? SelectedQuoteId, string? SelectedBy, DateTimeOffset? SelectedAt, string? SelectionReason);

/// <summary>The applicable technical package actually sent; later engineering revisions never rewrite it.</summary>
public sealed record SourcingPackage(long RfqId, string PartNumber, string? ItemRevision, string Summary,
    DateTimeOffset RecordedAt, string RecordedBy, IReadOnlyList<PackageDocument> Documents);

public sealed record SupplierQuote(long QuoteId, long RfqId, string SupplierCode, decimal Quantity, decimal UnitPrice, string Currency,
    DateOnly PromisedDate, DateOnly ValidThrough, string? ProposedAlternatePart, string OriginalDocumentNumber, string OriginalDocumentRevision,
    string RecordedBy, DateTimeOffset RecordedAt, string? Note);

public interface ISourcingLookup
{
    Task<IReadOnlyList<RfqInfo>> ListRfqsAsync(CancellationToken ct);
    Task<RfqInfo?> FindRfqAsync(long rfqId, CancellationToken ct);
    Task<IReadOnlyList<SupplierQuote>> ListQuotesAsync(long? rfqId, CancellationToken ct);
    Task<SupplierQuote?> SelectedQuoteForRequisitionAsync(long requisitionId, CancellationToken ct);
}

public interface ISourcingStore
{
    Task LockRfqAsync(long rfqId, CancellationToken ct);
    Task<long> InsertRfqAsync(RfqInfo rfq, CancellationToken ct);
    Task UpdateRfqAsync(RfqInfo rfq, CancellationToken ct);
    Task<long> InsertQuoteAsync(SupplierQuote quote, CancellationToken ct);
}

public interface ISourcingPackageResolver
{
    Task<SourcingPackage> ResolveAsync(IKernelUnitOfWork uow, long rfqId, RequisitionInfo need, DateTimeOffset now, string actor, CancellationToken ct);
}
