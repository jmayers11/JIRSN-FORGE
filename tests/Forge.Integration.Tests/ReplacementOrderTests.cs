using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Forge.Integration.Tests;

/// <summary>
/// Sprint 10 WW-S10-35 through the real API: a replacement order for scrapped production is proposed with a check of every routing
/// operation (who is qualified and authorized now, and what moving them leaves unstaffed at the source), and only a different person
/// who may release orders turns it into a planned order. Nobody is assigned by it, and uncovered work must be accepted explicitly.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ReplacementOrderTests(PostgresFixture postgres)
{
    private const string Url = "/api/planning/replacement-orders";

    private static string Day(int days) => DateTime.UtcNow.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task Rejected(SlittingPlant.Plant plant, HttpClient client, string url, object body, string code)
    {
        var response = await plant.Try(client, url, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{url} -> {(int)response.StatusCode}: {text}");
        Assert.Contains(code, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_replacement_order_is_checked_for_qualified_operators_and_source_impact_before_a_second_person_approves_it()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var e = plant.Engineer;
        foreach (var (subject, role) in new[] { ("qe", "QualityEngineer"), ("planner2", "Planner"), ("sup", "Supervisor"), ("kiosk", "ShopFloorTerminal") })
            await plant.Person(subject, role);
        var (qe, planner2, sup, kiosk) = (plant.As("qe"), plant.As("planner2"), plant.As("sup"), plant.As("kiosk"));
        foreach (var person in new[] { "sarah", "michael", "jennifer", "dana" })
            await plant.Ok(e, $"/api/workforce/workers/{plant.People[person]}/presence", new { kind = "SupervisorConfirmed", reason = "On shift" });

        // Five of twenty rolls are scrapped at slitting.
        var order = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "ROLL-100", quantity = 20m, dueDate = Day(10), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Customer order" });
        var ncr = Id(await plant.Ok(qe, "/api/quality/ncrs", new { partNumber = "ROLL-100", orderNumber = order, sequence = 10, quantity = 5m, defect = "Edge wave", description = "Five rolls wavy" }));
        object Propose(decimal quantity = 5m, long? ncrId = null) => new { partNumber = "ROLL-100", quantity, dueDate = Day(12), priority = 2, ncrId = ncrId ?? ncr, rationale = "Replace scrapped rolls for the customer", reason = "Scrap recovery" };
        await Rejected(plant, e, Url, Propose(), "ncr_not_scrapped");
        await plant.Ok(qe, $"/api/quality/ncrs/{ncr}/disposition", new { disposition = "Scrap", note = "Edges cannot be recut", reason = "MRB" });

        // Only people who may create orders propose one, never more than was scrapped.
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(sup, Url, Propose())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(kiosk, Url, Propose())).StatusCode);
        await Rejected(plant, e, Url, Propose(6m), "quantity_exceeds_scrap");
        var proposalId = (await plant.Ok(e, Url, Propose())).GetProperty("entityId").GetString()!;

        // The check is stored with the proposal: each operation on each eligible machine, and only people who hold the qualification.
        var proposal = Assert.Single((await Get(planner, Url + "?open=true")).EnumerateArray());
        Assert.Equal(("Proposed", order, "A"), (proposal.GetProperty("status").GetString(), proposal.GetProperty("replacesOrderNumber").GetInt64(), proposal.GetProperty("revision").GetString()));
        var checks = proposal.GetProperty("checks").EnumerateArray().ToList();
        Assert.Equal("10:SLITTER-A,10:SLITTER-B,20:", string.Join(",", checks.Select(c => $"{c.GetProperty("sequence").GetInt32()}:{(c.GetProperty("machineCode").ValueKind == JsonValueKind.Null ? "" : c.GetProperty("machineCode").GetString())}")));
        var names = plant.People.ToDictionary(p => p.Value, p => p.Key);
        string[] Candidates(JsonElement check) => check.GetProperty("candidates").EnumerateArray().Select(c => names[c.GetProperty("personId").GetGuid()]).ToArray();
        foreach (var slit in checks.Where(c => c.GetProperty("sequence").GetInt32() == 10))
            Assert.All(Candidates(slit), person => Assert.Contains(person, new[] { "sarah", "michael" })); // SLIT-II holders only
        Assert.All(Candidates(checks[2]), person => Assert.Contains(person, new[] { "sarah", "jennifer" })); // FIRST-AID holders only
        var slitterA = checks[0];
        Assert.False(slitterA.GetProperty("readyNow").GetBoolean());
        Assert.NotEmpty(Candidates(slitterA));
        // Every option says what it costs the work the person is on now: a source impact or a backfill move.
        Assert.Contains(checks.SelectMany(c => c.GetProperty("candidates").EnumerateArray()),
            c => c.GetProperty("requiresSourceBackfill").GetBoolean() || c.GetProperty("sourceImpacts").GetArrayLength() > 0);

        // Nothing exists yet: one order of ROLL-100, and nobody was moved.
        async Task<int> RollOrders() => (await Get(planner, "/api/orders/")).EnumerateArray().Count(o => o.GetProperty("partNumber").GetString() == "ROLL-100");
        Assert.Equal(1, await RollOrders());

        // A planner who did not propose it approves; the planned order follows the normal order rules.
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(e, $"{Url}/{proposalId}/decide", new { approve = true, note = "x", reason = "x" })).StatusCode);
        var approved = await plant.Ok(planner, $"{Url}/{proposalId}/decide", new { approve = true, note = "Sarah moves to Slitter A; Dana backfills staging", reason = "Recovery" });
        Assert.Contains("nobody was assigned", approved.GetProperty("message").GetString(), StringComparison.Ordinal);
        await Rejected(plant, planner2, $"{Url}/{proposalId}/decide", new { approve = false, note = "x", reason = "x" }, "proposal_decided");
        var decided = Assert.Single((await Get(planner, Url)).EnumerateArray());
        var created = await Get(planner, $"/api/orders/{decided.GetProperty("orderNumber").GetInt64()}");
        Assert.Equal(("Planned", 5m), (created.GetProperty("status").GetString(), created.GetProperty("quantity").GetDecimal()));
        Assert.Equal(2, await RollOrders());
        Assert.Empty((await Get(planner, Url + "?open=true")).EnumerateArray());

        // Work nobody present is qualified for: the proposer cannot decide it, and approval needs the gap accepted explicitly.
        await plant.Ok(e, "/api/competency/qualification-types", new { code = "BOX-Q", name = "Box assembly", kind = "Process", validityDays = 365 });
        await plant.Ok(e, "/api/engineering/items", new { partNumber = "BOX-1", description = "Shipping box", unitOfMeasure = "EA", makeBuy = "Make", leadTimeDays = 1 });
        await plant.Ok(e, "/api/engineering/items/BOX-1/revisions", new { revision = "A" });
        await plant.Ok(e, "/api/engineering/items/BOX-1/revisions/A/routing", new { sequence = 10, description = "Assemble", workCenterCode = "ASSY-WC", activityCode = "ASSEMBLE",
            requiredQualifications = new[] { "BOX-Q" }, setupMinutes = 0m, runMinutesPerUnit = 1m, crewSize = 1 });
        await plant.Ok(plant.Approver, "/api/engineering/items/BOX-1/revisions/A/release", new { reason = "ECN-2" });
        var box = (await plant.Ok(planner, Url, new { partNumber = "BOX-1", quantity = 3m, dueDate = Day(5), priority = 4, rationale = "Lost in transit", reason = "Recovery" })).GetProperty("entityId").GetString()!;
        var boxCheck = Assert.Single(Assert.Single((await Get(planner, Url + "?open=true")).EnumerateArray()).GetProperty("checks").EnumerateArray());
        Assert.Equal((false, 0), (boxCheck.GetProperty("readyNow").GetBoolean(), boxCheck.GetProperty("candidates").GetArrayLength()));
        await Rejected(plant, planner, $"{Url}/{box}/decide", new { approve = true, note = "x", reason = "x" }, "independent_review_required");
        await Rejected(plant, planner2, $"{Url}/{box}/decide", new { approve = true, note = "Train someone", reason = "x" }, "uncovered_operations");
        await plant.Ok(planner2, $"{Url}/{box}/decide", new { approve = false, note = "Nobody can build it yet; train first", reason = "Coverage" });
        Assert.Equal("Approved,Rejected", string.Join(",", (await Get(planner, Url)).EnumerateArray().Select(p => p.GetProperty("status").GetString())));
        Assert.Equal(0, (await Get(planner, "/api/orders/")).EnumerateArray().Count(o => o.GetProperty("partNumber").GetString() == "BOX-1"));
    }
}
