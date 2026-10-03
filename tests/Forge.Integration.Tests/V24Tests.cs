using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace Forge.Integration.Tests;

/// <summary>
/// V2.4 through the real API: an engineering change lists the revisions it moves, gets the reviews its
/// policy requires from people other than its originator, is approved by the change board and released —
/// by someone other than the approver unless a policy combines them — putting every listed revision in
/// force in one transaction. Revisions on an open change cannot be released on their own.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class V24Tests(PostgresFixture postgres)
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

    private static async Task Forbidden(SlittingPlant.Plant plant, HttpClient client, string url, object body) =>
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, client, url, body)).Status);

    private static async Task<string> Stored(HttpClient client, string text)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/engineering/document-files") { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(text)) };
        request.Headers.Add("Idempotency-Key", "v24-upload-" + Interlocked.Increment(ref key));
        request.Headers.Add("X-File-Name", "file.pdf");
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("entityId").GetString()!;
    }

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static string Number(JsonElement result) => result.GetProperty("entityId").GetString()!;

    private static async Task DraftDocumentRevisionAsync(SlittingPlant.Plant plant, string number, string revision)
    {
        var sha = await Stored(plant.Engineer, $"{number} rev {revision}");
        await plant.Ok(plant.Engineer, $"/api/engineering/documents/{number}/revisions", new { revision, fileSha256 = sha, fileName = $"{number}_{revision}.pdf" });
    }

    /// <summary>Releases rev A of an internal document the standalone way (no change).</summary>
    private static async Task ReleaseStandaloneAsync(SlittingPlant.Plant plant, string number, string revision)
    {
        await DraftDocumentRevisionAsync(plant, number, revision);
        await plant.Ok(plant.Engineer, $"/api/engineering/documents/{number}/revisions/{revision}/submit", new { });
        await plant.Ok(plant.Approver, $"/api/engineering/documents/{number}/revisions/{revision}/approve", new { });
        await plant.Ok(plant.Approver, $"/api/engineering/documents/{number}/revisions/{revision}/release", new { reason = "Baseline" });
    }

    private static JsonElement Revision(JsonElement document, string revision) =>
        document.GetProperty("document").GetProperty("revisions").EnumerateArray().Single(r => r.GetProperty("revision").GetString() == revision);

    private static async Task<JsonElement> Change(HttpClient client, string number) => (await Get(client, $"/api/engineering/changes/{number}")).GetProperty("change");

    [Fact]
    public async Task A_change_is_reviewed_by_policy_approved_by_the_board_and_released_by_someone_else()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var (e, approver) = (plant.Engineer, plant.Approver);
        await plant.Person("qe", "QualityEngineer");
        await plant.Person("dc", "DocumentController");
        var (qe, dc) = (plant.As("qe"), plant.As("dc"));

        // Baseline: DWG-ROLL rev A in force, ROLL-100 rev A built to it; a bought bracket at rev A.
        await plant.Ok(e, "/api/engineering/documents", new { number = "DWG-ROLL", title = "Slit roll drawing", type = "Drawing", authority = "Internal" });
        await plant.Ok(e, "/api/engineering/documents", new { number = "REF-PHOTO", title = "Photo", type = "Photo", authority = "Reference" });
        await ReleaseStandaloneAsync(plant, "DWG-ROLL", "A");
        await plant.Ok(e, "/api/engineering/items/ROLL-100/revisions/A/documents", new { documentNumber = "DWG-ROLL", reason = "Drawing baseline" });
        await plant.Ok(e, "/api/engineering/items", new { partNumber = "BRKT-9", description = "Bracket", unitOfMeasure = "EA", makeBuy = "Buy", leadTimeDays = 5, lotTracked = true, @class = "BuildToPrint", reason = "Our drawing" });
        await plant.Ok(e, "/api/engineering/items/BRKT-9/revisions", new { revision = "A" });
        await plant.Ok(approver, "/api/engineering/items/BRKT-9/revisions/A/release", new { reason = "Baseline" });

        // An order for ROLL-100 released, not started: the impact preview counts it.
        var order = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "ROLL-100", quantity = 10m, dueDate = Day(10), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Due next week" });

        // Review policy: major changes need technical and quality reviews. Only the change board sets policy.
        var policyBody = new { name = "Major changes", changeClass = "Major", requiredReviews = new[] { "Technical", "Quality" }, combineApprovalRelease = false, reason = "CCB charter" };
        await Forbidden(plant, e, "/api/engineering/review-policies", policyBody);
        await Rejected(plant, approver, "/api/engineering/review-policies", new { name = "Major changes", changeClass = "Major", requiredReviews = new[] { "Technical" } }, "reason_required");
        await Rejected(plant, approver, "/api/engineering/review-policies", new { name = "Empty", requiredReviews = Array.Empty<string>(), reason = "x" }, "reviews_required");
        await plant.Ok(approver, "/api/engineering/review-policies", policyBody);

        // The engineer drafts the change and the new revisions.
        await Forbidden(plant, planner, "/api/engineering/changes", new { title = "x", @class = "Major", type = "Design", changeReason = "x" });
        var ecn = Number(await plant.Ok(e, "/api/engineering/changes", new { title = "Wider slit tolerance", @class = "Major", type = "Design", priority = "Routine", changeReason = "Customer request CR-77" }));
        Assert.Matches("^ECN-[0-9]{4,}$", ecn);
        await DraftDocumentRevisionAsync(plant, "DWG-ROLL", "B");
        await plant.Ok(e, "/api/engineering/items/BRKT-9/revisions", new { revision = "B" });

        var lines = $"/api/engineering/changes/{ecn}/lines";
        Assert.Equal(HttpStatusCode.NotFound, (await Try(plant, e, lines, new { kind = "DocumentRevision", subject = "DWG-ROLL", toRevision = "Z" })).Status);
        await Rejected(plant, e, lines, new { kind = "DocumentRevision", subject = "DWG-ROLL", toRevision = "A" }, "revision_released");
        await Rejected(plant, e, lines, new { kind = "DocumentRevision", subject = "REF-PHOTO", toRevision = "A" }, "reference_not_controlled");
        await plant.Ok(e, lines, new { kind = "DocumentRevision", subject = "dwg-roll", toRevision = "b", note = "Tolerance ±0.2" });
        await Rejected(plant, e, lines, new { kind = "DocumentRevision", subject = "DWG-ROLL", toRevision = "B" }, "line_exists");
        await plant.Ok(e, lines, new { kind = "ItemRevision", subject = "BRKT-9", toRevision = "B" });

        var change = await Change(e, ecn);
        var docLine = change.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("kind").GetString() == "DocumentRevision");
        Assert.Equal(("DWG-ROLL", "A", "B"), (docLine.GetProperty("documentNumber").GetString(), docLine.GetProperty("fromRevision").GetString(), docLine.GetProperty("toRevision").GetString()));

        // Another change cannot list the same revision; a change with no lines cannot be submitted.
        var other = Number(await plant.Ok(e, "/api/engineering/changes", new { title = "Duplicate", @class = "Minor", type = "Documentation", changeReason = "x" }));
        await Rejected(plant, e, $"/api/engineering/changes/{other}/lines", new { kind = "DocumentRevision", subject = "DWG-ROLL", toRevision = "B" }, "on_other_change");
        await Rejected(plant, e, $"/api/engineering/changes/{other}/submit", new { }, "no_lines");
        await Rejected(plant, e, $"/api/engineering/changes/{other}/cancel", new { }, "reason_required");
        await plant.Ok(e, $"/api/engineering/changes/{other}/cancel", new { reason = "Raised in error" });

        // Listed revisions are released only by the change.
        await Rejected(plant, approver, "/api/engineering/items/BRKT-9/revisions/B/release", new { reason = "x" }, "under_change");
        await Rejected(plant, approver, "/api/engineering/documents/DWG-ROLL/revisions/B/approve", new { }, "under_change");

        // Impact before release: the released order, both parts, the document.
        var impact = await Get(e, $"/api/engineering/changes/{ecn}/impact");
        var parts = impact.GetProperty("parts").EnumerateArray().Select(p => p.GetProperty("partNumber").GetString()).ToList();
        Assert.Contains("ROLL-100", parts);
        Assert.Contains("BRKT-9", parts);
        Assert.True(impact.GetProperty("counts").GetProperty("ReleasedNotStarted").GetInt32() >= 1);
        Assert.Contains(impact.GetProperty("records").EnumerateArray(), r => r.GetProperty("category").GetString() == "Document" && r.GetProperty("reference").GetString() == "DWG-ROLL");

        // Submit: the policy requires technical and quality reviews.
        await plant.Ok(e, $"/api/engineering/changes/{ecn}/submit", new { });
        change = await Change(e, ecn);
        Assert.Equal(("InReview", 1), (change.GetProperty("status").GetString(), change.GetProperty("reviewRound").GetInt32()));
        Assert.Equal(["Quality", "Technical"], change.GetProperty("reviews").EnumerateArray().Select(r => r.GetProperty("kind").GetString()!).Order(StringComparer.Ordinal).ToArray());
        await Rejected(plant, e, lines, new { kind = "ItemRevision", subject = "ROLL-100", toRevision = "A" }, "invalid_status");

        // Reviews: never the originator, only the right roles; a rejection needs notes and returns the draft.
        var reviews = $"/api/engineering/changes/{ecn}/reviews";
        await Rejected(plant, e, reviews, new { kind = "Technical", decision = "Approve" }, "separation_of_duties");
        await Rejected(plant, planner, reviews, new { kind = "Quality", decision = "Approve" }, "not_a_reviewer");
        await Rejected(plant, approver, reviews, new { kind = "Program", decision = "Approve" }, "review_not_required");
        await Rejected(plant, approver, $"/api/engineering/changes/{ecn}/approve", new { }, "reviews_open");
        await Rejected(plant, qe, reviews, new { kind = "Quality", decision = "Reject" }, "notes_required");
        await plant.Ok(qe, reviews, new { kind = "Quality", decision = "Reject", notes = "Add the inspection impact to the description" });
        change = await Change(e, ecn);
        Assert.Equal("Draft", change.GetProperty("status").GetString());

        // Round 2 after the originator revises it.
        await plant.Ok(e, $"/api/engineering/changes/{ecn}", new { title = "Wider slit tolerance", @class = "Major", type = "Design", priority = "Routine", changeReason = "Customer request CR-77", description = "Inspection plan unchanged: edge check covers ±0.2." });
        await plant.Ok(e, $"/api/engineering/changes/{ecn}/submit", new { });
        await plant.Ok(approver, reviews, new { kind = "Technical", decision = "Approve" });
        await Rejected(plant, qe, reviews, new { kind = "Technical", decision = "Approve" }, "review_decided");
        await plant.Ok(qe, reviews, new { kind = "Quality", decision = "ApproveWithComments", notes = "Watch the first lot" });
        change = await Change(e, ecn);
        Assert.Equal(2, change.GetProperty("reviewRound").GetInt32());
        Assert.Equal(4, change.GetProperty("reviews").GetArrayLength());

        // Board approval: not by someone without the authority, not by the originator.
        await Forbidden(plant, qe, $"/api/engineering/changes/{ecn}/approve", new { });
        await Forbidden(plant, e, $"/api/engineering/changes/{ecn}/release", new { reason = "x" });
        await plant.Ok(approver, $"/api/engineering/changes/{ecn}/approve", new { });

        // Release: the approver may not (no policy combines them); the document controller does, with a reason.
        await Rejected(plant, approver, $"/api/engineering/changes/{ecn}/release", new { reason = "x" }, "separation_of_duties");
        await Rejected(plant, dc, $"/api/engineering/changes/{ecn}/release", new { }, "reason_required");
        await Rejected(plant, dc, $"/api/engineering/changes/{ecn}/release", new { effectiveFrom = Day(-1), reason = "x" }, "effective_in_past");
        await plant.Ok(dc, $"/api/engineering/changes/{ecn}/release", new { reason = "CCB approved" });

        change = await Change(e, ecn);
        Assert.Equal("Released", change.GetProperty("status").GetString());
        var drawing = await Get(e, "/api/engineering/documents/DWG-ROLL");
        Assert.Equal("Superseded", Revision(drawing, "A").GetProperty("status").GetString());
        Assert.Equal("Released", Revision(drawing, "B").GetProperty("status").GetString());
        Assert.Equal(plant.People["approver"].ToString(), Revision(drawing, "B").GetProperty("approvedBy").GetString()!.Replace("person:", string.Empty, StringComparison.Ordinal));
        var bracket = await Get(e, "/api/engineering/items/BRKT-9/revisions");
        Assert.Equal("Released", bracket.EnumerateArray().Single(r => r.GetProperty("revision").GetString() == "B").GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, bracket.EnumerateArray().Single(r => r.GetProperty("revision").GetString() == "A").GetProperty("effectiveTo").ValueKind);
        await Rejected(plant, dc, $"/api/engineering/changes/{ecn}/cancel", new { reason = "x" }, "invalid_status");

        // Reported, and the record is frozen in the database.
        var report = await Get(planner, "/api/reports/engineering-changes");
        Assert.True(report.GetProperty("rows").GetArrayLength() >= 1);
        await using var db = new NpgsqlConnection(app.ConnectionString);
        await db.OpenAsync();
        foreach (var sql in new[]
        {
            "DELETE FROM engineering.change_line",
            "DELETE FROM engineering.review_policy",
            $"UPDATE engineering.change SET title = 'x' WHERE change_number = '{ecn}'",
            "UPDATE engineering.change_review SET notes = 'x' WHERE decision IS NOT NULL",
        })
        {
            await using var tamper = new NpgsqlCommand(sql, db);
            await Assert.ThrowsAsync<PostgresException>(() => tamper.ExecuteNonQueryAsync());
        }
    }

    [Fact]
    public async Task A_policy_can_let_the_approver_release_and_rejected_changes_keep_their_drafts()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        var (e, approver) = (plant.Engineer, plant.Approver);
        await plant.Ok(e, "/api/engineering/documents", new { number = "WI-SLIT", title = "Slitting instruction", type = "WorkInstruction", authority = "Internal" });
        await ReleaseStandaloneAsync(plant, "WI-SLIT", "A");

        // With no policy a technical review is required; this policy combines approval and release for documentation changes.
        var policy = Id(await plant.Ok(approver, "/api/engineering/review-policies", new { name = "Documentation fixes", changeType = "Documentation", requiredReviews = new[] { "Technical" }, combineApprovalRelease = true, reason = "Small-shop exception" }));
        await DraftDocumentRevisionAsync(plant, "WI-SLIT", "B");
        var ecn = Number(await plant.Ok(e, "/api/engineering/changes", new { title = "Fix typo in step 4", @class = "Administrative", type = "Documentation", changeReason = "Typo" }));
        await plant.Ok(e, $"/api/engineering/changes/{ecn}/lines", new { kind = "DocumentRevision", subject = "WI-SLIT", toRevision = "B" });
        await plant.Ok(e, $"/api/engineering/changes/{ecn}/submit", new { });
        await plant.Ok(approver, $"/api/engineering/changes/{ecn}/reviews", new { kind = "Technical", decision = "Approve" });
        await plant.Ok(approver, $"/api/engineering/changes/{ecn}/approve", new { });
        await plant.Ok(approver, $"/api/engineering/changes/{ecn}/release", new { effectiveFrom = Day(3), reason = "Combined per policy" });
        var document = await Get(e, "/api/engineering/documents/WI-SLIT");
        Assert.Equal(Day(3), Revision(document, "B").GetProperty("effectiveFrom").GetString());

        // A board rejection closes the change; its draft stays a draft and can go on another change.
        await DraftDocumentRevisionAsync(plant, "WI-SLIT", "C");
        var rejected = Number(await plant.Ok(e, "/api/engineering/changes", new { title = "Reorder steps", @class = "Minor", type = "Process", changeReason = "Operator suggestion" }));
        await plant.Ok(e, $"/api/engineering/changes/{rejected}/lines", new { kind = "DocumentRevision", subject = "WI-SLIT", toRevision = "C" });
        await plant.Ok(e, $"/api/engineering/changes/{rejected}/submit", new { });
        Assert.Equal("Technical", Assert.Single((await Change(e, rejected)).GetProperty("reviews").EnumerateArray()).GetProperty("kind").GetString());
        await Rejected(plant, approver, $"/api/engineering/changes/{rejected}/reject", new { }, "reason_required");
        await plant.Ok(approver, $"/api/engineering/changes/{rejected}/reject", new { reason = "Order matters for safety" });
        Assert.Equal("Rejected", (await Change(e, rejected)).GetProperty("status").GetString());
        Assert.Equal("Draft", Revision(await Get(e, "/api/engineering/documents/WI-SLIT"), "C").GetProperty("status").GetString());
        var again = Number(await plant.Ok(e, "/api/engineering/changes", new { title = "Reorder steps v2", @class = "Minor", type = "Process", changeReason = "Operator suggestion" }));
        await plant.Ok(e, $"/api/engineering/changes/{again}/lines", new { kind = "DocumentRevision", subject = "WI-SLIT", toRevision = "C" });

        // Withdrawn policies stop applying but stay in the history.
        await Rejected(plant, approver, $"/api/engineering/review-policies/{policy}/withdraw", new { }, "reason_required");
        await plant.Ok(approver, $"/api/engineering/review-policies/{policy}/withdraw", new { reason = "Exception ended" });
        Assert.Equal(0, (await Get(e, "/api/engineering/review-policies")).GetArrayLength());
        Assert.Equal(1, (await Get(e, "/api/engineering/review-policies?history=true")).GetArrayLength());
    }
}
