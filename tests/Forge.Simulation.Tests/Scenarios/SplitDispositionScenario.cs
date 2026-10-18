using System.Globalization;
using System.Text.Json;
using Forge.Integration.Tests;
using Forge.Simulation.Tests.Engine;

namespace Forge.Simulation.Tests.Scenarios;

/// <summary>
/// Scenario 10b (split supplier dispositions, ADR 0055): one delivery is inspected with a rejection; part of it goes back
/// to the vendor, the open return is then reduced after a re-check, and each remainder takes its own disposition (use as
/// is with a second person's approval, and scrap). Every rejected unit must be in exactly one rejection, quantities only
/// go down, and nothing leaves quality hold without a decision.
/// </summary>
internal static class SplitDispositionScenario
{
    public const string Name = "S10b-split-dispositions";
    private const string Rulings = "ADR 0055";

    public static EventLog Plan(int seed)
    {
        var log = new EventLog(Name, seed);
        var received = log.Between(80, 240);
        var rejected = log.Between(12, received / 2);
        var returned = log.Between(rejected / 2 + 1, rejected - 2);
        var reducedTo = log.Between(1, returned - 1);
        var at = TimeSpan.Zero;
        log.Add(at += TimeSpan.FromMinutes(15), "Receive", ("quantity", received));
        log.Add(at += TimeSpan.FromMinutes(45), "Inspect", ("accepted", received - rejected), ("rejected", rejected), ("defect", "Simulated edge damage"));
        log.Add(at += TimeSpan.FromMinutes(30), "ReturnPart", ("quantity", returned));
        log.Add(at += TimeSpan.FromMinutes(90), "ReduceReturn", ("quantity", reducedTo));
        log.Add(at += TimeSpan.FromMinutes(20), "DecideRemainder", ("which", "first"), ("disposition", "UseAsIs"));
        log.Add(at += TimeSpan.FromMinutes(20), "DecideRemainder", ("which", "second"), ("disposition", "Scrap"));
        log.Add(at += TimeSpan.FromMinutes(60), "ShipReturn");
        return log;
    }

    public static async Task<(SimReport Report, EventLog Log, string Hash)> RunAsync(PostgresFixture postgres, int seed, Mutation mutation = Mutation.None)
    {
        var log = Plan(seed);
        var report = new SimReport(mutation == Mutation.None ? Name : Name + RunSummary.CanaryMarker + mutation, seed);
        await using var f = await SimFactory.BuildAsync(postgres, "s11-" + seed.ToString(CultureInfo.InvariantCulture));
        report.Evidence(f.Api);
        report.Covers(Coverage.Receiving, Coverage.Mrb);
        var (api, stores, insp, qe, qm) = (f.Api, f.StoresUser, f.Api.As("insp"), f.Api.As("qe"), f.Api.As("qm"));
        var parts = new[] { SimFactory.BulkPart };

        var expectedReleased = SimFactory.OpeningCoil;
        var expectedHold = 0m;
        decimal rejected = 0m, returned = 0m, reducedTo = 0m;
        long receipt = 0, inspection = 0, original = 0, rtvId = 0;
        var remainders = new Dictionary<string, (long Id, decimal Quantity)>(StringComparer.Ordinal);

        async Task<decimal> Stock(string status) =>
            (await Invariants.ReportedBalances(stores, parts)).GetValueOrDefault(new Bucket(SimFactory.BulkPart, SimFactory.Stores, null, status));

        async Task CheckStock(string step)
        {
            report.Expect($"{step}: Released", expectedReleased, await Stock("Released"));
            report.Expect($"{step}: Hold", expectedHold, await Stock("Hold"));
            report.Expect($"{step}: Inspection", 0m, await Stock("Inspection"));
        }

        async Task<JsonElement[]> OpenRejections() => (await SimApi.Get(qe, "/api/inventory/rejections?open=true")).EnumerateArray().ToArray();

        var sinceStart = TimeSpan.Zero;
        foreach (var e in log.Events)
        {
            f.Clock.Advance(e.At - sinceStart);
            sinceStart = e.At;
            var step = $"#{e.Index} {e.Type}";
            switch (e.Type)
            {
                case "Receive":
                {
                    report.Rule(Basis.Spec, "ADR 0035 §3: a receipt can go into inspection");
                    var outcome = await api.Send(stores, "/api/inventory/receipts", new { partNumber = SimFactory.BulkPart, quantity = e.Number("quantity"), locationCode = SimFactory.Stores, inspect = true, reason = "Simulated delivery" });
                    report.Expect($"{step}: receipt into inspection accepted", "200", outcome.Label);
                    receipt = outcome.Succeeded ? outcome.Id : 0;
                    break;
                }

                case "Inspect":
                {
                    report.Rule(Basis.Spec, "ADR 0035 §3: accepted quantity is released and rejected quantity goes on hold");
                    rejected = e.Number("rejected");
                    var outcome = await api.Send(insp, $"/api/inventory/receiving/{receipt}/inspections", new { accepted = e.Number("accepted"), rejected, defect = e.Text("defect"), reason = "Simulated inspection" });
                    report.Expect($"{step}: inspection recorded", "200", outcome.Label);
                    expectedReleased += e.Number("accepted");
                    expectedHold += rejected;
                    await CheckStock(step);
                    report.Rule(Basis.Spec, Rulings + " §4: an inspection with a rejected quantity records exactly one supplier rejection");
                    var open = await OpenRejections();
                    report.Expect($"{step}: exactly one rejection, for the whole rejected quantity", Invariants.Q(rejected),
                        open.Length == 1 ? Invariants.Q(open[0].GetProperty("quantity").GetDecimal()) : open.Length.ToString(CultureInfo.InvariantCulture) + " rejections");
                    if (open.Length == 1)
                    {
                        original = open[0].GetProperty("rejectionId").GetInt64();
                        inspection = open[0].GetProperty("inspectionId").GetInt64();
                    }

                    break;
                }

                case "ReturnPart":
                {
                    returned = e.Number("quantity");
                    report.Rule(Basis.Spec, Rulings + " §3: a disposition covers at most the rejected quantity");
                    var over = await api.Send(qe, $"/api/inventory/rejections/{original}/disposition", new { disposition = "ReturnToVendor", quantity = rejected + 1m, reason = "More than rejected" });
                    report.Expect($"{step}: a disposition for more than was rejected is refused", "422 over_rejected_quantity", over.Label);

                    report.Rule(Basis.Spec, Rulings + " §3, §5: part of a rejection goes back; the rest stays on hold as a new pending rejection split from it");
                    var decided = await api.Send(qe, $"/api/inventory/rejections/{original}/disposition", new { disposition = "ReturnToVendor", quantity = returned, reason = "Damaged section goes back" });
                    report.Expect($"{step}: part of the rejection returned to the vendor", "200", decided.Label);
                    var open = await OpenRejections();
                    var remainder = open.SingleOrDefault(r => r.TryGetProperty("splitFrom", out var s) && s.ValueKind == JsonValueKind.Number && s.GetInt64() == original);
                    var found = remainder.ValueKind == JsonValueKind.Object;
                    report.Expect($"{step}: the rest is one pending rejection split from the original", Invariants.Q(rejected - returned) + " Pending",
                        found ? Invariants.Q(remainder.GetProperty("quantity").GetDecimal()) + " " + remainder.GetProperty("disposition").GetString() : "none");
                    if (found)
                    {
                        remainders["first"] = (remainder.GetProperty("rejectionId").GetInt64(), rejected - returned);
                    }

                    var rtv = (await SimApi.Get(stores, "/api/inventory/rtvs?open=true")).EnumerateArray().SingleOrDefault(r => r.GetProperty("rejectionId").GetInt64() == original);
                    report.Expect($"{step}: the return carries the decided quantity", Invariants.Q(returned),
                        rtv.ValueKind == JsonValueKind.Object ? Invariants.Q(rtv.GetProperty("quantity").GetDecimal()) : "none");
                    rtvId = rtv.ValueKind == JsonValueKind.Object ? rtv.GetProperty("rtvId").GetInt64() : 0;
                    report.Rule(Basis.Spec, Rulings + " §5: nothing leaves quality hold until it ships or has its own disposition");
                    await CheckStock(step);
                    break;
                }

                case "ReduceReturn":
                {
                    reducedTo = e.Number("quantity");
                    report.Rule(Basis.Spec, Rulings + " §5: only quality disposition authority reduces an open return, and only downwards");
                    report.Expect($"{step}: a material handler cannot reduce a return", "403",
                        ((int)(await api.Send(stores, $"/api/inventory/rtvs/{rtvId}/reduce", new { quantity = reducedTo, reason = "I need it" })).Status).ToString(CultureInfo.InvariantCulture), "Authorization");
                    var up = await api.Send(qe, $"/api/inventory/rtvs/{rtvId}/reduce", new { quantity = returned + 1m, reason = "More" });
                    report.Expect($"{step}: a return cannot be increased", "422 rtv_quantity_not_reduced", up.Label);
                    var reduced = await api.Send(qe, $"/api/inventory/rtvs/{rtvId}/reduce", new { quantity = reducedTo, reason = "Re-check: part is within tolerance" });
                    report.Expect($"{step}: the return is reduced", "200", reduced.Label);

                    report.Rule(Basis.Spec, Rulings + " §5: the reduced part stays on hold as a new pending rejection split from the original");
                    var open = await OpenRejections();
                    var known = remainders.Values.Select(r => r.Id).ToHashSet();
                    var remainder = open.SingleOrDefault(r => !known.Contains(r.GetProperty("rejectionId").GetInt64())
                        && r.TryGetProperty("splitFrom", out var s) && s.ValueKind == JsonValueKind.Number && s.GetInt64() == original);
                    var found = remainder.ValueKind == JsonValueKind.Object;
                    report.Expect($"{step}: the reduced part is one new pending rejection", Invariants.Q(returned - reducedTo) + " Pending",
                        found ? Invariants.Q(remainder.GetProperty("quantity").GetDecimal()) + " " + remainder.GetProperty("disposition").GetString() : "none");
                    if (found)
                    {
                        remainders["second"] = (remainder.GetProperty("rejectionId").GetInt64(), returned - reducedTo);
                    }

                    var rtv = (await SimApi.Get(stores, "/api/inventory/rtvs?open=true")).EnumerateArray().SingleOrDefault(r => r.GetProperty("rtvId").GetInt64() == rtvId);
                    report.Expect($"{step}: the return now carries the reduced quantity", Invariants.Q(reducedTo),
                        rtv.ValueKind == JsonValueKind.Object ? Invariants.Q(rtv.GetProperty("quantity").GetDecimal()) : "none");
                    if (mutation == Mutation.ReleaseReducedPart)
                    {
                        expectedHold -= returned - reducedTo;
                        expectedReleased += returned - reducedTo;
                    }

                    await CheckStock(step);
                    break;
                }

                case "DecideRemainder" when remainders.TryGetValue(e.Text("which"), out var pending):
                {
                    var disposition = e.Text("disposition");
                    step += " " + e.Text("which");
                    report.Rule(Basis.Spec, Rulings + " §3: each remainder takes its own disposition");
                    var decided = await api.Send(qe, $"/api/inventory/rejections/{pending.Id}/disposition", new { disposition, reason = "Simulated decision for the remainder" });
                    report.Expect($"{step}: {disposition} decided", "200", decided.Label);
                    if (disposition == "UseAsIs")
                    {
                        report.Rule(Basis.Spec, "ADR 0035 §4: use as is waits for a different person with quality approval, and only then releases");
                        report.Expect($"{step}: the deciding engineer cannot approve", "403",
                            ((int)(await api.Send(qe, $"/api/inventory/rejections/{pending.Id}/approve", new { reason = "Mine" })).Status).ToString(CultureInfo.InvariantCulture), "Authorization");
                        var approved = await api.Send(qm, $"/api/inventory/rejections/{pending.Id}/approve", new { reason = "Agreed" });
                        report.Expect($"{step}: quality manager approves", "200", approved.Label);
                        expectedHold -= pending.Quantity;
                        expectedReleased += pending.Quantity;
                    }
                    else
                    {
                        report.Rule(Basis.Spec, "ADR 0035 §4: scrap posts a scrap transaction from hold");
                        expectedHold -= pending.Quantity;
                    }

                    await CheckStock(step);
                    break;
                }

                case "ShipReturn":
                {
                    report.Rule(Basis.Spec, "ADR 0035 §5: shipping posts a ReturnToVendor transaction out of hold");
                    var shipped = await api.Send(stores, $"/api/inventory/rtvs/{rtvId}/ship", new { carrier = "Simulated carrier", reason = "Return shipment" });
                    report.Expect($"{step}: the return ships", "200", shipped.Label);
                    expectedHold -= reducedTo;
                    await CheckStock(step);
                    report.Rule(Basis.Spec, Rulings + " §5: a return cannot change once shipped");
                    var late = await api.Send(qe, $"/api/inventory/rtvs/{rtvId}/reduce", new { quantity = 1m, reason = "Too late" });
                    report.Expect($"{step}: a shipped return cannot be reduced", "422 rtv_not_open", late.Label);
                    break;
                }

                default:
                    report.Rule(Basis.Plan, "Every planned event must be applied");
                    report.Expect($"{step}: event applied", "applied", "skipped: an earlier step did not produce what it needed");
                    break;
            }

            await Invariants.Inventory(report, step, stores, parts);
        }

        report.Rule(Basis.Spec, Rulings + " §6, Consequences: every rejected unit is in exactly one rejection and all are closed");
        report.Expect("End: no open rejections", "0", (await OpenRejections()).Length.ToString(CultureInfo.InvariantCulture));
        var all = (await SimApi.Get(qe, "/api/inventory/rejections")).EnumerateArray().Where(r => r.GetProperty("inspectionId").GetInt64() == inspection).ToArray();
        report.Expect("End: the rejections of the inspection add up to what was rejected", rejected, all.Sum(r => r.GetProperty("quantity").GetDecimal()), "Inventory");
        report.Expect("End: decisions are return to vendor, scrap and use as is", "ReturnToVendor,Scrap,UseAsIs",
            string.Join(',', all.Select(r => r.GetProperty("disposition").GetString()!).Order(StringComparer.Ordinal)));
        await Invariants.AuditChain(report, "End", f.Plant.Admin);
        var hash = await Invariants.StateHash(stores, parts);
        report.Note($"Rejected {Invariants.Q(rejected)}: returned {Invariants.Q(reducedTo)} after reducing from {Invariants.Q(returned)}, used as is {Invariants.Q(rejected - returned)}, scrapped {Invariants.Q(returned - reducedTo)}.");
        if (mutation != Mutation.None)
        {
            report.Note("Negative control: " + mutation + ". This run is expected to fail.");
        }

        return (report, log, hash);
    }
}
