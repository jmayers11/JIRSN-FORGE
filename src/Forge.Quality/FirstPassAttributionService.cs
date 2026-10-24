using System.Globalization;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;

namespace Forge.Quality;

/// <param name="SupersedesAttributionId">A verified attribution this one corrects; it is superseded, not rewritten, once the correction is verified.</param>
public sealed record ProposeFirstPassAttribution(long InspectionId, DefectCause Cause, bool EmployeeOpportunity, string Evidence,
    decimal? FailedQuantity = null, long? SupersedesAttributionId = null);

public sealed record VerifyFirstPassAttribution(long AttributionId, bool Verify);

public sealed record FirstPassAttributionChanged(long AttributionId, long InspectionId, string Cause, string State) : IDomainEvent
{
    public static string EventType => nameof(FirstPassAttributionChanged);

    public static int SchemaVersion => 1;
}

public interface IFirstPassAttributionStore
{
    Task<long> InsertAttributionAsync(FirstPassAttribution attribution, CancellationToken ct);

    Task<bool> SetAttributionStateAsync(long attributionId, AttributionState expected, AttributionState state, string? reviewer, DateTimeOffset? at, CancellationToken ct);
}

/// <summary>
/// Cause attribution of first-pass inspection results (Sprint 10 §16.2): proposed from Quality, maintenance or MRB evidence, verified by a
/// different person with disposition authority. Operators cannot reclassify their own results, and corrections keep the original.
/// </summary>
public sealed class FirstPassAttributionService(CommandPipeline pipeline)
{
    private const string SourceContext = "Quality";
    private const string Entity = "FirstPassAttribution";

    public Task<CommandResult> ProposeAsync(ProposeFirstPassAttribution c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var inspectionKey = c.InspectionId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, nameof(ProposeFirstPassAttribution), Permissions.QualityInspect, ResourceRef.Of("Inspection", inspectionKey), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            if (!Enum.IsDefined(c.Cause))
                throw new CommandRejectedException("invalid_cause", "Choose a cause.");
            var evidence = c.Evidence?.Trim();
            if (string.IsNullOrEmpty(evidence) || evidence.Length > 2000)
                throw new CommandRejectedException("evidence_required", "Name the Quality, maintenance or MRB evidence (at most 2000 characters).");
            var inspection = await uow.Store<IQualityLookup>().FindInspectionAsync(c.InspectionId, ct)
                ?? throw new CommandRejectedException("inspection_not_found", $"Inspection {inspectionKey} does not exist.");
            if (c.FailedQuantity is { } failed)
            {
                if (inspection.Result != InspectionResult.Fail)
                    throw new CommandRejectedException("not_failed", "A failed quantity applies only to a failed inspection.");
                if (failed <= 0 || failed > inspection.QuantityInspected)
                    throw new CommandRejectedException("invalid_quantity", $"Between 0 and {inspection.QuantityInspected} units failed.");
            }

            if (c.Cause == DefectCause.EmployeeControlled && !c.EmployeeOpportunity)
                throw new CommandRejectedException("invalid_attribution", "An employee-controlled cause needs a meaningful employee opportunity.");

            var existing = await uow.Store<IFirstPassAttributionLookup>().ListAttributionsAsync([c.InspectionId], ct);
            if (c.SupersedesAttributionId is { } supersedes)
            {
                var original = existing.FirstOrDefault(a => a.AttributionId == supersedes);
                if (original is not { State: AttributionState.Verified })
                    throw new CommandRejectedException("not_correctable", "Only the verified attribution of this inspection is corrected.");
                if (existing.Any(a => a.SupersedesAttributionId == supersedes && a.State == AttributionState.Proposed))
                    throw new CommandRejectedException("correction_pending", "A correction of this attribution is already waiting for review.");
            }
            else if (existing.Any(a => a.State is AttributionState.Proposed or AttributionState.Verified))
            {
                throw new CommandRejectedException("attribution_exists", "This inspection already has an attribution; propose a correction of it instead.");
            }

            var attribution = new FirstPassAttribution(0, c.InspectionId, c.Cause, c.EmployeeOpportunity, c.FailedQuantity, evidence, AttributionState.Proposed,
                context.Actor, now, null, null, c.SupersedesAttributionId);
            var id = await uow.Store<IFirstPassAttributionStore>().InsertAttributionAsync(attribution, ct);
            var key = id.ToString(CultureInfo.InvariantCulture);
            return new CommandOutcome(
                CommandResult.Ok(key, $"Attribution {key} of inspection {inspectionKey} proposed ({c.Cause}). It counts once a different person with disposition authority verifies it."),
                [pipeline.Event(new FirstPassAttributionChanged(id, c.InspectionId, c.Cause.ToString(), nameof(AttributionState.Proposed)), context, SourceContext, Entity, key)],
                new AuditChange(Entity, key, null, CanonicalJson.Serialize(attribution with { AttributionId = id })));
        }, ct);
    }

    public Task<CommandResult> VerifyAsync(VerifyFirstPassAttribution c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(context);
        var key = c.AttributionId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(SourceContext, nameof(VerifyFirstPassAttribution), Permissions.QualityDisposition, ResourceRef.Of(Entity, key), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var attribution = await uow.Store<IFirstPassAttributionLookup>().FindAttributionAsync(c.AttributionId, ct)
                ?? throw new CommandRejectedException("attribution_not_found", $"Attribution {key} does not exist.");
            if (attribution.State != AttributionState.Proposed)
                throw new CommandRejectedException("not_pending", $"Attribution {key} is {attribution.State}.");
            if (attribution.ProposedBy == context.Actor)
                throw new CommandRejectedException("independent_review_required", "You proposed this attribution; a different person verifies it.");

            var store = uow.Store<IFirstPassAttributionStore>();
            var state = c.Verify ? AttributionState.Verified : AttributionState.Rejected;
            if (!await store.SetAttributionStateAsync(attribution.AttributionId, AttributionState.Proposed, state, context.Actor, now, ct))
                throw new CommandRejectedException("attribution_changed", "The attribution changed meanwhile; reload it.");
            var events = new List<EventEnvelope>
            {
                pipeline.Event(new FirstPassAttributionChanged(attribution.AttributionId, attribution.InspectionId, attribution.Cause.ToString(), state.ToString()), context, SourceContext, Entity, key),
            };
            if (c.Verify && attribution.SupersedesAttributionId is { } original)
            {
                if (!await store.SetAttributionStateAsync(original, AttributionState.Verified, AttributionState.Superseded, null, null, ct))
                    throw new CommandRejectedException("attribution_changed", "The corrected attribution changed meanwhile; reload it.");
                events.Add(pipeline.Event(new FirstPassAttributionChanged(original, attribution.InspectionId, attribution.Cause.ToString(), nameof(AttributionState.Superseded)),
                    context, SourceContext, Entity, original.ToString(CultureInfo.InvariantCulture)));
            }

            return new CommandOutcome(
                CommandResult.Ok(key, c.Verify
                    ? $"Attribution {key} verified{(attribution.SupersedesAttributionId is null ? "." : "; the attribution it corrects stays in history as superseded.")}"
                    : $"Attribution {key} rejected; it does not count."),
                events, new AuditChange(Entity, key, nameof(AttributionState.Proposed), state.ToString()));
        }, ct);
    }
}
