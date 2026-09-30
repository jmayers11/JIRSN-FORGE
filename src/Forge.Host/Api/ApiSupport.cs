using Forge.Host.Security;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;

namespace Forge.Host.Api;

/// <summary>Shared request handling: resolve the Forge principal, build the command context, check permissions.</summary>
internal static class ApiSupport
{
    public const string IdempotencyHeader = "Idempotency-Key";
    public const string CorrelationHeader = "X-Correlation-Id";

    public static Task<ForgePrincipal?> PrincipalAsync(HttpContext http) =>
        http.RequestServices.GetRequiredService<RequestPrincipal>().ResolveAsync(http.User, http.RequestAborted);

    /// <summary>Runs a command with the caller's principal, idempotency key, correlation ID and reason.</summary>
    public static async Task<IResult> Command(HttpContext http, string? reason, Func<CommandContext, Task<CommandResult>> execute)
    {
        var principal = await PrincipalAsync(http);
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

        var time = http.RequestServices.GetRequiredService<TimeProvider>();
        var correlation = Guid.TryParse(http.Request.Headers[CorrelationHeader], out var supplied) ? supplied : ForgeIds.New(time);
        var context = new CommandContext(principal, correlation, "person:" + principal.PersonId + ":" + key, CommandSource.Api, reason);
        var result = await execute(context);
        http.Response.Headers[CorrelationHeader] = correlation.ToString();
        return Results.Ok(result);
    }

    /// <summary>Runs a read with the caller's principal after checking one permission.</summary>
    public static async Task<IResult> Read(HttpContext http, string permission, Func<ForgePrincipal, Task<object?>> query)
    {
        var principal = await PrincipalAsync(http);
        if (principal is null)
        {
            return NotRegistered();
        }

        var decision = http.RequestServices.GetRequiredService<IForgeAuthorizer>().Authorize(principal, permission, ResourceRef.Of(permission));
        if (!decision.IsPermitted)
        {
            throw new ForgeAuthorizationException(decision);
        }

        return Results.Ok(await query(principal));
    }

    /// <summary>A read against one context's lookup, in a consistent read-only transaction.</summary>
    public static Task<IResult> ReadStore<TStore>(HttpContext http, string permission, Func<TStore, CancellationToken, Task<object?>> query)
        where TStore : class =>
        Read(http, permission, async _ =>
        {
            await using var uow = await http.RequestServices.GetRequiredService<Forge.Kernel.Access.IKernelUnitOfWorkFactory>().BeginSnapshotAsync(http.RequestAborted);
            return await query(uow.Store<TStore>(), http.RequestAborted);
        });

    public static IResult NotRegistered() => Results.Problem(
        title: "Not registered in Forge",
        detail: "Your identity is authenticated but is not registered or not active in Forge. Ask a security administrator.",
        statusCode: StatusCodes.Status403Forbidden);
}
