using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Forge.Integration.Tests;

/// <summary>
/// V3.9 through the real API: every open operation answers "can this happen now, and why not?" with tooling, holds,
/// material at the point of use and training on controlled documents as dimensions; holds stop the shop floor and a
/// different person releases them; presence levels never count a schedule as presence.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class V39Tests(PostgresFixture postgres)
{
    private const string Sarah = "B-SARAH-0309";
    private static int key;

    private static string Day(int fromToday) => DateTime.UtcNow.AddDays(fromToday).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    private static async Task Rejected(SlittingPlant.Plant plant, HttpClient client, string url, object body, string code, HttpStatusCode status = HttpStatusCode.UnprocessableEntity)
    {
        var response = await plant.Try(client, url, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"{url} -> {(int)response.StatusCode}: {text}");
        Assert.Contains(code, text, StringComparison.Ordinal);
    }

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    /// <summary>The answer for operation 10 of an order, from the plant-wide list.</summary>
    private static async Task<JsonElement> Answer(HttpClient client, long order) =>
        (await Get(client, "/api/readiness/operations")).EnumerateArray().Single(o => o.GetProperty("orderNumber").GetInt64() == order && o.GetProperty("sequence").GetInt32() == 10);

    private static string? Constraint(JsonElement answer) => answer.GetProperty("executable").GetBoolean() ? null : answer.GetProperty("primaryConstraint").GetString();

    private static async Task<long> ReleasedOrderAsync(SlittingPlant.Plant plant, HttpClient planner)
    {
        var order = Id(await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = "ROLL-100", quantity = 10m, dueDate = Day(10), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Customer order" });
        return order;
    }

    [Fact]
    public async Task Machine_material_and_operator_ready_but_an_expired_gauge_is_not_executable_and_holds_need_a_second_person()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("qe", "QualityEngineer");
        var (e, qe) = (plant.Engineer, plant.As("qe"));
        var order = await ReleasedOrderAsync(plant, planner);
        Assert.Null(Constraint(await Answer(planner, order)));

        // An inspection gauge is required to run the slitter; the only caliper's calibration ran out 20 days ago.
        await plant.Ok(qe, "/api/resources/tool-types", new { code = "CALIPER", name = "Caliper", kind = "Gauge", calibrationDays = 180 });
        await plant.Ok(qe, "/api/resources/tools", new { code = "CAL-1", typeCode = "CALIPER", description = "Digital caliper", calibratedOn = Day(-200), certificate = "LAB-1" });
        await Rejected(plant, e, "/api/resources/tool-requirements", new { activityCode = "RUN_SLITTER", toolTypeCode = "NOPE" }, "unknown_tool_type");
        await plant.Ok(e, "/api/resources/tool-requirements", new { activityCode = "RUN_SLITTER", toolTypeCode = "CALIPER" });
        var blocked = await Answer(planner, order);
        Assert.Equal("Tooling", Constraint(blocked));
        Assert.Contains("was due for calibration", blocked.GetProperty("why").GetString(), StringComparison.Ordinal);
        var dimensions = blocked.GetProperty("dimensions").EnumerateArray().ToDictionary(d => d.GetProperty("name").GetString()!, d => d.GetProperty("outcome").GetString());
        Assert.Equal(("Pass", "Pass", "Pass", "Fail"), (dimensions["Equipment"], dimensions["Material"], dimensions["Workforce"], dimensions["Tooling"]));
        var readiness = await Get(planner, $"/api/orders/{order}/readiness");
        Assert.DoesNotContain("Tooling", readiness.GetProperty("output").GetProperty("operations")[0].GetProperty("notEvaluated").EnumerateArray().Select(x => x.GetString()));

        // The report and the Analyst give the same answer.
        var report = await Get(planner, $"/api/reports/executable-operations?search={order}");
        var row = report.GetProperty("rows").EnumerateArray().First().GetProperty("cells");
        Assert.Equal(("No", "Tooling"), (row[6].GetString(), row[7].GetString()));
        var answer = await (await planner.PostAsJsonAsync(new Uri("/api/analyst/ask", UriKind.Relative), new { question = "Which operations are blocked by tooling?" })).Content.ReadFromJsonAsync<JsonElement>();
        var request = answer.GetProperty("interpretation").GetProperty("report");
        Assert.Equal(("executable-operations", "Tooling"), (request.GetProperty("reportId").GetString(), request.GetProperty("parameters").GetProperty("status").GetString()));

        await plant.Ok(qe, "/api/resources/tools/CAL-1/calibrations", new { calibratedOn = Day(0), passed = true, certificate = "LAB-2" });
        Assert.Null(Constraint(await Answer(planner, order)));

        // A machine hold moves the work to the other slitter; a work-center hold stops it until someone else releases it.
        await Rejected(plant, e, "/api/holds", new { kind = "Production", targetType = "Machine", targetId = "SLITTER-A" }, "reason_required");
        var machineHold = Id(await plant.Ok(e, "/api/holds", new { kind = "Production", targetType = "Machine", targetId = "SLITTER-A", reason = "Guard damaged" }));
        var moved = await Answer(planner, order);
        Assert.Equal((true, "SLITTER-B"), (moved.GetProperty("executable").GetBoolean(), moved.GetProperty("machine").GetString()));
        var wcHold = Id(await plant.Ok(qe, "/api/holds", new { kind = "Production", targetType = "WorkCenter", targetId = "SLIT-WC", reason = "Area cleanup after spill" }));
        var held = await Answer(planner, order);
        Assert.Equal("Holds", Constraint(held));
        Assert.Contains("Area cleanup after spill", held.GetProperty("why").GetString(), StringComparison.Ordinal);
        // V3.10: the quality engineer who placed it has no release authority for production holds.
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(qe, $"/api/holds/{wcHold}/release", new { reason = "Clean" })).StatusCode);
        await Rejected(plant, planner, $"/api/holds/{wcHold}/release", new { }, "reason_required");
        await plant.Ok(planner, $"/api/holds/{wcHold}/release", new { reason = "Area inspected and clean" });
        await plant.Ok(planner, $"/api/holds/{machineHold}/release", new { reason = "Guard replaced" });
        Assert.Null(Constraint(await Answer(planner, order)));
        var holds = (await Get(planner, "/api/holds?includeReleased=true")).EnumerateArray().ToList();
        Assert.Equal(2, holds.Count);
        Assert.All(holds, h => Assert.Equal($"person:{plant.People["planner"]}", h.GetProperty("releasedBy").GetString()));
        Assert.Empty((await Get(planner, "/api/holds")).EnumerateArray());
    }

    [Fact]
    public async Task Material_must_reach_the_point_of_use_holds_stop_the_shop_floor_and_presence_never_counts_a_schedule()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("kiosk", "ShopFloorTerminal");
        var (e, stores, kiosk) = (plant.Engineer, plant.As("stores"), plant.As("kiosk"));
        var order = await ReleasedOrderAsync(plant, planner);

        // Once slitting has a staging location, the coil must be there before the first operation starts.
        await plant.Ok(stores, "/api/inventory/locations", new { code = "SL-STAGE", siteCode = "PLANT1", name = "Slitting staging", kind = "Staging", workCenterCode = "SLIT-WC" });
        var notStaged = await Answer(planner, order);
        Assert.Equal("PointOfUse", Constraint(notStaged));
        Assert.Contains("COIL-AL needs 26, 0 there", notStaged.GetProperty("why").GetString(), StringComparison.Ordinal);
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "COIL-AL", quantity = 30m, locationCode = "SL-STAGE", reason = "Staged for slitting" });
        Assert.Null(Constraint(await Answer(planner, order)));

        // A shift for everyone; Lee is qualified and on shift but has not clocked in.
        await plant.Ok(e, "/api/resources/shifts", new { code = "ALL", siteCode = "PLANT1", name = "Around the clock", start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 } });
        await plant.Ok(e, "/api/resources/work-centers/SLIT-WC/shifts", new { shiftCode = "ALL" });
        foreach (var who in new[] { "sarah", "michael" })
        {
            await plant.Ok(e, $"/api/workforce/workers/{plant.People[who]}/shift", new { shiftCode = "ALL" });
        }

        var lee = await plant.Person("lee");
        await plant.Ok(e, $"/api/workforce/workers/{lee}", new { homeWorkCenterCode = "SLIT-WC" });
        await plant.Ok(e, $"/api/workforce/workers/{lee}/shift", new { shiftCode = "ALL" });

        // A hold on the order stops the shop floor; released by someone else, work starts.
        await plant.Ok(plant.Admin, $"/api/workforce/people/{plant.People["sarah"]}/badge", new { badge = Sarah });
        await plant.Ok(kiosk, "/api/shopfloor/clock-in", new { badge = Sarah });
        await plant.Ok(stores, "/api/inventory/issues", new { orderNumber = order, partNumber = "COIL-AL", quantity = 26m, locationCode = "SL-STAGE" });
        var hold = Id(await plant.Ok(e, "/api/holds", new { kind = "Production", targetType = "Order", targetId = order.ToString(CultureInfo.InvariantCulture), reason = "Customer asked to wait for a drawing change" }));
        await Rejected(plant, kiosk, "/api/shopfloor/start", new { badge = Sarah, orderNumber = order, sequence = 10, machineCode = "SLITTER-A" }, "on_hold");
        Assert.Equal("Holds", Constraint(await Answer(planner, order)));
        await plant.Ok(planner, $"/api/holds/{hold}/release", new { reason = "Drawing confirmed unchanged" });
        await plant.Ok(kiosk, "/api/shopfloor/start", new { badge = Sarah, orderNumber = order, sequence = 10, machineCode = "SLITTER-A" });

        // Presence: Sarah is on the operation, Michael is assigned, Lee is scheduled only and never counted.
        var presence = (await Get(planner, "/api/readiness/presence?workCenter=SLIT-WC")).EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!);
        Assert.Equal(("ActiveOnOperation", $"{order}/10"), (presence["Sarah"].GetProperty("level").GetString(), presence["Sarah"].GetProperty("operation").GetString()));
        Assert.Equal("AssignedToWorkCenter", presence["Michael"].GetProperty("level").GetString());
        Assert.Equal(("Scheduled", false, true), (presence["Lee"].GetProperty("level").GetString(), presence["Lee"].GetProperty("countsAsPresent").GetBoolean(), presence["Lee"].GetProperty("onShift").GetBoolean()));

        var report = await Get(planner, "/api/reports/presence?status=Scheduled");
        Assert.Equal("Lee", Assert.Single(report.GetProperty("rows").EnumerateArray()).GetProperty("cells")[0].GetString());
        var answer = await (await planner.PostAsJsonAsync(new Uri("/api/analyst/ask", UriKind.Relative), new { question = "Who is scheduled but not clocked in?" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("presence", answer.GetProperty("interpretation").GetProperty("report").GetProperty("reportId").GetString());
    }

    [Fact]
    public async Task Training_on_the_controlled_work_instruction_warns_or_stops_by_policy()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var e = plant.Engineer;

        await plant.Ok(e, "/api/engineering/documents", new { number = "WI-SLIT", title = "Slitting instruction", type = "WorkInstruction", authority = "Internal" });
        await ReleaseAsync(plant, "WI-SLIT", "A");
        await plant.Ok(e, "/api/engineering/items/ROLL-100/revisions/A/documents", new { documentNumber = "WI-SLIT", operationSequence = 10, reason = "Threading instruction" });
        var order = await ReleasedOrderAsync(plant, planner);
        Assert.Null(Constraint(await Answer(planner, order)));

        // Warn names who is untrained and does not stop the work; Enforce does.
        await Rejected(plant, e, "/api/engineering/document-training/policy", new { policy = "Warn" }, "reason_required");
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(planner, "/api/engineering/document-training/policy", new { policy = "Warn", reason = "x" })).StatusCode);
        await plant.Ok(e, "/api/engineering/document-training/policy", new { policy = "Warn", reason = "Training records loaded" });
        var warned = await Answer(planner, order);
        Assert.Null(Constraint(warned));
        var training = warned.GetProperty("dimensions").EnumerateArray().Single(d => d.GetProperty("name").GetString() == "Training");
        Assert.Equal("Info", training.GetProperty("outcome").GetString());
        Assert.Contains("WI-SLIT rev A", training.GetProperty("detail").GetString(), StringComparison.Ordinal);

        await plant.Ok(e, "/api/engineering/document-training/policy", new { policy = "Enforce", reason = "Audit finding" });
        Assert.Equal("Training", Constraint(await Answer(planner, order)));

        await Rejected(plant, e, "/api/engineering/document-training", new { personId = plant.People["sarah"], documentNumber = "WI-SLIT", revision = "Z" }, "revision_not_found", HttpStatusCode.NotFound);
        await plant.Ok(e, "/api/engineering/document-training", new { personId = plant.People["sarah"], documentNumber = "WI-SLIT", revision = "A", trainedOn = Day(-1) });
        Assert.Null(Constraint(await Answer(planner, order)));
        var records = await Get(e, $"/api/engineering/document-training?personId={plant.People["sarah"]}");
        Assert.Equal(("Enforce", "WI-SLIT"), (records.GetProperty("policy").GetProperty("policy").GetString(), Assert.Single(records.GetProperty("records").EnumerateArray()).GetProperty("documentNumber").GetString()));
    }

    private static async Task ReleaseAsync(SlittingPlant.Plant plant, string number, string revision)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/engineering/document-files") { Content = new ByteArrayContent(Encoding.UTF8.GetBytes($"{number} rev {revision}")) };
        request.Headers.Add("Idempotency-Key", "v39-upload-" + Interlocked.Increment(ref key));
        request.Headers.Add("X-File-Name", "file.pdf");
        var response = await plant.Engineer.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        var sha = JsonDocument.Parse(body).RootElement.GetProperty("entityId").GetString()!;
        await plant.Ok(plant.Engineer, $"/api/engineering/documents/{number}/revisions", new { revision, fileSha256 = sha, fileName = $"{number}_{revision}.pdf" });
        await plant.Ok(plant.Engineer, $"/api/engineering/documents/{number}/revisions/{revision}/submit", new { });
        await plant.Ok(plant.Approver, $"/api/engineering/documents/{number}/revisions/{revision}/approve", new { });
        await plant.Ok(plant.Approver, $"/api/engineering/documents/{number}/revisions/{revision}/release", new { reason = "ECN" });
    }
}
