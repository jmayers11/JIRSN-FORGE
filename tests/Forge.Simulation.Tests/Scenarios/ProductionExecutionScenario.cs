using System.Globalization;
using Forge.Integration.Tests;
using Forge.Simulation.Tests.Engine;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Simulation.Tests.Scenarios;

/// <summary>
/// Scenario 18 (production execution): one ROLL-100 order is made on the shop floor from start to finished stock, while
/// other modules interfere. A trainee cannot start without a trainer; a machine that is down cannot be started; material
/// is issued up to exactly what the order needs; a report whose response is lost is retried without counting twice; the
/// host restarts mid-shift and open labor survives; two terminals report the last quantity at once and only one counts;
/// an inspection with an expired gauge is refused; the order completes and its quantity goes into finished goods.
/// Expectations come from the Phase 5 and 7 specifications (presence, training, machine state, over-reporting,
/// calibrated gauges, inspection before receipt), the inventory specification (no over-issue) and the idempotency
/// contract (ADR 0055 §1–2), computed from the plan, never from Forge.
/// </summary>
internal static class ProductionExecutionScenario
{
    public const string Name = "S18-production-execution";
    private const string Spec = "Phase 5 and 7 shop floor";
    private const string Fg = "FG";

    public static EventLog Plan(int seed)
    {
        var log = new EventLog(Name, seed);
        log.Add(TimeSpan.Zero, "Order", ("quantity", log.Between(20, 60)), ("dueInDays", log.Between(8, 14)));
        log.Add(TimeSpan.FromMinutes(10), "ClockIn");
        log.Add(TimeSpan.FromMinutes(15), "TraineeStart");
        log.Add(TimeSpan.FromMinutes(20), "MachineDown", ("machine", "SLITTER-B"));
        log.Add(TimeSpan.FromMinutes(25), "Start", ("machine", "SLITTER-A"));
        log.Add(TimeSpan.FromMinutes(30), "Issue");
        log.Add(TimeSpan.FromHours(1), "LostReport", ("good", log.Between(5, 15)), ("retries", log.Between(1, 3)));
        log.Add(TimeSpan.FromHours(2), "Restart");
        log.Add(TimeSpan.FromHours(3), "RaceFinalReport", ("terminals", 2));
        log.Add(TimeSpan.FromHours(3.5), "Inspect");
        log.Add(TimeSpan.FromHours(4), "Kit");
        log.Add(TimeSpan.FromHours(5), "Receive");
        return log;
    }

    public static async Task<(SimReport Report, EventLog Log, string Hash)> RunAsync(PostgresFixture postgres, int seed, Mutation mutation = Mutation.None)
    {
        var log = Plan(seed);
        var report = new SimReport(mutation == Mutation.None ? Name : Name + RunSummary.CanaryMarker + mutation, seed);
        await using var f = await SimFactory.BuildPlanningAsync(postgres, "s18-" + seed.ToString(CultureInfo.InvariantCulture));
        report.Evidence(f.Api);
        report.Covers(Coverage.Execution, Coverage.Training, Coverage.Maintenance, Coverage.Calibration, Coverage.Allocation, Coverage.Concurrency, Coverage.Network, Coverage.Recovery, Coverage.Warehouse);
        var kiosk = await Floor.SetUpAsync(f);
        await Floor.TraineeAsync(f);
        await f.Plant.Ok(f.Plant.Admin, $"/api/kernel/people/{f.Plant.People["sarah"]}/roles", new { role = "Inspector", reason = "Cross-trained inspector" });
        var (stores, insp, qe, tech, planner) = (f.StoresUser, f.Api.As("insp"), f.Api.As("qe"), f.Api.As("tech"), f.Planner);
        await f.Plant.Ok(qe, "/api/quality/gauges", new { code = "MIC-1", description = "Micrometer", calibrationDue = f.Day(30), certificate = "CAL-1" });
        await f.Plant.Ok(qe, "/api/quality/gauges", new { code = "MIC-OLD", description = "Micrometer past its calibration", calibrationDue = f.Day(-1) });
        await f.Plant.Ok(qe, "/api/quality/plans", new
        {
            partNumber = SimFactory.FlexPart, sequence = 10, title = "Slit width and edge",
            characteristics = new object[] { new { name = "Width", kind = "Variable", nominal = 100m, lower = 99.5m, upper = 100.5m, unit = "mm" }, new { name = "Edge free of burrs", kind = "Attribute" } },
        });
        await f.Plant.Ok(stores, "/api/inventory/locations", new { code = Fg, siteCode = "PLANT1", name = "Finished goods", kind = "Stores" });
        var parts = new[] { SimFactory.BulkPart, SimFactory.FlexPart };
        long order = 0;
        decimal quantity = 0m, reported = 0m;
        ForgeApp? restarted = null;
        var terminal = kiosk;

        async Task<Outcome> ReportAsync(HttpClient who, decimal good, bool complete, string? key = null) =>
            await f.Api.Send(who, "/api/shopfloor/report", new { badge = Floor.Sarah, orderNumber = order, sequence = 10, good, scrap = 0m, complete }, key);

        try
        {
            var sinceStart = TimeSpan.Zero;
            foreach (var ev in log.Events)
            {
                f.Clock.Advance(ev.At - sinceStart);
                sinceStart = ev.At;
                var step = $"#{ev.Index} {ev.Type}";
                switch (ev.Type)
                {
                    case "Order":
                        report.Rule(Basis.Plan, "Setup: a released ROLL-100 order");
                        quantity = ev.Number("quantity");
                        order = await f.ReleasedOrderAsync(SimFactory.FlexPart, quantity, (int)ev.Number("dueInDays"));
                        break;

                    case "ClockIn":
                        report.Rule(Basis.Spec, Spec + ": nobody starts work without evidence of presence (a clock-in or a supervisor's confirmation)");
                        report.Expect($"{step}: Alex, not yet present, cannot start", "422 clock_in_required",
                            (await f.Api.Send(kiosk, "/api/shopfloor/start", new { badge = Floor.Alex, orderNumber = order, sequence = 10, machineCode = "SLITTER-A" })).Label);
                        foreach (var badge in new[] { Floor.Sarah, Floor.Michael, Floor.Alex })
                        {
                            report.Expect($"{step}: clock-in {badge}", "200", (await f.Api.Send(kiosk, "/api/shopfloor/clock-in", new { badge })).Label);
                        }

                        break;

                    case "TraineeStart":
                        report.Rule(Basis.Spec, "Phase 5 spec §80: a person in training starts only under a present authorized trainer");
                        report.Expect($"{step}: trainee without trainer refused", "422 trainer_required",
                            (await f.Api.Send(kiosk, "/api/shopfloor/start", new { badge = Floor.Alex, orderNumber = order, sequence = 10, machineCode = "SLITTER-A" })).Label, "Training");
                        break;

                    case "MachineDown":
                    {
                        var machine = ev.Text("machine");
                        report.Expect($"{step}: breakdown recorded", "200", (await f.Api.Send(tech, $"/api/maintenance/machines/{machine}/down", new { description = "Blade drive fault", reason = "Observed" })).Label);
                        report.Rule(Basis.Spec, Spec + ": a machine that is down cannot be started");
                        var start = await f.Api.Send(kiosk, "/api/shopfloor/start", new { badge = Floor.Sarah, orderNumber = order, sequence = 10, machineCode = machine });
                        report.Expect($"{step}: start on {machine} refused", "422 refused because it is down",
                            (int)start.Status == 422 && start.Body.Contains($"{machine} is down", StringComparison.Ordinal) ? "422 refused because it is down" : start.Label, "Maintenance");
                        break;
                    }

                    case "Start":
                        report.Rule(Basis.Spec, Spec + ": a qualified, clocked-in operator starts on a machine that is up");
                        report.Expect($"{step}: started on {ev.Text("machine")}", "200",
                            (await f.Api.Send(kiosk, "/api/shopfloor/start", new { badge = Floor.Sarah, orderNumber = order, sequence = 10, machineCode = ev.Text("machine") })).Label);
                        break;

                    case "Issue":
                    {
                        var required = decimal.Round(2.5m * quantity * 1.04m, 6, MidpointRounding.AwayFromZero);
                        report.Rule(Basis.Spec, "Inventory: an order is issued at most what its BOM requires (2.5 per roll plus 4% scrap)");
                        report.Expect($"{step}: coil issued to the order", "200",
                            (await f.Api.Send(stores, "/api/inventory/issues", new { orderNumber = order, partNumber = SimFactory.BulkPart, quantity = required, locationCode = SimFactory.Stores })).Label, "Allocation");
                        report.Expect($"{step}: one more kilogram refused", "422 over_issue",
                            (await f.Api.Send(stores, "/api/inventory/issues", new { orderNumber = order, partNumber = SimFactory.BulkPart, quantity = 1m, locationCode = SimFactory.Stores })).Label, "Allocation");
                        break;
                    }

                    case "LostReport":
                    {
                        var good = ev.Number("good");
                        var key = f.Api.NextKey();
                        report.Rule(Basis.Spec, "ADR 0055 §1: a report whose response was lost, retried with the same key, counts once");
                        await SimApi.SendAndLoseResponse(kiosk, "/api/shopfloor/report", new { badge = Floor.Sarah, orderNumber = order, sequence = 10, good, scrap = 0m, complete = false }, key);
                        for (var i = 0; i < (int)ev.Number("retries"); i++)
                        {
                            report.Expect($"{step}: retry {i + 1} answered", "200", (await ReportAsync(kiosk, good, false, key)).Label, "Idempotency");
                        }

                        reported += good;
                        report.Expect($"{step}: good reported", reported, await Floor.GoodAsync(planner, order, 10), "Idempotency");
                        break;
                    }

                    case "Restart":
                    {
                        report.Rule(Basis.Spec, "Recovery: a new host on the same database carries on the same shift; open labor is not lost");
                        restarted = new ForgeApp(f.App.ConnectionString, services => services.AddSingleton<TimeProvider>(f.Clock));
                        terminal = restarted.As("kiosk");
                        report.Expect($"{step}: Sarah's labor is still open on the new host", "True", await Floor.LaborOpenAsync(f.Api, terminal, Floor.Sarah) ? "True" : "False", "Recovery");
                        report.Expect($"{step}: good reported unchanged", reported, await Floor.GoodAsync(restarted.As("planner"), order, 10), "Recovery");
                        break;
                    }

                    case "RaceFinalReport":
                    {
                        var rest = quantity - reported;
                        var terminals = (int)ev.Number("terminals");
                        var reporter = terminal;
                        report.Rule(Basis.Spec, Spec + ": an operation is never reported beyond its quantity; two terminals reporting the rest at once leave exactly one report");
                        var outcomes = await Task.WhenAll(Enumerable.Range(0, terminals).Select(_ => ReportAsync(reporter, rest, true)));
                        report.Expect($"{step}: one report wins", $"1 won, {terminals - 1} refused",
                            $"{outcomes.Count(o => o.Succeeded)} won, {outcomes.Count(o => !o.Succeeded && (int)o.Status is 409 or 422)} refused", "Concurrency");
                        reported = quantity;
                        report.Expect($"{step}: good reported equals the order quantity", quantity, await Floor.GoodAsync(planner, order, 10), "Concurrency");
                        break;
                    }

                    case "Inspect":
                    {
                        var findings = new object[] { new { number = 1, measured = 100m }, new { number = 2, accepted = true } };
                        report.Rule(Basis.Spec, "Phase 7 spec: a measured characteristic needs a gauge in calibration");
                        report.Expect($"{step}: expired gauge refused", "422 gauge_out_of_calibration",
                            (await f.Api.Send(insp, "/api/quality/inspections", new { orderNumber = order, sequence = 10, quantity = 2m, gaugeCode = "MIC-OLD", findings })).Label, "Calibration");
                        report.Expect($"{step}: inspection with a calibrated gauge", "200",
                            (await f.Api.Send(insp, "/api/quality/inspections", new { orderNumber = order, sequence = 10, quantity = 2m, gaugeCode = "MIC-1", findings })).Label, "Calibration");
                        break;
                    }

                    case "Kit":
                        report.Rule(Basis.Spec, Spec + ": completing the last operation completes the order");
                        report.Expect($"{step}: kitting started", "200", (await f.Api.Send(terminal, "/api/shopfloor/start", new { badge = Floor.Sarah, orderNumber = order, sequence = 20 })).Label);
                        report.Expect($"{step}: kitting reported complete", "200",
                            (await f.Api.Send(terminal, "/api/shopfloor/report", new { badge = Floor.Sarah, orderNumber = order, sequence = 20, good = quantity, scrap = 0m, complete = true })).Label);
                        report.Expect($"{step}: order status", "Completed", await Floor.OrderStatusAsync(planner, order));
                        break;

                    case "Receive":
                        report.Rule(Basis.Spec, "Inventory: a completed order's good quantity goes into finished goods");
                        report.Expect($"{step}: production receipt", "200",
                            (await f.Api.Send(stores, "/api/inventory/production-receipts", new { orderNumber = order, quantity, locationCode = Fg, reason = "Finished" })).Label);
                        report.Expect($"{step}: ROLL-100 in finished goods", quantity,
                            (await Invariants.ReportedBalances(stores, [SimFactory.FlexPart])).Where(b => b.Key.Location == Fg).Sum(b => b.Value), "Warehouse");
                        break;
                }

                await Invariants.Inventory(report, step, stores, parts);
            }

            await Invariants.AuditChain(report, "End", f.Plant.Admin);
        }
        finally
        {
            if (restarted is not null)
            {
                await restarted.DisposeAsync();
            }
        }

        report.Note("The restart is a second host on the same database (as after a restart or failover); the first host's clients keep working.");
        return (report, log, await Invariants.StateHash(stores, parts));
    }
}
