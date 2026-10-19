using System.Globalization;
using Forge.Integration.Tests;
using Forge.Simulation.Tests.Engine;

namespace Forge.Simulation.Tests.Scenarios;

/// <summary>
/// Scenario 27 (customer concession): a roll is slit wide. The inspection, made with a calibrated gauge, fails and raises
/// an NCR that holds the order's next operation, but not another order. The customer accepts the width: quality proposes
/// use as is under the customer's concession, and a second person approves it. The order is re-inspected, finished,
/// received into stock and shipped against the customer's order; the NCR is closed as shipped under concession, and a
/// corrective action is implemented by one person and verified by another. Expectations come from the Phase 7a quality
/// specification (NCRs hold the order until dispositioned; use as is needs a second person; receipts wait for final
/// inspection; CAPA is verified by someone else), computed from the plan, never from Forge.
/// </summary>
internal static class CustomerConcessionScenario
{
    public const string Name = "S27-customer-concession";
    private const string Spec = "Phase 7a quality";
    private const string Fg = "FG";
    private const string Concession = "CC-SIM-14";

    public static EventLog Plan(int seed)
    {
        var log = new EventLog(Name, seed);
        log.Add(TimeSpan.Zero, "Orders", ("quantity", log.Between(5, 20)), ("other", log.Between(5, 20)));
        log.Add(TimeSpan.FromMinutes(10), "Slit");
        log.Add(TimeSpan.FromHours(1), "InspectWide", ("measured", 100.6m + log.Between(1, 9) / 10m));
        log.Add(TimeSpan.FromHours(1.5), "Held");
        log.Add(TimeSpan.FromHours(2), "Concession");
        log.Add(TimeSpan.FromHours(3), "Finish");
        log.Add(TimeSpan.FromHours(4), "Ship");
        log.Add(TimeSpan.FromHours(5), "Close");
        log.Add(TimeSpan.FromDays(1), "Capa");
        return log;
    }

    public static async Task<(SimReport Report, EventLog Log, string Hash)> RunAsync(PostgresFixture postgres, int seed, Mutation mutation = Mutation.None)
    {
        var log = Plan(seed);
        var report = new SimReport(mutation == Mutation.None ? Name : Name + RunSummary.CanaryMarker + mutation, seed);
        await using var f = await SimFactory.BuildPlanningAsync(postgres, "s27-" + seed.ToString(CultureInfo.InvariantCulture));
        report.Evidence(f.Api);
        report.Covers(Coverage.Concessions, Coverage.Mrb, Coverage.Holds, Coverage.Calibration, Coverage.Execution, Coverage.Shipping, Coverage.Capa);
        var kiosk = await Floor.SetUpAsync(f);
        var (stores, insp, qe, qm, ship, planner) = (f.StoresUser, f.Api.As("insp"), f.Api.As("qe"), f.Api.As("qm"), f.Api.As("ship"), f.Planner);
        await f.Plant.Ok(qe, "/api/quality/gauges", new { code = "MIC-1", description = "Micrometer", calibrationDue = f.Day(30), certificate = "CAL-1" });
        await f.Plant.Ok(qe, "/api/quality/plans", new
        {
            partNumber = SimFactory.FlexPart, sequence = 10, title = "Slit width and edge",
            characteristics = new object[] { new { name = "Width", kind = "Variable", nominal = 100m, lower = 99.5m, upper = 100.5m, unit = "mm" }, new { name = "Edge free of burrs", kind = "Attribute" } },
        });
        await f.Plant.Ok(qe, "/api/quality/plans", new { partNumber = SimFactory.FlexPart, sequence = 20, title = "Final count and label", characteristics = new[] { new { name = "Label matches order", kind = "Attribute" } } });
        await f.Plant.Ok(stores, "/api/inventory/locations", new { code = Fg, siteCode = "PLANT1", name = "Finished goods", kind = "Stores" });
        var parts = new[] { SimFactory.BulkPart, SimFactory.FlexPart };
        long order = 0, other = 0, ncr = 0;
        decimal quantity = 0m;

        async Task<string> NcrStatus() =>
            (await SimApi.Get(qe, "/api/quality/ncrs?includeClosed=true")).EnumerateArray().Where(n => n.GetProperty("ncrId").GetInt64() == ncr)
                .Select(n => n.GetProperty("status").GetString() ?? "?").FirstOrDefault() ?? "missing";

        static string Held(Outcome o, long ncrId) =>
            (int)o.Status == 422 && o.Body.Contains("NCR " + ncrId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) ? "422 held by the NCR" : o.Label;

        var sinceStart = TimeSpan.Zero;
        foreach (var ev in log.Events)
        {
            f.Clock.Advance(ev.At - sinceStart);
            sinceStart = ev.At;
            var step = $"#{ev.Index} {ev.Type}";
            switch (ev.Type)
            {
                case "Orders":
                    report.Rule(Basis.Plan, "Setup: the customer's order and another order");
                    quantity = ev.Number("quantity");
                    order = await f.ReleasedOrderAsync(SimFactory.FlexPart, quantity, 10);
                    other = await f.ReleasedOrderAsync(SimFactory.FlexPart, ev.Number("other"), 12);
                    break;

                case "Slit":
                    report.Rule(Basis.Plan, "Setup: Sarah slits the customer's order");
                    report.Expect($"{step}: clock-in", "200 200", $"{(await f.Api.Send(kiosk, "/api/shopfloor/clock-in", new { badge = Floor.Sarah })).Label} {(await f.Api.Send(kiosk, "/api/shopfloor/clock-in", new { badge = Floor.Michael })).Label}");
                    report.Expect($"{step}: started", "200", (await f.Api.Send(kiosk, "/api/shopfloor/start", new { badge = Floor.Sarah, orderNumber = order, sequence = 10, machineCode = "SLITTER-A" })).Label);
                    report.Expect($"{step}: reported", "200", (await f.Api.Send(kiosk, "/api/shopfloor/report", new { badge = Floor.Sarah, orderNumber = order, sequence = 10, good = quantity, scrap = 0m, complete = true })).Label);
                    break;

                case "InspectWide":
                {
                    var measured = ev.Number("measured");
                    report.Rule(Basis.Spec, Spec + ": a measurement outside its limits fails the inspection and raises an NCR for the rejected quantity");
                    var seen = (await SimApi.Get(qe, "/api/quality/ncrs?includeClosed=true")).EnumerateArray().Select(n => n.GetProperty("ncrId").GetInt64()).ToHashSet();
                    report.Expect($"{step}: inspection recorded", "200", (await f.Api.Send(insp, "/api/quality/inspections", new
                    {
                        orderNumber = order, sequence = 10, quantity = 2m, gaugeCode = "MIC-1", rejectedQuantity = 1m, notes = "Second roll wide",
                        findings = new object[] { new { number = 1, measured }, new { number = 2, accepted = true } },
                    })).Label, "Calibration");
                    var raised = (await SimApi.Get(qe, "/api/quality/ncrs?includeClosed=true")).EnumerateArray().Where(n => !seen.Contains(n.GetProperty("ncrId").GetInt64())).ToList();
                    report.Expect($"{step}: one NCR raised", "1", raised.Count.ToString(CultureInfo.InvariantCulture), "Mrb");
                    ncr = raised.Select(n => n.GetProperty("ncrId").GetInt64()).FirstOrDefault();
                    report.Expect($"{step}: NCR quantity", 1m, raised.Select(n => n.GetProperty("quantity").GetDecimal()).FirstOrDefault(), "Mrb");
                    break;
                }

                case "Held":
                    report.Rule(Basis.Spec, Spec + ": the NCR holds this order's next operation, and only this order");
                    report.Expect($"{step}: kitting the customer's order refused", "422 held by the NCR",
                        Held(await f.Api.Send(kiosk, "/api/shopfloor/start", new { badge = Floor.Sarah, orderNumber = order, sequence = 20 }), ncr), "Holds");
                    report.Expect($"{step}: the other order starts", "200",
                        (await f.Api.Send(kiosk, "/api/shopfloor/start", new { badge = Floor.Michael, orderNumber = other, sequence = 10, machineCode = "SLITTER-B" })).Label, "Holds");
                    break;

                case "Concession":
                    report.Rule(Basis.Spec, Spec + ": use as is under a customer concession needs a reason, the disposition authority, and a different person to approve it");
                    report.Expect($"{step}: an inspector cannot disposition", "403",
                        ((int)(await f.Api.Send(insp, $"/api/quality/ncrs/{ncr}/disposition", new { disposition = "UseAsIs", note = "x", reason = "x" })).Status).ToString(CultureInfo.InvariantCulture), "Authorization");
                    report.Expect($"{step}: use as is proposed", "200",
                        (await f.Api.Send(qe, $"/api/quality/ncrs/{ncr}/disposition", new { disposition = "UseAsIs", note = "Customer drawing allows +1.0 mm", reason = "Customer concession " + Concession })).Label);
                    report.Expect($"{step}: NCR awaits approval", "AwaitingApproval", await NcrStatus(), "Mrb");
                    report.Expect($"{step}: the proposer cannot approve", "403",
                        ((int)(await f.Api.Send(qe, $"/api/quality/ncrs/{ncr}/approve", new { reason = "Mine" })).Status).ToString(CultureInfo.InvariantCulture), "Authorization");
                    report.Expect($"{step}: quality manager approves", "200", (await f.Api.Send(qm, $"/api/quality/ncrs/{ncr}/approve", new { reason = "Customer concession " + Concession })).Label);
                    report.Expect($"{step}: NCR dispositioned", "Dispositioned", await NcrStatus(), "Mrb");
                    break;

                case "Finish":
                {
                    var findings = new object[] { new { number = 1, measured = 100m }, new { number = 2, accepted = true } };
                    report.Rule(Basis.Spec, Spec + ": after the decision and a passing re-inspection the order finishes; finished goods wait for the final inspection");
                    report.Expect($"{step}: re-inspection passes", "200", (await f.Api.Send(insp, "/api/quality/inspections", new { orderNumber = order, sequence = 10, quantity = 2m, gaugeCode = "MIC-1", findings })).Label);
                    report.Expect($"{step}: kitting started", "200", (await f.Api.Send(kiosk, "/api/shopfloor/start", new { badge = Floor.Sarah, orderNumber = order, sequence = 20 })).Label);
                    report.Expect($"{step}: kitting reported", "200", (await f.Api.Send(kiosk, "/api/shopfloor/report", new { badge = Floor.Sarah, orderNumber = order, sequence = 20, good = quantity, scrap = 0m, complete = true })).Label);
                    report.Expect($"{step}: receipt before final inspection refused", "422 inspection_required",
                        (await f.Api.Send(stores, "/api/inventory/production-receipts", new { orderNumber = order, quantity, locationCode = Fg, reason = "Finished" })).Label);
                    report.Expect($"{step}: final inspection", "200", (await f.Api.Send(insp, "/api/quality/inspections", new { orderNumber = order, sequence = 20, quantity, findings = new[] { new { number = 1, accepted = true } } })).Label);
                    report.Expect($"{step}: received into finished goods", "200", (await f.Api.Send(stores, "/api/inventory/production-receipts", new { orderNumber = order, quantity, locationCode = Fg, reason = "Finished" })).Label);
                    break;
                }

                case "Ship":
                {
                    report.Rule(Basis.Spec, "Phase 7 shipping: the concession rolls ship against the customer's order; finished goods are then empty");
                    var demand = await f.Api.Send(planner, "/api/planning/demand", new { partNumber = SimFactory.FlexPart, quantity, needDate = f.Day(5), priority = 2, kind = "CustomerOrder", customer = "Concession customer", reference = Concession, reason = "Customer order" });
                    var created = await f.Api.Send(stores, "/api/inventory/shipments", new { demandId = demand.Id, shipTo = "Customer dock", lines = new[] { new { partNumber = SimFactory.FlexPart, quantity, locationCode = Fg } }, reason = "Order" });
                    var packed = await f.Api.Send(stores, $"/api/inventory/shipments/{created.Id}/pack", new { reason = "Packed" });
                    var shipped = await f.Api.Send(ship, $"/api/inventory/shipments/{created.Id}/ship", new { carrier = "UPS", reason = "Ship" });
                    report.Expect($"{step}: demand, shipment, pack, ship", "200 200 200 200", $"{demand.Label} {created.Label} {packed.Label} {shipped.Label}", "Shipping");
                    report.Expect($"{step}: finished goods on hand", 0m,
                        (await Invariants.ReportedBalances(stores, [SimFactory.FlexPart])).Where(b => b.Key.Location == Fg).Sum(b => b.Value), "Shipping");
                    break;
                }

                case "Close":
                    report.Rule(Basis.Spec, Spec + ": a decided NCR is closed with a note");
                    report.Expect($"{step}: NCR closed", "200", (await f.Api.Send(qe, $"/api/quality/ncrs/{ncr}/close", new { note = "Shipped under concession " + Concession, reason = "Complete" })).Label);
                    report.Expect($"{step}: NCR status", "Closed", await NcrStatus(), "Mrb");
                    break;

                case "Capa":
                {
                    report.Rule(Basis.Spec, Spec + ": a corrective action is implemented by one person and verified by another with authority");
                    var capa = await f.Api.Send(qe, "/api/quality/capas", new { ncrId = ncr, title = "Slit width drift", rootCause = "Knife spacer worn", action = "Replace spacers; add a spacer check", dueDate = f.Day(14), reason = "Recurrence prevention" });
                    report.Expect($"{step}: CAPA opened", "200", capa.Label, "Capa");
                    report.Expect($"{step}: implemented", "200", (await f.Api.Send(qe, $"/api/quality/capas/{capa.Id}/implement", new { note = "Spacers replaced", reason = "Done" })).Label, "Capa");
                    report.Expect($"{step}: the implementer cannot verify", "403",
                        ((int)(await f.Api.Send(qe, $"/api/quality/capas/{capa.Id}/verify", new { reason = "Mine" })).Status).ToString(CultureInfo.InvariantCulture), "Authorization");
                    report.Expect($"{step}: quality manager verifies", "200", (await f.Api.Send(qm, $"/api/quality/capas/{capa.Id}/verify", new { reason = "Three setups checked" })).Label, "Capa");
                    var status = (await SimApi.Get(qm, "/api/quality/capas?includeVerified=true")).EnumerateArray().Where(c => c.GetProperty("capaId").GetInt64() == capa.Id).Select(c => c.GetProperty("status").GetString()).FirstOrDefault();
                    report.Expect($"{step}: CAPA status", "Verified", status ?? "missing", "Capa");
                    break;
                }
            }

            await Invariants.Inventory(report, step, stores, parts);
        }

        await Invariants.AuditChain(report, "End", f.Plant.Admin);
        return (report, log, await Invariants.StateHash(stores, parts));
    }
}
