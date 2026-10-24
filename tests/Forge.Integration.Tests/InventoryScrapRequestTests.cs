using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Kernel.Postgres;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Integration.Tests;

/// <summary>
/// Sprint 10 §20 through the real API: planner-initiated scrap is a request, an independent decision (Inventory, or Quality for
/// quality-held material) and a separate execution; the ledger changes exactly once, and mistakes are corrected by a linked reversal.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class InventoryScrapRequestTests(PostgresFixture postgres)
{
    private const string Part = "COIL-AL";

    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<(HttpStatusCode Status, string Body)> Try(SlittingPlant.Plant plant, HttpClient client, string url, object body)
    {
        var response = await plant.Try(client, url, body);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static async Task<decimal> StockAsync(HttpClient client, string location, string status) =>
        (await Get(client, $"/api/inventory/stock?part={Part}")).EnumerateArray()
            .Where(b => b.GetProperty("locationCode").GetString() == location && b.GetProperty("status").GetString() == status)
            .Sum(b => b.GetProperty("quantity").GetDecimal());

    private static async Task<JsonElement> RequestAsync(HttpClient client, long id) =>
        (await Get(client, $"/api/inventory/scrap-requests?part={Part}")).EnumerateArray().Single(r => r.GetProperty("requestId").GetInt64() == id);

    private static async Task<int> ScrapEntriesAsync(HttpClient client, long requestId) =>
        (await Get(client, $"/api/inventory/transactions?part={Part}&take=500")).EnumerateArray()
            .Count(t => t.GetProperty("kind").GetString() == "Scrap"
                && (t.GetProperty("reason").GetString() ?? string.Empty).StartsWith($"Scrap request {requestId} ", StringComparison.Ordinal));

    private static async Task PropagateAsync(ForgeApp app)
    {
        var relay = app.Services.GetRequiredService<OutboxRelay>();
        while (await relay.RelayBatchAsync(100, CancellationToken.None) > 0)
        {
        }

        await app.Services.GetRequiredService<SubscriptionPump>().PumpOnceAsync(1_000_000, CancellationToken.None);
    }

    private static async Task<(SlittingPlant.Plant Plant, HttpClient Pam, HttpClient Ivy, HttpClient Ian, HttpClient Mike, HttpClient Quinn)> PlantAsync(ForgeApp app)
    {
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("pam", "Planner");
        await plant.Person("ivy", "InventoryController");
        await plant.Person("ian", "InventoryController");
        await plant.Person("mike", "MaterialHandler");
        await plant.Person("quinn", "QualityEngineer");
        return (plant, plant.As("pam"), plant.As("ivy"), plant.As("ian"), plant.As("mike"), plant.As("quinn"));
    }

    [Fact]
    public async Task A_planner_request_needs_an_independent_approval_and_one_separate_execution()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, pam, ivy, _, mike, _) = await PlantAsync(app);
        var before = await StockAsync(ivy, "STORES", "Released");

        // The preview shows what can be scrapped before anything is submitted.
        var preview = await plant.Ok(pam, "/api/inventory/scrap-preview", new { partNumber = Part, locationCode = "STORES", status = "Released", quantity = 12m });
        Assert.Equal(before, preview.GetProperty("eligibility").GetProperty("eligible").GetDecimal());

        var request = Id(await plant.Ok(pam, "/api/inventory/scrap-requests", new
        {
            partNumber = Part, locationCode = "STORES", status = "Released", quantity = 12m, reasonCode = "Damaged", condition = "Crushed coil edge", reason = "Forklift damage in aisle 3",
        }));
        Assert.Equal("PendingApproval", (await RequestAsync(ivy, request)).GetProperty("state").GetString());
        Assert.Equal(before, await StockAsync(ivy, "STORES", "Released"));

        // A planner cannot approve, nobody approves their own request, and an unapproved request cannot be executed.
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, pam, $"/api/inventory/scrap-requests/{request}/approve", new { reason = "Mine" })).Status);
        Assert.Contains("not_approved", (await Try(plant, mike, $"/api/inventory/scrap-requests/{request}/execute", new { reason = "Too early" })).Body, StringComparison.Ordinal);
        await plant.Ok(ivy, $"/api/inventory/scrap-requests/{request}/approve", new { reason = "Photos reviewed" });
        Assert.Equal(before, await StockAsync(ivy, "STORES", "Released"));

        // Execution posts exactly once, even when the same click is retried with the same idempotency key.
        var first = await mike.CommandAsync($"/api/inventory/scrap-requests/{request}/execute", new { reason = "Scrapped to the metal bin" }, "scrap-execute-1");
        var retry = await mike.CommandAsync($"/api/inventory/scrap-requests/{request}/execute", new { reason = "Scrapped to the metal bin" }, "scrap-execute-1");
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
        Assert.True(retry.IsSuccessStatusCode, await retry.Content.ReadAsStringAsync());
        Assert.Contains("not_approved", (await Try(plant, mike, $"/api/inventory/scrap-requests/{request}/execute", new { reason = "Second click" })).Body, StringComparison.Ordinal);
        Assert.Equal(before - 12m, await StockAsync(ivy, "STORES", "Released"));
        Assert.Equal(1, await ScrapEntriesAsync(ivy, request));
        var executed = await RequestAsync(ivy, request);
        Assert.Equal("Executed", executed.GetProperty("state").GetString());
        Assert.False(executed.GetProperty("transactionId").ValueKind == JsonValueKind.Null);

        // More than is eligible is refused with no partial or hidden ledger change.
        var tooMuch = await Try(plant, pam, "/api/inventory/scrap-requests", new { partNumber = Part, locationCode = "STORES", status = "Released", quantity = before * 2, reasonCode = "Obsolete", reason = "Superseded alloy" });
        Assert.Contains("insufficient_eligible", tooMuch.Body, StringComparison.Ordinal);
        Assert.Equal(before - 12m, await StockAsync(ivy, "STORES", "Released"));

        // A correction is a linked compensating entry, approved by someone else and executed separately; the stock returns on hold.
        var reversal = Id(await plant.Ok(pam, $"/api/inventory/scrap-requests/{request}/reversal", new { quantity = 5m, reason = "Five coils were only scuffed" }));
        Assert.Contains("reversal_exists", (await Try(plant, pam, $"/api/inventory/scrap-requests/{request}/reversal", new { quantity = 1m, reason = "Again" })).Body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, pam, $"/api/inventory/scrap-requests/{reversal}/approve", new { reason = "Mine" })).Status);
        await plant.Ok(ivy, $"/api/inventory/scrap-requests/{reversal}/approve", new { reason = "Re-inspected" });
        await plant.Ok(mike, $"/api/inventory/scrap-requests/{reversal}/execute", new { reason = "Returned to stores" });
        Assert.Equal(5m, await StockAsync(ivy, "STORES", "Hold"));
        Assert.Equal(request, (await RequestAsync(ivy, reversal)).GetProperty("correctsRequestId").GetInt64());

        // The posted scrap is history: the database refuses to change it.
        await using (var connection = new Npgsql.NpgsqlConnection(app.ConnectionString))
        {
            await connection.OpenAsync();
            await using var edit = new Npgsql.NpgsqlCommand("UPDATE inventory.scrap_request SET quantity = 1 WHERE request_id = $1", connection) { Parameters = { new() { Value = request } } };
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => edit.ExecuteNonQueryAsync());
        }

        // Planners and supervisors hear about posted scrap through the durable notification outbox.
        await PropagateAsync(app);
        var inbox = (await pam.GetFromJsonAsync<JsonElement[]>("/api/notifications/inbox"))!;
        Assert.Contains(inbox, a => a.GetProperty("message").GetString()!.Contains($"Scrap request {request} was posted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Quality_related_scrap_is_held_until_an_authorized_disposition()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, pam, ivy, _, mike, quinn) = await PlantAsync(app);
        var released = await StockAsync(ivy, "STORES", "Released");

        // A quality rejection puts the quantity on hold at once; Inventory cannot decide it, Quality must.
        var request = Id(await plant.Ok(pam, "/api/inventory/scrap-requests", new
        {
            partNumber = Part, locationCode = "STORES", status = "Released", quantity = 30m, reasonCode = "QualityReject", linkedNcr = "NCR-77", reason = "Surface pitting found at incoming audit",
        }));
        Assert.Equal(released - 30m, await StockAsync(ivy, "STORES", "Released"));
        Assert.Equal(30m, await StockAsync(ivy, "STORES", "Hold"));
        Assert.Equal("PendingDisposition", (await RequestAsync(ivy, request)).GetProperty("state").GetString());
        Assert.Contains("wrong_decider", (await Try(plant, ivy, $"/api/inventory/scrap-requests/{request}/approve", new { reason = "Not mine to decide" })).Body, StringComparison.Ordinal);
        Assert.Contains("not_approved", (await Try(plant, mike, $"/api/inventory/scrap-requests/{request}/execute", new { reason = "Too early" })).Body, StringComparison.Ordinal);
        Assert.Contains("quality_disposition_required", (await Try(plant, ivy, "/api/inventory/scrap-requests", new
        {
            partNumber = Part, locationCode = "STORES", status = "Hold", quantity = 1m, reasonCode = "QualityReject", executeDirectly = true, reason = "Shortcut",
        })).Body, StringComparison.Ordinal);

        await plant.Ok(quinn, $"/api/inventory/scrap-requests/{request}/disposition", new { scrap = true, note = "MRB: not reworkable", reason = "MRB decision 2026-10-24" });
        await plant.Ok(mike, $"/api/inventory/scrap-requests/{request}/execute", new { reason = "Scrapped" });
        Assert.Equal(0m, await StockAsync(ivy, "STORES", "Hold"));
        Assert.Equal(released - 30m, await StockAsync(ivy, "STORES", "Released"));

        // Keeping the material rejects the request and releases the hold it placed; nothing is scrapped.
        var kept = Id(await plant.Ok(pam, "/api/inventory/scrap-requests", new
        {
            partNumber = Part, locationCode = "STORES", status = "Released", quantity = 20m, reasonCode = "QualityReject", reason = "Suspected contamination",
        }));
        Assert.Equal(20m, await StockAsync(ivy, "STORES", "Hold"));
        await plant.Ok(quinn, $"/api/inventory/scrap-requests/{kept}/disposition", new { scrap = false, reason = "Lab result clean" });
        Assert.Equal(0m, await StockAsync(ivy, "STORES", "Hold"));
        Assert.Equal(released - 30m, await StockAsync(ivy, "STORES", "Released"));
        Assert.Equal("Rejected", (await RequestAsync(ivy, kept)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Concurrent_requests_never_claim_the_same_units()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, pam, ivy, _, mike, _) = await PlantAsync(app);
        await plant.Ok(ivy, "/api/inventory/locations", new { code = "CAGE", siteCode = "PLANT1", name = "Scrap staging cage", kind = "Stores" });
        await plant.Ok(mike, "/api/inventory/transfers", new { partNumber = Part, quantity = 10m, fromLocation = "STORES", toLocation = "CAGE" });

        var attempts = await Task.WhenAll(
            Try(plant, pam, "/api/inventory/scrap-requests", new { partNumber = Part, locationCode = "CAGE", status = "Released", quantity = 8m, reasonCode = "Damaged", reason = "First report" }),
            Try(plant, ivy, "/api/inventory/scrap-requests", new { partNumber = Part, locationCode = "CAGE", status = "Released", quantity = 8m, reasonCode = "Damaged", reason = "Second report" }));
        Assert.Single(attempts, a => a.Status == HttpStatusCode.OK);
        Assert.Single(attempts, a => a.Body.Contains("insufficient_eligible", StringComparison.Ordinal));
        Assert.Equal(10m, await StockAsync(ivy, "CAGE", "Released"));
    }

    [Fact]
    public async Task Direct_scrap_needs_an_independently_approved_site_rule()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, pam, ivy, ian, _, _) = await PlantAsync(app);
        var released = await StockAsync(ivy, "STORES", "Released");
        var direct = new { partNumber = Part, locationCode = "STORES", status = "Released", quantity = 3m, reasonCode = "Damaged", executeDirectly = true, reason = "Dropped during kitting" };

        Assert.Contains("direct_scrap_not_permitted", (await Try(plant, ivy, "/api/inventory/scrap-requests", direct)).Body, StringComparison.Ordinal);
        var policy = Id(await plant.Ok(ivy, "/api/inventory/scrap-policies", new { roleName = "InventoryController", reasonCode = "Damaged", maxQuantity = 5m, reason = "Small handling damage" }));
        Assert.Contains("separation_of_duties", (await Try(plant, ivy, $"/api/inventory/scrap-policies/{policy}/decide", new { approve = true, reason = "Mine" })).Body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, pam, $"/api/inventory/scrap-policies/{policy}/decide", new { approve = true, reason = "Planner" })).Status);
        await plant.Ok(ian, $"/api/inventory/scrap-policies/{policy}/decide", new { approve = true, reason = "Plant manager agreed" });

        var scrapped = Id(await plant.Ok(ivy, "/api/inventory/scrap-requests", direct));
        var row = await RequestAsync(ivy, scrapped);
        Assert.Equal("Executed", row.GetProperty("state").GetString());
        Assert.Equal(policy, row.GetProperty("policyId").GetInt64());
        Assert.Equal(released - 3m, await StockAsync(ivy, "STORES", "Released"));

        // Outside the rule (too many, or a planner without transaction rights) it is refused, and nothing moves.
        Assert.Contains("direct_scrap_not_permitted", (await Try(plant, ivy, "/api/inventory/scrap-requests", direct with { quantity = 6m })).Body, StringComparison.Ordinal);
        Assert.Contains("direct_scrap_not_permitted", (await Try(plant, pam, "/api/inventory/scrap-requests", direct)).Body, StringComparison.Ordinal);
        Assert.Equal(released - 3m, await StockAsync(ivy, "STORES", "Released"));
    }
}
