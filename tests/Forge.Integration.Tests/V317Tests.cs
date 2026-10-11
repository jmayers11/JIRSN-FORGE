using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class V317Tests(PostgresFixture postgres)
{
    private static string Day(int fromToday) => DateTime.UtcNow.AddDays(fromToday).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static long Id(JsonElement value) => long.Parse(value.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static async Task SetRoundTheClockScheduleAsync(SlittingPlant.Plant plant)
    {
        await plant.Ok(plant.Engineer, "/api/resources/shifts", new { code = "ALL", siteCode = "PLANT1", name = "Around the clock", start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 } });
        foreach (var workCenter in new[] { "SLIT-WC", "PAINT-WC", "ASSY-WC" })
        {
            await plant.Ok(plant.Engineer, $"/api/resources/work-centers/{workCenter}/shifts", new { shiftCode = "ALL" });
        }

        foreach (var person in new[] { "sarah", "michael", "jennifer", "dana" })
        {
            await plant.Ok(plant.Engineer, $"/api/workforce/workers/{plant.People[person]}/shift", new { shiftCode = "ALL" });
        }
    }

    [Fact]
    public async Task Priority_change_requires_authority_reason_and_a_current_impact_preview()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await SetRoundTheClockScheduleAsync(plant);
        var first = Id(await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = "ROLL-100", quantity = 12m, dueDate = Day(12), priority = 5 }));
        var second = Id(await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = "ROLL-100", quantity = 12m, dueDate = Day(12), priority = 6 }));
        var target = Id(await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = "ROLL-100", quantity = 12m, dueDate = Day(12), priority = 8 }));
        await plant.Ok(planner, $"/api/orders/{first}/release", new { reason = "Schedule priority acceptance" });
        await plant.Ok(planner, $"/api/orders/{second}/release", new { reason = "Schedule priority acceptance" });
        await plant.Ok(planner, $"/api/orders/{target}/release", new { reason = "Schedule priority acceptance" });

        var preview = await plant.Ok(planner, $"/api/orders/{target}/priority-preview", new { priority = 1 });
        Assert.Equal(8, preview.GetProperty("currentPriority").GetInt32());
        Assert.Equal(1, preview.GetProperty("proposedPriority").GetInt32());
        var impacts = preview.GetProperty("orders").EnumerateArray().ToList();
        var delayed = impacts.Where(o => o.GetProperty("orderNumber").GetInt64() != target && o.GetProperty("delayMinutes").ValueKind == JsonValueKind.Number).ToList();
        Assert.True(delayed.Count > 0, preview.ToString());
        Assert.Contains(delayed, o => o.GetProperty("orderNumber").GetInt64() is var id && (id == first || id == second));

        var changed = await plant.Ok(planner, $"/api/orders/{target}/priority", new
        {
            priority = 1,
            expectedPriority = 8,
            previewAsOf = preview.GetProperty("asOf").GetDateTimeOffset(),
            previewFingerprint = preview.GetProperty("fingerprint").GetString(),
            reason = "Customer expedite approved after reviewing displacement",
        });
        Assert.Equal(target.ToString(CultureInfo.InvariantCulture), changed.GetProperty("entityId").GetString());
        using var orderResponse = await planner.GetAsync($"/api/orders/{target}");
        var updated = await orderResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, updated.GetProperty("priority").GetInt32());
        Assert.Contains("projected delays", changed.GetProperty("message").GetString(), StringComparison.Ordinal);

        var refreshed = await plant.Ok(planner, $"/api/orders/{target}/priority-preview", new { priority = 2 });
        var fourth = Id(await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = "ROLL-100", quantity = 1m, dueDate = Day(12), priority = 5 }));
        await plant.Ok(planner, $"/api/orders/{fourth}/release", new { reason = "Change the schedule after preview" });
        var stale = await plant.Try(planner, $"/api/orders/{target}/priority", new
        {
            priority = 2,
            expectedPriority = 1,
            previewAsOf = refreshed.GetProperty("asOf").GetDateTimeOffset(),
            previewFingerprint = refreshed.GetProperty("fingerprint").GetString(),
            reason = "Reviewing stale preview behavior",
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, stale.StatusCode);
        Assert.Contains("stale_priority_preview", await stale.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await plant.Person("priority-kiosk", "ShopFloorTerminal");
        var denied = await plant.Try(plant.As("priority-kiosk"), $"/api/orders/{target}/priority", new
        {
            priority = 2,
            expectedPriority = 1,
            previewAsOf = refreshed.GetProperty("asOf").GetDateTimeOffset(),
            previewFingerprint = refreshed.GetProperty("fingerprint").GetString(),
            reason = "Unauthorized priority attempt",
        });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task Raising_priority_never_schedules_an_order_past_quality_or_production_holds()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await SetRoundTheClockScheduleAsync(plant);
        var blocked = Id(await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = "ROLL-100", quantity = 10m, dueDate = Day(10), priority = 8 }));
        await plant.Ok(planner, $"/api/orders/{blocked}/release", new { reason = "Quality gate acceptance" });
        await plant.Ok(plant.Engineer, "/api/holds/", new
        {
            kind = "Quality",
            targetType = "Order",
            targetId = blocked.ToString(CultureInfo.InvariantCulture),
            targetPart = "ROLL-100",
            source = "V317 test",
            evidence = "Open quality investigation",
            affectedRecords = Array.Empty<string>(),
            requiredResolution = "Quality release",
            reason = "Keep the order blocked during the priority preview",
        });

        var preview = await plant.Ok(planner, $"/api/orders/{blocked}/priority-preview", new { priority = 1 });
        var targetImpact = preview.GetProperty("orders").EnumerateArray().Single(o => o.GetProperty("orderNumber").GetInt64() == blocked);
        Assert.Null(targetImpact.GetProperty("completionAfter").GetDateTimeOffsetOrNull());
        Assert.Equal("Unschedulable", targetImpact.GetProperty("statusAfter").GetString());
    }
}

internal static class JsonElementExtensions
{
    public static DateTimeOffset? GetDateTimeOffsetOrNull(this JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? null : value.GetDateTimeOffset();
}
