using System.Globalization;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Procurement.Contracts;

namespace Forge.Procurement;

public sealed record SetScorecardPolicy(int LateToleranceDays, int? EarlyLimitDays, decimal GradeAOnTimePercent, decimal GradeAQualityPercent,
    decimal GradeBOnTimePercent, decimal GradeBQualityPercent, int PeriodDays);

public sealed record ScorecardPolicyChanged(int LateToleranceDays, int? EarlyLimitDays) : IDomainEvent
{
    public static string EventType => nameof(ScorecardPolicyChanged);

    public static int SchemaVersion => 1;
}

public interface IScorecardPolicyStore
{
    Task InsertScorecardPolicyAsync(ScorecardPolicy policy, CancellationToken ct);
}

/// <summary>A purchasing manager keeps the scorecard policy: what counts as on time and where the grades fall. Versions are kept.</summary>
public sealed class ScorecardPolicyService(CommandPipeline pipeline)
{
    public Task<CommandResult> SetPolicyAsync(SetScorecardPolicy c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(PurchasingService.SourceContext, nameof(SetScorecardPolicy), Permissions.PurchasingApprove, ResourceRef.Of("ScorecardPolicy"), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            if (c.LateToleranceDays is < 0 or > 30) throw new CommandRejectedException("invalid_tolerance", "Days late allowed is 0 to 30.");
            if (c.EarlyLimitDays is < 0 or > 365) throw new CommandRejectedException("invalid_early_limit", "Days early allowed is 0 to 365, or blank for no limit.");
            if (c.PeriodDays is < 7 or > 730) throw new CommandRejectedException("invalid_period", "The scorecard period is 7 to 730 days.");
            if (new[] { c.GradeAOnTimePercent, c.GradeAQualityPercent, c.GradeBOnTimePercent, c.GradeBQualityPercent }.Any(p => p is < 0 or > 100))
                throw new CommandRejectedException("invalid_percent", "Grade thresholds are percentages from 0 to 100.");
            if (c.GradeBOnTimePercent > c.GradeAOnTimePercent || c.GradeBQualityPercent > c.GradeAQualityPercent)
                throw new CommandRejectedException("invalid_grades", "A grade B threshold cannot be above the grade A threshold.");
            var policy = new ScorecardPolicy(c.LateToleranceDays, c.EarlyLimitDays, c.GradeAOnTimePercent, c.GradeAQualityPercent, c.GradeBOnTimePercent, c.GradeBQualityPercent,
                c.PeriodDays, now, context.Actor, context.Reason!);
            await uow.Store<IScorecardPolicyStore>().InsertScorecardPolicyAsync(policy, ct);
            var early = c.EarlyLimitDays is { } e ? string.Create(CultureInfo.InvariantCulture, $"up to {e} day(s) early") : "any time early";
            return new CommandOutcome(
                CommandResult.Ok("scorecard-policy", string.Create(CultureInfo.InvariantCulture, $"On time: up to {c.LateToleranceDays} day(s) late and {early}. Grades A ≥ {c.GradeAOnTimePercent}% / {c.GradeAQualityPercent}%, B ≥ {c.GradeBOnTimePercent}% / {c.GradeBQualityPercent}%.")),
                [pipeline.Event(new ScorecardPolicyChanged(c.LateToleranceDays, c.EarlyLimitDays), context, PurchasingService.SourceContext, "ScorecardPolicy", "scorecard-policy")],
                new AuditChange("ScorecardPolicy", "scorecard-policy", null, CanonicalJson.Serialize(policy)));
        }, ct);
    }
}
