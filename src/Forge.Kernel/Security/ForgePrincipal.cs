using Forge.Kernel.Identity;

namespace Forge.Kernel.Security;

/// <summary>
/// Who is acting, as Forge understands them: the identity provider vouches that the person exists
/// and is active; Forge supplies roles, program access and markings.
/// </summary>
public sealed record ForgePrincipal(
    PersonId PersonId,
    string DisplayName,
    bool IsActive,
    IReadOnlySet<string> Roles,
    IReadOnlySet<ProgramId> Programs,
    DataMarking Markings)
{
    public bool HasPermission(string permission) =>
        Roles.Any(r => Security.Roles.PermissionsOf(r).Contains(permission, StringComparer.Ordinal));
}

/// <summary>An actor that is not a person: background relays, bootstrap, integrations.</summary>
public static class SystemActors
{
    public const string Bootstrap = "system:bootstrap";
    public const string OutboxRelay = "system:outbox-relay";

    /// <summary>Carries a released engineering change into orders and actions (V2.5).</summary>
    public const string Propagation = "system:propagation";

    /// <summary>Records parts issued to a work order as installed on its asset (V2.9).</summary>
    public const string Installation = "system:installation";
}
