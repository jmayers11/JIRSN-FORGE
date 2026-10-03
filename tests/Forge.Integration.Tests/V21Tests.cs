using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Forge.Integration.Tests;

/// <summary>
/// V2.1 through the real API: item classes, manufacturer parts proposed by one person and approved by
/// another, a COTS item that cannot be bought until it has an approved manufacturer part, and the
/// supplier master with holds and approved sources.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class V21Tests(PostgresFixture postgres)
{
    private static string Day(int fromToday) => DateTime.UtcNow.AddDays(fromToday).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

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

    private static async Task Rejected(SlittingPlant.Plant plant, HttpClient client, string url, object body, string code)
    {
        var (status, text) = await Try(plant, client, url, body);
        Assert.True(status == HttpStatusCode.UnprocessableEntity, $"{url} -> {(int)status}: {text}");
        Assert.Contains(code, text, StringComparison.Ordinal);
    }

    private static int Column(JsonElement report, string key) =>
        report.GetProperty("columns").EnumerateArray().Select((c, i) => (c, i)).Single(x => x.c.GetProperty("key").GetString() == key).i;

    private static JsonElement Row(JsonElement report, string part) =>
        report.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("cells")[Column(report, "part")].GetString() == part).GetProperty("cells");

    private static object Po(string supplier, string part) => new { supplier, lines = new[] { new { partNumber = part, quantity = 10m, promisedDate = Day(7) } } };

    [Fact]
    public async Task A_cots_item_is_bought_only_as_an_approved_manufacturer_part()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("bob", "Buyer");
        var (e, approver, bob) = (plant.Engineer, plant.Approver, plant.As("bob"));
        await plant.Ok(bob, "/api/procurement/suppliers", new { code = "MOTION", name = "Motion Industries" });

        // New bought items start unclassified; made items start as Make.
        await plant.Ok(e, "/api/engineering/items", new { partNumber = "BRG-6205", description = "Ball bearing 6205", unitOfMeasure = "EA", makeBuy = "Buy", leadTimeDays = 5 });
        var items = await Get(e, "/api/engineering/items");
        Assert.Equal("Unclassified", items.EnumerateArray().Single(i => i.GetProperty("partNumber").GetString() == "BRG-6205").GetProperty("class").GetString());
        Assert.Equal("Make", items.EnumerateArray().Single(i => i.GetProperty("partNumber").GetString() == "ROLL-100").GetProperty("class").GetString());

        // A manufacturer part needs a classified, off-the-shelf item.
        var skf = new { manufacturer = "SKF", mpn = "6205-2RSH", lifecycle = "Active" };
        await Rejected(plant, e, "/api/engineering/items/BRG-6205/manufacturer-parts", skf, "item_unclassified");
        await Rejected(plant, e, "/api/engineering/items/ROLL-100/manufacturer-parts", skf, "not_manufacturer_item");

        // The class must agree with make or buy, and every classification needs a reason.
        await Rejected(plant, e, "/api/engineering/items/BRG-6205/classification", new { @class = "Make", reason = "x" }, "class_conflict");
        await Rejected(plant, e, "/api/engineering/items/ROLL-100/classification", new { @class = "Cots", reason = "x" }, "class_conflict");
        await Rejected(plant, e, "/api/engineering/items/BRG-6205/classification", new { @class = "Cots" }, "reason");
        await plant.Ok(e, "/api/engineering/items/BRG-6205/classification", new { @class = "Cots", reason = "Catalogue bearing" });
        Assert.Contains("already", (await plant.Ok(e, "/api/engineering/items/BRG-6205/classification", new { @class = "Cots", reason = "again" })).GetProperty("message").GetString(), StringComparison.Ordinal);

        // A COTS item without an approved manufacturer part cannot be ordered.
        await Rejected(plant, bob, "/api/procurement/purchase-orders", Po("MOTION", "BRG-6205"), "no_approved_manufacturer_part");

        // Proposed by one person, approved by a different person with COTS authority.
        await plant.Ok(bob, "/api/engineering/items/BRG-6205/manufacturer-parts", skf);
        await Rejected(plant, bob, "/api/engineering/items/BRG-6205/manufacturer-parts", skf, "manufacturer_part_exists");
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, bob, "/api/engineering/items/BRG-6205/manufacturer-parts/approve", new { manufacturer = "SKF", mpn = "6205-2RSH" })).Status);
        await Rejected(plant, bob, "/api/procurement/purchase-orders", Po("MOTION", "BRG-6205"), "no_approved_manufacturer_part");
        await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["bob"]}/roles", new { role = "EngineeringApprover", reason = "Covering COTS approvals" });
        await Rejected(plant, bob, "/api/engineering/items/BRG-6205/manufacturer-parts/approve", new { manufacturer = "SKF", mpn = "6205-2RSH" }, "separation_of_duties");
        await plant.Ok(approver, "/api/engineering/items/BRG-6205/manufacturer-parts/approve", new { manufacturer = "SKF", mpn = "6205-2RSH" });
        await Rejected(plant, approver, "/api/engineering/items/BRG-6205/manufacturer-parts/approve", new { manufacturer = "SKF", mpn = "6205-2RSH" }, "not_proposed");
        await plant.Ok(e, "/api/engineering/items/BRG-6205/manufacturer-parts", new { manufacturer = "NSK", mpn = "6205DDU", lifecycle = "Active" });

        var mpns = await Get(bob, "/api/engineering/manufacturer-parts?part=BRG-6205");
        var approved = mpns.EnumerateArray().Single(m => m.GetProperty("mpn").GetString() == "6205-2RSH");
        Assert.Equal("Approved", approved.GetProperty("status").GetString());
        Assert.Equal($"person:{plant.People["bob"]}", approved.GetProperty("proposedBy").GetString());
        Assert.Equal($"person:{plant.People["approver"]}", approved.GetProperty("approvedBy").GetString());
        Assert.Equal("Proposed", mpns.EnumerateArray().Single(m => m.GetProperty("mpn").GetString() == "6205DDU").GetProperty("status").GetString());

        // Now the item can be bought.
        await plant.Ok(bob, "/api/procurement/purchase-orders", Po("MOTION", "BRG-6205"));

        // A COTS item with an approved manufacturer part cannot quietly stop being COTS.
        await Rejected(plant, e, "/api/engineering/items/BRG-6205/classification", new { @class = "StandardHardware", reason = "x" }, "has_manufacturer_parts");

        // Lifecycle is what people record from the manufacturer, with a reason.
        await Rejected(plant, bob, "/api/engineering/items/BRG-6205/manufacturer-parts/lifecycle", new { manufacturer = "SKF", mpn = "6205-2RSH", lifecycle = "LastTimeBuy" }, "reason");
        await plant.Ok(bob, "/api/engineering/items/BRG-6205/manufacturer-parts/lifecycle", new { manufacturer = "skf", mpn = "6205-2RSH", lifecycle = "LastTimeBuy", reason = "SKF notice 2026-14" });

        // Unclassified items and COTS items that cannot be bought are visible in the report.
        await plant.Ok(e, "/api/engineering/items", new { partNumber = "FLT-4417", description = "Hydraulic filter", unitOfMeasure = "EA", makeBuy = "Buy", leadTimeDays = 3 });
        await plant.Ok(e, "/api/engineering/items", new { partNumber = "SNS-88", description = "Proximity sensor", unitOfMeasure = "EA", makeBuy = "Buy", leadTimeDays = 3, @class = "Cots", reason = "Catalogue sensor" });
        await Rejected(plant, e, "/api/engineering/items", new { partNumber = "BAD-1", description = "Bad", unitOfMeasure = "EA", makeBuy = "Make", leadTimeDays = 1, @class = "Cots" }, "class_conflict");
        var report = await Get(e, $"/api/reports/item-classification?from={Day(-1)}&to={Day(0)}");
        Assert.Equal("Classification", Row(report, "FLT-4417")[Column(report, "attention")].GetString());
        Assert.Equal("Approved manufacturer part", Row(report, "SNS-88")[Column(report, "attention")].GetString());
        var bearing = Row(report, "BRG-6205");
        Assert.Equal(1m, bearing[Column(report, "approvedMpns")].GetDecimal());
        Assert.Equal("LastTimeBuy", bearing[Column(report, "lifecycle")].GetString());
        Assert.Equal("Lifecycle review", bearing[Column(report, "attention")].GetString());
        var cotsOnly = await Get(e, $"/api/reports/item-classification?from={Day(-1)}&to={Day(0)}&status=Cots");
        Assert.All(cotsOnly.GetProperty("rows").EnumerateArray(), r => Assert.Equal("Cots", r.GetProperty("cells")[Column(cotsOnly, "class")].GetString()));

        // Withdrawing the last approved manufacturer part blocks buying again.
        await plant.Ok(approver, "/api/engineering/items/BRG-6205/manufacturer-parts/withdraw", new { manufacturer = "SKF", mpn = "6205-2RSH", reason = "Last-time buy; moving to NSK" });
        await Rejected(plant, bob, "/api/procurement/purchase-orders", Po("MOTION", "BRG-6205"), "no_approved_manufacturer_part");
        Assert.Equal(HttpStatusCode.NotFound, (await Try(plant, approver, "/api/engineering/items/BRG-6205/manufacturer-parts/withdraw", new { manufacturer = "ACME", mpn = "X", reason = "x" })).Status);
    }

    [Fact]
    public async Task Suppliers_holds_and_approved_sources_govern_new_orders()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("bob", "Buyer");
        await plant.Person("pat", "PurchasingManager");
        var (bob, pat) = (plant.As("bob"), plant.As("pat"));

        // A purchase order names a supplier record, found by code or exact name.
        await Rejected(plant, bob, "/api/procurement/purchase-orders", Po("Alcoa", "COIL-AL"), "unknown_supplier");
        await Rejected(plant, bob, "/api/procurement/suppliers", new { code = "al coa", name = "Alcoa" }, "invalid");
        await plant.Ok(bob, "/api/procurement/suppliers", new { code = "ALCOA", name = "Alcoa" });
        await plant.Ok(bob, "/api/procurement/suppliers", new { code = "NOVELIS", name = "Novelis" });
        await Rejected(plant, bob, "/api/procurement/suppliers", new { code = "ALCOA2", name = "alcoa" }, "supplier_name_exists");
        await Rejected(plant, bob, "/api/procurement/suppliers", new { code = "alcoa", name = "Alcoa Corp" }, "supplier_exists");
        var byName = long.Parse((await plant.Ok(bob, "/api/procurement/purchase-orders", Po("alcoa", "COIL-AL"))).GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);
        var order = (await Get(bob, "/api/procurement/purchase-orders")).EnumerateArray().Single(o => o.GetProperty("poNumber").GetInt64() == byName);
        Assert.Equal("ALCOA", order.GetProperty("supplierCode").GetString());
        Assert.Equal("Alcoa", order.GetProperty("supplier").GetString());

        // Holds need purchasing approval authority and a reason; a held supplier takes no new orders or approvals.
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, bob, "/api/procurement/suppliers/ALCOA/status", new { status = "OnHold", reason = "x" })).Status);
        await Rejected(plant, pat, "/api/procurement/suppliers/ALCOA/status", new { status = "OnHold" }, "reason");
        await plant.Ok(pat, "/api/procurement/suppliers/ALCOA/status", new { status = "OnHold", reason = "Late certificates" });
        await Rejected(plant, bob, "/api/procurement/purchase-orders", Po("ALCOA", "COIL-AL"), "supplier_not_active");
        await Rejected(plant, pat, $"/api/procurement/purchase-orders/{byName}/approve", new { reason = "Budget" }, "supplier_not_active");
        await plant.Ok(pat, "/api/procurement/suppliers/ALCOA/status", new { status = "Active", reason = "Certificates received" });
        await plant.Ok(pat, $"/api/procurement/purchase-orders/{byName}/approve", new { reason = "Budget" });

        // An item with approved sources is bought only from them; made items have none.
        await Rejected(plant, pat, "/api/procurement/items/ROLL-100/approved-sources", new { supplierCode = "ALCOA", reason = "x" }, "not_purchased");
        Assert.Equal(HttpStatusCode.NotFound, (await Try(plant, pat, "/api/procurement/items/COIL-AL/approved-sources", new { supplierCode = "NOPE", reason = "x" })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, bob, "/api/procurement/items/COIL-AL/approved-sources", new { supplierCode = "NOVELIS", reason = "x" })).Status);
        await plant.Ok(pat, "/api/procurement/items/COIL-AL/approved-sources", new { supplierCode = "NOVELIS", note = "Qualified mill", reason = "Mill audit passed" });
        await Rejected(plant, bob, "/api/procurement/purchase-orders", Po("ALCOA", "COIL-AL"), "not_approved_source");
        await plant.Ok(bob, "/api/procurement/purchase-orders", Po("NOVELIS", "COIL-AL"));
        var sources = await Get(bob, "/api/procurement/approved-sources?part=COIL-AL");
        Assert.Equal("NOVELIS", Assert.Single(sources.EnumerateArray()).GetProperty("supplierCode").GetString());

        await plant.Ok(pat, "/api/procurement/items/COIL-AL/approved-sources/NOVELIS/remove", new { reason = "Mill closed" });
        await plant.Ok(bob, "/api/procurement/purchase-orders", Po("ALCOA", "COIL-AL"));

        var suppliers = await Get(bob, "/api/procurement/suppliers");
        Assert.Equal("ALCOA,NOVELIS", string.Join(",", suppliers.EnumerateArray().Select(s => s.GetProperty("code").GetString()).Order(StringComparer.Ordinal)));
    }
}
