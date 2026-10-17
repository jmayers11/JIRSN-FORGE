using System.Globalization;
using Forge.Integration.Tests;
using Forge.Simulation.Tests.Engine;

namespace Forge.Simulation.Tests.Scenarios;

/// <summary>
/// Scenario 10 (supplier MRB): four deliveries of the same material are inspected with partial rejections, and each
/// rejection takes a different disposition: return to vendor (then credit), rework by supplier, scrap, and use as is.
/// Expected outcomes come from the specification (ADR 0035 and README V3.11), not from Forge's code:
/// accepted quantity is released and rejected quantity goes on hold; return to vendor and rework by supplier open an
/// RTV that ships out of hold; scrap posts from hold; use as is releases only after a different person with quality
/// approval approves; credit needs purchasing approval; nobody without quality disposition authority can disposition.
/// Production NCR (MRB) dispositions follow in Phase B.
/// </summary>
internal static class MrbDispositionScenario
{
    public const string Name = "S10-supplier-mrb";
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

    public static async Task<(SimReport Report, EventLog Log, string Hash)> RunAsync(PostgresFixture postgres, int seed)
    {
        var log = Plan(seed);
        var report = new SimReport(Name, seed);
        await using var f = await SimFactory.BuildAsync(postgres, "s10-" + seed.ToString(CultureInfo.InvariantCulture));
        var (api, stores, insp, qe, qm, pat) = (f.Api, f.StoresUser, f.Api.As("insp"), f.Api.As("qe"), f.Api.As("qm"), f.Api.As("pat"));
        var parts = new[] { SimFactory.BulkPart };

        // The independent oracle: what Released and Hold must be, computed only from the plan and the specification.
        var expectedReleased = SimFactory.OpeningCoil;
        var expectedHold = 0m;
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
                    var outcome = await api.Send(stores, "/api/inventory/receipts", new { partNumber = SimFactory.BulkPart, quantity = e.Number("quantity"), locationCode = SimFactory.Stores, inspect = true, reason = "Simulated delivery " + route });
                    report.Expect($"{step}: receipt into inspection accepted", "200", outcome.Label);
                    receipts[route] = outcome.Id;
                    break;
                }

                case "Inspect":
                {
                    // The receiver may not inspect; an independent inspector does.
                    var outcome = await api.Send(insp, $"/api/inventory/receiving/{receipts[route]}/inspections",
                        new { accepted = e.Number("accepted"), rejected = e.Number("rejected"), defect = e.Text("defect"), reason = "Simulated inspection" });
                    report.Expect($"{step}: inspection recorded", "200", outcome.Label);
                    expectedReleased += e.Number("accepted");
                    expectedHold += e.Number("rejected");
                    await CheckStock(step);
                    var open = (await SimApi.Get(qe, "/api/inventory/rejections?open=true")).EnumerateArray()
                        .Select(r => r.GetProperty("rejectionId").GetInt64()).Where(id => !seen.Contains(id)).ToList();
                    report.Expect($"{step}: exactly one new open rejection", "1", open.Count.ToString(CultureInfo.InvariantCulture), "Outcome");
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
                    report.Expect($"{step}: event applied", "applied", "skipped: no rejection to disposition", "Outcome");
                    break;
            }

            await Invariants.Inventory(report, step, stores, parts);
        }

        async Task DispositionAsync(string route, string step)
        {
            var id = rejections[route];
            var r = log.Events.Single(x => x.Type == "Inspect" && x.Text("route") == route).Number("rejected");

            // Authorization: a material handler cannot decide what happens to rejected material.
            report.Expect($"{step}: material handler cannot disposition", "403",
                ((int)(await api.Send(stores, $"/api/inventory/rejections/{id}/disposition", new { disposition = route, reason = "Not my decision" })).Status).ToString(CultureInfo.InvariantCulture), "Authorization");

            var decided = await api.Send(qe, $"/api/inventory/rejections/{id}/disposition", new { disposition = route, reason = "Simulated MRB decision: " + route });
            report.Expect($"{step}: quality engineer dispositions", "200", decided.Label);
            report.Expect($"{step}: a second disposition is refused", "422",
                ((int)(await api.Send(qe, $"/api/inventory/rejections/{id}/disposition", new { disposition = "Scrap", reason = "Changed my mind" })).Status).ToString(CultureInfo.InvariantCulture), "Outcome");

            switch (route)
            {
                case "Scrap":
                    expectedHold -= r; // Scrap posts from hold immediately.
                    await CheckStock(step + " (scrapped)");
                    break;

                case "ReturnToVendor" or "ReworkBySupplier":
                {
                    await CheckStock(step + " (RTV open, still on hold)");
                    var rtv = (await SimApi.Get(stores, "/api/inventory/rtvs?open=true")).EnumerateArray()
                        .SingleOrDefault(x => x.GetProperty("rejectionId").GetInt64() == id);
                    report.Expect($"{step}: an RTV is open for the rejection", "Open", rtv.ValueKind == System.Text.Json.JsonValueKind.Object ? rtv.GetProperty("status").GetString()! : "none", "Outcome");
                    if (rtv.ValueKind != System.Text.Json.JsonValueKind.Object)
                    {
                        break;
                    }

                    var rtvId = rtv.GetProperty("rtvId").GetInt64();
                    report.Expect($"{step}: RTV marked for rework only when the supplier reworks", route == "ReworkBySupplier" ? "True" : "False",
                        rtv.GetProperty("forRework").GetBoolean() ? "True" : "False", "Outcome");
                    report.Expect($"{step}: RTV quantity equals the rejected quantity", r, rtv.GetProperty("quantity").GetDecimal());
                    var shipped = await api.Send(stores, $"/api/inventory/rtvs/{rtvId}/ship", new { carrier = "Simulated carrier", reason = "Return shipment" });
                    report.Expect($"{step}: RTV ships", "200", shipped.Label);
                    expectedHold -= r; // Shipping posts out of hold.
                    await CheckStock(step + " (RTV shipped)");
                    if (route == "ReturnToVendor")
                    {
                        report.Expect($"{step}: material handler cannot record a supplier credit", "403",
                            ((int)(await api.Send(stores, $"/api/inventory/rtvs/{rtvId}/credit", new { reason = "Credit note" })).Status).ToString(CultureInfo.InvariantCulture), "Authorization");
                        var credited = await api.Send(pat, $"/api/inventory/rtvs/{rtvId}/credit", new { reason = "Supplier credit note CN-SIM" });
                        report.Expect($"{step}: purchasing manager records the credit", "200", credited.Label);
                        var after = (await SimApi.Get(stores, "/api/inventory/rtvs")).EnumerateArray().Single(x => x.GetProperty("rtvId").GetInt64() == rtvId);
                        report.Expect($"{step}: RTV closed as credited", "Credited", after.GetProperty("status").GetString()!, "Outcome");
                    }

                    break;
                }

                case "UseAsIs":
                {
                    await CheckStock(step + " (use as is awaiting approval, still on hold)");
                    report.Expect($"{step}: the deciding engineer cannot approve their own use-as-is", "403",
                        ((int)(await api.Send(qe, $"/api/inventory/rejections/{id}/approve", new { reason = "Self approval" })).Status).ToString(CultureInfo.InvariantCulture), "Authorization");
                    var approved = await api.Send(qm, $"/api/inventory/rejections/{id}/approve", new { reason = "Second person agrees: cosmetic" });
                    report.Expect($"{step}: quality manager approves", "200", approved.Label);
                    expectedHold -= r;
                    expectedReleased += r;
                    await CheckStock(step + " (released after approval)");
                    break;
                }
            }
        }

        report.Expect("End: no open rejections", "0",
            (await SimApi.Get(qe, "/api/inventory/rejections?open=true")).EnumerateArray().Count().ToString(CultureInfo.InvariantCulture), "Outcome");
        await Invariants.AuditChain(report, "End", f.Plant.Admin);
        var hash = await Invariants.StateHash(stores, parts);
        report.Note("Supplier dispositions only. Production NCR (MRB) dispositions and customer concessions are Phase B scenarios.");
        report.Note("Expected quantities are computed from the seeded plan and ADR 0035, independently of Forge's code.");
        return (report, log, hash);
    }
}
