using System.Net;
using System.Text.Json;

namespace Forge.Integration.Tests;

/// <summary>
/// Phase 3 exit gate: engineering definitions are validated and frozen on release, orders snapshot
/// them, and every released order shows readiness per operation.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class Phase3Tests(PostgresFixture postgres)
{
    private static readonly Guid Falcon = Guid.Parse("018f0000-0000-7000-8000-00000000fa1c");

    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    [Fact]
    public async Task Engineering_rejects_loops_and_freezes_released_revisions()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        var e = plant.Engineer;

        await plant.Ok(e, "/api/engineering/items", new { partNumber = "ROLL-200", description = "Twin roll", unitOfMeasure = "EA", makeBuy = "Make", leadTimeDays = 1 });
        await plant.Ok(e, "/api/engineering/items/ROLL-200/revisions", new { revision = "A" });
        await plant.Ok(e, "/api/engineering/items/ROLL-200/revisions/A/bom", new { lineNumber = 1, componentPartNumber = "ROLL-100", quantityPer = 2m, scrapPercent = 0m });
        await plant.Ok(e, "/api/engineering/items/ROLL-100/revisions", new { revision = "B" });

        var loop = await plant.Try(e, "/api/engineering/items/ROLL-100/revisions/B/bom", new { lineNumber = 1, componentPartNumber = "ROLL-200", quantityPer = 1m, scrapPercent = 0m });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, loop.StatusCode);
        Assert.Contains("bom_loop", await loop.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var frozen = await plant.Try(e, "/api/engineering/items/ROLL-100/revisions/A/bom", new { lineNumber = 2, componentPartNumber = "COIL-AL", quantityPer = 1m, scrapPercent = 0m });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, frozen.StatusCode);
        Assert.Contains("revision_released", await frozen.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // An engineer cannot release; release authority is separate.
        var noAuthority = await plant.Try(e, "/api/engineering/items/ROLL-200/revisions/A/release", new { reason = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, noAuthority.StatusCode);
    }

    [Fact]
    public async Task Released_order_snapshots_engineering_and_shows_readiness_per_operation()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);

        // The engineer sets up the shop order; the planner releases it.
        var created = await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = "ROLL-100", quantity = 10m, dueDate = "2026-10-09", priority = 3 });
        var number = long.Parse(created.GetProperty("entityId").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(10001, number);

        var planned = await Get(planner, $"/api/orders/{number}/readiness");
        Assert.Contains("no operations", planned.GetProperty("output").GetProperty("summary").GetString(), StringComparison.Ordinal);

        await plant.Ok(planner, $"/api/orders/{number}/release", new { reason = "Due next week" });

        var order = await Get(planner, $"/api/orders/{number}");
        Assert.Equal("Released", order.GetProperty("status").GetString());
        Assert.Equal(26m, order.GetProperty("materials")[0].GetProperty("requiredQuantity").GetDecimal()); // 2.5 x 10 x 1.04
        Assert.Equal(45m, order.GetProperty("operations")[0].GetProperty("plannedMinutes").GetDecimal()); // 30 + 1.5 x 10

        var readiness = await Get(planner, $"/api/orders/{number}/readiness");
        var ops = readiness.GetProperty("output").GetProperty("operations");
        Assert.True(ops[0].GetProperty("executable").GetBoolean(), ops[0].GetRawText());
        Assert.Equal("Predecessor", ops[1].GetProperty("primaryConstraint").GetString());

        // Engineering releases rev B with more material; the released order keeps its snapshot
        // and its readiness asks engineering to confirm.
        var e = plant.Engineer;
        await plant.Ok(e, "/api/engineering/items/ROLL-100/revisions", new { revision = "B" });
        await plant.Ok(e, "/api/engineering/items/ROLL-100/revisions/B/bom", new { lineNumber = 1, componentPartNumber = "COIL-AL", quantityPer = 3m, scrapPercent = 4m });
        await plant.Ok(e, "/api/engineering/items/ROLL-100/revisions/B/routing", new { sequence = 10, description = "Slit", workCenterCode = "SLIT-WC", activityCode = "RUN_SLITTER", eligibleMachines = new[] { "SLITTER-A" }, setupMinutes = 30m, runMinutesPerUnit = 1.5m, crewSize = 1 });
        await plant.Ok(plant.Approver, "/api/engineering/items/ROLL-100/revisions/B/release", new { reason = "ECN-2 wider coil" });

        order = await Get(planner, $"/api/orders/{number}");
        Assert.Equal(26m, order.GetProperty("materials")[0].GetProperty("requiredQuantity").GetDecimal());
        readiness = await Get(planner, $"/api/orders/{number}/readiness");
        Assert.Equal("Engineering", readiness.GetProperty("output").GetProperty("operations")[0].GetProperty("primaryConstraint").GetString());

        var next = await plant.Ok(planner, "/api/orders/", new { partNumber = "ROLL-100", quantity = 1m, dueDate = "2026-10-20", priority = 5 });
        Assert.Contains("rev B", next.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Order_lifecycle_requires_reasons_and_valid_transitions()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Ok(planner, "/api/orders/", new { partNumber = "ROLL-100", quantity = 5m, dueDate = "2026-10-09", priority = 3 });

        await plant.Ok(planner, "/api/orders/10001/firm", new { });
        var noReason = await plant.Try(planner, "/api/orders/10001/hold", new { });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noReason.StatusCode);
        await plant.Ok(planner, "/api/orders/10001/hold", new { reason = "Customer query" });
        await plant.Ok(planner, "/api/orders/10001/resume", new { reason = "Customer confirmed" });
        Assert.Equal("FirmPlanned", (await Get(planner, "/api/orders/10001")).GetProperty("status").GetString());

        await plant.Ok(planner, "/api/orders/10001/cancel", new { reason = "Order withdrawn" });
        var releaseCancelled = await plant.Try(planner, "/api/orders/10001/release", new { reason = "x" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, releaseCancelled.StatusCode);

        var purchased = await plant.Try(planner, "/api/orders/", new { partNumber = "COIL-AL", quantity = 1m, dueDate = "2026-10-09", priority = 3 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, purchased.StatusCode);
    }

    [Fact]
    public async Task Program_orders_are_invisible_without_program_access()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);

        // Without Falcon access the planner cannot even create a Falcon order.
        var denied = await plant.Try(planner, "/api/orders/", new { partNumber = "ROLL-100", quantity = 1m, dueDate = "2026-10-09", priority = 1, programId = Falcon });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["planner"]}/programs", new { programId = Falcon, reason = "Falcon planner" });
        var falconOrder = await plant.Ok(planner, "/api/orders/", new { partNumber = "ROLL-100", quantity = 1m, dueDate = "2026-10-09", priority = 1, programId = Falcon });
        var number = falconOrder.GetProperty("entityId").GetString();

        // The engineer (supervisor role, no Falcon access) sees nothing of it.
        var list = await Get(plant.Engineer, "/api/orders/");
        Assert.DoesNotContain(list.EnumerateArray(), o => o.GetProperty("orderNumber").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) == number);
        Assert.Equal(HttpStatusCode.NotFound, (await plant.Engineer.GetAsync($"/api/orders/{number}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await plant.Engineer.GetAsync($"/api/orders/{number}/readiness")).StatusCode);

        var visible = await Get(planner, "/api/orders/");
        Assert.Contains(visible.EnumerateArray(), o => o.GetProperty("orderNumber").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) == number);
    }

    [Fact]
    public async Task Setting_up_and_releasing_always_takes_two_people()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);

        // A planner who sets up an order cannot release it; another planner can.
        await plant.Ok(planner, "/api/orders/", new { partNumber = "ROLL-100", quantity = 5m, dueDate = "2026-10-09", priority = 3 });
        var own = await plant.Try(planner, "/api/orders/10001/release", new { reason = "x" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, own.StatusCode);
        Assert.Contains("separation_of_duties", await own.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await plant.Person("planner2", "Planner");
        await plant.Ok(plant.As("planner2"), "/api/orders/10001/release", new { reason = "Second planner review" });

        // An approver who edits a draft revision may no longer release it.
        await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["approver"]}/roles", new { role = "ManufacturingEngineer", reason = "Covering engineering" });
        await plant.Ok(plant.Engineer, "/api/engineering/items/ROLL-100/revisions", new { revision = "B" });
        await plant.Ok(plant.Approver, "/api/engineering/items/ROLL-100/revisions/B/routing", new { sequence = 10, description = "Slit", workCenterCode = "SLIT-WC", activityCode = "RUN_SLITTER", eligibleMachines = new[] { "SLITTER-A" }, setupMinutes = 30m, runMinutesPerUnit = 1.5m, crewSize = 1 });
        var ownEdit = await plant.Try(plant.Approver, "/api/engineering/items/ROLL-100/revisions/B/release", new { reason = "x" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, ownEdit.StatusCode);
        Assert.Contains("separation_of_duties", await ownEdit.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
