using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Kernel.Engines;
using Forge.Resources.Contracts;

namespace Forge.Planning.Engines;

/// <summary>What is known about the work a document is checked for (an order, or a purchase).</summary>
public sealed record ApplicabilityFacts(long? OrderNumber, string PartNumber, string ItemRevision, Guid? ProgramId, string? Customer, DateOnly? DueDate);

/// <summary>Why a document must be followed: the item revision is built to it, or it was attached to follow.</summary>
public sealed record DocumentNeed(string DocumentNumber, string? PinnedRevision, int? OperationSequence, string Source, DateTimeOffset AddedAt);

public enum DocumentState
{
    /// <summary>The revision that applies is released (or applicable) and is the one the work references.</summary>
    Current,

    /// <summary>The requirement names a revision; a newer one exists, but this work is built to the pinned one.</summary>
    Pinned,

    /// <summary>Released with the revision in force then; a newer revision took effect later. Work in progress continues.</summary>
    LegitimatelySuperseded,

    /// <summary>The revision the work references is not the one that applies.</summary>
    Mismatch,

    /// <summary>No revision applies.</summary>
    Missing,

    /// <summary>The revision that applies is not released.</summary>
    NotReleased,

    /// <summary>The revision that applies was made obsolete.</summary>
    Obsolete,

    /// <summary>The rules do not decide (they disagree). A person must decide; Forge never guesses.</summary>
    RequiresReview,
}

public enum ResolutionBasis
{
    Pinned,
    Deviation,
    Rule,
    DateEffectivity,
    Conflict,
    None,
}

/// <summary>The revision of a document that applies at an instant, and why.</summary>
public sealed record Resolution(DocumentRevisionInfo? Revision, ResolutionBasis Basis, string Explanation);

public sealed record DocumentApplicability(
    string DocumentNumber,
    string Title,
    DocumentAuthority Authority,
    string Source,
    int? OperationSequence,
    string? PinnedRevision,
    string? AtRelease,
    string? Applicable,
    DocumentState State,
    EvidenceOutcome Outcome,
    string Explanation);

/// <summary>Every document an order must follow, the revision that applies and whether the order matches it.</summary>
public sealed record OrderDocumentation(long OrderNumber, string PartNumber, string Revision, ItemClass ItemClass, bool Enforced, IReadOnlyList<DocumentApplicability> Documents, string Summary)
{
    /// <summary>The documentation readiness dimension for one operation (whole-item documents count for every operation).</summary>
    public ReadinessDimension DimensionFor(int sequence)
    {
        var relevant = Documents.Where(d => d.OperationSequence is null || d.OperationSequence == sequence).ToList();
        if (relevant.Count == 0)
        {
            return new ReadinessDimension("Documentation", EvidenceOutcome.Pass, "No documents are required for this operation.");
        }

        var failed = relevant.Where(d => d.Outcome == EvidenceOutcome.Fail).ToList();
        var review = relevant.Where(d => d.Outcome == EvidenceOutcome.Indeterminate).ToList();
        return failed.Count > 0
            ? new ReadinessDimension("Documentation", EvidenceOutcome.Fail, string.Join(" ", failed.Select(d => d.Explanation)))
            : review.Count > 0
                ? new ReadinessDimension("Documentation", EvidenceOutcome.Indeterminate, string.Join(" ", review.Select(d => d.Explanation)))
                : new ReadinessDimension("Documentation", EvidenceOutcome.Pass, $"{relevant.Count} document(s) apply and match: {string.Join(", ", relevant.Select(d => $"{d.DocumentNumber} rev {d.AtRelease ?? d.Applicable}"))}.");
    }
}

/// <summary>
/// Which revision of a document applies (V2 §7) and whether the work matches it. Date effectivity is the
/// default rule; applicability rules override it: an order rule (a deviation) first, then program,
/// customer and due-date rules, which must agree or the result is "requires review". Pure: same inputs,
/// same answer, no database, no clock.
/// </summary>
public static class ApplicabilityEngine
{
    public static Resolution Resolve(DocumentInfo document, string? pinned, IReadOnlyList<ApplicabilityRuleInfo> rules, ApplicabilityFacts facts, DateTimeOffset asOf, string? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(facts);
        if (pinned is not null)
        {
            var revision = document.Revisions.FirstOrDefault(r => r.Revision == pinned);
            return revision is null
                ? new Resolution(null, ResolutionBasis.None, $"{document.Number} has no revision {pinned}.")
                : new Resolution(revision, ResolutionBasis.Pinned, $"the requirement names rev {pinned}");
        }

        var active = rules.Where(r => r.DocumentNumber == document.Number && r.IsActiveAt(asOf)).ToList();
        var deviations = active.Where(r => r.Condition == ApplicabilityCondition.Order && r.OrderNumber == facts.OrderNumber).ToList();
        var matching = deviations.Count > 0 ? deviations : active.Where(r => r.Condition != ApplicabilityCondition.Order && Matches(r, facts)).ToList();
        var chosen = matching.Select(r => r.Revision).Distinct(StringComparer.Ordinal).ToList();
        if (chosen.Count > 1)
        {
            return new Resolution(null, ResolutionBasis.Conflict, $"rules {string.Join(", ", matching.Select(r => r.RuleId))} select different revisions ({string.Join(", ", chosen)}); a person must decide which applies");
        }

        if (chosen.Count == 1)
        {
            var revision = document.Revisions.FirstOrDefault(r => r.Revision == chosen[0]);
            var rule = matching[0];
            return deviations.Count > 0
                ? new Resolution(revision, ResolutionBasis.Deviation, $"deviation {rule.Reference} (rule {rule.RuleId}) sets rev {chosen[0]} for this order")
                : new Resolution(revision, ResolutionBasis.Rule, $"rule {rule.RuleId} sets rev {chosen[0]} for {Describe(rule)}");
        }

        // As known at that instant: only revisions released by then count, and the newest one in effect wins.
        // (A later release supersedes by taking effect, so effective_to written afterwards is not used here.)
        var day = SiteTime.DateAtOrUtc(asOf, timeZone);
        var inForce = document.Revisions
            .Where(r => r.EffectiveFrom is { } from && from <= day && r.ReleasedAt is { } released && released <= asOf
                && r.Status is DocumentRevisionStatus.Released or DocumentRevisionStatus.Applicable or DocumentRevisionStatus.Superseded or DocumentRevisionStatus.Obsolete
                && !(r.ObsoletedAt is { } gone && gone <= asOf && r.EffectiveTo is { } to && to <= day))
            .OrderByDescending(r => r.EffectiveFrom)
            .ThenByDescending(r => r.ReleasedAt)
            .FirstOrDefault();
        return inForce is null
            ? new Resolution(null, ResolutionBasis.None, $"no revision of {document.Number} was in force on {day:yyyy-MM-dd}")
            : new Resolution(inForce, ResolutionBasis.DateEffectivity, $"rev {inForce.Revision} is in force from {inForce.EffectiveFrom:yyyy-MM-dd}");
    }

    /// <summary>
    /// The state of one needed document for the work, now. For a released order the revision that
    /// applied at release is what the order references (history is kept, so it is reconstructed exactly).
    /// </summary>
    public static DocumentApplicability Evaluate(DocumentInfo document, DocumentNeed need, IReadOnlyList<ApplicabilityRuleInfo> rules, ApplicabilityFacts facts, DateTimeOffset now, DateTimeOffset? releasedAt, string? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(need);
        var current = Resolve(document, need.PinnedRevision, rules, facts, now, timeZone);
        var compare = releasedAt is { } released && need.AddedAt <= released;
        var atRelease = compare ? Resolve(document, need.PinnedRevision, rules, facts, releasedAt!.Value, timeZone) : null;

        DocumentApplicability Result(DocumentState state, string explanation) => new(
            document.Number, document.Title, document.Authority, need.Source, need.OperationSequence, need.PinnedRevision,
            atRelease?.Revision?.Revision, current.Revision?.Revision, state, Outcome(state), explanation);

        if (current.Basis == ResolutionBasis.Conflict)
        {
            return Result(DocumentState.RequiresReview, $"{document.Number}: {current.Explanation}.");
        }

        if (current.Revision is not { } applies)
        {
            return Result(DocumentState.Missing, $"{document.Number}: {current.Explanation}.");
        }

        switch (applies.Status)
        {
            case DocumentRevisionStatus.Draft or DocumentRevisionStatus.InReview or DocumentRevisionStatus.Approved:
                return Result(DocumentState.NotReleased, $"{document.Number} rev {applies.Revision} applies ({current.Explanation}) but is {Status(applies.Status)}, not released.");
            case DocumentRevisionStatus.Obsolete:
                return Result(DocumentState.Obsolete, $"{document.Number} rev {applies.Revision} ({current.Explanation}) was made obsolete.");
            case DocumentRevisionStatus.Uncontrolled:
                return Result(DocumentState.Mismatch, $"{document.Number} is a reference document and is never production authority.");
        }

        if (current.Basis == ResolutionBasis.Pinned && applies.Status == DocumentRevisionStatus.Superseded)
        {
            var newer = document.Current;
            return Result(DocumentState.Pinned, $"{document.Number}: built to rev {applies.Revision} as the requirement names it{(newer is null ? string.Empty : $"; rev {newer.Revision} is the latest")}.");
        }

        if (!compare)
        {
            var note = releasedAt is not null ? " (required after the order was released)" : string.Empty;
            return Result(DocumentState.Current, $"{document.Number} rev {applies.Revision} applies: {current.Explanation}{note}.");
        }

        if (atRelease!.Basis == ResolutionBasis.Conflict)
        {
            return Result(DocumentState.RequiresReview, $"{document.Number}: when the order was released, {atRelease.Explanation}.");
        }

        if (atRelease.Revision is not { } then)
        {
            return Result(DocumentState.Current, $"{document.Number} rev {applies.Revision} applies ({current.Explanation}); none did when the order was released, so issue it with the order.");
        }

        if (then.Revision == applies.Revision)
        {
            return Result(DocumentState.Current, $"{document.Number} rev {applies.Revision}: the order was released with it and it still applies ({current.Explanation}).");
        }

        var thenNow = document.Revisions.First(r => r.Revision == then.Revision);
        if (thenNow.Status == DocumentRevisionStatus.Obsolete)
        {
            return Result(DocumentState.Mismatch, $"{document.Number}: the order was released with rev {then.Revision}, which was made obsolete; rev {applies.Revision} applies now.");
        }

        var releasedDay = SiteTime.DateAtOrUtc(releasedAt!.Value, timeZone);
        if (current.Basis == ResolutionBasis.DateEffectivity && atRelease.Basis == ResolutionBasis.DateEffectivity
            && (applies.ReleasedAt > releasedAt || applies.EffectiveFrom > releasedDay))
        {
            return Result(DocumentState.LegitimatelySuperseded, $"{document.Number}: the order was released with rev {then.Revision} before rev {applies.Revision} took effect ({applies.EffectiveFrom:yyyy-MM-dd}); work in progress continues on rev {then.Revision}.");
        }

        return Result(DocumentState.Mismatch, $"{document.Number}: the order was released with rev {then.Revision}, but rev {applies.Revision} applies now ({current.Explanation}). Reissue the order's documentation once the floor has rev {applies.Revision}.");
    }

    public static EvidenceOutcome Outcome(DocumentState state) => state switch
    {
        DocumentState.Current or DocumentState.Pinned or DocumentState.LegitimatelySuperseded => EvidenceOutcome.Pass,
        DocumentState.RequiresReview => EvidenceOutcome.Indeterminate,
        _ => EvidenceOutcome.Fail,
    };

    private static bool Matches(ApplicabilityRuleInfo rule, ApplicabilityFacts facts) => rule.Condition switch
    {
        ApplicabilityCondition.Program => facts.ProgramId is { } p && p == rule.ProgramId,
        ApplicabilityCondition.Customer => facts.Customer is { } c && rule.Customer is { } rc && string.Equals(c.Trim(), rc.Trim(), StringComparison.OrdinalIgnoreCase),
        ApplicabilityCondition.DueDate => facts.DueDate is { } d && (rule.DueFrom is null || d >= rule.DueFrom) && (rule.DueTo is null || d <= rule.DueTo),
        _ => false,
    };

    private static string Describe(ApplicabilityRuleInfo rule) => rule.Condition switch
    {
        ApplicabilityCondition.Program => "this program's orders",
        ApplicabilityCondition.Customer => $"customer {rule.Customer}",
        ApplicabilityCondition.DueDate => string.Create(CultureInfo.InvariantCulture, $"orders due {(rule.DueFrom is { } f ? $"from {f:yyyy-MM-dd}" : "until")}{(rule.DueTo is { } t ? $" {(rule.DueFrom is null ? string.Empty : "to ")}{t:yyyy-MM-dd}" : " onwards")}"),
        _ => "this order",
    };

    private static string Status(DocumentRevisionStatus status) => status == DocumentRevisionStatus.InReview ? "in review" : status.ToString().ToLowerInvariant();
}
