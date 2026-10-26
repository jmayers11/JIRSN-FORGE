using System.Net.Http.Json;
using Forge.Host.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

namespace Forge.Integration.Tests;

/// <summary>The real host against a real database. Background relays are off so tests drive them.</summary>
public sealed class ForgeApp(
    string connectionString,
    Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? services = null,
    Action<IWebHostBuilder>? configureBuilder = null) : WebApplicationFactory<Program>
{
    public const string AdminSubject = "admin-sub";

    public string ConnectionString => connectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Forge", connectionString);
        builder.UseSetting("Forge:Authentication:Mode", "Development");
        builder.UseSetting("Forge:Database:MigrateOnStartup", "true");
        builder.UseSetting("Forge:BackgroundServices:Enabled", "false");
        builder.UseSetting("Forge:Bootstrap:AdminSubject", AdminSubject);
        builder.UseSetting("Forge:Bootstrap:AdminDisplayName", "Test Administrator");
        // Fixtures exercise multi-site rules; production runs one site per database (SiteLimitTests covers that default).
        builder.UseSetting("Forge:Sites:MaxSites", "0");
        builder.ConfigureLogging(logging => logging.ClearProviders());
        configureBuilder?.Invoke(builder);
        if (services is not null)
        {
            builder.ConfigureTestServices(services);
        }
    }

    public HttpClient As(string subject)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(DevelopmentAuthenticationHandler.HeaderName, subject);
        return client;
    }
}

internal static class HttpExtensions
{
    public static Task<HttpResponseMessage> CommandAsync(this HttpClient client, string url, object body, string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return client.SendAsync(request);
    }
}
