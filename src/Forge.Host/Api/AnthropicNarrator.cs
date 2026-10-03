using System.Net.Http.Json;
using System.Text.Json;
using Forge.Planning.Analyst;

namespace Forge.Host.Api;

/// <summary>Settings for the optional generative AI provider (spec §61). Off unless Provider is "anthropic" and a key is set.</summary>
public sealed class AnalystNarratorOptions
{
    public string Provider { get; set; } = "none";

    public string? ApiKey { get; set; }

    public string Model { get; set; } = "claude-haiku-4-5-20251001";

    public Uri Endpoint { get; set; } = new("https://api.anthropic.com/v1/messages");

    public int TimeoutSeconds { get; set; } = 20;
}

/// <summary>
/// Restates a finished Forge Analyst answer in plain prose through the Anthropic Messages API. It is
/// sent only the question and the answer's facts, which Forge already computed and filtered for the
/// reader; it never receives database access, and its text is shown labeled, next to the facts.
/// Any failure leaves the answer without a narrative.
/// </summary>
public sealed partial class AnthropicNarrator(HttpClient http, AnalystNarratorOptions options, ILogger<AnthropicNarrator> logger) : IAnalystNarrator
{
    internal const string Instructions =
        "You restate answers from JIRSN Forge, a manufacturing operations system, for the person who asked. " +
        "Use only the facts given in the JSON. Do not add numbers, names, causes, predictions or recommendations that are not in the facts. " +
        "Do not change any figure. If the facts do not answer the question, say so plainly. Write 2 to 5 short sentences of plain prose, no lists, no headings.";

    public string Name => "anthropic:" + options.Model;

    public bool Enabled => true;

    public async Task<string?> NarrateAsync(string question, string factsJson, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint)
            {
                Content = JsonContent.Create(new
                {
                    model = options.Model,
                    max_tokens = 400,
                    system = Instructions,
                    messages = new[] { new { role = "user", content = $"Question: {question}\n\nForge's answer (facts, JSON):\n{factsJson}" } },
                }),
            };
            request.Headers.Add("x-api-key", options.ApiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            using var response = await http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                LogFailed(logger, (int)response.StatusCode);
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            return doc.RootElement.GetProperty("content").EnumerateArray()
                .Where(c => c.GetProperty("type").GetString() == "text")
                .Select(c => c.GetProperty("text").GetString())
                .FirstOrDefault();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            LogError(logger, e.GetType().Name);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Forge Analyst narration failed with HTTP {Status}; the answer is shown without it.")]
    private static partial void LogFailed(ILogger logger, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Forge Analyst narration failed ({Error}); the answer is shown without it.")]
    private static partial void LogError(ILogger logger, string error);
}
