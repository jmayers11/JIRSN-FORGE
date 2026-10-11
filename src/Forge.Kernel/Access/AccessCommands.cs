using Forge.Kernel.Identity;
using Forge.Kernel.Security;

namespace Forge.Kernel.Access;

public sealed record RegisterPerson(string IdpSubject, string DisplayName, DataMarking Markings);

public sealed record DeactivateIdentity(string IdpSubject, string Provider);

public sealed record AssignRole(PersonId PersonId, string Role, DateTimeOffset? ExpiresAt = null);

public sealed record CreateRole(string Role, string DisplayName, IReadOnlyList<string> Permissions);

public sealed record RevokeRole(PersonId PersonId, string Role);

public sealed record ChangeRolePermission(string Role, string Permission, bool Granted);

public sealed record ChangeRolePermissionScope(string Role, string Permission, string ScopeType, string ScopeId, bool Granted);

public sealed record ChangeSeparationPolicy(string Policy, bool Enabled);

public sealed record ChangeApprovalThreshold(string Permission, decimal? MaximumAmount, string? Currency);

public sealed record GrantProgramAccess(PersonId PersonId, ProgramId ProgramId);

public sealed record RevokeProgramAccess(PersonId PersonId, ProgramId ProgramId);
