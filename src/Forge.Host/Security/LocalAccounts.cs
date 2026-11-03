using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using Forge.Kernel.Access;
using Forge.Kernel.Audit;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Postgres;
using Forge.Kernel.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Forge.Host.Security;

public sealed record LocalCredential(string Subject, string PasswordHash, bool MustChange, int FailedAttempts, DateTimeOffset? LockedUntil, DateTimeOffset ChangedAt, string ChangedBy,
    DateTimeOffset? LastSignInAt);

public interface ILocalCredentialStore
{
    Task<LocalCredential?> FindAsync(string subject, CancellationToken ct);

    Task UpsertAsync(LocalCredential credential, CancellationToken ct);

    Task RecordFailureAsync(string subject, int attempts, DateTimeOffset? lockedUntil, CancellationToken ct);

    Task RecordSignInAsync(string subject, DateTimeOffset at, CancellationToken ct);
}

internal sealed class LocalCredentialStore(PostgresUnitOfWork uow) : ILocalCredentialStore
{
    public async Task<LocalCredential?> FindAsync(string subject, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "SELECT idp_subject, password_hash, must_change, failed_attempts, locked_until, changed_at, changed_by, last_sign_in_at FROM kernel.local_credential WHERE idp_subject = $1 FOR UPDATE",
            subject);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new LocalCredential(r.GetString(0), r.GetString(1), r.GetBoolean(2), r.GetInt32(3), r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4), r.GetFieldValue<DateTimeOffset>(5),
                r.GetString(6), r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7))
            : null;
    }

    public async Task UpsertAsync(LocalCredential c, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            """
            INSERT INTO kernel.local_credential (idp_subject, password_hash, must_change, failed_attempts, locked_until, changed_at, changed_by) VALUES ($1, $2, $3, 0, NULL, $4, $5)
            ON CONFLICT (idp_subject) DO UPDATE SET password_hash = EXCLUDED.password_hash, must_change = EXCLUDED.must_change, failed_attempts = 0, locked_until = NULL,
                changed_at = EXCLUDED.changed_at, changed_by = EXCLUDED.changed_by
            """, c.Subject, c.PasswordHash, c.MustChange, c.ChangedAt, c.ChangedBy);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordFailureAsync(string subject, int attempts, DateTimeOffset? lockedUntil, CancellationToken ct)
    {
        await using var cmd = uow.Command("UPDATE kernel.local_credential SET failed_attempts = $2, locked_until = $3 WHERE idp_subject = $1", subject, attempts,
            new Npgsql.NpgsqlParameter { Value = (object?)lockedUntil ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordSignInAsync(string subject, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = uow.Command("UPDATE kernel.local_credential SET failed_attempts = 0, locked_until = NULL, last_sign_in_at = $2 WHERE idp_subject = $1", subject, at);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>How Forge's own accounts sign people in: the token signing key, token lifetime, lockout and the assurance a password gives.</summary>
public sealed class LocalAuthenticationOptions
{
    public const string Scheme = "ForgeLocal";
    public const string Issuer = "urn:forge:local";
    public const string Audience = "forge-api";
    public const string MustChangeClaim = "forge_pwd_change";

    public LocalAuthenticationOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var raw = configuration["Forge:Authentication:Local:SigningKey"];
        byte[] key;
        try
        {
            key = string.IsNullOrWhiteSpace(raw) ? [] : Convert.FromBase64String(raw);
        }
        catch (FormatException)
        {
            key = [];
        }

        if (key.Length < 32) throw new InvalidOperationException("Forge:Authentication:Local:SigningKey must be at least 32 random bytes, base64-encoded (the installer writes one).");
        SigningKey = new SymmetricSecurityKey(key);
        TokenLifetime = TimeSpan.FromHours(Math.Clamp(configuration.GetValue("Forge:Authentication:Local:TokenHours", 12), 1, 24));
        MaxFailedAttempts = Math.Clamp(configuration.GetValue("Forge:Authentication:Local:MaxFailedAttempts", 5), 3, 20);
        LockoutMinutes = Math.Clamp(configuration.GetValue("Forge:Authentication:Local:LockoutMinutes", 15), 1, 1440);
        Assurance = configuration["Forge:Authentication:Local:Assurance"] ?? "urn:forge:assurance:high";
    }

    public SymmetricSecurityKey SigningKey { get; }

    public TimeSpan TokenLifetime { get; }

    public int MaxFailedAttempts { get; }

    public int LockoutMinutes { get; }

    public string Assurance { get; }

    public TokenValidationParameters Validation() => new()
    {
        ValidIssuer = Issuer,
        ValidAudience = Audience,
        IssuerSigningKey = SigningKey,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ClockSkew = TimeSpan.FromMinutes(1),
        NameClaimType = "sub",
    };

    /// <summary>Whether a request carries a Forge-issued token (otherwise it is for the OIDC provider).</summary>
    public static bool IsLocalToken(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            return new JsonWebToken(header[7..].Trim()).Issuer == Issuer;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}

public sealed record LocalSignInResult(bool Succeeded, string? Token, DateTimeOffset? ExpiresAt, bool MustChangePassword, string? Error);

/// <summary>
/// Forge's own accounts (owner decision 2026-11-03): usernames are the person's Forge subject; passwords are stored only as salted hashes;
/// five failed attempts lock the account for 15 minutes; an administrator's reset sets a temporary password the person must change. A
/// signed-in person gets a short-lived token Forge signs itself; nothing else about authorization changes.
/// </summary>
public sealed class LocalAccountService(IKernelUnitOfWorkFactory unitOfWork, LocalAuthenticationOptions options, TimeProvider time)
{
    public const int MinimumLength = 10;
    private static readonly PasswordHasher<string> Hasher = new();
    private const string Entity = "Person";

    public static string? PasswordProblem(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinimumLength) return string.Create(CultureInfo.InvariantCulture, $"Use at least {MinimumLength} characters.");
        if (password.Length > 200) return "Use at most 200 characters.";
        if (password.Distinct().Count() < 4) return "Use a less repetitive password.";
        return null;
    }

    public async Task<LocalSignInResult> SignInAsync(string? username, string? password, CancellationToken ct)
    {
        const string generic = "That username and password do not match, or the account is locked. Try again or ask your Forge security administrator.";
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password)) return new(false, null, null, false, generic);
        var subject = username.Trim();
        await using var uow = await unitOfWork.BeginAsync(ct);
        var store = uow.Store<ILocalCredentialStore>();
        var person = await uow.People.FindBySubjectAsync(subject, ct);
        var credential = person is null ? null : await store.FindAsync(person.IdpSubject, ct);
        var now = time.GetUtcNow();
        if (person is not { IsActive: true } || credential is null || credential.LockedUntil > now)
        {
            // Spend the same work as a real check so a missing account is not told apart by timing.
            Hasher.VerifyHashedPassword(subject, DummyHash, password);
            return new(false, null, null, false, generic);
        }

        if (Hasher.VerifyHashedPassword(person.IdpSubject, credential.PasswordHash, password) == PasswordVerificationResult.Failed)
        {
            var attempts = credential.FailedAttempts + 1;
            var locked = attempts >= options.MaxFailedAttempts ? now.AddMinutes(options.LockoutMinutes) : (DateTimeOffset?)null;
            await store.RecordFailureAsync(person.IdpSubject, locked is null ? attempts : 0, locked, ct);
            await AuditAsync(uow, person, locked is null ? "LocalSignInFailed" : "LocalAccountLocked", AuditOutcomes.Denied, person.IdpSubject, locked is null ? "Wrong password" : "Too many failed sign-ins", now, ct);
            await uow.CommitAsync(ct);
            return new(false, null, null, false, generic);
        }

        await store.RecordSignInAsync(person.IdpSubject, now, ct);
        await uow.CommitAsync(ct);
        var (token, expires) = Issue(person.IdpSubject, credential.MustChange, now);
        return new(true, token, expires, credential.MustChange, null);
    }

    /// <summary>A fresh token after the person re-enters their password, for actions that need recent sign-in.</summary>
    public Task<LocalSignInResult> ReauthenticateAsync(ClaimsPrincipal user, string? password, CancellationToken ct) =>
        SignInAsync(user?.FindFirstValue("sub"), password, ct);

    public async Task<LocalSignInResult> ChangePasswordAsync(ClaimsPrincipal user, string? current, string? replacement, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        var subject = user.FindFirstValue("sub");
        if (PasswordProblem(replacement) is { } problem) return new(false, null, null, true, problem);
        if (string.Equals(current, replacement, StringComparison.Ordinal)) return new(false, null, null, true, "Choose a password different from the current one.");
        var check = await SignInAsync(subject, current, ct);
        if (!check.Succeeded) return new(false, null, null, true, "The current password is not right.");
        await using var uow = await unitOfWork.BeginAsync(ct);
        var person = (await uow.People.FindBySubjectAsync(subject!, ct))!;
        var now = time.GetUtcNow();
        await uow.Store<ILocalCredentialStore>().UpsertAsync(new LocalCredential(person.IdpSubject, Hasher.HashPassword(person.IdpSubject, replacement!), false, 0, null, now, person.IdpSubject, null), ct);
        await AuditAsync(uow, person, "LocalPasswordChanged", AuditOutcomes.Succeeded, person.IdpSubject, "Changed by the person", now, ct);
        await uow.CommitAsync(ct);
        var (token, expires) = Issue(person.IdpSubject, false, now);
        return new(true, token, expires, false, null);
    }

    /// <summary>A security administrator sets a temporary password; the person must change it at their next sign-in. Never one's own.</summary>
    public async Task<string?> SetTemporaryPasswordAsync(ForgePrincipal admin, PersonId personId, string? password, string? reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(admin);
        if (!admin.HasPermission(Permissions.AccessManage)) return "Only a security administrator sets passwords.";
        if (string.IsNullOrWhiteSpace(reason)) return "Give a reason.";
        if (PasswordProblem(password) is { } problem) return problem;
        if (admin.PersonId == personId) return "Change your own password from your profile, not here.";
        await using var uow = await unitOfWork.BeginAsync(ct);
        var person = await uow.People.FindAsync(personId, ct);
        if (person is null) return "That person is not registered in Forge.";
        var now = time.GetUtcNow();
        var actor = "person:" + admin.PersonId.Value.ToString();
        await uow.Store<ILocalCredentialStore>().UpsertAsync(new LocalCredential(person.IdpSubject, Hasher.HashPassword(person.IdpSubject, password!), true, 0, null, now, actor, null), ct);
        await AuditAsync(uow, person, "LocalPasswordSet", AuditOutcomes.Succeeded, actor, reason!, now, ct);
        await uow.CommitAsync(ct);
        return null;
    }

    /// <summary>The first security administrator, from the setup wizard: does nothing once any active administrator exists.</summary>
    public async Task<bool> CreateFirstAdministratorAsync(AccessService access, string subject, string displayName, string password, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (!await access.BootstrapAdministratorAsync(subject, displayName, ct)) return false;
        await using var uow = await unitOfWork.BeginAsync(ct);
        var person = (await uow.People.FindBySubjectAsync(subject, ct))!;
        var now = time.GetUtcNow();
        await uow.Store<ILocalCredentialStore>().UpsertAsync(new LocalCredential(person.IdpSubject, Hasher.HashPassword(person.IdpSubject, password), false, 0, null, now, SystemActors.Bootstrap, null), ct);
        await AuditAsync(uow, person, "LocalPasswordSet", AuditOutcomes.Succeeded, SystemActors.Bootstrap, "First administrator from the setup wizard", now, ct);
        await uow.CommitAsync(ct);
        return true;
    }

    private (string Token, DateTimeOffset Expires) Issue(string subject, bool mustChange, DateTimeOffset now)
    {
        var expires = now.Add(mustChange ? TimeSpan.FromMinutes(15) : options.TokenLifetime);
        var claims = new Dictionary<string, object>
        {
            ["sub"] = subject,
            ["acr"] = options.Assurance,
            ["auth_time"] = now.ToUnixTimeSeconds(),
            ["amr"] = new[] { "pwd" },
            ["jti"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
        };
        if (mustChange) claims[LocalAuthenticationOptions.MustChangeClaim] = true;
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = LocalAuthenticationOptions.Issuer,
            Audience = LocalAuthenticationOptions.Audience,
            Claims = claims,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(options.SigningKey, SecurityAlgorithms.HmacSha256),
        });
        return (token, expires);
    }

    private static readonly string DummyHash = Hasher.HashPassword("forge", "not-a-real-password-" + Guid.Empty);

    private async Task AuditAsync(IKernelUnitOfWork uow, PersonRecord person, string action, string outcome, string actor, string reason, DateTimeOffset now, CancellationToken ct) =>
        await uow.Audit.AppendAsync(new AuditEntry
        {
            AuditId = ForgeIds.New(time),
            RecordedAt = now,
            Actor = actor,
            Action = action,
            Outcome = outcome,
            EntityType = Entity,
            EntityId = person.Id.ToString(),
            Reason = reason,
            Source = nameof(CommandSource.Api),
            CorrelationId = ForgeIds.New(time),
        }, ct);
}

/// <summary>A token issued with a temporary password only lets the person change it.</summary>
public sealed class PasswordChangeGate(RequestDelegate next)
{
    private static readonly string[] Allowed = ["/api/auth/local/", "/api/ui/config", "/api/kernel/me", "/api/setup/"];

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.User.HasClaim(c => c.Type == LocalAuthenticationOptions.MustChangeClaim) && context.Request.Path.StartsWithSegments("/api")
            && !Allowed.Any(a => context.Request.Path.Value!.StartsWith(a, StringComparison.OrdinalIgnoreCase)))
        {
            return Results.Problem(title: "Choose a new password", detail: "Your password was set by an administrator; choose your own before going on.",
                statusCode: StatusCodes.Status403Forbidden, extensions: new Dictionary<string, object?> { ["code"] = "password_change_required" }).ExecuteAsync(context);
        }

        return next(context);
    }
}
