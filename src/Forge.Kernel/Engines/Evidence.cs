namespace Forge.Kernel.Engines;

public enum EvidenceOutcome
{
    /// <summary>The check was satisfied.</summary>
    Pass,

    /// <summary>The check was definitely not satisfied.</summary>
    Fail,

    /// <summary>The inputs were missing or too stale to decide. Never treated as a pass.</summary>
    Indeterminate,

    /// <summary>A fact recorded for explanation; not a pass/fail check.</summary>
    Info,
}

/// <summary>
/// One node of an explanation: which check ran, on which inputs, under which rule version, and what
/// it concluded. Children explain how a parent's outcome was reached. This is what "WHY?" renders.
/// </summary>
public sealed record EvidenceNode
{
    public EvidenceNode(string check, EvidenceOutcome outcome, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(check);
        Check = check;
        Outcome = outcome;
        Detail = detail ?? string.Empty;
    }

    public string Check { get; }

    public EvidenceOutcome Outcome { get; }

    public string Detail { get; }

    /// <summary>Input values the check used, keyed by name. Sorted so evidence is deterministic.</summary>
    public SortedDictionary<string, string> Inputs { get; init; } = new(StringComparer.Ordinal);

    /// <summary>The rule and version applied, for example "SafetyRule:SLIT-AREA@v3".</summary>
    public string? RuleRef { get; init; }

    public IReadOnlyList<EvidenceNode> Children { get; init; } = [];

    /// <summary>
    /// Combines child outcomes the way every Forge engine must: any Fail fails; otherwise any
    /// Indeterminate is Indeterminate; otherwise Pass. Info children do not affect the result.
    /// </summary>
    public static EvidenceOutcome Combine(IEnumerable<EvidenceNode> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        var outcomes = children.Select(c => c.Outcome).Where(o => o != EvidenceOutcome.Info).ToList();
        if (outcomes.Contains(EvidenceOutcome.Fail))
        {
            return EvidenceOutcome.Fail;
        }

        return outcomes.Contains(EvidenceOutcome.Indeterminate) ? EvidenceOutcome.Indeterminate : EvidenceOutcome.Pass;
    }
}
