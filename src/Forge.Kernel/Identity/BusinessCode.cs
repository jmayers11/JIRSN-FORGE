using System.Text.RegularExpressions;
using Forge.Kernel.Commands;

namespace Forge.Kernel.Identity;

/// <summary>
/// Human-readable, immutable business keys (machine "SLITTER-02", qualification "SLIT-II").
/// Upper-case letters, digits, '-' and '_', 1 to 40 characters.
/// </summary>
public static partial class BusinessCode
{
    public static string Require(string? value, string what)
    {
        var code = (value ?? string.Empty).Trim().ToUpperInvariant();
        if (!Pattern().IsMatch(code))
        {
            throw new CommandRejectedException("invalid_code", $"{what} code '{value}' is invalid. Use 1-40 characters: A-Z, 0-9, '-' or '_'.");
        }

        return code;
    }

    public static string RequireName(string? value, string what)
    {
        var name = (value ?? string.Empty).Trim();
        if (name.Length is 0 or > 200)
        {
            throw new CommandRejectedException("invalid_name", $"{what} name is required (at most 200 characters).");
        }

        return name;
    }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9_-]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
