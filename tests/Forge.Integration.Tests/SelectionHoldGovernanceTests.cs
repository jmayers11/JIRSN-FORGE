using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class SelectionHoldGovernanceTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Selection_excludes_direct_and_descendant_issue_holds_preserves_independent_material_and_restores_after_release()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("selection-quality", "QualityEngineer");
        var quality = plant.As("selection-quality");
        var stores = plant.As("stores");
        foreach (var (part, makeBuy) in new[] { ("SELECT-RAW", "Buy"), ("SELECT-OUT", "Make") })
        {
            await plant.Ok(plant.Engineer, "/api/engineering/items", new { partNumber = part, description = part, unitOfMeasure = "EA", makeBuy, leadTimeDays = 1, lotTracked = true });
            await plant.Ok(plant.Engineer, $"/api/engineering/items/{part}/revisions", new { revision = "A" });
        }
        await plant.Ok(plant.Engineer, "/api/engineering/items/SELECT-OUT/revisions/A/bom", new { lineNumber = 1, componentPartNumber = "SELECT-RAW", quantityPer = 1m, scrapPercent = 0m });
        await plant.Ok(plant.Engineer, "/api/engineering/items/SELECT-OUT/revisions/A/routing", new { sequence = 10, description = "Slit selection material", workCenterCode = "SLIT-WC", activityCode = "RUN_SLITTER", eligibleMachines = new[] { "SLITTER-A" }, setupMinutes = 0m, runMinutesPerUnit = 1m, crewSize = 1 });
        foreach (var part in new[] { "SELECT-RAW", "SELECT-OUT" })
            await plant.Ok(plant.Approver, $"/api/engineering/items/{part}/revisions/A/release", new { reason = "Release factual selection fixture" });
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "SELECT-RAW", quantity = 100m, locationCode = "STORES", lotNumber = "SELECT-ROOT", revision = "A", reason = "Receive master roll" });
        await plant.Ok(stores, "/api/inventory/splits", new { partNumber = "SELECT-RAW", lotNumber = "SELECT-ROOT", locationCode = "STORES", status = "Released", results = new[] { new { lotNumber = "SELECT-CHILD", quantity = 50m } }, reason = "Create a factual descendant while retaining master stock" });
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "SELECT-RAW", quantity = 20m, locationCode = "STORES", lotNumber = "SELECT-INDEPENDENT", revision = "A", reason = "Unrelated material" });
        var order = Id(await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = "SELECT-OUT", quantity = 10m, dueDate = DateTime.UtcNow.AddDays(5).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Release selection source requirement" });
        var path = $"/api/inventory/selection?part=SELECT-RAW&order={order}";
        Assert.All((await GetAsync(stores, path)).GetProperty("candidates").EnumerateArray(), candidate => Assert.True(candidate.GetProperty("rank").GetInt32() > 0));
        await plant.Ok(plant.Admin, "/api/holds/kinds/Quality", new { blocks = new[] { "IssueMaterial" }, propagateDownstream = true, reason = "Use the configured material issue gate" });
        await plant.Ok(plant.Admin, "/api/holds/kinds/Shipping", new { blocks = new[] { "Ship" }, reason = "A shipment-only rule does not block material issue" });
        var qualityHold = Id(await plant.Ok(quality, "/api/holds", new { kind = "Quality", targetType = "Lot", targetPart = "SELECT-RAW", targetId = "SELECT-ROOT", reason = "Quality review SELECT-H-01" }));
        await plant.Ok(plant.Engineer, "/api/holds", new { kind = "Shipping", targetType = "Lot", targetPart = "SELECT-RAW", targetId = "SELECT-INDEPENDENT", reason = "Shipping paperwork pending" });
        var selection = (await GetAsync(stores, path)).GetProperty("candidates").EnumerateArray().ToDictionary(candidate => candidate.GetProperty("lotNumber").GetString()!);
        foreach (var lot in new[] { "SELECT-ROOT", "SELECT-CHILD" })
        {
            Assert.Equal(0, selection[lot].GetProperty("rank").GetInt32());
            Assert.Contains($"Quality hold {qualityHold}", selection[lot].GetProperty("excludedBecause").GetString()!, StringComparison.Ordinal);
            var refused = await plant.Try(stores, "/api/inventory/issues", new { orderNumber = order, partNumber = "SELECT-RAW", quantity = 1m, locationCode = "STORES", lotNumber = lot });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Contains("on_hold", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        Assert.Equal(1, selection["SELECT-INDEPENDENT"].GetProperty("rank").GetInt32());
        Assert.Equal(JsonValueKind.Null, selection["SELECT-INDEPENDENT"].GetProperty("excludedBecause").ValueKind);
        await plant.Ok(stores, "/api/inventory/issues", new { orderNumber = order, partNumber = "SELECT-RAW", quantity = 1m, locationCode = "STORES", lotNumber = "SELECT-INDEPENDENT" });
        Assert.All((await GetAsync(stores, "/api/inventory/stock?part=SELECT-RAW")).EnumerateArray(), row => Assert.Equal("Released", row.GetProperty("status").GetString()));
        await plant.Ok(quality, $"/api/holds/{qualityHold}/release", new { reason = "Quality evidence reviewed and material cleared" });
        var restored = (await GetAsync(stores, path)).GetProperty("candidates").EnumerateArray().ToArray();
        Assert.Equal(3, restored.Length);
        Assert.All(restored, candidate => Assert.True(candidate.GetProperty("rank").GetInt32() > 0));
        Assert.All(restored, candidate => Assert.Equal(JsonValueKind.Null, candidate.GetProperty("excludedBecause").ValueKind));
    }

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);
    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }
}
