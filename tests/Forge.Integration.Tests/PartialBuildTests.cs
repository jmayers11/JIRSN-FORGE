using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Forge.Integration.Tests;

/// <summary>
/// Sprint 10 §17–19 through the real API: a BOM consumption point lets an order go as far as its material allows, waiting WIP needs
/// safety-approved storage, and the approval atomically reserves material and space until the build is completed or cancelled.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PartialBuildTests(PostgresFixture postgres)
{
    private static string Day(int fromToday) => DateTime.UtcNow.AddDays(fromToday).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static string Blockers(JsonElement plan) => string.Join(" | ", plan.GetProperty("blockers").EnumerateArray().Select(b => b.GetString()));

    [Fact]
    public async Task A_partial_build_waits_in_approved_space_and_reserves_its_material_and_storage()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var e = plant.Engineer;
        var stores = plant.As("stores");

        // A frame: coil is consumed at op 10, the motor (none in stock) only at op 20.
        await plant.Ok(e, "/api/engineering/items", new { partNumber = "MOTOR-1", description = "Drive motor", unitOfMeasure = "EA", makeBuy = "Buy", leadTimeDays = 20 });
        await plant.Ok(e, "/api/engineering/items", new { partNumber = "FRAME-1", description = "Welded frame", unitOfMeasure = "EA", makeBuy = "Make", leadTimeDays = 3 });
        await plant.Ok(e, "/api/engineering/items/MOTOR-1/revisions", new { revision = "A" });
        await plant.Ok(e, "/api/engineering/items/FRAME-1/revisions", new { revision = "A" });
        await plant.Ok(e, "/api/engineering/items/FRAME-1/revisions/A/bom", new { lineNumber = 1, componentPartNumber = "COIL-AL", quantityPer = 2m, scrapPercent = 0m, consumedAtSequence = 10 });
        await plant.Ok(e, "/api/engineering/items/FRAME-1/revisions/A/bom", new { lineNumber = 2, componentPartNumber = "MOTOR-1", quantityPer = 1m, scrapPercent = 0m, consumedAtSequence = 20 });
        Assert.Contains("invalid_consumption_point", await (await plant.Try(e, "/api/engineering/items/FRAME-1/revisions/A/bom",
            new { lineNumber = 3, componentPartNumber = "COIL-AL", quantityPer = 1m, scrapPercent = 0m, consumedAtSequence = 0 })).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        foreach (var (sequence, description) in new[] { (10, "Form"), (20, "Fit motor") })
        {
            await plant.Ok(e, "/api/engineering/items/FRAME-1/revisions/A/routing", new
            {
                sequence, description, workCenterCode = "SLIT-WC", activityCode = "RUN_SLITTER", eligibleMachines = new[] { "SLITTER-A", "SLITTER-B" },
                setupMinutes = 0m, runMinutesPerUnit = 1m, crewSize = 1,
            });
        }

        await plant.Ok(plant.Approver, "/api/engineering/items/MOTOR-1/revisions/A/release", new { reason = "ECN-17" });
        await plant.Ok(plant.Approver, "/api/engineering/items/FRAME-1/revisions/A/release", new { reason = "ECN-17" });
        var order = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "FRAME-1", quantity = 40m, dueDate = Day(14), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Customer order" });
        var materials = (await Get(planner, $"/api/orders/{order}")).GetProperty("materials").EnumerateArray().ToList();
        Assert.Equal(new int?[] { 10, 20 }, materials.Select(m => (int?)m.GetProperty("consumedAtSequence").GetInt32()).ToArray());

        var previewUrl = $"/api/planning/orders/{order}/partial-build-preview";
        var plan = await plant.Ok(planner, previewUrl, new { receivingHours = 2m, inspectionHours = 4m, putAwayHours = 1m });
        Assert.Equal(10, plan.GetProperty("stopSequence").GetInt32());
        Assert.Equal(20, plan.GetProperty("blockedSequence").GetInt32());
        Assert.False(plan.GetProperty("canApprove").GetBoolean());
        Assert.Contains("No approved WIP storage rule", Blockers(plan), StringComparison.Ordinal);
        Assert.Contains("partial_build_not_feasible", await (await plant.Try(planner, $"/api/planning/orders/{order}/partial-builds", new { reason = "Go" })).Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Safe holding space is a safety decision by someone other than the proposer, never a planner's assumption.
        await plant.Ok(stores, "/api/inventory/locations", new { code = "WIP-RACK", siteCode = "PLANT1", name = "Frame WIP rack", kind = "Production" });
        Assert.Contains("safety_reference_required", await (await plant.Try(stores, "/api/inventory/storage-rules",
            new { locationCode = "WIP-RACK", partNumber = "FRAME-1", maxUnits = 25m, safetyReference = " ", reason = "x" })).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var rule = Id(await plant.Ok(stores, "/api/inventory/storage-rules", new { locationCode = "WIP-RACK", partNumber = "FRAME-1", maxUnits = 25m, maxDwellHours = 720, safetyReference = "Rack rating RR-12", reason = "Rack installed" }));
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(planner, $"/api/inventory/storage-rules/{rule}/decision", new { approve = true, reason = "x" })).StatusCode);
        await plant.Ok(plant.Approver, $"/api/inventory/storage-rules/{rule}/decision", new { approve = true, reason = "Rack inspected" });

        plan = await plant.Ok(planner, previewUrl, new { receivingHours = 2m, inspectionHours = 4m, putAwayHours = 1m });
        Assert.True(plan.GetProperty("canApprove").GetBoolean(), Blockers(plan));
        Assert.Equal(25m, plan.GetProperty("quantity").GetDecimal());
        Assert.Equal(25m, plan.GetProperty("storageQuantity").GetDecimal());
        Assert.Equal("WIP-RACK", plan.GetProperty("storageLocation").GetString());
        var coil = Assert.Single(plan.GetProperty("materials").EnumerateArray());
        Assert.Equal(("COIL-AL", 50m), (coil.GetProperty("partNumber").GetString(), coil.GetProperty("quantity").GetDecimal()));
        Assert.Equal(JsonValueKind.Null, plan.GetProperty("expectedRestart").ValueKind);   // the motor has no open supply at all

        // Reading a preview reserves nothing; approval reserves material and space in one step, once per order.
        Assert.Equal(25m, (await Get(planner, "/api/inventory/storage-rules?part=FRAME-1"))[0].GetProperty("availability").GetProperty("free").GetDecimal());
        var build = Id(await plant.Ok(planner, $"/api/planning/orders/{order}/partial-builds", new { receivingHours = 2m, inspectionHours = 4m, putAwayHours = 1m, reason = "Keep the cell loaded" }));
        var held = Assert.Single((await Get(planner, $"/api/planning/partial-builds?order={order}&active=true")).EnumerateArray());
        Assert.Equal(build, held.GetProperty("buildId").GetInt64());
        Assert.Equal(50m, held.GetProperty("materials")[0].GetProperty("quantity").GetDecimal());
        Assert.Contains("partial-build-v1", held.GetProperty("evidence").GetString() ?? string.Empty, StringComparison.Ordinal);
        var space = (await Get(planner, "/api/inventory/storage-rules?part=FRAME-1"))[0].GetProperty("availability");
        Assert.Equal(0m, space.GetProperty("free").GetDecimal());
        Assert.Equal(25m, space.GetProperty("occupancy").GetProperty("reserved").GetDecimal());
        Assert.Contains("already approved", await (await plant.Try(planner, $"/api/planning/orders/{order}/partial-builds", new { reason = "Again" })).Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Cancelling releases both reservations.
        await plant.Ok(planner, $"/api/planning/partial-builds/{build}/cancel", new { reason = "Motors expedited" });
        Assert.Equal(25m, (await Get(planner, "/api/inventory/storage-rules?part=FRAME-1"))[0].GetProperty("availability").GetProperty("free").GetDecimal());
        Assert.Empty((await Get(planner, $"/api/planning/partial-builds?order={order}&active=true")).EnumerateArray());
        Assert.Contains("partial_build_closed", await (await plant.Try(planner, $"/api/planning/partial-builds/{build}/cancel", new { reason = "Twice" })).Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
