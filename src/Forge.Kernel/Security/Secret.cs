using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forge.Kernel.Security;

/// <summary>
/// A value that must never reach logs, the audit trail, events or request fingerprints: a badge
/// number or a PIN. It serializes as "redacted"; only the code that checks it calls <see cref="Reveal"/>.
/// </summary>
[JsonConverter(typeof(SecretJsonConverter))]
public sealed class Secret
{
    private readonly string value;

    public Secret(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        this.value = value.Trim();
    }

    public string Reveal() => value;

    public override string ToString() => "redacted";

    private sealed class SecretJsonConverter : JsonConverter<Secret>
    {
        public override Secret Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(reader.GetString() ?? throw new JsonException("A secret cannot be empty."));

        public override void Write(Utf8JsonWriter writer, Secret value, JsonSerializerOptions options) => writer.WriteStringValue("redacted");
    }
}
