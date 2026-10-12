using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class V313Tests(PostgresFixture postgres)
{
    private static int key;
    private static string Day(int days) => DateTime.UtcNow.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);
    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement;
    }
    private static async Task Reject(SlittingPlant.Plant plant, HttpClient client, string url, object body, string code)
    {
        var response = await plant.Try(client, url, body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(code, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
    private static async Task<string> ResponseFile(SlittingPlant.Plant plant, HttpClient buyer, long rfq)
    {
        using var upload = new HttpRequestMessage(HttpMethod.Post, "/api/engineering/document-files") { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("Original supplier quotation for test")) };
        upload.Headers.Add("Idempotency-Key", "v313-file-" + Interlocked.Increment(ref key));
        upload.Headers.Add("X-File-Name", "supplier-response.pdf");
        var response = await buyer.SendAsync(upload);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        var sha = JsonDocument.Parse(text).RootElement.GetProperty("entityId").GetString();
        var attached = Id(await plant.Ok(buyer, $"/api/engineering/attachments/Rfq/{rfq}/file", new { fileSha256 = sha, fileName = "supplier-response.pdf", type = "Other" }));
        return (await Get(buyer, $"/api/engineering/attachments/Rfq/{rfq}")).EnumerateArray().Single(l => l.GetProperty("linkId").GetInt64() == attached).GetProperty("documentNumber").GetString()!;
    }

    [Fact]
    public async Task Rfq_records_package_and_original_responses_but_only_selected_purchase_orders_create_supply()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("buyer", "Buyer", "PurchasingManager");
        await plant.Person("manager", "Buyer", "PurchasingManager");
        var buyer = plant.As("buyer");
        var manager = plant.As("manager");
        await plant.Ok(buyer, "/api/procurement/suppliers", new { code = "ACME", name = "Acme" });
        await plant.Ok(buyer, "/api/procurement/suppliers", new { code = "OTHER", name = "Other Supplier" });
        var need = Id(await plant.Ok(buyer, "/api/procurement/requisitions", new { partNumber = "COIL-AL", quantity = 10m, needDate = Day(14), sourceKind = "Internal", sourceKey = "internal:replacement", sourceLabel = "Replacement coil", requiredRevision = "A" }));
        var rfq = Id(await plant.Ok(buyer, "/api/procurement/rfqs", new { requisitionId = need, title = "Coil sourcing", supplierCodes = new[] { "ACME", "OTHER" } }));
        await Reject(plant, buyer, "/api/procurement/rfqs", new { requisitionId = need, title = "Duplicate", supplierCodes = new[] { "ACME" } }, "rfq_exists");
        await Reject(plant, buyer, $"/api/procurement/rfqs/{rfq}/release", new { reason = "Release" }, "separation_of_duties");
        await plant.Ok(manager, $"/api/procurement/rfqs/{rfq}/release", new { reason = "Reviewed sourcing package" });
        var state = (await Get(buyer, "/api/procurement/rfqs")).EnumerateArray().Single().GetProperty("rfq");
        Assert.Equal("A", state.GetProperty("package").GetProperty("itemRevision").GetString());
        await plant.Ok(plant.Engineer, "/api/engineering/items/COIL-AL/revisions", new { revision = "B" });
        await plant.Ok(plant.Approver, "/api/engineering/items/COIL-AL/revisions/B/release", new { reason = "Later engineering revision" });
        Assert.Equal("A", (await Get(buyer, "/api/procurement/rfqs"))[0].GetProperty("rfq").GetProperty("package").GetProperty("itemRevision").GetString());
        Assert.Empty((await Get(buyer, "/api/procurement/purchase-orders")).EnumerateArray());
        var original = await ResponseFile(plant, buyer, rfq);
        // References use the existing initial revision convention, not a sourcing-specific revision system.
        var document = await Get(buyer, $"/api/engineering/documents/{original}");
        var revision = document.GetProperty("document").GetProperty("revisions")[0].GetProperty("revision").GetString();
        object ActualQuote(string supplier, decimal quantity = 10m, string? alternate = null) => new { supplierCode = supplier, quantity, unitPrice = 5m, currency = "USD", promisedDate = Day(10), validThrough = Day(7), proposedAlternatePart = alternate, originalDocumentNumber = original, originalDocumentRevision = revision, reason = "Transcribed supplier response" };
        var shortQuote = Id(await plant.Ok(buyer, $"/api/procurement/rfqs/{rfq}/quotes", ActualQuote("ACME", 5m)));
        await Reject(plant, manager, $"/api/procurement/rfqs/{rfq}/select", new { quoteId = shortQuote, reason = "Select" }, "quote_quantity");
        var alternate = Id(await plant.Ok(buyer, $"/api/procurement/rfqs/{rfq}/quotes", ActualQuote("OTHER", alternate: "ROLL-100")));
        await Reject(plant, manager, $"/api/procurement/rfqs/{rfq}/select", new { quoteId = alternate, reason = "Select" }, "alternate_requires_engineering");
        var quote = Id(await plant.Ok(buyer, $"/api/procurement/rfqs/{rfq}/quotes", ActualQuote("ACME")));
        object Purchase(string supplier) => new { supplier, lines = new[] { new { partNumber = "COIL-AL", quantity = 10m, promisedDate = Day(10), requisitionId = need } }, reason = "Buy selected coil" };
        await Reject(plant, buyer, "/api/procurement/purchase-orders", Purchase("ACME"), "source_selection_required");
        await Reject(plant, buyer, $"/api/procurement/rfqs/{rfq}/select", new { quoteId = quote, reason = "Select" }, "separation_of_duties");
        var selection = new { quoteId = quote, reason = "Complete quantity and required configuration" };
        var first = await manager.CommandAsync($"/api/procurement/rfqs/{rfq}/select", selection, "source-once");
        var retry = await manager.CommandAsync($"/api/procurement/rfqs/{rfq}/select", selection, "source-once");
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
        Assert.Equal(await first.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());
        await Reject(plant, buyer, "/api/procurement/purchase-orders", Purchase("OTHER"), "selected_source_mismatch");
        var po = Id(await plant.Ok(buyer, "/api/procurement/purchase-orders", Purchase("ACME")));
        await plant.Ok(manager, $"/api/procurement/purchase-orders/{po}/approve", new { reason = "Reviewed purchase" });
        Assert.Equal("Approved", (await Get(buyer, "/api/procurement/purchase-orders"))[0].GetProperty("status").GetString());
        Assert.Equal("A", (await Get(buyer, $"/api/procurement/purchase-orders/{po}/lines/1/package")).GetProperty("itemRevision").GetString());
        var report = await Get(buyer, "/api/reports/sourcing");
        Assert.Equal(3, report.GetProperty("rows").GetArrayLength());
        var answer = await buyer.PostAsJsonAsync(new Uri("/api/analyst/ask", UriKind.Relative), new { question = "Show supplier quotes" });
        Assert.Equal("sourcing", JsonDocument.Parse(await answer.Content.ReadAsStringAsync()).RootElement.GetProperty("interpretation").GetProperty("report").GetProperty("reportId").GetString());
        await using var database = new NpgsqlConnection(app.ConnectionString);
        await database.OpenAsync();
        await using var count = new NpgsqlCommand("SELECT count(*) FROM kernel.audit_log WHERE action='SelectSupplierQuote' AND outcome='Succeeded'", database);
        Assert.Equal(1L, await count.ExecuteScalarAsync());
        await using var tamper = new NpgsqlCommand("UPDATE procurement.supplier_quote SET unit_price = unit_price + 1", database);
        await Assert.ThrowsAsync<PostgresException>(() => tamper.ExecuteNonQueryAsync());
        var denied = await plant.Try(plant.As("stores"), "/api/procurement/rfqs", new { requisitionId = need, title = "Unauthorized", supplierCodes = new[] { "ACME" } });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task Program_access_revocation_hides_sourcing_in_lists_reports_evidence_and_commands()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("buyer", "Buyer", "PurchasingManager");
        await plant.Person("manager", "Buyer", "PurchasingManager");
        await plant.Person("pm", "ProgramManager");
        var buyer = plant.As("buyer");
        var manager = plant.As("manager");
        var program = Guid.Parse((await plant.Ok(plant.As("pm"), "/api/programs/", new { code = "SOURCING", name = "Private sourcing", customer = "Acme" })).GetProperty("entityId").GetString()!);
        foreach (var subject in new[] { "buyer", "manager" })
            await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People[subject]}/programs", new { programId = program, reason = "Sourcing team" });
        await plant.Ok(buyer, "/api/procurement/suppliers", new { code = "ACME", name = "Acme" });
        var need = Id(await plant.Ok(buyer, "/api/procurement/requisitions", new { partNumber = "COIL-AL", quantity = 10m, needDate = Day(14), sourceKind = "Internal", sourceKey = "internal:private", sourceLabel = "Private sourcing need", programId = program }));
        var rfq = Id(await plant.Ok(buyer, "/api/procurement/rfqs", new { requisitionId = need, title = "Private sourcing", supplierCodes = new[] { "ACME" } }));
        await plant.Ok(manager, $"/api/procurement/rfqs/{rfq}/release", new { reason = "Reviewed package" });
        var original = await ResponseFile(plant, buyer, rfq);
        await plant.Ok(buyer, $"/api/procurement/rfqs/{rfq}/quotes", new { supplierCode = "ACME", quantity = 10m, unitPrice = 5m, currency = "USD", promisedDate = Day(10), validThrough = Day(7), originalDocumentNumber = original, originalDocumentRevision = "1" });
        await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["buyer"]}/programs/{program}/revoke", new { reason = "Sourcing assignment ended" });
        Assert.Empty((await Get(buyer, "/api/procurement/rfqs")).EnumerateArray());
        Assert.Empty((await Get(buyer, "/api/reports/sourcing")).GetProperty("rows").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await buyer.GetAsync(new Uri($"/api/engineering/documents/{original}", UriKind.Relative))).StatusCode);
        var hidden = await buyer.GetAsync(new Uri($"/api/procurement/rfqs/{rfq}/quotes", UriKind.Relative));
        Assert.False(hidden.IsSuccessStatusCode);
        var denied = await plant.Try(buyer, $"/api/procurement/rfqs/{rfq}/cancel", new { reason = "Attempt after revocation" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.DoesNotContain("Private sourcing", await denied.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var currentRfq = Assert.Single((await Get(manager, "/api/procurement/rfqs")).EnumerateArray(), row => row.GetProperty("rfq").GetProperty("rfqId").GetInt64() == rfq).GetProperty("rfq");
        Assert.Equal("Released", currentRfq.GetProperty("status").GetString());
        await using var database = new NpgsqlConnection(app.ConnectionString);
        await database.OpenAsync();
        await using var audit = new NpgsqlCommand("SELECT count(*) FROM kernel.audit_log WHERE action='CancelRfq' AND outcome='Denied' AND entity_id=$1 AND actor=$2", database);
        audit.Parameters.AddWithValue(rfq.ToString(CultureInfo.InvariantCulture));
        audit.Parameters.AddWithValue("person:" + plant.People["buyer"].ToString("D"));
        Assert.Equal(1L, await audit.ExecuteScalarAsync());
    }
}
