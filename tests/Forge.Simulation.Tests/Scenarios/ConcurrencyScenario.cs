using System.Globalization;
using Forge.Integration.Tests;
using Forge.Simulation.Tests.Engine;

namespace Forge.Simulation.Tests.Scenarios;

/// <summary>
/// Scenario 16 (concurrent transactions): terminals act on the same material at the same moment, each with its own
/// command. Two scraps of a whole lot, two shipments of the whole lot, two different dispositions of one rejection, and
/// two quality managers approving one use-as-is: in every race exactly one wins, the losers are refused, and stock is
/// never consumed twice or driven negative. Expectations come from the inventory and receiving specifications (the
/// ledger explains every quantity; a rejection is decided once; ADR 0055) and the idempotency contract, never from Forge.
/// </summary>
internal static class ConcurrencyScenario
{
    public const string Name = "S16-concurrent-transactions";
    private const string Part = SimFactory.LotPart;

    public static EventLog Plan(int seed)
    {
        var log = new EventLog(Name, seed);
        log.Add(TimeSpan.Zero, "ScrapRace", ("lot", "RACE-SCRAP"), ("quantity", log.Between(5, 50)), ("terminals", log.Between(2, 4)));
        log.Add(TimeSpan.FromMinutes(10), "ShipRace", ("lot", "RACE-SHIP"), ("quantity", log.Between(5, 50)), ("terminals", 2));
        log.Add(TimeSpan.FromMinutes(20), "DispositionRace", ("quantity", log.Between(20, 80)), ("rejected", log.Between(2, 10)));
        log.Add(TimeSpan.FromMinutes(30), "ApproveRace", ("quantity", log.Between(20, 80)), ("rejected", log.Between(2, 10)));
        return log;
    }

    public static async Task<(SimReport Report, EventLog Log, string Hash)> RunAsync(PostgresFixture postgres, int seed, Mutation mutation = Mutation.None)
    {
        var log = Plan(seed);
        var report = new SimReport(mutation == Mutation.None ? Name : Name + RunSummary.CanaryMarker + mutation, seed);
        await using var f = await SimFactory.BuildAsync(postgres, "s16-" + seed.ToString(CultureInfo.InvariantCulture));
        report.Evidence(f.Api);
        report.Covers(Coverage.Concurrency, Coverage.Shipping, Coverage.Mrb, Coverage.Warehouse);
        await f.PeopleAsync(("qe2", "QualityEngineer"), ("qm2", "QualityManager"), ("ship2", "Shipper"));
        var (stores, insp, qe, qm) = (f.StoresUser, f.Api.As("insp"), f.Api.As("qe"), f.Api.As("qm"));
        var parts = new[] { Part, SimFactory.BulkPart };

        async Task<decimal> Lot(string lot) =>
            (await Invariants.ReportedBalances(stores, [Part])).Where(b => b.Key.Lot == lot).Sum(b => b.Value);

        async Task<decimal> Coil(string status) =>
            (await Invariants.ReportedBalances(stores, [SimFactory.BulkPart])).GetValueOrDefault(new Bucket(SimFactory.BulkPart, SimFactory.Stores, null, status));

        static string Tally(Outcome[] outcomes) =>
            $"{outcomes.Count(o => o.Succeeded)} won, {outcomes.Count(o => !o.Succeeded && (int)o.Status is 409 or 422)} refused, {outcomes.Count(o => !o.Succeeded && (int)o.Status is not (409 or 422))} other";

        async Task<long> RejectionAsync(decimal quantity, decimal rejected, string step)
        {
            var receipt = await f.Api.Send(stores, "/api/inventory/receipts", new { partNumber = SimFactory.BulkPart, quantity, locationCode = SimFactory.Stores, inspect = true, reason = "Delivery" });
            report.Expect($"{step}: receipt", "200", receipt.Label);
            var seen = (await SimApi.Get(qe, "/api/inventory/rejections")).EnumerateArray().Select(r => r.GetProperty("rejectionId").GetInt64()).ToHashSet();
            report.Expect($"{step}: inspection", "200", (await f.Api.Send(insp, $"/api/inventory/receiving/{receipt.Id}/inspections", new { accepted = quantity - rejected, rejected, defect = "Dented", reason = "Inspection" })).Label);
            return (await SimApi.Get(qe, "/api/inventory/rejections?open=true")).EnumerateArray().Select(r => r.GetProperty("rejectionId").GetInt64()).Single(id => !seen.Contains(id));
        }

        var sinceStart = TimeSpan.Zero;
        foreach (var ev in log.Events)
        {
            f.Clock.Advance(ev.At - sinceStart);
            sinceStart = ev.At;
            var step = $"#{ev.Index} {ev.Type}";
            switch (ev.Type)
            {
                case "ScrapRace":
                {
                    var (lot, quantity, terminals) = (ev.Text("lot"), ev.Number("quantity"), (int)ev.Number("terminals"));
                    report.Rule(Basis.Plan, "Setup: one lot in stores");
                    report.Expect($"{step}: lot received", "200", (await f.Api.Send(stores, "/api/inventory/receipts", new { partNumber = Part, quantity, locationCode = SimFactory.Stores, lotNumber = lot, revision = "A", reason = "Stock" })).Label);
                    report.Rule(Basis.Spec, "Inventory ledger: stock is never consumed twice or driven negative; concurrent consumers of the same stock serialize");
                    var outcomes = await Task.WhenAll(Enumerable.Range(0, terminals).Select(_ => f.Api.Send(f.Api.As("stores"), "/api/inventory/scrap",
                        new { partNumber = Part, quantity, locationCode = SimFactory.Stores, lotNumber = lot, reason = "Damaged in handling" })));
                    report.Expect($"{step}: one scrap wins, the rest are refused", $"1 won, {terminals - 1} refused, 0 other", Tally(outcomes), "Concurrency");
                    report.Expect($"{step}: lot is empty, not negative", 0m, await Lot(lot));
                    break;
                }

                case "ShipRace":
                {
                    var (lot, quantity) = (ev.Text("lot"), ev.Number("quantity"));
                    report.Rule(Basis.Plan, "Setup: one lot and two packed shipments, each for all of it");
                    report.Expect($"{step}: lot received", "200", (await f.Api.Send(stores, "/api/inventory/receipts", new { partNumber = Part, quantity, locationCode = SimFactory.Stores, lotNumber = lot, revision = "A", reason = "Stock" })).Label);
                    var shipments = new List<long>();
                    for (var i = 0; i < 2; i++)
                    {
                        var created = await f.Api.Send(stores, "/api/inventory/shipments", new { customer = "Simulation customer", reference = $"RACE-{i}", lines = new[] { new { partNumber = Part, quantity, locationCode = SimFactory.Stores, lotNumber = lot } }, reason = "Order" });
                        if (created.Succeeded)
                        {
                            shipments.Add(created.Id);
                            await f.Api.Send(stores, $"/api/inventory/shipments/{created.Id}/pack", new { reason = "Packed" });
                        }
                    }

                    report.Rule(Basis.Spec, "Inventory ledger: two shipments of the same stock cannot both leave");
                    var shippers = new[] { f.Api.As("ship"), f.Api.As("ship2") };
                    var outcomes = await Task.WhenAll(shipments.Select((s, i) => f.Api.Send(shippers[i % 2], $"/api/inventory/shipments/{s}/ship", new { carrier = "Carrier", reason = "Ship" })));
                    report.Note($"{step}: {shipments.Count} of 2 shipments were accepted for the same lot before the race (a refusal at creation or packing is also safe).");
                    report.Expect($"{step}: exactly one shipment leaves", "1", outcomes.Count(o => o.Succeeded).ToString(CultureInfo.InvariantCulture), "Concurrency");
                    report.Expect($"{step}: no racing ship failed unexpectedly", "0", outcomes.Count(o => !o.Succeeded && (int)o.Status is not (409 or 422)).ToString(CultureInfo.InvariantCulture), "Concurrency");
                    report.Expect($"{step}: lot is empty, not negative", 0m, await Lot(lot));
                    break;
                }

                case "DispositionRace":
                {
                    var rejected = ev.Number("rejected");
                    var id = await RejectionAsync(ev.Number("quantity"), rejected, step);
                    var holdBefore = await Coil("Hold");
                    report.Rule(Basis.Spec, "ADR 0055 §3: a rejection is decided once; two engineers deciding at once leave exactly one decision");
                    var outcomes = await Task.WhenAll(
                        f.Api.Send(qe, $"/api/inventory/rejections/{id}/disposition", new { disposition = "Scrap", reason = "Scrap it" }),
                        f.Api.Send(f.Api.As("qe2"), $"/api/inventory/rejections/{id}/disposition", new { disposition = "ReturnToVendor", reason = "Send it back" }));
                    report.Expect($"{step}: one disposition wins", "1 won, 1 refused, 0 other", Tally(outcomes), "Concurrency");
                    var decided = (await SimApi.Get(qe, "/api/inventory/rejections")).EnumerateArray().Single(r => r.GetProperty("rejectionId").GetInt64() == id).GetProperty("disposition").GetString();
                    var rtvs = (await SimApi.Get(stores, "/api/inventory/rtvs")).EnumerateArray().Count(r => r.GetProperty("rejectionId").GetInt64() == id);
                    report.Rule(Basis.Spec, "ADR 0035 §4: the winning decision alone has effect: scrap posts from hold, return to vendor opens one RTV and stays on hold");
                    report.Expect($"{step}: effect matches the winner", decided == "Scrap" ? $"Hold {Invariants.Q(holdBefore - rejected)}, RTVs 0" : $"Hold {Invariants.Q(holdBefore)}, RTVs 1",
                        $"Hold {Invariants.Q(await Coil("Hold"))}, RTVs {rtvs.ToString(CultureInfo.InvariantCulture)}", "Concurrency");
                    break;
                }

                case "ApproveRace":
                {
                    var rejected = ev.Number("rejected");
                    var id = await RejectionAsync(ev.Number("quantity"), rejected, step);
                    report.Expect($"{step}: use as is decided", "200", (await f.Api.Send(qe, $"/api/inventory/rejections/{id}/disposition", new { disposition = "UseAsIs", reason = "Cosmetic" })).Label);
                    var releasedBefore = await Coil("Released");
                    report.Rule(Basis.Spec, "ADR 0035 §4: use as is is released once, by one second person; a concurrent second approval is refused");
                    var outcomes = await Task.WhenAll(
                        f.Api.Send(qm, $"/api/inventory/rejections/{id}/approve", new { reason = "Agreed" }),
                        f.Api.Send(f.Api.As("qm2"), $"/api/inventory/rejections/{id}/approve", new { reason = "Agreed too" }));
                    report.Expect($"{step}: one approval wins", "1 won, 1 refused, 0 other", Tally(outcomes), "Concurrency");
                    report.Expect($"{step}: released exactly once", releasedBefore + rejected, await Coil("Released"));
                    break;
                }
            }

            await Invariants.Inventory(report, step, stores, parts);
        }

        await Invariants.AuditChain(report, "End", f.Plant.Admin);
        report.Note("Statuses of every racing command are in calls.jsonl; a refusal may be a 422 rule or a 409 conflict, never a second effect.");
        return (report, log, await Invariants.StateHash(stores, parts));
    }
}
