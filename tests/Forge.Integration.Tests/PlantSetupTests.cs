using System.Net;
using System.Text.Json;

namespace Forge.Integration.Tests;

/// <summary>
/// Plant setup through the real API: any industry's structure can be built and then edited
/// (rename, move, retire, reactivate), codes never change, every edit carries a reason, and
/// nothing in use can be retired or moved out from under orders, routings or people.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PlantSetupTests(PostgresFixture postgres)
{
    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    private static async Task<(HttpStatusCode Status, string Body)> Try(SlittingPlant.Plant plant, HttpClient client, string url, object body)
    {
        var response = await plant.Try(client, url, body);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static JsonElement Find(JsonElement resources, string list, string code) =>
        resources.GetProperty(list).EnumerateArray().Single(x => x.GetProperty("code").GetString() == code);

    [Fact]
    public async Task A_bonding_plant_is_set_up_edited_retired_and_reactivated()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var plant = new SlittingPlant.Plant(app);
        await plant.Person("eng", "ManufacturingEngineer", "TrainingCoordinator");
        await plant.Person("planner", "Planner");
        var e = plant.Engineer;

        await plant.Ok(e, "/api/competency/qualification-types", new { code = "BOND-I", name = "Bonding operator", kind = "Machine", validityDays = 365 });
        await plant.Ok(e, "/api/resources/sites", new { code = "WEST", name = "West plant", timeZone = "America/Chicago" });
        await plant.Ok(e, "/api/resources/safety-areas", new { code = "BONDING", name = "Bonding hall", parent = "WEST" });
        await plant.Ok(e, "/api/resources/safety-areas", new { code = "WINDING", name = "Winding", parent = "WEST" });
        await plant.Ok(e, "/api/resources/work-centers", new { code = "BOND-WC", name = "Bonding", parent = "BONDING", picture = "plain" });
        await plant.Ok(e, "/api/resources/work-centers", new { code = "WIND-WC", name = "Winders", parent = "WINDING" });
        await plant.Ok(e, "/api/resources/activity-types", new { code = "BOND", name = "Bond panels" });
        await plant.Ok(e, "/api/resources/machines", new { code = "LAM-1", name = "Laminator", parent = "BOND-WC" });
        await plant.Ok(e, "/api/resources/machines/LAM-1/activities", new { activityCode = "BOND", operatorQualifications = new[] { "BOND-I" } });

        var badPicture = await Try(plant, e, "/api/resources/work-centers", new { code = "X-WC", name = "X", parent = "BONDING", picture = "volcano" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badPicture.Status);

        // Edits need a reason, and only the engineer (ResourcesManage) may make them.
        var noReason = await Try(plant, e, "/api/resources/work-centers/BOND-WC/edit", new { name = "Bonding line", parent = "BONDING" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noReason.Status);
        Assert.Contains("reason", noReason.Body, StringComparison.OrdinalIgnoreCase);
        var planner = await Try(plant, plant.As("planner"), "/api/resources/work-centers/BOND-WC/edit", new { name = "Mine", parent = "BONDING", reason = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, planner.Status);

        await plant.Ok(e, "/api/resources/sites/WEST/edit", new { name = "West campus", timeZone = "America/Denver", reason = "Renamed site" });
        await plant.Ok(e, "/api/resources/work-centers/BOND-WC/edit", new { name = "Bonding line", parent = "BONDING", picture = "assembly", reason = "Clearer name" });
        await plant.Ok(e, "/api/resources/activity-types/BOND/edit", new { name = "Bond and cure", description = "Adhesive bonding with oven cure", reason = "Describe the step" });
        var unchanged = await plant.Ok(e, "/api/resources/activity-types/BOND/edit", new { name = "Bond and cure", description = "Adhesive bonding with oven cure", reason = "Again" });
        Assert.Equal("no_change", unchanged.GetProperty("code").GetString());

        // A machine moves to another work center; a work center cannot leave its site.
        await plant.Ok(e, "/api/resources/machines/LAM-1/edit", new { name = "Laminator 1", parent = "WIND-WC", reason = "Moved during re-layout" });
        await plant.Ok(e, "/api/resources/sites", new { code = "EAST", name = "East plant" });
        await plant.Ok(e, "/api/resources/safety-areas", new { code = "EAST-FLOOR", name = "East floor", parent = "EAST" });
        var crossSite = await Try(plant, e, "/api/resources/work-centers/WIND-WC/edit", new { name = "Winders", parent = "EAST-FLOOR", reason = "Move" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, crossSite.Status);

        var r = await Get(e, "/api/resources/?includeRetired=true");
        Assert.Equal("West campus", Find(r, "sites", "WEST").GetProperty("name").GetString());
        Assert.Equal("America/Denver", Find(r, "sites", "WEST").GetProperty("timeZone").GetString());
        Assert.Equal("assembly", Find(r, "workCenters", "BOND-WC").GetProperty("picture").GetString());
        Assert.Equal("WIND-WC", Find(r, "machines", "LAM-1").GetProperty("workCenterCode").GetString());
        Assert.Equal("WINDING", Find(r, "machines", "LAM-1").GetProperty("safetyAreaCode").GetString());

        // A work center with an active machine cannot be retired; retire the machine first.
        var withMachine = await Try(plant, e, "/api/resources/work-centers/WIND-WC/retire", new { reason = "Closing winding" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, withMachine.Status);
        Assert.Contains("LAM-1", withMachine.Body, StringComparison.Ordinal);
        await plant.Ok(e, "/api/resources/machines/LAM-1/retire", new { reason = "Sold" });
        await plant.Ok(e, "/api/resources/work-centers/WIND-WC/retire", new { reason = "Closing winding" });

        r = await Get(e, "/api/resources/?includeRetired=true");
        Assert.False(Find(r, "machines", "LAM-1").GetProperty("active").GetBoolean());
        Assert.False(Find(r, "workCenters", "WIND-WC").GetProperty("active").GetBoolean());
        var everyday = await Get(e, "/api/resources/");
        Assert.DoesNotContain(everyday.GetProperty("machines").EnumerateArray(), m => m.GetProperty("code").GetString() == "LAM-1");

        // Nothing new can refer to a retired record, and planning no longer sees it.
        var onRetired = await Try(plant, e, "/api/resources/machines", new { code = "WINDER-2", name = "Winder 2", parent = "WIND-WC" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, onRetired.Status);
        Assert.Contains("retired", onRetired.Body, StringComparison.Ordinal);
        var capacity = await Get(e, $"/api/planning/capacity?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(1).ToString("O"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(2).ToString("O"))}");
        Assert.DoesNotContain(capacity.GetProperty("output").GetProperty("machines").EnumerateArray(), m => m.GetProperty("machineCode").GetString() == "LAM-1");

        // A machine comes back only after its work center does.
        var orphan = await Try(plant, e, "/api/resources/machines/LAM-1/reactivate", new { reason = "Bought back" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, orphan.Status);
        await plant.Ok(e, "/api/resources/work-centers/WIND-WC/reactivate", new { reason = "Winding reopened" });
        await plant.Ok(e, "/api/resources/machines/LAM-1/reactivate", new { reason = "Bought back" });

        // Activity types in use cannot be retired; once removed from the machine they can.
        var bondInUse = await Try(plant, e, "/api/resources/activity-types/BOND/retire", new { reason = "Obsolete" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bondInUse.Status);
        await plant.Ok(e, "/api/resources/machines/LAM-1/activities/BOND/remove", new { reason = "No longer bonds" });
        await plant.Ok(e, "/api/resources/activity-types/BOND/retire", new { reason = "Obsolete" });

        var missing = await Try(plant, e, "/api/resources/machines/NOPE/edit", new { name = "x", parent = "WIND-WC", reason = "x" });
        Assert.Equal(HttpStatusCode.NotFound, missing.Status);
    }

    [Fact]
    public async Task Nothing_in_use_by_routings_orders_or_people_can_be_retired_moved_or_removed()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var e = plant.Engineer;
        var due = DateTime.UtcNow.AddDays(10).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var created = await plant.Ok(e, "/api/orders/", new { partNumber = "ROLL-100", quantity = 5m, dueDate = due, priority = 3 });
        var order = created.GetProperty("entityId").GetString();
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Customer order" });

        // SLITTER-A is an eligible machine in ROLL-100 rev A's released routing.
        var routing = await Try(plant, e, "/api/resources/machines/SLITTER-A/retire", new { reason = "Worn out" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, routing.Status);
        Assert.Contains("ROLL-100 rev A", routing.Body, StringComparison.Ordinal);
        var activity = await Try(plant, e, "/api/resources/machines/SLITTER-A/activities/RUN_SLITTER/remove", new { reason = "x" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, activity.Status);

        // Jennifer is assigned to the paint booth; once her assignment ends it can be retired.
        var assigned = await Try(plant, e, "/api/resources/machines/PAINT-BOOTH/retire", new { reason = "Replaced" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, assigned.Status);
        Assert.Contains("Jennifer", assigned.Body, StringComparison.Ordinal);
        await plant.Ok(e, $"/api/workforce/workers/{plant.People["jennifer"]}/assignment/end", new { reason = "Booth removed" });
        await plant.Ok(e, "/api/resources/machines/PAINT-BOOTH/retire", new { reason = "Replaced" });

        // Sarah is staging rolls at SLIT-WC, and people call it home.
        var staging = await Try(plant, e, "/api/resources/work-centers/SLIT-WC/activities/STAGE_ROLL/remove", new { reason = "x" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, staging.Status);
        Assert.Contains("Sarah", staging.Body, StringComparison.Ordinal);
        var move = await Try(plant, e, "/api/resources/work-centers/SLIT-WC/edit", new { name = "Slitting", parent = "PAINT", reason = "Re-layout" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, move.Status);
        Assert.Contains("home work center", move.Body, StringComparison.Ordinal);
        Assert.Contains($"order {order} operation 10", move.Body, StringComparison.Ordinal);

        // Renaming in place is always fine.
        await plant.Ok(e, "/api/resources/work-centers/SLIT-WC/edit", new { name = "Slitting line", parent = "SLITTING", reason = "Name on the floor signs" });
    }
}
