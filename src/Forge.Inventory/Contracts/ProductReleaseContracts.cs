using Forge.Quality.Contracts;

namespace Forge.Inventory.Contracts;

public sealed record ProductReleaseConsumption(long DecisionId,long TransactionId,string PartNumber,string LotNumber,string LocationCode,decimal Quantity,string Actor,DateTimeOffset At,string PolicyFingerprint,string EvidenceFingerprint);
public sealed record ReleasedScopeCoverage(IReadOnlyList<ProductReleaseConsumption> Consumptions,decimal RemainingAuthorizedQuantity,decimal ActualReleasedQuantity);
public interface IProductReleaseInventoryLookup:IProductReleaseConsumptionLookup
{
    Task<ReleasedScopeCoverage> CoverageAsync(string part,IReadOnlyList<string> connectedLots,CancellationToken ct);
}
internal interface IProductReleaseInventoryStore:IProductReleaseInventoryLookup
{
    Task LockShipmentAsync(long shipmentId,CancellationToken ct);
    Task LockContainerAsync(string code,CancellationToken ct);
    Task InsertConsumptionAsync(ProductReleaseConsumption consumption,CancellationToken ct);
}
