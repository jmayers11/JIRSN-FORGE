namespace Forge.SafetyRules.Contracts;

public enum RuleScopeKind
{
    SafetyArea,
    WorkCenter,
    Machine,
}

public enum RuleStatus
{
    Draft,
    Published,
    Retired,
}

/// <summary>
/// A role that a distinct physical person must fill, for example "one Slitting Level II operator".
/// Any one of <see cref="SatisfiedBy"/> qualifies. <see cref="TraineeMayFill"/> is false unless the
/// safety authority explicitly allows a trainee to count for this slot.
/// </summary>
public sealed record RoleSlot(string Name, int Count, IReadOnlyList<string> SatisfiedBy, bool TraineeMayFill);

/// <summary>
/// One version of a safety staffing rule. Presence is always counted across the safety area that
/// contains the scope. An empty activity list means the rule applies to every activity in scope.
/// </summary>
public sealed record SafetyRuleDefinition(
    string Code,
    int Version,
    string Title,
    RuleScopeKind ScopeKind,
    string ScopeCode,
    IReadOnlyList<string> ActivityCodes,
    int MinimumPhysicalPeople,
    bool ProhibitLoneWork,
    IReadOnlyList<RoleSlot> Slots,
    RuleStatus Status,
    string AuthoredBy,
    string? ApprovedBy,
    DateTimeOffset? EffectiveFrom,
    DateTimeOffset? EffectiveTo)
{
    public string Reference => $"SafetyRule:{Code}@v{Version}";

    public bool IsEffectiveAt(DateTimeOffset at) =>
        Status != RuleStatus.Draft && EffectiveFrom is { } from && from <= at && (EffectiveTo is null || at < EffectiveTo);
}

public interface ISafetyRuleLookup
{
    /// <summary>Every rule version in force at the instant, sorted by code.</summary>
    Task<IReadOnlyList<SafetyRuleDefinition>> GetEffectiveRulesAsync(DateTimeOffset at, CancellationToken ct);

    /// <summary>All versions of all rules, including drafts and retired versions, for review screens.</summary>
    Task<IReadOnlyList<SafetyRuleDefinition>> ListAllAsync(CancellationToken ct);
}
