using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Forge.Simulation.Tests.Engine;

/// <summary>Controlled internal documents through the real API: create, store a file, draft, submit, approve, release.</summary>
internal static class Documents
{
    private static int upload;

    public static async Task<Outcome> CreateAsync(SimFactory f, string number, string type, string title) =>
        await f.Api.Send(f.Plant.Engineer, "/api/engineering/documents", new { number, title, type, authority = "Internal", reason = "Controlled document" });

    /// <summary>Stores a file and drafts the revision; returns the draft outcome.</summary>
    public static async Task<Outcome> DraftAsync(SimFactory f, string number, string revision)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/engineering/document-files", UriKind.Relative))
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes($"{number} rev {revision} {Interlocked.Increment(ref upload).ToString(CultureInfo.InvariantCulture)}")),
        };
        request.Headers.Add("Idempotency-Key", $"sim-doc-{number}-{revision}-{upload.ToString(CultureInfo.InvariantCulture)}");
        request.Headers.Add("X-File-Name", "file.pdf");
        using var response = await f.Plant.Engineer.SendAsync(request);
        var sha = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("entityId").GetString()!;
        return await f.Api.Send(f.Plant.Engineer, $"/api/engineering/documents/{number}/revisions", new { revision, fileSha256 = sha, fileName = $"{number}_{revision}.pdf", reason = "Draft" });
    }

    /// <summary>Drafts, submits, approves and releases a revision; returns "200 200 200 200" when every step succeeded.</summary>
    public static async Task<string> ReleaseAsync(SimFactory f, string number, string revision)
    {
        var drafted = await DraftAsync(f, number, revision);
        var submitted = await f.Api.Send(f.Plant.Engineer, $"/api/engineering/documents/{number}/revisions/{revision}/submit", new { reason = "Submit" });
        var approved = await f.Api.Send(f.Plant.Approver, $"/api/engineering/documents/{number}/revisions/{revision}/approve", new { reason = "Approve" });
        var released = await f.Api.Send(f.Plant.Approver, $"/api/engineering/documents/{number}/revisions/{revision}/release", new { reason = "ECN" });
        return $"{drafted.Label} {submitted.Label} {approved.Label} {released.Label}";
    }

    /// <summary>The applicability state of a document for an order (Current, Missing, LegitimatelySuperseded, Mismatch, RequiresReview).</summary>
    public static async Task<string> StateAsync(SimFactory f, long order, string number)
    {
        var docs = await SimApi.Get(f.Planner, $"/api/planning/applicability?order={order}");
        return docs.GetProperty("documents").EnumerateArray().Where(d => d.GetProperty("documentNumber").GetString() == number)
            .Select(d => d.GetProperty("state").GetString()!).FirstOrDefault() ?? "not listed";
    }

    /// <summary>The order's operation readiness: "executable" or "blocked: constraint".</summary>
    public static async Task<string> OperationAsync(SimFactory f, long order, int sequence)
    {
        var op = (await SimApi.Get(f.Planner, $"/api/orders/{order}/readiness")).GetProperty("output").GetProperty("operations").EnumerateArray()
            .Single(o => o.GetProperty("sequence").GetInt32() == sequence);
        return op.GetProperty("executable").GetBoolean() ? "executable"
            : "blocked: " + (op.TryGetProperty("primaryConstraint", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : "?");
    }

    /// <summary>The order's Documentation readiness dimension outcome (Pass, Fail, Indeterminate) or "not evaluated".</summary>
    public static async Task<string> DocumentationOutcomeAsync(SimFactory f, long order, int sequence)
    {
        var op = (await SimApi.Get(f.Planner, $"/api/orders/{order}/readiness")).GetProperty("output").GetProperty("operations").EnumerateArray()
            .Single(o => o.GetProperty("sequence").GetInt32() == sequence);
        return op.GetProperty("dimensions").EnumerateArray().Where(d => d.GetProperty("name").GetString() == "Documentation")
            .Select(d => d.GetProperty("outcome").GetString()!).FirstOrDefault() ?? "not evaluated";
    }
}
