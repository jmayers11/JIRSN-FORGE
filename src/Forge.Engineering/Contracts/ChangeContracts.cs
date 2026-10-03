namespace Forge.Engineering.Contracts;

public enum ChangeClass
{
    Major,
    Minor,
    Administrative,
}

public enum ChangeType
{
    Design,
    Documentation,
    Process,
    Supplier,
    Correction,
}

public enum ChangePriority
{
    Routine,
    Urgent,
    Emergency,
}

public enum ChangeStatus
{
    Draft,
    InReview,
    Approved,
    Released,
    Rejected,
    Cancelled,
}

public enum ChangeLineKind
{
    DocumentRevision,
    ItemRevision,
}

public enum ReviewKind
{
    Technical,
    Quality,
    Manufacturing,
    Procurement,
    Program,
    Maintenance,
}

public enum ReviewDecision
{
    Approve,
    ApproveWithComments,
    Reject,
}

/// <summary>One affected revision: a document or item revision the change moves from -> to.</summary>
public sealed record ChangeLineInfo(
    string ChangeNumber,
    int LineNumber,
    ChangeLineKind Kind,
    string? DocumentNumber,
    string? PartNumber,
    string? FromRevision,
    string ToRevision,
    string? Note,
    string AddedBy,
    DateTimeOffset AddedAt,
    string? RemovedBy,
    DateTimeOffset? RemovedAt)
{
    public string Subject => Kind == ChangeLineKind.DocumentRevision ? DocumentNumber! : PartNumber!;
}

/// <summary>A review the policy requires in one review round, and its decision once someone makes it.</summary>
public sealed record ChangeReviewInfo(string ChangeNumber, int Round, ReviewKind Kind, string RequiredBy, string? Reviewer, ReviewDecision? Decision, string? Notes, DateTimeOffset? DecidedAt);

public sealed record ChangeInfo(
    string Number,
    string Title,
    ChangeClass Class,
    ChangeType Type,
    ChangePriority Priority,
    string Reason,
    string? Description,
    Guid? ProgramId,
    string? Customer,
    DateOnly? RequestedEffective,
    ChangeStatus Status,
    int ReviewRound,
    string Originator,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SubmittedAt,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    string? ReleasedBy,
    DateTimeOffset? ReleasedAt,
    DateOnly? EffectiveFrom,
    string? ClosedBy,
    DateTimeOffset? ClosedAt,
    string? ClosedReason,
    IReadOnlyList<ChangeLineInfo> Lines,
    IReadOnlyList<ChangeReviewInfo> Reviews)
{
    public IEnumerable<ChangeLineInfo> ActiveLines => Lines.Where(l => l.RemovedAt is null);

    public IEnumerable<ChangeReviewInfo> CurrentReviews => Reviews.Where(r => r.Round == ReviewRound);

    public bool IsOpen => Status is ChangeStatus.Draft or ChangeStatus.InReview or ChangeStatus.Approved;
}

public sealed record ReviewPolicyInfo(
    long PolicyId,
    string Name,
    ChangeClass? ChangeClass,
    ChangeType? ChangeType,
    ItemClass? ItemClass,
    Guid? ProgramId,
    string? Customer,
    IReadOnlyList<ReviewKind> RequiredReviews,
    bool CombineApprovalRelease,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string? WithdrawnBy,
    DateTimeOffset? WithdrawnAt,
    string? WithdrawnReason);

/// <summary>The reviews a change needs and why.</summary>
public sealed record RequiredReviews(IReadOnlyList<ReviewKind> Kinds, bool CombineApprovalRelease, IReadOnlyList<string> Basis);

/// <summary>
/// Which reviews a change needs (V2 §4): every criterion a policy sets must match the change; the reviews
/// of all matching policies are required. With no match a Technical review is required, so a change is
/// never reviewed by fewer than one person besides its originator.
/// </summary>
public static class ReviewPolicies
{
    public static RequiredReviews For(IEnumerable<ReviewPolicyInfo> policies, ChangeInfo change, IReadOnlyCollection<ItemClass> itemClasses)
    {
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(itemClasses);
        var matching = policies.Where(p => p.WithdrawnAt is null
            && (p.ChangeClass is null || p.ChangeClass == change.Class)
            && (p.ChangeType is null || p.ChangeType == change.Type)
            && (p.ItemClass is null || itemClasses.Contains(p.ItemClass.Value))
            && (p.ProgramId is null || p.ProgramId == change.ProgramId)
            && (p.Customer is null || string.Equals(p.Customer.Trim(), change.Customer?.Trim(), StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (matching.Count == 0)
        {
            return new RequiredReviews([ReviewKind.Technical], false, ["No review policy matches: a technical review is always required."]);
        }

        var kinds = matching.SelectMany(p => p.RequiredReviews).Distinct().Order().ToList();
        return new RequiredReviews(kinds, matching.Any(p => p.CombineApprovalRelease), matching.Select(p => $"Policy {p.PolicyId} ({p.Name})").ToList());
    }
}

public interface IChangeLookup
{
    Task<IReadOnlyList<ChangeInfo>> ListChangesAsync(CancellationToken ct);

    Task<ChangeInfo?> FindChangeAsync(string number, CancellationToken ct);

    Task<IReadOnlyList<ReviewPolicyInfo>> ListReviewPoliciesAsync(bool includeWithdrawn, CancellationToken ct);
}
