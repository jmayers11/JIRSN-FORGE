using System.Globalization;
using System.Text.Json;
using Forge.Integration.Tests;
using Forge.Simulation.Tests.Engine;

namespace Forge.Simulation.Tests.Scenarios;

/// <summary>
/// Scenario 10 (supplier MRB): four deliveries of the same material are inspected with partial rejections, and each
/// rejection takes a different disposition: return to vendor (then credit), rework by supplier, scrap, and use as is.
/// Expected outcomes come from ADR 0035, not from Forge's code; every check names the rule it tests.
/// Production NCR (MRB) dispositions follow in Phase B.
/// </summary>
internal static class MrbDispositionScenario
{
    public const string Name = "S10-supplier-mrb";
    private const string Adr = "ADR 0035";
    private static readonly string[] Routes = ["ReturnToVendor", "ReworkBySupplier", "Scrap"];

    /// <summary>The seeded plan: quantities, rejections and disposition order. Nothing executes here.</summary>
    public static EventLog Plan(int seed)
    {
        var log = new EventLog(Name, seed);
        var routes = Routes.OrderBy(_ => log.Random.Next()).Append("UseAsIs").ToList();
        var at = TimeSpan.Zero;
        foreach (var route in routes)
        {
            var received = log.Between(40, 240);
            var rejected = log.Between(1, Math.Max(1, received * 3 / 10));
            log.Add(at += TimeSpan.FromMinutes(20), "Receive", ("route", route), ("quantity", received));
            log.Add(at += TimeSpan.FromMinutes(40), "Inspect", ("route", route), ("accepted", received - rejected), ("rejected", rejected), ("defect", "Simulated defect for " + route));
            log.Add(at += TimeSpan.FromMinutes(30), "Disposition", ("route", route));
        }

        return log;
    }

    public static async Task<(SimReport Report, EventLog Log, string Hash)> RunAsync(PostgresFixture postgres, int seed, Mutation mutation = Mutation.None)
    {
        var log = Plan(seed);
        var report = new SimReport(mutation == Mutation.None ? Name : Name + RunSummary.CanaryMarker + mutation, seed);
        await using var f = await SimFactory.BuildAsync(postgres, "s10-" + seed.ToString(CultureInfo.InvariantCulture));
        report.Evidence(f.Api);
        report.Covers(Coverage.Receiving, Coverage.Mrb);
        var (api, stores, insp, qe, qm, pat) = (f.Api, f.StoresUser, f.Api.As("insp"), f.Api.As("qe"), f.Api.As("qm"), f.Api.As("pat"));
        var parts = new[] { SimFactory.BulkPart };

        // The independent oracle: what Released and Hold must be, computed only from the plan and ADR 0035.
        var expectedReleased = SimFactory.OpeningCoil;
        var expectedHold = 0m;
        var offByOneApplied = false;
        var seen = new HashSet<long>();
        var receipts = new Dictionary<string, long>(StringComparer.Ordinal);
        var rejections = new Dictionary<string, long>(StringComparer.Ordinal);

        async Task<decimal> Stock(string status) =>
            (await Invariants.ReportedBalances(stores, parts)).GetValueOrDefault(new Bucket(SimFactory.BulkPart, SimFactory.Stores, null, status));

        async Task CheckStock(string step)
        {
            report.Expect($"{step}: Released", expectedReleased, await Stock("Released"));
            report.Expect($"{step}: Hold", expectedHold, await Stock("Hold"));
            report.Expect($"{step}: Inspection", 0m, await Stock("Inspection"));
        }

        report.Rule(Basis.Plan, "Reference plant opening stock (1,000,000 COIL-AL released at STORES)");
        await CheckStock("Baseline");
        var sinceStart = TimeSpan.Zero;
        foreach (var e in log.Events)
        {
            f.Clock.Advance(e.At - sinceStart);
            sinceStart = e.At;
            var route = e.Text("route");
            var step = $"#{e.Index} {e.Type} {route}";
            switch (e.Type)
            {
                case "Receive":
                {
                    report.Rule(Basis.Spec, Adr + " §3: a receipt can go into inspection");
                    var outcome = await api.Send(stores, "/api/inventory/receipts", new { partNumber = SimFactory.BulkPart, quantity = e.Number("quantity"), locationCode = SimFactory.Stores, inspect = true, reason = "Simulated delivery " + route });
                    report.Expect($"{step}: receipt into inspection accepted", "200", outcome.Label);
                    receipts[route] = outcome.Id;
                    break;
                }

                case "Inspect":
                {
                    // ADR 0035 §3 refuses the receiver as inspector, so an independent inspector inspects.
                    report.Rule(Basis.Spec, Adr + " §3: a different person inspects; no PO means FAI NotApplicable, which allows acceptance");
                    var outcome = await api.Send(insp, $"/api/inventory/receiving/{receipts[route]}/inspections",
                        new { accepted = e.Number("accepted"), rejected = e.Number("rejected"), defect = e.Text("defect"), reason = "Simulated inspection" });
                    report.Expect($"{step}: inspection recorded", "200", outcome.Label);
                    expectedReleased += e.Number("accepted");
                    expectedHold += e.Number("rejected");
                    if (mutation == Mutation.OracleOffByOne && !offByOneApplied)
                    {
                        expectedReleased += 1m;
                        offByOneApplied = true;
                    }

                    report.Rule(Basis.Spec, Adr + " §3: accepted quantity is released and rejected quantity goes on hold");
                    await CheckStock(step);
                    report.Rule(Basis.Spec, "ADR 0055 §4: an inspection with a rejected quantity records exactly one supplier rejection");
                    var open = (await SimApi.Get(qe, "/api/inventory/rejections?open=true")).EnumerateArray()
                        .Select(r => r.GetProperty("rejectionId").GetInt64()).Where(id => !seen.Contains(id)).ToList();
                    report.Expect($"{step}: exactly one new open rejection", "1", open.Count.ToString(CultureInfo.InvariantCulture));
                    if (open.Count == 1)
                    {
                        rejections[route] = open[0];
                        seen.Add(open[0]);
                    }

                    break;
                }

                case "Disposition" when rejections.ContainsKey(route):
                    await DispositionAsync(route, step);
                    break;

                default:
                    report.Rule(Basis.Plan, "Every planned event must be applied");
                    report.Expect($"{step}: event applied", "applied", "skipped: no rejection to disposition");
                    break;
            }

            await Invariants.Inventory(report, step, stores, parts);
        }

        async Task DispositionAsync(string route, string step)
        {
            var id = rejections[route];
            var r = log.Events.Single(x => x.Type == "Inspect" && x.Text("route") == route).Number("rejected");

            report.Rule(Basis.Spec, Adr + " §4: dispositions are recorded by quality disposition authority");
            report.Expect($"{step}: material handler cannot disposition", "403",
                Status(await api.Send(stores, $"/api/inventory/rejections/{id}/disposition", new { disposition = route, reason = "Not my decision" })), "Authorization");
            var decided = await api.Send(qe, $"/api/inventory/rejections/{id}/disposition", new { disposition = route, reason = "Simulated MRB decision: " + route });
            report.Expect($"{step}: quality engineer dispositions", "200", decided.Label);

            report.Rule(Basis.Spec, "ADR 0055 §3: each rejection is decided once; a decided rejection refuses a second disposition");
            report.Expect($"{step}: a second disposition is refused", "422",
                Status(await api.Send(qe, $"/api/inventory/rejections/{id}/disposition", new { disposition = "Scrap", reason = "Changed my mind" })));

            switch (route)
            {
                case "Scrap":
                    report.Rule(Basis.Spec, Adr + " §4: scrap posts a scrap transaction from hold");
                    expectedHold -= r;
                    await CheckStock(step + " (scrapped)");
                    break;

                case "ReturnToVendor" or "ReworkBySupplier":
                {
                    report.Rule(Basis.Spec, Adr + " §4: return to vendor and rework by supplier open an RTV; §5 stock stays on hold until it ships");
                    await CheckStock(step + " (RTV open, still on hold)");
                    var rtv = (await SimApi.Get(stores, "/api/inventory/rtvs?open=true")).EnumerateArray()
                        .SingleOrDefault(x => x.GetProperty("rejectionId").GetInt64() == id);
                    var exists = rtv.ValueKind == JsonValueKind.Object;
                    report.Expect($"{step}: an RTV is open for the rejection", "Open", exists ? rtv.GetProperty("status").GetString()! : "none");
                    if (!exists)
                    {
                        break;
                    }

                    var rtvId = rtv.GetProperty("rtvId").GetInt64();
                    report.Rule(Basis.Spec, Adr + " §4: rework by the supplier opens an RTV marked for rework");
                    report.Expect($"{step}: RTV marked for rework only when the supplier reworks", route == "ReworkBySupplier" ? "True" : "False",
                        rtv.GetProperty("forRework").GetBoolean() ? "True" : "False");
                    report.Rule(Basis.Spec, "ADR 0055 §5: a return to vendor carries the quantity its disposition decided (here the whole rejection)");
                    report.Expect($"{step}: RTV quantity equals the rejected quantity", r, rtv.GetProperty("quantity").GetDecimal());
                    report.Rule(Basis.Spec, Adr + " §5: shipping posts a ReturnToVendor transaction out of hold");
                    var shipped = await api.Send(stores, $"/api/inventory/rtvs/{rtvId}/ship", new { carrier = "Simulated carrier", reason = "Return shipment" });
                    report.Expect($"{step}: RTV ships", "200", shipped.Label);
                    expectedHold -= r;
                    await CheckStock(step + " (RTV shipped)");
                    if (route == "ReturnToVendor")
                    {
                        report.Rule(Basis.Spec, Adr + " §5: a credit closes the RTV; it needs purchasing approval and a reason");
                        report.Expect($"{step}: material handler cannot record a supplier credit", "403",
                            Status(await api.Send(stores, $"/api/inventory/rtvs/{rtvId}/credit", new { reason = "Credit note" })), "Authorization");
                        var credited = await api.Send(pat, $"/api/inventory/rtvs/{rtvId}/credit", new { reason = "Supplier credit note CN-SIM" });
                        report.Expect($"{step}: purchasing manager records the credit", "200", credited.Label);
                        var after = (await SimApi.Get(stores, "/api/inventory/rtvs")).EnumerateArray().Single(x => x.GetProperty("rtvId").GetInt64() == rtvId);
                        report.Expect($"{step}: RTV closed as credited", "Credited", after.GetProperty("status").GetString()!);
                    }

                    break;
                }

                case "UseAsIs":
                {
                    report.Rule(Basis.Spec, Adr + " §4: use as is waits for a different person with quality approval, and only then releases");
                    await CheckStock(step + " (use as is awaiting approval, still on hold)");
                    report.Expect($"{step}: the deciding engineer cannot approve their own use-as-is", "403",
                        Status(await api.Send(qe, $"/api/inventory/rejections/{id}/approve", new { reason = "Self approval" })), "Authorization");
                    var approver = mutation == Mutation.SelfApproveUseAsIs ? qe : qm;
                    var approved = await api.Send(approver, $"/api/inventory/rejections/{id}/approve", new { reason = "Second person agrees: cosmetic" });
                    report.Expect($"{step}: quality manager approves", "200", approved.Label);
                    expectedHold -= r;
                    expectedReleased += r;
                    await CheckStock(step + " (released after approval)");
                    break;
                }
            }
        }

        report.Rule(Basis.Spec, "ADR 0055 §6: a rejection is open while pending or awaiting use-as-is approval; all four are closed");
        report.Expect("End: no open rejections", "0",
            (await SimApi.Get(qe, "/api/inventory/rejections?open=true")).EnumerateArray().Count().ToString(CultureInfo.InvariantCulture));
        await Invariants.AuditChain(report, "End", f.Plant.Admin);
        var hash = await Invariants.StateHash(stores, parts);
        report.Note("Supplier dispositions only. Production NCR (MRB) dispositions and customer concessions are Phase B scenarios.");
        report.Note("Expected quantities are computed from the seeded plan and ADR 0035, independently of Forge's code.");
        if (mutation != Mutation.None)
        {
            report.Note("Negative control: " + mutation + ". This run is expected to fail.");
        }

        return (report, log, hash);
    }

    private static string Status(Outcome o) => ((int)o.Status).ToString(CultureInfo.InvariantCulture);
}
