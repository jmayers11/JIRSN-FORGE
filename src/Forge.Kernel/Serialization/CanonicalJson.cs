using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Forge.Kernel.Serialization;

/// <summary>
/// Canonical JSON: object keys sorted ordinally at every level, no whitespace, invariant number
/// formatting. Two values that are equal as data produce identical bytes regardless of property
/// declaration order or dictionary insertion order. Used for fingerprints and audit hashes.
/// </summary>
public static class CanonicalJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            WriteIndented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public static string Serialize<T>(T value)
    {
        var node = JsonSerializer.SerializeToNode(value, Options);
        return Write(node);
    }

    public static string Canonicalize(string json)
    {
        var node = JsonNode.Parse(json);
        return Write(node);
    }

    public static byte[] Sha256(string canonical) => SHA256.HashData(Encoding.UTF8.GetBytes(canonical));

    public static string Sha256Hex(string canonical) => Convert.ToHexStringLower(Sha256(canonical));

    public static string Fingerprint<T>(T value) => Sha256Hex(Serialize(value));

    private static string Write(JsonNode? node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            WriteNode(writer, node);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteNode(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var property in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Key);
                    WriteNode(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array)
                {
                    WriteNode(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }
}
