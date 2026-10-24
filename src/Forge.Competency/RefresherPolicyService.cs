using Forge.Competency.Contracts;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;

namespace Forge.Competency;

/// <param name="RefresherAfterDays">Days without verified operation after which a refresher is required; null removes the rule.</param>
/// <param name="ReacclimationMinutes">Extra setup minutes to plan for someone returning after a refresher; null plans none.</param>
public sealed record SetRefresherPolicy(string Code, int? RefresherAfterDays, int? ReacclimationMinutes);

public sealed record RefresherPolicyChanged(string Code, int? RefresherAfterDays, int? ReacclimationMinutes) : IDomainEvent
{
    public static string EventType => nameof(RefresherPolicyChanged);

    public static int SchemaVersion => 1;
}

public interface IRefresherPolicyStore
{
    Task InsertRefresherPolicyAsync(RefresherPolicy policy, CancellationToken ct);
}

/// <summary>Sets a qualification type's refresher rule (Sprint 10 §21.1). Every change is a new version with who, when and why.</summary>
public sealed class RefresherPolicyService(CommandPipeline pipeline)
{
    private const string SourceContext = "Competency";

    public Task<CommandResult> SetAsync(SetRefresherPolicy c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var code = BusinessCode.Require(c.Code, "Qualification");
        var spec = new CommandSpec(SourceContext, nameof(SetRefresherPolicy), Permissions.CompetencyManage, ResourceRef.Of("QualificationType", code), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            if (!await uow.Store<ICompetencyLookup>().QualificationTypeExistsAsync(code, ct))
                throw new CommandRejectedException("qualification_not_found", $"There is no qualification {code}.");
            if (c.RefresherAfterDays is < 1 or > 3650)
                throw new CommandRejectedException("invalid_refresher", "A refresher interval is between 1 and 3650 days.");
            if (c.ReacclimationMinutes is < 0 or > 960)
                throw new CommandRejectedException("invalid_reacclimation", "Reacclimation is between 0 and 960 minutes.");
            var policy = new RefresherPolicy(code, c.RefresherAfterDays, c.ReacclimationMinutes, context.Actor, now);
            await uow.Store<IRefresherPolicyStore>().InsertRefresherPolicyAsync(policy, ct);
            return new CommandOutcome(
                CommandResult.Ok(code, c.RefresherAfterDays is { } days
                    ? $"{code}: a refresher is required after {days} days without verified operation{(c.ReacclimationMinutes is > 0 ? $"; plan {c.ReacclimationMinutes} minutes of reacclimation" : string.Empty)}."
                    : $"{code}: no refresher rule; qualification expiry still applies."),
                [pipeline.Event(new RefresherPolicyChanged(code, c.RefresherAfterDays, c.ReacclimationMinutes), context, SourceContext, "QualificationType", code)],
                new AuditChange("RefresherPolicy", code, null, CanonicalJson.Serialize(policy)));
        }, ct);
    }
}
