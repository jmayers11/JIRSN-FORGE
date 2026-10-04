using System.Globalization;
using System.Net;
using System.Text.Json;
using Npgsql;

namespace Forge.Integration.Tests;

/// <summary>
/// V2.7 through the real API: demand of every kind names its source; requisitions from MRP carry the source
/// their pegging leads to, manual ones carry what the requester said; purchase order lines keep the source,
/// and every line, receipt and issue answers "why was this purchased?" (scenario 6).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class V27Tests(PostgresFixture postgres)
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

    private static async Task Rejected(SlittingPlant.Plant plant, HttpClient client, string url, object body, string code, HttpStatusCode status = HttpStatusCode.UnprocessableEntity)
    {
        var (actual, text) = await Try(plant, client, url, body);
        Assert.True(actual == status, $"{url} -> {(int)actual}: {text}");
        Assert.Contains(code, text, StringComparison.Ordinal);
    }

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static async Task BoughtAsync(SlittingPlant.Plant plant, string part, bool lotTracked = true)
    {
        await plant.Ok(plant.Engineer, "/api/engineering/items", new { partNumber = part, description = part, unitOfMeasure = "EA", makeBuy = "Buy", leadTimeDays = 10, lotTracked });
        await plant.Ok(plant.Engineer, $"/api/engineering/items/{part}/revisions", new { revision = "A" });
        await plant.Ok(plant.Approver, $"/api/engineering/items/{part}/revisions/A/release", new { reason = "Baseline" });
    }

    private static async Task<long> RepairAsync(SlittingPlant.Plant plant, HttpClient mp)
    {
        await plant.Ok(plant.Engineer, "/api/maintenance/machines/SLITTER-B/down", new { description = "Rewind motor bearing noisy" });
        return (await Get(mp, "/api/maintenance/work-orders")).EnumerateArray().Select(v => v.GetProperty("order"))
            .Single(o => o.GetProperty("kind").GetString() == "Corrective").GetProperty("workOrderId").GetInt64();
    }

    private static async Task<JsonElement> Requisition(HttpClient client, long id) =>
        (await Get(client, "/api/procurement/requisitions?open=false")).EnumerateArray().Single(r => r.GetProperty("requisitionId").GetInt64() == id);

    private static async Task<JsonElement> Why(HttpClient client, long po, int line) => await Get(client, $"/api/procurement/purchase-orders/{po}/lines/{line}/why");

    [Fact]
    public async Task Every_kind_of_demand_keeps_its_source_from_the_plan_to_the_purchase_order_receipt_and_issue()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("mp", "MaintenancePlanner");
        await plant.Person("tech", "MaintenanceTechnician");
        await plant.Person("bob", "Buyer");
        await plant.Person("pat", "Buyer", "PurchasingManager");
        var (mp, tech, bob, pat, stores) = (plant.As("mp"), plant.As("tech"), plant.As("bob"), plant.As("pat"), plant.As("stores"));
        await plant.Ok(bob, "/api/procurement/suppliers", new { code = "ACME", name = "Acme Industrial" });
        var repair = await RepairAsync(plant, mp);

        var kinds = new (string Kind, string? Key)[]
        {
            ("Maintenance", $"work-order:{repair}"),
            ("MroReplenishment", "mro:SP-1"),
            ("FacilityProject", "project:FAC-2027-014/3"),
            ("CapitalProject", "project:CAP-9"),
            ("Quality", "ncr:12"),
            ("Tooling", "tool:DIE-4"),
            ("Engineering", "ecn:ECN-0002"),
            ("CustomerOrder", null),
        };
        for (var i = 0; i < kinds.Length; i++)
        {
            await BoughtAsync(plant, $"SP-{i}");
        }

        // Demand from maintenance on names what it is for; contract delivery comes from contracts only.
        var demandUrl = "/api/planning/demand";
        await Rejected(plant, planner, demandUrl, new { partNumber = "SP-0", quantity = 4m, needDate = Day(30), priority = 3, kind = "Maintenance" }, "source_required");
        await Rejected(plant, planner, demandUrl, new { partNumber = "SP-0", quantity = 4m, needDate = Day(30), priority = 3, kind = "Maintenance", sourceKey = "repair 9" }, "invalid_source");
        await Rejected(plant, planner, demandUrl, new { partNumber = "SP-0", quantity = 4m, needDate = Day(30), priority = 3, kind = "ContractDelivery" }, "use_contract");
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, tech, demandUrl, new { partNumber = "SP-0", quantity = 4m, needDate = Day(30), priority = 3, kind = "Maintenance", sourceKey = $"work-order:{repair}" })).Status);

        var demands = new long[kinds.Length];
        for (var i = 0; i < kinds.Length; i++)
        {
            var who = kinds[i].Kind is "Maintenance" or "MroReplenishment" ? mp : planner;
            demands[i] = Id(await plant.Ok(who, demandUrl, new { partNumber = $"SP-{i}", quantity = 4m + i, needDate = Day(30), priority = 3, kind = kinds[i].Kind, sourceKey = kinds[i].Key, customer = kinds[i].Key is null ? "Acme Rolls" : null }));
        }

        await Rejected(plant, mp, demandUrl, new { partNumber = "SP-0", quantity = 1m, needDate = Day(30), priority = 3, kind = "Maintenance", sourceKey = $"Work-Order:{repair}" }, "source_exists");

        // MRP plans each purchase; a requisition from the plan carries the source its pegging leads back to.
        var mrpUrl = "/api/planning/mrp/requisitions";
        await Rejected(plant, planner, mrpUrl, new { plannedOrder = "PLN-NOPE-001" }, "planned_order_not_found", HttpStatusCode.NotFound);
        var requisitions = new long[kinds.Length];
        for (var i = 0; i < kinds.Length; i++)
        {
            requisitions[i] = Id(await plant.Ok(planner, mrpUrl, new { plannedOrder = $"PLN-SP-{i}-001", supplierCode = "ACME" }));
            var r = await Requisition(planner, requisitions[i]);
            Assert.Equal((kinds[i].Kind, kinds[i].Key ?? $"demand:{demands[i]}", demands[i], 4m + i, "Open"),
                (r.GetProperty("sourceKind").GetString(), r.GetProperty("sourceKey").GetString(), r.GetProperty("demandId").GetInt64(), r.GetProperty("quantity").GetDecimal(), r.GetProperty("status").GetString()));
            Assert.Equal($"PLN-SP-{i}-001", r.GetProperty("plannedOrder").GetString());
        }

        await Rejected(plant, planner, mrpUrl, new { plannedOrder = "PLN-SP-0-001" }, "requisition_exists");

        // The buyer orders them; each line keeps its requisition's source and the requisition is ordered.
        var lines = Enumerable.Range(0, kinds.Length).Select(i => new { partNumber = $"SP-{i}", quantity = 4m + i, promisedDate = Day(20), requisitionId = (long?)requisitions[i] }).ToArray();
        await Rejected(plant, bob, "/api/procurement/purchase-orders", new { supplier = "ACME", lines = new[] { new { partNumber = "SP-1", quantity = 1m, promisedDate = Day(20), requisitionId = (long?)requisitions[0] } } }, "requisition_part");
        await Rejected(plant, bob, "/api/procurement/purchase-orders", new { supplier = "ACME", lines = new[] { lines[0], lines[0] with { } } }, "requisition_not_open");
        await Rejected(plant, bob, "/api/procurement/purchase-orders", new { supplier = "ACME", lines = new[] { new { partNumber = "SP-0", quantity = 1m, promisedDate = Day(20), requisitionId = (long?)99999 } } }, "requisition_not_found", HttpStatusCode.NotFound);
        var po = Id(await plant.Ok(bob, "/api/procurement/purchase-orders", new { supplier = "ACME", lines }));
        Assert.Equal(("Ordered", po, 1), ((await Requisition(bob, requisitions[0])).GetProperty("status").GetString(), (await Requisition(bob, requisitions[0])).GetProperty("poNumber").GetInt64(), (await Requisition(bob, requisitions[0])).GetProperty("poLine").GetInt32()));
        Assert.DoesNotContain((await Get(bob, "/api/procurement/requisitions")).EnumerateArray(), r => requisitions.Contains(r.GetProperty("requisitionId").GetInt64()));
        await plant.Ok(pat, $"/api/procurement/purchase-orders/{po}/approve", new { reason = "Planned needs" });
        var order = (await Get(bob, "/api/procurement/purchase-orders")).EnumerateArray().Single(o => o.GetProperty("poNumber").GetInt64() == po);
        foreach (var line in order.GetProperty("lines").EnumerateArray())
        {
            var i = line.GetProperty("lineNumber").GetInt32() - 1;
            Assert.Equal((requisitions[i], kinds[i].Kind, kinds[i].Key ?? $"demand:{demands[i]}"),
                (line.GetProperty("requisitionId").GetInt64(), line.GetProperty("sourceKind").GetString(), line.GetProperty("sourceKey").GetString()));
        }

        // Receive each line into its own lot; the maintenance spares go to the repair.
        for (var i = 0; i < kinds.Length; i++)
        {
            await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = $"SP-{i}", quantity = 4m + i, locationCode = "STORES", lotNumber = $"L{i}", poNumber = po, poLine = i + 1 });
        }

        await plant.Ok(tech, $"/api/maintenance/work-orders/{repair}/parts", new { partNumber = "SP-0", quantity = 3m, locationCode = "STORES", lotNumber = "L0" });

        // Why was each line purchased: source, requisition, demand, what arrived and where it went.
        for (var i = 0; i < kinds.Length; i++)
        {
            var why = await Why(bob, po, i + 1);
            var key = kinds[i].Key ?? $"demand:{demands[i]}";
            Assert.Equal((kinds[i].Kind, key, requisitions[i], demands[i]),
                (why.GetProperty("sourceKind").GetString(), why.GetProperty("sourceKey").GetString(), why.GetProperty("requisition").GetProperty("requisitionId").GetInt64(), why.GetProperty("demand").GetProperty("demandId").GetInt64()));
            Assert.Equal(4m + i, Assert.Single(why.GetProperty("receipts").EnumerateArray()).GetProperty("quantity").GetDecimal());
            var answer = why.GetProperty("answer").GetString()!;
            Assert.Contains(key, answer, StringComparison.Ordinal);
            Assert.Contains($"lot L{i}", answer, StringComparison.Ordinal);
            Assert.Contains(i == 0 ? $"3 issued to work order {repair}" : "Still in stock.", answer, StringComparison.Ordinal);
        }

        // From the issue itself, back to why it was bought.
        var ledger = await Get(mp, "/api/inventory/transactions?part=SP-0&take=100");
        var issue = ledger.EnumerateArray().Single(t => t.GetProperty("kind").GetString() == "MaintenanceIssue").GetProperty("transactionId").GetInt64();
        var fromIssue = await Get(mp, $"/api/inventory/transactions/{issue}/why?part=SP-0");
        var purchase = Assert.Single(fromIssue.GetProperty("purchases").EnumerateArray());
        Assert.Equal((po, 1, "Maintenance"), (purchase.GetProperty("poNumber").GetInt64(), purchase.GetProperty("lineNumber").GetInt32(), purchase.GetProperty("sourceKind").GetString()));
        Assert.Contains($"work-order:{repair}", fromIssue.GetProperty("answer").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await mp.GetAsync(new Uri($"/api/inventory/transactions/{issue}/why?part=SP-1", UriKind.Relative))).StatusCode);
        await plant.Person("tc", "TrainingCoordinator");
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.As("tc").GetAsync(new Uri($"/api/procurement/purchase-orders/{po}/lines/1/why", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task Manual_requisitions_are_validated_cancelled_only_by_their_requester_or_a_buyer_and_reopen_when_their_line_goes()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("mp", "MaintenancePlanner");
        await plant.Person("qe", "QualityEngineer");
        await plant.Person("tech", "MaintenanceTechnician");
        await plant.Person("bob", "Buyer");
        await plant.Person("pat", "Buyer", "PurchasingManager");
        var (mp, qe, tech, bob, pat, stores) = (plant.As("mp"), plant.As("qe"), plant.As("tech"), plant.As("bob"), plant.As("pat"), plant.As("stores"));
        await plant.Ok(bob, "/api/procurement/suppliers", new { code = "ACME", name = "Acme Industrial" });
        await BoughtAsync(plant, "BRG-6204", lotTracked: false);
        await BoughtAsync(plant, "GAUGE-PIN");
        var repair = await RepairAsync(plant, mp);

        var url = "/api/procurement/requisitions";
        object Ask(string part = "BRG-6204", decimal quantity = 2m, string? key = null, string label = "Bearing for the rewind motor repair", string? revision = null, string? supplier = null) =>
            new { partNumber = part, quantity, needDate = Day(14), sourceKind = "Maintenance", sourceKey = key ?? $"work-order:{repair}", sourceLabel = label, requiredRevision = revision, supplierCode = supplier };
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, tech, url, Ask())).Status);
        await Rejected(plant, mp, url, Ask(part: "ROLL-100"), "not_purchased");
        await Rejected(plant, mp, url, Ask(quantity: 0m), "invalid_quantity");
        await Rejected(plant, mp, url, Ask(key: "the motor"), "invalid_source");
        await Rejected(plant, mp, url, Ask(label: " "), "source_required");
        await Rejected(plant, mp, url, Ask(revision: "Z"), "revision_not_found", HttpStatusCode.NotFound);
        await Rejected(plant, mp, url, Ask(supplier: "NOBODY"), "unknown_supplier");
        var bearing = Id(await plant.Ok(mp, url, Ask(revision: "A", supplier: "ACME")));
        await Rejected(plant, mp, url, Ask(), "requisition_exists");
        var pins = Id(await plant.Ok(qe, url, new { partNumber = "GAUGE-PIN", quantity = 5m, needDate = Day(10), sourceKind = "Quality", sourceKey = "ncr:7", sourceLabel = "Replacement gauge pins after NCR 7" }));
        var r = await Requisition(mp, bearing);
        Assert.Equal(("Open", "A", "ACME", "Maintenance"), (r.GetProperty("status").GetString(), r.GetProperty("requiredRevision").GetString(), r.GetProperty("supplierCode").GetString(), r.GetProperty("sourceKind").GetString()));

        // Only the requester or a buyer cancels, with a reason.
        await Rejected(plant, qe, $"{url}/{bearing}/cancel", new { reason = "Not mine" }, "not_yours");
        await Rejected(plant, qe, $"{url}/{pins}/cancel", new { }, "reason");
        await Rejected(plant, qe, $"{url}/99999/cancel", new { reason = "x" }, "requisition_not_found", HttpStatusCode.NotFound);

        // A cancelled line or draft order gives its requisition back to the queue.
        await plant.Ok(qe, "/api/planning/demand", new { partNumber = "GAUGE-PIN", quantity = 2m, needDate = Day(40), priority = 4, kind = "Quality", sourceKey = "ncr:8" });
        var po1 = Id(await plant.Ok(bob, "/api/procurement/purchase-orders", new { supplier = "ACME", lines = new[] { new { partNumber = "BRG-6204", quantity = 2m, promisedDate = Day(12), requisitionId = (long?)bearing } } }));
        await Rejected(plant, mp, $"{url}/{bearing}/cancel", new { reason = "Changed my mind" }, "requisition_not_open");
        await plant.Ok(bob, $"/api/procurement/purchase-orders/{po1}/lines", new { partNumber = "GAUGE-PIN", quantity = 5m, promisedDate = Day(12), requisitionId = (long?)pins });
        await plant.Ok(bob, $"/api/procurement/purchase-orders/{po1}/lines/1/cancel", new { reason = "Wrong supplier" });
        r = await Requisition(mp, bearing);
        Assert.Equal(("Open", JsonValueKind.Null), (r.GetProperty("status").GetString(), r.GetProperty("poNumber").ValueKind));
        Assert.Equal("Ordered", (await Requisition(qe, pins)).GetProperty("status").GetString());
        await plant.Ok(bob, $"/api/procurement/purchase-orders/{po1}/cancel", new { reason = "Re-sourcing both" });
        Assert.Equal("Open", (await Requisition(qe, pins)).GetProperty("status").GetString());

        // Cancelled requisitions cannot be ordered.
        await plant.Ok(qe, $"{url}/{pins}/cancel", new { reason = "Pins found in the gauge crib" });
        Assert.Equal("Cancelled", (await Requisition(qe, pins)).GetProperty("status").GetString());
        await Rejected(plant, bob, "/api/procurement/purchase-orders", new { supplier = "ACME", lines = new[] { new { partNumber = "GAUGE-PIN", quantity = 5m, promisedDate = Day(12), requisitionId = (long?)pins } } }, "requisition_not_open");
        Assert.DoesNotContain((await Get(qe, url)).EnumerateArray(), x => x.GetProperty("requisitionId").GetInt64() == pins);

        // Ordered again, received and issued: stock that is not lot-tracked is traced to its receipt only, and says so.
        var po2 = Id(await plant.Ok(bob, "/api/procurement/purchase-orders", new { supplier = "ACME", lines = new[]
        {
            new { partNumber = "BRG-6204", quantity = 2m, promisedDate = Day(12), requisitionId = (long?)bearing },
            new { partNumber = "BRG-6204", quantity = 1m, promisedDate = Day(12), requisitionId = (long?)null },
        } }));
        await plant.Ok(pat, $"/api/procurement/purchase-orders/{po2}/approve", new { reason = "Repair parts" });
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "BRG-6204", quantity = 2m, locationCode = "STORES", poNumber = po2, poLine = 1 });
        await plant.Ok(tech, $"/api/maintenance/work-orders/{repair}/parts", new { partNumber = "BRG-6204", quantity = 2m, locationCode = "STORES" });
        var why = await Why(mp, po2, 1);
        Assert.Equal(("Maintenance", $"work-order:{repair}", JsonValueKind.Null), (why.GetProperty("sourceKind").GetString(), why.GetProperty("sourceKey").GetString(), why.GetProperty("demand").ValueKind));
        Assert.Contains("not lot-tracked", why.GetProperty("answer").GetString(), StringComparison.Ordinal);
        Assert.Contains($"requisition {bearing}", why.GetProperty("answer").GetString(), StringComparison.Ordinal);
        Assert.Contains("No source was recorded", (await Why(bob, po2, 2)).GetProperty("answer").GetString(), StringComparison.Ordinal);
        var ledger = await Get(mp, "/api/inventory/transactions?part=BRG-6204&take=100");
        var issued = ledger.EnumerateArray().Single(t => t.GetProperty("kind").GetString() == "MaintenanceIssue").GetProperty("transactionId").GetInt64();
        var fromIssue = await Get(mp, $"/api/inventory/transactions/{issued}/why?part=BRG-6204");
        Assert.Empty(fromIssue.GetProperty("purchases").EnumerateArray());
        Assert.Contains("not lot-tracked", fromIssue.GetProperty("answer").GetString(), StringComparison.Ordinal);
        var received = ledger.EnumerateArray().Single(t => t.GetProperty("kind").GetString() == "Receipt").GetProperty("transactionId").GetInt64();
        Assert.Single((await Get(mp, $"/api/inventory/transactions/{received}/why?part=BRG-6204")).GetProperty("purchases").EnumerateArray());

        // What was asked and why never changes, nothing is deleted, and lines keep their source.
        await using var db = new NpgsqlConnection(app.ConnectionString);
        await db.OpenAsync();
        foreach (var sql in new[]
        {
            "DELETE FROM procurement.requisition",
            $"UPDATE procurement.requisition SET quantity = 9 WHERE requisition_id = {bearing}",
            $"UPDATE procurement.requisition SET source_key = 'work-order:1' WHERE requisition_id = {bearing}",
            $"UPDATE procurement.requisition SET status = 'Open', cancelled_at = NULL, cancelled_by = NULL, cancelled_reason = NULL WHERE requisition_id = {pins}",
            $"UPDATE procurement.purchase_order_line SET source_key = 'ncr:1' WHERE po_number = {po2} AND line_number = 1",
            $"UPDATE procurement.purchase_order_line SET source_kind = 'Quality', source_key = 'ncr:1', source_label = 'x' WHERE po_number = {po2} AND line_number = 2",
            "UPDATE planning.demand SET source_key = NULL WHERE kind = 'Quality'",
        })
        {
            await using var tamper = new NpgsqlCommand(sql, db);
            await Assert.ThrowsAsync<PostgresException>(() => tamper.ExecuteNonQueryAsync());
        }
    }
}
