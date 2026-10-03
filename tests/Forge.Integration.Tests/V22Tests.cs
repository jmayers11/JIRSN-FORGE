using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace Forge.Integration.Tests;

/// <summary>
/// V2.2 through the real API: controlled documents (internal, external, reference) with two-person
/// approval and release, frozen released revisions, files stored by hash, documents attached to orders,
/// purchase orders and suppliers with history kept on detach, and program and marking segregation.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class V22Tests(PostgresFixture postgres)
{
    private static int key;

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

    private static async Task<HttpResponseMessage> Upload(HttpClient client, byte[] content, string name)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/engineering/document-files") { Content = new ByteArrayContent(content) };
        request.Headers.Add("Idempotency-Key", "upload-" + Interlocked.Increment(ref key));
        request.Headers.Add("X-File-Name", Uri.EscapeDataString(name));
        return await client.SendAsync(request);
    }

    private static async Task<string> Stored(HttpClient client, string text, string name)
    {
        var response = await Upload(client, Encoding.UTF8.GetBytes(text), name);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("entityId").GetString()!;
    }

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static JsonElement Revision(JsonElement detail, string revision) =>
        detail.GetProperty("document").GetProperty("revisions").EnumerateArray().Single(r => r.GetProperty("revision").GetString() == revision);

    [Fact]
    public async Task Internal_revisions_are_approved_and_released_by_someone_else_and_then_frozen()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var (e, approver) = (plant.Engineer, plant.Approver);

        // Files are stored once, by SHA-256 of their content.
        var bytes = Encoding.UTF8.GetBytes("DWG-100 rev A: slit roll 100 mm, edge burr 0.05 max");
        var sha = await Stored(e, "DWG-100 rev A: slit roll 100 mm, edge burr 0.05 max", "DWG-100_A.pdf");
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), sha);
        Assert.Equal(sha, await Stored(e, "DWG-100 rev A: slit roll 100 mm, edge burr 0.05 max", "copy.pdf"));
        Assert.Contains("empty_file", await (await Upload(e, [], "empty.pdf")).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await Upload(plant.As("approver"), bytes, "x.pdf")).StatusCode);

        await plant.Ok(e, "/api/engineering/documents", new { number = "DWG-100", title = "Slit roll 100 mm", type = "Drawing", authority = "Internal" });
        await Rejected(plant, e, "/api/engineering/documents", new { number = "dwg-100", title = "Again", type = "Drawing", authority = "Internal" }, "document_exists");
        await Rejected(plant, e, "/api/engineering/documents", new { number = "ASTM-B209", title = "Aluminum sheet", type = "Standard", authority = "External" }, "external_authority_required");
        Assert.Equal(HttpStatusCode.NotFound, (await Try(plant, e, "/api/engineering/documents/DWG-100/revisions", new { revision = "A", fileSha256 = new string('0', 64), fileName = "x.pdf" })).Status);
        await plant.Ok(e, "/api/engineering/documents/DWG-100/revisions", new { revision = "A", fileSha256 = sha, fileName = "DWG-100_A.pdf", changeNote = "Initial release", reviewBy = Day(-1) });
        await Rejected(plant, e, "/api/engineering/documents/DWG-100/revisions", new { revision = "B" }, "open_revision");
        await Rejected(plant, approver, "/api/engineering/documents/DWG-100/revisions/A/approve", new { }, "invalid_status");

        // The author submits; someone else approves; a release needs release authority and a reason.
        await plant.Ok(e, "/api/engineering/documents/DWG-100/revisions/A/submit", new { });
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, e, "/api/engineering/documents/DWG-100/revisions/A/approve", new { })).Status);
        await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["eng"]}/roles", new { role = "DocumentController", reason = "Covers document control" });
        await Rejected(plant, e, "/api/engineering/documents/DWG-100/revisions/A/approve", new { }, "separation_of_duties");
        await plant.Ok(approver, "/api/engineering/documents/DWG-100/revisions/A/approve", new { });
        await Rejected(plant, e, "/api/engineering/documents/DWG-100/revisions/A/release", new { reason = "ECN-7" }, "separation_of_duties");
        await Rejected(plant, approver, "/api/engineering/documents/DWG-100/revisions/A/release", new { }, "reason_required");
        await Rejected(plant, approver, "/api/engineering/documents/DWG-100/revisions/A/release", new { effectiveFrom = Day(-3), reason = "ECN-7" }, "effective_in_past");
        await plant.Ok(approver, "/api/engineering/documents/DWG-100/revisions/A/release", new { reason = "ECN-7" });

        var detail = await Get(planner, "/api/engineering/documents/DWG-100");
        var a = Revision(detail, "A");
        Assert.Equal("Released", a.GetProperty("status").GetString());
        Assert.Equal(Day(0), a.GetProperty("effectiveFrom").GetString());
        Assert.Equal($"person:{plant.People["approver"]}", a.GetProperty("releasedBy").GetString());

        // Anyone with engineering read may download it: never rendered, always a download.
        var file = await planner.GetAsync("/api/engineering/documents/DWG-100/revisions/A/file");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("application/octet-stream", file.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", file.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal(bytes, await file.Content.ReadAsByteArrayAsync());

        // Released revisions are frozen in the database, and revisions are never deleted.
        await using (var db = new NpgsqlConnection(app.ConnectionString))
        {
            await db.OpenAsync();
            await using var edit = new NpgsqlCommand("UPDATE engineering.document_revision SET change_note = 'edited' WHERE document_number = 'DWG-100'", db);
            await Assert.ThrowsAsync<PostgresException>(() => edit.ExecuteNonQueryAsync());
            await using var delete = new NpgsqlCommand("DELETE FROM engineering.document_revision WHERE document_number = 'DWG-100'", db);
            await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
            await using var file2 = new NpgsqlCommand("UPDATE engineering.document_file SET stored_by = 'x'", db);
            await Assert.ThrowsAsync<PostgresException>(() => file2.ExecuteNonQueryAsync());
        }

        // Revision B supersedes A on the day it takes effect.
        var shaB = await Stored(e, "DWG-100 rev B: edge burr 0.03 max", "DWG-100_B.pdf");
        await plant.Ok(e, "/api/engineering/documents/DWG-100/revisions", new { revision = "B", fileSha256 = shaB, fileName = "DWG-100_B.pdf" });
        await plant.Ok(e, "/api/engineering/documents/DWG-100/revisions/B/submit", new { });
        await plant.Ok(approver, "/api/engineering/documents/DWG-100/revisions/B/return", new { reason = "Tolerance needs a note" });
        await plant.Ok(e, "/api/engineering/documents/DWG-100/revisions/B", new { fileSha256 = shaB, fileName = "DWG-100_B.pdf", changeNote = "Tighter burr limit per customer" });
        await plant.Ok(e, "/api/engineering/documents/DWG-100/revisions/B/submit", new { });
        await plant.Ok(approver, "/api/engineering/documents/DWG-100/revisions/B/approve", new { });
        await plant.Ok(approver, "/api/engineering/documents/DWG-100/revisions/B/release", new { effectiveFrom = Day(2), reason = "ECN-9" });
        detail = await Get(planner, "/api/engineering/documents/DWG-100");
        Assert.Equal("Superseded", Revision(detail, "A").GetProperty("status").GetString());
        Assert.Equal(Day(2), Revision(detail, "A").GetProperty("effectiveTo").GetString());
        Assert.Equal("Released", Revision(detail, "B").GetProperty("status").GetString());
        await Rejected(plant, approver, "/api/engineering/documents/DWG-100/revisions/A/obsolete", new { reason = "x" }, "not_current");

        // External documents are recorded as applicable, never released or reviewed by Forge.
        await plant.Ok(e, "/api/engineering/documents", new { number = "ASTM-B209", title = "Aluminum and aluminum-alloy sheet and plate", type = "Standard", authority = "External", externalAuthority = "ASTM International", externalNumber = "B209" });
        var shaAstm = await Stored(e, "ASTM B209-21 (licensed copy)", "B209-21.pdf");
        await plant.Ok(e, "/api/engineering/documents/ASTM-B209/revisions", new { revision = "21", fileSha256 = shaAstm, fileName = "B209-21.pdf" });
        await Rejected(plant, e, "/api/engineering/documents/ASTM-B209/revisions/21/submit", new { }, "wrong_authority");
        await plant.Ok(approver, "/api/engineering/documents/ASTM-B209/revisions/21/release", new { reason = "Customer specification cites B209-21" });
        Assert.Equal("Applicable", Revision(await Get(planner, "/api/engineering/documents/ASTM-B209"), "21").GetProperty("status").GetString());

        // The register shows each document, the revision in force and what needs doing.
        await plant.Ok(e, "/api/engineering/documents", new { number = "WI-SLIT-01", title = "Slitter threading", type = "WorkInstruction", authority = "Internal" });
        var register = await Get(planner, $"/api/reports/document-register?from={Day(-1)}&to={Day(0)}");
        var rows = register.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("cells")).ToList();
        int Col(string k) => register.GetProperty("columns").EnumerateArray().Select((c, i) => (c, i)).Single(x => x.c.GetProperty("key").GetString() == k).i;
        var dwg = rows.Single(r => r[Col("document")].GetString() == "DWG-100");
        Assert.Equal("B (Released)", dwg[Col("current")].GetString());
        Assert.Equal("Revision", rows.Single(r => r[Col("document")].GetString() == "WI-SLIT-01")[Col("attention")].GetString());
        Assert.Equal("External (ASTM International)", rows.Single(r => r[Col("document")].GetString() == "ASTM-B209")[Col("authority")].GetString());
    }

    [Fact]
    public async Task Documents_attached_to_orders_and_purchase_orders_stay_in_their_history()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var (e, approver) = (plant.Engineer, plant.Approver);
        await plant.Person("bob", "Buyer");
        await plant.Person("pat", "PurchasingManager");
        var (bob, pat) = (plant.As("bob"), plant.As("pat"));
        var order = Id(await plant.Ok(planner, "/api/orders/", new { partNumber = "ROLL-100", quantity = 10m, dueDate = Day(10), priority = 3 }));
        var orderUrl = $"/api/engineering/attachments/ProductionOrder/{order}";

        // A drawing, released.
        var sha = await Stored(e, "DWG-100 A", "DWG-100_A.pdf");
        await plant.Ok(e, "/api/engineering/documents", new { number = "DWG-100", title = "Slit roll 100 mm", type = "Drawing", authority = "Internal" });
        await plant.Ok(e, "/api/engineering/documents/DWG-100/revisions", new { revision = "A", fileSha256 = sha, fileName = "DWG-100_A.pdf" });
        await Rejected(plant, planner, orderUrl, new { documentNumber = "DWG-100", revision = "A", purpose = "Follow" }, "revision_not_released");
        await plant.Ok(e, "/api/engineering/documents/DWG-100/revisions/A/submit", new { });
        await plant.Ok(approver, "/api/engineering/documents/DWG-100/revisions/A/approve", new { });
        await plant.Ok(approver, "/api/engineering/documents/DWG-100/revisions/A/release", new { reason = "ECN-7" });

        // People with order access attach a file (as an uncontrolled reference) or a controlled document.
        var po = await Stored(planner, "Customer PO 4471: 10 rolls, deliver by the 15th", "PO-4471.pdf");
        var attached = await plant.Ok(planner, $"{orderUrl}/file", new { fileSha256 = po, fileName = "PO-4471.pdf", title = "Customer PO 4471", type = "PurchaseDocument", note = "Quantity confirmed by phone" });
        Assert.Contains("REF-000001", attached.GetProperty("message").GetString(), StringComparison.Ordinal);
        await plant.Ok(planner, orderUrl, new { documentNumber = "DWG-100", purpose = "Follow", note = "Customer drawing" });
        Assert.Contains("already attached", (await plant.Ok(planner, orderUrl, new { documentNumber = "DWG-100", purpose = "Follow" })).GetProperty("message").GetString(), StringComparison.Ordinal);
        await Rejected(plant, planner, orderUrl, new { documentNumber = "REF-000001", purpose = "Follow" }, "reference_not_authority");

        var list = await Get(planner, orderUrl);
        Assert.Equal(2, list.GetArrayLength());
        var reference = list.EnumerateArray().Single(a => a.GetProperty("documentNumber").GetString() == "REF-000001");
        Assert.Equal("Reference", reference.GetProperty("authority").GetString());
        Assert.Equal("Information", reference.GetProperty("purpose").GetString());
        Assert.Equal("Reference", reference.GetProperty("state").GetString());
        Assert.Equal("PO-4471.pdf", reference.GetProperty("revision").GetProperty("fileName").GetString());
        var drawing = list.EnumerateArray().Single(a => a.GetProperty("documentNumber").GetString() == "DWG-100");
        Assert.Equal("Current", drawing.GetProperty("state").GetString());
        Assert.Equal("A", drawing.GetProperty("revision").GetProperty("revision").GetString());

        // The order's record decides who may see and attach: no order access, no order.
        Assert.Equal(HttpStatusCode.NotFound, (await Try(plant, pat, orderUrl, new { documentNumber = "DWG-100", purpose = "Information" })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await pat.GetAsync(orderUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await planner.GetAsync("/api/engineering/attachments/ProductionOrder/999999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await planner.GetAsync("/api/engineering/attachments/Spaceship/1")).StatusCode);

        // Detaching needs a reason and keeps the history.
        var linkId = reference.GetProperty("linkId").GetInt64();
        await Rejected(plant, planner, $"/api/engineering/attachment-links/{linkId}/detach", new { }, "reason_required");
        await plant.Ok(planner, $"/api/engineering/attachment-links/{linkId}/detach", new { reason = "Superseded by PO 4471 rev 1" });
        Assert.Single((await Get(planner, orderUrl)).EnumerateArray());
        var history = await Get(planner, orderUrl + "?history=true");
        var detached = history.EnumerateArray().Single(a => a.GetProperty("linkId").GetInt64() == linkId);
        Assert.Equal($"person:{plant.People["planner"]}", detached.GetProperty("detachedBy").GetString());
        Assert.Equal("Superseded by PO 4471 rev 1", detached.GetProperty("detachReason").GetString());

        // Purchase orders and suppliers carry documents too: a supplier certificate, a PO quote.
        await plant.Ok(bob, "/api/procurement/suppliers", new { code = "ALCOA", name = "Alcoa" });
        var poNumber = Id(await plant.Ok(bob, "/api/procurement/purchase-orders", new { supplier = "ALCOA", lines = new[] { new { partNumber = "COIL-AL", quantity = 100m, promisedDate = Day(14) } } }));
        var quote = await Stored(bob, "Alcoa quote Q-881", "Q-881.pdf");
        await plant.Ok(bob, $"/api/engineering/attachments/PurchaseOrder/{poNumber}/file", new { fileSha256 = quote, fileName = "Q-881.pdf", type = "PurchaseDocument" });
        var cert = await Stored(bob, "Alcoa AS9100 certificate", "as9100.pdf");
        await plant.Ok(bob, "/api/engineering/attachments/Supplier/alcoa/file", new { fileSha256 = cert, fileName = "as9100.pdf", title = "AS9100 certificate", type = "Certificate" });
        Assert.Equal("Q-881.pdf", Assert.Single((await Get(pat, $"/api/engineering/attachments/PurchaseOrder/{poNumber}")).EnumerateArray()).GetProperty("title").GetString());
        Assert.Equal("AS9100 certificate", Assert.Single((await Get(pat, "/api/engineering/attachments/Supplier/ALCOA")).EnumerateArray()).GetProperty("title").GetString());

        // A document shows where it is attached, including what was detached.
        var where = await Get(planner, "/api/engineering/documents/REF-000001");
        Assert.NotEqual(JsonValueKind.Null, Assert.Single(where.GetProperty("links").EnumerateArray()).GetProperty("detachedAt").ValueKind);

        await using var db = new NpgsqlConnection(app.ConnectionString);
        await db.OpenAsync();
        await using var tamper = new NpgsqlCommand("DELETE FROM engineering.document_link", db);
        await Assert.ThrowsAsync<PostgresException>(() => tamper.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Program_documents_and_marked_documents_are_invisible_without_access()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var e = plant.Engineer;
        await plant.Person("pm", "ProgramManager");
        await plant.Person("outsider", "Planner");
        var (pm, outsider) = (plant.As("pm"), plant.As("outsider"));
        var falcon = Guid.Parse((await plant.Ok(pm, "/api/programs/", new { code = "FALCON", name = "Program Falcon", customer = "USAF" })).GetProperty("entityId").GetString()!);
        foreach (var who in new[] { "pm", "planner", "eng" })
        {
            await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People[who]}/programs", new { programId = falcon, reason = "Falcon team" });
        }

        var programOrder = Id(await plant.Ok(planner, "/api/orders/", new { partNumber = "ROLL-100", quantity = 5m, dueDate = Day(20), priority = 2, programId = falcon }));
        var plainOrder = Id(await plant.Ok(planner, "/api/orders/", new { partNumber = "ROLL-100", quantity = 5m, dueDate = Day(20), priority = 2 }));

        // A program document: visible to the program team only, and only attachable to that program's records.
        await plant.Ok(e, "/api/engineering/documents", new { number = "FAL-SPEC-1", title = "Falcon roll specification", type = "Specification", authority = "External", externalAuthority = "USAF", programId = falcon });
        await Rejected(plant, planner, $"/api/engineering/attachments/ProductionOrder/{plainOrder}", new { documentNumber = "FAL-SPEC-1" }, "program_mismatch");
        await plant.Ok(planner, $"/api/engineering/attachments/ProductionOrder/{programOrder}", new { documentNumber = "FAL-SPEC-1" });
        var sow = await Stored(planner, "Falcon statement of work", "sow.pdf");
        await plant.Ok(planner, $"/api/engineering/attachments/ProductionOrder/{programOrder}/file", new { fileSha256 = sow, fileName = "sow.pdf", type = "Contract" });
        Assert.Equal(2, (await Get(planner, $"/api/engineering/attachments/ProductionOrder/{programOrder}")).GetArrayLength());

        Assert.DoesNotContain((await Get(outsider, "/api/engineering/documents")).EnumerateArray(), d => d.GetProperty("number").GetString() is "FAL-SPEC-1" or "REF-000001");
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync("/api/engineering/documents/FAL-SPEC-1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync("/api/engineering/documents/REF-000001")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/engineering/attachments/ProductionOrder/{programOrder}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync("/api/engineering/documents/REF-000001/revisions/1/file")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Try(plant, outsider, $"/api/engineering/attachments/ProductionOrder/{plainOrder}", new { documentNumber = "FAL-SPEC-1" })).Status);

        // Marked documents: only people holding every marking may create, see or download them.
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, e, "/api/engineering/documents", new { number = "EXP-1", title = "Export-controlled tooling drawing", type = "Drawing", authority = "Internal", markings = "ExportControlled" })).Status);
        var cleared = Guid.Parse((await plant.Ok(plant.Admin, "/api/kernel/people", new { idpSubject = "itar", displayName = "Ira Cleared", markings = new[] { "ExportControlled" }, reason = "US person, export control briefing" })).GetProperty("entityId").GetString()!);
        await plant.Ok(plant.Admin, $"/api/kernel/people/{cleared}/roles", new { role = "ManufacturingEngineer", reason = "Tooling engineer" });
        var itar = plant.As("itar");
        await plant.Ok(itar, "/api/engineering/documents", new { number = "EXP-1", title = "Export-controlled tooling drawing", type = "Drawing", authority = "Internal", markings = "ExportControlled" });
        var drawing = await Stored(itar, "EXP-1 tooling", "exp1.pdf");
        await plant.Ok(itar, "/api/engineering/documents/EXP-1/revisions", new { revision = "A", fileSha256 = drawing, fileName = "exp1.pdf" });
        Assert.Contains((await Get(itar, "/api/engineering/documents")).EnumerateArray(), d => d.GetProperty("number").GetString() == "EXP-1");
        Assert.DoesNotContain((await Get(planner, "/api/engineering/documents")).EnumerateArray(), d => d.GetProperty("number").GetString() == "EXP-1");
        Assert.Equal(HttpStatusCode.NotFound, (await planner.GetAsync("/api/engineering/documents/EXP-1/revisions/A/file")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await itar.GetAsync("/api/engineering/documents/EXP-1/revisions/A/file")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, planner, $"/api/engineering/attachments/ProductionOrder/{plainOrder}/file", new { fileSha256 = sow, fileName = "x.pdf", markings = "ExportControlled" })).Status);
        var register = await Get(planner, $"/api/reports/document-register?from={Day(-1)}&to={Day(0)}");
        Assert.DoesNotContain(register.GetProperty("rows").EnumerateArray(), r => r.GetProperty("cells")[0].GetString() == "EXP-1");
    }
}
