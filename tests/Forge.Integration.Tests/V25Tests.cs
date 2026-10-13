using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Forge.Engineering.Contracts;
using Forge.Host.History;
using Forge.Host.Propagation;
using Forge.Kernel.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Forge.Integration.Tests;

/// <summary>
/// V2.5 through the real API, review scenario 1 end to end: a released change moves future orders and
/// released orders not started to the new revision, raises dispositions for work in progress and reviews
/// for open purchase lines, asks for training acknowledgments, leaves completed orders and the technical
/// packages already sent exactly as they were, and verifies the counts. A failed step leaves the change
/// "propagation incomplete" until a retry completes it.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class V25Tests(PostgresFixture postgres)
{
    private const string Sarah = "B-SARAH-0001";
    private static int key;

    private static string Day(int fromToday) => DateTime.UtcNow.AddDays(fromToday).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<string> Text(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
        return text;
    }

    private static async Task<JsonElement> Get(HttpClient client, string url) => JsonDocument.Parse(await Text(client, url)).RootElement;

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
        request.Headers.Add("Idempotency-Key", "v25-upload-" + Interlocked.Increment(ref key));
        request.Headers.Add("X-File-Name", "file.pdf");
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("entityId").GetString()!;
    }

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static async Task DraftDocumentAsync(SlittingPlant.Plant plant, string number, string revision)
    {
        var sha = await Stored(plant.Engineer, $"{number} rev {revision}");
        await plant.Ok(plant.Engineer, $"/api/engineering/documents/{number}/revisions", new { revision, fileSha256 = sha, fileName = $"{number}_{revision}.pdf" });
    }

    private static async Task DocumentAsync(SlittingPlant.Plant plant, string number, string type)
    {
        await plant.Ok(plant.Engineer, "/api/engineering/documents", new { number, title = number, type, authority = "Internal" });
        await DraftDocumentAsync(plant, number, "A");
        await plant.Ok(plant.Engineer, $"/api/engineering/documents/{number}/revisions/A/submit", new { });
        await plant.Ok(plant.Approver, $"/api/engineering/documents/{number}/revisions/A/approve", new { });
        await plant.Ok(plant.Approver, $"/api/engineering/documents/{number}/revisions/A/release", new { reason = "Baseline" });
    }

    /// <summary>Lists the lines, gets the technical review, board approval, and the document controller's release.</summary>
    private static async Task<string> ReleaseChangeAsync(SlittingPlant.Plant plant, string title, IEnumerable<(string Kind, string Subject, string To)> lines, string? effectiveFrom = null)
    {
        var e = plant.Engineer;
        var ecn = (await plant.Ok(e, "/api/engineering/changes", new { title, @class = "Minor", type = "Design", changeReason = "Scenario 1" })).GetProperty("entityId").GetString()!;
        foreach (var (kind, subject, to) in lines)
        {
            await plant.Ok(e, $"/api/engineering/changes/{ecn}/lines", new { kind, subject, toRevision = to });
        }

        await plant.Ok(e, $"/api/engineering/changes/{ecn}/submit", new { });
        await plant.Ok(plant.Approver, $"/api/engineering/changes/{ecn}/reviews", new { kind = "Technical", decision = "Approve" });
        await plant.Ok(plant.Approver, $"/api/engineering/changes/{ecn}/approve", new { });
        await plant.Ok(plant.As("dc"), $"/api/engineering/changes/{ecn}/release", new { effectiveFrom, reason = "CCB approved" });
        return ecn;
    }

    /// <summary>Background services are off in tests: relay the outbox to the event log, then feed the subscribers.</summary>
    private static async Task PropagateAsync(ForgeApp app)
    {
        var relay = app.Services.GetRequiredService<OutboxRelay>();
        while (await relay.RelayBatchAsync(100, CancellationToken.None) > 0)
        {
        }

        await app.Services.GetRequiredService<SubscriptionPump>().PumpOnceAsync(1_000_000, CancellationToken.None);
        while (await relay.RelayBatchAsync(100, CancellationToken.None) > 0)
        {
        }
    }

    private sealed record RecordedPropagationEvent(Guid Id, string Type, string Context, string EntityType,
        string EntityId, DateTimeOffset At, Guid? Cause, string? Reason);

    private static async Task VerifyRecordedPropagationCauseMapAsync(ForgeApp app, string changeNumber, long planned, long released)
    {
        // Owner-approved V3.22 acceptance uses this available real engineering propagation chain.
        // It is source-record proof, not a reconstruction of the unavailable original numeric §54.
        // Positive events come only from ReleaseChange and the existing subscriber's normal commands.
        var events = new List<RecordedPropagationEvent>();
        await using (var connection = await app.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync())
        await using (var command = new NpgsqlCommand("""
            SELECT event_id,event_type,envelope::jsonb->>'sourceContext',entity_type,entity_id,occurred_at,causation_id,envelope::jsonb->>'reason'
            FROM kernel.event_log
            WHERE (event_type='EngineeringChangeReleased' AND entity_type='EngineeringChange' AND entity_id=$1)
               OR (event_type='OrderRevisionChanged' AND entity_type='ProductionOrder' AND entity_id=ANY($2::text[]))
               OR (event_type='OrderDocumentationReissued' AND entity_type='OrderDocumentation' AND entity_id=$3)
            ORDER BY sequence
            """, connection))
        {
            command.Parameters.AddWithValue(changeNumber);
            command.Parameters.AddWithValue(new[] { planned.ToString(CultureInfo.InvariantCulture), released.ToString(CultureInfo.InvariantCulture) });
            command.Parameters.AddWithValue(released.ToString(CultureInfo.InvariantCulture));
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                events.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetFieldValue<DateTimeOffset>(5), reader.IsDBNull(6) ? null : reader.GetGuid(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        var release = Assert.Single(events, source => source.Type == "EngineeringChangeReleased");
        Assert.Equal(("Engineering", "EngineeringChange", changeNumber, "CCB approved"),
            (release.Context, release.EntityType, release.EntityId, release.Reason));
        Assert.Null(release.Cause);
        var effects = events.Where(source => source.Id != release.Id).ToArray();
        Assert.Equal(3, effects.Length);
        Assert.Equal(new[] { planned, released }.Order(), effects.Where(source => source.Type == "OrderRevisionChanged")
            .Select(source => long.Parse(source.EntityId, CultureInfo.InvariantCulture)).Order());
        var reissue = Assert.Single(effects, source => source.Type == "OrderDocumentationReissued");
        Assert.Equal(("Engineering", "OrderDocumentation", released.ToString(CultureInfo.InvariantCulture)),
            (reissue.Context, reissue.EntityType, reissue.EntityId));
        Assert.All(effects.Where(source => source.Type == "OrderRevisionChanged"), source => Assert.Equal(("Execution", "ProductionOrder"), (source.Context, source.EntityType)));
        Assert.All(effects, source => Assert.Equal(release.Id, source.Cause));
        Assert.All(effects, source => Assert.Equal("Propagation of a released engineering change", source.Reason));

        var principal = await app.Services.GetRequiredService<KernelReadStore>().ResolvePrincipalBySubjectAsync("planner", CancellationToken.None);
        Assert.NotNull(principal);
        // Seed the read window with effects only. CauseMap must recover the earlier actual release
        // by following the recorded causation IDs, rather than merely finding a shared correlation.
        var from = effects.Min(source => source.At);
        var to = effects.Max(source => source.At);
        Assert.True(release.At < from, "The command release must precede its asynchronously propagated effects.");
        var map = await app.Services.GetRequiredService<CauseMapReadStore>().LoadAsync(principal!, from, to, CancellationToken.None);
        foreach (var source in events)
        {
            var step = Assert.Single(map.Steps, step => step.EventId == source.Id);
            Assert.Equal((source.Type, source.Context, source.EntityType, source.EntityId, source.At, source.Reason, "event:" + source.Id.ToString("D")),
                (step.EventType, step.SourceContext, step.EntityType, step.EntityId, step.At, step.Reason, step.Trace));
        }
        foreach (var effect in effects)
        {
            var link = Assert.Single(map.Links, link => link.EffectEventId == effect.Id);
            Assert.Equal(release.Id, link.CauseEventId);
            Assert.DoesNotContain(effect.Id, map.EventsWithoutVisibleCause);
        }
        // Revision propagation and documentation reissue are recorded siblings of the release.
        // Their execution order must never invent an unsupported revision -> document cause link.
        Assert.DoesNotContain(map.Links, link => effects.Any(effect => effect.Id == link.CauseEventId)
            && effects.Any(effect => effect.Id == link.EffectEventId));
    }

    private static Dictionary<string, (int Expected, int Done, int Waiting, int Failed)> Counts(JsonElement view) =>
        view.GetProperty("verification").GetProperty("lines").EnumerateArray().ToDictionary(
            l => l.GetProperty("kind").GetString()!,
            l => (l.GetProperty("expected").GetInt32(), l.GetProperty("done").GetInt32(), l.GetProperty("waiting").GetInt32(), l.GetProperty("failed").GetInt32()));

    private static async Task<long> OrderAsync(SlittingPlant.Plant plant, HttpClient planner, bool release, decimal quantity = 4m)
    {
        var order = Id(await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = "ROLL-100", quantity, dueDate = Day(10), priority = 3 }));
        if (release)
        {
            await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Customer order" });
        }

        return order;
    }

    private static async Task<(SlittingPlant.Plant Plant, HttpClient Planner)> PlantAsync(ForgeApp app)
    {
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("dc", "DocumentController");
        await plant.Person("qe", "QualityEngineer");
        await plant.Person("bob", "Buyer");
        await plant.Person("pat", "PurchasingManager");
        await plant.Person("tc", "TrainingCoordinator");
        return (plant, planner);
    }

    [Fact]
    public async Task Scenario_1_propagates_a_released_change_and_verifies_it()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await PlantAsync(app);
        var e = plant.Engineer;
        var (qe, bob, pat, tc) = (plant.As("qe"), plant.As("bob"), plant.As("pat"), plant.As("tc"));

        // A shift and a kiosk so Sarah can run orders.
        await plant.Ok(e, "/api/resources/shifts", new { code = "ALL", siteCode = "PLANT1", name = "Around the clock", start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 } });
        await plant.Ok(e, "/api/resources/work-centers/SLIT-WC/shifts", new { shiftCode = "ALL" });
        await plant.Ok(e, $"/api/workforce/workers/{plant.People["sarah"]}/shift", new { shiftCode = "ALL" });
        await plant.Ok(plant.Admin, $"/api/workforce/people/{plant.People["sarah"]}/badge", new { badge = Sarah });
        await plant.Person("kiosk", "ShopFloorTerminal");
        var k = plant.As("kiosk");
        await plant.Ok(k, "/api/shopfloor/clock-in", new { badge = Sarah });

        // Baseline: ROLL-100 rev A is built to DWG-ROLL and, at op 10, to WI-SLIT; BRKT-9 rev A (bought to our print) to DWG-BRKT.
        await DocumentAsync(plant, "DWG-ROLL", "Drawing");
        await DocumentAsync(plant, "WI-SLIT", "WorkInstruction");
        await DocumentAsync(plant, "DWG-BRKT", "Drawing");
        await plant.Ok(e, "/api/engineering/items/ROLL-100/revisions/A/documents", new { documentNumber = "DWG-ROLL", reason = "Baseline" });
        await plant.Ok(e, "/api/engineering/items/ROLL-100/revisions/A/documents", new { documentNumber = "WI-SLIT", operationSequence = 10, reason = "Baseline" });
        await plant.Ok(e, "/api/engineering/items", new { partNumber = "BRKT-9", description = "Bracket", unitOfMeasure = "EA", makeBuy = "Buy", leadTimeDays = 5, @class = "BuildToPrint", reason = "Our drawing" });
        await plant.Ok(e, "/api/engineering/items/BRKT-9/revisions", new { revision = "A" });
        await plant.Ok(e, "/api/engineering/items/BRKT-9/revisions/A/documents", new { documentNumber = "DWG-BRKT" });
        await plant.Ok(plant.Approver, "/api/engineering/items/BRKT-9/revisions/A/release", new { reason = "Baseline" });

        // Orders in every state: completed, in progress, released not started, planned.
        var completed = await OrderAsync(plant, planner, true, 2m);
        await plant.Ok(k, "/api/shopfloor/start", new { badge = Sarah, orderNumber = completed, sequence = 10, machineCode = "SLITTER-A" });
        await plant.Ok(k, "/api/shopfloor/report", new { badge = Sarah, orderNumber = completed, sequence = 10, good = 2m, scrap = 0m, complete = true });
        await plant.Ok(k, "/api/shopfloor/start", new { badge = Sarah, orderNumber = completed, sequence = 20 });
        await plant.Ok(k, "/api/shopfloor/report", new { badge = Sarah, orderNumber = completed, sequence = 20, good = 2m, scrap = 0m, complete = true });
        Assert.Equal("Completed", (await Get(planner, $"/api/orders/{completed}")).GetProperty("status").GetString());
        var inProgress = await OrderAsync(plant, planner, true);
        await plant.Ok(k, "/api/shopfloor/start", new { badge = Sarah, orderNumber = inProgress, sequence = 10, machineCode = "SLITTER-A" });
        var released = await OrderAsync(plant, planner, true);
        var planned = await OrderAsync(plant, planner, false);

        // An approved purchase order for the bracket: its technical package is recorded at approval.
        await plant.Ok(bob, "/api/procurement/suppliers", new { code = "ACME", name = "Acme Brackets" });
        var po = Id(await plant.Ok(bob, "/api/procurement/purchase-orders", new { supplier = "ACME", lines = new[] { new { partNumber = "BRKT-9", quantity = 50m, promisedDate = Day(20) } } }));
        Assert.Equal(HttpStatusCode.NotFound, (await planner.GetAsync($"/api/procurement/purchase-orders/{po}/lines/1/package")).StatusCode);
        await plant.Ok(pat, $"/api/procurement/purchase-orders/{po}/approve", new { reason = "Within budget" });
        var package = await Get(bob, $"/api/procurement/purchase-orders/{po}/lines/1/package");
        Assert.Equal("A", package.GetProperty("itemRevision").GetString());
        var sent = Assert.Single(package.GetProperty("documents").EnumerateArray());
        Assert.Equal(("DWG-BRKT", "A"), (sent.GetProperty("documentNumber").GetString(), sent.GetProperty("revision").GetString()));
        Assert.Equal(64, sent.GetProperty("fileSha256").GetString()!.Length);

        // Rev B of everything: drawing, instruction, the roll (new routing text) and the bracket.
        await DraftDocumentAsync(plant, "DWG-ROLL", "B");
        await DraftDocumentAsync(plant, "WI-SLIT", "B");
        await plant.Ok(e, "/api/engineering/items/ROLL-100/revisions", new { revision = "B" });
        await plant.Ok(e, "/api/engineering/items/ROLL-100/revisions/B/bom", new { lineNumber = 1, componentPartNumber = "COIL-AL", quantityPer = 2.6m, scrapPercent = 4m });
        foreach (var (sequence, description, activity) in new[] { (10, "Slit to ±0.2", "RUN_SLITTER"), (20, "Kit", "KITTING") })
        {
            await plant.Ok(e, "/api/engineering/items/ROLL-100/revisions/B/routing", new { sequence, description, workCenterCode = "SLIT-WC", activityCode = activity, eligibleMachines = sequence == 10 ? new[] { "SLITTER-A", "SLITTER-B" } : Array.Empty<string>(), setupMinutes = 0m, runMinutesPerUnit = 1m, crewSize = 1 });
        }

        await plant.Ok(e, "/api/engineering/items/ROLL-100/revisions/B/documents", new { documentNumber = "DWG-ROLL" });
        await plant.Ok(e, "/api/engineering/items/ROLL-100/revisions/B/documents", new { documentNumber = "WI-SLIT", operationSequence = 10 });
        await plant.Ok(e, "/api/engineering/items/BRKT-9/revisions", new { revision = "B" });

        var completedBefore = await Text(planner, $"/api/orders/{completed}");
        var inProgressBefore = await Text(planner, $"/api/orders/{inProgress}");
        var packageBefore = await Text(bob, $"/api/procurement/purchase-orders/{po}/lines/1/package");

        var ecn = await ReleaseChangeAsync(plant, "Slit tolerance ±0.2", [("DocumentRevision", "DWG-ROLL", "B"), ("DocumentRevision", "WI-SLIT", "B"), ("ItemRevision", "ROLL-100", "B"), ("ItemRevision", "BRKT-9", "B")]);
        // The impact preview counts the seeded orders exactly, by state (the preview is the same before and after release).
        var impact = (await Get(e, $"/api/engineering/changes/{ecn}/impact")).GetProperty("counts");
        Assert.Equal((1, 1, 1, 1, 1), (impact.GetProperty("FutureOrder").GetInt32(), impact.GetProperty("ReleasedNotStarted").GetInt32(), impact.GetProperty("WorkInProgress").GetInt32(),
            impact.GetProperty("Completed").GetInt32(), impact.GetProperty("OpenPurchase").GetInt32()));
        var url = $"/api/engineering/changes/{ecn}/propagation";
        var before = await Get(e, url);
        Assert.Equal("Running", before.GetProperty("propagation").GetProperty("status").GetString());
        Assert.Empty(before.GetProperty("propagation").GetProperty("steps").EnumerateArray());

        await PropagateAsync(app);
        await VerifyRecordedPropagationCauseMapAsync(app, ecn, planned, released);

        var view = await Get(e, url);
        Assert.True(view.GetProperty("propagation").GetProperty("status").GetString() == "Complete", view.GetRawText());
        var counts = Counts(view);
        Assert.Equal((2, 2, 0, 0), counts["MoveOrder"]);
        Assert.Equal((1, 1, 0, 0), counts["ReissueDocumentation"]);
        Assert.Equal((1, 1, 0, 0), counts["WipDisposition"]);
        Assert.Equal((1, 1, 0, 0), counts["CompletedUnchanged"]);
        Assert.Equal((1, 1, 0, 0), counts["PurchaseReview"]);
        Assert.Equal((2, 2, 0, 0), counts["TrainingAcknowledgment"]);
        Assert.Equal(4, view.GetProperty("verification").GetProperty("actionsRaised").GetInt32());

        // Future and released-not-started orders now build rev B; the released one was reissued from rev B's routing.
        Assert.Equal("B", (await Get(planner, $"/api/orders/{planned}")).GetProperty("revision").GetString());
        var moved = await Get(planner, $"/api/orders/{released}");
        Assert.Equal("B", moved.GetProperty("revision").GetString());
        Assert.Equal("Slit to ±0.2", moved.GetProperty("operations").EnumerateArray().First().GetProperty("description").GetString());
        Assert.Equal(10.816m, moved.GetProperty("materials").EnumerateArray().Single().GetProperty("requiredQuantity").GetDecimal());

        // Completed orders, work in progress and the package already sent are exactly as they were.
        Assert.Equal(completedBefore, await Text(planner, $"/api/orders/{completed}"));
        Assert.Equal(inProgressBefore, await Text(planner, $"/api/orders/{inProgress}"));
        Assert.Equal(packageBefore, await Text(bob, $"/api/procurement/purchase-orders/{po}/lines/1/package"));

        // Every change propagation made is audited as system:propagation under the release's correlation id.
        await using (var db = new NpgsqlConnection(app.ConnectionString))
        {
            await db.OpenAsync();
            async Task<object?> Scalar(string sql)
            {
                await using var cmd = new NpgsqlCommand(sql, db);
                return await cmd.ExecuteScalarAsync();
            }

            var release = await Scalar($"SELECT correlation_id FROM kernel.audit_log WHERE action = 'ReleaseChange' AND outcome = 'Succeeded' AND entity_id = '{ecn}'");
            Assert.Equal(release, await Scalar($"SELECT correlation_id FROM kernel.audit_log WHERE action = 'ApplyChangeRevision' AND entity_id = '{released}' AND actor = 'system:propagation'"));
            Assert.Equal(0L, await Scalar($"SELECT count(*) FROM kernel.audit_log WHERE entity_type = 'ProductionOrder' AND entity_id IN ('{completed}', '{inProgress}') AND actor = 'system:propagation'"));
            Assert.Equal(3L, await Scalar($"SELECT count(*) FROM kernel.audit_log WHERE actor = 'system:propagation' AND correlation_id = '{release}' AND action IN ('ApplyChangeRevision', 'ReissueOrderDocumentation')"));
            await using var tamper = new NpgsqlCommand("UPDATE procurement.line_package_document SET revision = 'B'", db);
            await Assert.ThrowsAsync<PostgresException>(() => tamper.ExecuteNonQueryAsync());
        }

        // People decide the actions: quality the disposition (with a basis), the buyer the purchase line, the training coordinator the acknowledgments.
        var actions = view.GetProperty("actions").EnumerateArray().ToList();
        long Action(string kind) => actions.First(a => a.GetProperty("kind").GetString() == kind).GetProperty("actionId").GetInt64();
        var wip = Action("WipDisposition");
        Assert.Equal(inProgress.ToString(CultureInfo.InvariantCulture), actions.Single(a => a.GetProperty("actionId").GetInt64() == wip).GetProperty("target").GetString());
        Assert.Contains("DWG-BRKT rev A", actions.Single(a => a.GetProperty("kind").GetString() == "PurchaseReview").GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, bob, $"/api/engineering/change-actions/{wip}/resolve", new { resolution = "Scrap", notes = "x" })).Status);
        await Rejected(plant, qe, $"/api/engineering/change-actions/{wip}/resolve", new { resolution = "CompleteAtOldRevision" }, "notes_required");
        await Rejected(plant, qe, $"/api/engineering/change-actions/{wip}/resolve", new { resolution = "Ship it", notes = "x" }, "invalid_resolution");

        // A stopped WIP operator cannot silently restart a superseded engineering snapshot while
        // its authoritative Quality disposition is still open. Stopping also avoids the legitimate
        // already-on-this-operation no-change response, which would not prove a fresh start gate.
        await plant.Ok(k, "/api/shopfloor/stop", new { badge = Sarah });
        var stoppedLabor = (await Get(planner, $"/api/orders/{inProgress}/shopfloor")).GetProperty("labor");
        var originalLabor = Assert.Single(stoppedLabor.EnumerateArray());
        Assert.Equal(JsonValueKind.String, originalLabor.GetProperty("endedAt").ValueKind);
        var restart = new { badge = Sarah, orderNumber = inProgress, sequence = 10, machineCode = "SLITTER-A" };
        var deniedRestart = await Try(plant, k, "/api/shopfloor/start", restart);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, deniedRestart.Status);
        Assert.Contains("operation_not_ready", deniedRestart.Body, StringComparison.Ordinal);
        Assert.Contains("Engineering", deniedRestart.Body, StringComparison.Ordinal);
        Assert.Equal(stoppedLabor.GetRawText(), (await Get(planner, $"/api/orders/{inProgress}/shopfloor")).GetProperty("labor").GetRawText());
        var beforeDisposition = (await Get(planner, $"/api/orders/{inProgress}/readiness")).GetProperty("output").GetProperty("operations");
        var beforeEngineering = Assert.Single(Assert.Single(beforeDisposition.EnumerateArray(), operation => operation.GetProperty("sequence").GetInt32() == 10)
            .GetProperty("dimensions").EnumerateArray(), dimension => dimension.GetProperty("name").GetString() == "Engineering");
        Assert.Equal("Fail", beforeEngineering.GetProperty("outcome").GetString());

        // A normal resolved action for another source record/part is not continuation authority
        // for this WIP snapshot, even though both actions belong to the same released change.
        var review = Action("PurchaseReview");
        await Rejected(plant, bob, $"/api/engineering/change-actions/{review}/resolve", new { resolution = "SupplierNotified" }, "notes_required");
        await plant.Ok(bob, $"/api/engineering/change-actions/{review}/resolve", new { resolution = "SupplierNotified", notes = "Rev B drawing sent to Acme (J. Smith)" });
        var unrelatedResolution = await Try(plant, k, "/api/shopfloor/start", restart);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unrelatedResolution.Status);
        Assert.Contains("Engineering", unrelatedResolution.Body, StringComparison.Ordinal);
        Assert.Equal(stoppedLabor.GetRawText(), (await Get(planner, $"/api/orders/{inProgress}/shopfloor")).GetProperty("labor").GetRawText());

        await plant.Ok(qe, $"/api/engineering/change-actions/{wip}/resolve", new { resolution = "CompleteAtOldRevision", notes = "Deviation DV-21: customer accepts rev A for this lot" });
        var resolvedWip = Assert.Single((await Get(e, url)).GetProperty("actions").EnumerateArray(), action => action.GetProperty("actionId").GetInt64() == wip);
        Assert.Equal((ecn, inProgress.ToString(CultureInfo.InvariantCulture), "ROLL-100", "Resolved", "CompleteAtOldRevision", $"person:{plant.People["qe"]}"),
            (resolvedWip.GetProperty("changeNumber").GetString(), resolvedWip.GetProperty("target").GetString(), resolvedWip.GetProperty("partNumber").GetString(), resolvedWip.GetProperty("status").GetString(), resolvedWip.GetProperty("resolution").GetString(), resolvedWip.GetProperty("resolvedBy").GetString()));
        Assert.Equal("Deviation DV-21: customer accepts rev A for this lot", resolvedWip.GetProperty("notes").GetString());
        Assert.Equal(JsonValueKind.String, resolvedWip.GetProperty("resolvedAt").ValueKind);

        // The normal resolved WipDisposition contract authorizes this exact old-revision WIP,
        // with its concession basis. Shared preview and transactional start must agree, while
        // frozen engineering and the ended labor history retain their original revision/facts.
        var afterDisposition = (await Get(planner, $"/api/orders/{inProgress}/readiness")).GetProperty("output").GetProperty("operations");
        var afterEngineering = Assert.Single(Assert.Single(afterDisposition.EnumerateArray(), operation => operation.GetProperty("sequence").GetInt32() == 10)
            .GetProperty("dimensions").EnumerateArray(), dimension => dimension.GetProperty("name").GetString() == "Engineering");
        Assert.Equal("Pass", afterEngineering.GetProperty("outcome").GetString());
        var restarted = await plant.Ok(k, "/api/shopfloor/start", restart);
        Assert.Equal("ok", restarted.GetProperty("code").GetString());
        var restartedLabor = (await Get(planner, $"/api/orders/{inProgress}/shopfloor")).GetProperty("labor").EnumerateArray().ToArray();
        Assert.Equal(2, restartedLabor.Length);
        var preservedLabor = Assert.Single(restartedLabor, labor => labor.GetProperty("laborId").GetInt64() == originalLabor.GetProperty("laborId").GetInt64());
        Assert.Equal(originalLabor.GetRawText(), preservedLabor.GetRawText());
        var currentLabor = Assert.Single(restartedLabor, labor => labor.GetProperty("endedAt").ValueKind == JsonValueKind.Null);
        Assert.Equal((plant.People["sarah"], "SLITTER-A"), (currentLabor.GetProperty("personId").GetGuid(), currentLabor.GetProperty("machineCode").GetString()));
        var continued = await Get(planner, $"/api/orders/{inProgress}");
        Assert.Equal("A", continued.GetProperty("revision").GetString());
        Assert.Equal(10.4m, Assert.Single(continued.GetProperty("materials").EnumerateArray()).GetProperty("requiredQuantity").GetDecimal());
        await plant.Ok(k, "/api/shopfloor/stop", new { badge = Sarah });
        await Rejected(plant, qe, $"/api/engineering/change-actions/{wip}/resolve", new { resolution = "Scrap", notes = "x" }, "action_resolved");
        foreach (var ack in actions.Where(a => a.GetProperty("kind").GetString() == "TrainingAcknowledgment"))
        {
            await plant.Ok(tc, $"/api/engineering/change-actions/{ack.GetProperty("actionId").GetInt64()}/resolve", new { resolution = "Acknowledged" });
        }

        Assert.Empty((await Get(e, "/api/engineering/change-actions")).EnumerateArray());
        Assert.Equal(4, (await Get(e, "/api/engineering/change-actions?open=false")).GetArrayLength());

        // Delivered again (at least once): nothing is planned or raised twice.
        await plant.Ok(plant.As("dc"), $"/api/engineering/changes/{ecn}/propagation/retry", new { });
        Assert.Equal(counts, Counts(await Get(e, url)));
        Assert.Equal(4, (await Get(e, url)).GetProperty("actions").GetArrayLength());

        await using var check = new NpgsqlConnection(app.ConnectionString);
        await check.OpenAsync();
        await using var edit = new NpgsqlCommand($"UPDATE engineering.change_action SET notes = 'x' WHERE action_id = {wip}", check);
        await Assert.ThrowsAsync<PostgresException>(() => edit.ExecuteNonQueryAsync());
        await using var forget = new NpgsqlCommand("DELETE FROM engineering.propagation_step", check);
        await Assert.ThrowsAsync<PostgresException>(() => forget.ExecuteNonQueryAsync());
    }

    private sealed class Outage : IPropagationFaults
    {
        public bool On { get; set; } = true;

        public void BeforeStep(string changeNumber, PropagationStepInfo step)
        {
            if (On && step.Kind == PropagationStepKind.MoveOrder)
            {
                throw new InvalidOperationException("Simulated outage while moving the order");
            }
        }
    }

    [Fact]
    public async Task A_failed_step_leaves_propagation_incomplete_until_a_retry_completes_it()
    {
        var outage = new Outage();
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), s => s.AddSingleton<IPropagationFaults>(outage));
        var (plant, planner) = await PlantAsync(app);
        var e = plant.Engineer;

        var planned = await OrderAsync(plant, planner, false);
        await plant.Ok(e, "/api/engineering/items/ROLL-100/revisions", new { revision = "B" });
        await plant.Ok(e, "/api/engineering/items/ROLL-100/revisions/B/bom", new { lineNumber = 1, componentPartNumber = "COIL-AL", quantityPer = 2.5m, scrapPercent = 4m });
        await plant.Ok(e, "/api/engineering/items/ROLL-100/revisions/B/routing", new { sequence = 10, description = "Slit", workCenterCode = "SLIT-WC", activityCode = "RUN_SLITTER", eligibleMachines = new[] { "SLITTER-A" }, setupMinutes = 0m, runMinutesPerUnit = 1m, crewSize = 1 });
        var ecn = await ReleaseChangeAsync(plant, "Simpler routing", [("ItemRevision", "ROLL-100", "B")]);

        await PropagateAsync(app);
        var url = $"/api/engineering/changes/{ecn}/propagation";
        var view = await Get(e, url);
        Assert.Equal("Incomplete", view.GetProperty("propagation").GetProperty("status").GetString());
        Assert.Equal((1, 0, 0, 1), Counts(view)["MoveOrder"]);
        Assert.Contains("Simulated outage", view.GetProperty("propagation").GetProperty("lastError").GetString(), StringComparison.Ordinal);
        Assert.Equal("A", (await Get(planner, $"/api/orders/{planned}")).GetProperty("revision").GetString());
        Assert.Contains((await Get(e, "/api/engineering/change-propagations")).EnumerateArray(), p => p.GetProperty("changeNumber").GetString() == ecn);

        // Only someone with release authority runs it again; the subscriber has moved on, the retry finishes it.
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, e, $"{url}/retry", new { })).Status);
        await plant.Ok(plant.As("dc"), $"{url}/retry", new { });
        Assert.Equal("Incomplete", (await Get(e, url)).GetProperty("propagation").GetProperty("status").GetString());
        outage.On = false;
        await plant.Ok(plant.As("dc"), $"{url}/retry", new { });
        view = await Get(e, url);
        Assert.Equal("Complete", view.GetProperty("propagation").GetProperty("status").GetString());
        var step = Assert.Single(view.GetProperty("propagation").GetProperty("steps").EnumerateArray());
        Assert.Equal(3, step.GetProperty("attempts").GetInt32());
        Assert.Equal("B", (await Get(planner, $"/api/orders/{planned}")).GetProperty("revision").GetString());
        Assert.DoesNotContain((await Get(e, "/api/engineering/change-propagations")).EnumerateArray(), p => p.GetProperty("changeNumber").GetString() == ecn);

        // A change that takes effect later waits: the released order's documentation is reissued only once it is in force.
        await DocumentAsync(plant, "WI-NEW", "Procedure");
        await plant.Ok(e, "/api/engineering/items/ROLL-100/revisions/B/documents", new { documentNumber = "WI-NEW", reason = "New procedure" });
        var released = await OrderAsync(plant, planner, true);
        await DraftDocumentAsync(plant, "WI-NEW", "B");
        var later = await ReleaseChangeAsync(plant, "Procedure update", [("DocumentRevision", "WI-NEW", "B")], Day(3));
        await PropagateAsync(app);
        view = await Get(e, $"/api/engineering/changes/{later}/propagation");
        Assert.Equal("Incomplete", view.GetProperty("propagation").GetProperty("status").GetString());
        Assert.Equal((1, 0, 1, 0), Counts(view)["ReissueDocumentation"]);
        Assert.Contains("waiting for the change to take effect", view.GetProperty("propagation").GetProperty("lastError").GetString(), StringComparison.Ordinal);
        Assert.Equal(released.ToString(CultureInfo.InvariantCulture), view.GetProperty("propagation").GetProperty("steps").EnumerateArray().Single(x => x.GetProperty("kind").GetString() == "ReissueDocumentation").GetProperty("target").GetString());
    }
}
