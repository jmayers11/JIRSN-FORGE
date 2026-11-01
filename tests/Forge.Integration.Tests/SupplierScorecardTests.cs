using System.Globalization;
using System.Net;
using System.Text.Json;
using Forge.Resources.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Forge.Integration.Tests;

/// <summary>
/// Supplier scorecards through the real API (owner decisions 2026-11-01): receipts against promised dates, nonconformances on received
/// lots as PPM and percent good, past-due lines, and an A/B/C grade; the company sets what counts as on time and where grades fall, and
/// a grade never puts a supplier on hold.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SupplierScorecardTests(PostgresFixture postgres)
{
    private static string Day(int fromToday) => DateTime.UtcNow.AddDays(fromToday).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);


    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task Scorecards_measure_delivery_and_received_quality_and_the_company_sets_on_time_and_grades()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("bob", "Buyer");
        await plant.Person("pat", "Buyer", "PurchasingManager");
        await plant.Person("qe", "QualityEngineer");
        var (bob, pat, stores, qe) = (plant.As("bob"), plant.As("pat"), plant.As("stores"), plant.As("qe"));
        await plant.Ok(plant.Engineer, "/api/engineering/items", new { partNumber = "BAR-LOT", description = "Bar stock", unitOfMeasure = "KG", makeBuy = "Buy", leadTimeDays = 7, lotTracked = true });
        await plant.Ok(plant.Engineer, "/api/engineering/items/BAR-LOT/revisions", new { revision = "A" });
        await plant.Ok(plant.Approver, "/api/engineering/items/BAR-LOT/revisions/A/release", new { reason = "Approved bar stock" });
        await plant.Ok(bob, "/api/procurement/suppliers", new { code = "ALCOA", name = "Alcoa" });
        var po = Id(await plant.Ok(bob, "/api/procurement/purchase-orders", new
        {
            supplier = "ALCOA",
            lines = new[] { new { partNumber = "BAR-LOT", quantity = 100m, promisedDate = Day(7) }, new { partNumber = "BAR-LOT", quantity = 100m, promisedDate = Day(7) } },
        }));
        await plant.Ok(pat, $"/api/procurement/purchase-orders/{po}/approve", new { reason = "Budget approved" });
        var open = Id(await plant.Ok(bob, "/api/procurement/purchase-orders", new { supplier = "ALCOA", lines = new[] { new { partNumber = "BAR-LOT", quantity = 50m, promisedDate = Day(7) } } }));
        await plant.Ok(pat, $"/api/procurement/purchase-orders/{open}/approve", new { reason = "Budget approved" });

        DateOnly LocalDay(int fromToday) => SiteTime.DateAt(DateTimeOffset.UtcNow, app.Services.GetRequiredService<ISiteClock>().HomeTimeZone).AddDays(fromToday);

        // History: line 1 was promised three days ago, and the open order's line two days ago (promises cannot be entered in the past).
        await using (var db = new NpgsqlConnection(app.ConnectionString))
        {
            await db.OpenAsync();
            foreach (var (number, line, day) in new[] { (po, 1, LocalDay(-3)), (open, 1, LocalDay(-2)) })
            {
                await using var cmd = new NpgsqlCommand("UPDATE procurement.purchase_order_line SET promised_date = $1 WHERE po_number = $2 AND line_number = $3", db);
                cmd.Parameters.AddWithValue(day);
                cmd.Parameters.AddWithValue(number);
                cmd.Parameters.AddWithValue(line);
                Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
            }
        }

        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "BAR-LOT", quantity = 100m, locationCode = "STORES", lotNumber = "L-1", revision = "A", poNumber = po, poLine = 1 });
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "BAR-LOT", quantity = 100m, locationCode = "STORES", lotNumber = "L-2", revision = "A", poNumber = po, poLine = 2 });
        await plant.Ok(qe, "/api/quality/ncrs", new { partNumber = "BAR-LOT", lotNumber = "L-1", quantity = 5m, defect = "Edge crack", description = "Cracked edge on the outer wraps" });

        var card = Assert.Single((await Get(bob, "/api/procurement/scorecard")).GetProperty("suppliers").EnumerateArray());
        Assert.Equal(("ALCOA", 2, 1, 50m), (card.GetProperty("supplierCode").GetString(), card.GetProperty("receipts").GetInt32(), card.GetProperty("onTimeReceipts").GetInt32(), card.GetProperty("onTimePercent").GetDecimal()));
        Assert.Equal((3m, 200m, 5m, 25000m, 97.5m, 1, 1), (card.GetProperty("averageDaysLate").GetDecimal(), card.GetProperty("quantityReceived").GetDecimal(), card.GetProperty("quantityRejected").GetDecimal(),
            card.GetProperty("ppm").GetDecimal(), card.GetProperty("qualityPercent").GetDecimal(), card.GetProperty("nonconformances").GetInt32(), card.GetProperty("pastDueLines").GetInt32()));
        Assert.Equal("C", card.GetProperty("grade").GetString());
        Assert.Equal(3, Assert.Single(card.GetProperty("late").EnumerateArray()).GetProperty("daysLate").GetInt32());

        // Only a purchasing manager sets the policy. Three days' grace and a lower B quality bar make this a B; the grace applies to past due too.
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(bob, "/api/procurement/scorecard/policy", new { lateToleranceDays = 3, earlyLimitDays = (int?)null, gradeAOnTimePercent = 95m, gradeAQualityPercent = 99.5m, gradeBOnTimePercent = 85m, gradeBQualityPercent = 97m, periodDays = 90, reason = "x" })).StatusCode);
        var inverted = await plant.Try(pat, "/api/procurement/scorecard/policy", new { lateToleranceDays = 3, earlyLimitDays = (int?)null, gradeAOnTimePercent = 80m, gradeAQualityPercent = 99.5m, gradeBOnTimePercent = 85m, gradeBQualityPercent = 97m, periodDays = 90, reason = "x" });
        Assert.Contains("invalid_grades", await inverted.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await plant.Ok(pat, "/api/procurement/scorecard/policy", new { lateToleranceDays = 3, earlyLimitDays = (int?)null, gradeAOnTimePercent = 95m, gradeAQualityPercent = 99.5m, gradeBOnTimePercent = 85m, gradeBQualityPercent = 97m, periodDays = 90, reason = "Agreed with suppliers: three days' grace" });
        card = Assert.Single((await Get(bob, "/api/procurement/scorecard")).GetProperty("suppliers").EnumerateArray());
        Assert.Equal((2, 100m, 0, "B"), (card.GetProperty("onTimeReceipts").GetInt32(), card.GetProperty("onTimePercent").GetDecimal(), card.GetProperty("pastDueLines").GetInt32(), card.GetProperty("grade").GetString()));

        // Limiting how early counts: line 2 arrived seven days early.
        await plant.Ok(pat, "/api/procurement/scorecard/policy", new { lateToleranceDays = 3, earlyLimitDays = 5, gradeAOnTimePercent = 95m, gradeAQualityPercent = 99.5m, gradeBOnTimePercent = 85m, gradeBQualityPercent = 97m, periodDays = 90, reason = "Dock space" });
        var report = await Get(bob, "/api/procurement/scorecard");
        card = Assert.Single(report.GetProperty("suppliers").EnumerateArray());
        Assert.Equal((1, -7), (card.GetProperty("onTimeReceipts").GetInt32(), Assert.Single(card.GetProperty("late").EnumerateArray()).GetProperty("daysLate").GetInt32()));
        Assert.Equal(5, report.GetProperty("policy").GetProperty("earlyLimitDays").GetInt32());

        // A grade holds nothing: the supplier is still active.
        var supplier = (await Get(bob, "/api/procurement/suppliers")).EnumerateArray().Single(s => s.GetProperty("code").GetString() == "ALCOA");
        Assert.Equal("Active", supplier.GetProperty("status").GetString());
    }
}
