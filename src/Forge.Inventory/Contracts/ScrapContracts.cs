namespace Forge.Inventory.Contracts;

/// <summary>Why stock is to be scrapped (Sprint 10 §20). A free-text condition and notes carry the detail.</summary>
public enum ScrapReasonCode
{
    Damaged,
    Obsolete,
    Expired,
    Contaminated,
    QualityReject,
    Other,
}

/// <summary>A scrap request, or a request to correct an executed one with a compensating entry.</summary>
public enum ScrapRequestKind
{
    Scrap,
    Reversal,
}

/// <summary>
/// Where a request stands. Only <see cref="Executed"/> changes stock by scrapping; a request that needs Quality
/// may already have moved its quantity onto hold, which a rejection or cancellation releases again.
/// </summary>
public enum ScrapRequestState
{
    /// <summary>Waiting for an independent Inventory approval.</summary>
    PendingApproval,

    /// <summary>Quality-held or quality-related material: waiting for an authorized Quality (MRB) disposition.</summary>
    PendingDisposition,

    /// <summary>Approved; a separate, authorized execution posts it.</summary>
    Approved,

    Rejected,
    Cancelled,

    /// <summary>Posted. Immutable; a mistake is corrected by a linked reversal request.</summary>
    Executed,
}

/// <summary>A site rule letting a role execute low-risk scrap directly (§20.2). Proposed, then approved by someone else.</summary>
public enum ScrapPolicyState
{
    Proposed,
    Active,
    Rejected,
    Retired,
}

/// <param name="RoleName">The role whose members may scrap directly under this rule.</param>
/// <param name="ReasonCode">The only reason the rule covers; null covers any reason except quality-related ones.</param>
/// <param name="MaxQuantity">The largest quantity one request may scrap directly; null is no quantity limit.</param>
/// <param name="MaxValue">The largest estimated value (last purchase price × quantity); when set, material without a known price never qualifies.</param>
public sealed record ScrapPolicy(
    long PolicyId,
    string RoleName,
    ScrapReasonCode? ReasonCode,
    decimal? MaxQuantity,
    decimal? MaxValue,
    ScrapPolicyState State,
    string ProposedBy,
    DateTimeOffset ProposedAt,
    string? DecidedBy,
    DateTimeOffset? DecidedAt);

/// <param name="StockStatus">The status the stock had when the request was made.</param>
/// <param name="ExecuteStatus">The status the quantity is scrapped from: Hold when the request placed it on hold for Quality.</param>
/// <param name="RequiresQuality">True when an authorized Quality disposition decides the request instead of Inventory.</param>
/// <param name="PolicyId">The direct-scrap rule that approved it, when no person did.</param>
/// <param name="EstimatedUnitValue">The part's last purchase price, when one is recorded; Forge has no costing module.</param>
/// <param name="HoldTransactionId">The hold this request placed on Released stock while it waits for Quality.</param>
/// <param name="ReleaseTransactionId">The release that undid that hold after a rejection or cancellation.</param>
/// <param name="CorrectsRequestId">For a reversal: the executed scrap request it compensates.</param>
public sealed record ScrapRequest(
    long RequestId,
    ScrapRequestKind Kind,
    string PartNumber,
    string LocationCode,
    string? LotNumber,
    StockStatus StockStatus,
    StockStatus ExecuteStatus,
    decimal Quantity,
    ScrapReasonCode ReasonCode,
    string? Condition,
    string? Notes,
    long? LinkedOrderNumber,
    long? LinkedPoNumber,
    long? LinkedWorkOrderId,
    string? LinkedNcr,
    string? LinkedSupplier,
    bool RequiresQuality,
    long? PolicyId,
    decimal? EstimatedUnitValue,
    string? Currency,
    ScrapRequestState State,
    string RequestedBy,
    DateTimeOffset RequestedAt,
    long? HoldTransactionId,
    string? DecidedBy,
    DateTimeOffset? DecidedAt,
    string? DecisionNote,
    string? ExecutedBy,
    DateTimeOffset? ExecutedAt,
    long? TransactionId,
    long? ReleaseTransactionId,
    long? CorrectsRequestId)
{
    /// <summary>Still holds its quantity back from other scrap requests.</summary>
    public bool Open => State is ScrapRequestState.PendingApproval or ScrapRequestState.PendingDisposition or ScrapRequestState.Approved;
}

/// <summary>
/// What could be scrapped from one stock bucket right now: on hand, less what material tasks hold, what other open
/// scrap requests claim and what is packed in containers. Tasks that are only allocated can be released by an
/// explicit choice at execution; picked material is in someone's hands and never is.
/// </summary>
public sealed record ScrapEligibility(
    string PartNumber,
    string LocationCode,
    string? LotNumber,
    StockStatus Status,
    decimal OnHand,
    decimal AllocatedToTasks,
    decimal PickedForTasks,
    decimal PendingScrap,
    decimal Packed,
    decimal Eligible,
    decimal EligibleIfAllocationsReleased,
    IReadOnlyList<long> AllocatedTaskIds);

public interface IScrapLookup
{
    Task<IReadOnlyList<ScrapRequest>> ListScrapRequestsAsync(bool openOnly, string? partNumber, CancellationToken ct);

    Task<ScrapRequest?> FindScrapRequestAsync(long requestId, CancellationToken ct);

    Task<IReadOnlyList<ScrapPolicy>> ListScrapPoliciesAsync(CancellationToken ct);
}
