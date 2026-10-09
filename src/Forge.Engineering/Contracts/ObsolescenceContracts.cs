namespace Forge.Engineering.Contracts;

/// <summary>
/// How an MRO or spare item is stocked (V2.8): when what is on hand and on order falls to the reorder point,
/// MRP plans a purchase up to the maximum. A critical spare below its minimum is reported as an exception.
/// Every setting is kept; the latest is in force, and a cleared policy stops replenishment.
/// </summary>
public sealed record MroPolicyInfo(
    long PolicyId,
    string PartNumber,
    decimal MinQuantity,
    decimal ReorderPoint,
    decimal MaxQuantity,
    bool CriticalSpare,
    int? ShelfLifeDays,
    bool Cleared,
    string SetBy,
    DateTimeOffset SetAt,
    string Reason);

public interface IMroLookup
{
    /// <summary>The policy in force for each part that has one (cleared policies are left out).</summary>
    Task<IReadOnlyList<MroPolicyInfo>> ListMroPoliciesAsync(CancellationToken ct);

    /// <summary>Every setting of one part's policy, newest first.</summary>
    Task<IReadOnlyList<MroPolicyInfo>> MroHistoryAsync(string partNumber, CancellationToken ct);
}

/// <summary>Where an item stands in its manufacturer's life, as recorded with evidence.</summary>
public enum ObsolescenceStatus
{
    NotRecommended,
    LastTimeBuy,
    EndOfLife,
    Obsolete,
}

/// <summary>The state of the replacement for an obsolete item. None means nobody has named one — Forge never invents one.</summary>
public enum ReplacementStatus
{
    None,
    Proposed,
    Approved,
    Rejected,
}

/// <summary>
/// An obsolescence record (V2.8): what is known about an item's end of life, from what evidence, and the
/// replacement if one has been named and approved. Records are never edited; a newer record for the same
/// item supersedes it. An item without an approved replacement stays flagged.
/// </summary>
public sealed record ObsolescenceInfo(
    long RecordId,
    string PartNumber,
    ObsolescenceStatus Status,
    DateOnly? LastTimeBuy,
    DateOnly? EndOfLife,
    DateOnly? EndOfSupport,
    string Evidence,
    string RecordedBy,
    DateTimeOffset RecordedAt,
    string? ReplacementPart,
    ReplacementStatus Replacement,
    string? ReplacementProposedBy,
    DateTimeOffset? ReplacementProposedAt,
    string? ReplacementDecidedBy,
    DateTimeOffset? ReplacementDecidedAt,
    string? ReplacementReason)
{
    /// <summary>Still needs action: nothing approved stands in for it.</summary>
    public bool Flagged => Replacement != ReplacementStatus.Approved;
}

public interface IObsolescenceLookup
{
    /// <summary>All records, newest first; <paramref name="currentOnly"/> keeps only the latest record per item.</summary>
    Task<IReadOnlyList<ObsolescenceInfo>> ListObsolescenceAsync(bool currentOnly, CancellationToken ct);

    Task<ObsolescenceInfo?> FindObsolescenceAsync(long recordId, CancellationToken ct);
}
