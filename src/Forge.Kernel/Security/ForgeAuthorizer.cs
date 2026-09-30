namespace Forge.Kernel.Security;

public sealed record AuthorizationDecision(bool IsPermitted, IReadOnlyList<string> Reasons)
{
    public static AuthorizationDecision Permit { get; } = new(true, []);
}

public interface IForgeAuthorizer
{
    AuthorizationDecision Authorize(ForgePrincipal principal, string permission, ResourceRef resource);
}

/// <summary>
/// The single authorization decision point: role (what you can do) AND program access AND markings
/// (what you can see). Every failed check is reported, so a denial can always be explained.
/// </summary>
public sealed class ForgeAuthorizer : IForgeAuthorizer
{
    public AuthorizationDecision Authorize(ForgePrincipal principal, string permission, ResourceRef resource)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(resource);

        var reasons = new List<string>();

        if (!principal.IsActive)
        {
            reasons.Add("Identity is not active.");
        }

        if (!principal.HasPermission(permission))
        {
            reasons.Add($"No assigned role grants '{permission}'.");
        }

        if (resource.Program is { } program && !principal.Programs.Contains(program))
        {
            reasons.Add("No access to the program that owns this resource.");
        }

        var missing = resource.Markings & ~principal.Markings;
        if (missing != DataMarking.None)
        {
            reasons.Add($"Missing data markings: {missing}.");
        }

        return reasons.Count == 0 ? AuthorizationDecision.Permit : new AuthorizationDecision(false, reasons);
    }
}

public sealed class ForgeAuthorizationException(AuthorizationDecision decision)
    : Exception("Not authorized: " + string.Join(" ", decision.Reasons))
{
    public AuthorizationDecision Decision { get; } = decision;
}
