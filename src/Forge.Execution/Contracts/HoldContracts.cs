namespace Forge.Execution.Contracts;

/// <summary>What a production hold stops (V3.9).</summary>
public enum HoldScope
{
    Order,
    Operation,
    Part,
    WorkCenter,
    Machine,
}

/// <summary>
/// A production hold (V3.9): work on its target stops until a different person releases it, with a reason. Quality's
/// holds (an undecided NCR, an inspection not passed) are separate and come from Quality through <see cref="IWorkHoldLookup"/>.
/// </summary>
public sealed record ProductionHold(
    long HoldId,
    HoldScope Scope,
    long? OrderNumber,
    int? Sequence,
    string? PartNumber,
    string? WorkCenterCode,
    string? MachineCode,
    string Reason,
    DateTimeOffset PlacedAt,
    string PlacedBy,
    DateTimeOffset? ReleasedAt = null,
    string? ReleasedBy = null,
    string? ReleaseReason = null)
{
    public bool Active => ReleasedAt is null;

    public string Target => Scope switch
    {
        HoldScope.Order => $"order {OrderNumber}",
        HoldScope.Operation => $"operation {OrderNumber}/{Sequence}",
        HoldScope.Part => $"part {PartNumber}",
        HoldScope.WorkCenter => $"work center {WorkCenterCode}",
        _ => $"machine {MachineCode}",
    };

    public string Describe() => $"Hold {HoldId} on {Target}: {Reason}";

    /// <summary>
    /// Whether this hold stops an operation of an order. A machine hold stops the operation only on that machine
    /// (pass null when the machine is not chosen yet: then only holds that stop every candidate count).
    /// </summary>
    public bool Covers(long orderNumber, string partNumber, int sequence, string workCenterCode, string? machineCode) => Active && Scope switch
    {
        HoldScope.Order => OrderNumber == orderNumber,
        HoldScope.Operation => OrderNumber == orderNumber && Sequence == sequence,
        HoldScope.Part => PartNumber == partNumber,
        HoldScope.WorkCenter => WorkCenterCode == workCenterCode,
        _ => machineCode is not null && MachineCode == machineCode,
    };

    /// <summary>Whether it stops reporting good output on the order (whole-order and part holds).</summary>
    public bool CoversOrder(long orderNumber, string partNumber) => Active && Scope switch
    {
        HoldScope.Order => OrderNumber == orderNumber,
        HoldScope.Part => PartNumber == partNumber,
        _ => false,
    };
}

public interface IProductionHoldLookup
{
    /// <summary>Holds, newest first; only active ones unless <paramref name="includeReleased"/>.</summary>
    Task<IReadOnlyList<ProductionHold>> ListProductionHoldsAsync(bool includeReleased, CancellationToken ct);
}

public interface IProductionHoldStore
{
    Task<long> InsertProductionHoldAsync(ProductionHold hold, CancellationToken ct);

    Task ReleaseProductionHoldAsync(long holdId, DateTimeOffset at, string actor, string reason, CancellationToken ct);
}
