using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Forge.Engineering.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Audit;
using Forge.Kernel.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class V312Tests(PostgresFixture postgres)
{
    private static int key;

    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement;
    }

    private static async Task<string> Upload(HttpClient client, string content)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/engineering/document-files") { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(content)) };
        request.Headers.Add("Idempotency-Key", "v312-upload-" + Interlocked.Increment(ref key));
        request.Headers.Add("X-File-Name", "evidence.pdf");
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("entityId").GetString()!;
    }

    [Fact]
    public async Task Fai_evidence_inherits_program_scope_and_is_hidden_after_access_is_revoked()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("pm", "ProgramManager");
        await plant.Person("insp", "Inspector");
        var inspector = plant.As("insp");
        var program = Guid.Parse((await plant.Ok(plant.As("pm"), "/api/programs/", new { code = "PRIVATE", name = "Private", customer = "Acme" })).GetProperty("entityId").GetString()!);
        foreach (var person in new[] { "eng", "insp" })
            await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People[person]}/programs", new { programId = program, reason = "Program quality team" });
        var sha = await Upload(inspector, "Signed original program FAI source");
        await plant.Ok(plant.Engineer, "/api/engineering/documents", new { number = "PRIVATE-FAI", title = "Private supplier report", type = "FaiReport", authority = "Reference", programId = program });
        await plant.Ok(plant.Engineer, "/api/engineering/documents/PRIVATE-FAI/revisions", new { revision = "A", fileSha256 = sha, fileName = "private.pdf" });
        await plant.Ok(inspector, "/api/engineering/documents/PRIVATE-FAI/verify-signature", new { revision = "A", sourceSigner = "Supplier inspector", sourceSignedAt = DateTimeOffset.UtcNow.AddDays(-1), meaning = "Certified first article", reason = "Reviewed original signed file" });
        var fai = (await plant.Ok(inspector, "/api/quality/fai", new
        {
            partNumber = "ROLL-100", revision = "A", supplierCode = "ACME", passed = true,
            results = new[] { new { characteristic = "Width", requirement = "100", measured = "100", pass = true } }, documentNumber = "PRIVATE-FAI", documentRevision = "A",
        })).GetProperty("entityId").GetString()!;
        var attachmentUrl = $"/api/engineering/attachments/Fai/{fai}/file";
        var attachmentBody = new { fileSha256 = sha, fileName = "supporting.pdf", type = "MeasurementRecord" };
        const string attachmentKey = "v312-private-source-attachment";
        var original = await inspector.CommandAsync(attachmentUrl, attachmentBody, attachmentKey);
        Assert.True(original.IsSuccessStatusCode, await original.Content.ReadAsStringAsync());
        var attachment = Assert.Single((await Get(inspector, $"/api/engineering/attachments/Fai/{fai}")).EnumerateArray());
        var reference = attachment.GetProperty("documentNumber").GetString()!;
        var detail = await Get(inspector, $"/api/engineering/documents/{reference}");
        Assert.Equal(program, detail.GetProperty("document").GetProperty("programId").GetGuid());
        async Task<string> RecordedSource()
        {
            await using var read = await app.Services.GetRequiredService<IKernelUnitOfWorkFactory>().BeginSnapshotAsync(CancellationToken.None);
            var lookup = read.Store<IDocumentLookup>();
            return CanonicalJson.Serialize(new
            {
                Links = await lookup.ListLinksAsync(DocumentTargetType.Fai, fai, null, true, CancellationToken.None),
                Document = await lookup.FindDocumentAsync(reference, CancellationToken.None),
            });
        }
        var recordedSource = await RecordedSource();
        await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["insp"]}/programs/{program}/revoke", new { reason = "Program assignment ended" });
        Assert.Equal(HttpStatusCode.NotFound, (await inspector.GetAsync(new Uri($"/api/engineering/attachments/Fai/{fai}", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await inspector.GetAsync(new Uri($"/api/engineering/documents/{reference}", UriKind.Relative))).StatusCode);
        var denied = await plant.Try(inspector, $"/api/engineering/attachments/Fai/{fai}/file", new { fileSha256 = sha, fileName = "another.pdf" });
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Contains("target_not_found", await denied.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var replay = await inspector.CommandAsync(attachmentUrl, attachmentBody, attachmentKey);
        Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
        Assert.Contains("target_not_found", await replay.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var absent = await plant.Try(inspector, "/api/engineering/attachments/Fai/999999999/file", new { fileSha256 = sha, fileName = "another.pdf" });
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        Assert.Contains("target_not_found", await absent.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(recordedSource, await RecordedSource());
        var auditorId = await plant.Person("fai-audit-reader", "Inspector", "Auditor");
        await plant.Ok(plant.Admin, $"/api/kernel/people/{auditorId}/programs", new { programId = program, reason = "Independent review of protected evidence denials" });
        var audited = await Get(plant.As("fai-audit-reader"), $"/api/kernel/audit?entityType=DocumentLink&entityId={Uri.EscapeDataString("Fai:" + fai)}");
        var entries = audited.EnumerateArray().Select(row => row.GetProperty("entry").Deserialize<AuditEntry>(CanonicalJson.Options)!).ToArray();
        var sourceDenials = entries.Where(entry => entry.Action == "AttachFile" && entry.Outcome == "Denied").ToArray();
        Assert.Equal(2, sourceDenials.Length);
        Assert.All(sourceDenials, entry =>
        {
            Assert.Equal(program, entry.ProgramId?.Value);
            Assert.Equal("person:" + plant.People["insp"], entry.Actor);
            Assert.Contains("No access to the program", entry.NewValue!, StringComparison.Ordinal);
        });
        Assert.True((await Get(plant.As("fai-audit-reader"), "/api/kernel/audit/verify")).GetProperty("intact").GetBoolean());
        Assert.Empty((await Get(inspector, "/api/reports/business-evidence")).GetProperty("rows").EnumerateArray());
        Assert.DoesNotContain((await Get(inspector, "/api/reports/document-register")).GetProperty("rows").EnumerateArray(), r => r.GetProperty("cells")[0].GetString() == reference);
        Assert.Empty((await Get(inspector, "/api/quality/fai")).EnumerateArray());
    }

    [Fact]
    public async Task Certificates_do_not_complete_inspection_and_structured_results_do_not_replace_signed_evidence()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("insp", "Inspector");
        var inspector = plant.As("insp");
        var receipt = long.Parse((await plant.Ok(plant.As("stores"), "/api/inventory/receipts", new { partNumber = "COIL-AL", quantity = 10m, locationCode = "STORES", inspect = true, reason = "Receive supplier material for evidence review" })).GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);
        var sha = await Upload(inspector, "Supplier certificate, signed by Sam Supplier");
        await plant.Ok(inspector, $"/api/engineering/attachments/Receipt/{receipt}/file", new { fileSha256 = sha, fileName = "coc.pdf", type = "CertificateOfConformance" });
        var attachments = await Get(inspector, $"/api/engineering/attachments/Receipt/{receipt}");
        Assert.Equal("Reference", attachments[0].GetProperty("authority").GetString());
        Assert.Equal("Uncontrolled", attachments[0].GetProperty("revision").GetProperty("status").GetString());
        var state = (await Get(inspector, "/api/inventory/receiving")).EnumerateArray().Single(r => r.GetProperty("receipt").GetProperty("transactionId").GetInt64() == receipt);
        Assert.Equal(0m, state.GetProperty("accepted").GetDecimal());
        Assert.Equal(10m, state.GetProperty("remaining").GetDecimal());
        var report = await Get(inspector, "/api/reports/business-evidence");
        Assert.Single(report.GetProperty("rows").EnumerateArray());
        Assert.Equal("Attached", report.GetProperty("rows")[0].GetProperty("cells")[5].GetString());
        var analyst = await inspector.PostAsJsonAsync(new Uri("/api/analyst/ask", UriKind.Relative), new { question = "Show receipt evidence" });
        var answer = JsonDocument.Parse(await analyst.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("business-evidence", answer.GetProperty("interpretation").GetProperty("report").GetProperty("reportId").GetString());
        await plant.Ok(plant.Engineer, "/api/engineering/documents", new { number = "CONTROLLED-INSTRUCTION", title = "Engineering instruction", type = "WorkInstruction", authority = "Internal" });
        var controlled = await plant.Try(inspector, $"/api/engineering/attachments/Receipt/{receipt}", new { documentNumber = "CONTROLLED-INSTRUCTION", purpose = "Information" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, controlled.StatusCode);
        Assert.Contains("business_evidence_reference_only", await controlled.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await plant.Ok(plant.Engineer, "/api/engineering/documents", new { number = "SIGNED-FAI", title = "Supplier FAI", type = "FaiReport", authority = "Reference" });
        await plant.Ok(plant.Engineer, "/api/engineering/documents/SIGNED-FAI/revisions", new { revision = "A", fileSha256 = sha, fileName = "fai.pdf" });
        var results = new[] { new { characteristic = "Width", requirement = "100", measured = "100", pass = true } };
        var command = new { partNumber = "ROLL-100", revision = "A", supplierCode = "ACME", passed = true, results, documentNumber = "SIGNED-FAI", documentRevision = "A" };
        var missingSignature = await plant.Try(inspector, "/api/quality/fai", command);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missingSignature.StatusCode);
        Assert.Contains("report_not_signed", await missingSignature.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var signature = new { revision = "A", sourceSigner = "Sam Supplier", sourceSignedAt = DateTimeOffset.UtcNow.AddDays(-1), meaning = "I certify the first article results", reason = "Checked signature in original file" };
        var first = await inspector.CommandAsync("/api/engineering/documents/SIGNED-FAI/verify-signature", signature, "source-signature-once");
        var retry = await inspector.CommandAsync("/api/engineering/documents/SIGNED-FAI/verify-signature", signature, "source-signature-once");
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
        Assert.Equal(await first.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());
        await using (var database = new NpgsqlConnection(app.ConnectionString))
        {
            await database.OpenAsync();
            await using var count = new NpgsqlCommand("SELECT count(*) FROM kernel.audit_log WHERE action='VerifySignedEvidence' AND outcome='Succeeded'", database);
            Assert.Equal(1L, await count.ExecuteScalarAsync());
        }
        var missingResults = await plant.Try(inspector, "/api/quality/fai", new { partNumber = "ROLL-100", revision = "A", supplierCode = "ACME", passed = true, results = Array.Empty<object>(), documentNumber = "SIGNED-FAI", documentRevision = "A" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missingResults.StatusCode);
        Assert.Contains("results_required", await missingResults.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await plant.Ok(inspector, "/api/quality/fai", command);
        var verification = await Get(inspector, "/api/engineering/documents/SIGNED-FAI/signature-verification/A");
        Assert.Equal(sha, verification.GetProperty("fileSha256").GetString());
        Assert.Equal($"person:{plant.People["insp"]}", verification.GetProperty("verifiedBy").GetString());
        Assert.Single((await Get(inspector, "/api/quality/fai")).EnumerateArray());

        var denied = await plant.Try(plant.As("stores"), "/api/engineering/documents/SIGNED-FAI/verify-signature", new { revision = "A", sourceSigner = "Sam", sourceSignedAt = DateTimeOffset.UtcNow.AddDays(-1), meaning = "Verified", reason = "Review" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var absent = await plant.Try(inspector, "/api/engineering/attachments/Receipt/999999/file", new { fileSha256 = sha, fileName = "coc.pdf" });
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
    }
}
