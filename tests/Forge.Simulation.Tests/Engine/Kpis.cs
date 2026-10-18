using System.Globalization;
using System.Text.Json;

namespace Forge.Simulation.Tests.Engine;

/// <summary>One labelled reading of the factory's KPIs, keyed "module.subject.measure[.window]".</summary>
public sealed record KpiSnapshot(string Label, TimeSpan At, IReadOnlyDictionary<string, decimal> Values)
{
    public decimal Get(string key) => Values.TryGetValue(key, out var v) ? v : 0m;
}

/// <summary>
/// Reads KPIs from Forge's own engines and reports, never from the simulation's bookkeeping: executable capacity per
/// machine activity and qualified-labour hours over 7 and 30 days, operation readiness, the delivery outcome of a
/// proposed schedule, open planning constraints and stock by status. The planning engines cap their horizon at 62
/// days, so 90-day consequences are a Phase C reading after advancing the clock.
/// Money is not measured (owner decision: quantities and time only).
/// </summary>
internal static class Kpis
{
    public static readonly int[] Windows = [7, 30];

    /// <summary>A reading with no values, the starting point before the first real reading.</summary>
    public static KpiSnapshot Empty { get; } = new("none", TimeSpan.Zero, new Dictionary<string, decimal>(StringComparer.Ordinal));

    public static async Task<KpiSnapshot> CollectAsync(SimFactory f, string label, bool proposeSchedule = true, params string[] parts)
    {
        var v = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
        var now = f.Clock.GetUtcNow();
        var planner = f.Planner;
        foreach (var days in Windows)
        {
            var w = days.ToString(CultureInfo.InvariantCulture) + "d";
            var capacity = (await SimApi.Get(planner, $"/api/planning/capacity?from={Iso(now)}&to={Iso(now.AddDays(days))}")).GetProperty("output");
            foreach (var m in capacity.GetProperty("machines").EnumerateArray())
            {
                var key = $"capacity.{m.GetProperty("machineCode").GetString()}.{m.GetProperty("activityCode").GetString()}";
                v[$"{key}.executableHours.{w}"] = m.GetProperty("executableHours").GetDecimal();
                v[$"{key}.workingHours.{w}"] = m.GetProperty("workingHours").GetDecimal();
            }

            foreach (var q in capacity.GetProperty("qualifiedLabor").EnumerateArray().GroupBy(q => q.GetProperty("qualification").GetString()!, StringComparer.Ordinal))
            {
                v[$"labor.{q.Key}.hours.{w}"] = q.Sum(d => d.GetProperty("hours").GetDecimal());
            }
        }

        var operations = (await SimApi.Get(planner, "/api/readiness/operations")).EnumerateArray().ToList();
        v["readiness.operations.total"] = operations.Count;
        v["readiness.operations.notExecutable"] = operations.Count(o => !o.GetProperty("executable").GetBoolean());
        foreach (var g in operations.Where(o => !o.GetProperty("executable").GetBoolean())
                     .GroupBy(o => o.TryGetProperty("primaryConstraint", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "Unknown", StringComparer.Ordinal))
        {
            v[$"readiness.blockedBy.{g.Key}"] = g.Count();
        }

        if (proposeSchedule)
        {
            await f.Api.Send(planner, "/api/planning/schedules", new { horizonDays = 30, reason = "Simulation KPI reading: " + label });
            var result = (await SimApi.Get(planner, "/api/planning/schedules/proposed")).GetProperty("result");
            var orders = result.GetProperty("orders").EnumerateArray().ToList();
            v["schedule.orders.total"] = orders.Count;
            foreach (var status in new[] { "OnTime", "AtRisk", "Late", "Unschedulable" })
            {
                v[$"schedule.orders.{status}"] = orders.Count(o => o.GetProperty("status").GetString() == status);
            }

            v["schedule.lateHours"] = Math.Round(orders.Where(o => o.TryGetProperty("projectedCompletion", out var p) && p.ValueKind == JsonValueKind.String)
                .Sum(o => (decimal)Math.Max(0, (o.GetProperty("projectedCompletion").GetDateTimeOffset() - o.GetProperty("dueAt").GetDateTimeOffset()).TotalHours)), 2);
            foreach (var c in (await SimApi.Get(planner, "/api/planning/constraints")).EnumerateArray().GroupBy(c => c.GetProperty("category").GetString()!, StringComparer.Ordinal))
            {
                v[$"constraints.open.{c.Key}"] = c.Count();
            }
        }

        foreach (var (bucket, quantity) in await Invariants.ReportedBalances(f.StoresUser, parts.Length == 0 ? [SimFactory.BulkPart] : parts))
        {
            var key = $"inventory.{bucket.Part}.{bucket.Status}";
            v[key] = v.GetValueOrDefault(key) + quantity;
        }

        return new KpiSnapshot(label, f.Clock.Elapsed, v);
    }

    /// <summary>The KPIs that moved between two readings: key, before, after.</summary>
    public static IReadOnlyList<(string Key, decimal Before, decimal After)> Changed(KpiSnapshot before, KpiSnapshot after) =>
        before.Values.Keys.Union(after.Values.Keys).Order(StringComparer.Ordinal)
            .Select(k => (Key: k, Before: before.Get(k), After: after.Get(k)))
            .Where(x => x.Before != x.After)
            .ToList();

    private static string Iso(DateTimeOffset t) => Uri.EscapeDataString(t.ToString("o", CultureInfo.InvariantCulture));
}
