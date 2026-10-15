using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Forge.Host.Identification;

/// <summary>A bounded presentation of an existing authoritative key, never a second source of identity.</summary>
public sealed partial record CanonicalIdentification(string Code, string Namespace, string Kind, long? OrderNumber, int? Sequence)
{
    public const string NamespaceConfigurationKey = "Forge:Identification:Namespace";
    public const int MaxCodeLength = 1024;
    public const int MaxKeyLength = 40;
    public static IReadOnlyList<string> SupportedKinds { get; } = Array.AsReadOnly(new[] {
        "person", "order", "operation", "traveler", "material", "batch", "roll", "serial", "wip", "remnant",
        "machine", "workcenter", "tool", "gauge", "location", "container", "package", "inspection", "shipment",
        "output", "label", "alias", "instruction", "product-release" });
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public string? PartNumber { get; init; }
    public string? LotNumber { get; init; }
    public string? LocationCode { get; init; }
    public string? Key { get; init; }
    public Guid? Id { get; init; }
    public long? RecordNumber { get; init; }
    public int? LineNumber { get; init; }
    public string? Revision { get; init; }
    public string? InspectionNamespace { get; init; }

    public static bool IsValidNamespace(string? value) => value is not null && NamespacePattern().IsMatch(value);

    public static CanonicalIdentification Parse(string? code, string? configuredNamespace)
    {
        if (!IsValidNamespace(configuredNamespace))
            throw new IdentificationException("identification_not_configured", "Identification requires a valid configured namespace.");

        if (string.IsNullOrEmpty(code) || code.Length > MaxCodeLength || code.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
            throw Invalid();

        var components = code.Split(':');
        if (components.Length is not (5 or 6) || components[0] != "forge" || components[1] != "v1" || !IsValidNamespace(components[2]))
            throw Invalid();

        CanonicalIdentification result;
        switch (components[3])
        {
            case "order":
            case "traveler":
            case "operation":
            case "wip":
                var operationKind = components[3] is "operation" or "wip";
                if ((!operationKind ? components.Length != 5 : components.Length != 6)
                    || !PositiveDigits(components[4])
                    || !long.TryParse(components[4], NumberStyles.None, CultureInfo.InvariantCulture, out var orderNumber))
                    throw Invalid();
                int? sequence = null;
                if (operationKind)
                {
                    if (!PositiveDigits(components[5])
                        || !int.TryParse(components[5], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedSequence))
                        throw Invalid();
                    sequence = parsedSequence;
                }
                result = new(code, components[2], components[3], orderNumber, sequence);
                break;
            case "material":
            case "batch":
            case "roll":
            case "serial":
            case "remnant":
                if (components.Length != 6) throw Invalid();
                result = new(code, components[2], components[3], null, null)
                { PartNumber = DecodeKey(components[4]), LotNumber = DecodeKey(components[5]) };
                break;
            case "location":
                if (components.Length != 5) throw Invalid();
                result = new(code, components[2], components[3], null, null) { LocationCode = DecodeKey(components[4]) };
                break;
            case "machine":
            case "workcenter":
            case "tool":
            case "gauge":
            case "container":
            case "package":
                if (components.Length != 5) throw Invalid();
                result = new(code, components[2], components[3], null, null) { Key = DecodeKey(components[4]) };
                break;
            case "person":
            case "label":
            case "alias":
                if (components.Length != 5 || !Guid.TryParseExact(components[4], "D", out var id) || id == Guid.Empty
                    || components[4] != id.ToString("D")) throw Invalid();
                result = new(code, components[2], components[3], null, null) { Id = id };
                break;
            case "inspection":
                var acceptanceInspection = components.Length == 6 && components[4] == "acceptance";
                var inspectionIndex = acceptanceInspection ? 5 : 4;
                if ((!acceptanceInspection && components.Length != 5) || !PositiveDigits(components[inspectionIndex])
                    || !long.TryParse(components[inspectionIndex], NumberStyles.None, CultureInfo.InvariantCulture, out var inspectionNumber)) throw Invalid();
                result = new(code, components[2], components[3], null, null)
                    { RecordNumber = inspectionNumber, InspectionNamespace = acceptanceInspection ? "acceptance" : "legacy" };
                break;
            case "product-release":
            case "shipment":
            case "output":
                if ((components[3] == "output" ? components.Length != 6 : components.Length != 5)
                    || !PositiveDigits(components[4]) || !long.TryParse(components[4], NumberStyles.None, CultureInfo.InvariantCulture, out var number)) throw Invalid();
                int? line = null;
                if (components[3] == "output")
                {
                    if (!PositiveDigits(components[5]) || !int.TryParse(components[5], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedLine)) throw Invalid();
                    line = parsedLine;
                }
                result = new(code, components[2], components[3], null, null) { RecordNumber = number, LineNumber = line };
                break;
            case "instruction":
                if (components.Length != 6) throw Invalid();
                result = new(code, components[2], components[3], null, null) { Key = DecodeKey(components[4]), Revision = DecodeKey(components[5]) };
                break;
            default:
                throw Invalid();
        }

        if (!string.Equals(components[2], configuredNamespace, StringComparison.Ordinal))
            throw new IdentificationException("foreign_namespace", "Identification belongs to a different namespace.");

        return result;
    }

    /// <summary>Escapes exact key bytes without trimming, case folding or Unicode normalization.</summary>
    public static string EncodeKey(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxKeyLength || value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
            throw Invalid();
        try
        {
            var result = new StringBuilder();
            foreach (var valueByte in Utf8.GetBytes(value))
                if (Unreserved(valueByte)) result.Append((char)valueByte);
                else result.Append('%').Append(valueByte.ToString("X2", CultureInfo.InvariantCulture));
            return result.ToString();
        }
        catch (EncoderFallbackException)
        {
            throw Invalid();
        }
    }

    private static string DecodeKey(string value)
    {
        var bytes = new List<byte>();
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '%')
            {
                if (index + 2 >= value.Length || !byte.TryParse(value.AsSpan(index + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var parsed))
                    throw Invalid();
                bytes.Add(parsed);
                index += 2;
            }
            else if (value[index] <= 127 && Unreserved((byte)value[index])) bytes.Add((byte)value[index]);
            else throw Invalid();
        }
        try
        {
            var decoded = Utf8.GetString(bytes.ToArray());
            if (!string.Equals(EncodeKey(decoded), value, StringComparison.Ordinal)) throw Invalid();
            return decoded;
        }
        catch (DecoderFallbackException)
        {
            throw Invalid();
        }
    }

    private static bool Unreserved(byte value) => value is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z'
        or >= (byte)'0' and <= (byte)'9' or (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~';

    private static bool PositiveDigits(string value) => value.Length > 0 && value[0] is >= '1' and <= '9' && value.All(char.IsAsciiDigit);

    private static IdentificationException Invalid() => new("invalid_identification", "Use a canonical Forge identification for a supported subject.");

    [GeneratedRegex("\\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamespacePattern();
}

/// <summary>Identification failures contain a safe outcome code and never repeat untrusted input or private source data.</summary>
public sealed class IdentificationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
