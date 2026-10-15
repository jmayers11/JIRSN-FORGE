namespace Forge.Quality.Contracts;

public sealed record CommercialCaseQuality(IReadOnlyList<long> InspectionIds,IReadOnlyList<long> NcrIds,IReadOnlyList<long> CapaIds);
/// <summary>Complete owner query for exact orders/material identities, independent of ordinary presentation caps.</summary>
public interface ICommercialCaseLookup
{
    Task<CommercialCaseQuality> QualityForAsync(IReadOnlyCollection<long> orders,IReadOnlyCollection<string> partLotKeys,CancellationToken ct);
}

