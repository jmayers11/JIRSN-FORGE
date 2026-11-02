namespace Forge.Inventory.Contracts;

/// <summary>How competing released orders are ranked when free stock cannot cover them all (owner decisions 2026-11-02).</summary>
public enum AllocationRule
{
    /// <summary>Earliest due date first.</summary>
    DueDate,

    /// <summary>Higher order priority (lower number) first.</summary>
    Priority,

    /// <summary>The smallest shortage first, so more orders become complete.</summary>
    SmallestShortage,

    /// <summary>Released earliest first.</summary>
    ReleasedFirst,
}

public sealed record AllocationPolicy(IReadOnlyList<AllocationRule> Rules, string SetBy, DateTimeOffset SetAt)
{
    public static AllocationPolicy Default { get; } = new([AllocationRule.DueDate, AllocationRule.Priority, AllocationRule.SmallestShortage], "system:default", DateTimeOffset.UnixEpoch);
}

public enum AllocationMode
{
    /// <summary>The part the order's BOM calls for.</summary>
    Primary,

    /// <summary>An approved alternate the BOM line lets be used automatically.</summary>
    AutomaticAlternate,

    /// <summary>An approved alternate the BOM line only suggests: a person reviews it before it is used.</summary>
    ReviewAlternate,
}

/// <param name="Quantity">In the offered part's units; for an alternate, <see cref="PrimaryQuantity"/> is what it covers.</param>
public sealed record AllocationLine(long OrderNumber, DateOnly DueDate, int Priority, int Rank, string NeededPart, decimal Shortage, string OfferedPart, decimal Quantity, decimal PrimaryQuantity,
    AllocationMode Mode, long? AlternateId, string Why);

/// <summary>A shortage the proposal cannot cover, and what is in the way.</summary>
public sealed record AllocationGap(long OrderNumber, string PartNumber, decimal Missing, string Why);

/// <summary>A proposal for one needed part: what is free, who gets it and why, who stays short, and what was ruled out.</summary>
public sealed record AllocationProposal(string PartNumber, decimal OnHand, decimal Unavailable, decimal ReservedForOthers, decimal Free, IReadOnlyList<AllocationLine> Lines,
    IReadOnlyList<AllocationGap> StillShort, IReadOnlyList<string> RuledOut);

/// <summary>An alternate part reserved for an order in place of the part its BOM calls for.</summary>
public sealed record ReservationAlternate(long ReservationId, string PrimaryPart, decimal Factor, long? AlternateId);

/// <summary>Alternates issued to an order, in the alternate's units and in the primary units they stood for.</summary>
public sealed record AlternateIssue(long OrderNumber, string PartNumber, string PrimaryPart, decimal Quantity, decimal PrimaryQuantity);

public interface IAllocationLookup
{
    Task<AllocationPolicy> AllocationPolicyAsync(CancellationToken ct);

    Task<IReadOnlyList<ReservationAlternate>> ListReservationAlternatesAsync(CancellationToken ct);

    Task<IReadOnlyList<AlternateIssue>> ListAlternateIssuesAsync(CancellationToken ct);
}
