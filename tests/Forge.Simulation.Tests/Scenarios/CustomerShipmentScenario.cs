using System.Globalization;
using Forge.Integration.Tests;
using Forge.Simulation.Tests.Engine;

namespace Forge.Simulation.Tests.Scenarios;

/// <summary>
/// Scenario 22 (customer shipment): a customer order for brackets is shipped from two lots. The first shipment is packed
/// by stores and shipped by a shipper (stores cannot ship); its ship response is lost and retried, and it leaves once.
/// A shipment for more than the lot holds cannot be packed. The second lot goes on quality hold and cannot ship until
/// quality releases it. After each step the lots hold received minus shipped, and the demand shows what has shipped.
/// Expectations come from the Phase 7 shipping specification (pack and ship by different people; no shipping beyond
/// stock), ADR 0034 (quality holds block shipping) and ADR 0055 §1 (a retried command applies once), never from Forge.
/// </summary>
internal static class CustomerShipmentScenario
{
    public const string Name = "S22-customer-shipment";
    private const string Spec = "Phase 7 shipping";
    private const string Part = SimFactory.LotPart;

    public static EventLog Plan(int seed)
    {
        var log = new EventLog(Name, seed);
        var a = log.Between(20, 60);
        var b = log.Between(20, 60);
        log.Add(TimeSpan.Zero, "Stock", ("a", a), ("b", b));
        log.Add(TimeSpan.FromMinutes(5), "Demand", ("quantity", a + b - log.Between(1, 10)), ("needInDays", log.Between(3, 10)));
        log.Add(TimeSpan.FromMinutes(10), "ShipLost", ("lot", "SHIP-1"), ("quantity", a - log.Between(1, 5)), ("retries", log.Between(1, 3)));
        log.Add(TimeSpan.FromMinutes(20), "OverShip", ("lot", "SHIP-1"));
        log.Add(TimeSpan.FromMinutes(30), "Hold", ("lot", "SHIP-2"));
        log.Add(TimeSpan.FromMinutes(40), "ShipHeld", ("lot", "SHIP-2"));
        log.Add(TimeSpan.FromMinutes(50), "Release", ("lot", "SHIP-2"));
        log.Add(TimeSpan.FromMinutes(60), "ShipRest", ("lot", "SHIP-2"));
        return log;
    }

    public static async Task<(SimReport Report, EventLog Log, string Hash)> RunAsync(PostgresFixture postgres, int seed, Mutation mutation = Mutation.None)
    {
        var log = Plan(seed);
        var report = new SimReport(mutation == Mutation.None ? Name : Name + RunSummary.CanaryMarker + mutation, seed);
        await using var f = await SimFactory.BuildAsync(postgres, "s22-" + seed.ToString(CultureInfo.InvariantCulture));
        report.Evidence(f.Api);
        report.Covers(Coverage.Shipping, Coverage.Holds, Coverage.Warehouse, Coverage.Demand, Coverage.Network);
        var (stores, ship, qe, planner) = (f.StoresUser, f.Api.As("ship"), f.Api.As("qe"), f.Api.As("planner"));
        var parts = new[] { Part };
        var onHand = new Dictionary<string, decimal>(StringComparer.Ordinal) { ["SHIP-1"] = 0m, ["SHIP-2"] = 0m };
        long demand = 0, hold = 0;
        long? packedHeld = null;
        decimal wanted = 0m, shipped = 0m;

        async Task<Outcome> CreateAndPack(string lot, decimal quantity)
        {
            var created = await f.Api.Send(stores, "/api/inventory/shipments", new { demandId = demand, shipTo = "Customer dock", lines = new[] { new { partNumber = Part, quantity, locationCode = SimFactory.Stores, lotNumber = lot } }, reason = "Customer order" });
            if (!created.Succeeded)
            {
                return created;
            }

            var packed = await f.Api.Send(stores, $"/api/inventory/shipments/{created.Id}/pack", new { reason = "Packed" });
            return packed.Succeeded ? created : packed;
        }

        static string Refusal(Outcome o) => o.Body.Contains("on_hold", StringComparison.Ordinal) ? ((int)o.Status).ToString(CultureInfo.InvariantCulture) + " on_hold" : o.Label;

        async Task ExpectState(string step)
        {
            report.Rule(Basis.Spec, "Inventory: each lot holds what was received minus what shipped");
            var balances = await Invariants.ReportedBalances(stores, [Part]);
            foreach (var (lot, q) in onHand)
            {
                report.Expect($"{step}: on hand {lot}", q, balances.Where(b => b.Key.Lot == lot).Sum(b => b.Value));
            }

            report.Rule(Basis.Spec, "Demand: shipments against a customer order count as shipped on it");
            var view = (await SimApi.Get(planner, "/api/planning/demand?includeClosed=true")).EnumerateArray().Single(d => d.GetProperty("demandId").GetInt64() == demand);
            report.Expect($"{step}: shipped on the demand", shipped, view.GetProperty("shipped").GetDecimal(), "Shipping");
            report.Expect($"{step}: remaining on the demand", Math.Max(0m, wanted - shipped), view.GetProperty("remaining").GetDecimal(), "Shipping");
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
                    report.Rule(Basis.Plan, "Setup: two lots of brackets in stores");
                    foreach (var (lot, key) in new[] { ("SHIP-1", "a"), ("SHIP-2", "b") })
                    {
                        report.Expect($"{step}: {lot} received", "200", (await f.Api.Send(stores, "/api/inventory/receipts", new { partNumber = Part, quantity = ev.Number(key), locationCode = SimFactory.Stores, lotNumber = lot, revision = "A", reason = "Stock" })).Label);
                        onHand[lot] = ev.Number(key);
                    }

                    break;

                case "Demand":
                {
                    wanted = ev.Number("quantity");
                    report.Rule(Basis.Plan, "Setup: a customer order");
                    var recorded = await f.Api.Send(planner, "/api/planning/demand", new { partNumber = Part, quantity = wanted, needDate = f.Day((int)ev.Number("needInDays")), priority = 2, kind = "CustomerOrder", customer = "Acme", reference = "PO-901", reason = "Customer order" });
                    report.Expect($"{step}: demand recorded", "200", recorded.Label);
                    demand = recorded.Id;
                    await ExpectState(step);
                    break;
                }

                case "ShipLost":
                {
                    var (lot, quantity) = (ev.Text("lot"), ev.Number("quantity"));
                    var created = await CreateAndPack(lot, quantity);
                    report.Expect($"{step}: created and packed", "200", created.Label);
                    report.Rule(Basis.Spec, Spec + ": the person who packs does not ship");
                    report.Expect($"{step}: stores cannot ship", "403",
                        ((int)(await f.Api.Send(stores, $"/api/inventory/shipments/{created.Id}/ship", new { carrier = "UPS", reason = "Ship" })).Status).ToString(CultureInfo.InvariantCulture), "Authorization");
                    report.Rule(Basis.Spec, "ADR 0055 §1: a ship command whose response was lost, retried with the same key, ships once");
                    var key = f.Api.NextKey();
                    var body = new { carrier = "UPS", tracking = "1Z-SIM", reason = "Ship" };
                    await SimApi.SendAndLoseResponse(ship, $"/api/inventory/shipments/{created.Id}/ship", body, key);
                    for (var i = 0; i < (int)ev.Number("retries"); i++)
                    {
                        report.Expect($"{step}: retry {i + 1} answered", "200", (await f.Api.Send(ship, $"/api/inventory/shipments/{created.Id}/ship", body, key)).Label, "Idempotency");
                    }

                    onHand[lot] -= quantity;
                    shipped += quantity;
                    await ExpectState(step);
                    break;
                }

                case "OverShip":
                {
                    var lot = ev.Text("lot");
                    report.Rule(Basis.Spec, Spec + ": a shipment for more than the lot holds is not packed");
                    var outcome = await CreateAndPack(lot, onHand[lot] + 1m);
                    report.Expect($"{step}: refused", "422 insufficient_stock", outcome.Label);
                    await ExpectState(step);
                    break;
                }

                case "Hold":
                {
                    report.Rule(Basis.Plan, "Setup: quality holds the second lot");
                    var placed = await f.Api.Send(qe, "/api/holds", new { kind = "Quality", targetType = "Lot", targetPart = Part, targetId = ev.Text("lot"), source = "Simulation", reason = "Supplier recall" });
                    report.Expect($"{step}: hold placed", "200", placed.Label);
                    hold = placed.Succeeded ? placed.Id : 0;
                    break;
                }

                case "ShipHeld":
                case "ShipRest":
                {
                    var lot = ev.Text("lot");
                    var quantity = Math.Min(wanted - shipped, onHand[lot]);
                    Outcome outcome;
                    if (ev.Type == "ShipRest" && packedHeld is { } waiting)
                    {
                        // The shipment packed while the lot was held is still there; once released it ships.
                        outcome = await f.Api.Send(ship, $"/api/inventory/shipments/{waiting}/ship", new { carrier = "UPS", reason = "Ship" });
                    }
                    else
                    {
                        var created = await CreateAndPack(lot, quantity);
                        outcome = created.Succeeded ? await f.Api.Send(ship, $"/api/inventory/shipments/{created.Id}/ship", new { carrier = "UPS", reason = "Ship" }) : created;
                        if (ev.Type == "ShipHeld" && created.Succeeded)
                        {
                            packedHeld = created.Id;
                        }
                    }
                    if (ev.Type == "ShipHeld")
                    {
                        report.Rule(Basis.Spec, "ADR 0034 §3: a lot on quality hold does not ship");
                        report.Expect($"{step}: refused", "422 on_hold", Refusal(outcome), "Holds");
                    }
                    else
                    {
                        report.Rule(Basis.Spec, Spec + ": once released, the rest of the order ships");
                        report.Expect($"{step}: shipped", "200", outcome.Label);
                        onHand[lot] -= quantity;
                        shipped += quantity;
                    }

                    await ExpectState(step);
                    break;
                }

                case "Release":
                    report.Rule(Basis.Spec, "ADR 0034 §4: quality releases its hold");
                    report.Expect($"{step}: hold released", "200", (await f.Api.Send(qe, $"/api/holds/{hold}/release", new { reason = "Recall cleared" })).Label);
                    break;
            }

            await Invariants.Inventory(report, step, stores, parts);
        }

        await Invariants.AuditChain(report, "End", f.Plant.Admin);
        report.Note("A held lot may be refused at creation, packing or shipping; the check accepts the first refusal and requires that nothing leaves.");
        return (report, log, await Invariants.StateHash(stores, parts));
    }
}
