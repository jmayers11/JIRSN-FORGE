using Forge.Kernel.Security;

namespace Forge.Host.Api;

/// <summary>Allows business-record enumeration for current grants; every returned record still needs contextual authorization.</summary>
internal static class BusinessSourceReadAccess
{
    public static void Demand(HttpContext http, ForgePrincipal principal, string permission)
    {
        var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        var granted = principal.EffectivePermissions is { } effective ? effective.Contains(permission)
            : principal.Roles.Any(role => Roles.PermissionsOf(role).Contains(permission, StringComparer.Ordinal));
        if (granted || principal.ScopedPermissions?.Any(grant => grant.Permission == permission) == true
            || principal.DelegatedPermissions?.Any(grant => grant.Permission == permission && grant.StartsAt <= now && now < grant.ExpiresAt) == true) return;
        throw new ForgeAuthorizationException(http.RequestServices.GetRequiredService<IForgeAuthorizer>()
            .Authorize(principal, permission, ResourceRef.Of(permission)));
    }
}
