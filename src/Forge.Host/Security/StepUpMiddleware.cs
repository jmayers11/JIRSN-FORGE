using System.Globalization;
using System.Security.Claims;
using Forge.Kernel.Holds;

namespace Forge.Host.Api;

/// <summary>Marks an endpoint whose policy requires recent provider-authenticated step-up.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class StepUpRequiredAttribute : Attribute;

public sealed class StepUpMiddleware(RequestDelegate next, IConfiguration configuration, TimeProvider time)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<StepUpRequiredAttribute>() is null)
        {
            await next(context);
            return;
        }

        if (TryGetEvidence(context, configuration, time, Guid.Empty) is not null)
        {
            await next(context);
            return;
        }
        await Challenge(context).ExecuteAsync(context);
    }

    /// <summary>Constructs assurance evidence from validated provider claims, using the same freshness policy as marked endpoints.</summary>
    public static HoldReleaseAuthentication? TryGetEvidence(HttpContext context, IConfiguration configuration, TimeProvider time, Guid personId)
    {
        var acceptedAcr = configuration.GetSection("Forge:Authentication:StepUp:AcceptedAcrValues")
            .Get<string[]>() ?? ["urn:forge:assurance:high"];
        var acr = context.User.FindFirstValue("acr");
        var authenticationTime = context.User.FindFirstValue("auth_time");
        var maxAge = configuration.GetValue("Forge:Authentication:StepUp:MaxAgeSeconds", 300);
        var now = time.GetUtcNow();
        var fresh = long.TryParse(authenticationTime, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            && seconds is >= 0 and <= 253402300799
            && DateTimeOffset.FromUnixTimeSeconds(seconds) <= now
            && now - DateTimeOffset.FromUnixTimeSeconds(seconds) <= TimeSpan.FromSeconds(Math.Clamp(maxAge, 1, 3600));

        var subject = context.User.FindFirstValue("sub") ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var issuer = context.User.FindFirstValue("iss");
        if (context.User.Identity?.IsAuthenticated != true || acr is null || !acceptedAcr.Contains(acr, StringComparer.Ordinal) || !fresh
            || string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(issuer)) return null;
        var authenticatedAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return new HoldReleaseAuthentication(personId, subject, issuer, acr, context.User.FindAll("amr").Select(c => c.Value).ToArray(),
            authenticatedAt, authenticatedAt.AddSeconds(Math.Clamp(maxAge, 1, 3600)));
    }

    public static IResult Challenge(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer error=\"insufficient_user_authentication\", error_description=\"fresh_step_up_required\"";
        return Results.Problem(
            title: "Fresh step-up authentication required",
            detail: "Reauthenticate with the identity provider using an accepted assurance level, then retry this action.",
            statusCode: StatusCodes.Status401Unauthorized,
            extensions: new Dictionary<string, object?> { ["code"] = "step_up_required" });
    }
}
