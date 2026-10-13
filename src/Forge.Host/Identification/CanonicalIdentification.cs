using System.Globalization;
using System.Text.RegularExpressions;

namespace Forge.Host.Identification;

/// <summary>A bounded presentation of an existing order or operation key, never a second source of identity.</summary>
public sealed partial record CanonicalIdentification(string Code, string Namespace, string Kind, long OrderNumber, int? Sequence)
{
    public const string NamespaceConfigurationKey = "Forge:Identification:Namespace";

    public static bool IsValidNamespace(string? value) => value is not null && NamespacePattern().IsMatch(value);

    public static CanonicalIdentification Parse(string? code, string? configuredNamespace)
    {
        if (!IsValidNamespace(configuredNamespace))
            throw new IdentificationException("identification_not_configured", "Identification requires a valid configured namespace.");

        if (string.IsNullOrEmpty(code) || code.Length > 256 || code.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
            throw Invalid();

        var components = code.Split(':');
        if (components.Length is not (5 or 6) || components[0] != "forge" || components[1] != "v1"
            || !IsValidNamespace(components[2])
            || (components[3] != "order" && components[3] != "operation")
            || (components[3] == "order" ? components.Length != 5 : components.Length != 6)
            || !PositiveDigits(components[4])
            || !long.TryParse(components[4], NumberStyles.None, CultureInfo.InvariantCulture, out var orderNumber))
            throw Invalid();

        int? sequence = null;
        if (components[3] == "operation")
        {
            if (!PositiveDigits(components[5])
                || !int.TryParse(components[5], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedSequence))
                throw Invalid();
            sequence = parsedSequence;
        }

        if (!string.Equals(components[2], configuredNamespace, StringComparison.Ordinal))
            throw new IdentificationException("foreign_namespace", "Identification belongs to a different namespace.");

        return new CanonicalIdentification(code, components[2], components[3], orderNumber, sequence);
    }

    private static bool PositiveDigits(string value) => value.Length > 0 && value[0] is >= '1' and <= '9' && value.All(char.IsAsciiDigit);

    private static IdentificationException Invalid() => new("invalid_identification", "Use a canonical Forge order or operation identification.");

    [GeneratedRegex("\\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamespacePattern();
}

/// <summary>Identification failures contain a safe outcome code and never repeat untrusted input or private source data.</summary>
public sealed class IdentificationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
