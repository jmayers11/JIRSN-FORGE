using System.Globalization;
using System.Net;
using System.Text.Json;
using Forge.Inventory.Contracts;

namespace Forge.Integration.Tests;

/// <summary>
/// Owner decisions 2026-11-02 through the real host: the allocation optimizer offers the smallest leftover piece that fits a job's minimum
/// size (and sets it aside on approval), and output of open production orders that no demand is counting on, promised to a short order and
/// reserved for it when received.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RemnantWipAllocationTests(PostgresFixture postgres)
{
    private const string Dana = "B-DANA-1102";

    private static string Day(int fromToday) => DateTime.UtcNow.AddDays(fromToday).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task Rejected(SlittingPlant.Plant plant, HttpClient client, string url, object body, string code)
    {
        var response = await plant.Try(client, url, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{url} -> {(int)response.StatusCode}: {text}");
        Assert.Contains(code, text, StringComparison.Ordinal);
    }

    private static async Task<JsonElement?> ProposalAsync(HttpClient planner, string part) =>
        (await Get(planner, "/api/allocation/proposals")).EnumerateArray().Cast<JsonElement?>().SingleOrDefault(p => p!.Value.GetProperty("partNumber").GetString() == part);

    private static async Task MakeAsync(SlittingPlant.Plant plant, string part, string component, decimal per, params string[] operations)
    {
        var e = plant.Engineer;
        await plant.Ok(e, "/api/engineering/items", new { partNumber = part, description = part, unitOfMeasure = "EA", makeBuy = "Make", leadTimeDays = 2 });
        await plant.Ok(e, $"/api/engineering/items/{part}/revisions", new { revision = "A" });
        await plant.Ok(e, $"/api/engineering/items/{part}/revisions/A/bom", new { lineNumber = 1, componentPartNumber = component, quantityPer = per, scrapPercent = 0m });
        for (var i = 0; i < operations.Length; i++)
            await plant.Ok(e, $"/api/engineering/items/{part}/revisions/A/routing", new { sequence = (i + 1) * 10, description = operations[i], workCenterCode = "ASSY-WC", activityCode = "ASSEMBLE", setupMinutes = 0m, runMinutesPerUnit = 1m, crewSize = 1 });
        await plant.Ok(plant.Approver, $"/api/engineering/items/{part}/revisions/A/release", new { reason = "Baseline" });
    }

    [Fact]
    public void A_piece_fits_turned_but_never_thinner_and_needs_its_sizes_recorded()
    {
        var need = new PieceSize(16, 10, null, SizeUnit.Inch);
        Assert.True(PieceFit.Fits(new PieceSize(12, 18, null, SizeUnit.Inch), need, out var turned));
        Assert.Contains("turned", turned, StringComparison.Ordinal);
        Assert.True(PieceFit.Fits(new PieceSize(450, 300, null, SizeUnit.Millimetre), need, out _)); // 17.7 x 11.8 in
        Assert.False(PieceFit.Fits(new PieceSize(30, 9, null, SizeUnit.Inch), need, out _)); // too narrow either way
        Assert.False(PieceFit.Fits(new PieceSize(20, 20, 3, SizeUnit.Millimetre), new PieceSize(10, 10, 5, SizeUnit.Millimetre), out var thin));
        Assert.Contains("thinner", thin, StringComparison.Ordinal);
        Assert.False(PieceFit.Fits(new PieceSize(20, null, null, SizeUnit.Inch), need, out var missing));
        Assert.Contains("width is not recorded", missing, StringComparison.Ordinal);
        Assert.True(PieceFit.Measure(new PieceSize(18, 12, null, SizeUnit.Inch)) < PieceFit.Measure(new PieceSize(30, 20, null, SizeUnit.Inch)));
    }

    [Fact]
    public async Task The_smallest_leftover_piece_that_fits_is_offered_first_and_set_aside_on_approval()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var (e, stores) = (plant.Engineer, plant.As("stores"));
        await plant.Ok(e, "/api/engineering/items", new { partNumber = "PLATE", description = "Steel plate", unitOfMeasure = "EA", makeBuy = "Buy", leadTimeDays = 10, lotTracked = true });
        await plant.Ok(e, "/api/engineering/items/PLATE/revisions", new { revision = "A" });
        await plant.Ok(plant.Approver, "/api/engineering/items/PLATE/revisions/A/release", new { reason = "Baseline" });
        await MakeAsync(plant, "BRACKET", "PLATE", 1m, "Cut and form");
        foreach (var (lot, quantity) in new[] { ("FULL", 3m), ("R-SMALL", 1m), ("R-FIT", 1m), ("R-BIG", 1m) })
            await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "PLATE", quantity, locationCode = "STORES", lotNumber = lot, reason = "Opening" });
        foreach (var (lot, length, width) in new[] { ("R-SMALL", 10m, 8m), ("R-FIT", 18m, 12m), ("R-BIG", 30m, 20m) })
            await plant.Ok(stores, "/api/allocation/lot-dimensions", new { partNumber = "PLATE", lotNumber = lot, length, width, unit = "Inch" });
        Assert.Equal(HttpStatusCode.NotFound, (await plant.Try(stores, "/api/allocation/lot-dimensions", new { partNumber = "PLATE", lotNumber = "NOPE", length = 1m, unit = "Inch" })).StatusCode);

        // Only engineers, planners and inventory controllers set minimum piece sizes, with a reason.
        await plant.Person("bob", "Buyer");
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(plant.As("bob"), "/api/allocation/piece-sizes", new { parentPart = "BRACKET", componentPart = "PLATE", length = 16m, width = 10m, unit = "Inch", reason = "x" })).StatusCode);
        await plant.Ok(planner, "/api/allocation/piece-sizes", new { parentPart = "BRACKET", componentPart = "PLATE", length = 16m, width = 10m, unit = "Inch", reason = "Drawing B-12 blank" });

        var a = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "BRACKET", quantity = 2m, dueDate = Day(5), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{a}/release", new { reason = "Customer order" });
        var b = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "BRACKET", quantity = 1m, dueDate = Day(9), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{b}/release", new { reason = "Customer order" });

        // A (due first) gets the smallest pieces that fit: R-FIT, then R-BIG. B gets full stock. R-SMALL is too small for anyone.
        var proposal = (await ProposalAsync(planner, "PLATE"))!.Value;
        var lines = proposal.GetProperty("lines").EnumerateArray().Select(l => (l.GetProperty("orderNumber").GetInt64(), l.GetProperty("lotNumber").GetString()!, l.GetProperty("quantity").GetDecimal())).ToArray();
        Assert.Equal(new[] { (a, "R-FIT", 1m), (a, "R-BIG", 1m), (b, "FULL", 1m) }, lines);
        Assert.Contains("smallest that fits (measured lot, 18 × 12 in)", proposal.GetProperty("lines")[0].GetProperty("why").GetString(), StringComparison.Ordinal);
        Assert.Contains(proposal.GetProperty("ruledOut").EnumerateArray(), r => r.GetString()!.Contains("R-SMALL", StringComparison.Ordinal) && r.GetString()!.Contains("smaller", StringComparison.Ordinal));

        // Approving sets R-FIT aside for A: B may not cut it; A may.
        await Rejected(plant, planner, "/api/allocation/approve", new { orderNumber = a, partNumber = "PLATE", quantity = 1m, lotNumber = "R-SMALL" }, "not_proposed");
        var approved = await plant.Ok(planner, "/api/allocation/approve", new { orderNumber = a, partNumber = "PLATE", quantity = 1m, lotNumber = "R-FIT" });
        Assert.Contains("lot R-FIT is set aside", approved.GetProperty("message").GetString(), StringComparison.Ordinal);
        var sources = await Get(planner, "/api/allocation/sources");
        Assert.Equal(a, Assert.Single(sources.GetProperty("setAside").EnumerateArray()).GetProperty("orderNumber").GetInt64());
        await Rejected(plant, stores, "/api/inventory/issues", new { orderNumber = b, partNumber = "PLATE", quantity = 1m, locationCode = "STORES", lotNumber = "R-FIT" }, "set_aside_for_other");
        await plant.Ok(stores, "/api/inventory/issues", new { orderNumber = a, partNumber = "PLATE", quantity = 1m, locationCode = "STORES", lotNumber = "R-FIT" });
        Assert.Empty((await Get(planner, "/api/allocation/sources")).GetProperty("setAside").EnumerateArray());
        Assert.DoesNotContain((await ProposalAsync(planner, "PLATE"))!.Value.GetProperty("lines").EnumerateArray(), l => l.GetProperty("lotNumber").GetString() == "R-FIT");

        // Clearing the size: plain quantity again, no lot named.
        await plant.Ok(planner, "/api/allocation/piece-sizes", new { parentPart = "BRACKET", componentPart = "PLATE", unit = "Inch", reason = "Any blank will do" });
        Assert.All((await ProposalAsync(planner, "PLATE"))!.Value.GetProperty("lines").EnumerateArray(),
            l => Assert.True(!l.TryGetProperty("lotNumber", out var lot) || lot.ValueKind == JsonValueKind.Null));
    }

    [Fact]
    public async Task Unpegged_work_in_progress_is_promised_to_a_short_order_and_reserved_for_it_when_received()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var (e, stores) = (plant.Engineer, plant.As("stores"));
        await plant.Person("kiosk", "ShopFloorTerminal");
        var kiosk = plant.As("kiosk");
        await MakeAsync(plant, "SUB", "COIL-AL", 1m, "Assemble", "Final inspection");
        await MakeAsync(plant, "ASSY", "SUB", 1m, "Assemble unit");

        // A customer is counting on 4 SUB; order W makes 10, so 6 are unpegged.
        await plant.Ok(planner, "/api/planning/demand", new { partNumber = "SUB", quantity = 4m, needDate = Day(10), priority = 3, kind = "CustomerOrder", customer = "ACME", reference = "ACME PO 77", reason = "Customer order" });
        var w = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "SUB", quantity = 10m, dueDate = Day(6), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{w}/release", new { reason = "Stock build" });
        var m = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "ASSY", quantity = 8m, dueDate = Day(4), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{m}/release", new { reason = "Customer order" });

        var supply = (await Get(planner, "/api/allocation/sources")).GetProperty("workInProgress").EnumerateArray().Single(x => x.GetProperty("orderNumber").GetInt64() == w);
        Assert.Equal((10m, 4m, 6m), (supply.GetProperty("projected").GetDecimal(), supply.GetProperty("pegged").GetDecimal(), supply.GetProperty("unpegged").GetDecimal()));
        Assert.Contains("ACME PO 77", supply.GetProperty("peggedTo")[0].GetString(), StringComparison.Ordinal);
        Assert.Equal(2, supply.GetProperty("remainingOperations").GetArrayLength());

        // M is short 8 SUB with no stock: 6 are offered from W, which still runs every operation, including its inspection; 2 stay short.
        var proposal = (await ProposalAsync(planner, "SUB"))!.Value;
        var line = Assert.Single(proposal.GetProperty("lines").EnumerateArray());
        Assert.Equal((m, "WorkInProgress", 6m, w), (line.GetProperty("orderNumber").GetInt64(), line.GetProperty("mode").GetString(), line.GetProperty("quantity").GetDecimal(), line.GetProperty("wipOrderNumber").GetInt64()));
        Assert.Contains("20 Final inspection", line.GetProperty("why").GetString(), StringComparison.Ordinal);
        Assert.Contains("day(s) after order", line.GetProperty("why").GetString(), StringComparison.Ordinal); // W is due after M
        Assert.Equal(2m, Assert.Single(proposal.GetProperty("stillShort").EnumerateArray()).GetProperty("missing").GetDecimal());

        var promised = await plant.Ok(planner, "/api/allocation/approve", new { orderNumber = m, partNumber = "SUB", quantity = 6m, wipOrderNumber = w, reason = "Expedite W for M" });
        Assert.Contains("promised to order", promised.GetProperty("message").GetString(), StringComparison.Ordinal);
        await Rejected(plant, planner, "/api/allocation/approve", new { orderNumber = m, partNumber = "SUB", quantity = 1m, wipOrderNumber = w }, "not_proposed");
        var after = (await ProposalAsync(planner, "SUB"))!.Value;
        Assert.Empty(after.GetProperty("lines").EnumerateArray());
        Assert.Equal(6m, Assert.Single(after.GetProperty("promised").EnumerateArray()).GetProperty("quantity").GetDecimal());
        Assert.Equal(2m, Assert.Single(after.GetProperty("stillShort").EnumerateArray()).GetProperty("missing").GetDecimal());

        // W runs both operations and is received: 6 are reserved for M as promised; the rest is free, and M's last 2 come from it.
        await plant.Ok(stores, "/api/inventory/issues", new { orderNumber = w, partNumber = "COIL-AL", quantity = 10m, locationCode = "STORES" });
        await plant.Ok(e, "/api/resources/shifts", new { code = "ALL", siteCode = "PLANT1", name = "Around the clock", start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 } });
        await plant.Ok(e, "/api/resources/work-centers/ASSY-WC/shifts", new { shiftCode = "ALL" });
        await plant.Ok(e, $"/api/workforce/workers/{plant.People["dana"]}/shift", new { shiftCode = "ALL" });
        await plant.Ok(plant.Admin, $"/api/workforce/people/{plant.People["dana"]}/badge", new { badge = Dana });
        await plant.Ok(kiosk, "/api/shopfloor/clock-in", new { badge = Dana });
        foreach (var sequence in new[] { 10, 20 })
        {
            await plant.Ok(kiosk, "/api/shopfloor/start", new { badge = Dana, orderNumber = w, sequence });
            await plant.Ok(kiosk, "/api/shopfloor/report", new { badge = Dana, orderNumber = w, sequence, good = 10m, scrap = 0m, complete = true });
        }

        var received = await plant.Ok(stores, "/api/inventory/production-receipts", new { orderNumber = w, quantity = 10m, locationCode = "STORES" });
        Assert.Contains($"6 reserved for order {m} as promised", received.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty((await Get(planner, "/api/allocation/sources")).GetProperty("promised").EnumerateArray());
        var last = Assert.Single((await ProposalAsync(planner, "SUB"))!.Value.GetProperty("lines").EnumerateArray());
        Assert.Equal((m, "Primary", 2m), (last.GetProperty("orderNumber").GetInt64(), last.GetProperty("mode").GetString(), last.GetProperty("quantity").GetDecimal()));
    }
}
