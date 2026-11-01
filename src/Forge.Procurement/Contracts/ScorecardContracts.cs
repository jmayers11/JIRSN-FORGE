namespace Forge.Procurement.Contracts;

// Supplier scorecards (owner decisions 2026-11-01): raw measures with an A/B/C grade. On time means received on or before the promised
// date; each company can allow days late, and optionally limit how early counts. A grade never puts a supplier on hold; a person decides.

/// <param name="LateToleranceDays">Days after the promised date a receipt still counts on time (0: on or before the promised date).</param>
/// <param name="EarlyLimitDays">Days before the promised date a receipt still counts on time; null when early is always on time.</param>
/// <param name="GradeAOnTimePercent">On-time percentage needed, with <paramref name="GradeAQualityPercent"/>, for an A.</param>
/// <param name="GradeBOnTimePercent">On-time percentage needed, with <paramref name="GradeBQualityPercent"/>, for a B; below either is a C.</param>
/// <param name="PeriodDays">The period the scorecard covers unless someone asks for another.</param>
public sealed record ScorecardPolicy(
    int LateToleranceDays,
    int? EarlyLimitDays,
    decimal GradeAOnTimePercent,
    decimal GradeAQualityPercent,
    decimal GradeBOnTimePercent,
    decimal GradeBQualityPercent,
    int PeriodDays,
    DateTimeOffset EffectiveFrom,
    string SetBy,
    string Reason)
{
    public static ScorecardPolicy Default { get; } = new(0, null, 95m, 99.5m, 85m, 98m, 90, DateTimeOffset.MinValue, "system:default", "Default policy");

    /// <summary>Whether a receipt this many days after its promised date (negative: early) is on time.</summary>
    public bool OnTime(int daysLate) => daysLate <= LateToleranceDays && (EarlyLimitDays is not { } early || -daysLate <= early);

    /// <summary>A, B or C from on-time and quality percentages.</summary>
    public string Grade(decimal onTimePercent, decimal qualityPercent) =>
        onTimePercent >= GradeAOnTimePercent && qualityPercent >= GradeAQualityPercent ? "A"
        : onTimePercent >= GradeBOnTimePercent && qualityPercent >= GradeBQualityPercent ? "B"
        : "C";
}

public interface IScorecardPolicyLookup
{
    /// <summary>The policy in force now (the default when none was recorded).</summary>
    Task<ScorecardPolicy> ScorecardPolicyAsync(CancellationToken ct);
}
