using Forge.Kernel.Identity;
using Forge.Kernel.Security;

namespace Forge.Kernel.Events;

/// <summary>A domain event: something that happened to an authoritative record.</summary>
public interface IDomainEvent
{
    /// <summary>Past-tense PascalCase type, for example "ProgramAccessGranted".</summary>
    static abstract string EventType { get; }

    static abstract int SchemaVersion { get; }
}

/// <summary>
/// The stored form of every event. Carries two times: when it happened in the business
/// (<see cref="OccurredAt"/>) and when Forge learned of it (<see cref="RecordedAt"/>), so history can
/// answer both "what was true then" and "what did we know then".
/// </summary>
public sealed record EventEnvelope
{
    public required Guid EventId { get; init; }

    public required string EventType { get; init; }

    public required int SchemaVersion { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required DateTimeOffset RecordedAt { get; init; }

    public required string SourceContext { get; init; }

    public required string EntityType { get; init; }

    public required string EntityId { get; init; }

    public required string Actor { get; init; }

    public string? OnBehalfOf { get; init; }

    public required Guid CorrelationId { get; init; }

    /// <summary>The event or command that caused this one. CauseMap walks these links.</summary>
    public Guid? CausationId { get; init; }

    public string? Reason { get; init; }

    public ProgramId? ProgramId { get; init; }

    public DataMarking Markings { get; init; }

    /// <summary>Canonical JSON of the event payload (including previous and new values).</summary>
    public required string Payload { get; init; }
}
