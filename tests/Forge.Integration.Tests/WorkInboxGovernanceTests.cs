using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Forge.Kernel.Security;
using Npgsql;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class WorkInboxGovernanceTests(PostgresFixture postgres)
{
    private static string Day(int days) => DateTime.UtcNow.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);
    private static async Task<JsonElement> Get(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path}: {response.StatusCode}: {body}");
        using var json = JsonDocument.Parse(body);
        return json.RootElement.Clone();
    }
    private static bool Has(JsonElement inbox, string type, object id) => inbox.GetProperty("approvals").EnumerateArray()
        .Any(item => item.GetProperty("workType").GetString() == type && item.GetProperty("workId").GetString() == Convert.ToString(id, CultureInfo.InvariantCulture));
    private static async Task<string> Upload(HttpClient client, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/engineering/document-files") { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("Recorded inbox acceptance evidence")) };
        request.Headers.Add("Idempotency-Key", key);
        request.Headers.Add("X-File-Name", "evidence.pdf");
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("entityId").GetString()!;
    }

    [Fact]
    public async Task Inbox_uses_actual_private_program_scoped_authority_and_assignments_keep_audit_event_retry_and_current_access()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("wi-programs", "ProgramManager");
        await plant.Person("wi-proposer", "QualityEngineer", "QualityManager");
        var recipient = await plant.Person("wi-recipient", "QualityManager");
        await plant.Person("wi-outsider", "QualityManager");
        var programA = Guid.Parse((await plant.Ok(plant.As("wi-programs"), "/api/programs/", new { code = "WI-A", name = "Permitted inbox program" })).GetProperty("entityId").GetString()!);
        var programB = Guid.Parse((await plant.Ok(plant.As("wi-programs"), "/api/programs/", new { code = "WI-B", name = "Private inbox program" })).GetProperty("entityId").GetString()!);
        await plant.Ok(plant.Admin, "/api/kernel/roles", new { role = "InboxProgramReviewer", displayName = "Program A inbox reviewer", permissions = new[] { Permissions.QualityRead, Permissions.QualityApprove }, reason = "Configure real scoped review authority" });
        foreach (var permission in new[] { Permissions.QualityRead, Permissions.QualityApprove })
            await plant.Ok(plant.Admin, "/api/kernel/roles/InboxProgramReviewer/permission-scopes", new { permission, scopeType = "program", scopeId = programA.ToString("D"), reason = "Program A decisions only" });
        await plant.Person("wi-scoped", "InboxProgramReviewer");
        foreach (var subject in new[] { "eng", "planner", "wi-proposer", "wi-scoped" })
        foreach (var program in new[] { programA, programB })
            await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People[subject]}/programs", new { programId = program, reason = "Recorded source membership" });
        await plant.Ok(plant.Admin, $"/api/kernel/people/{recipient}/programs", new { programId = programA, reason = "Independent reviewer access" });
        var ncrs = new List<long>();
        var orders = new List<long>();
        foreach (var program in new[] { programA, programB })
        {
            var order = Id(await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = "ROLL-100", quantity = 10m, dueDate = Day(10), priority = 3, programId = program }));
            await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Release factual inbox source" });
            orders.Add(order);
            var ncr = Id(await plant.Ok(plant.As("wi-proposer"), "/api/quality/ncrs", new { partNumber = "ROLL-100", orderNumber = order, sequence = 10, quantity = 1m, defect = program == programB ? "WI SECRET NCR B" : "Permitted NCR A", description = "Actual private source context", reason = "Observed defect" }));
            await plant.Ok(plant.As("wi-proposer"), $"/api/quality/ncrs/{ncr}/disposition", new { disposition = "UseAsIs", note = "Recorded concession", reason = "Request independent review" });
            ncrs.Add(ncr);
        }
        var reviewer = plant.As("wi-recipient");
        var inbox = await Get(reviewer, "/api/work-inbox");
        Assert.True(Has(inbox, "NCR", ncrs[0]));
        Assert.False(Has(inbox, "NCR", ncrs[1]));
        Assert.DoesNotContain("WI SECRET NCR B", inbox.GetRawText(), StringComparison.Ordinal);
        Assert.False(Has(await Get(plant.As("wi-proposer"), "/api/work-inbox"), "NCR", ncrs[0]));
        Assert.False(Has(await Get(plant.As("wi-outsider"), "/api/work-inbox"), "NCR", ncrs[0]));
        var scoped = await Get(plant.As("wi-scoped"), "/api/work-inbox");
        Assert.True(Has(scoped, "NCR", ncrs[0]));
        Assert.False(Has(scoped, "NCR", ncrs[1]));
        var scopedSourceList = await Get(plant.As("wi-scoped"), "/api/quality/ncrs");
        Assert.Equal(ncrs[0], Assert.Single(scopedSourceList.EnumerateArray()).GetProperty("ncrId").GetInt64());
        Assert.Equal(orders[0], (await Get(plant.As("wi-scoped"), $"/api/quality/orders/{orders[0]}")).GetProperty("quality").GetProperty("orderNumber").GetInt64());
        Assert.Equal(HttpStatusCode.NotFound, (await plant.As("wi-scoped").GetAsync($"/api/quality/orders/{orders[1]}")).StatusCode);
        await plant.Ok(plant.Admin, $"/api/kernel/roles/InboxProgramReviewer/permissions/{Permissions.QualityApprove}/revoke", new { reason = "The inbox must honor current permission withdrawal" });
        Assert.False(Has(await Get(plant.As("wi-scoped"), "/api/work-inbox"), "NCR", ncrs[0]));

        var assignmentBody = new { workType = "NCR", workId = ncrs[0].ToString(CultureInfo.InvariantCulture), title = "Review permitted NCR A", deepLink = "#/quality", assigneePersonId = recipient, reason = "Assign the authorized independent reviewer" };
        var first = await plant.Engineer.CommandAsync("/api/work-inbox/assignments", assignmentBody, "wi-assignment-once");
        var firstBody = await first.Content.ReadAsStringAsync();
        Assert.True(first.IsSuccessStatusCode, firstBody);
        var repeated = await plant.Engineer.CommandAsync("/api/work-inbox/assignments", assignmentBody, "wi-assignment-once");
        Assert.Equal(firstBody, await repeated.Content.ReadAsStringAsync());
        using var created = JsonDocument.Parse(firstBody);
        var assignmentId = Guid.Parse(created.RootElement.GetProperty("entityId").GetString()!);
        var acknowledged = new { status = "Acknowledged", reason = "Incoming reviewer acknowledges the assignment" };
        var acknowledgement = await reviewer.CommandAsync($"/api/work-inbox/assignments/{assignmentId}/status", acknowledged, "wi-ack-once");
        Assert.True(acknowledgement.IsSuccessStatusCode, await acknowledgement.Content.ReadAsStringAsync());
        var acknowledgementBody = await acknowledgement.Content.ReadAsStringAsync();
        Assert.Equal(acknowledgementBody, await (await reviewer.CommandAsync($"/api/work-inbox/assignments/{assignmentId}/status", acknowledged, "wi-ack-once")).Content.ReadAsStringAsync());
        await plant.Ok(plant.Engineer, "/api/work-inbox/assignments", assignmentBody);
        Assert.Equal("Acknowledged", Assert.Single((await Get(reviewer, "/api/work-inbox")).GetProperty("assignments").EnumerateArray()).GetProperty("status").GetString());
        await plant.Ok(plant.Admin, $"/api/kernel/people/{recipient}/programs/{programA}/revoke", new { reason = "Withdraw current source access" });
        var revoked = await Get(reviewer, "/api/work-inbox");
        Assert.Empty(revoked.GetProperty("assignments").EnumerateArray());
        Assert.False(Has(revoked, "NCR", ncrs[0]));
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(reviewer, $"/api/work-inbox/assignments/{assignmentId}/status", new { status = "InProgress", reason = "A retained assignment cannot preserve revoked access" })).StatusCode);
        await plant.Ok(plant.Admin, $"/api/kernel/people/{recipient}/programs", new { programId = programA, reason = "Restore independently granted access" });
        foreach (var status in new[] { "InProgress", "Completed" })
            await plant.Ok(reviewer, $"/api/work-inbox/assignments/{assignmentId}/status", new { status, reason = $"Record actual assignment {status}" });
        Assert.Equal("Completed", Assert.Single((await Get(reviewer, "/api/work-inbox")).GetProperty("assignments").EnumerateArray()).GetProperty("status").GetString());
        Assert.Equal(acknowledgementBody, await (await reviewer.CommandAsync($"/api/work-inbox/assignments/{assignmentId}/status", acknowledged, "wi-ack-once")).Content.ReadAsStringAsync());

        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync();
        await using var proof = new NpgsqlCommand("SELECT (SELECT count(*) FROM kernel.work_assignment_history WHERE assignment_id=$1),(SELECT count(*) FROM kernel.audit_log WHERE entity_type='WorkAssignment' AND entity_id=$2 AND outcome='Succeeded'),(SELECT count(*) FROM kernel.outbox WHERE envelope::jsonb->>'entityType'='WorkAssignment' AND envelope::jsonb->>'entityId'=$2 AND envelope::jsonb->>'eventType' IN ('WorkAssigned','WorkAssignmentStatusChanged')),(SELECT count(*) FROM kernel.outbox WHERE envelope::jsonb->>'entityType'='WorkAssignment' AND envelope::jsonb->>'entityId'=$2 AND envelope::jsonb->>'eventType'='AuthorizationDenied')", connection);
        proof.Parameters.AddWithValue(assignmentId);
        proof.Parameters.AddWithValue(assignmentId.ToString("D"));
        await using var reader = await proof.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal((4L, 4L, 4L), (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)));
        Assert.Equal(1L, reader.GetInt64(3));
    }

    [Fact]
    public async Task Inbox_catalog_contains_real_pending_decisions_and_honors_actual_budget_and_document_markings()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("wi-writer", "QualityEngineer", "QualityManager");
        await plant.Person("wi-review", "QualityEngineer", "QualityManager", "InventoryController", "PurchasingManager", "FacilitiesManager", "EngineeringApprover");
        await plant.Person("wi-buyer", "Buyer", "PurchasingManager");
        await plant.Person("wi-access", "SecurityAdministrator");
        var writer = plant.As("wi-writer");
        var reviewer = plant.As("wi-review");
        var buyer = plant.As("wi-buyer");
        await plant.Ok(plant.Engineer, "/api/engineering/items", new { partNumber = "WI-ADH", description = "Shelf-life inbox source", unitOfMeasure = "EA", makeBuy = "Buy", leadTimeDays = 1, lotTracked = true });
        await plant.Ok(plant.Engineer, "/api/engineering/items/WI-ADH/revisions", new { revision = "A" });
        await plant.Ok(plant.Approver, "/api/engineering/items/WI-ADH/revisions/A/release", new { reason = "Release the existing engineering source" });
        await plant.Ok(plant.As("stores"), "/api/inventory/receipts", new { partNumber = "WI-ADH", quantity = 10m, locationCode = "STORES", lotNumber = "WI-LOT", revision = "A", expiresOn = Day(1), reason = "Record source expiry" });
        var extension = Id(await plant.Ok(writer, "/api/inventory/lots/WI-ADH/WI-LOT/extensions", new { newExpires = Day(5), evidence = "Supplier retest report", reason = "Request independent shelf-life decision" }));
        var adjustment = Id(await plant.Ok(plant.As("stores"), "/api/inventory/adjustments", new { partNumber = "COIL-AL", locationCode = "STORES", change = -1m, reason = "Observed damaged quantity" }));
        var receipt = Id(await plant.Ok(plant.As("stores"), "/api/inventory/receipts", new { partNumber = "COIL-AL", quantity = 5m, locationCode = "STORES", inspect = true, reason = "Inspect actual inbound material" }));
        await plant.Ok(writer, $"/api/inventory/receiving/{receipt}/inspections", new { accepted = 4m, rejected = 1m, defect = "Cosmetic wrap" });
        var rejection = Assert.Single((await Get(writer, "/api/inventory/rejections?open=true")).EnumerateArray()).GetProperty("rejectionId").GetInt64();
        await plant.Ok(writer, $"/api/inventory/rejections/{rejection}/disposition", new { disposition = "UseAsIs", reason = "Cosmetic defect needs a second person's approval" });
        await plant.Ok(plant.Engineer, "/api/execution/reason-codes", new { code = "WI-JAM", kind = "Downtime", category = "Unplanned", description = "Recorded web jam" });
        var stop = Id(await plant.Ok(plant.Engineer, "/api/execution/stoppages", new { machineCode = "SLITTER-A", category = "Unplanned", reasonCode = "WI-JAM" }));
        var cause = Id(await plant.Ok(plant.Engineer, $"/api/execution/stoppages/{stop}/root-causes", new { cause = "Broken splice tape", evidence = "Tape sample and machine log" }));
        await plant.Ok(buyer, "/api/procurement/suppliers", new { code = "WI-SUP", name = "Inbox supplier" });
        var need = Id(await plant.Ok(buyer, "/api/procurement/requisitions", new { partNumber = "COIL-AL", quantity = 10m, needDate = Day(10), sourceKind = "Internal", sourceKey = "internal:wi-need", sourceLabel = "Factual sourcing need", requiredRevision = "A" }));
        var rfq = Id(await plant.Ok(buyer, "/api/procurement/rfqs", new { requisitionId = need, title = "Actual RFQ inbox source", supplierCodes = new[] { "WI-SUP" } }));
        var before = await Get(reviewer, "/api/work-inbox");
        foreach (var pair in new[] { ("ShelfLifeExtension", extension), ("Adjustment", adjustment), ("SupplierUseAsIs", rejection), ("RootCause", cause), ("RFQRelease", rfq) })
            Assert.True(Has(before, pair.Item1, pair.Item2), $"Missing actual {pair.Item1} decision: {before}");
        var own = await Get(writer, "/api/work-inbox");
        Assert.False(Has(own, "ShelfLifeExtension", extension));
        Assert.False(Has(own, "SupplierUseAsIs", rejection));
        await plant.Ok(reviewer, $"/api/procurement/rfqs/{rfq}/release", new { reason = "Independent applicable package review" });
        var sha = await Upload(buyer, "wi-original-file");
        var attached = Id(await plant.Ok(buyer, $"/api/engineering/attachments/Rfq/{rfq}/file", new { fileSha256 = sha, fileName = "evidence.pdf", type = "Other" }));
        var original = (await Get(buyer, $"/api/engineering/attachments/Rfq/{rfq}")).EnumerateArray().Single(link => link.GetProperty("linkId").GetInt64() == attached).GetProperty("documentNumber").GetString()!;
        var revision = (await Get(buyer, $"/api/engineering/documents/{original}")).GetProperty("document").GetProperty("revisions")[0].GetProperty("revision").GetString();
        var quote = Id(await plant.Ok(buyer, $"/api/procurement/rfqs/{rfq}/quotes", new { supplierCode = "WI-SUP", quantity = 10m, unitPrice = 5m, currency = "USD", promisedDate = Day(5), validThrough = Day(7), originalDocumentNumber = original, originalDocumentRevision = revision, reason = "Record actual supplier evidence" }));
        Assert.True(Has(await Get(reviewer, "/api/work-inbox"), "SourceSelection", $"{rfq}/{quote}"));
        Assert.False(Has(await Get(buyer, "/api/work-inbox"), "SourceSelection", $"{rfq}/{quote}"));
        await plant.Ok(plant.Admin, $"/api/kernel/approval-thresholds/{Permissions.PurchasingApprove}", new { maximumAmount = 49m, currency = "USD", reason = "Quoted value must govern the decision list" });
        Assert.False(Has(await Get(reviewer, "/api/work-inbox"), "SourceSelection", $"{rfq}/{quote}"));
        await plant.Ok(plant.Admin, $"/api/kernel/approval-thresholds/{Permissions.PurchasingApprove}", new { maximumAmount = 50m, currency = "USD", reason = "Exact quote approval boundary" });
        Assert.True(Has(await Get(reviewer, "/api/work-inbox"), "SourceSelection", $"{rfq}/{quote}"));
        await plant.Ok(plant.Admin, $"/api/kernel/approval-thresholds/{Permissions.ProjectsApprove}", new { maximumAmount = 1000m, currency = "USD", reason = "Real project budget governs the decision list" });
        foreach (var budget in new[] { 1000m, 1001m })
            await plant.Ok(plant.Engineer, "/api/projects/", new { code = "WI-P-" + budget.ToString(CultureInfo.InvariantCulture), title = "Recorded facilities budget", type = "Facility", funding = "Operating", budget, startDate = Day(1), endDate = Day(10) });
        var budgets = await Get(reviewer, "/api/work-inbox");
        Assert.True(Has(budgets, "Project", "WI-P-1000"));
        Assert.False(Has(budgets, "Project", "WI-P-1001"));

        var markedAuthor = Guid.Parse((await plant.Ok(plant.Admin, "/api/kernel/people", new { idpSubject = "wi-marked-author", displayName = "Cleared document author", markings = new[] { "ExportControlled" }, reason = "Recorded document clearance" })).GetProperty("entityId").GetString()!);
        await plant.Ok(plant.Admin, $"/api/kernel/people/{markedAuthor}/roles", new { role = "ManufacturingEngineer", reason = "Controlled document author" });
        var author = plant.As("wi-marked-author");
        await plant.Ok(author, "/api/engineering/documents", new { number = "WI-CONTROLLED", title = "WI SECRET CONTROLLED DOCUMENT", type = "Procedure", authority = "Internal", markings = "ExportControlled" });
        var controlledSha = await Upload(author, "wi-controlled-file");
        await plant.Ok(author, "/api/engineering/documents/WI-CONTROLLED/revisions", new { revision = "A", fileSha256 = controlledSha, fileName = "evidence.pdf" });
        await plant.Ok(author, "/api/engineering/documents/WI-CONTROLLED/revisions/A/submit", new { reason = "Submit actual controlled content" });
        Assert.False(Has(await Get(reviewer, "/api/work-inbox"), "DocumentRevision", "WI-CONTROLLED rev A"));
        var clearedId = Guid.Parse((await plant.Ok(plant.Admin, "/api/kernel/people", new { idpSubject = "wi-marked-review", displayName = "Cleared independent reviewer", markings = new[] { "ExportControlled" }, reason = "Independent reviewer clearance" })).GetProperty("entityId").GetString()!);
        await plant.Ok(plant.Admin, $"/api/kernel/people/{clearedId}/roles", new { role = "EngineeringApprover", reason = "Document approval authority" });
        Assert.True(Has(await Get(plant.As("wi-marked-review"), "/api/work-inbox"), "DocumentRevision", "WI-CONTROLLED rev A"));
        var adminId = (await Get(plant.Admin, "/api/kernel/me")).GetProperty("personId").GetGuid();
        var delegation = Guid.Parse((await plant.Ok(plant.Engineer, "/api/kernel/delegations", new { recipient = adminId, permissions = new[] { Permissions.OrdersManage }, scopes = Array.Empty<object>(), startsAt = DateTimeOffset.UtcNow, expiresAt = DateTimeOffset.UtcNow.AddHours(1), reason = "Recorded temporary cover awaiting independent approval" })).GetProperty("entityId").GetString()!);
        Assert.False(Has(await Get(plant.Admin, "/api/work-inbox"), "Delegation", delegation));
        Assert.True(Has(await Get(plant.As("wi-access"), "/api/work-inbox"), "Delegation", delegation));
    }
}
