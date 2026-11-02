using System.Globalization;
using System.Net;
using System.Text.Json;
using Forge.Kernel.Postgres;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Integration.Tests;

/// <summary>
/// Sprint 10 WW-S10-14 and WW-S10-29 through the real API: rework that needs work the order never had. An engineer proposes the added
/// solder and retest steps, a connector and the people to train; someone else with Quality approval authority approves. The order gains
/// the operations (labor standard and required qualifications, an inspection step included) and released demand MRP sees, a completed
/// order opens again, trainers are asked to train the named people, and the NCR disposition, the rework record and the existing lines
/// stay exactly as they were.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ReworkPlanTests(PostgresFixture postgres)
{
    private const string Sarah = "B-SARAH-RWK";

    private static string Day(int fromToday) => DateTime.UtcNow.AddDays(fromToday).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

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

    private static async Task PropagateAsync(ForgeApp app)
    {
        var relay = app.Services.GetRequiredService<OutboxRelay>();
        while (await relay.RelayBatchAsync(100, CancellationToken.None) > 0)
        {
        }

        await app.Services.GetRequiredService<SubscriptionPump>().PumpOnceAsync(1_000_000, CancellationToken.None);
    }

    private static decimal State(JsonElement states, string state) =>
        states.GetProperty("states").EnumerateArray().Single(s => s.GetProperty("state").GetString() == state).GetProperty("quantity").GetDecimal();

    [Fact]
    public async Task Approved_rework_adds_operations_inspection_labor_demand_and_training_without_changing_the_disposition()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var e = plant.Engineer;
        await plant.Person("sup", "Supervisor");
        await plant.Person("kiosk", "ShopFloorTerminal");
        await plant.Person("qe", "QualityEngineer");
        await plant.Person("qm", "QualityManager");
        await plant.Person("mq", "ManufacturingEngineer", "QualityManager");
        await plant.Person("trainer", "TechnicalTrainer");
        var (sup, kiosk, qe, qm, mq, trainer, stores) = (plant.As("sup"), plant.As("kiosk"), plant.As("qe"), plant.As("qm"), plant.As("mq"), plant.As("trainer"), plant.As("stores"));

        // The new work needs a solder qualification and a test-inspector qualification nobody holds yet, and one new connector each.
        foreach (var (code, name) in new[] { ("SOLDER-J", "J-STD solder touch-up"), ("TEST-INSP", "Functional test inspector") })
            await plant.Ok(e, "/api/competency/qualification-types", new { code, name, kind = "Process", validityDays = 365 });
        await plant.Ok(e, "/api/engineering/items", new { partNumber = "CONN-2", description = "Two-pin connector", unitOfMeasure = "EA", makeBuy = "Buy", leadTimeDays = 4 });
        await plant.Ok(e, "/api/engineering/items/CONN-2/revisions", new { revision = "A" });
        await plant.Ok(plant.Approver, "/api/engineering/items/CONN-2/revisions/A/release", new { reason = "Baseline" });
        await plant.Ok(e, "/api/execution/reason-codes", new { code = "EDGE", kind = "Scrap", description = "Edge wave" });

        // A 100-roll order runs through both operations and completes.
        var order = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "ROLL-100", quantity = 100m, dueDate = Day(10), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Customer order" });
        await plant.Ok(stores, "/api/inventory/issues", new { orderNumber = order, partNumber = "COIL-AL", quantity = 260m, locationCode = "STORES" });
        await plant.Ok(e, "/api/resources/shifts", new { code = "ALL", siteCode = "PLANT1", name = "Around the clock", start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 } });
        await plant.Ok(e, "/api/resources/work-centers/SLIT-WC/shifts", new { shiftCode = "ALL" });
        await plant.Ok(e, $"/api/workforce/workers/{plant.People["sarah"]}/shift", new { shiftCode = "ALL" });
        await plant.Ok(plant.Admin, $"/api/workforce/people/{plant.People["sarah"]}/badge", new { badge = Sarah });
        await plant.Ok(kiosk, "/api/shopfloor/clock-in", new { badge = Sarah });
        await plant.Ok(kiosk, "/api/shopfloor/start", new { badge = Sarah, orderNumber = order, sequence = 10, machineCode = "SLITTER-A" });
        await plant.Ok(kiosk, "/api/shopfloor/report", new { badge = Sarah, orderNumber = order, sequence = 10, good = 96m, scrap = 4m, scrapReasonCode = "EDGE", complete = true });
        await plant.Ok(kiosk, "/api/shopfloor/start", new { badge = Sarah, orderNumber = order, sequence = 20 });
        await plant.Ok(kiosk, "/api/shopfloor/report", new { badge = Sarah, orderNumber = order, sequence = 20, good = 96m, scrap = 0m, complete = true });
        Assert.Equal("Completed", (await Get(planner, $"/api/orders/{order}")).GetProperty("status").GetString());

        // Ten rolls fail: the NCR is dispositioned rework and the ten go back for it.
        var ncr = Id(await plant.Ok(qe, "/api/quality/ncrs", new { partNumber = "ROLL-100", orderNumber = order, sequence = 20, quantity = 10m, defect = "Open joint", description = "Lead joint open on 10 rolls" }));
        await plant.Ok(qe, $"/api/quality/ncrs/{ncr}/disposition", new { disposition = "Rework", note = "Touch up the joint and retest", reason = "Rework to drawing" });
        var rework = Id(await plant.Ok(sup, "/api/quality/rework", new { kind = "Rework", orderNumber = order, sequence = 20, quantity = 10m, instructions = "Touch up and retest", ncrId = ncr, reason = "NCR rework" }));
        var before = await Get(sup, $"/api/orders/{order}/quantity-states");

        var (dana, sarah) = (plant.People["dana"], plant.People["sarah"]);
        object Solder(int sequence = 30) => new
        {
            sequence, description = "Solder touch-up", workCenterCode = "ASSY-WC", activityCode = "ASSEMBLE", eligibleMachines = Array.Empty<string>(),
            requiredQualifications = new[] { "SOLDER-J" }, setupMinutes = 15m, runMinutesPerUnit = 3m, crewSize = 1, inspection = false,
        };
        object Retest(string[] quals) => new
        {
            sequence = 40, description = "Functional retest", workCenterCode = "ASSY-WC", activityCode = "ASSEMBLE", eligibleMachines = Array.Empty<string>(),
            requiredQualifications = quals, setupMinutes = 0m, runMinutesPerUnit = 2m, crewSize = 1, inspection = true,
        };
        object Plan(object[] operations, int consumedAt = 30) => new
        {
            operations, materials = new[] { new { partNumber = "CONN-2", quantity = 10m, consumedAtSequence = consumedAt } },
            trainees = new[] { dana, sarah }, rationale = "The open joint needs a new connector, a J-STD touch-up and a retest", reason = "NCR rework plan",
        };
        var plans = $"/api/quality/rework/{rework}/plans";

        // Only order setup may propose; the added steps follow the order, an inspection names its inspector, material feeds an added step.
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(sup, plans, Plan([Solder(), Retest(["TEST-INSP"])]))).StatusCode);
        await Rejected(plant, mq, plans, Plan([Solder(15), Retest(["TEST-INSP"])]), "invalid_sequence");
        await Rejected(plant, mq, plans, Plan([Solder(), Retest([])]), "inspection_qualification_required");
        await Rejected(plant, mq, plans, Plan([Solder(), Retest(["TEST-INSP"])], consumedAt: 99), "unknown_operation");
        var proposed = await plant.Ok(mq, plans, Plan([Solder(), Retest(["TEST-INSP"])]));
        var planId = proposed.GetProperty("entityId").GetString()!;
        Assert.Contains("65 planned minutes", proposed.GetProperty("message").GetString(), StringComparison.Ordinal); // 15 + 3x10, then 2x10
        await Rejected(plant, mq, plans, Plan([Solder(), Retest(["TEST-INSP"])]), "plan_already_proposed");
        Assert.Equal(2, (await Get(planner, $"/api/orders/{order}")).GetProperty("operations").GetArrayLength());

        // The proposer cannot decide it, and neither can someone without Quality approval authority.
        var decide = $"/api/quality/rework-plans/{planId}/decide";
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(e, decide, new { approve = true, note = "Fine", reason = "x" })).StatusCode);
        await Rejected(plant, mq, decide, new { approve = true, note = "Mine", reason = "Self" }, "independent_review_required");
        var approved = await plant.Ok(qm, decide, new { approve = true, note = "Approved per NCR review", reason = "MRB" });
        Assert.Contains("4 training request(s)", approved.GetProperty("message").GetString(), StringComparison.Ordinal);
        await Rejected(plant, qm, decide, new { approve = false, note = "Again", reason = "x" }, "plan_decided");

        // WW-S10-14/29: the order gained the steps with their own standards and qualifications, and reopened; nothing it had changed.
        var after = await Get(planner, $"/api/orders/{order}");
        Assert.Equal("Released", after.GetProperty("status").GetString());
        var ops = after.GetProperty("operations").EnumerateArray().ToDictionary(o => o.GetProperty("sequence").GetInt32());
        Assert.Equal(new[] { 10, 20, 30, 40 }, ops.Keys.Order().ToArray());
        Assert.Equal((96m, "Complete"), (ops[20].GetProperty("goodQuantity").GetDecimal(), ops[20].GetProperty("status").GetString()));
        Assert.Equal((45m, 15m, 10m, rework), (ops[30].GetProperty("plannedMinutes").GetDecimal(), ops[30].GetProperty("setupMinutes").GetDecimal(),
            ops[30].GetProperty("plannedQuantity").GetDecimal(), ops[30].GetProperty("reworkId").GetInt64()));
        Assert.Equal(new[] { "SOLDER-J" }, ops[30].GetProperty("requiredQualifications").EnumerateArray().Select(q => q.GetString()).ToArray());
        Assert.Equal((20m, "Pending"), (ops[40].GetProperty("plannedMinutes").GetDecimal(), ops[40].GetProperty("status").GetString()));
        var connector = Assert.Single(after.GetProperty("materials").EnumerateArray(), m => m.GetProperty("componentPartNumber").GetString() == "CONN-2");
        Assert.Equal((10m, 30, rework), (connector.GetProperty("requiredQuantity").GetDecimal(), connector.GetProperty("consumedAtSequence").GetInt32(), connector.GetProperty("reworkId").GetInt64()));
        var ncrRow = (await Get(qe, "/api/quality/ncrs")).EnumerateArray().Single(n => n.GetProperty("ncrId").GetInt64() == ncr);
        Assert.Equal(("Dispositioned", "Rework"), (ncrRow.GetProperty("status").GetString(), ncrRow.GetProperty("disposition").GetString()));
        var reworkRow = Assert.Single((await Get(sup, $"/api/quality/rework?order={order}")).EnumerateArray());
        Assert.Equal((10m, JsonValueKind.Null), (reworkRow.GetProperty("quantity").GetDecimal(), reworkRow.GetProperty("closedAt").ValueKind));
        var states = await Get(sup, $"/api/orders/{order}/quantity-states");
        Assert.Equal((State(before, "Produced"), State(before, "Rework")), (State(states, "Produced"), State(states, "Rework")));

        // The connector is released demand: MRP plans to buy it for this order.
        var mrp = (await Get(planner, "/api/planning/mrp?part=CONN-2")).GetProperty("output");
        var buy = Assert.Single(mrp.GetProperty("plannedOrders").EnumerateArray());
        Assert.Equal(10m, buy.GetProperty("quantity").GetDecimal());
        Assert.Contains($"WO{order}/CONN-2", buy.GetProperty("pegging").EnumerateArray().Select(p => p.GetProperty("requirementKey").GetString()));

        // The named people are not qualified, so the trainers are asked to train each of them for each qualification; nobody is certified.
        foreach (var person in new[] { dana, sarah })
        {
            var requests = (await Get(trainer, $"/api/workforce/training-requests?personId={person}&open=true")).EnumerateArray()
                .Where(r => r.GetProperty("source").GetString() != "Onboarding").ToList(); // site-wide HazCom onboarding opens its own
            Assert.Equal(new[] { "SOLDER-J", "TEST-INSP" }, requests.Select(r => r.GetProperty("qualificationCode").GetString()).Order(StringComparer.Ordinal).ToArray());
            Assert.All(requests, r => Assert.Equal("Rework", r.GetProperty("source").GetString()));
        }

        await PropagateAsync(app);
        Assert.Contains((await Get(trainer, "/api/notifications/inbox")).EnumerateArray(), a => a.GetProperty("message").GetString()!.Contains("(approved rework plan", StringComparison.Ordinal));

        // The added work is gated like any work: Sarah may not do it unqualified, and the rework cannot be closed before it is done.
        await Rejected(plant, kiosk, "/api/shopfloor/start", new { badge = Sarah, orderNumber = order, sequence = 30 }, "not_qualified");
        await Rejected(plant, sup, $"/api/quality/rework/{rework}/close", new { goodQuantity = 10m, scrapQuantity = 0m, note = "Done" }, "rework_work_open");

        // A later plan cannot slot work in among what is already there, and the decided plan stays in the history.
        await Rejected(plant, mq, plans, new
        {
            operations = new[] { Solder(35) }, materials = Array.Empty<object>(), trainees = Array.Empty<Guid>(), rationale = "Second touch-up", reason = "x",
        }, "invalid_sequence");
        var history = Assert.Single((await Get(sup, $"/api/quality/rework-plans?order={order}")).EnumerateArray());
        Assert.Equal(("Approved", "Approved per NCR review"), (history.GetProperty("status").GetString(), history.GetProperty("decisionNote").GetString()));
    }
}
