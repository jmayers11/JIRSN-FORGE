using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Forge.Kernel.Security;
using Npgsql;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ScopedSourcingFlowTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_scoped_buyer_can_create_record_and_review_program_A_sources_while_B_mixed_and_revoked_sources_stay_denied()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("scoped-sourcing-programs", "ProgramManager");
        await plant.Person("scoped-sourcing-full", "Buyer", "PurchasingManager");
        await plant.Person("scoped-sourcing-reviewer", "Buyer", "PurchasingManager");
        var programManager = plant.As("scoped-sourcing-programs");
        var programA = Guid.Parse((await plant.Ok(programManager, "/api/programs/", new { code = "SCOPE-SOURCE-A", name = "Scoped source A" })).GetProperty("entityId").GetString()!);
        var programB = Guid.Parse((await plant.Ok(programManager, "/api/programs/", new { code = "SCOPE-SOURCE-B", name = "Other source B" })).GetProperty("entityId").GetString()!);
        const string role = "ScopedSourcingBuyer";
        var permissions = new[] { Permissions.PurchasingManage, Permissions.PurchasingRead, Permissions.RequisitionRaise };
        await plant.Ok(plant.Admin, "/api/kernel/roles", new { role, displayName = "Buyer for source program A", permissions, reason = "Configure contextual sourcing actions" });
        foreach (var permission in permissions)
            await plant.Ok(plant.Admin, $"/api/kernel/roles/{role}/permission-scopes", new { permission, scopeType = "program", scopeId = programA.ToString("D"), reason = "Only actual source program A" });
        await plant.Person("scoped-sourcing-buyer", role);
        foreach (var subject in new[] { "scoped-sourcing-full", "scoped-sourcing-reviewer", "scoped-sourcing-buyer" })
        foreach (var program in new[] { programA, programB })
            await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People[subject]}/programs", new { programId = program, reason = "Membership is independent of action scope" });
        var full = plant.As("scoped-sourcing-full");
        var reviewer = plant.As("scoped-sourcing-reviewer");
        var scoped = plant.As("scoped-sourcing-buyer");
        await plant.Ok(full, "/api/procurement/suppliers", new { code = "SCOPED-SUPPLIER", name = "Shared sourcing supplier" });
        var day = DateTime.UtcNow.AddDays(10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        async Task<long> NeedAsync(Guid program) => Id(await plant.Ok(full, "/api/procurement/requisitions", new
        {
            partNumber = "COIL-AL", quantity = 10m, needDate = day, sourceKind = "Internal", sourceKey = "internal:scope-" + Guid.NewGuid().ToString("N"),
            sourceLabel = "Actual sourcing program", requiredRevision = "A", programId = program, reason = "Recorded scoped source",
        }));
        object Rfq(long need) => new { requisitionId = need, title = "Scoped sourcing flow", supplierCodes = new[] { "SCOPED-SUPPLIER" }, reason = "Request an actual source response" };
        var needA = await NeedAsync(programA);
        var needB = await NeedAsync(programB);
        var rfqA = Id(await plant.Ok(scoped, "/api/procurement/rfqs", Rfq(needA)));
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(scoped, "/api/procurement/rfqs", Rfq(needB))).StatusCode);
        var rfqB = Id(await plant.Ok(full, "/api/procurement/rfqs", Rfq(needB)));
        foreach (var rfq in new[] { rfqA, rfqB })
            await plant.Ok(reviewer, $"/api/procurement/rfqs/{rfq}/release", new { reason = "Independent release of applicable technical package" });

        async Task<object> QuoteAsync(long rfq)
        {
            using var upload = new HttpRequestMessage(HttpMethod.Post, "/api/engineering/document-files") { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("Original supplier response for RFQ " + rfq)) };
            upload.Headers.Add("Idempotency-Key", "scoped-source-file-" + rfq);
            upload.Headers.Add("X-File-Name", "supplier-response.pdf");
            var response = await full.SendAsync(upload);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, body);
            var sha = JsonDocument.Parse(body).RootElement.GetProperty("entityId").GetString();
            var link = Id(await plant.Ok(full, $"/api/engineering/attachments/Rfq/{rfq}/file", new { fileSha256 = sha, fileName = "supplier-response.pdf", type = "Other" }));
            var documentNumber = Assert.Single((await GetAsync(full, $"/api/engineering/attachments/Rfq/{rfq}")).EnumerateArray(), item => item.GetProperty("linkId").GetInt64() == link).GetProperty("documentNumber").GetString();
            var document = await GetAsync(full, $"/api/engineering/documents/{documentNumber}");
            return new { supplierCode = "SCOPED-SUPPLIER", quantity = 10m, unitPrice = 2m, currency = "USD", promisedDate = day, validThrough = day,
                originalDocumentNumber = documentNumber, originalDocumentRevision = document.GetProperty("document").GetProperty("revisions")[0].GetProperty("revision").GetString(), reason = "Transcribe the recorded supplier response" };
        }
        var quoteA = await QuoteAsync(rfqA);
        var quoteB = await QuoteAsync(rfqB);
        var recordedA = Id(await plant.Ok(scoped, $"/api/procurement/rfqs/{rfqA}/quotes", quoteA));
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(scoped, $"/api/procurement/rfqs/{rfqB}/quotes", quoteB)).StatusCode);
        await plant.Ok(full, $"/api/procurement/rfqs/{rfqB}/quotes", quoteB);
        var rfqs = await GetAsync(scoped, "/api/procurement/rfqs");
        Assert.Equal(rfqA, Assert.Single(rfqs.EnumerateArray()).GetProperty("rfq").GetProperty("rfqId").GetInt64());
        Assert.Equal(recordedA, Assert.Single((await GetAsync(scoped, $"/api/procurement/rfqs/{rfqA}/quotes")).EnumerateArray()).GetProperty("quoteId").GetInt64());
        Assert.Equal(HttpStatusCode.NotFound, (await scoped.GetAsync($"/api/procurement/rfqs/{rfqB}/quotes")).StatusCode);
        Assert.Equal(needA, Assert.Single((await GetAsync(scoped, "/api/procurement/requisitions?open=false")).EnumerateArray()).GetProperty("requisitionId").GetInt64());

        await plant.Ok(reviewer, $"/api/procurement/rfqs/{rfqA}/select", new { quoteId = recordedA, reason = "Independently select the actual program A response" });
        object Line(long need) => new { partNumber = "COIL-AL", quantity = 10m, promisedDate = day, requisitionId = need };
        object Purchase(params long[] needs) => new { supplier = "SCOPED-SUPPLIER", lines = needs.Select(Line).ToArray(), reason = "Buy actual source requisitions" };
        var purchaseA = Purchase(needA);
        var created = await scoped.CommandAsync("/api/procurement/purchase-orders", purchaseA, "scoped-po-create-once");
        var createdBody = await created.Content.ReadAsStringAsync();
        Assert.True(created.IsSuccessStatusCode, createdBody);
        var poA = Id(JsonDocument.Parse(createdBody).RootElement);
        var replay = await scoped.CommandAsync("/api/procurement/purchase-orders", purchaseA, "scoped-po-create-once");
        Assert.True(replay.IsSuccessStatusCode);
        Assert.Equal(createdBody, await replay.Content.ReadAsStringAsync());
        var otherNeedB = await NeedAsync(programB);
        var otherNeedA = await NeedAsync(programA);
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(scoped, "/api/procurement/purchase-orders", Purchase(otherNeedB))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(scoped, "/api/procurement/purchase-orders", Purchase(otherNeedA, otherNeedB))).StatusCode);
        const string deniedLineReason = "Program A authority cannot add an actual program B source";
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(scoped, $"/api/procurement/purchase-orders/{poA}/lines", new
        {
            partNumber = "COIL-AL", quantity = 10m, promisedDate = day, requisitionId = otherNeedB, reason = deniedLineReason,
        })).StatusCode);
        await using (var connection = new NpgsqlConnection(app.ConnectionString))
        {
            await connection.OpenAsync();
            await using var proof = new NpgsqlCommand("SELECT (SELECT count(*) FROM kernel.audit_log WHERE entity_type='PurchaseOrder' AND entity_id=$1 AND action='CreatePurchaseOrder' AND outcome='Succeeded'),(SELECT count(*) FROM kernel.outbox WHERE envelope::jsonb->>'entityType'='PurchaseOrder' AND envelope::jsonb->>'entityId'=$1 AND envelope::jsonb->>'eventType'='PurchaseOrderChanged'),(SELECT count(*) FROM kernel.audit_log WHERE entity_type='PurchaseOrder' AND entity_id=$1 AND action='AddPurchaseLine' AND outcome='Denied' AND actor=$2 AND reason=$3),(SELECT count(*) FROM kernel.outbox WHERE envelope::jsonb->>'entityType'='PurchaseOrder' AND envelope::jsonb->>'entityId'=$1 AND envelope::jsonb->>'eventType'='AuthorizationDenied')", connection);
            proof.Parameters.AddWithValue(poA.ToString(CultureInfo.InvariantCulture));
            proof.Parameters.AddWithValue("person:" + plant.People["scoped-sourcing-buyer"].ToString("D"));
            proof.Parameters.AddWithValue(deniedLineReason);
            await using var reader = await proof.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal((1L, 1L, 1L, 1L), (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)));
        }
        await plant.Ok(reviewer, $"/api/procurement/purchase-orders/{poA}/approve", new { reason = "Independent source purchase approval" });

        async Task<long> PurchaseAsync(params Guid[] programs)
        {
            var lines = new List<object>();
            foreach (var program in programs)
                lines.Add(new { partNumber = "COIL-AL", quantity = 10m, promisedDate = day, requisitionId = await NeedAsync(program) });
            var po = Id(await plant.Ok(full, "/api/procurement/purchase-orders", new { supplier = "SCOPED-SUPPLIER", lines, reason = "Buy actual source requisitions" }));
            await plant.Ok(reviewer, $"/api/procurement/purchase-orders/{po}/approve", new { reason = "Independent source purchase approval" });
            return po;
        }
        var poB = await PurchaseAsync(programB);
        var mixed = await PurchaseAsync(programA, programB);
        Assert.Equal(poA, Assert.Single((await GetAsync(scoped, "/api/procurement/purchase-orders")).EnumerateArray()).GetProperty("poNumber").GetInt64());
        Assert.Equal(poA, (await GetAsync(scoped, $"/api/procurement/purchase-orders/{poA}/lines/1/package")).GetProperty("poNumber").GetInt64());
        Assert.Equal(needA, (await GetAsync(scoped, $"/api/procurement/purchase-orders/{poA}/lines/1/why")).GetProperty("requisition").GetProperty("requisitionId").GetInt64());
        foreach (var hidden in new[] { poB, mixed })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await scoped.GetAsync($"/api/procurement/purchase-orders/{hidden}/lines/1/package")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await scoped.GetAsync($"/api/procurement/purchase-orders/{hidden}/lines/1/why")).StatusCode);
        }
        var remainingNeedA = await NeedAsync(programA);
        await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["scoped-sourcing-buyer"]}/programs/{programA}/revoke", new { reason = "Source review and mutation must honor current revocation" });
        Assert.Empty((await GetAsync(scoped, "/api/procurement/rfqs")).EnumerateArray());
        Assert.Empty((await GetAsync(scoped, "/api/procurement/requisitions?open=false")).EnumerateArray());
        Assert.Empty((await GetAsync(scoped, "/api/procurement/purchase-orders")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await scoped.GetAsync($"/api/procurement/rfqs/{rfqA}/quotes")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await scoped.GetAsync($"/api/procurement/purchase-orders/{poA}/lines/1/package")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await scoped.GetAsync($"/api/procurement/purchase-orders/{poA}/lines/1/why")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(scoped, "/api/procurement/rfqs", Rfq(remainingNeedA))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(scoped, $"/api/procurement/rfqs/{rfqA}/quotes", quoteA)).StatusCode);
    }

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);
    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }
}
