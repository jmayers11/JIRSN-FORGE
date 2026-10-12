using System.Globalization;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Postgres;
using Forge.Kernel.Security;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record RegisterPersonRequest(string IdpSubject, string DisplayName, string[]? Markings, string? Reason);

public sealed record RoleRequest(string Role, string? Reason, DateTimeOffset? ExpiresAt = null);

public sealed record CreateRoleRequest(string Role, string DisplayName, string[] Permissions, string? Reason);

public sealed record RolePermissionRequest(string Permission, string? Reason);

public sealed record RoleScopeRequest(string Permission, string ScopeType, string ScopeId, string? Reason);

public sealed record SeparationPolicyRequest(bool Enabled, string? Reason);

public sealed record ApprovalThresholdRequest(decimal? MaximumAmount, string? Currency, string? Reason);

public sealed record ProgramAccessRequest(Guid ProgramId, string? Reason);

public sealed record ProposeDelegationRequest(Guid Recipient, string[]? Permissions, DelegationScope[]? Scopes,
    DateTimeOffset StartsAt, DateTimeOffset ExpiresAt, string? Reason);

public sealed record ReasonRequest(string? Reason);

public static class KernelEndpoints
{
    public static void MapKernelEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/kernel").RequireAuthorization();

        api.MapGet("/me", async (HttpContext http, IConfiguration configuration, CancellationToken _) =>
        {
            var principal = await PrincipalAsync(http);
            var idleTimeoutSeconds = int.TryParse(configuration["Forge:ShopFloor:IdleTimeoutSeconds"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var configuredIdleTimeoutSeconds)
                && configuredIdleTimeoutSeconds is >= 1 and <= 3600 ? configuredIdleTimeoutSeconds : 60;
            return principal is null
                ? NotRegistered()
                : Results.Ok(new
                {
                    personId = principal.PersonId.Value,
                    principal.DisplayName,
                    roles = principal.Roles.Order(StringComparer.Ordinal),
                    programs = principal.Programs.Select(p => p.Value).Order(),
                    markings = principal.Markings.ToString(),
                    permissions = (principal.EffectivePermissions ?? principal.Roles.SelectMany(Roles.PermissionsOf).ToHashSet(StringComparer.Ordinal)).Order(StringComparer.Ordinal),
                    scopedPermissions = principal.ScopedPermissions ?? [],
                    approvalThresholds = principal.ApprovalThresholds ?? [],
                    delegatedPermissions = principal.DelegatedPermissions ?? [],
                    shopFloorIdleTimeoutSeconds = idleTimeoutSeconds,
                });
        });

        // Names only, for showing who did what (actors are recorded as person IDs). Any registered person may read it.
        api.MapGet("/directory", async (HttpContext http, KernelReadStore reads, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null)
            {
                return NotRegistered();
            }

            var people = await reads.ListPeopleAsync(ct);
            return Results.Ok(people.Where(p => p.IsActive).Select(p => new { p.PersonId, p.DisplayName }).ToList());
        });

        api.MapGet("/people", (HttpContext http, KernelReadStore reads, CancellationToken ct) =>
            Read(http, Permissions.PeopleRead, async _ => await reads.ListPeopleAsync(ct)));

        api.MapPost("/people", (HttpContext http, RegisterPersonRequest body, AccessService access, CancellationToken ct) =>
            Command(http, body.Reason, context =>
            {
                var markings = DataMarking.None;
                foreach (var m in body.Markings ?? [])
                {
                    if (!Enum.TryParse<DataMarking>(m, ignoreCase: false, out var parsed) || !Enum.IsDefined(parsed))
                    {
                        throw new CommandRejectedException("unknown_marking", $"Unknown data marking '{m}'.");
                    }

                    markings |= parsed;
                }

                return access.RegisterPersonAsync(new RegisterPerson(body.IdpSubject, body.DisplayName, markings), context, ct);
            }));

        api.MapPost("/people/{personId:guid}/roles", (HttpContext http, Guid personId, RoleRequest body, AccessService access, CancellationToken ct) =>
            Command(http, body.Reason, context => access.AssignRoleAsync(new AssignRole(new PersonId(personId), body.Role, body.ExpiresAt), context, ct)));

        api.MapGet("/delegations", async (HttpContext http, DelegationService service, TimeProvider time, CancellationToken ct) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null) return NotRegistered();
            var records = await service.ListAsync(principal, ct);
            return Results.Ok(records.Select(record => new
            {
                record.DelegationId, grantor = record.Grantor.Value, recipient = record.Recipient.Value,
                record.Permissions, record.Scopes, record.StartsAt, record.ExpiresAt, state = record.StateAt(time.GetUtcNow()),
                record.ProposedAt, record.Reason, approver = record.Approver?.Value, record.ApprovedAt, record.ApprovalReason,
                record.RevokedAt, record.RevocationReason,
            }));
        });
        api.MapGet("/delegations/lendable", async (HttpContext http) =>
        {
            var principal = await PrincipalAsync(http);
            if (principal is null) return NotRegistered();
            return Results.Ok(new
            {
                permissions = (principal.EffectivePermissions ?? new HashSet<string>()).Concat(principal.ScopedPermissions?.Select(grant => grant.Permission) ?? [])
                    .Where(permission => permission is not (Permissions.AccessManage or Permissions.DelegationPropose)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
                principal.ScopedPermissions,
            });
        });
        api.MapPost("/delegations", (HttpContext http, ProposeDelegationRequest body, DelegationService service, CancellationToken ct) =>
            Command(http, body.Reason, context => service.ProposeAsync(new ProposeDelegation(new PersonId(body.Recipient), body.Permissions ?? [], body.Scopes ?? [], body.StartsAt, body.ExpiresAt), context, ct)));
        api.MapPost("/delegations/{delegationId:guid}/approve", (HttpContext http, Guid delegationId, ReasonRequest body, DelegationService service, CancellationToken ct) =>
            Command(http, body.Reason, context => service.ApproveAsync(new ApproveDelegation(delegationId), context, ct)));
        api.MapPost("/delegations/{delegationId:guid}/revoke", (HttpContext http, Guid delegationId, ReasonRequest body, DelegationService service, CancellationToken ct) =>
            Command(http, body.Reason, context => service.RevokeAsync(new RevokeDelegation(delegationId), context, ct)));

        api.MapPost("/roles", (HttpContext http, CreateRoleRequest body, AccessService access, CancellationToken ct) =>
            Command(http, body.Reason, context => access.CreateRoleAsync(new CreateRole(body.Role, body.DisplayName, body.Permissions), context, ct)));

        api.MapPost("/roles/{role}/permissions", (HttpContext http, string role, RolePermissionRequest body, AccessService access, CancellationToken ct) =>
            Command(http, body.Reason, context => access.ChangeRolePermissionAsync(new ChangeRolePermission(role, body.Permission, true), context, ct)));

        api.MapPost("/roles/{role}/permissions/{permission}/revoke", (HttpContext http, string role, string permission, ReasonRequest body, AccessService access, CancellationToken ct) =>
            Command(http, body.Reason, context => access.ChangeRolePermissionAsync(new ChangeRolePermission(role, permission, false), context, ct)));

        api.MapPost("/roles/{role}/permission-scopes", (HttpContext http, string role, RoleScopeRequest body, AccessService access, CancellationToken ct) =>
            Command(http, body.Reason, context => access.ChangeRolePermissionScopeAsync(new ChangeRolePermissionScope(role, body.Permission, body.ScopeType, body.ScopeId, true), context, ct)));

        api.MapPost("/roles/{role}/permission-scopes/revoke", (HttpContext http, string role, RoleScopeRequest body, AccessService access, CancellationToken ct) =>
            Command(http, body.Reason, context => access.ChangeRolePermissionScopeAsync(new ChangeRolePermissionScope(role, body.Permission, body.ScopeType, body.ScopeId, false), context, ct)));

        api.MapPost("/separation-policies/{policy}", (HttpContext http, string policy, SeparationPolicyRequest body, AccessService access, CancellationToken ct) =>
            Command(http, body.Reason, context => access.ChangeSeparationPolicyAsync(new ChangeSeparationPolicy(policy, body.Enabled), context, ct)));

        api.MapPost("/approval-thresholds/{permission}", (HttpContext http, string permission, ApprovalThresholdRequest body, AccessService access, CancellationToken ct) =>
            Command(http, body.Reason, context => access.ChangeApprovalThresholdAsync(new ChangeApprovalThreshold(permission, body.MaximumAmount, body.Currency), context, ct)));

        api.MapPost("/people/{personId:guid}/roles/{role}/revoke", (HttpContext http, Guid personId, string role, ReasonRequest body, AccessService access, CancellationToken ct) =>
            Command(http, body.Reason, context => access.RevokeRoleAsync(new RevokeRole(new PersonId(personId), role), context, ct)));

        api.MapPost("/people/{personId:guid}/programs", (HttpContext http, Guid personId, ProgramAccessRequest body, AccessService access, CancellationToken ct) =>
            Command(http, body.Reason, context => access.GrantProgramAccessAsync(new GrantProgramAccess(new PersonId(personId), new ProgramId(body.ProgramId)), context, ct)));

        api.MapPost("/people/{personId:guid}/programs/{programId:guid}/revoke", (HttpContext http, Guid personId, Guid programId, ReasonRequest body, AccessService access, CancellationToken ct) =>
            Command(http, body.Reason, context => access.RevokeProgramAccessAsync(new RevokeProgramAccess(new PersonId(personId), new ProgramId(programId)), context, ct)));

        api.MapGet("/audit", (HttpContext http, string? entityType, string? entityId, int? take, KernelReadStore reads, CancellationToken ct) =>
            Read(http, Permissions.AuditRead, async principal => await reads.QueryAuditAsync(entityType, entityId, principal.Programs, take ?? 100, ct)));

        api.MapGet("/audit/verify", (HttpContext http, KernelReadStore reads, CancellationToken ct) =>
            Read(http, Permissions.AuditRead, async _ => await reads.VerifyAuditChainAsync(ct)));
    }
}
