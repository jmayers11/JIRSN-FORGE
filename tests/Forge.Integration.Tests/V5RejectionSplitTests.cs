using System.Globalization;
using System.Net;
using System.Text.Json;
using Npgsql;

namespace Forge.Integration.Tests;

/// <summary>
/// ADR 0055 (owner rulings, 2026-10-18) through the real API: a supplier rejection is decided per quantity. Part of a
/// rejection can be returned to the vendor, the rest stays on hold as its own pending rejection; an open return to
/// vendor can be reduced, never increased; nothing leaves quality hold without a disposition, and every unit rejected
/// is accounted for by exactly one rejection.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class V5RejectionSplitTests(PostgresFixture postgres)
{
    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    private static async Task Rejected(SlittingPlant.Plant plant, HttpClient client, string url, object body, string code)
    {
        var response = await plant.Try(client, url, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{url} -> {(int)response.StatusCode}: {text}");
        Assert.Contains(code, text, StringComparison.Ordinal);
    }

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static async Task<decimal> Stock(HttpClient client, string status) =>
        (await Get(client, "/api/inventory/stock?part=COIL-AL")).EnumerateArray()
            .Where(b => b.GetProperty("locationCode").GetString() == "STORES" && b.GetProperty("status").GetString() == status)
            .Sum(b => b.GetProperty("quantity").GetDecimal());

    private static async Task<JsonElement[]> Rejections(HttpClient client, bool open) =>
        (await Get(client, $"/api/inventory/rejections?open={(open ? "true" : "false")}")).EnumerateArray().ToArray();

    [Fact]
    public async Task Part_of_a_rejection_goes_back_and_the_rest_needs_its_own_decision_while_an_open_return_only_goes_down()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        foreach (var (who, role) in new[] { ("insp", "Inspector"), ("qe", "QualityEngineer"), ("qm", "QualityManager") })
        {
            await plant.Person(who, role);
        }

        var (stores, insp, qe, qm) = (plant.As("stores"), plant.As("insp"), plant.As("qe"), plant.As("qm"));
        var released = await Stock(stores, "Released");
        var receipt = Id(await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "COIL-AL", quantity = 50m, locationCode = "STORES", inspect = true, reason = "Delivery with damage" }));
        await plant.Ok(insp, $"/api/inventory/receiving/{receipt}/inspections", new { accepted = 20m, rejected = 30m, defect = "Edge damage on part of the coil" });
        var first = Assert.Single(await Rejections(qe, open: true));
        var r1 = first.GetProperty("rejectionId").GetInt64();
        var inspection = first.GetProperty("inspectionId").GetInt64();
        Assert.Equal((released + 20m, 30m), (await Stock(stores, "Released"), await Stock(stores, "Hold")));

        // A disposition never covers more than was rejected, nor nothing.
        await Rejected(plant, qe, $"/api/inventory/rejections/{r1}/disposition", new { disposition = "ReturnToVendor", quantity = 31m, reason = "Too much" }, "over_rejected_quantity");
        await Rejected(plant, qe, $"/api/inventory/rejections/{r1}/disposition", new { disposition = "ReturnToVendor", quantity = 0m, reason = "Nothing" }, "invalid_quantity");

        // Return 20; the other 10 stay on hold as their own pending rejection, split from the first.
        await plant.Ok(qe, $"/api/inventory/rejections/{r1}/disposition", new { disposition = "ReturnToVendor", quantity = 20m, reason = "Damaged section goes back" });
        var r2 = Assert.Single(await Rejections(qe, open: true));
        Assert.Equal((10m, r1, "Pending"), (r2.GetProperty("quantity").GetDecimal(), r2.GetProperty("splitFrom").GetInt64(), r2.GetProperty("disposition").GetString()));
        var rtv = Assert.Single((await Get(stores, "/api/inventory/rtvs?open=true")).EnumerateArray());
        var rtvId = rtv.GetProperty("rtvId").GetInt64();
        Assert.Equal((20m, r1), (rtv.GetProperty("quantity").GetDecimal(), rtv.GetProperty("rejectionId").GetInt64()));
        Assert.Equal(30m, await Stock(stores, "Hold"));
        await Rejected(plant, qe, $"/api/inventory/rejections/{r1}/disposition", new { disposition = "Scrap", reason = "Changed my mind" }, "already_dispositioned");

        // An open return only goes down, by quality disposition authority; the reduced part stays on hold, pending.
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(stores, $"/api/inventory/rtvs/{rtvId}/reduce", new { quantity = 16m, reason = "x" })).StatusCode);
        await Rejected(plant, qe, $"/api/inventory/rtvs/{rtvId}/reduce", new { quantity = 20m, reason = "Same" }, "rtv_quantity_not_reduced");
        await Rejected(plant, qe, $"/api/inventory/rtvs/{rtvId}/reduce", new { quantity = 25m, reason = "More" }, "rtv_quantity_not_reduced");
        await Rejected(plant, qe, $"/api/inventory/rtvs/{rtvId}/reduce", new { quantity = 16m }, "reason_required");
        await plant.Ok(qe, $"/api/inventory/rtvs/{rtvId}/reduce", new { quantity = 16m, reason = "Re-check: 4 more are within tolerance" });
        var open = await Rejections(qe, open: true);
        Assert.Equal(2, open.Length);
        var r3 = open.Single(r => r.GetProperty("rejectionId").GetInt64() != r2.GetProperty("rejectionId").GetInt64());
        Assert.Equal((4m, r1), (r3.GetProperty("quantity").GetDecimal(), r3.GetProperty("splitFrom").GetInt64()));
        Assert.Equal(16m, (await Get(stores, "/api/inventory/rtvs?open=true")).EnumerateArray().Single().GetProperty("quantity").GetDecimal());
        Assert.Equal(30m, await Stock(stores, "Hold"));

        // The remainders take their own dispositions: use as is (a second person approves) and scrap.
        var r2Id = r2.GetProperty("rejectionId").GetInt64();
        var r3Id = r3.GetProperty("rejectionId").GetInt64();
        await plant.Ok(qe, $"/api/inventory/rejections/{r2Id}/disposition", new { disposition = "UseAsIs", reason = "Cosmetic only" });
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(qe, $"/api/inventory/rejections/{r2Id}/approve", new { reason = "Mine" })).StatusCode);
        await plant.Ok(qm, $"/api/inventory/rejections/{r2Id}/approve", new { reason = "Agreed: cosmetic" });
        await plant.Ok(qe, $"/api/inventory/rejections/{r3Id}/disposition", new { disposition = "Scrap", reason = "Not worth returning" });
        Assert.Equal((released + 30m, 16m), (await Stock(stores, "Released"), await Stock(stores, "Hold")));

        await plant.Ok(stores, $"/api/inventory/rtvs/{rtvId}/ship", new { carrier = "Freight" });
        await Rejected(plant, qe, $"/api/inventory/rtvs/{rtvId}/reduce", new { quantity = 10m, reason = "Too late" }, "rtv_not_open");
        Assert.Equal((released + 30m, 0m, 0m), (await Stock(stores, "Released"), await Stock(stores, "Hold"), await Stock(stores, "Inspection")));
        Assert.Empty(await Rejections(qe, open: true));

        // Every rejected unit is in exactly one rejection: 16 returned + 10 used as is + 4 scrapped = 30.
        var all = (await Rejections(qe, open: false)).Where(r => r.GetProperty("inspectionId").GetInt64() == inspection).ToArray();
        Assert.Equal(30m, all.Sum(r => r.GetProperty("quantity").GetDecimal()));
        Assert.Equal("ReturnToVendor,Scrap,UseAsIs", string.Join(',', all.Select(r => r.GetProperty("disposition").GetString()!).Order(StringComparer.Ordinal)));

        // The database refuses what the service never does: raising a rejection, or changing a return once shipped.
        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync();
        await using (var raise = new NpgsqlCommand("UPDATE inventory.supplier_rejection SET quantity = quantity + 1 WHERE rejection_id = $1", connection) { Parameters = { new() { Value = r1 } } })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => raise.ExecuteNonQueryAsync());
            Assert.Contains("only be reduced", error.MessageText, StringComparison.Ordinal);
        }

        await using (var shrink = new NpgsqlCommand("UPDATE inventory.rtv SET quantity = quantity - 1 WHERE rtv_id = $1", connection) { Parameters = { new() { Value = rtvId } } })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => shrink.ExecuteNonQueryAsync());
            Assert.Contains("only an open return can be reduced", error.MessageText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_disposition_without_a_quantity_still_decides_the_whole_rejection()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("insp", "Inspector");
        await plant.Person("qe", "QualityEngineer");
        var (stores, insp, qe) = (plant.As("stores"), plant.As("insp"), plant.As("qe"));
        var receipt = Id(await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "COIL-AL", quantity = 12m, locationCode = "STORES", inspect = true, reason = "Delivery" }));
        await plant.Ok(insp, $"/api/inventory/receiving/{receipt}/inspections", new { accepted = 4m, rejected = 8m, defect = "Wrong temper" });
        var r1 = Assert.Single(await Rejections(qe, open: true)).GetProperty("rejectionId").GetInt64();
        await plant.Ok(qe, $"/api/inventory/rejections/{r1}/disposition", new { disposition = "ReworkBySupplier", reason = "Supplier re-tempers" });
        Assert.Empty(await Rejections(qe, open: true));
        var rtv = Assert.Single((await Get(stores, "/api/inventory/rtvs?open=true")).EnumerateArray());
        Assert.Equal((8m, true), (rtv.GetProperty("quantity").GetDecimal(), rtv.GetProperty("forRework").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, Assert.Single(await Rejections(qe, open: false)).GetProperty("splitFrom").ValueKind);
    }
}
