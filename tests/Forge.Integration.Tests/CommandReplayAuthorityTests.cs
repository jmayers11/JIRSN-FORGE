using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class CommandReplayAuthorityTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Shared_terminal_retry_is_bound_to_the_badged_person_and_authenticated_terminal()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("replay-kiosk", "ShopFloorTerminal");
        await plant.Person("replay-other-kiosk", "ShopFloorTerminal");
        await plant.Person("replay-outsider");
        var unauthorized = await plant.Try(plant.As("replay-outsider"), "/api/shopfloor/start",
            new { badge = "NOT-A-REGISTERED-BADGE", orderNumber = 999999L, sequence = 10 });
        Assert.Equal(HttpStatusCode.Forbidden, unauthorized.StatusCode);
        Assert.DoesNotContain("unknown_badge", await unauthorized.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var kiosk = plant.As("replay-kiosk");
        const string sarah = "REPLAY-SARAH";
        const string michael = "REPLAY-MICHAEL";
        await plant.Ok(plant.Admin, $"/api/workforce/people/{plant.People["sarah"]}/badge", new { badge = sarah });
        await plant.Ok(plant.Admin, $"/api/workforce/people/{plant.People["michael"]}/badge", new { badge = michael });
        await plant.Ok(kiosk, "/api/shopfloor/clock-in", new { badge = sarah });
        await plant.Ok(kiosk, "/api/shopfloor/clock-in", new { badge = michael });
        var number = long.Parse((await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = "ROLL-100", quantity = 10m, dueDate = Day(), priority = 3 })).GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);
        await plant.Ok(planner, $"/api/orders/{number}/release", new { reason = "Replay source order" });
        var start = new { badge = sarah, orderNumber = number, sequence = 10, machineCode = "SLITTER-A" };
        var first = await kiosk.CommandAsync("/api/shopfloor/start", start, "replay-person-source");
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
        var repeated = await kiosk.CommandAsync("/api/shopfloor/start", start, "replay-person-source");
        Assert.Equal(await first.Content.ReadAsStringAsync(), await repeated.Content.ReadAsStringAsync());
        var otherPerson = await kiosk.CommandAsync("/api/shopfloor/start", new { badge = michael, orderNumber = number, sequence = 10, machineCode = "SLITTER-A" }, "replay-person-source");
        Assert.Equal(HttpStatusCode.Conflict, otherPerson.StatusCode);
        var otherTerminal = await plant.As("replay-other-kiosk").CommandAsync("/api/shopfloor/start", start, "replay-person-source");
        // HTTP keys already include the authenticated actor. Another terminal executes its
        // own command and sees current labor; it cannot receive the original "ok" replay.
        Assert.True(otherTerminal.IsSuccessStatusCode, await otherTerminal.Content.ReadAsStringAsync());
        using var terminalResult = JsonDocument.Parse(await otherTerminal.Content.ReadAsStringAsync());
        Assert.Equal("no_change", terminalResult.RootElement.GetProperty("code").GetString());
        var activity = await planner.GetFromJsonAsync<JsonElement>($"/api/orders/{number}/shopfloor");
        Assert.Equal(plant.People["sarah"], Assert.Single(activity.GetProperty("labor").EnumerateArray()).GetProperty("personId").GetGuid());
        var stopped = await kiosk.CommandAsync("/api/shopfloor/stop", new { badge = sarah }, "replay-stopped-person");
        Assert.True(stopped.IsSuccessStatusCode, await stopped.Content.ReadAsStringAsync());
        var otherStop = await kiosk.CommandAsync("/api/shopfloor/stop", new { badge = michael }, "replay-stopped-person");
        Assert.Equal(HttpStatusCode.Conflict, otherStop.StatusCode);
    }

    [Fact]
    public async Task Replaying_a_success_rechecks_the_badged_persons_current_program_access()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("replay-programs", "ProgramManager");
        await plant.Person("replay-private-kiosk", "ShopFloorTerminal");
        var program = Guid.Parse((await plant.Ok(plant.As("replay-programs"), "/api/programs/", new { code = "REPLAY-PRIVATE", name = "Private execution replay" })).GetProperty("entityId").GetString()!);
        foreach (var who in new[] { "eng", "planner", "sarah", "replay-private-kiosk" })
            await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People[who]}/programs", new { programId = program, reason = "Current source access" });
        var number = long.Parse((await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = "ROLL-100", quantity = 10m, dueDate = Day(), priority = 3, programId = program })).GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);
        await plant.Ok(planner, $"/api/orders/{number}/release", new { reason = "Release private source order" });
        const string badge = "REPLAY-PRIVATE-SARAH";
        var kiosk = plant.As("replay-private-kiosk");
        await plant.Ok(plant.Admin, $"/api/workforce/people/{plant.People["sarah"]}/badge", new { badge });
        await plant.Ok(kiosk, "/api/shopfloor/clock-in", new { badge });
        var start = new { badge, orderNumber = number, sequence = 10, machineCode = "SLITTER-A" };
        var started = await kiosk.CommandAsync("/api/shopfloor/start", start, "replay-revoked-program");
        Assert.True(started.IsSuccessStatusCode, await started.Content.ReadAsStringAsync());
        await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["sarah"]}/programs/{program}/revoke", new { reason = "Access ended" });
        var retry = await kiosk.CommandAsync("/api/shopfloor/start", start, "replay-revoked-program");
        Assert.Equal(HttpStatusCode.NotFound, retry.StatusCode);
        Assert.Contains("order_not_found", await retry.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var activity = await planner.GetFromJsonAsync<JsonElement>($"/api/orders/{number}/shopfloor");
        Assert.Single(activity.GetProperty("labor").EnumerateArray());
    }

    private static string Day() => DateTime.UtcNow.AddDays(10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
