using System.Security.Cryptography;
using System.Text;
using Forge.Host.Security;
using Forge.Kernel.Access;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;

namespace Forge.Host.Api;

public sealed record LocalSignInRequest(string? Username, string? Password);

public sealed record PasswordChangeRequest(string? CurrentPassword, string? NewPassword);

public sealed record ReauthenticateRequest(string? Password);

public sealed record SetPasswordRequest(string? Password, string? Reason);

public sealed record FirstRunRequest(string? SetupCode, string? DisplayName, string? Username, string? Password);

/// <summary>
/// Forge's own sign-in and the first-run setup wizard of an installed server (owner decisions 2026-11-03). Setup is open only while no
/// active security administrator exists, and only to someone holding the setup code the installer showed on the server.
/// </summary>
internal static class AuthSetupEndpoints
{
    public static void MapAuthSetupEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth/local");
        auth.MapPost("/sign-in", async (LocalSignInRequest b, HttpContext http, CancellationToken ct) =>
        {
            var service = http.RequestServices.GetService<LocalAccountService>();
            if (service is null) return NotLocal();
            var result = await service.SignInAsync(b.Username, b.Password, ct);
            return result.Succeeded ? Results.Ok(result) : Results.Json(result, statusCode: StatusCodes.Status401Unauthorized);
        }).AllowAnonymous();
        auth.MapPost("/change-password", async (PasswordChangeRequest b, HttpContext http, CancellationToken ct) =>
        {
            var service = http.RequestServices.GetService<LocalAccountService>();
            if (service is null) return NotLocal();
            var result = await service.ChangePasswordAsync(http.User, b.CurrentPassword, b.NewPassword, ct);
            return result.Succeeded ? Results.Ok(result) : Results.Json(result, statusCode: StatusCodes.Status422UnprocessableEntity);
        }).RequireAuthorization();
        auth.MapPost("/reauthenticate", async (ReauthenticateRequest b, HttpContext http, CancellationToken ct) =>
        {
            var service = http.RequestServices.GetService<LocalAccountService>();
            if (service is null) return NotLocal();
            var result = await service.ReauthenticateAsync(http.User, b.Password, ct);
            return result.Succeeded ? Results.Ok(result) : Results.Json(result, statusCode: StatusCodes.Status422UnprocessableEntity);
        }).RequireAuthorization();
        app.MapPost("/api/kernel/people/{id:guid}/password", async (Guid id, SetPasswordRequest b, HttpContext http, RequestPrincipal principals, CancellationToken ct) =>
        {
            var service = http.RequestServices.GetService<LocalAccountService>();
            if (service is null) return NotLocal();
            var admin = await principals.ResolveAsync(http.User, ct);
            if (admin is null) return ApiSupport.NotRegistered();
            var problem = await service.SetTemporaryPasswordAsync(admin, new PersonId(id), b.Password, b.Reason, ct);
            return problem is null
                ? Results.Ok(new { code = "ok", message = "Temporary password set. The person chooses their own at next sign-in." })
                : Results.Problem(title: "Password not set", detail: problem, statusCode: problem.StartsWith("Only", StringComparison.Ordinal) ? StatusCodes.Status403Forbidden : StatusCodes.Status422UnprocessableEntity);
        }).RequireAuthorization();

        var setup = app.MapGroup("/api/setup");
        setup.MapGet("/status", async (HttpContext http, IKernelUnitOfWorkFactory unitOfWork, CancellationToken ct) =>
        {
            var available = http.RequestServices.GetService<LocalAccountService>() is not null && SetupCode(http) is not null;
            await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
            var hasAdmin = await uow.People.CountActiveWithRoleAsync(Roles.SecurityAdministrator, ct) > 0;
            return Results.Ok(new { needed = available && !hasAdmin, hasAdministrator = hasAdmin });
        }).AllowAnonymous();
        setup.MapPost("/", async (FirstRunRequest b, HttpContext http, AccessService access, CancellationToken ct) =>
        {
            var service = http.RequestServices.GetService<LocalAccountService>();
            var code = SetupCode(http);
            if (service is null || code is null) return Results.Problem(title: "Setup is not available", detail: "This server was not installed with Forge's setup.", statusCode: StatusCodes.Status404NotFound);
            if (b.SetupCode is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(b.SetupCode.Trim().ToUpperInvariant()), Encoding.UTF8.GetBytes(code)))
                return Results.Problem(title: "Wrong setup code", detail: "Use the setup code shown at the end of the installation (also in setup-code.txt on the server).", statusCode: StatusCodes.Status403Forbidden);
            var username = b.Username?.Trim();
            if (string.IsNullOrWhiteSpace(username) || username.Length > 100 || username.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
                return Results.Problem(title: "Choose a username", detail: "A username has no spaces and is at most 100 characters.", statusCode: StatusCodes.Status422UnprocessableEntity);
            if (string.IsNullOrWhiteSpace(b.DisplayName) || b.DisplayName.Length > 200)
                return Results.Problem(title: "Give your name", detail: "Your name as people should see it.", statusCode: StatusCodes.Status422UnprocessableEntity);
            if (LocalAccountService.PasswordProblem(b.Password) is { } problem)
                return Results.Problem(title: "Choose a stronger password", detail: problem, statusCode: StatusCodes.Status422UnprocessableEntity);
            if (!await service.CreateFirstAdministratorAsync(access, username, b.DisplayName.Trim(), b.Password!, ct))
                return Results.Problem(title: "Setup is already done", detail: "A security administrator exists; sign in instead.", statusCode: StatusCodes.Status409Conflict);
            return Results.Ok(new { code = "ok", message = $"{b.DisplayName.Trim()} is Forge's first security administrator. Sign in as {username}." });
        }).AllowAnonymous();
    }

    private static string? SetupCode(HttpContext http)
    {
        var path = http.RequestServices.GetRequiredService<IConfiguration>()["Forge:Setup:CodeFile"];
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        var code = File.ReadAllText(path).Trim().ToUpperInvariant();
        return code.Length >= 8 ? code : null;
    }

    private static IResult NotLocal() =>
        Results.Problem(title: "Forge accounts are off", detail: "This server signs people in through its identity provider.", statusCode: StatusCodes.Status404NotFound);
}
