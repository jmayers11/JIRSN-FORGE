using Forge.Procurement.Contracts;
using Forge.Kernel.Security;

namespace Forge.Inventory.Contracts;

/// <summary>Immutable configuration sent with company-owned material; quantities remain in the inventory ledger.</summary>
public sealed record OutsideProcessDispatchInfo(long TransactionId, string Key, string PartNumber, string LotNumber,
    string? MaterialRevision, long PurchaseOrder, int PurchaseOrderLine, string ProcessorLocation,
    string? SpecificationDocument, string? SpecificationRevision, DateOnly? ExpectedReturnDate,
    bool RequireCertificate, TechnicalPackage TechnicalPackage, DateTimeOffset DispatchedAt, string DispatchedBy,
    Guid? ProgramId = null, DataMarking Markings = DataMarking.None)
{
    public string Ownership { get; } = "Company";
    public string IdentityPolicy { get; } = "Preserved";
}

public sealed record OutsideProcessReturnInfo(long TransactionId, string Key, string PartNumber, string LotNumber,
    string LocationCode, DateTimeOffset ReturnedAt, string ReturnedBy);

public interface IOutsideProcessLookup
{
    Task<IReadOnlyList<OutsideProcessDispatchInfo>> ListDispatchesAsync(string? key, CancellationToken ct);
    Task<OutsideProcessReturnInfo?> FindReturnAsync(long transactionId, CancellationToken ct);
    Task<IReadOnlyList<OutsideProcessReturnInfo>> ListReturnsAsync(string? key, CancellationToken ct);
}

public interface IOutsideProcessStore
{
    Task InsertDispatchAsync(OutsideProcessDispatchInfo dispatch, CancellationToken ct);
    Task InsertReturnAsync(OutsideProcessReturnInfo returned, CancellationToken ct);
}
