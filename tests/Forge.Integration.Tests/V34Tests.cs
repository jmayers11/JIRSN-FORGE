using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Forge.Integration.Tests;

/// <summary>
/// V3.4 through the real API: material for an order is requested, allocated (held from other tasks), picked
/// with the location and lot scanned, and delivered with the destination scanned; only delivery moves stock.
/// Required, requested, allocated, picked, delivered, issued and consumed are separate and partial; stock in the
/// warehouse but not at the machine is a material-flow constraint, not a shortage. Cycle-count programs make
/// balances due until they are counted.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class V34Tests(PostgresFixture postgres)
{
    private static string Day(int fromToday) => DateTime.UtcNow.AddDays(fromToday).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    private static async Task Rejected(SlittingPlant.Plant plant, HttpClient client, string url, object body, string code, HttpStatusCode status = HttpStatusCode.UnprocessableEntity)
    {
        var response = await plant.Try(client, url, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"{url} -> {(int)response.StatusCode}: {text}");
        Assert.Contains(code, text, StringComparison.Ordinal);
    }

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static async Task<JsonElement> Flow(HttpClient client, long order) =>
        (await Get(client, $"/api/inventory/orders/{order}/material-flow")).GetProperty("lines").EnumerateArray().Single();

    [Fact]
    public async Task Material_is_requested_allocated_picked_and_delivered_by_scan_and_flow_is_kept_apart_from_shortage()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var e = plant.Engineer;
        await plant.Person("mo", "MaterialHandler");
        var (stores, mo) = (plant.As("stores"), plant.As("mo"));
        foreach (var (part, makeBuy) in new[] { ("FOIL-4", "Buy"), ("STRIP-4", "Make") })
        {
            await plant.Ok(e, "/api/engineering/items", new { partNumber = part, description = part, unitOfMeasure = "M", makeBuy, leadTimeDays = 5, lotTracked = part == "FOIL-4" });
            await plant.Ok(e, $"/api/engineering/items/{part}/revisions", new { revision = "A" });
        }

        await plant.Ok(e, "/api/engineering/items/STRIP-4/revisions/A/bom", new { lineNumber = 1, componentPartNumber = "FOIL-4", quantityPer = 1m, scrapPercent = 0m });
        await plant.Ok(e, "/api/engineering/items/STRIP-4/revisions/A/routing", new { sequence = 10, description = "Slit", workCenterCode = "SLIT-WC", activityCode = "RUN_SLITTER", eligibleMachines = new[] { "SLITTER-A" }, setupMinutes = 0m, runMinutesPerUnit = 0.01m, crewSize = 1 });
        foreach (var part in new[] { "FOIL-4", "STRIP-4" })
        {
            await plant.Ok(plant.Approver, $"/api/engineering/items/{part}/revisions/A/release", new { reason = "Baseline" });
        }

        const string locations = "/api/inventory/locations";
        await plant.Ok(stores, locations, new { code = "WH1", siteCode = "PLANT1", name = "Warehouse 1", kind = "Stores" });
        await plant.Ok(stores, locations, new { code = "SL-STAGE", siteCode = "PLANT1", name = "Slitting staging", kind = "Staging", workCenterCode = "SLIT-WC" });
        await plant.Ok(stores, locations, new { code = "SL-SIDE", siteCode = "PLANT1", name = "Beside slitter A", kind = "MachineSide", machineCode = "SLITTER-A" });
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "FOIL-4", quantity = 150m, locationCode = "WH1", lotNumber = "F-1", reason = "Opening balance" });

        var order = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "STRIP-4", quantity = 100m, dueDate = Day(10), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Customer order" });

        // In the warehouse, not at the machine: a flow constraint, not a shortage.
        var flow = await Flow(mo, order);
        Assert.Equal(("InWarehouse", "SLIT-WC", 0m, 150m), (flow.GetProperty("state").GetString(), flow.GetProperty("workCenterCode").GetString(), flow.GetProperty("atPointOfUse").GetDecimal(), flow.GetProperty("elsewhere").GetDecimal()));
        var report = await Get(planner, "/api/reports/material-flow?status=InWarehouse");
        Assert.Contains(report.GetProperty("rows").EnumerateArray(), r => r.GetProperty("cells")[0].GetString() == order.ToString(CultureInfo.InvariantCulture));

        // Requested for the order, to its staging; never more than it still needs; allocated at once from a named lot.
        const string tasks = "/api/inventory/tasks";
        object Stage(decimal quantity, string to, string? from = null) => new { kind = "Stage", orderNumber = order, partNumber = "FOIL-4", quantity, toLocation = to, fromLocation = from, lotNumber = "F-1" };
        await Rejected(plant, mo, tasks, Stage(10, "WH1"), "not_point_of_use");
        await Rejected(plant, mo, tasks, Stage(120, "SL-STAGE"), "over_request");
        var task = Id(await plant.Ok(mo, tasks, Stage(100, "SL-STAGE", "WH1")));

        // Allocated stock is held: a replenishment cannot take what the task holds.
        await Rejected(plant, mo, tasks, new { kind = "Replenish", partNumber = "FOIL-4", quantity = 60m, toLocation = "SL-SIDE", fromLocation = "WH1", lotNumber = "F-1" }, "insufficient_unallocated");
        var replenish = Id(await plant.Ok(mo, tasks, new { kind = "Replenish", partNumber = "FOIL-4", quantity = 50m, toLocation = "SL-SIDE", lotNumber = "F-1" }));
        Assert.Equal(("Requested", 100m, 100m, 0m), ((await Get(mo, $"{tasks}?open=true")).EnumerateArray().Single(t => t.GetProperty("taskId").GetInt64() == replenish).GetProperty("status").GetString(),
            (await Flow(mo, order)).GetProperty("requested").GetDecimal(), (await Flow(mo, order)).GetProperty("allocated").GetDecimal(), (await Flow(mo, order)).GetProperty("picked").GetDecimal()));

        // Picking is confirmed by scanning the location and the lot; a short pick is recorded as such. Nothing moves yet.
        await Rejected(plant, mo, $"{tasks}/{task}/deliver", new { scannedLocation = "SL-STAGE" }, "wrong_step");
        await Rejected(plant, mo, $"{tasks}/{task}/pick", new { scannedLocation = "SL-SIDE", scannedLot = "F-1" }, "scan_mismatch");
        await Rejected(plant, mo, $"{tasks}/{task}/pick", new { scannedLocation = "WH1", scannedLot = "F-9" }, "scan_mismatch");
        await plant.Ok(mo, $"{tasks}/{task}/pick", new { scannedLocation = "wh1", scannedLot = "F-1", quantity = 90m });
        Assert.Equal(150m, (await Get(mo, "/api/inventory/stock?part=FOIL-4")).EnumerateArray().Where(b => b.GetProperty("locationCode").GetString() == "WH1").Sum(b => b.GetProperty("quantity").GetDecimal()));

        // Delivery is confirmed by scanning where it went; the picked quantity moves through In Transit custody and a different,
        // authenticated worker receives it at the point of use (Sprint 10).
        await plant.EnsureCustodyAsync();
        await Rejected(plant, mo, $"{tasks}/{task}/deliver", new { scannedLocation = "SL-SIDE" }, "scan_mismatch");
        await plant.Ok(mo, $"{tasks}/{task}/deliver", new { scannedLocation = "SL-STAGE" });
        await plant.ReceiveAsync(task, "SL-STAGE", 90m);
        var moved = (await Get(mo, $"/api/inventory/transactions?part=FOIL-4")).EnumerateArray().First();
        Assert.Equal(("Transfer", 90m, SlittingPlant.Plant.TransitLocation, "SL-STAGE", order), (moved.GetProperty("kind").GetString(), moved.GetProperty("quantity").GetDecimal(), moved.GetProperty("fromLocation").GetString(), moved.GetProperty("toLocation").GetString(), moved.GetProperty("orderNumber").GetInt64()));

        // Each state on its own: 100 requested, 90 picked, delivered and received, then 90 issued from staging. Once the receiver settles the
        // short task at 90, only those 90 remain allocated to it.
        await plant.Ok(stores, "/api/inventory/issues", new { orderNumber = order, partNumber = "FOIL-4", quantity = 90m, locationCode = "SL-STAGE", lotNumber = "F-1" });
        flow = await Flow(mo, order);
        Assert.Equal((100m, 100m, 90m, 90m, 90m, 90m), (flow.GetProperty("required").GetDecimal(), flow.GetProperty("requested").GetDecimal(), flow.GetProperty("allocated").GetDecimal(),
            flow.GetProperty("picked").GetDecimal(), flow.GetProperty("delivered").GetDecimal(), flow.GetProperty("issued").GetDecimal()));
        Assert.Equal("InWarehouse", flow.GetProperty("state").GetString()); // 10 still to issue, 60 in the warehouse

        // Cancelling needs a reason and frees what was held.
        await Rejected(plant, mo, $"{tasks}/{replenish}/cancel", new { }, "reason_required");
        await plant.Ok(mo, $"{tasks}/{replenish}/cancel", new { reason = "Slitter A down for the week" });
        await Rejected(plant, mo, $"{tasks}/{replenish}/allocate", new { fromLocation = "WH1", lotNumber = "F-1" }, "wrong_step");

        // A count program makes every balance in the warehouse due until it is counted.
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(mo, "/api/inventory/count-programs", new { programCode = "CC-WH1", name = "Warehouse monthly", locationCode = "WH1", frequencyDays = 30 })).StatusCode);
        await plant.Ok(stores, "/api/inventory/count-programs", new { programCode = "CC-WH1", name = "Warehouse monthly", locationCode = "WH1", frequencyDays = 30 });
        var due = (await Get(mo, "/api/inventory/counts-due")).EnumerateArray().ToList();
        Assert.Equal(("CC-WH1", "FOIL-4", "F-1"), (Assert.Single(due).GetProperty("programCode").GetString(), due[0].GetProperty("partNumber").GetString(), due[0].GetProperty("lotNumber").GetString()));
        await plant.Ok(mo, "/api/inventory/counts", new { partNumber = "FOIL-4", locationCode = "WH1", lotNumber = "F-1", status = "Released", counted = 60m });
        Assert.Empty((await Get(mo, "/api/inventory/counts-due")).EnumerateArray());
    }
}
