using Microsoft.AspNetCore.DataProtection;

namespace Forge.Host.Security;

/// <summary>
/// Encrypts secrets Forge must be able to use again — webhook signing secrets, machine connection passwords — before they are stored
/// (owner decisions 2026-11-01). ASP.NET Core data protection keys live in Forge:Secrets:KeyFolder (on Windows also protected with DPAPI).
/// Stored values start with "enc:"; a value without it was stored before encryption and is used as it is.
/// </summary>
public interface ISecretProtector
{
    string Protect(string plain);

    string Unprotect(string stored);
}

public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    public const string Prefix = "enc:";
    private readonly IDataProtector protector = provider.CreateProtector("Forge.Secrets.v1");

    public string Protect(string plain)
    {
        ArgumentNullException.ThrowIfNull(plain);
        return plain.StartsWith(Prefix, StringComparison.Ordinal) ? plain : Prefix + protector.Protect(plain);
    }

    public string Unprotect(string stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        return stored.StartsWith(Prefix, StringComparison.Ordinal) ? protector.Unprotect(stored[Prefix.Length..]) : stored;
    }
}
