using System.Net;
using System.Text.Json;

namespace Forge.Integration.Tests;

/// <summary>
/// V3.2 through the real API: a lot, roll or batch keeps its identity through every move; splitting it creates new
/// instances whose quantities add up to what left it; serials are never split; merging happens only where the
/// part's merge policy allows, within what it says the sources must share; and every split and merge is a link
/// that the lineage of an instance shows.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class V32Tests(PostgresFixture postgres)
{
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

    private static async Task<decimal> Balance(HttpClient client, string part, string lot, string location)
    {
        var rows = (await Get(client, $"/api/inventory/stock?part={part}")).EnumerateArray()
            .Where(b => b.GetProperty("lotNumber").GetString() == lot && b.GetProperty("locationCode").GetString() == location);
        return rows.Sum(b => b.GetProperty("quantity").GetDecimal());
    }

    private static async Task<(SlittingPlant.Plant Plant, HttpClient Stores, HttpClient Mo)> SetUpAsync(ForgeApp app)
    {
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("mo", "MaterialHandler");
        foreach (var (part, serialized) in new[] { ("FOIL-1", false), ("CTRL-9", true) })
        {
            await plant.Ok(plant.Engineer, "/api/engineering/items", new { partNumber = part, description = part, unitOfMeasure = serialized ? "EA" : "M", makeBuy = "Buy", leadTimeDays = 5, lotTracked = !serialized, serialized });
            await plant.Ok(plant.Engineer, $"/api/engineering/items/{part}/revisions", new { revision = "A" });
            await plant.Ok(plant.Approver, $"/api/engineering/items/{part}/revisions/A/release", new { reason = "Baseline" });
        }

        var stores = plant.As("stores");
        await plant.Ok(stores, "/api/inventory/locations", new { code = "WH1", siteCode = "PLANT1", name = "Warehouse 1", kind = "Stores" });
        await plant.Ok(stores, "/api/inventory/locations", new { code = "SL-B-SIDE", siteCode = "PLANT1", name = "Beside slitter B", kind = "MachineSide", machineCode = "SLITTER-B" });
        return (plant, stores, plant.As("mo"));
    }

    [Fact]
    public async Task A_split_creates_new_instances_that_add_up_and_identity_survives_moves()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, stores, mo) = await SetUpAsync(app);
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "FOIL-1", quantity = 1000m, locationCode = "WH1", lotNumber = "R-100", supplierLot = "MILL-7", revision = "A", identityKind = "Roll", reason = "Opening balance" });
        await Rejected(plant, stores, "/api/inventory/receipts", new { partNumber = "FOIL-1", quantity = 1m, locationCode = "WH1", lotNumber = "R-101", identityKind = "Serial", reason = "x" }, "invalid_identity_kind");

        // A move keeps the instance: same number, same kind, same revision.
        await plant.Ok(mo, "/api/inventory/transfers", new { partNumber = "FOIL-1", quantity = 1000m, fromLocation = "WH1", toLocation = "SL-B-SIDE", lotNumber = "R-100", status = "Released", reason = "To the slitter" });
        var lot = (await Get(mo, "/api/inventory/lots?part=FOIL-1")).EnumerateArray().Single(l => l.GetProperty("lotNumber").GetString() == "R-100");
        Assert.Equal(("Roll", "A", "MILL-7"), (lot.GetProperty("identityKind").GetString(), lot.GetProperty("partRevision").GetString(), lot.GetProperty("supplierLot").GetString()));

        // Split 1000 into 900, 70 and 30: the source keeps nothing, the three add up exactly.
        object Split(params (string Lot, decimal Qty)[] results) => new
        {
            partNumber = "FOIL-1", lotNumber = "R-100", locationCode = "SL-B-SIDE", status = "Released",
            results = results.Select(r => new { lotNumber = r.Lot, quantity = r.Qty }).ToArray(), reason = "Slit to width",
        };
        await Rejected(plant, mo, "/api/inventory/splits", Split(("R-100-A", 900m), ("R-100-B", 200m)), "insufficient_stock");
        await Rejected(plant, mo, "/api/inventory/splits", Split(("R-100", 10m)), "lot_exists");
        await Rejected(plant, mo, "/api/inventory/splits", Split(("R-100-A", 10m), ("R-100-A", 10m)), "lot_exists");
        await Rejected(plant, mo, "/api/inventory/splits", new { partNumber = "FOIL-1", lotNumber = "R-100", locationCode = "SL-B-SIDE", results = new[] { new { lotNumber = "R-100-A", quantity = 1m } } }, "reason_required");
        await plant.Ok(mo, "/api/inventory/splits", Split(("R-100-A", 900m), ("R-100-B", 70m), ("R-100-C", 30m)));

        Assert.Equal(0m, await Balance(mo, "FOIL-1", "R-100", "SL-B-SIDE"));
        Assert.Equal((900m, 70m, 30m), (await Balance(mo, "FOIL-1", "R-100-A", "SL-B-SIDE"), await Balance(mo, "FOIL-1", "R-100-B", "SL-B-SIDE"), await Balance(mo, "FOIL-1", "R-100-C", "SL-B-SIDE")));
        var a = (await Get(mo, "/api/inventory/lots?part=FOIL-1")).EnumerateArray().Single(l => l.GetProperty("lotNumber").GetString() == "R-100-A");
        Assert.Equal(("Roll", "A", "MILL-7"), (a.GetProperty("identityKind").GetString(), a.GetProperty("partRevision").GetString(), a.GetProperty("supplierLot").GetString()));
        var ledger = (await Get(mo, "/api/inventory/transactions?part=FOIL-1")).EnumerateArray().Where(t => t.GetProperty("kind").GetString() == "Split").ToList();
        Assert.Equal(4, ledger.Count); // one out of the source, one into each result

        // Lineage both ways; the second split's result goes back to the first roll.
        await plant.Ok(mo, "/api/inventory/splits", new { partNumber = "FOIL-1", lotNumber = "R-100-A", locationCode = "SL-B-SIDE", status = "Released", results = new[] { new { lotNumber = "R-100-A1", quantity = 450m } }, reason = "Half for job 2" });
        var source = await Get(mo, "/api/inventory/lots/FOIL-1/R-100/lineage");
        Assert.Equal("R-100-A,R-100-B,R-100-C", string.Join(",", source.GetProperty("results").EnumerateArray().Select(l => l.GetProperty("resultLot").GetString())));
        Assert.Empty(source.GetProperty("sources").EnumerateArray());
        var grandchild = await Get(mo, "/api/inventory/lots/FOIL-1/R-100-A1/lineage");
        Assert.Equal("R-100-A", Assert.Single(grandchild.GetProperty("sources").EnumerateArray()).GetProperty("sourceLot").GetString());
        Assert.Equal("R-100", string.Join(",", grandchild.GetProperty("origins").EnumerateArray().Select(o => o.GetString())));
        Assert.Equal(450m, await Balance(mo, "FOIL-1", "R-100-A", "SL-B-SIDE"));
        var report = await Get(mo, "/api/reports/lot-lineage?search=R-100-A1");
        var row = Assert.Single(report.GetProperty("rows").EnumerateArray());
        Assert.Equal(("Split", "R-100-A", 450m), (row.GetProperty("cells")[1].GetString(), row.GetProperty("cells")[3].GetString(), row.GetProperty("cells")[5].GetDecimal()));
        var missing = await mo.GetAsync(new Uri("/api/inventory/lots/FOIL-1/NOPE/lineage", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        // A serial is one unit: never split.
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "CTRL-9", quantity = 1m, locationCode = "WH1", lotNumber = "SN-1", reason = "Opening balance" });
        Assert.Equal("Serial", (await Get(mo, "/api/inventory/lots?part=CTRL-9")).EnumerateArray().Single().GetProperty("identityKind").GetString());
        await Rejected(plant, mo, "/api/inventory/splits", new { partNumber = "CTRL-9", lotNumber = "SN-1", locationCode = "WH1", results = new[] { new { lotNumber = "SN-1A", quantity = 1m } }, reason = "x" }, "not_divisible");
    }

    [Fact]
    public async Task Merging_needs_a_policy_and_what_it_says_the_sources_share()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, stores, mo) = await SetUpAsync(app);
        foreach (var (lot, supplierLot, quantity) in new[] { ("R-1", "MILL-1", 40m), ("R-2", "MILL-1", 25m), ("R-3", "MILL-2", 10m) })
        {
            await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "FOIL-1", quantity, locationCode = "WH1", lotNumber = lot, supplierLot, revision = "A", identityKind = "Roll", reason = "Opening balance" });
        }

        object Merge(string resultLot, params (string Lot, decimal Qty)[] sources) => new
        {
            partNumber = "FOIL-1", resultLot, locationCode = "WH1", status = "Released",
            sources = sources.Select(s => new { lotNumber = s.Lot, quantity = s.Qty }).ToArray(), reason = "Consolidate short rolls",
        };

        // No policy: never.
        await Rejected(plant, mo, "/api/inventory/merges", Merge("R-9", ("R-1", 40m), ("R-2", 25m)), "merge_not_allowed");
        Assert.False((await Get(mo, "/api/inventory/merge-policy/FOIL-1")).GetProperty("allowed").GetBoolean());

        // Only inventory approval authority sets one, with a reason; it says the sources share a supplier lot.
        var policy = new { allowed = true, sameRevisionRequired = true, sameSupplierLotRequired = true, reason = "Short rolls of one mill lot may be combined" };
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(mo, "/api/inventory/merge-policy/FOIL-1", policy)).StatusCode);
        await Rejected(plant, stores, "/api/inventory/merge-policy/FOIL-1", policy with { reason = "" }, "reason_required");
        await plant.Ok(stores, "/api/inventory/merge-policy/FOIL-1", policy);
        await Rejected(plant, stores, "/api/inventory/merge-policy/CTRL-9", policy, "not_divisible");

        await Rejected(plant, mo, "/api/inventory/merges", Merge("R-9", ("R-1", 40m), ("R-3", 10m)), "supplier_lot_mismatch");
        await Rejected(plant, mo, "/api/inventory/merges", Merge("R-9", ("R-1", 41m)), "insufficient_stock");
        await Rejected(plant, mo, "/api/inventory/merges", Merge("R-1", ("R-1", 40m)), "invalid_sources");
        await plant.Ok(mo, "/api/inventory/merges", Merge("R-9", ("R-1", 40m), ("R-2", 25m)));

        Assert.Equal((0m, 0m, 65m), (await Balance(mo, "FOIL-1", "R-1", "WH1"), await Balance(mo, "FOIL-1", "R-2", "WH1"), await Balance(mo, "FOIL-1", "R-9", "WH1")));
        var merged = (await Get(mo, "/api/inventory/lots?part=FOIL-1")).EnumerateArray().Single(l => l.GetProperty("lotNumber").GetString() == "R-9");
        Assert.Equal(("Roll", "A", "MILL-1"), (merged.GetProperty("identityKind").GetString(), merged.GetProperty("partRevision").GetString(), merged.GetProperty("supplierLot").GetString()));
        var lineage = await Get(mo, "/api/inventory/lots/FOIL-1/R-9/lineage");
        Assert.Equal("R-1,R-2", string.Join(",", lineage.GetProperty("sources").EnumerateArray().Select(l => l.GetProperty("sourceLot").GetString()).Order()));
        Assert.All(lineage.GetProperty("sources").EnumerateArray(), l => Assert.Equal("Merge", l.GetProperty("relation").GetString()));
        Assert.Equal("R-1,R-2", string.Join(",", lineage.GetProperty("origins").EnumerateArray().Select(o => o.GetString())));

        // Turning it off stops the next merge; the earlier setting is kept.
        await plant.Ok(stores, "/api/inventory/merge-policy/FOIL-1", new { allowed = false, sameRevisionRequired = true, sameSupplierLotRequired = true, reason = "Customer wants mill lots kept apart" });
        await Rejected(plant, mo, "/api/inventory/merges", Merge("R-9", ("R-3", 10m)), "merge_not_allowed");
    }
}
