namespace Forge.Workforce.Contracts;

/// <summary>Verifies a badge PIN and persists failed-attempt lockout state independently of a command transaction.</summary>
public interface IBadgePinVerifier
{
    Task<bool> VerifyAsync(string badge, string pin, CancellationToken ct);
}
