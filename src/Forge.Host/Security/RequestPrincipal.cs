using System.Security.Claims;
using Forge.Kernel.Postgres;
using Forge.Kernel.Security;

namespace Forge.Host.Security;

/// <summary>
/// Turns an authenticated identity-provider subject into the Forge principal (roles, programs,
/// markings). An authenticated person unknown to Forge, or inactive, gets nothing.
/// </summary>
public sealed class RequestPrincipal(KernelReadStore readStore)
{
    public const string SubjectClaim = "sub";

    public async Task<ForgePrincipal?> ResolveAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        var subject = user.FindFirstValue(SubjectClaim) ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        var principal = await readStore.ResolvePrincipalBySubjectAsync(subject, ct);
        return principal is { IsActive: true } ? principal : null;
    }
}
