namespace Forge.Competency.Contracts;

/// <summary>
/// A site's refresher rule for one qualification type (Sprint 10 §21.1). Qualification expiry is a separate hard stop; this rule
/// covers unused skills: after <see cref="RefresherAfterDays"/> without verified operation, a passed practical assessment is required
/// again before independent work. <see cref="ReacclimationMinutes"/> is extra setup time in capacity estimates, never a penalty on the person.
/// </summary>
public sealed record RefresherPolicy(string Code, int? RefresherAfterDays, int? ReacclimationMinutes, string SetBy, DateTimeOffset SetAt);

public interface IRefresherPolicyLookup
{
    /// <summary>The current rule of each qualification type that has one.</summary>
    Task<IReadOnlyList<RefresherPolicy>> ListRefresherPoliciesAsync(CancellationToken ct);
}
