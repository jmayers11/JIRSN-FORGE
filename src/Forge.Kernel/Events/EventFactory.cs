using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;

namespace Forge.Kernel.Events;

public static class EventFactory
{
    public static EventEnvelope Create<TEvent>(
        TEvent payload,
        CommandContext context,
        TimeProvider time,
        string sourceContext,
        string entityType,
        string entityId,
        ProgramId? programId = null,
        DataMarking markings = DataMarking.None)
        where TEvent : IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(context);
        return Create(payload, context.Actor, context.CorrelationId, context.Reason, time, sourceContext, entityType, entityId, programId, markings);
    }

    public static EventEnvelope Create<TEvent>(
        TEvent payload,
        string actor,
        Guid correlationId,
        string? reason,
        TimeProvider time,
        string sourceContext,
        string entityType,
        string entityId,
        ProgramId? programId = null,
        DataMarking markings = DataMarking.None)
        where TEvent : IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(time);
        var now = time.GetUtcNow();
        return new EventEnvelope
        {
            EventId = ForgeIds.New(time),
            EventType = TEvent.EventType,
            SchemaVersion = TEvent.SchemaVersion,
            OccurredAt = now,
            RecordedAt = now,
            SourceContext = sourceContext,
            EntityType = entityType,
            EntityId = entityId,
            Actor = actor,
            CorrelationId = correlationId,
            Reason = reason,
            ProgramId = programId,
            Markings = markings,
            Payload = CanonicalJson.Serialize(payload),
        };
    }
}
