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

/// <summary>One command as sent and answered, kept as evidence for the defect register.</summary>
public sealed record CallRecord(int Index, string Subject, string Url, string Key, int Status, string Label, string Body);

/// <summary>
/// Sends commands to the real host exactly as a client would, with an idempotency key per command.
/// It never asserts: refusals are outcomes the scenario compares with its expectations. Every command and its answer
/// is logged (calls.jsonl) as evidence.
/// </summary>
public sealed class SimApi(ForgeApp app, string keyPrefix)
{
    private readonly List<CallRecord> calls = [];
    private int key;

    public IReadOnlyList<CallRecord> Calls
    {
        get
        {
            lock (calls)
            {
                return [.. calls];
            }
        }
    }

    public HttpClient As(string subject) => app.As(subject);

    public string NextKey() => keyPrefix + "-" + Interlocked.Increment(ref key).ToString(CultureInfo.InvariantCulture);

    public async Task<Outcome> Send(HttpClient client, string url, object body, string? idempotencyKey = null)
    {
        var commandKey = idempotencyKey ?? NextKey();
        using var response = await client.CommandAsync(url, body, commandKey);
        var outcome = new Outcome(response.StatusCode, await response.Content.ReadAsStringAsync());
        lock (calls)
        {
            calls.Add(new CallRecord(calls.Count, Subject(client), url, commandKey, (int)outcome.Status, outcome.Label,
                outcome.Body.Length > 4000 ? outcome.Body[..4000] + "…" : outcome.Body));
        }

        return outcome;
    }

    private static string Subject(HttpClient client) =>
        client.DefaultRequestHeaders.Where(h => h.Key.Contains("Subject", StringComparison.OrdinalIgnoreCase)).SelectMany(h => h.Value).FirstOrDefault() ?? "?";

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
