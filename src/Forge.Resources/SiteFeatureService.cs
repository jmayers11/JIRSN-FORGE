using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Resources.Contracts;

namespace Forge.Resources;

public sealed record SetSiteFeature(string Code, bool Enabled);

public sealed record SiteFeatureChanged(string Code, bool Enabled) : IDomainEvent
{
    public static string EventType => nameof(SiteFeatureChanged);

    public static int SchemaVersion => 1;
}

public interface ISiteFeatureStore
{
    Task SetFeatureAsync(string code, bool enabled, string actor, DateTimeOffset at, CancellationToken ct);
}

/// <summary>A security administrator turns the optional parts of Forge on or off for the plant (owner decision 2026-11-01).</summary>
public sealed class SiteFeatureService(CommandPipeline pipeline)
{
    private const string Entity = "SiteFeature";

    public Task<CommandResult> SetAsync(SetSiteFeature c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var feature = SiteFeatures.Find(c.Code ?? string.Empty);
        var spec = new CommandSpec(ResourcesService.SourceContext, nameof(SetSiteFeature), Permissions.SiteFeaturesManage, ResourceRef.Of(Entity, feature.Code), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var before = (await uow.Store<ISiteFeatureLookup>().ListFeaturesAsync(ct)).First(f => f.Code == feature.Code);
            if (before.Enabled == c.Enabled)
                return CommandOutcome.NoChange(CommandResult.NoChange(feature.Code, $"{feature.Name} is already {(c.Enabled ? "on" : "off")}."));
            await uow.Store<ISiteFeatureStore>().SetFeatureAsync(feature.Code, c.Enabled, context.Actor, now, ct);
            var after = before with { Enabled = c.Enabled, UpdatedBy = context.Actor, UpdatedAt = now };
            return new CommandOutcome(
                CommandResult.Ok(feature.Code, c.Enabled ? $"{feature.Name} is on." : $"{feature.Name} is off. Nothing recorded was removed; turning it on again carries on from where it was."),
                [pipeline.Event(new SiteFeatureChanged(feature.Code, c.Enabled), context, ResourcesService.SourceContext, Entity, feature.Code)],
                new AuditChange(Entity, feature.Code, CanonicalJson.Serialize(before), CanonicalJson.Serialize(after)));
        }, ct);
    }
}
