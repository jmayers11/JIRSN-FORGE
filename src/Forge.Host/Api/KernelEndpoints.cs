using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Postgres;
using Forge.Kernel.Security;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record RegisterPersonRequest(string IdpSubject, string DisplayName, string[]? Markings, string? Reason);

public sealed record RoleRequest(string Role, string? Reason);

public sealed record ProgramAccessRequest(Guid ProgramId, string? Reason);

public sealed record ReasonRequest(string? Reason);

public static class KernelEndpoints
{
    public static void MapKernelEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/kernel").RequireAuthorization();

        api.MapGet("/me", async (HttpContext http, CancellationToken _) =>
        {
            var principal = await PrincipalAsync(http);
            return principal is null
                ? NotRegistered()
                : Results.Ok(new
                {
                    personId = principal.PersonId.Value,
                    principal.DisplayName,
                    roles = principal.Roles.Order(StringComparer.Ordinal),
                    programs = principal.Programs.Select(p => p.Value).Order(),
                    markings = principal.Markings.ToString(),
                });
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
            Command(http, body.Reason, context => access.AssignRoleAsync(new AssignRole(new PersonId(personId), body.Role), context, ct)));

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
