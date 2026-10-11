using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Forge.Host.Security;

/// <summary>
/// Local development only: trusts the X-Forge-Dev-Subject header as the identity-provider subject.
/// The host refuses to start with this scheme outside the Development environment.
/// </summary>
public sealed class DevelopmentAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration,
    TimeProvider time)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ForgeDevelopment";
    public const string HeaderName = "X-Forge-Dev-Subject";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var subject) || string.IsNullOrWhiteSpace(subject))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var authTime = configuration["Forge:Authentication:DevelopmentAuthTime"];
        if (!long.TryParse(authTime, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            seconds = time.GetUtcNow().ToUnixTimeSeconds();
        }

        var acr = configuration["Forge:Authentication:DevelopmentAssurance"] ?? "urn:forge:assurance:high";
        var claims = new List<Claim>
        {
            new("sub", subject.ToString()),
            new("iss", "urn:forge:development"),
            new("acr", acr),
            new("auth_time", seconds.ToString(CultureInfo.InvariantCulture)),
        };
        if (!string.IsNullOrWhiteSpace(configuration["Forge:Authentication:DevelopmentScopes"]))
        {
            claims.Add(new Claim("scope", configuration["Forge:Authentication:DevelopmentScopes"]!));
        }

        var identity = new ClaimsIdentity(
            claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
