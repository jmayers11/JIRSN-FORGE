using Forge.Kernel.Identity;
using Forge.Kernel.Access;

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

public sealed record DelegatedPermission(Guid DelegationId, string Permission, IReadOnlyList<DelegationScope> Scopes,
    DateTimeOffset StartsAt, DateTimeOffset ExpiresAt, ApprovalThreshold? SourceThreshold = null)
{
    public bool MatchesScope(ResourceRef? resource) => Scopes.Count == 0 || resource is not null && Scopes.All(scope => scope.Matches(resource));
    public bool Matches(ResourceRef? resource, DateTimeOffset? at = null) => MatchesScope(resource)
        && (at is null || at >= StartsAt && at < ExpiresAt)
        && (SourceThreshold is null || SourceThreshold.Allows(resource));
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
    IReadOnlyList<ApprovalThreshold>? ApprovalThresholds = null,
    IReadOnlyList<DelegatedPermission>? DelegatedPermissions = null)
{
    public bool HasPermission(string permission, ResourceRef? resource = null, DateTimeOffset? at = null)
    {
        var permissionGranted = (EffectivePermissions is { } configured
        ? configured.Contains(permission)
        : Roles.Any(r => Security.Roles.PermissionsOf(r).Contains(permission, StringComparer.Ordinal)))
            || (ScopedPermissions?.Any(p => p.Permission == permission && p.Matches(resource)) ?? false)
            || (DelegatedPermissions?.Any(p => p.Permission == permission && p.Matches(resource, at)) ?? false);
        var threshold = ApprovalThresholds?.FirstOrDefault(t => t.Permission == permission);
        return permissionGranted && (threshold is null || threshold.Allows(resource));
    }

    public bool ExceedsApprovalThreshold(string permission, ResourceRef resource) =>
        ApprovalThresholds?.FirstOrDefault(t => t.Permission == permission) is { } threshold && !threshold.Allows(resource)
        || (DelegatedPermissions?.Any(grant => grant.Permission == permission && grant.MatchesScope(resource)
            && grant.SourceThreshold is { } source && !source.Allows(resource)) ?? false);
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

    /// <summary>Re-plans the schedule after a failed inspection or a new NCR (ADR 0057 §2.4).</summary>
    public const string QualityReplan = "system:quality-replan";

    /// <summary>Reminds supervisors of temporary moves ending and returns workers when they run out (ADR 0057 §5).</summary>
    public const string WorkforceMoves = "system:workforce-moves";

    /// <summary>Opens training requests for qualifications about to expire.</summary>
    public const string TrainingRequests = "system:training-requests";

    /// <summary>Tells planners when released orders become, or stop being, at risk from material.</summary>
    public const string MaterialRisk = "system:material-risk";

    /// <summary>Keeps an approved sales order's customer-order demand in step with the order.</summary>
    public const string SalesDemand = "system:sales-demand";

    /// <summary>Keeps stock reservations in step with released orders and approved sales orders.</summary>
    public const string StockReservations = "system:stock-reservations";
}
