using System.Text.Json;
using Forge.Kernel.Events;
using Forge.Kernel.Serialization;

namespace Forge.Kernel.Postgres;

internal static class EnvelopeSerializer
{
    public static string Serialize(EventEnvelope envelope) => CanonicalJson.Serialize(envelope);

    public static EventEnvelope Deserialize(string json) =>
        JsonSerializer.Deserialize<EventEnvelope>(json, CanonicalJson.Options)
        ?? throw new InvalidOperationException("Stored event envelope was empty.");
}
