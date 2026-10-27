using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Forge.Integration.Tests;

/// <summary>
/// Codex's operational evidence backend (2026-10-27) on PostgreSQL through the real API: every included source shares one read
/// snapshot, a recorded NCR and stoppage link to their exact order, and a reader without a source's permission gets that source marked
/// NotAuthorized instead of its signals.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OperationalEvidenceApiTests(PostgresFixture postgres)
{
    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task Evidence_is_one_snapshot_with_exact_order_links_and_unauthorized_sources_withheld()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var e = plant.Engineer;
        await plant.Person("qe", "QualityEngineer");
        await plant.Person("kiosk", "ShopFloorTerminal");
        var order = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "ROLL-100", quantity = 20m, dueDate = DateTime.UtcNow.AddDays(10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Customer order" });
        await plant.Ok(plant.As("qe"), "/api/quality/ncrs", new { partNumber = "ROLL-100", orderNumber = order, sequence = 10, quantity = 2m, defect = "Edge wave", description = "Two rolls wavy" });
        await plant.Ok(e, "/api/execution/reason-codes", new { code = "OE-JAM", kind = "Downtime", category = "Unplanned", description = "Web jam" });
        await plant.Ok(e, "/api/execution/stoppages", new { machineCode = "SLITTER-A", category = "Unplanned", reasonCode = "OE-JAM", orderNumber = order, sequence = 10, note = "Jam", reason = "Record the jam" });

        var url = $"/api/planning/operational-evidence?orderNumber={order}";
        var evidence = await Get(planner, url);
        Assert.True(evidence.GetProperty("sourcesShareReadSnapshot").GetBoolean());
        var snapshot = evidence.GetProperty("read").GetProperty("snapshotId").GetGuid();
        Assert.All(evidence.GetProperty("sources").EnumerateArray().Where(s => s.GetProperty("status").GetString() == "Included"),
            s => Assert.Equal(snapshot, s.GetProperty("snapshotId").GetGuid()));
        var signals = evidence.GetProperty("signals").EnumerateArray().ToList();
        bool LinksOrder(JsonElement signal) => signal.GetProperty("links").EnumerateArray().Any(l => l.GetProperty("targetId").GetString()!.StartsWith(order.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
        Assert.Contains(signals, s => s.GetProperty("category").GetString() == "QualityNcr" && LinksOrder(s));
        Assert.Contains(signals, s => s.GetProperty("category").GetString() == "ProductionStoppage" && LinksOrder(s));
        // No signal is attributed to a cause without a root-cause record.
        Assert.All(signals.Where(s => s.GetProperty("category").GetString() != "RootCauseAssessment"),
            s => Assert.Equal(JsonValueKind.Null, s.GetProperty("causeIdentifier").ValueKind));
        Assert.All(evidence.GetProperty("associations").EnumerateArray(), a => Assert.False(a.GetProperty("commonCauseProven").GetBoolean()));

        // The terminal may read readiness but not quality: the NCR source is withheld and said to be.
        var terminal = await Get(plant.As("kiosk"), url);
        Assert.Equal("NotAuthorized", terminal.GetProperty("sources").EnumerateArray().Single(s => s.GetProperty("code").GetString() == "quality").GetProperty("status").GetString());
        Assert.DoesNotContain(terminal.GetProperty("signals").EnumerateArray(), s => s.GetProperty("category").GetString() == "QualityNcr");

        // Nobody without readiness access reads it, and out-of-range windows are refused.
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.As("sarah").GetAsync(new Uri(url, UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await planner.GetAsync(new Uri(url + "&lookbackDays=400", UriKind.Relative))).StatusCode);
    }
}
