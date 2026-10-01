using System.Security.Cryptography;
using System.Text;

namespace Forge.Workforce;

/// <summary>
/// Badge and PIN handling. Forge never stores a badge number or a PIN: badges are kept as a
/// SHA-256 digest (enough to look one up, useless for printing a copy), PINs as PBKDF2 hashes with
/// a per-badge salt. Any scanner technology works: whatever the reader types is the badge value.
/// </summary>
public static class Badges
{
    private const int PinIterations = 100_000;

    /// <summary>Scanners differ in case and padding; the same card must give the same digest.</summary>
    public static string Normalize(string badge)
    {
        ArgumentNullException.ThrowIfNull(badge);
        var value = new string(badge.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)).ToArray()).ToUpperInvariant();
        if (value.Length is < 4 or > 128)
        {
            throw new ArgumentException("A badge value must be 4 to 128 characters.", nameof(badge));
        }

        return value;
    }

    public static bool TryNormalize(string? badge, out string normalized)
    {
        normalized = string.Empty;
        if (badge is null)
        {
            return false;
        }

        try
        {
            normalized = Normalize(badge);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static string Digest(string badge) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("forge-badge:" + Normalize(badge))));

    public static bool IsValidPin(string pin) => pin is { Length: >= 4 and <= 8 } && pin.All(char.IsAsciiDigit);

    public static (byte[] Hash, byte[] Salt) HashPin(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return (Pbkdf2(pin, salt), salt);
    }

    public static bool PinMatches(string pin, byte[] hash, byte[] salt) =>
        CryptographicOperations.FixedTimeEquals(Pbkdf2(pin, salt), hash);

    private static byte[] Pbkdf2(string pin, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, PinIterations, HashAlgorithmName.SHA256, 32);
}
