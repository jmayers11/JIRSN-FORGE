using System.Globalization;
using System.Text.Json;
using Forge.Integration.Tests;
using Forge.Simulation.Tests.Engine;

namespace Forge.Simulation.Tests.Scenarios;

/// <summary>
/// Scenario 25 (corrective action): the same edge burr turns up on two orders. Each NCR holds only its own order. One lot
/// is scrapped; the other is used as is under the customer's concession, approved by a second person. A corrective
/// action is opened against the first NCR: an inspector cannot open it, it needs an implementation note, and whoever
/// implemented it cannot verify it, even with verification authority. Once verified it leaves the open list. The NCR
/// report shows both NCRs closed with their decisions. Expectations come from the Phase 7a quality specification (NCRs
/// hold their order until dispositioned; use as is needs a second person; CAPA is verified by someone other than the
/// implementer) and Phase 8 reports, computed from the plan, never from Forge.
/// </summary>
internal static class CorrectiveActionScenario
{
    public const string Name = "S25-corrective-action";
    private const string Spec = "Phase 7a quality";

    public static EventLog Plan(int seed)
    {
        var log = new EventLog(Name, seed);
        log.Add(TimeSpan.Zero, "Orders", ("a", log.Between(5, 20)), ("b", log.Between(5, 20)));
        log.Add(TimeSpan.FromMinutes(10), "Ncr", ("which", "a"), ("quantity", log.Between(1, 3)));
        log.Add(TimeSpan.FromMinutes(20), "Ncr", ("which", "b"), ("quantity", log.Between(1, 3)));
        log.Add(TimeSpan.FromHours(1), "Scrap", ("which", "b"));
        log.Add(TimeSpan.FromHours(2), "UseAsIs", ("which", "a"));
        log.Add(TimeSpan.FromHours(3), "OpenCapa", ("which", "a"), ("dueInDays", log.Between(7, 21)));
        log.Add(TimeSpan.FromDays(3), "Implement");
        log.Add(TimeSpan.FromDays(10), "Verify");
        log.Add(TimeSpan.FromDays(10.5), "CloseAndReport");
        return log;
    }

    public static async Task<(SimReport Report, EventLog Log, string Hash)> RunAsync(PostgresFixture postgres, int seed, Mutation mutation = Mutation.None)
    {
        var log = Plan(seed);
        var report = new SimReport(mutation == Mutation.None ? Name : Name + RunSummary.CanaryMarker + mutation, seed);
        await using var f = await SimFactory.BuildPlanningAsync(postgres, "s25-" + seed.ToString(CultureInfo.InvariantCulture));
        report.Evidence(f.Api);
        report.Covers(Coverage.Capa, Coverage.Mrb, Coverage.Execution, Coverage.Concessions, Coverage.Reporting);
        await f.Plant.Person("lead", "QualityEngineer", "QualityManager");
        var (insp, qe, qm, lead, planner) = (f.Api.As("insp"), f.Api.As("qe"), f.Api.As("qm"), f.Api.As("lead"), f.Planner);
        var parts = new[] { SimFactory.BulkPart };
        var orders = new Dictionary<string, long>(StringComparer.Ordinal);
        var ncrs = new Dictionary<string, long>(StringComparer.Ordinal);
        long capa = 0;

        async Task<string> NcrStatus(long id) =>
            (await SimApi.Get(qe, "/api/quality/ncrs?includeClosed=true")).EnumerateArray().Where(n => n.GetProperty("ncrId").GetInt64() == id)
                .Select(n => n.GetProperty("status").GetString() ?? "?").FirstOrDefault() ?? "missing";

        async Task<bool> HeldBy(long order, long ncr) =>
            (await SimApi.Get(planner, $"/api/orders/{order}/readiness")).GetRawText().Contains("NCR " + ncr.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        async Task<string?> CapaStatus(bool includeVerified) =>
            (await SimApi.Get(qm, $"/api/quality/capas?includeVerified={(includeVerified ? "true" : "false")}")).EnumerateArray()
                .Where(c => c.GetProperty("capaId").GetInt64() == capa).Select(c => c.GetProperty("status").GetString()).FirstOrDefault();

        async Task ExpectHolds(string step)
        {
            report.Rule(Basis.Spec, Spec + ": an open NCR holds its own order, and only its own");
            foreach (var (which, order) in orders)
            {
                var raised = ncrs.TryGetValue(which, out var ncr);
                var open = raised && await NcrStatus(ncr) is "Open" or "AwaitingApproval";
                report.Expect($"{step}: order {which} held by its NCR", open ? "True" : "False", raised && await HeldBy(order, ncr) ? "True" : "False", "Execution");
            }
        }

        var sinceStart = TimeSpan.Zero;
        foreach (var ev in log.Events)
        {
            f.Clock.Advance(ev.At - sinceStart);
            sinceStart = ev.At;
            var which = ev.Data.ContainsKey("which") ? ev.Text("which") : string.Empty;
            var step = $"#{ev.Index} {ev.Type} {which}".TrimEnd();
            switch (ev.Type)
            {
                case "Orders":
                    report.Rule(Basis.Plan, "Setup: two released orders");
                    orders["a"] = await f.ReleasedOrderAsync(SimFactory.FlexPart, ev.Number("a"), 10);
                    orders["b"] = await f.ReleasedOrderAsync(SimFactory.FlexPart, ev.Number("b"), 12);
                    break;

                case "Ncr":
                {
                    report.Rule(Basis.Plan, "Setup: an inspector raises an NCR for an edge burr");
                    var raised = await f.Api.Send(insp, "/api/quality/ncrs", new { partNumber = SimFactory.FlexPart, orderNumber = orders[which], sequence = 10, quantity = ev.Number("quantity"), defect = "Edge burr", description = "Burr on slit edge", reason = "Found at inspection" });
                    report.Expect($"{step}: NCR raised", "200", raised.Label, "Mrb");
                    ncrs[which] = raised.Succeeded ? raised.Id : 0;
                    await ExpectHolds(step);
                    break;
                }

                case "Scrap":
                    report.Rule(Basis.Spec, Spec + ": scrap is decided at once by the disposition authority");
                    report.Expect($"{step}: scrapped", "200", (await f.Api.Send(qe, $"/api/quality/ncrs/{ncrs[which]}/disposition", new { disposition = "Scrap", note = "Burr too deep", reason = "Not usable" })).Label, "Mrb");
                    report.Expect($"{step}: NCR status", "Dispositioned", await NcrStatus(ncrs[which]), "Mrb");
                    await ExpectHolds(step);
                    break;

                case "UseAsIs":
                    report.Rule(Basis.Spec, Spec + ": use as is under the customer's concession waits for a second person");
                    report.Expect($"{step}: proposed", "200", (await f.Api.Send(qe, $"/api/quality/ncrs/{ncrs[which]}/disposition", new { disposition = "UseAsIs", note = "Customer accepts light burr", reason = "Customer concession CC-SIM-25" })).Label, "Concessions");
                    await ExpectHolds(step);
                    report.Expect($"{step}: approved", "200", (await f.Api.Send(qm, $"/api/quality/ncrs/{ncrs[which]}/approve", new { reason = "Customer concession CC-SIM-25" })).Label, "Concessions");
                    report.Expect($"{step}: NCR status", "Dispositioned", await NcrStatus(ncrs[which]), "Concessions");
                    await ExpectHolds(step);
                    break;

                case "OpenCapa":
                {
                    var body = new { ncrId = ncrs[which], title = "Edge burr on slit rolls", rootCause = "Knife wear beyond change interval", action = "Shorten knife change interval; add burr check", dueDate = f.Day((int)ev.Number("dueInDays")), reason = "Repeat defect" };
                    report.Rule(Basis.Spec, Spec + ": a corrective action is opened by the disposition authority");
                    report.Expect($"{step}: an inspector cannot open it", "403", ((int)(await f.Api.Send(insp, "/api/quality/capas", body)).Status).ToString(CultureInfo.InvariantCulture), "Authorization");
                    var opened = await f.Api.Send(qe, "/api/quality/capas", body);
                    report.Expect($"{step}: opened", "200", opened.Label, "Capa");
                    capa = opened.Succeeded ? opened.Id : 0;
                    report.Expect($"{step}: on the open list", "Open", await CapaStatus(false) ?? "missing", "Capa");
                    break;
                }

                case "Implement":
                    report.Rule(Basis.Spec, Spec + ": implementing needs a note");
                    report.Expect($"{step}: without a note refused", "422", ((int)(await f.Api.Send(lead, $"/api/quality/capas/{capa}/implement", new { reason = "Done" })).Status).ToString(CultureInfo.InvariantCulture), "Capa");
                    report.Expect($"{step}: implemented", "200", (await f.Api.Send(lead, $"/api/quality/capas/{capa}/implement", new { note = "Interval shortened to 40 hours; check added to setup sheet", reason = "Done" })).Label, "Capa");
                    report.Expect($"{step}: status", "Implemented", await CapaStatus(false) ?? "missing", "Capa");
                    break;

                case "Verify":
                    report.Rule(Basis.Spec, Spec + ": whoever implemented the action cannot verify it, even with verification authority");
                    report.Expect($"{step}: implementer cannot verify", "422 separation_of_duties", (await f.Api.Send(lead, $"/api/quality/capas/{capa}/verify", new { reason = "Mine" })).Label, "Authorization");
                    report.Expect($"{step}: quality manager verifies", "200", (await f.Api.Send(qm, $"/api/quality/capas/{capa}/verify", new { reason = "Ten setups checked, no burrs" })).Label, "Capa");
                    report.Rule(Basis.Spec, Spec + ": a verified action leaves the open list and stays on record");
                    report.Expect($"{step}: off the open list", "absent", await CapaStatus(false) ?? "absent", "Capa");
                    report.Expect($"{step}: on record as verified", "Verified", await CapaStatus(true) ?? "missing", "Capa");
                    break;

                case "CloseAndReport":
                {
                    report.Rule(Basis.Spec, Spec + ": decided NCRs are closed");
                    foreach (var (w, id) in ncrs)
                    {
                        report.Expect($"{step}: NCR {w} closed", "200", (await f.Api.Send(qe, $"/api/quality/ncrs/{id}/close", new { note = "Decision carried out", reason = "Complete" })).Label, "Mrb");
                    }

                    await ExpectHolds(step);
                    report.Rule(Basis.Spec, "Phase 8 reports: the NCR report shows each NCR with its decision and status");
                    var summary = await SimApi.Get(qe, $"/api/reports/ncr-summary?from={f.Day(-14)}&to={f.Day(1)}");
                    var columns = summary.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("key").GetString()).ToList();
                    string Cell(JsonElement row, string key) => row.GetProperty("cells")[columns.IndexOf(key)] is var c && c.ValueKind == JsonValueKind.String ? c.GetString() ?? string.Empty : string.Empty;
                    var rows = summary.GetProperty("rows").EnumerateArray().ToList();
                    foreach (var (w, decision) in new[] { ("a", "UseAsIs"), ("b", "Scrap") })
                    {
                        var id = ncrs[w].ToString(CultureInfo.InvariantCulture);
                        var row = rows.Where(r => Cell(r, "ncr").Contains(id, StringComparison.Ordinal)).ToList();
                        report.Expect($"{step}: NCR {w} in the report", $"Closed {decision}", row.Count == 1 ? $"{Cell(row[0], "status")} {Cell(row[0], "disposition")}" : $"{row.Count} rows", "Reporting");
                    }

                    break;
                }
            }

            await Invariants.Inventory(report, step, f.StoresUser, parts);
        }

        await Invariants.AuditChain(report, "End", f.Plant.Admin);
        return (report, log, await Invariants.StateHash(f.StoresUser, parts));
    }
}
