using System.Text.Json;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using static Forge.Host.Api.ApiSupport;

namespace Forge.Host.Api;

public sealed record ScimPatchOperation(string Op, string? Path, JsonElement Value);

public sealed record ScimPatchRequest(string[]? Schemas, ScimPatchOperation[]? Operations);

internal static class ScimEndpoints
{
    private const string PatchSchema = "urn:ietf:params:scim:api:messages:2.0:PatchOp";
    private static readonly string[] UserSchema = ["urn:ietf:params:scim:schemas:core:2.0:User"];

    public static void MapScimEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapMethods("/api/scim/v2/Users/{subject}", ["PATCH"], async (
            HttpContext http,
            string subject,
            ScimPatchRequest body,
            AccessService access,
            IConfiguration configuration,
            TimeProvider time,
            CancellationToken ct) =>
        {
            if (!configuration.GetValue("Forge:Authentication:Scim:Enabled", false))
            {
                return Results.NotFound();
            }

            var requiredScope = configuration["Forge:Authentication:Scim:RequiredScope"] ?? "forge.provisioning";
            var scopes = http.User.FindAll("scope").Concat(http.User.FindAll("scp"))
                .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (!scopes.Contains(requiredScope, StringComparer.Ordinal))
            {
                return Results.Forbid();
            }

            var patch = body.Schemas?.Contains(PatchSchema, StringComparer.Ordinal) == true
                && body.Operations is { Length: 1 } operations
                && string.Equals(operations[0].Op, "replace", StringComparison.OrdinalIgnoreCase)
                && string.Equals(operations[0].Path, "active", StringComparison.OrdinalIgnoreCase)
                && operations[0].Value.ValueKind == JsonValueKind.False;
            if (!patch || string.IsNullOrWhiteSpace(subject))
            {
                return Results.BadRequest(new { code = "unsupported_scim_patch", detail = "Only SCIM replace active=false is accepted." });
            }

            var key = http.Request.Headers[IdempotencyHeader].ToString();
            if (string.IsNullOrWhiteSpace(key) || key.Length > 200)
            {
                return Results.Problem(title: "Idempotency key required", statusCode: StatusCodes.Status400BadRequest);
            }

            var correlation = Guid.TryParse(http.Request.Headers[CorrelationHeader], out var supplied) ? supplied : ForgeIds.New(time);
            http.Response.Headers[CorrelationHeader] = correlation.ToString();
            var servicePrincipal = new ForgePrincipal(
                new PersonId(Guid.Empty),
                "Nexus provisioning",
                true,
                new HashSet<string>(StringComparer.Ordinal),
                new HashSet<ProgramId>(),
                DataMarking.None,
                new HashSet<string>([Permissions.AccessManage], StringComparer.Ordinal));
            var context = new CommandContext(
                servicePrincipal,
                correlation,
                "nexus-scim:" + key,
                CommandSource.Integration,
                "Nexus SCIM deactivation",
                "system:nexus-provisioning");
            var result = await access.DeactivateIdentityAsync(new DeactivateIdentity(subject, "Nexus"), context, ct);
            return Results.Ok(new
            {
                schemas = UserSchema,
                id = result.EntityId,
                externalId = subject,
                active = false,
            });
        }).RequireAuthorization();
    }
}
