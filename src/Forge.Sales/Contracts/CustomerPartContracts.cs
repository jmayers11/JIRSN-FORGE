namespace Forge.Sales.Contracts;

/// <summary>A customer's own number for one of our parts (owner decisions 2026-11-01: EDI orders often carry the buyer's part number).</summary>
public sealed record CustomerPartInfo(string CustomerCode, string CustomerPart, string PartNumber, string? Description, string UpdatedBy, DateTimeOffset UpdatedAt);

public interface ICustomerPartLookup
{
    /// <summary>One customer's part numbers, or every customer's.</summary>
    Task<IReadOnlyList<CustomerPartInfo>> ListCustomerPartsAsync(string? customerCode, CancellationToken ct);

    Task<CustomerPartInfo?> FindCustomerPartAsync(string customerCode, string customerPart, CancellationToken ct);
}
