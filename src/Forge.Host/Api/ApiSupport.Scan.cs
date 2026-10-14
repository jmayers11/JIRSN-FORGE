using System.Globalization;
using System.Security.Claims;
using Forge.Kernel.Commands;

namespace Forge.Host.Api;

internal static partial class ApiSupport
{
    public const string ScanSessionHeader = "X-Forge-Scan-Session";
    public const string ScanVersionHeader = "X-Forge-Scan-Version";

    public static (CommandSessionReference? Reference, IResult? Error) ScanReference(HttpContext http)
    {
        var id = http.Request.Headers[ScanSessionHeader].ToString();
        var version = http.Request.Headers[ScanVersionHeader].ToString();
        if (id.Length == 0 && version.Length == 0) return (null, null);
        if (id.Length != 36 || !Guid.TryParseExact(id, "D", out var parsed) || parsed == Guid.Empty
            || !int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
            return (null, Results.Problem(title: "Invalid scan session reference", statusCode: StatusCodes.Status400BadRequest,
                detail: "Supply the exact server session ID and positive current version together."));
        return (new(parsed, number), null);
    }

    public static CommandAuthenticationEvidence? AuthenticationEvidence(HttpContext http)
    {
        if (http.User.Identity?.IsAuthenticated != true) return null;
        var subject = http.User.FindFirstValue("sub") ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var issuer = http.User.FindFirstValue("iss");
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(issuer)) return null;
        var configuration = http.RequestServices.GetRequiredService<IConfiguration>();
        var time = http.RequestServices.GetRequiredService<TimeProvider>();
        var sidClaim = configuration["Forge:Authentication:ScanSession:ProviderSessionClaim"] ?? "sid";
        var stationClaim = configuration["Forge:Authentication:ScanSession:WorkstationClaim"] ?? "forge_workstation";
        static string? Bounded(string? value, int length) => string.IsNullOrWhiteSpace(value) || value.Length > length || value.Any(char.IsControl) ? null : value;
        static DateTimeOffset? Epoch(string? value) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            && seconds is >= 0 and <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
        var stepUp = StepUpMiddleware.TryGetEvidence(http, configuration, time, Guid.Empty);
        var tokenExpiry = Epoch(http.User.FindFirstValue("exp"));
        var until = stepUp?.StepUpExpiresAt;
        if (tokenExpiry is { } expiry && (until is null || expiry < until)) until = expiry;
        return new(subject, issuer, Bounded(http.User.FindFirstValue(sidClaim), 200),
            Bounded(http.User.FindFirstValue(stationClaim), 100), stepUp is null ? "Authenticated" : "StepUp",
            stepUp?.AuthenticatedAt ?? Epoch(http.User.FindFirstValue("auth_time")), until);
    }
}
