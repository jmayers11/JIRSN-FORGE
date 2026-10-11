using System.Globalization;
using System.Security.Claims;

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

        if (acr is not null && acceptedAcr.Contains(acr, StringComparer.Ordinal) && fresh)
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer error=\"insufficient_user_authentication\", error_description=\"fresh_step_up_required\"";
        await Results.Problem(
            title: "Fresh step-up authentication required",
            detail: "Reauthenticate with the identity provider using an accepted assurance level, then retry this action.",
            statusCode: StatusCodes.Status401Unauthorized,
            extensions: new Dictionary<string, object?> { ["code"] = "step_up_required" })
            .ExecuteAsync(context);
    }
}
