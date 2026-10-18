using System.Globalization;
using System.Text.Json;
using Forge.Integration.Tests;
using Forge.Simulation.Tests.Engine;

namespace Forge.Simulation.Tests.Scenarios;

/// <summary>
/// Scenario 6 (supplier delay): an order for wire bundles has no wire. An approved purchase order makes the wire expected
/// and the order schedulable on time; the supplier then slips its promised date past the order's due date, so the order
/// becomes late; finally the wire arrives early and the order is ready and on time again. MRP buys exactly what is still
/// needed. Expectations come from the Phase 6 specification (material drives readiness and the schedule; the schedule
/// waits for an expected receipt's date; MRP explains its recommendation), computed from the plan, not from Forge.
/// </summary>
internal static class SupplierDelayScenario
{
    public const string Name = "S06-supplier-delay";
    private const string Spec = "Phase 6 spec: material drives order readiness and the schedule";
    private const decimal WirePerBundle = 5m;

    public static EventLog Plan(int seed)
    {
        var log = new EventLog(Name, seed);
        var due = log.Between(8, 12);
        log.Add(TimeSpan.Zero, "Order", ("quantity", log.Between(4, 12)), ("dueInDays", due));
        log.Add(TimeSpan.FromMinutes(5), "Short");
        log.Add(TimeSpan.FromMinutes(10), "Purchase", ("promisedInDays", log.Between(2, 4)));
        log.Add(TimeSpan.FromHours(4), "SupplierDelay", ("promisedInDays", due + log.Between(2, 5)));
        log.Add(TimeSpan.FromHours(8), "EarlyArrival");
        return log;
    }

    public static async Task<(SimReport Report, EventLog Log, string Hash)> RunAsync(PostgresFixture postgres, int seed, Mutation mutation = Mutation.None)
    {
        var log = Plan(seed);
        var report = new SimReport(mutation == Mutation.None ? Name : Name + RunSummary.CanaryMarker + mutation, seed);
        await using var f = await SimFactory.BuildPlanningAsync(postgres, "s06-" + seed.ToString(CultureInfo.InvariantCulture));
        report.Evidence(f.Api);
        report.Covers(Coverage.Procurement, Coverage.Mrp, Coverage.Scheduling, Coverage.Demand);
        await f.PeopleAsync(("bob", "Buyer"));
        var (e, bob, pat, planner) = (f.Plant.Engineer, f.Api.As("bob"), f.Api.As("pat"), f.Planner);
        var parts = new[] { "SIM-WIRE" };
        KpiSnapshot previous = Kpis.Empty;
        long order = 0, po = 0;
        decimal wire = 0m;

        // Setup: bought wire nobody has, and a bundle assembled from 5 m of it at Assembly.
        await f.Plant.Ok(e, "/api/engineering/items", new { partNumber = "SIM-WIRE", description = "Copper wire", unitOfMeasure = "M", makeBuy = "Buy", leadTimeDays = 5 });
        await f.Plant.Ok(e, "/api/engineering/items", new { partNumber = "SIM-BUNDLE", description = "Wire bundle", unitOfMeasure = "EA", makeBuy = "Make", leadTimeDays = 1 });
        await f.Plant.Ok(e, "/api/engineering/items/SIM-WIRE/revisions", new { revision = "A" });
        await f.Plant.Ok(e, "/api/engineering/items/SIM-BUNDLE/revisions", new { revision = "A" });
        await f.Plant.Ok(e, "/api/engineering/items/SIM-BUNDLE/revisions/A/bom", new { lineNumber = 1, componentPartNumber = "SIM-WIRE", quantityPer = WirePerBundle, scrapPercent = 0m });
        await f.Plant.Ok(e, "/api/engineering/items/SIM-BUNDLE/revisions/A/routing", new { sequence = 10, description = "Bundle", workCenterCode = "ASSY-WC", activityCode = "ASSEMBLE", setupMinutes = 0m, runMinutesPerUnit = 5m, crewSize = 1 });
        await f.Plant.Ok(f.Plant.Approver, "/api/engineering/items/SIM-WIRE/revisions/A/release", new { reason = "Simulation part release" });
        await f.Plant.Ok(f.Plant.Approver, "/api/engineering/items/SIM-BUNDLE/revisions/A/release", new { reason = "Simulation part release" });
        await f.Plant.Ok(bob, "/api/procurement/suppliers", new { code = "SIMWIRE", name = "Simulated wire supplier" });

        async Task<string> MaterialState() =>
            (await SimApi.Get(planner, "/api/planning/material")).GetProperty("output").EnumerateArray()
                .Where(m => m.GetProperty("orderNumber").GetInt64() == order).Select(m => m.GetProperty("state").GetString()!).FirstOrDefault() ?? "none";

        async Task<(string Status, DateTimeOffset? Start)> Scheduled()
        {
            await f.Api.Send(planner, "/api/planning/schedules", new { horizonDays = 30, reason = "Simulation reading" });
            var result = (await SimApi.Get(planner, "/api/planning/schedules/proposed")).GetProperty("result");
            var status = result.GetProperty("orders").EnumerateArray().Single(o => o.GetProperty("orderNumber").GetInt64() == order).GetProperty("status").GetString()!;
            var op = result.GetProperty("operations").EnumerateArray().FirstOrDefault(o => o.GetProperty("orderNumber").GetInt64() == order);
            DateTimeOffset? start = op.ValueKind == JsonValueKind.Object && op.TryGetProperty("start", out var s) && s.ValueKind == JsonValueKind.String ? s.GetDateTimeOffset() : null;
            return (status, start);
        }

        var sinceStart = TimeSpan.Zero;
        foreach (var ev in log.Events)
        {
            f.Clock.Advance(ev.At - sinceStart);
            sinceStart = ev.At;
            var step = $"#{ev.Index} {ev.Type}";
            switch (ev.Type)
            {
                case "Order":
                    report.Rule(Basis.Plan, "Setup: a released customer order");
                    wire = ev.Number("quantity") * WirePerBundle;
                    order = await f.ReleasedOrderAsync("SIM-BUNDLE", ev.Number("quantity"), (int)ev.Number("dueInDays"));
                    break;

                case "Short":
                {
                    previous = report.Kpi(await Kpis.CollectAsync(f, "no wire", true, parts));
                    report.Rule(Basis.Spec, Spec + ": with no wire on hand or expected, the order is short and cannot be scheduled");
                    report.Expect($"{step}: material state", "Shortfall", await MaterialState());
                    report.Expect($"{step}: schedule status", "Unschedulable", (await Scheduled()).Status);
                    report.Rule(Basis.Spec, Spec + ": MRP recommends buying exactly what the order still needs");
                    var mrp = (await SimApi.Get(planner, "/api/planning/mrp?part=SIM-WIRE")).GetProperty("output").GetProperty("plannedOrders").EnumerateArray().ToList();
                    report.Expect($"{step}: MRP planned quantity", wire, mrp.Sum(p => p.GetProperty("quantity").GetDecimal()), "Inventory");
                    break;
                }

                case "Purchase":
                {
                    var promised = (int)ev.Number("promisedInDays");
                    report.Rule(Basis.Plan, "Setup: a buyer raises the purchase order and a purchasing manager approves it");
                    var created = await f.Api.Send(bob, "/api/procurement/purchase-orders", new { supplier = "SIMWIRE", lines = new[] { new { partNumber = "SIM-WIRE", quantity = wire, promisedDate = f.Day(promised) } }, reason = "Wire for bundles" });
                    report.Expect($"{step}: purchase order created", "200", created.Label);
                    po = created.Succeeded ? created.Id : 0;
                    report.Expect($"{step}: purchase order approved", "200", (await f.Api.Send(pat, $"/api/procurement/purchase-orders/{po}/approve", new { reason = "Approved" })).Label);
                    report.Rule(Basis.Spec, Spec + ": an approved purchase order makes the material expected; the schedule waits for its date and still meets the due date");
                    report.Expect($"{step}: material state", "Expected", await MaterialState());
                    var (status, start) = await Scheduled();
                    report.Expect($"{step}: schedule status", "OnTime", status);
                    report.Expect($"{step}: the operation starts on or after the promised date", "True",
                        start is { } s && DateOnly.FromDateTime(s.UtcDateTime) >= DateOnly.Parse(f.Day(promised), CultureInfo.InvariantCulture) ? "True" : "False");
                    var after = report.Kpi(await Kpis.CollectAsync(f, "PO approved", true, parts));
                    report.ImpactOf("Purchase order approved", previous, after);
                    previous = after;
                    break;
                }

                case "SupplierDelay":
                {
                    var promised = (int)ev.Number("promisedInDays");
                    report.Rule(Basis.Plan, "Setup: the supplier slips; the buyer records the new promised date");
                    report.Expect($"{step}: promised date changed", "200", (await f.Api.Send(bob, $"/api/procurement/purchase-orders/{po}/lines/1/date", new { promisedDate = f.Day(promised), reason = "Supplier confirmed a delay" })).Label);
                    report.Rule(Basis.Spec, Spec + ": the schedule waits for the later date, which is after the due date, so the order is late");
                    report.Expect($"{step}: material state", "Expected", await MaterialState());
                    var (status, start) = await Scheduled();
                    report.Expect($"{step}: schedule status", "Late", status);
                    report.Expect($"{step}: the operation starts on or after the new promised date", "True",
                        start is { } s && DateOnly.FromDateTime(s.UtcDateTime) >= DateOnly.Parse(f.Day(promised), CultureInfo.InvariantCulture) ? "True" : "False");
                    var after = report.Kpi(await Kpis.CollectAsync(f, "after supplier delay", true, parts));
                    report.ImpactOf("Supplier delay past the due date", previous, after);
                    previous = after;
                    break;
                }

                case "EarlyArrival":
                {
                    report.Rule(Basis.Plan, "Setup: the wire arrives early and is received against the purchase order");
                    report.Expect($"{step}: received", "200", (await f.Api.Send(f.StoresUser, "/api/inventory/receipts", new { partNumber = "SIM-WIRE", quantity = wire, locationCode = SimFactory.Stores, poNumber = po, reason = "Delivery" })).Label);
                    report.Rule(Basis.Spec, Spec + ": with the wire on hand the order is ready and on time");
                    report.Expect($"{step}: material state", "Ready", await MaterialState());
                    report.Expect($"{step}: schedule status", "OnTime", (await Scheduled()).Status);
                    report.Expect($"{step}: wire released in stores", wire, (await Invariants.ReportedBalances(f.StoresUser, parts)).GetValueOrDefault(new Bucket("SIM-WIRE", SimFactory.Stores, null, "Released")));
                    var after = report.Kpi(await Kpis.CollectAsync(f, "after early arrival", true, parts));
                    report.ImpactOf("Wire arrives early", previous, after);
                    break;
                }
            }

            await Invariants.Inventory(report, step, f.StoresUser, parts);
        }

        await Invariants.AuditChain(report, "End", f.Plant.Admin);
        report.Note("Approval of an alternate material is not exercised here (allocation optimizer not implemented; approved alternates are a Phase C scenario).");
        return (report, log, await Invariants.StateHash(f.StoresUser, parts));
    }
}
