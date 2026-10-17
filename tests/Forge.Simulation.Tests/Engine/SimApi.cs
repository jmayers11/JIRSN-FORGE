using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Integration.Tests;

namespace Forge.Simulation.Tests.Engine;

/// <summary>What the real API did with a command: the status, the problem code (if refused) and the body.</summary>
public sealed record Outcome(HttpStatusCode Status, string Body)
{
    public bool Succeeded => (int)Status is >= 200 and < 300;

    public JsonElement Json => JsonDocument.Parse(Body).RootElement;

    public string? EntityId => Succeeded && Json.TryGetProperty("entityId", out var id) ? id.GetString() : null;

    public long Id => long.Parse(EntityId ?? throw new InvalidOperationException("No entity id: " + Body), CultureInfo.InvariantCulture);

    /// <summary>A short label for the expected-versus-actual table: "200", or "422 code", or "403".</summary>
    public string Label
    {
        get
        {
            var code = ((int)Status).ToString(CultureInfo.InvariantCulture);
            if (Succeeded || string.IsNullOrWhiteSpace(Body))
            {
                return code;
            }

            try
            {
                var problem = Json;
                if (problem.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    return code + " " + c.GetString();
                }
            }
            catch (JsonException)
            {
            }

            return code;
        }
    }
}

/// <summary>
/// Sends commands to the real host exactly as a client would, with an idempotency key per command.
/// It never asserts: refusals are outcomes the scenario compares with its expectations.
/// </summary>
public sealed class SimApi(ForgeApp app, string keyPrefix)
{
    private int key;

    public HttpClient As(string subject) => app.As(subject);

    public string NextKey() => keyPrefix + "-" + Interlocked.Increment(ref key).ToString(CultureInfo.InvariantCulture);

    public async Task<Outcome> Send(HttpClient client, string url, object body, string? idempotencyKey = null)
    {
        using var response = await client.CommandAsync(url, body, idempotencyKey ?? NextKey());
        return new Outcome(response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>A command whose response never reaches the client (a dropped connection after the server received it).</summary>
    public static async Task SendAndLoseResponse(HttpClient client, string url, object body, string idempotencyKey)
    {
        using var response = await client.CommandAsync(url, body, idempotencyKey);
        // The response is discarded unread: the client cannot know whether the command was applied.
    }

    public static async Task<JsonElement> Get(HttpClient client, string url)
    {
        using var response = await client.GetAsync(new Uri(url, UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GET {url} -> {(int)response.StatusCode}: {text}");
        }

        return JsonDocument.Parse(text).RootElement;
    }

    public static Task<HttpResponseMessage> PostJson(HttpClient client, string url, object body) =>
        client.PostAsJsonAsync(new Uri(url, UriKind.Relative), body);
}
