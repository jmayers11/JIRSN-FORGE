using System.Globalization;
using Forge.Integration.Tests;
using Forge.Simulation.Tests.Engine;

namespace Forge.Simulation.Tests.Scenarios;

/// <summary>
/// Scenario 7 (inaccurate inventory): the book says there is enough tape for an order; a count finds less. The count posts
/// nothing until a different person approves it; the requester cannot approve their own count. Once approved, the order
/// is short and cannot be scheduled, MRP recommends exactly the shortfall, and a replenishment receipt makes it ready
/// again. Expectations come from the Phase 6 specification (a count posts its variance only on approval by another
/// person; material drives readiness, the schedule and MRP), computed from the plan, never from Forge.
/// </summary>
internal static class InaccurateInventoryScenario
{
    public const string Name = "S07-inaccurate-inventory";
    private const string Spec = "Phase 6 spec: counts, approvals and material readiness";
    private const decimal TapePerPack = 2m;

    public static EventLog Plan(int seed)
    {
        var log = new EventLog(Name, seed);
        var packs = log.Between(10, 40);
        var onBook = (int)(packs * TapePerPack) + log.Between(5, 30);
        var counted = log.Between(1, (int)(packs * TapePerPack) - 1);
        log.Add(TimeSpan.Zero, "Stock", ("quantity", onBook));
        log.Add(TimeSpan.Zero, "Order", ("quantity", packs), ("dueInDays", log.Between(8, 12)));
        log.Add(TimeSpan.FromMinutes(5), "Baseline");
        log.Add(TimeSpan.FromMinutes(30), "Count", ("counted", counted));
        log.Add(TimeSpan.FromMinutes(45), "Approve");
        log.Add(TimeSpan.FromHours(3), "Replenish");
        return log;
    }

    public static async Task<(SimReport Report, EventLog Log, string Hash)> RunAsync(PostgresFixture postgres, int seed, Mutation mutation = Mutation.None)
    {
        var log = Plan(seed);
        var report = new SimReport(mutation == Mutation.None ? Name : Name + RunSummary.CanaryMarker + mutation, seed);
        await using var f = await SimFactory.BuildPlanningAsync(postgres, "s07-" + seed.ToString(CultureInfo.InvariantCulture));
        report.Evidence(f.Api);
        report.Covers(Coverage.Warehouse, Coverage.Mrp, Coverage.Scheduling);
        await f.PeopleAsync(("ivy", "InventoryController"));
        var (e, stores, ivy, planner) = (f.Plant.Engineer, f.StoresUser, f.Api.As("ivy"), f.Planner);
        var parts = new[] { "SIM-TAPE" };
        KpiSnapshot previous = Kpis.Empty;
        long order = 0, count = 0;
        decimal book = 0m, need = 0m, counted = 0m;

        await f.Plant.Ok(e, "/api/engineering/items", new { partNumber = "SIM-TAPE", description = "Packing tape", unitOfMeasure = "M", makeBuy = "Buy", leadTimeDays = 3 });
        await f.Plant.Ok(e, "/api/engineering/items", new { partNumber = "SIM-PACK", description = "Packed set", unitOfMeasure = "EA", makeBuy = "Make", leadTimeDays = 1 });
        await f.Plant.Ok(e, "/api/engineering/items/SIM-TAPE/revisions", new { revision = "A" });
        await f.Plant.Ok(e, "/api/engineering/items/SIM-PACK/revisions", new { revision = "A" });
        await f.Plant.Ok(e, "/api/engineering/items/SIM-PACK/revisions/A/bom", new { lineNumber = 1, componentPartNumber = "SIM-TAPE", quantityPer = TapePerPack, scrapPercent = 0m });
        await f.Plant.Ok(e, "/api/engineering/items/SIM-PACK/revisions/A/routing", new { sequence = 10, description = "Pack", workCenterCode = "ASSY-WC", activityCode = "ASSEMBLE", setupMinutes = 0m, runMinutesPerUnit = 2m, crewSize = 1 });
        await f.Plant.Ok(f.Plant.Approver, "/api/engineering/items/SIM-TAPE/revisions/A/release", new { reason = "Simulation part release" });
        await f.Plant.Ok(f.Plant.Approver, "/api/engineering/items/SIM-PACK/revisions/A/release", new { reason = "Simulation part release" });

        async Task<decimal> OnHand() =>
            (await Invariants.ReportedBalances(stores, parts)).GetValueOrDefault(new Bucket("SIM-TAPE", SimFactory.Stores, null, "Released"));

        async Task<string> MaterialState() =>
            (await SimApi.Get(planner, "/api/planning/material")).GetProperty("output").EnumerateArray()
                .Where(m => m.GetProperty("orderNumber").GetInt64() == order).Select(m => m.GetProperty("state").GetString()!).FirstOrDefault() ?? "none";

        async Task<string> Status()
        {
            await f.Api.Send(planner, "/api/planning/schedules", new { horizonDays = 30, reason = "Simulation reading" });
            return (await SimApi.Get(planner, "/api/planning/schedules/proposed")).GetProperty("result").GetProperty("orders").EnumerateArray()
                .Single(o => o.GetProperty("orderNumber").GetInt64() == order).GetProperty("status").GetString()!;
        }

        var sinceStart = TimeSpan.Zero;
        foreach (var ev in log.Events)
        {
            f.Clock.Advance(ev.At - sinceStart);
            sinceStart = ev.At;
            var step = $"#{ev.Index} {ev.Type}";
            switch (ev.Type)
            {
                case "Stock":
                    book = ev.Number("quantity");
                    report.Rule(Basis.Plan, "Setup: tape on the book");
                    report.Expect($"{step}: received", "200", (await f.Api.Send(stores, "/api/inventory/receipts", new { partNumber = "SIM-TAPE", quantity = book, locationCode = SimFactory.Stores, reason = "Opening balance" })).Label);
                    break;

                case "Order":
                    need = ev.Number("quantity") * TapePerPack;
                    report.Rule(Basis.Plan, "Setup: a released customer order");
                    order = await f.ReleasedOrderAsync("SIM-PACK", ev.Number("quantity"), (int)ev.Number("dueInDays"));
                    break;

                case "Baseline":
                    previous = report.Kpi(await Kpis.CollectAsync(f, "baseline", true, parts));
                    report.Rule(Basis.Spec, Spec + ": the book covers the order, so it is ready and on time");
                    report.Expect($"{step}: material state", "Ready", await MaterialState());
                    report.Expect($"{step}: schedule status", "OnTime", await Status());
                    break;

                case "Count":
                {
                    counted = ev.Number("counted");
                    report.Rule(Basis.Spec, Spec + ": a count records its variance and posts nothing until approved");
                    var recorded = await f.Api.Send(stores, "/api/inventory/counts", new { partNumber = "SIM-TAPE", locationCode = SimFactory.Stores, counted });
                    report.Expect($"{step}: count recorded", "200", recorded.Label);
                    count = recorded.Succeeded ? recorded.Id : 0;
                    report.Expect($"{step}: the count states its variance", "True", recorded.Body.Contains("variance", StringComparison.Ordinal) ? "True" : "False");
                    report.Expect($"{step}: stock still on the book", book, await OnHand());
                    report.Rule(Basis.Spec, Spec + ": the person who counted cannot approve their own count");
                    var own = await f.Api.Send(stores, $"/api/inventory/requests/{count}/approve", new { reason = "My count" });
                    report.Expect($"{step}: self-approval refused", "True", !own.Succeeded && own.Body.Contains("separation_of_duties", StringComparison.Ordinal) ? "True" : "False", "Authorization");
                    break;
                }

                case "Approve":
                {
                    report.Rule(Basis.Spec, Spec + ": a different person approves; the variance posts and the order no longer has its material");
                    report.Expect($"{step}: count approved", "200", (await f.Api.Send(mutation == Mutation.SelfApproveCount ? stores : ivy, $"/api/inventory/requests/{count}/approve", new { reason = "Recount agreed" })).Label);
                    report.Expect($"{step}: stock is the counted quantity", counted, await OnHand());
                    report.Expect($"{step}: material state", "Shortfall", await MaterialState());
                    report.Expect($"{step}: schedule status", "Unschedulable", await Status());
                    var mrp = (await SimApi.Get(planner, "/api/planning/mrp?part=SIM-TAPE")).GetProperty("output").GetProperty("plannedOrders").EnumerateArray().ToList();
                    report.Expect($"{step}: MRP plans exactly the shortfall", need - counted, mrp.Sum(p => p.GetProperty("quantity").GetDecimal()), "Inventory");
                    var after = report.Kpi(await Kpis.CollectAsync(f, "after count approved", true, parts));
                    report.ImpactOf("Count variance approved", previous, after);
                    previous = after;
                    break;
                }

                case "Replenish":
                {
                    report.Rule(Basis.Spec, Spec + ": receiving the shortfall makes the order ready and on time again");
                    report.Expect($"{step}: replenishment received", "200", (await f.Api.Send(stores, "/api/inventory/receipts", new { partNumber = "SIM-TAPE", quantity = need - counted, locationCode = SimFactory.Stores, reason = "Replenishment" })).Label);
                    report.Expect($"{step}: stock", need, await OnHand());
                    report.Expect($"{step}: material state", "Ready", await MaterialState());
                    report.Expect($"{step}: schedule status", "OnTime", await Status());
                    var after = report.Kpi(await Kpis.CollectAsync(f, "after replenishment", true, parts));
                    report.ImpactOf("Replenishment received", previous, after);
                    break;
                }
            }

            await Invariants.Inventory(report, step, stores, parts);
        }

        await Invariants.AuditChain(report, "End", f.Plant.Admin);
        return (report, log, await Invariants.StateHash(stores, parts));
    }
}
