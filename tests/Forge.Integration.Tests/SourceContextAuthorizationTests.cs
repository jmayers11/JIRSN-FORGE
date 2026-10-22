using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Forge.Kernel.Security;
using Npgsql;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class SourceContextAuthorizationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Ncr_disposition_and_approval_use_the_recorded_program_scope_and_preserve_separation_of_duties()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var (programA, programB) = await ProgramsAsync(plant);
        await plant.Person("context-qe", "QualityEngineer");
        await plant.Person("context-outsider", "QualityManager");
        await ScopedRoleAsync(plant, "NcrProgramAuthority", [Permissions.QualityDisposition, Permissions.QualityApprove], programA);
        await plant.Person("context-ncr-proposer", "NcrProgramAuthority");
        await plant.Person("context-ncr-approver", "NcrProgramAuthority");
        foreach (var subject in new[] { "eng", "planner", "context-qe", "context-ncr-proposer", "context-ncr-approver" })
        foreach (var program in new[] { programA, programB })
            await GrantAsync(plant, subject, program);

        var ncrs = new List<long>();
        foreach (var program in new[] { programA, programB })
        {
            var order = Id(await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = "ROLL-100", quantity = 10m, dueDate = DateTime.UtcNow.AddDays(10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), priority = 3, programId = program }));
            await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Release scoped NCR source work" });
            ncrs.Add(Id(await plant.Ok(plant.As("context-qe"), "/api/quality/ncrs", new { partNumber = "ROLL-100", orderNumber = order, sequence = 10, quantity = 1m, defect = "Recorded surface flaw", description = "Inspect source context", reason = "Observed defect" })));
        }
        var proposer = plant.As("context-ncr-proposer");
        var approver = plant.As("context-ncr-approver");
        var proposal = new { disposition = "UseAsIs", note = "Factual customer concession", reason = "Review recorded defect" };
        await plant.Ok(proposer, $"/api/quality/ncrs/{ncrs[0]}/disposition", proposal);
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(proposer, $"/api/quality/ncrs/{ncrs[1]}/disposition", proposal)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(plant.As("context-outsider"), $"/api/quality/ncrs/{ncrs[0]}/approve", new { reason = "A global role does not grant private program access" })).StatusCode);
        var self = await plant.Try(proposer, $"/api/quality/ncrs/{ncrs[0]}/approve", new { reason = "Scoped authority does not remove duties" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, self.StatusCode);
        Assert.Contains("separation_of_duties", await self.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await plant.Ok(approver, $"/api/quality/ncrs/{ncrs[0]}/approve", new { reason = "Independent program A concession approval" });

        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync();
        await using (var statuses = new NpgsqlCommand("SELECT status FROM quality.ncr WHERE ncr_id=$1 OR ncr_id=$2 ORDER BY ncr_id", connection))
        {
            statuses.Parameters.AddWithValue(ncrs[0]);
            statuses.Parameters.AddWithValue(ncrs[1]);
            await using var reader = await statuses.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync()); Assert.Equal("Dispositioned", reader.GetString(0));
            Assert.True(await reader.ReadAsync()); Assert.Equal("Open", reader.GetString(0));
        }
        await using var audit = new NpgsqlCommand("SELECT count(*) FROM kernel.audit_log WHERE entity_type='Ncr' AND entity_id=$1 AND program_id=$2 AND action IN ('DispositionNcr','DecideUseAsIs') AND outcome='Succeeded'", connection);
        audit.Parameters.AddWithValue(ncrs[0].ToString(CultureInfo.InvariantCulture));
        audit.Parameters.AddWithValue(programA);
        Assert.Equal(2L, await audit.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Purchase_approval_requires_every_actual_requisition_program_and_does_not_invent_a_monetary_value()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        var (programA, programB) = await ProgramsAsync(plant);
        await plant.Person("context-buyer", "Buyer");
        await plant.Person("context-po-outsider", "PurchasingManager");
        await ScopedRoleAsync(plant, "PurchaseProgramAuthority", [Permissions.PurchasingApprove], programA);
        await plant.Person("context-po-approver", "PurchaseProgramAuthority");
        foreach (var subject in new[] { "context-buyer", "context-po-approver" })
        foreach (var program in new[] { programA, programB })
            await GrantAsync(plant, subject, program);
        var buyer = plant.As("context-buyer");
        var approver = plant.As("context-po-approver");
        await plant.Ok(buyer, "/api/procurement/suppliers", new { code = "CTX-SUPPLIER", name = "Context supplier" });
        var needDate = DateTime.UtcNow.AddDays(10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        async Task<long> PurchaseAsync(decimal? unitPrice, decimal quantity, params Guid[] programs)
        {
            var lines = new List<object>();
            foreach (var program in programs)
            {
                var requisition = Id(await plant.Ok(buyer, "/api/procurement/requisitions", new { partNumber = "COIL-AL", quantity, needDate, sourceKind = "Internal", sourceKey = "internal:context-" + Guid.NewGuid().ToString("N"), sourceLabel = "Recorded program requirement", requiredRevision = "A", programId = program, reason = "Source-specific requirement" }));
                lines.Add(new { partNumber = "COIL-AL", quantity, promisedDate = needDate, requisitionId = requisition, unitPrice, currency = "USD" });
            }
            return Id(await plant.Ok(buyer, "/api/procurement/purchase-orders", new { supplier = "CTX-SUPPLIER", lines, reason = "Purchase recorded requisitions" }));
        }
        var poA = await PurchaseAsync(null, 1m, programA);
        var poB = await PurchaseAsync(null, 1m, programB);
        var poBoth = await PurchaseAsync(null, 1m, programA, programB);
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(plant.As("context-po-outsider"), $"/api/procurement/purchase-orders/{poA}/approve", new { reason = "Global authority without program membership" })).StatusCode);
        await plant.Ok(approver, $"/api/procurement/purchase-orders/{poA}/approve", new { reason = "Approve within actual program A scope" });
        foreach (var po in new[] { poB, poBoth })
            Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(approver, $"/api/procurement/purchase-orders/{po}/approve", new { reason = "Program membership must not expand action scope" })).StatusCode);

        var unknownPrice = await PurchaseAsync(null, 1m, programA);
        var exactlyAtLimit = await PurchaseAsync(250m, 4m, programA);
        var aboveLimit = await PurchaseAsync(250.01m, 4m, programA);
        await plant.Ok(plant.Admin, $"/api/kernel/approval-thresholds/{Permissions.PurchasingApprove}", new { maximumAmount = 1000m, currency = "USD", reason = "Monetary decisions need recorded money" });
        var refused = await plant.Try(approver, $"/api/procurement/purchase-orders/{unknownPrice}/approve", new { reason = "No amount or currency was recorded on the PO" });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("threshold", await refused.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        var valuedOrders = await buyer.GetFromJsonAsync<JsonElement[]>("/api/procurement/purchase-orders");
        var exactOrder = Assert.Single(valuedOrders ?? [], order => order.GetProperty("poNumber").GetInt64() == exactlyAtLimit);
        Assert.Equal(1000m, exactOrder.GetProperty("totalAmount").GetDecimal());
        Assert.Equal("USD", exactOrder.GetProperty("currency").GetString());
        Assert.Equal(250m, exactOrder.GetProperty("lines")[0].GetProperty("unitPrice").GetDecimal());
        await plant.Ok(approver, $"/api/procurement/purchase-orders/{exactlyAtLimit}/approve", new { reason = "The complete USD order value is exactly at the configured limit" });
        var tooLarge = await plant.Try(approver, $"/api/procurement/purchase-orders/{aboveLimit}/approve", new { reason = "The complete USD order value exceeds the configured limit" });
        Assert.Equal(HttpStatusCode.Forbidden, tooLarge.StatusCode);
        Assert.Contains("threshold", await tooLarge.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync();
        await using var states = new NpgsqlCommand("SELECT status FROM procurement.purchase_order WHERE po_number=ANY($1) ORDER BY po_number", connection);
        states.Parameters.AddWithValue(new[] { poA, poB, poBoth, unknownPrice, exactlyAtLimit, aboveLimit });
        await using var reader = await states.ExecuteReaderAsync();
        var statuses = new List<string>();
        while (await reader.ReadAsync()) statuses.Add(reader.GetString(0));
        Assert.Equal(["Approved", "Draft", "Draft", "Draft", "Approved", "Draft"], statuses);
    }

    [Fact]
    public async Task Supplier_source_selection_enforces_actual_quote_total_currency_and_program_context()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        var (programA, programB) = await ProgramsAsync(plant);
        await plant.Person("context-quote-buyer", "Buyer");
        await ScopedRoleAsync(plant, "QuoteProgramAuthority", [Permissions.PurchasingApprove], programA);
        await plant.Person("context-quote-approver", "QuoteProgramAuthority");
        foreach (var subject in new[] { "context-quote-buyer", "context-quote-approver" })
        foreach (var program in new[] { programA, programB })
            await GrantAsync(plant, subject, program);
        var buyer = plant.As("context-quote-buyer");
        var approver = plant.As("context-quote-approver");
        await plant.Ok(buyer, "/api/procurement/suppliers", new { code = "CTX-QUOTE", name = "Recorded quote supplier" });
        var day = DateTime.UtcNow.AddDays(10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        async Task<long> RfqAsync(Guid program)
        {
            var need = Id(await plant.Ok(buyer, "/api/procurement/requisitions", new { partNumber = "COIL-AL", quantity = 10m, needDate = day, sourceKind = "Internal", sourceKey = "internal:quote-" + Guid.NewGuid().ToString("N"), sourceLabel = "Actual sourcing context", requiredRevision = "A", programId = program, reason = "Recorded source requirement" }));
            return Id(await plant.Ok(buyer, "/api/procurement/rfqs", new { requisitionId = need, title = "Quoted authority proof", supplierCodes = new[] { "CTX-QUOTE" }, reason = "Request current supplier response" }));
        }
        var rfq = await RfqAsync(programA);
        await plant.Ok(approver, $"/api/procurement/rfqs/{rfq}/release", new { reason = "Authorized program A technical package" });
        using var upload = new HttpRequestMessage(HttpMethod.Post, "/api/engineering/document-files") { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("Original priced supplier response")) };
        upload.Headers.Add("Idempotency-Key", "source-context-quote-file");
        upload.Headers.Add("X-File-Name", "original-quote.pdf");
        var uploaded = await buyer.SendAsync(upload);
        var uploadBody = await uploaded.Content.ReadAsStringAsync();
        Assert.True(uploaded.IsSuccessStatusCode, uploadBody);
        var sha = JsonDocument.Parse(uploadBody).RootElement.GetProperty("entityId").GetString();
        var attached = Id(await plant.Ok(buyer, $"/api/engineering/attachments/Rfq/{rfq}/file", new { fileSha256 = sha, fileName = "original-quote.pdf", type = "Other" }));
        var links = await buyer.GetFromJsonAsync<JsonElement[]>($"/api/engineering/attachments/Rfq/{rfq}");
        var documentNumber = Assert.Single(links ?? [], link => link.GetProperty("linkId").GetInt64() == attached).GetProperty("documentNumber").GetString();
        var document = await buyer.GetFromJsonAsync<JsonElement>($"/api/engineering/documents/{documentNumber}");
        var revision = document.GetProperty("document").GetProperty("revisions")[0].GetProperty("revision").GetString();
        async Task<long> QuoteAsync(decimal unitPrice, string currency) =>
            Id(await plant.Ok(buyer, $"/api/procurement/rfqs/{rfq}/quotes", new { supplierCode = "CTX-QUOTE", quantity = 10m, unitPrice, currency, promisedDate = day, validThrough = day, originalDocumentNumber = documentNumber, originalDocumentRevision = revision, reason = "Transcribe actual priced supplier response" }));
        var aboveLimit = await QuoteAsync(50.1m, "USD");
        var wrongCurrency = await QuoteAsync(50m, "EUR");
        var exactLimit = await QuoteAsync(50m, "USD");
        var unknownMoneyRfq = await RfqAsync(programA);
        await plant.Ok(plant.Admin, $"/api/kernel/approval-thresholds/{Permissions.PurchasingApprove}", new { maximumAmount = 500m, currency = "USD", reason = "Compare the complete quoted commitment" });
        foreach (var quote in new[] { aboveLimit, wrongCurrency })
        {
            var refused = await plant.Try(approver, $"/api/procurement/rfqs/{rfq}/select", new { quoteId = quote, reason = "Recorded quote must fit amount and currency" });
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Contains("threshold", await refused.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(approver, $"/api/procurement/rfqs/{unknownMoneyRfq}/release", new { reason = "A draft RFQ does not record a monetary commitment" })).StatusCode);
        await plant.Ok(approver, $"/api/procurement/rfqs/{rfq}/select", new { quoteId = exactLimit, reason = "Independent source selection at actual USD 500 limit" });
        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync();
        await using var selected = new NpgsqlCommand("SELECT selected_quote_id FROM procurement.rfq WHERE rfq_id=$1", connection);
        selected.Parameters.AddWithValue(rfq);
        Assert.Equal(exactLimit, await selected.ExecuteScalarAsync());
    }

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static async Task<(Guid A, Guid B)> ProgramsAsync(SlittingPlant.Plant plant)
    {
        await plant.Person("context-programs", "ProgramManager");
        var programs = plant.As("context-programs");
        var a = await plant.Ok(programs, "/api/programs/", new { code = "CTX-A", name = "Private source program A" });
        var b = await plant.Ok(programs, "/api/programs/", new { code = "CTX-B", name = "Private source program B" });
        return (Guid.Parse(a.GetProperty("entityId").GetString()!), Guid.Parse(b.GetProperty("entityId").GetString()!));
    }

    private static Task GrantAsync(SlittingPlant.Plant plant, string subject, Guid program) =>
        plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People[subject]}/programs", new { programId = program, reason = "Recorded independent program membership" });

    private static async Task ScopedRoleAsync(SlittingPlant.Plant plant, string role, string[] permissions, Guid program)
    {
        await plant.Ok(plant.Admin, "/api/kernel/roles", new { role, displayName = role, permissions, reason = "Configure contextual business actions" });
        foreach (var permission in permissions)
            await plant.Ok(plant.Admin, $"/api/kernel/roles/{role}/permission-scopes", new { permission, scopeType = "program", scopeId = program.ToString("D"), reason = "Only the source program A" });
    }
}
