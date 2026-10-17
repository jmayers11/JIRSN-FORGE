using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Forge.Simulation.Tests.Engine;

/// <summary>One stock bucket: part, location, lot and quality status.</summary>
public readonly record struct Bucket(string Part, string Location, string? Lot, string Status)
{
    public override string ToString() => $"{Part}@{Location}/{Lot ?? "-"}/{Status}";
}

/// <summary>
/// System-wide checks that must hold after every step, whatever the scenario. They read through the public API.
/// Inventory conservation is recomputed independently from the ledger and compared with the balances Forge reports.
/// </summary>
public static class Invariants
{
    /// <summary>Every ledger movement for the parts given: from-bucket minus, to-bucket plus.</summary>
    public static async Task<Dictionary<Bucket, decimal>> LedgerBalances(HttpClient reader, IEnumerable<string> parts)
    {
        var sums = new Dictionary<Bucket, decimal>();
        foreach (var part in parts)
        {
            var ledger = await SimApi.Get(reader, $"/api/inventory/transactions?part={Uri.EscapeDataString(part)}&take=100000");
            foreach (var t in ledger.EnumerateArray())
            {
                var quantity = t.GetProperty("quantity").GetDecimal();
                var lot = t.TryGetProperty("lotNumber", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
                if (Text(t, "fromLocation") is { } from && Text(t, "fromStatus") is { } fromStatus)
                {
                    Add(sums, new Bucket(part, from, lot, fromStatus), -quantity);
                }

                if (Text(t, "toLocation") is { } to && Text(t, "toStatus") is { } toStatus)
                {
                    Add(sums, new Bucket(part, to, lot, toStatus), quantity);
                }
            }
        }

        foreach (var zero in sums.Where(kv => kv.Value == 0m).Select(kv => kv.Key).ToList())
        {
            sums.Remove(zero);
        }

        return sums;
    }

    /// <summary>The balances Forge reports for the parts given.</summary>
    public static async Task<Dictionary<Bucket, decimal>> ReportedBalances(HttpClient reader, IEnumerable<string> parts)
    {
        var map = new Dictionary<Bucket, decimal>();
        foreach (var part in parts)
        {
            foreach (var b in (await SimApi.Get(reader, $"/api/inventory/stock?part={Uri.EscapeDataString(part)}")).EnumerateArray())
            {
                var lot = b.TryGetProperty("lotNumber", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
                Add(map, new Bucket(b.GetProperty("partNumber").GetString()!, b.GetProperty("locationCode").GetString()!, lot, b.GetProperty("status").GetString()!),
                    b.GetProperty("quantity").GetDecimal());
            }
        }

        return map;
    }

    /// <summary>
    /// Conservation and non-negativity: the balances Forge reports equal a recomputation of its own ledger, and no
    /// bucket is negative. Records one result per check in the report.
    /// </summary>
    public static async Task Inventory(SimReport report, string step, HttpClient reader, IReadOnlyCollection<string> parts)
    {
        var ledger = await LedgerBalances(reader, parts);
        var reported = await ReportedBalances(reader, parts);
        var mismatches = ledger.Keys.Union(reported.Keys)
            .Where(k => ledger.GetValueOrDefault(k) != reported.GetValueOrDefault(k))
            .Select(k => $"{k}: ledger {Q(ledger.GetValueOrDefault(k))} vs reported {Q(reported.GetValueOrDefault(k))}")
            .ToList();
        report.Expect($"{step}: balances equal the recomputed ledger", "no differences", mismatches.Count == 0 ? "no differences" : string.Join("; ", mismatches), "Invariant");
        var negative = reported.Where(kv => kv.Value < 0m).Select(kv => $"{kv.Key} = {Q(kv.Value)}").ToList();
        report.Expect($"{step}: no negative stock", "none", negative.Count == 0 ? "none" : string.Join("; ", negative), "Invariant");
    }

    /// <summary>The tamper-evident audit chain is intact.</summary>
    public static async Task AuditChain(SimReport report, string step, HttpClient auditor)
    {
        var verify = await SimApi.Get(auditor, "/api/kernel/audit/verify");
        var intact = verify.TryGetProperty("intact", out var i) && i.GetBoolean();
        report.Expect($"{step}: audit chain intact", "intact", intact ? "intact" : verify.GetRawText(), "Invariant");
    }

    /// <summary>
    /// A hash of the business state that must be identical for the same seed: balances plus the ledger's
    /// (kind, part, lot, quantity, from status, to status) multiset. Identifiers and timestamps are excluded.
    /// </summary>
    public static async Task<string> StateHash(HttpClient reader, IReadOnlyCollection<string> parts)
    {
        var lines = new List<string>();
        foreach (var (bucket, quantity) in await ReportedBalances(reader, parts))
        {
            lines.Add("B " + bucket + " " + Q(quantity));
        }

        foreach (var part in parts)
        {
            foreach (var t in (await SimApi.Get(reader, $"/api/inventory/transactions?part={Uri.EscapeDataString(part)}&take=100000")).EnumerateArray())
            {
                lines.Add(string.Join(' ', "T", Text(t, "kind"), part, Text(t, "lotNumber") ?? "-", Q(t.GetProperty("quantity").GetDecimal()),
                    Text(t, "fromStatus") ?? "-", Text(t, "toStatus") ?? "-"));
            }
        }

        lines.Sort(StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
    }

    public static string Q(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static void Add(Dictionary<Bucket, decimal> map, Bucket key, decimal quantity) =>
        map[key] = map.GetValueOrDefault(key) + quantity;
}
