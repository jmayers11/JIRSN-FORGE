using System.Globalization;
using Forge.Integration.Tests;
using Forge.Simulation.Tests.Engine;

namespace Forge.Simulation.Tests.Scenarios;

/// <summary>
/// Scenario 9 (quality hold scope): two lots of the same part are in stock; a quality hold on one lot must stop that
/// lot leaving and must not stop the other. Expected outcomes come from the specification, not from Forge's code:
/// a Quality hold blocks shipping of what it targets and nothing else; only quality disposition authority releases it;
/// once released, the lot ships; shipping removes exactly the shipped quantity.
/// </summary>
internal static class HoldScopeScenario
{
    public const string Name = "S09-quality-hold-scope";
    public const string HeldLot = "SIM-L1";
    public const string FreeLot = "SIM-L2";

    public static EventLog Plan(int seed)
    {
        var log = new EventLog(Name, seed);
        var held = log.Between(20, 120);
        var free = log.Between(20, 120);
        var at = TimeSpan.Zero;
        log.Add(at += TimeSpan.FromMinutes(10), "Receive", ("lot", HeldLot), ("quantity", held));
        log.Add(at += TimeSpan.FromMinutes(10), "Receive", ("lot", FreeLot), ("quantity", free));
        log.Add(at += TimeSpan.FromMinutes(60), "PlaceHold", ("lot", HeldLot), ("reason", "Simulated supplier recall on " + HeldLot));
        log.Add(at += TimeSpan.FromMinutes(30), "Ship", ("lot", HeldLot), ("quantity", log.Between(1, held)), ("whileHeld", true));
        log.Add(at += TimeSpan.FromMinutes(5), "Ship", ("lot", FreeLot), ("quantity", log.Between(1, free)), ("whileHeld", false));
        log.Add(at += TimeSpan.FromMinutes(120), "ReleaseHold", ("lot", HeldLot));
        log.Add(at += TimeSpan.FromMinutes(30), "Ship", ("lot", HeldLot), ("quantity", log.Between(1, held)), ("whileHeld", false));
        return log;
    }

    public static async Task<(SimReport Report, EventLog Log, string Hash)> RunAsync(PostgresFixture postgres, int seed, Mutation mutation = Mutation.None)
    {
        var log = Plan(seed);
        var report = new SimReport(mutation == Mutation.None ? Name : Name + RunSummary.CanaryMarker + mutation, seed);
        await using var f = await SimFactory.BuildAsync(postgres, "s09-" + seed.ToString(CultureInfo.InvariantCulture));
        var (api, stores, qe, shipper) = (f.Api, f.StoresUser, f.Api.As("qe"), f.Api.As("ship"));
        var parts = new[] { SimFactory.LotPart };
        var expected = new Dictionary<string, decimal>(StringComparer.Ordinal) { [HeldLot] = 0m, [FreeLot] = 0m };
        long? hold = null;
        var held = false;

        async Task<decimal> OnHand(string lot) =>
            (await Invariants.ReportedBalances(stores, parts)).Where(kv => kv.Key.Lot == lot).Sum(kv => kv.Value);

        var sinceStart = TimeSpan.Zero;
        foreach (var e in log.Events)
        {
            f.Clock.Advance(e.At - sinceStart);
            sinceStart = e.At;
            var lot = e.Text("lot");
            var step = $"#{e.Index} {e.Type} {lot}";
            switch (e.Type)
            {
                case "Receive":
                {
                    report.Rule(Basis.Plan, "Setup: lot-tracked receipt into released stock");
                    var r = await api.Send(stores, "/api/inventory/receipts", new { partNumber = SimFactory.LotPart, quantity = e.Number("quantity"), locationCode = SimFactory.Stores, lotNumber = lot, revision = "A", reason = "Simulated delivery" });
                    report.Expect($"{step}: received", "200", r.Label);
                    expected[lot] += e.Number("quantity");
                    break;
                }

                case "PlaceHold":
                {
                    report.Rule(Basis.Spec, "ADR 0034 §2, §4: a hold can target a lot (with its part); placing is open to the roles that raise holds");
                    var target = mutation == Mutation.HoldWrongLot ? FreeLot : lot;
                    var r = await api.Send(qe, "/api/holds", new { kind = "Quality", targetType = "Lot", targetPart = SimFactory.LotPart, targetId = target, source = "Simulation", reason = e.Text("reason") });
                    report.Expect($"{step}: quality engineer places the hold", "200", r.Label);
                    if (r.Succeeded)
                    {
                        hold = r.Id;
                        held = true;
                    }

                    break;
                }

                case "ReleaseHold" when hold is { } id:
                {
                    report.Rule(Basis.Spec, "ADR 0034 §4: a Quality hold is released with quality.disposition");
                    var denied = await api.Send(stores, $"/api/holds/{id}/release", new { reason = "I need the material" });
                    report.Expect($"{step}: a material handler cannot release a quality hold", "403", ((int)denied.Status).ToString(CultureInfo.InvariantCulture), "Authorization");
                    var r = await api.Send(qe, $"/api/holds/{id}/release", new { reason = "Recall cleared with supplier evidence" });
                    report.Expect($"{step}: quality engineer releases the hold", "200", r.Label);
                    held = !r.Succeeded;
                    break;
                }

                case "Ship":
                {
                    var quantity = e.Number("quantity");
                    var blocked = lot == HeldLot && held;
                    var outcome = await ShipAsync(api, stores, shipper, lot, quantity);
                    report.Rule(Basis.Spec, "ADR 0034 §3, §5: Quality blocks shipping; the gate refuses with on_hold only when the hold's target is among the records shipped");
                    if (blocked)
                    {
                        report.Expect($"{step}: refused because the lot is on quality hold", "422 on_hold", outcome, "Scope");
                    }
                    else
                    {
                        report.Expect($"{step}: ships", "shipped", outcome, "Scope");
                        expected[lot] -= quantity;
                    }

                    break;
                }

                default:
                    report.Rule(Basis.Plan, "Every planned event must be applied");
                    report.Expect($"{step}: event applied", "applied", "skipped: no hold to release", "Outcome");
                    break;
            }

            report.Rule(Basis.Plan, "On hand per lot = received − shipped, from the seeded plan");
            foreach (var (l, q) in expected)
            {
                report.Expect($"{step}: on hand {l}", q, await OnHand(l));
            }

            await Invariants.Inventory(report, step, stores, parts);
        }

        await Invariants.AuditChain(report, "End", f.Plant.Admin);
        var hash = await Invariants.StateHash(stores, parts);
        if (mutation != Mutation.None)
        {
            report.Note("Negative control: " + mutation + ". This run is expected to fail.");
        }

        report.Note("The refusal may come at shipment creation, packing or shipping; the scenario records the first refusal and requires that the held lot never leaves.");
        return (report, log, hash);
    }

    /// <summary>Create, pack and ship one line. Returns "shipped", or the first refusal as "status code" (on_hold when the body names a hold).</summary>
    private static async Task<string> ShipAsync(SimApi api, HttpClient stores, HttpClient shipper, string lot, decimal quantity)
    {
        var created = await api.Send(stores, "/api/inventory/shipments", new { customer = "Simulation customer", reference = "SIM-" + lot, lines = new[] { new { partNumber = SimFactory.LotPart, quantity, locationCode = SimFactory.Stores, lotNumber = lot } }, reason = "Simulated customer shipment" });
        if (!created.Succeeded)
        {
            return Refusal(created);
        }

        var id = created.Id;
        var packed = await api.Send(stores, $"/api/inventory/shipments/{id}/pack", new { reason = "Packed" });
        if (!packed.Succeeded)
        {
            return Refusal(packed);
        }

        var shipped = await api.Send(shipper, $"/api/inventory/shipments/{id}/ship", new { carrier = "Simulated carrier", tracking = "TRK-" + lot, reason = "Shipped" });
        return shipped.Succeeded ? "shipped" : Refusal(shipped);
    }

    private static string Refusal(Outcome o) =>
        o.Body.Contains("on_hold", StringComparison.Ordinal) ? ((int)o.Status).ToString(CultureInfo.InvariantCulture) + " on_hold" : o.Label;
}
