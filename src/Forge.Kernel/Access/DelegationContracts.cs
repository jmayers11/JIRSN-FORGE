using Forge.Kernel.Identity;
using Forge.Kernel.Security;

namespace Forge.Kernel.Access;

public sealed record DelegationScope(string ScopeType, string ScopeId)
{
    public bool Matches(ResourceRef resource) => new ScopedPermission(string.Empty, ScopeType, ScopeId).Matches(resource);
}

public enum DelegationStatus { PendingApproval, Approved, Revoked }

public sealed record AuthorityDelegation(
    Guid DelegationId, PersonId Grantor, PersonId Recipient, IReadOnlyList<string> Permissions,
    IReadOnlyList<DelegationScope> Scopes, DateTimeOffset StartsAt, DateTimeOffset ExpiresAt,
    DelegationStatus Status, DateTimeOffset ProposedAt, string Reason,
    PersonId? Approver = null, DateTimeOffset? ApprovedAt = null, string? ApprovalReason = null,
    DateTimeOffset? RevokedAt = null, string? RevocationReason = null)
{
    public string StateAt(DateTimeOffset at) => Status switch
    {
        DelegationStatus.PendingApproval => "Pending approval",
        DelegationStatus.Revoked => "Revoked",
        _ when at >= ExpiresAt => "Expired",
        _ when at < StartsAt => "Scheduled",
        _ => "Active",
    };
}

public sealed record ProposeDelegation(PersonId Recipient, IReadOnlyList<string> Permissions,
    IReadOnlyList<DelegationScope> Scopes, DateTimeOffset StartsAt, DateTimeOffset ExpiresAt);
public sealed record ApproveDelegation(Guid DelegationId);
public sealed record RevokeDelegation(Guid DelegationId);

public interface IDelegationStore
{
    Task<AuthorityDelegation?> FindAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<AuthorityDelegation>> ListAsync(PersonId? participant, CancellationToken ct);
    Task<IReadOnlyList<AuthorityDelegation>> ActiveForRecipientAsync(PersonId recipient, DateTimeOffset at, CancellationToken ct);
    Task InsertAsync(AuthorityDelegation delegation, CancellationToken ct);
    Task<bool> ApproveAsync(Guid id, PersonId approver, DateTimeOffset at, string reason, CancellationToken ct);
    Task<bool> RevokeAsync(Guid id, DateTimeOffset at, string reason, CancellationToken ct);
}

/// <summary>Lending is limited to role-derived authority. Borrowed authority cannot be lent onward.</summary>
public static class DelegationAuthority
{
    public static bool CanLend(ForgePrincipal grantor, string permission, IReadOnlyList<DelegationScope> scopes)
    {
        if (!grantor.IsActive || permission is Permissions.AccessManage or Permissions.DelegationPropose) return false;
        if (scopes.Any(scope => scope.ScopeType == "program"
            && (!Guid.TryParse(scope.ScopeId, out var programId) || !grantor.Programs.Contains(new ProgramId(programId))))) return false;
        var global = grantor.EffectivePermissions?.Contains(permission)
            ?? grantor.Roles.Any(role => Roles.PermissionsOf(role).Contains(permission, StringComparer.Ordinal));
        if (global) return true;
        return grantor.ScopedPermissions?.Any(grant => grant.Permission == permission
            && scopes.Any(scope => scope.ScopeType == grant.ScopeType
                && string.Equals(scope.ScopeId, grant.ScopeId, StringComparison.OrdinalIgnoreCase))) ?? false;
    }

    public static ResourceRef Resource(IReadOnlyList<DelegationScope> scopes, string? id = null)
    {
        string? Value(string type) => scopes.FirstOrDefault(scope => scope.ScopeType == type)?.ScopeId;
        var program = Guid.TryParse(Value("program"), out var programId) ? new ProgramId(programId) : (ProgramId?)null;
        return new ResourceRef("AuthorityDelegation", id, program, FacilityId: Value("facility"),
            WorkCenterId: Value("work_center"), ContractId: Value("contract"), CustomerId: Value("customer"), ShiftId: Value("shift"));
    }
}
