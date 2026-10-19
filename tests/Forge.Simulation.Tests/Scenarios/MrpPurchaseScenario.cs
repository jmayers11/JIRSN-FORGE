using System.Globalization;
using Forge.Integration.Tests;
using Forge.Simulation.Tests.Engine;

namespace Forge.Simulation.Tests.Scenarios;

/// <summary>
/// Scenario 21 (MRP planned purchase): a customer orders film nobody has. MRP plans a purchase of exactly that amount;
/// a requisition raised from the plan carries the demand it is for, and a second requisition from the same plan is
/// refused. The buyer orders it and the purchasing manager approves; MRP then plans nothing more. When the customer
/// adds to the order, MRP plans exactly the addition. Expectations come from the Phase 6 and V2.7 specifications (MRP
/// nets demand against stock and approved supply lot-for-lot; a requisition keeps its pegged source), never from Forge.
/// </summary>
internal static class MrpPurchaseScenario
{
    public const string Name = "S21-mrp-planned-purchase";
    private const string Spec = "Phase 6 and V2.7 MRP";
    private const string Film = "SIM-FILM";

    public static EventLog Plan(int seed)
    {
        var log = new EventLog(Name, seed);
        log.Add(TimeSpan.Zero, "Demand", ("quantity", log.Between(50, 200)), ("needInDays", log.Between(25, 40)));
        log.Add(TimeSpan.FromHours(1), "Requisition");
        log.Add(TimeSpan.FromHours(2), "Purchase", ("promisedInDays", log.Between(10, 20)));
        log.Add(TimeSpan.FromHours(3), "MoreDemand", ("extra", log.Between(10, 40)));
        return log;
    }

    public static async Task<(SimReport Report, EventLog Log, string Hash)> RunAsync(PostgresFixture postgres, int seed, Mutation mutation = Mutation.None)
    {
        var log = Plan(seed);
        var report = new SimReport(mutation == Mutation.None ? Name : Name + RunSummary.CanaryMarker + mutation, seed);
        await using var f = await SimFactory.BuildPlanningAsync(postgres, "s21-" + seed.ToString(CultureInfo.InvariantCulture));
        report.Evidence(f.Api);
        report.Covers(Coverage.Mrp, Coverage.Procurement, Coverage.Demand);
        await f.PeopleAsync(("bob", "Buyer"));
        var (e, approver, planner, bob, pat) = (f.Plant.Engineer, f.Plant.Approver, f.Planner, f.Api.As("bob"), f.Api.As("pat"));
        await f.Plant.Ok(e, "/api/engineering/items", new { partNumber = Film, description = "Protective film", unitOfMeasure = "M", makeBuy = "Buy", leadTimeDays = 7 });
        await f.Plant.Ok(e, $"/api/engineering/items/{Film}/revisions", new { revision = "A" });
        await f.Plant.Ok(approver, $"/api/engineering/items/{Film}/revisions/A/release", new { reason = "Simulation part release" });
        await f.Plant.Ok(bob, "/api/procurement/suppliers", new { code = "SIMFILM", name = "Simulated film supplier" });
        var parts = new[] { Film };
        long demand = 0, requisition = 0;
        decimal wanted = 0m, ordered = 0m;
        string need = string.Empty;

        async Task<List<System.Text.Json.JsonElement>> Planned() =>
            (await SimApi.Get(planner, $"/api/planning/mrp?part={Film}")).GetProperty("output").GetProperty("plannedOrders").EnumerateArray()
                .Where(p => p.GetProperty("partNumber").GetString() == Film).ToList();

        async Task ExpectPlanned(string step)
        {
            report.Rule(Basis.Spec, Spec + ": MRP plans exactly the demand not covered by stock or approved purchase orders");
            report.Expect($"{step}: planned purchase", wanted - ordered, (await Planned()).Sum(p => p.GetProperty("quantity").GetDecimal()), "Planning");
        }

        var sinceStart = TimeSpan.Zero;
        foreach (var ev in log.Events)
        {
            f.Clock.Advance(ev.At - sinceStart);
            sinceStart = ev.At;
            var step = $"#{ev.Index} {ev.Type}";
            switch (ev.Type)
            {
                case "Demand":
                {
                    wanted = ev.Number("quantity");
                    need = f.Day((int)ev.Number("needInDays"));
                    report.Rule(Basis.Plan, "Setup: a customer orders film");
                    var recorded = await f.Api.Send(planner, "/api/planning/demand", new { partNumber = Film, quantity = wanted, needDate = need, priority = 3, kind = "CustomerOrder", customer = "Film customer", reference = "PO-F1", reason = "Customer order" });
                    report.Expect($"{step}: demand recorded", "200", recorded.Label);
                    demand = recorded.Id;
                    await ExpectPlanned(step);
                    break;
                }

                case "Requisition":
                {
                    var key = (await Planned()).Select(p => p.GetProperty("key").GetString()).FirstOrDefault() ?? "none";
                    report.Rule(Basis.Spec, "V2.7: a requisition raised from a planned purchase carries the plan's quantity and the demand it is for");
                    var raised = await f.Api.Send(planner, "/api/planning/mrp/requisitions", new { plannedOrder = key, supplierCode = "SIMFILM", reason = "Planned need" });
                    report.Expect($"{step}: requisition raised", "200", raised.Label);
                    requisition = raised.Succeeded ? raised.Id : 0;
                    var r = (await SimApi.Get(bob, "/api/procurement/requisitions?open=false")).EnumerateArray().FirstOrDefault(x => x.GetProperty("requisitionId").GetInt64() == requisition);
                    report.Expect($"{step}: requisition quantity", wanted, r.ValueKind == System.Text.Json.JsonValueKind.Object ? r.GetProperty("quantity").GetDecimal() : -1m, "Planning");
                    report.Expect($"{step}: requisition pegged to the demand", demand.ToString(CultureInfo.InvariantCulture),
                        r.ValueKind == System.Text.Json.JsonValueKind.Object && r.TryGetProperty("demandId", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.Number ? d.GetInt64().ToString(CultureInfo.InvariantCulture) : "none", "Planning");
                    report.Expect($"{step}: a second requisition from the same plan refused", "422 requisition_exists",
                        (await f.Api.Send(planner, "/api/planning/mrp/requisitions", new { plannedOrder = key, supplierCode = "SIMFILM", reason = "Again" })).Label);
                    break;
                }

                case "Purchase":
                {
                    report.Rule(Basis.Spec, "Procurement: the buyer orders the requisition; a different person with purchasing authority approves");
                    var po = await f.Api.Send(bob, "/api/procurement/purchase-orders", new
                    {
                        supplier = "SIMFILM", reason = "Planned need",
                        lines = new[] { new { partNumber = Film, quantity = wanted, promisedDate = f.Day((int)ev.Number("promisedInDays")), requisitionId = (long?)requisition } },
                    });
                    report.Expect($"{step}: purchase order created", "200", po.Label);
                    report.Expect($"{step}: approved by the purchasing manager", "200", (await f.Api.Send(pat, $"/api/procurement/purchase-orders/{po.Id}/approve", new { reason = "Planned need" })).Label);
                    ordered = wanted;
                    await ExpectPlanned(step);
                    break;
                }

                case "MoreDemand":
                {
                    var extra = ev.Number("extra");
                    report.Rule(Basis.Plan, "Setup: the customer adds to the order");
                    report.Expect($"{step}: demand raised", "200", (await f.Api.Send(planner, $"/api/planning/demand/{demand}/change", new { quantity = wanted + extra, needDate = need, priority = 3, reason = "Customer added film" })).Label);
                    wanted += extra;
                    await ExpectPlanned(step);
                    break;
                }
            }

            await Invariants.Inventory(report, step, f.StoresUser, parts);
        }

        await Invariants.AuditChain(report, "End", f.Plant.Admin);
        return (report, log, await Invariants.StateHash(f.StoresUser, parts));
    }
}
