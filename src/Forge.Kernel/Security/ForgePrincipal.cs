using Forge.Kernel.Identity;

namespace Forge.Kernel.Security;

public sealed record ScopedPermission(string Permission, string ScopeType, string ScopeId)
{
    public bool Matches(ResourceRef? resource)
    {
        if (resource is null)
        {
            return false;
        }

        var value = ScopeType switch
        {
            "program" => resource.Program?.Value.ToString("D"),
            "facility" => resource.FacilityId,
            "work_center" => resource.WorkCenterId,
            "contract" => resource.ContractId,
            "customer" => resource.CustomerId,
            "shift" => resource.ShiftId,
            _ => null,
        };
        return string.Equals(value, ScopeId, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record ApprovalThreshold(string Permission, decimal MaximumAmount, string Currency)
{
    public bool Allows(ResourceRef? resource) => resource?.Amount is { } amount
        && string.Equals(resource.Currency, Currency, StringComparison.OrdinalIgnoreCase)
        && amount <= MaximumAmount;
}

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
    DataMarking Markings,
    IReadOnlySet<string>? EffectivePermissions = null,
    IReadOnlyList<ScopedPermission>? ScopedPermissions = null,
    IReadOnlyList<ApprovalThreshold>? ApprovalThresholds = null)
{
    public bool HasPermission(string permission, ResourceRef? resource = null)
    {
        var permissionGranted = (EffectivePermissions is { } configured
        ? configured.Contains(permission)
        : Roles.Any(r => Security.Roles.PermissionsOf(r).Contains(permission, StringComparer.Ordinal)))
            || (ScopedPermissions?.Any(p => p.Permission == permission && p.Matches(resource)) ?? false);
        var threshold = ApprovalThresholds?.FirstOrDefault(t => t.Permission == permission);
        return permissionGranted && (threshold is null || threshold.Allows(resource));
    }

    public bool ExceedsApprovalThreshold(string permission, ResourceRef resource) =>
        ApprovalThresholds?.FirstOrDefault(t => t.Permission == permission) is { } threshold && !threshold.Allows(resource);
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
