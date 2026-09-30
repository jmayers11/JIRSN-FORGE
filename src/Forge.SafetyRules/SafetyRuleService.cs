using Forge.Competency.Contracts;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Resources.Contracts;
using Forge.SafetyRules.Contracts;

namespace Forge.SafetyRules;

public sealed record DraftSafetyRule(
    string Code,
    string Title,
    RuleScopeKind ScopeKind,
    string ScopeCode,
    IReadOnlyList<string>? ActivityCodes,
    int MinimumPhysicalPeople,
    bool ProhibitLoneWork,
    IReadOnlyList<RoleSlot>? Slots);

public sealed record PublishSafetyRule(string Code, int Version);

public sealed record DiscardSafetyRuleDraft(string Code);

public sealed record RetireSafetyRule(string Code);

public sealed record SafetyRuleDrafted(string Code, int Version) : IDomainEvent
{
    public static string EventType => nameof(SafetyRuleDrafted);

    public static int SchemaVersion => 1;
}

public sealed record SafetyRulePublished(string Code, int Version, int? SupersededVersion, DateTimeOffset EffectiveFrom) : IDomainEvent
{
    public static string EventType => nameof(SafetyRulePublished);

    public static int SchemaVersion => 1;
}

public sealed record SafetyRuleRetired(string Code, int Version) : IDomainEvent
{
    public static string EventType => nameof(SafetyRuleRetired);

    public static int SchemaVersion => 1;
}

public sealed record SafetyRuleDraftDiscarded(string Code, int Version) : IDomainEvent
{
    public static string EventType => nameof(SafetyRuleDraftDiscarded);

    public static int SchemaVersion => 1;
}

public interface ISafetyRuleStore
{
    Task<IReadOnlyList<SafetyRuleDefinition>> VersionsAsync(string code, CancellationToken ct);

    Task InsertDraftAsync(SafetyRuleDefinition rule, DateTimeOffset at, CancellationToken ct);

    Task DeleteDraftAsync(string code, int version, CancellationToken ct);

    Task SetStatusAsync(string code, int version, RuleStatus status, string? approvedBy, DateTimeOffset? effectiveFrom, DateTimeOffset? effectiveTo, CancellationToken ct);
}

/// <summary>
/// Safety rules are authored by safety engineers and put in force only by a different person with
/// approval authority. Rules are never edited: a change is a new version, and every evaluation
/// records the version it used.
/// </summary>
public sealed class SafetyRuleService(CommandPipeline pipeline)
{
    public const string SourceContext = "SafetyRules";
    private const string RuleEntity = "SafetyRule";

    public Task<CommandResult> DraftAsync(DraftSafetyRule c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var code = BusinessCode.Require(c.Code, "Safety rule");
        var spec = new CommandSpec(SourceContext, nameof(DraftSafetyRule), Permissions.SafetyRulesDraft, ResourceRef.Of(RuleEntity, code), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var title = BusinessCode.RequireName(c.Title, "Safety rule");
            var scope = BusinessCode.Require(c.ScopeCode, c.ScopeKind.ToString());
            var resources = uow.Store<IResourcesLookup>();
            var scopeExists = c.ScopeKind switch
            {
                RuleScopeKind.SafetyArea => await resources.FindSafetyAreaAsync(scope, ct) is not null,
                RuleScopeKind.WorkCenter => await resources.FindWorkCenterAsync(scope, ct) is not null,
                RuleScopeKind.Machine => await resources.FindMachineAsync(scope, ct) is not null,
                _ => false,
            };
            if (!scopeExists)
            {
                throw new CommandRejectedException("unknown_scope", $"{c.ScopeKind} {scope} does not exist.");
            }

            var activities = new List<string>();
            foreach (var a in (c.ActivityCodes ?? []).Select(a => BusinessCode.Require(a, "Activity")).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                if (!await resources.ActivityTypeExistsAsync(a, ct))
                {
                    throw new CommandRejectedException("unknown_activity", $"Activity type {a} does not exist.");
                }

                activities.Add(a);
            }

            if (c.MinimumPhysicalPeople is < 1 or > 50)
            {
                throw new CommandRejectedException("invalid_minimum", "Minimum physical people must be between 1 and 50.");
            }

            var competency = uow.Store<ICompetencyLookup>();
            var slots = new List<RoleSlot>();
            foreach (var slot in c.Slots ?? [])
            {
                var name = BusinessCode.RequireName(slot.Name, "Role slot");
                if (slot.Count is < 1 or > 20)
                {
                    throw new CommandRejectedException("invalid_slot", $"Slot '{name}' must require between 1 and 20 people.");
                }

                var quals = (slot.SatisfiedBy ?? []).Select(q => BusinessCode.Require(q, "Qualification")).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                if (quals.Count == 0)
                {
                    throw new CommandRejectedException("invalid_slot", $"Slot '{name}' needs at least one qualification that satisfies it.");
                }

                foreach (var q in quals)
                {
                    if (!await competency.QualificationTypeExistsAsync(q, ct))
                    {
                        throw new CommandRejectedException("unknown_qualification", $"Qualification type {q} does not exist.");
                    }
                }

                slots.Add(new RoleSlot(name, slot.Count, quals, slot.TraineeMayFill));
            }

            if (slots.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != slots.Count)
            {
                throw new CommandRejectedException("invalid_slot", "Slot names must be unique within a rule.");
            }

            var store = uow.Store<ISafetyRuleStore>();
            var versions = await store.VersionsAsync(code, ct);
            if (versions.Any(v => v.Status == RuleStatus.Draft))
            {
                throw new CommandRejectedException("draft_exists", $"Rule {code} already has a draft. Publish or discard it first.");
            }

            var rule = new SafetyRuleDefinition(
                code,
                versions.Count == 0 ? 1 : versions.Max(v => v.Version) + 1,
                title,
                c.ScopeKind,
                scope,
                activities,
                c.MinimumPhysicalPeople,
                c.ProhibitLoneWork,
                slots,
                RuleStatus.Draft,
                context.Actor,
                null,
                null,
                null);
            await store.InsertDraftAsync(rule, now, ct);
            return new CommandOutcome(
                CommandResult.Ok(rule.Reference, $"Draft {rule.Reference} created. It has no effect until approved."),
                [pipeline.Event(new SafetyRuleDrafted(code, rule.Version), context, SourceContext, RuleEntity, code)],
                new AuditChange(RuleEntity, rule.Reference, null, CanonicalJson.Serialize(rule)));
        }, ct);
    }

    public Task<CommandResult> PublishAsync(PublishSafetyRule c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var code = BusinessCode.Require(c.Code, "Safety rule");
        var spec = new CommandSpec(SourceContext, nameof(PublishSafetyRule), Permissions.SafetyRulesApprove, ResourceRef.Of(RuleEntity, code), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var store = uow.Store<ISafetyRuleStore>();
            var versions = await store.VersionsAsync(code, ct);
            var draft = versions.SingleOrDefault(v => v.Version == c.Version && v.Status == RuleStatus.Draft)
                ?? throw new CommandRejectedException("draft_not_found", $"Rule {code} has no draft version {c.Version}.");
            if (draft.AuthoredBy == context.Actor)
            {
                throw new CommandRejectedException("separation_of_duties", "A safety rule must be approved by someone other than its author.");
            }

            var current = versions.SingleOrDefault(v => v.Status == RuleStatus.Published);
            if (current is not null)
            {
                await store.SetStatusAsync(code, current.Version, RuleStatus.Retired, current.ApprovedBy, current.EffectiveFrom, now, ct);
            }

            await store.SetStatusAsync(code, draft.Version, RuleStatus.Published, context.Actor, now, null, ct);
            var published = draft with { Status = RuleStatus.Published, ApprovedBy = context.Actor, EffectiveFrom = now };
            return new CommandOutcome(
                CommandResult.Ok(published.Reference, current is null ? $"{published.Reference} is in force." : $"{published.Reference} is in force and replaces v{current.Version}."),
                [pipeline.Event(new SafetyRulePublished(code, draft.Version, current?.Version, now), context, SourceContext, RuleEntity, code)],
                new AuditChange(RuleEntity, published.Reference, current is null ? null : CanonicalJson.Serialize(current), CanonicalJson.Serialize(published)));
        }, ct);
    }

    public Task<CommandResult> DiscardDraftAsync(DiscardSafetyRuleDraft c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var code = BusinessCode.Require(c.Code, "Safety rule");
        var spec = new CommandSpec(SourceContext, nameof(DiscardSafetyRuleDraft), Permissions.SafetyRulesDraft, ResourceRef.Of(RuleEntity, code), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, _) =>
        {
            var store = uow.Store<ISafetyRuleStore>();
            var draft = (await store.VersionsAsync(code, ct)).SingleOrDefault(v => v.Status == RuleStatus.Draft)
                ?? throw new CommandRejectedException("draft_not_found", $"Rule {code} has no draft.");
            await store.DeleteDraftAsync(code, draft.Version, ct);
            return new CommandOutcome(
                CommandResult.Ok(draft.Reference, $"Draft {draft.Reference} discarded."),
                [pipeline.Event(new SafetyRuleDraftDiscarded(code, draft.Version), context, SourceContext, RuleEntity, code)],
                new AuditChange(RuleEntity, draft.Reference, CanonicalJson.Serialize(draft), null));
        }, ct);
    }

    public Task<CommandResult> RetireAsync(RetireSafetyRule c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var code = BusinessCode.Require(c.Code, "Safety rule");
        var spec = new CommandSpec(SourceContext, nameof(RetireSafetyRule), Permissions.SafetyRulesApprove, ResourceRef.Of(RuleEntity, code), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var store = uow.Store<ISafetyRuleStore>();
            var current = (await store.VersionsAsync(code, ct)).SingleOrDefault(v => v.Status == RuleStatus.Published)
                ?? throw new CommandRejectedException("not_in_force", $"Rule {code} has no version in force.");
            await store.SetStatusAsync(code, current.Version, RuleStatus.Retired, current.ApprovedBy, current.EffectiveFrom, now, ct);
            return new CommandOutcome(
                CommandResult.Ok(current.Reference, $"{current.Reference} retired. No version of {code} is in force."),
                [pipeline.Event(new SafetyRuleRetired(code, current.Version), context, SourceContext, RuleEntity, code)],
                new AuditChange(RuleEntity, current.Reference, CanonicalJson.Serialize(current), "retired"));
        }, ct);
    }
}
