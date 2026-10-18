using System.Globalization;
using System.Net;
using Forge.Integration.Tests;
using Forge.Simulation.Tests.Engine;

namespace Forge.Simulation.Tests.Scenarios;

/// <summary>
/// Scenario 15 (network interruption): a client loses the response to a command and retries it, several terminals send
/// the same command at once, and a client reuses a key for a different command. Expected outcomes come from the
/// command contract (Idempotency-Key): one key is one effect; a retry returns the original result; the same key with a
/// different body is refused with 409; nothing is applied twice.
/// </summary>
internal static class NetworkInterruptionScenario
{
    public const string Name = "S15-network-interruption";

    /// <summary>The replay and conflict rules, written down by the owner's ruling (previously only on IIdempotencyStore).</summary>
    private const string IdempotencyContract = "ADR 0055 §1";

    public static EventLog Plan(int seed)
    {
        var log = new EventLog(Name, seed);
        var at = TimeSpan.Zero;
        log.Add(at += TimeSpan.FromMinutes(5), "LostReceipt", ("lot", "NET-1"), ("quantity", log.Between(5, 50)), ("retries", log.Between(1, 3)));
        log.Add(at += TimeSpan.FromMinutes(5), "ConcurrentReceipt", ("lot", "NET-2"), ("quantity", log.Between(5, 50)), ("terminals", 5));
        log.Add(at += TimeSpan.FromMinutes(5), "KeyReuse", ("lot", "NET-3"), ("quantity", log.Between(5, 50)));
        log.Add(at += TimeSpan.FromMinutes(5), "LostHold", ("lot", "NET-1"), ("retries", log.Between(1, 3)));
        return log;
    }

    public static async Task<(SimReport Report, EventLog Log, string Hash)> RunAsync(PostgresFixture postgres, int seed, Mutation mutation = Mutation.None)
    {
        var log = Plan(seed);
        var report = new SimReport(mutation == Mutation.None ? Name : Name + RunSummary.CanaryMarker + mutation, seed);
        await using var f = await SimFactory.BuildAsync(postgres, "s15-" + seed.ToString(CultureInfo.InvariantCulture));
        var (api, stores, qe) = (f.Api, f.StoresUser, f.Api.As("qe"));
        var parts = new[] { SimFactory.LotPart };

        object Receipt(string lot, decimal quantity) =>
            new { partNumber = SimFactory.LotPart, quantity, locationCode = SimFactory.Stores, lotNumber = lot, revision = "A", reason = "Simulated delivery " + lot };

        async Task<int> LedgerReceipts(string lot) =>
            (await SimApi.Get(stores, $"/api/inventory/transactions?part={SimFactory.LotPart}&take=100000")).EnumerateArray()
                .Count(t => t.GetProperty("kind").GetString() == "Receipt" && t.TryGetProperty("lotNumber", out var l) && l.GetString() == lot);

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
                case "LostReceipt":
                {
                    report.Rule(Basis.Spec, IdempotencyContract + ": a repeat with the same key and body returns the stored result and applies nothing again");
                    var key = api.NextKey();
                    var body = Receipt(lot, e.Number("quantity"));
                    await SimApi.SendAndLoseResponse(stores, "/api/inventory/receipts", body, key);
                    var ids = new HashSet<string?>(StringComparer.Ordinal);
                    for (var i = 0; i < (int)e.Number("retries"); i++)
                    {
                        var retry = await api.Send(stores, "/api/inventory/receipts", body, mutation == Mutation.RetryWithFreshKey ? api.NextKey() : key);
                        report.Expect($"{step}: retry {i + 1} returns the original result", "200", retry.Label, "Idempotency");
                        ids.Add(retry.EntityId);
                    }

                    report.Expect($"{step}: every retry names the same receipt", "1", ids.Count.ToString(CultureInfo.InvariantCulture), "Idempotency");
                    report.Expect($"{step}: exactly one receipt in the ledger", "1", (await LedgerReceipts(lot)).ToString(CultureInfo.InvariantCulture), "Idempotency");
                    report.Expect($"{step}: on hand", e.Number("quantity"), await OnHand(lot));
                    break;
                }

                case "ConcurrentReceipt":
                {
                    report.Rule(Basis.Spec, "ADR 0055 §2: simultaneous requests with one key take effect once; each caller gets the result or an in-progress conflict, never a second effect");
                    var key = api.NextKey();
                    var body = Receipt(lot, e.Number("quantity"));
                    var terminals = (int)e.Number("terminals");
                    var outcomes = await Task.WhenAll(Enumerable.Range(0, terminals).Select(_ => api.Send(api.As("stores"), "/api/inventory/receipts", body, key)));

                    // A retry while the first is still running may be told to wait (409), never applied twice.
                    var unexpected = outcomes.Where(o => !o.Succeeded && o.Status != HttpStatusCode.Conflict).Select(o => o.Label).ToList();
                    report.Expect($"{step}: every terminal either gets the result or is told the command is in progress", "none",
                        unexpected.Count == 0 ? "none" : string.Join(", ", unexpected), "Idempotency");
                    var ids = outcomes.Where(o => o.Succeeded).Select(o => o.EntityId).Distinct(StringComparer.Ordinal).Count();
                    report.Expect($"{step}: all successful terminals name one receipt", "1", ids.ToString(CultureInfo.InvariantCulture), "Idempotency");
                    report.Expect($"{step}: exactly one receipt in the ledger", "1", (await LedgerReceipts(lot)).ToString(CultureInfo.InvariantCulture), "Idempotency");
                    report.Expect($"{step}: on hand", e.Number("quantity"), await OnHand(lot));
                    report.Note($"Concurrent terminals for {lot}: " + string.Join(", ", outcomes.Select(o => o.Label)));
                    break;
                }

                case "KeyReuse":
                {
                    report.Rule(Basis.Spec, IdempotencyContract + ": a key reused for a different request is refused with 409 and changes nothing");
                    var key = api.NextKey();
                    var first = await api.Send(stores, "/api/inventory/receipts", Receipt(lot, e.Number("quantity")), key);
                    report.Expect($"{step}: first use of the key", "200", first.Label, "Idempotency");
                    var reused = await api.Send(stores, "/api/inventory/receipts", Receipt(lot, e.Number("quantity") + 1m), key);
                    report.Expect($"{step}: same key, different body is refused", "409", ((int)reused.Status).ToString(CultureInfo.InvariantCulture), "Idempotency");
                    report.Expect($"{step}: exactly one receipt in the ledger", "1", (await LedgerReceipts(lot)).ToString(CultureInfo.InvariantCulture), "Idempotency");
                    report.Expect($"{step}: on hand is the first quantity", e.Number("quantity"), await OnHand(lot));
                    break;
                }

                case "LostHold":
                {
                    report.Rule(Basis.Spec, IdempotencyContract + ": applies to every command, including holds");
                    var before = (await SimApi.Get(qe, "/api/holds")).EnumerateArray().Count();
                    var key = api.NextKey();
                    var body = new { kind = "Quality", targetType = "Lot", targetPart = SimFactory.LotPart, targetId = lot, source = "Simulation", reason = "Simulated hold with a lost response" };
                    await SimApi.SendAndLoseResponse(qe, "/api/holds", body, key);
                    for (var i = 0; i < (int)e.Number("retries"); i++)
                    {
                        var retry = await api.Send(qe, "/api/holds", body, key);
                        report.Expect($"{step}: retry {i + 1} returns the original result", "200", retry.Label, "Idempotency");
                    }

                    var after = (await SimApi.Get(qe, "/api/holds")).EnumerateArray().Count();
                    report.Expect($"{step}: exactly one new hold", "1", (after - before).ToString(CultureInfo.InvariantCulture), "Idempotency");
                    break;
                }
            }

            await Invariants.Inventory(report, step, stores, parts);
        }

        await Invariants.AuditChain(report, "End", f.Plant.Admin);
        if (mutation != Mutation.None)
        {
            report.Note("Negative control: " + mutation + ". This run is expected to fail.");
        }

        var hash = await Invariants.StateHash(stores, parts);
        return (report, log, hash);
    }
}
