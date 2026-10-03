using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace Forge.Integration.Tests;

/// <summary>
/// V2.3 through the real API: item revisions list the documents they are built to; the applicability
/// engine decides which revision applies (date first, rules on top, never a guess); released orders are
/// compared with what applied at release; documentation blocks readiness only where the policy enforces
/// it; receipts record the part revision; inspection plans follow the revision built.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class V23Tests(PostgresFixture postgres)
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

    private static async Task<string> Stored(HttpClient client, string text)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/engineering/document-files") { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(text)) };
        request.Headers.Add("Idempotency-Key", "v23-upload-" + Interlocked.Increment(ref key));
        request.Headers.Add("X-File-Name", "file.pdf");
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("entityId").GetString()!;
    }

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    /// <summary>Creates, submits, approves and releases one revision of an internal document.</summary>
    private static async Task ReleaseAsync(SlittingPlant.Plant plant, string number, string revision, string? effectiveFrom = null)
    {
        var sha = await Stored(plant.Engineer, $"{number} rev {revision}");
        await plant.Ok(plant.Engineer, $"/api/engineering/documents/{number}/revisions", new { revision, fileSha256 = sha, fileName = $"{number}_{revision}.pdf" });
        await plant.Ok(plant.Engineer, $"/api/engineering/documents/{number}/revisions/{revision}/submit", new { });
        await plant.Ok(plant.Approver, $"/api/engineering/documents/{number}/revisions/{revision}/approve", new { });
        await plant.Ok(plant.Approver, $"/api/engineering/documents/{number}/revisions/{revision}/release", new { effectiveFrom, reason = "ECN" });
    }

    private static JsonElement Doc(JsonElement documentation, string number) =>
        documentation.GetProperty("documents").EnumerateArray().Single(d => d.GetProperty("documentNumber").GetString() == number);

    private static JsonElement Op(JsonElement readiness, int sequence) =>
        readiness.GetProperty("output").GetProperty("operations").EnumerateArray().Single(o => o.GetProperty("sequence").GetInt32() == sequence);

    [Fact]
    public async Task Orders_follow_the_revision_that_applies_and_documentation_blocks_only_where_enforced()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var (e, approver) = (plant.Engineer, plant.Approver);
        await plant.Person("pm", "ProgramManager");

        await plant.Ok(e, "/api/engineering/documents", new { number = "DWG-ROLL", title = "Slit roll drawing", type = "Drawing", authority = "Internal" });
        await plant.Ok(e, "/api/engineering/documents", new { number = "WI-SLIT", title = "Slitting instruction", type = "WorkInstruction", authority = "Internal" });
        await plant.Ok(e, "/api/engineering/documents", new { number = "REF-PHOTO", title = "Photo", type = "Photo", authority = "Reference" });
        await ReleaseAsync(plant, "DWG-ROLL", "A");

        // ROLL-100 rev A is released: adding a document needs a reason; references are never built to.
        var url = "/api/engineering/items/ROLL-100/revisions/A/documents";
        await Rejected(plant, e, url, new { documentNumber = "DWG-ROLL" }, "reason_required");
        await plant.Ok(e, url, new { documentNumber = "DWG-ROLL", reason = "Drawing baseline for rev A" });
        await Rejected(plant, e, url, new { documentNumber = "DWG-ROLL", reason = "again" }, "requirement_exists");
        await Rejected(plant, e, url, new { documentNumber = "REF-PHOTO", reason = "x" }, "reference_not_authority");
        await Rejected(plant, e, url, new { documentNumber = "WI-SLIT", operationSequence = 99, reason = "x" }, "unknown_operation");
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, planner, url, new { documentNumber = "WI-SLIT", reason = "x" })).Status);
        await plant.Ok(e, url, new { documentNumber = "WI-SLIT", operationSequence = 10, reason = "Threading instruction for slitting" });

        // An order released now references DWG-ROLL rev A; the work instruction has no released revision.
        var order = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "ROLL-100", quantity = 10m, dueDate = Day(10), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Due next week" });
        var docs = await Get(planner, $"/api/planning/applicability?order={order}");
        Assert.Equal("Current", Doc(docs, "DWG-ROLL").GetProperty("state").GetString());
        Assert.Equal("A", Doc(docs, "DWG-ROLL").GetProperty("atRelease").GetString());
        Assert.Equal("Missing", Doc(docs, "WI-SLIT").GetProperty("state").GetString());
        Assert.False(docs.GetProperty("enforced").GetBoolean());

        // Not enforced: listed as not evaluated, never passed.
        var readiness = await Get(planner, $"/api/orders/{order}/readiness");
        Assert.Contains("Documentation", Op(readiness, 10).GetProperty("notEvaluated").EnumerateArray().Select(x => x.GetString()));
        Assert.DoesNotContain(Op(readiness, 10).GetProperty("dimensions").EnumerateArray(), d => d.GetProperty("name").GetString() == "Documentation");

        // Enforced for Make items: the missing work instruction stops the operation.
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, e, "/api/engineering/documentation-policy/Make", new { enforced = true, reason = "x" })).Status);
        await Rejected(plant, approver, "/api/engineering/documentation-policy/Make", new { enforced = true }, "reason_required");
        await plant.Ok(approver, "/api/engineering/documentation-policy/Make", new { enforced = true, reason = "Documents loaded for made parts" });
        readiness = await Get(planner, $"/api/orders/{order}/readiness");
        var op = Op(readiness, 10);
        Assert.False(op.GetProperty("executable").GetBoolean());
        Assert.Equal("Documentation", op.GetProperty("primaryConstraint").GetString());
        Assert.Contains("WI-SLIT", op.GetProperty("dimensions").EnumerateArray().Single(d => d.GetProperty("name").GetString() == "Documentation").GetProperty("detail").GetString(), StringComparison.Ordinal);

        // Releasing the instruction clears it. Rev B of the drawing, released after the order, is a legitimate supersession.
        await ReleaseAsync(plant, "WI-SLIT", "A");
        await ReleaseAsync(plant, "DWG-ROLL", "B");
        docs = await Get(planner, $"/api/planning/applicability?order={order}");
        Assert.Equal(("LegitimatelySuperseded", "A", "B"), (Doc(docs, "DWG-ROLL").GetProperty("state").GetString(), Doc(docs, "DWG-ROLL").GetProperty("atRelease").GetString(), Doc(docs, "DWG-ROLL").GetProperty("applicable").GetString()));
        Assert.Equal("Current", Doc(docs, "WI-SLIT").GetProperty("state").GetString());
        var dimension = Op(await Get(planner, $"/api/orders/{order}/readiness"), 10).GetProperty("dimensions").EnumerateArray().Single(d => d.GetProperty("name").GetString() == "Documentation");
        Assert.Equal("Pass", dimension.GetProperty("outcome").GetString());

        // A new order released now references rev B.
        var second = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "ROLL-100", quantity = 5m, dueDate = Day(20), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{second}/release", new { reason = "Due later" });
        Assert.Equal("B", Doc(await Get(planner, $"/api/planning/applicability?order={second}"), "DWG-ROLL").GetProperty("atRelease").GetString());

        // A deviation keeping order 1 on rev A: a rule now decides, and the order already uses rev A.
        await Rejected(plant, approver, "/api/engineering/documents/DWG-ROLL/rules", new { revision = "A", condition = "Order", orderNumber = order, reason = "x" }, "reference_required");
        await plant.Ok(approver, "/api/engineering/documents/DWG-ROLL/rules", new { revision = "A", condition = "Order", orderNumber = order, reference = "DV-12", reason = "Customer accepted rev A for this lot" });
        Assert.Equal("Current", Doc(await Get(planner, $"/api/planning/applicability?order={order}"), "DWG-ROLL").GetProperty("state").GetString());

        // A deviation that moves the second order (released with B) to rev A is a mismatch until reissued.
        var deviation = Id(await plant.Ok(approver, "/api/engineering/documents/DWG-ROLL/rules", new { revision = "A", condition = "Order", orderNumber = second, reference = "DV-13", reason = "Use up rev A material" }));
        var mismatch = Doc(await Get(planner, $"/api/planning/applicability?order={second}"), "DWG-ROLL");
        Assert.Equal(("Mismatch", "B", "A"), (mismatch.GetProperty("state").GetString(), mismatch.GetProperty("atRelease").GetString(), mismatch.GetProperty("applicable").GetString()));
        Assert.Equal("Documentation", Op(await Get(planner, $"/api/orders/{second}/readiness"), 10).GetProperty("primaryConstraint").GetString());

        // Reissuing the order's documentation makes rev A what it references (needs order release authority and a reason).
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, e, $"/api/engineering/orders/{second}/reissue-documentation", new { reason = "x" })).Status);
        await Rejected(plant, planner, $"/api/engineering/orders/{second}/reissue-documentation", new { }, "reason_required");
        await plant.Ok(planner, $"/api/engineering/orders/{second}/reissue-documentation", new { reason = "Traveler reprinted with rev A under DV-13" });
        var reissued = Doc(await Get(planner, $"/api/planning/applicability?order={second}"), "DWG-ROLL");
        Assert.Equal(("Current", "A"), (reissued.GetProperty("state").GetString(), reissued.GetProperty("atRelease").GetString()));

        // Rules that disagree are never guessed: requires review (indeterminate) until one is withdrawn.
        await plant.Ok(approver, "/api/engineering/documents/DWG-ROLL/rules", new { revision = "B", condition = "Order", orderNumber = second, reference = "DV-14", reason = "Conflicting concession" });
        var review = Doc(await Get(planner, $"/api/planning/applicability?order={second}"), "DWG-ROLL");
        Assert.Equal("RequiresReview", review.GetProperty("state").GetString());
        var opReview = Op(await Get(planner, $"/api/orders/{second}/readiness"), 10).GetProperty("dimensions").EnumerateArray().Single(d => d.GetProperty("name").GetString() == "Documentation");
        Assert.Equal("Indeterminate", opReview.GetProperty("outcome").GetString());
        await Rejected(plant, approver, $"/api/engineering/applicability-rules/{deviation}/withdraw", new { }, "reason_required");
        await plant.Ok(approver, $"/api/engineering/applicability-rules/{deviation}/withdraw", new { reason = "DV-13 cancelled" });

        // Now DV-14 (rev B) decides, but the order was reissued with rev A: a mismatch again, honestly reported.
        Assert.Equal("Mismatch", Doc(await Get(planner, $"/api/planning/applicability?order={second}"), "DWG-ROLL").GetProperty("state").GetString());

        // The report lists every open order's documents; the history of rules and requirements is kept.
        var report = await Get(planner, "/api/reports/documentation-status?status=Current");
        Assert.True(report.GetProperty("rows").GetArrayLength() >= 3);
        var rules = await Get(planner, "/api/engineering/documents/DWG-ROLL/rules?history=true");
        Assert.Equal(3, rules.GetArrayLength());
        Assert.Single(rules.EnumerateArray(), r => r.GetProperty("withdrawnAt").ValueKind != JsonValueKind.Null);
        await using var db = new NpgsqlConnection(app.ConnectionString);
        await db.OpenAsync();
        await using var tamper = new NpgsqlCommand("DELETE FROM engineering.applicability_rule", db);
        await Assert.ThrowsAsync<PostgresException>(() => tamper.ExecuteNonQueryAsync());
        await using var edit = new NpgsqlCommand("UPDATE engineering.revision_document SET operation_sequence = 5", db);
        await Assert.ThrowsAsync<PostgresException>(() => edit.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Receipts_record_the_part_revision_and_inspection_follows_the_revision_built()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var e = plant.Engineer;
        await plant.Person("mike", "MaterialHandler");
        await plant.Person("ivy", "InventoryController");
        await plant.Person("qe", "QualityEngineer");
        var (mike, ivy, qe) = (plant.As("mike"), plant.As("ivy"), plant.As("qe"));
        await plant.Ok(ivy, "/api/inventory/locations", new { code = "RECV", siteCode = "PLANT1", name = "Receiving dock", kind = "Receiving" });

        // A lot-tracked bought part at rev A.
        await plant.Ok(e, "/api/engineering/items", new { partNumber = "BRKT-9", description = "Bracket", unitOfMeasure = "EA", makeBuy = "Buy", leadTimeDays = 5, lotTracked = true, @class = "BuildToPrint", reason = "Our drawing" });
        await plant.Ok(e, "/api/engineering/items/BRKT-9/revisions", new { revision = "A" });
        await plant.Ok(plant.Approver, "/api/engineering/items/BRKT-9/revisions/A/release", new { reason = "ECN-21" });

        Assert.Equal(HttpStatusCode.NotFound, (await Try(plant, mike, "/api/inventory/receipts", new { partNumber = "BRKT-9", quantity = 5m, locationCode = "RECV", lotNumber = "L1", revision = "Z", reason = "Opening" })).Status);
        await plant.Ok(mike, "/api/inventory/receipts", new { partNumber = "BRKT-9", quantity = 5m, locationCode = "RECV", lotNumber = "L1", revision = "a", reason = "Opening balance" });
        await plant.Ok(mike, "/api/inventory/receipts", new { partNumber = "BRKT-9", quantity = 3m, locationCode = "RECV", lotNumber = "L0", reason = "Found on the shelf" });
        await Rejected(plant, mike, "/api/inventory/receipts", new { partNumber = "BRKT-9", quantity = 1m, locationCode = "RECV", lotNumber = "L0", revision = "A", reason = "x" }, "lot_revision_not_recorded");

        var lots = await Get(mike, "/api/inventory/lots?part=BRKT-9");
        Assert.Equal("A", lots.EnumerateArray().Single(l => l.GetProperty("lotNumber").GetString() == "L1").GetProperty("partRevision").GetString());
        var ledger = await Get(mike, "/api/inventory/transactions?part=BRKT-9");
        Assert.Contains(ledger.EnumerateArray(), t => t.GetProperty("partRevision").GetString() == "A");

        var report = await Get(planner, "/api/reports/stock-by-revision?partNumber=BRKT-9");
        var rows = report.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("cells")).ToList();
        Assert.Equal(5m, rows.Single(r => r[1].GetString() == "A")[5].GetDecimal());
        Assert.Equal(3m, rows.Single(r => r[1].GetString() == "Revision not recorded")[5].GetDecimal());

        // Inspection plans: one for every revision, one for rev A; an order at rev A uses rev A's.
        await plant.Ok(qe, "/api/quality/plans", new { partNumber = "ROLL-100", sequence = 10, title = "General slit check", characteristics = new[] { new { name = "Edge", kind = "Attribute" } } });
        await plant.Ok(qe, "/api/quality/plans", new { partNumber = "ROLL-100", sequence = 10, revision = "A", title = "Rev A slit check", characteristics = new[] { new { name = "Edge", kind = "Attribute" }, new { name = "Burr", kind = "Attribute" } } });
        Assert.Equal(HttpStatusCode.NotFound, (await Try(plant, qe, "/api/quality/plans", new { partNumber = "ROLL-100", sequence = 10, revision = "Q", title = "x", characteristics = new[] { new { name = "Edge", kind = "Attribute" } } })).Status);
        var plans = await Get(qe, "/api/quality/plans?part=ROLL-100");
        Assert.Equal(2, plans.GetArrayLength());
        var order = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "ROLL-100", quantity = 2m, dueDate = Day(5), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Go" });
        var quality = await Get(qe, $"/api/quality/orders/{order}");
        Assert.Equal("Rev A slit check", Assert.Single(quality.GetProperty("plans").EnumerateArray()).GetProperty("title").GetString());
        await plant.Ok(qe, "/api/quality/plans/ROLL-100/10/remove?revision=A", new { reason = "Merged into the general check" });
        quality = await Get(qe, $"/api/quality/orders/{order}");
        Assert.Equal("General slit check", Assert.Single(quality.GetProperty("plans").EnumerateArray()).GetProperty("title").GetString());
    }
}
