using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Forge.Integration.Tests;

/// <summary>
/// V3.11 through the real API: a receipt accepted in part; FAI pending determination never lets material be accepted;
/// structured FAI results backed by a released report; supplier dispositions with a second person for use as is; a
/// return to vendor whose replacement is a new instance while the rejected one keeps its history; customer returns.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class V311Tests(PostgresFixture postgres)
{
    private static int key;

    private static string Day(int fromToday) => DateTime.UtcNow.AddDays(fromToday).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

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

    private static async Task<JsonElement> Receipt(HttpClient client, long receipt) =>
        (await Get(client, "/api/inventory/receiving")).EnumerateArray().Single(r => r.GetProperty("receipt").GetProperty("transactionId").GetInt64() == receipt);

    [Fact]
    public async Task Fai_recording_does_not_reveal_a_report_outside_the_inspectors_program()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("pm", "ProgramManager");
        await plant.Person("insp", "Inspector");
        var program = Guid.Parse((await plant.Ok(plant.As("pm"), "/api/programs/", new { code = "PRIVATE", name = "Private", customer = "Acme" })).GetProperty("entityId").GetString()!);
        await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["eng"]}/programs", new { programId = program, reason = "Program engineer" });
        await plant.Ok(plant.Engineer, "/api/engineering/documents", new { number = "PRIVATE-FAI", title = "Private report", type = "InspectionPlan", authority = "Internal", programId = program });
        var response = await plant.Try(plant.As("insp"), "/api/quality/fai", new
        {
            partNumber = "ROLL-100", revision = "A", supplierCode = "ACME", passed = true,
            results = new[] { new { characteristic = "Width", requirement = "100", measured = "100", pass = true } },
            documentNumber = "PRIVATE-FAI", documentRevision = "A",
        });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("document_not_found", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty((await Get(plant.As("insp"), "/api/quality/fai")).EnumerateArray());
    }

    [Fact]
    public async Task Concurrent_customer_returns_cannot_exceed_the_shipped_quantity()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("ship", "Shipper");
        await plant.Person("qe", "QualityEngineer");
        var stores = plant.As("stores");
        var shipment = Id(await plant.Ok(stores, "/api/inventory/shipments", new { customer = "Acme", lines = new[] { new { partNumber = "COIL-AL", quantity = 10m, locationCode = "STORES" } } }));
        await plant.Ok(stores, $"/api/inventory/shipments/{shipment}/pack", new { });
        await plant.Ok(plant.As("ship"), $"/api/inventory/shipments/{shipment}/ship", new { carrier = "UPS" });
        var body = new { shipmentId = shipment, partNumber = "COIL-AL", quantity = 7m, locationCode = "STORES", reason = "Customer return" };
        var responses = await Task.WhenAll(plant.Try(stores, "/api/inventory/customer-returns", body), plant.Try(stores, "/api/inventory/customer-returns", body));
        Assert.Single(responses, r => r.IsSuccessStatusCode);
        var rejected = Assert.Single(responses, r => !r.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        Assert.Contains("over_return", await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(7m, (await Get(stores, $"/api/inventory/customer-returns?shipmentId={shipment}")).EnumerateArray().Sum(r => r.GetProperty("quantity").GetDecimal()));
        var returned = Assert.Single((await Get(stores, $"/api/inventory/customer-returns?shipmentId={shipment}")).EnumerateArray());
        var rma = returned.GetProperty("rmaId").GetInt64();
        var disposition = new { disposition = "Restock", reason = "Inspected and released" };
        var quality = plant.As("qe");
        var decisions = await Task.WhenAll(plant.Try(quality, $"/api/inventory/customer-returns/{rma}/disposition", disposition), plant.Try(quality, $"/api/inventory/customer-returns/{rma}/disposition", disposition));
        Assert.Single(decisions, r => r.IsSuccessStatusCode);
        var duplicate = Assert.Single(decisions, r => !r.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);
        Assert.Contains("already_dispositioned", await duplicate.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_original_receiver_cannot_inspect_their_receipt_after_another_receipt_is_added()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("receiver", "MaterialHandler", "Inspector");
        await plant.Person("other", "MaterialHandler");
        var receiver = plant.As("receiver");
        var receipt = Id(await plant.Ok(receiver, "/api/inventory/receipts", new { partNumber = "COIL-AL", quantity = 10m, locationCode = "STORES", inspect = true, reason = "First delivery" }));
        await plant.Ok(plant.As("other"), "/api/inventory/receipts", new { partNumber = "COIL-AL", quantity = 10m, locationCode = "STORES", inspect = true, reason = "Second delivery" });
        await Rejected(plant, receiver, $"/api/inventory/receiving/{receipt}/inspections", new { accepted = 10m, rejected = 0m }, "separation_of_duties");
        Assert.Equal(10m, (await Receipt(receiver, receipt)).GetProperty("remaining").GetDecimal());
    }

    [Fact]
    public async Task A_receipt_is_accepted_in_part_only_once_fai_is_complete_and_a_returned_lot_is_replaced_by_a_new_instance()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        foreach (var (who, role) in new[] { ("insp", "Inspector"), ("qe", "QualityEngineer"), ("bob", "Buyer"), ("pat", "PurchasingManager"), ("mike", "MaterialHandler") })
        {
            await plant.Person(who, role);
        }

        var (e, insp, qe, bob, pat, mike) = (plant.Engineer, plant.As("insp"), plant.As("qe"), plant.As("bob"), plant.As("pat"), plant.As("mike"));
        await plant.Ok(e, "/api/engineering/items", new { partNumber = "BRKT-9", description = "Bracket", unitOfMeasure = "EA", makeBuy = "Buy", leadTimeDays = 5, lotTracked = true });
        await plant.Ok(e, "/api/engineering/items/BRKT-9/revisions", new { revision = "A" });
        await plant.Ok(plant.Approver, "/api/engineering/items/BRKT-9/revisions/A/release", new { reason = "ECN-3" });
        await plant.Ok(bob, "/api/procurement/suppliers", new { code = "ACME", name = "Acme Brackets" });
        var po = Id(await plant.Ok(bob, "/api/procurement/purchase-orders", new { supplier = "ACME", lines = new[] { new { partNumber = "BRKT-9", quantity = 100m, promisedDate = Day(7) } } }));
        await plant.Ok(pat, $"/api/procurement/purchase-orders/{po}/approve", new { reason = "Budget approved" });
        var receipt = Id(await plant.Ok(mike, "/api/inventory/receipts", new { partNumber = "BRKT-9", quantity = 100m, locationCode = "STORES", lotNumber = "B-1", poNumber = po, inspect = true, revision = "A" }));

        // FAI is pending determination: nothing is accepted, and it is never shown as complete.
        Assert.Equal("PendingDetermination", (await Receipt(insp, receipt)).GetProperty("faiStatus").GetString());
        await Rejected(plant, insp, $"/api/inventory/receiving/{receipt}/inspections", new { accepted = 60m, rejected = 0m }, "pending determination");
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(mike, $"/api/inventory/receiving/{receipt}/inspections", new { accepted = 1m, rejected = 0m })).StatusCode);
        await plant.Ok(qe, "/api/quality/fai-determinations", new { partNumber = "BRKT-9", revision = "A", supplierCode = "ACME", state = "Required", reason = "New supplier for a build-to-print part" });
        await Rejected(plant, insp, $"/api/inventory/receiving/{receipt}/inspections", new { accepted = 60m, rejected = 0m }, "no passing FAI");

        // The FAI: structured results and the signed (released) report.
        await plant.Ok(e, "/api/engineering/documents", new { number = "FAI-BRKT-9", title = "FAI report BRKT-9 from Acme", type = "FaiReport", authority = "Reference" });
        var results = new[] { new { characteristic = "Hole spacing", requirement = "50 ±0.1", measured = "50.03", pass = true }, new { characteristic = "Finish", requirement = "No burrs", measured = "OK", pass = true } };
        await Rejected(plant, insp, "/api/quality/fai", new { partNumber = "BRKT-9", revision = "A", supplierCode = "ACME", passed = true, results, documentNumber = "FAI-BRKT-9", documentRevision = "A" }, "report_not_signed");
        await ReleaseAsync(plant, "FAI-BRKT-9", "A");
        await plant.Ok(insp, "/api/quality/fai", new { partNumber = "BRKT-9", revision = "A", supplierCode = "ACME", passed = true, results, documentNumber = "FAI-BRKT-9", documentRevision = "A" });

        // Partial acceptance: 60 accepted, 30 rejected with results, 10 still to inspect.
        await Rejected(plant, insp, $"/api/inventory/receiving/{receipt}/inspections", new { accepted = 60m, rejected = 30m }, "defect_required");
        await plant.Ok(insp, $"/api/inventory/receiving/{receipt}/inspections", new
        {
            accepted = 60m, rejected = 30m, defect = "Burrs on the punched edge",
            results = new[] { new { characteristic = "Finish", requirement = "No burrs", measured = "Burrs on 30", pass = false } },
        });
        var state = await Receipt(insp, receipt);
        Assert.Equal((60m, 30m, 10m, "Complete"), (state.GetProperty("accepted").GetDecimal(), state.GetProperty("rejected").GetDecimal(), state.GetProperty("remaining").GetDecimal(), state.GetProperty("faiStatus").GetString()));
        await Rejected(plant, insp, $"/api/inventory/receiving/{receipt}/inspections", new { accepted = 20m, rejected = 0m }, "over_inspection");

        // Supplier disposition: return to vendor; shipped back; the replacement is a new instance.
        var rejection = Assert.Single((await Get(qe, "/api/inventory/rejections?open=true")).EnumerateArray()).GetProperty("rejectionId").GetInt64();
        await plant.Ok(qe, $"/api/inventory/rejections/{rejection}/disposition", new { disposition = "ReturnToVendor", reason = "Supplier to replace" });
        var rtv = Assert.Single((await Get(mike, "/api/inventory/rtvs?open=true")).EnumerateArray()).GetProperty("rtvId").GetInt64();
        await Rejected(plant, mike, $"/api/inventory/rtvs/{rtv}/replacement", new { quantity = 30m, lotNumber = "B-2", locationCode = "STORES" }, "rtv_not_shipped");
        await plant.Ok(mike, $"/api/inventory/rtvs/{rtv}/ship", new { carrier = "UPS" });
        await Rejected(plant, mike, $"/api/inventory/rtvs/{rtv}/replacement", new { quantity = 31m, lotNumber = "B-2", locationCode = "STORES" }, "replacement_quantity_mismatch");
        await Rejected(plant, mike, $"/api/inventory/rtvs/{rtv}/replacement", new { quantity = 30m, lotNumber = "B-1", locationCode = "STORES" }, "replacement_is_new_instance");
        await plant.Ok(mike, "/api/inventory/receipts", new { partNumber = "BRKT-9", quantity = 1m, locationCode = "STORES", lotNumber = "EXISTING", revision = "A", reason = "Unrelated delivery" });
        await Rejected(plant, mike, $"/api/inventory/rtvs/{rtv}/replacement", new { quantity = 30m, lotNumber = "EXISTING", locationCode = "STORES" }, "replacement_is_new_instance");
        await plant.Ok(mike, $"/api/inventory/rtvs/{rtv}/replacement", new { quantity = 30m, lotNumber = "B-2", locationCode = "STORES" });
        var replacementReceipt = (await Get(insp, "/api/inventory/receiving")).EnumerateArray().Single(r => r.GetProperty("receipt").GetProperty("lotNumber").GetString() == "B-2");
        Assert.Equal("ACME", replacementReceipt.GetProperty("supplierCode").GetString());
        Assert.Equal(po, replacementReceipt.GetProperty("receipt").GetProperty("poNumber").GetInt64());
        var closed = (await Get(mike, "/api/inventory/rtvs")).EnumerateArray().Single(r => r.GetProperty("rtvId").GetInt64() == rtv);
        Assert.Equal(("Replaced", "B-2"), (closed.GetProperty("status").GetString(), closed.GetProperty("replacementLot").GetString()));
        var link = Assert.Single((await Get(mike, "/api/reports/lot-lineage?status=Replaces")).GetProperty("rows").EnumerateArray()).GetProperty("cells");
        Assert.Equal(("B-1", "B-2"), (link[3].GetString(), link[4].GetString()));
        var ledger = (await Get(mike, "/api/inventory/transactions?part=BRKT-9")).EnumerateArray().Select(t => t.GetProperty("kind").GetString()).ToList();
        Assert.Contains("ReturnToVendor", ledger);

        // The report and the Analyst.
        var report = await Get(insp, "/api/reports/receiving-inspection?search=BRKT-9");
        Assert.Contains(report.GetProperty("rows").EnumerateArray(), r => r.GetProperty("cells")[9].GetString() == "Complete");
        var answer = await (await plant.As("planner").PostAsJsonAsync(new Uri("/api/analyst/ask", UriKind.Relative), new { question = "Which FAIs are pending determination?" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("receiving-inspection", answer.GetProperty("interpretation").GetProperty("report").GetProperty("reportId").GetString());
    }

    [Fact]
    public async Task Use_as_is_needs_a_second_person_and_a_customer_return_comes_back_on_hold()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        foreach (var (who, role) in new[] { ("insp", "Inspector"), ("qe", "QualityEngineer"), ("qm", "QualityManager"), ("ship", "Shipper") })
        {
            await plant.Person(who, role);
        }

        var (stores, insp, qe, qm, ship) = (plant.As("stores"), plant.As("insp"), plant.As("qe"), plant.As("qm"), plant.As("ship"));

        // Not bought on a PO: FAI does not apply.
        var receipt = Id(await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "COIL-AL", quantity = 50m, locationCode = "STORES", inspect = true, reason = "Transfer from sister plant" }));
        Assert.Equal("NotApplicable", (await Receipt(insp, receipt)).GetProperty("faiStatus").GetString());
        await plant.Ok(insp, $"/api/inventory/receiving/{receipt}/inspections", new { accepted = 45m, rejected = 5m, defect = "Scratched outer wrap" });
        var rejection = Assert.Single((await Get(qe, "/api/inventory/rejections?open=true")).EnumerateArray()).GetProperty("rejectionId").GetInt64();
        await Rejected(plant, qe, $"/api/inventory/rejections/{rejection}/disposition", new { disposition = "UseAsIs" }, "reason_required");
        await plant.Ok(qe, $"/api/inventory/rejections/{rejection}/disposition", new { disposition = "UseAsIs", reason = "Wrap only; coil is fine" });
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(qe, $"/api/inventory/rejections/{rejection}/approve", new { reason = "x" })).StatusCode);
        await plant.Ok(qm, $"/api/inventory/rejections/{rejection}/approve", new { reason = "Agreed: cosmetic" });
        Assert.Empty((await Get(qe, "/api/inventory/rejections?open=true")).EnumerateArray());

        // A customer sends part of a shipment back: it comes onto hold, then is restocked.
        var shipment = Id(await plant.Ok(stores, "/api/inventory/shipments", new { customer = "Acme", lines = new[] { new { partNumber = "COIL-AL", quantity = 10m, locationCode = "STORES" } } }));
        await plant.Ok(stores, $"/api/inventory/shipments/{shipment}/pack", new { });
        await plant.Ok(ship, $"/api/inventory/shipments/{shipment}/ship", new { carrier = "UPS" });
        await Rejected(plant, stores, "/api/inventory/customer-returns", new { shipmentId = shipment, partNumber = "COIL-AL", quantity = 4m, locationCode = "STORES" }, "reason_required");
        var rma = Id(await plant.Ok(stores, "/api/inventory/customer-returns", new { shipmentId = shipment, partNumber = "COIL-AL", quantity = 4m, locationCode = "STORES", reason = "Wrong width" }));
        await Rejected(plant, stores, "/api/inventory/customer-returns", new { shipmentId = shipment, partNumber = "COIL-AL", quantity = 7m, locationCode = "STORES", reason = "More" }, "over_return");
        await plant.Ok(qe, $"/api/inventory/customer-returns/{rma}/disposition", new { disposition = "Restock", reason = "Inspected, within spec" });
        var back = Assert.Single((await Get(stores, $"/api/inventory/customer-returns?shipmentId={shipment}")).EnumerateArray());
        Assert.Equal(("Restock", "Acme"), (back.GetProperty("disposition").GetString(), back.GetProperty("customer").GetString()));
        await Rejected(plant, qe, $"/api/inventory/customer-returns/{rma}/disposition", new { disposition = "Scrap", reason = "x" }, "already_dispositioned");
    }

    private static async Task ReleaseAsync(SlittingPlant.Plant plant, string number, string revision)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/engineering/document-files") { Content = new ByteArrayContent(Encoding.UTF8.GetBytes($"{number} rev {revision}")) };
        request.Headers.Add("Idempotency-Key", "v311-upload-" + Interlocked.Increment(ref key));
        request.Headers.Add("X-File-Name", "file.pdf");
        var response = await plant.Engineer.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        var sha = JsonDocument.Parse(body).RootElement.GetProperty("entityId").GetString()!;
        await plant.Ok(plant.Engineer, $"/api/engineering/documents/{number}/revisions", new { revision, fileSha256 = sha, fileName = $"{number}_{revision}.pdf" });
        await plant.Ok(plant.As("insp"), $"/api/engineering/documents/{number}/verify-signature", new { revision, sourceSigner = "Supplier inspector", sourceSignedAt = DateTimeOffset.UtcNow.AddDays(-1), meaning = "I certify these first article results", reason = "Reviewed the signed original report" });
    }
}


