using System.Globalization;
using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;

namespace Forge.Quality;

public sealed record SetSpcPolicy(decimal MinimumCpk, decimal CriticalMinimumCpk, int MinimumPoints, bool NotifyOnSignal);

public sealed record MarkCriticalCharacteristic(string PartNumber, int Sequence, int Number, bool Critical);

/// <summary>Records the rules a new measurement broke (run by the system after an inspection).</summary>
public sealed record RecordSpcSignals(IReadOnlyList<SpcSignal> Signals);

public sealed record AcknowledgeSpcSignal(long SignalId, string Note);

/// <summary>A new measurement broke a control-chart rule; quality engineers are told.</summary>
public sealed record SpcSignalDetected(long SignalId, string PartNumber, int Sequence, int Characteristic, string Rule, long OrderNumber, long InspectionId, string Message) : IDomainEvent
{
    public static string EventType => nameof(SpcSignalDetected);

    public static int SchemaVersion => 1;
}

public sealed record SpcSignalAcknowledged(long SignalId, string PartNumber) : IDomainEvent
{
    public static string EventType => nameof(SpcSignalAcknowledged);

    public static int SchemaVersion => 1;
}

public sealed record SpcPolicyChanged(string Change) : IDomainEvent
{
    public static string EventType => nameof(SpcPolicyChanged);

    public static int SchemaVersion => 1;
}

public interface ISpcStore
{
    Task InsertPolicyAsync(SpcPolicy policy, CancellationToken ct);

    Task SetCriticalAsync(SpcCharacteristicKey key, bool critical, DateTimeOffset at, string actor, CancellationToken ct);

    Task<long?> InsertSignalAsync(SpcSignal signal, CancellationToken ct);

    Task<SpcSignal?> FindSignalAsync(long signalId, CancellationToken ct);

    Task<bool> AcknowledgeSignalAsync(long signalId, DateTimeOffset at, string actor, string note, CancellationToken ct);
}

/// <summary>
/// SPC (owner decisions 2026-11-01). A Quality Manager keeps the policy (required Cpk 1.33, 1.67 for critical characteristics, and when
/// limits are calculated) and marks characteristics critical; signals are recorded by the system and acknowledged by a quality engineer
/// with a note. Nothing here holds work or material.
/// </summary>
public sealed class SpcService(CommandPipeline pipeline)
{
    private const string Entity = "SpcSignal";

    public Task<CommandResult> SetPolicyAsync(SetSpcPolicy c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(QualityService.SourceContext, nameof(SetSpcPolicy), Permissions.SpcManage, ResourceRef.Of("SpcPolicy"), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            if (c.MinimumCpk is < 0.5m or > 3m || c.CriticalMinimumCpk is < 0.5m or > 3m || c.CriticalMinimumCpk < c.MinimumCpk)
                throw new CommandRejectedException("invalid_cpk", "Required Cpk is 0.5 to 3, and the critical requirement is at least the general one.");
            if (c.MinimumPoints is < 5 or > 100) throw new CommandRejectedException("invalid_points", "Limits need 5 to 100 measurements.");
            var policy = new SpcPolicy(c.MinimumCpk, c.CriticalMinimumCpk, c.MinimumPoints, c.NotifyOnSignal, now, context.Actor, context.Reason!);
            await uow.Store<ISpcStore>().InsertPolicyAsync(policy, ct);
            return new CommandOutcome(
                CommandResult.Ok("spc-policy", string.Create(CultureInfo.InvariantCulture, $"SPC policy: Cpk ≥ {c.MinimumCpk} ({c.CriticalMinimumCpk} critical), limits after {c.MinimumPoints} measurements, notices {(c.NotifyOnSignal ? "on" : "off")}.")),
                [pipeline.Event(new SpcPolicyChanged("Policy"), context, QualityService.SourceContext, "SpcPolicy", "spc-policy")],
                new AuditChange("SpcPolicy", "spc-policy", null, CanonicalJson.Serialize(policy)));
        }, ct);
    }

    public Task<CommandResult> MarkCriticalAsync(MarkCriticalCharacteristic c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = BusinessCode.Require(c.PartNumber, "Part");
        var key = new SpcCharacteristicKey(part, c.Sequence, c.Number);
        var id = string.Create(CultureInfo.InvariantCulture, $"{part}/{c.Sequence}/{c.Number}");
        var spec = new CommandSpec(QualityService.SourceContext, nameof(MarkCriticalCharacteristic), Permissions.SpcManage, ResourceRef.Of("SpcCharacteristic", id), ReasonRequired: true);
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var plan = (await uow.Store<IQualityLookup>().ListPlansAsync(part, ct)).FirstOrDefault(p => p.Sequence == c.Sequence && p.Characteristics.Any(x => x.Number == c.Number && x.Kind == CharacteristicKind.Variable))
                ?? throw new CommandRejectedException("characteristic_not_found", $"{part} has no measured characteristic {c.Number} after operation {c.Sequence}.");
            var already = (await uow.Store<ISpcLookup>().ListCriticalAsync(ct)).Contains(key);
            if (already == c.Critical) return CommandOutcome.NoChange(CommandResult.NoChange(id, "Nothing changed."));
            await uow.Store<ISpcStore>().SetCriticalAsync(key, c.Critical, now, context.Actor, ct);
            var name = plan.Characteristics.First(x => x.Number == c.Number).Name;
            return new CommandOutcome(CommandResult.Ok(id, $"{part} op {c.Sequence} characteristic {c.Number} ({name}) is {(c.Critical ? "critical" : "no longer critical")}."),
                [pipeline.Event(new SpcPolicyChanged(c.Critical ? "Critical" : "NotCritical"), context, QualityService.SourceContext, "SpcCharacteristic", id)],
                new AuditChange("SpcCharacteristic", id, already.ToString(), c.Critical.ToString()));
        }, ct);
    }

    public Task<CommandResult> RecordSignalsAsync(RecordSpcSignals c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var spec = new CommandSpec(QualityService.SourceContext, nameof(RecordSpcSignals), Permissions.SpcRecord, ResourceRef.Of(Entity));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var events = new List<EventEnvelope>();
            var store = uow.Store<ISpcStore>();
            foreach (var signal in c.Signals ?? [])
            {
                if (await store.InsertSignalAsync(signal with { DetectedAt = now, Status = SpcSignalStatus.Open }, ct) is not { } id) continue; // already recorded
                var message = string.Create(CultureInfo.InvariantCulture,
                    $"{signal.Key.PartNumber} op {signal.Key.Sequence} {signal.CharacteristicName}: {signal.Description} Measured {signal.Value} on order {signal.OrderNumber}. Review the chart; deciding whether to hold anything is yours.");
                events.Add(pipeline.Event(new SpcSignalDetected(id, signal.Key.PartNumber, signal.Key.Sequence, signal.Key.Number, signal.Rule.ToString(), signal.OrderNumber, signal.InspectionId, message),
                    context, QualityService.SourceContext, Entity, id.ToString(CultureInfo.InvariantCulture)));
            }

            return events.Count == 0
                ? CommandOutcome.NoChange(CommandResult.NoChange("spc", "No new signals."))
                : new CommandOutcome(CommandResult.Ok("spc", $"{events.Count} control-chart signal(s) recorded."), events, new AuditChange(Entity, "spc", null, CanonicalJson.Serialize(c.Signals)));
        }, ct);
    }

    public Task<CommandResult> AcknowledgeAsync(AcknowledgeSpcSignal c, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var id = c.SignalId.ToString(CultureInfo.InvariantCulture);
        var spec = new CommandSpec(QualityService.SourceContext, nameof(AcknowledgeSpcSignal), Permissions.QualityPlan, ResourceRef.Of(Entity, id));
        return pipeline.ExecuteAsync(spec, c, context, async (uow, now) =>
        {
            var note = (c.Note ?? string.Empty).Trim();
            if (note.Length is 0 or > 1000) throw new CommandRejectedException("note_required", "Say what was found and what was done (at most 1000 characters).");
            var store = uow.Store<ISpcStore>();
            var signal = await store.FindSignalAsync(c.SignalId, ct) ?? throw new CommandRejectedException("signal_not_found", $"Signal {id} does not exist.");
            if (!await store.AcknowledgeSignalAsync(c.SignalId, now, context.Actor, note, ct))
                return CommandOutcome.NoChange(CommandResult.NoChange(id, $"Signal {id} is already acknowledged."));
            return new CommandOutcome(CommandResult.Ok(id, $"Signal {id} on {signal.Key.PartNumber} acknowledged."),
                [pipeline.Event(new SpcSignalAcknowledged(signal.SignalId, signal.Key.PartNumber), context, QualityService.SourceContext, Entity, id)],
                new AuditChange(Entity, id, nameof(SpcSignalStatus.Open), note));
        }, ct);
    }
}
