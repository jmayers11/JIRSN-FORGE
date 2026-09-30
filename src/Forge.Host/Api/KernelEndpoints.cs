using Forge.Host.Security;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Postgres;
using Forge.Kernel.Security;

namespace Forge.Host.Api;

public sealed record RegisterPersonRequest(string IdpSubject, string DisplayName, string[]? Markings, string? Reason);

public sealed record RoleRequest(string Role, string? Reason);

public sealed record ProgramAccessRequest(Guid ProgramId, string? Reason);

public sealed record ReasonRequest(string? Reason);

public static class KernelEndpoints
{
    public const string IdempotencyHeader = "Idempotency-Key";
    public const string CorrelationHeader = "X-Correlation-Id";

    public static void MapKernelEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/kernel").RequireAuthorization();

        api.MapGet("/me", async (HttpContext http, RequestPrincipal principals, CancellationToken ct) =>
        {
            var principal = await principals.ResolveAsync(http.User, ct);
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

        api.MapGet("/people", async (HttpContext http, RequestPrincipal principals, IForgeAuthorizer authorizer, KernelReadStore reads, CancellationToken ct) =>
        {
            var principal = await principals.ResolveAsync(http.User, ct);
            if (principal is null)
            {
                return NotRegistered();
            }

            Demand(authorizer, principal, Permissions.PeopleRead, ResourceRef.Of("Person"));
            return Results.Ok(await reads.ListPeopleAsync(ct));
        });

        api.MapPost("/people", (HttpContext http, RegisterPersonRequest body, AccessService access, RequestPrincipal principals, TimeProvider time, CancellationToken ct) =>
            Command(http, principals, time, body.Reason, context =>
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

        api.MapPost("/people/{personId:guid}/roles", (HttpContext http, Guid personId, RoleRequest body, AccessService access, RequestPrincipal principals, TimeProvider time, CancellationToken ct) =>
            Command(http, principals, time, body.Reason, context =>
                access.AssignRoleAsync(new AssignRole(new PersonId(personId), body.Role), context, ct)));

        api.MapPost("/people/{personId:guid}/roles/{role}/revoke", (HttpContext http, Guid personId, string role, ReasonRequest body, AccessService access, RequestPrincipal principals, TimeProvider time, CancellationToken ct) =>
            Command(http, principals, time, body.Reason, context =>
                access.RevokeRoleAsync(new RevokeRole(new PersonId(personId), role), context, ct)));

        api.MapPost("/people/{personId:guid}/programs", (HttpContext http, Guid personId, ProgramAccessRequest body, AccessService access, RequestPrincipal principals, TimeProvider time, CancellationToken ct) =>
            Command(http, principals, time, body.Reason, context =>
                access.GrantProgramAccessAsync(new GrantProgramAccess(new PersonId(personId), new ProgramId(body.ProgramId)), context, ct)));

        api.MapPost("/people/{personId:guid}/programs/{programId:guid}/revoke", (HttpContext http, Guid personId, Guid programId, ReasonRequest body, AccessService access, RequestPrincipal principals, TimeProvider time, CancellationToken ct) =>
            Command(http, principals, time, body.Reason, context =>
                access.RevokeProgramAccessAsync(new RevokeProgramAccess(new PersonId(personId), new ProgramId(programId)), context, ct)));

        api.MapGet("/audit", async (HttpContext http, string? entityType, string? entityId, int? take, RequestPrincipal principals, IForgeAuthorizer authorizer, KernelReadStore reads, CancellationToken ct) =>
        {
            var principal = await principals.ResolveAsync(http.User, ct);
            if (principal is null)
            {
                return NotRegistered();
            }

            Demand(authorizer, principal, Permissions.AuditRead, ResourceRef.Of("Audit"));
            var entries = await reads.QueryAuditAsync(entityType, entityId, principal.Programs, take ?? 100, ct);
            return Results.Ok(entries);
        });

        api.MapGet("/audit/verify", async (HttpContext http, RequestPrincipal principals, IForgeAuthorizer authorizer, KernelReadStore reads, CancellationToken ct) =>
        {
            var principal = await principals.ResolveAsync(http.User, ct);
            if (principal is null)
            {
                return NotRegistered();
            }

            Demand(authorizer, principal, Permissions.AuditRead, ResourceRef.Of("Audit"));
            return Results.Ok(await reads.VerifyAuditChainAsync(ct));
        });
    }

    private static async Task<IResult> Command(
        HttpContext http,
        RequestPrincipal principals,
        TimeProvider time,
        string? reason,
        Func<CommandContext, Task<CommandResult>> execute)
    {
        var principal = await principals.ResolveAsync(http.User, http.RequestAborted);
        if (principal is null)
        {
            return NotRegistered();
        }

        var key = http.Request.Headers[IdempotencyHeader].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200)
        {
            return Results.Problem(
                title: "Idempotency key required",
                detail: $"Send a unique '{IdempotencyHeader}' header (at most 200 characters) with every command.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var correlation = Guid.TryParse(http.Request.Headers[CorrelationHeader], out var supplied) ? supplied : ForgeIds.New(time);
        var context = new CommandContext(principal, correlation, "person:" + principal.PersonId + ":" + key, CommandSource.Api, reason);
        var result = await execute(context);
        http.Response.Headers[CorrelationHeader] = correlation.ToString();
        return Results.Ok(result);
    }

    private static void Demand(IForgeAuthorizer authorizer, ForgePrincipal principal, string permission, ResourceRef resource)
    {
        var decision = authorizer.Authorize(principal, permission, resource);
        if (!decision.IsPermitted)
        {
            throw new ForgeAuthorizationException(decision);
        }
    }

    private static IResult NotRegistered() => Results.Problem(
        title: "Not registered in Forge",
        detail: "Your identity is authenticated but is not registered or not active in Forge. Ask a security administrator.",
        statusCode: StatusCodes.Status403Forbidden);
}
