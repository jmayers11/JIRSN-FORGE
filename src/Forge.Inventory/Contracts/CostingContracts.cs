namespace Forge.Inventory.Contracts;

/// <summary>
/// Inventory valuation methods accepted under US GAAP. Standard cost is the plant's method (owner decision 2026-10-25); weighted average
/// and FIFO need actual-cost layers from receipts and production, which Forge does not record yet, so a policy can name them only once
/// those layers exist.
/// </summary>
public enum CostingMethod { Standard, WeightedAverage, Fifo }

/// <summary>
/// The costing policy in force from <see cref="EffectiveFrom"/>: the method, the currency, and the plant labor and overhead rates per
/// direct labor hour used to cost added work (full absorption). A change applies going forward only and needs a reason; earlier
/// valuations are never rewritten.
/// </summary>
public sealed record CostingPolicy(long PolicyId, CostingMethod Method, string Currency, decimal? LaborRatePerHour, decimal? OverheadRatePerHour,
    DateTimeOffset EffectiveFrom, string SetBy, string Reason);

/// <summary>A part's standard unit cost from <see cref="EffectiveFrom"/>, split into material, labor and overhead.</summary>
public sealed record StandardCost(long CostId, string PartNumber, decimal Material, decimal Labor, decimal Overhead, string Currency,
    DateTimeOffset EffectiveFrom, string SetBy, string Reason)
{
    public decimal Total => Material + Labor + Overhead;
}

public interface ICostingLookup
{
    /// <summary>Every policy version, oldest first.</summary>
    Task<IReadOnlyList<CostingPolicy>> ListPoliciesAsync(CancellationToken ct);

    /// <summary>Every standard cost version (of one part when given), oldest first.</summary>
    Task<IReadOnlyList<StandardCost>> ListStandardCostsAsync(string? partNumber, CancellationToken ct);
}

public interface ICostingStore : ICostingLookup
{
    Task<long> InsertPolicyAsync(CostingPolicy policy, CancellationToken ct);

    Task<long> InsertStandardCostAsync(StandardCost cost, CancellationToken ct);
}

/// <summary>Which version applies at an instant: the latest that took effect at or before it.</summary>
public static class Costing
{
    public static CostingPolicy? PolicyAt(IEnumerable<CostingPolicy> policies, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(policies);
        return policies.Where(p => p.EffectiveFrom <= at).MaxBy(p => (p.EffectiveFrom, p.PolicyId));
    }

    public static StandardCost? StandardAt(IEnumerable<StandardCost> costs, string partNumber, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(costs);
        return costs.Where(c => c.PartNumber == partNumber && c.EffectiveFrom <= at).MaxBy(c => (c.EffectiveFrom, c.CostId));
    }
}
