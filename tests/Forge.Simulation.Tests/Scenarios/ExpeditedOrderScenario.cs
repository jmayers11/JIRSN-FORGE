using System.Globalization;
using System.Text.Json;
using Forge.Integration.Tests;
using Forge.Simulation.Tests.Engine;

namespace Forge.Simulation.Tests.Scenarios;

/// <summary>
/// Scenario 12 (expedited order): several ROLL-300 orders queue on SLITTER-B, the only machine that can run them. The last
/// one in the queue is expedited. A priority change needs a fresh preview: a stale preview (older than five minutes) and
/// a wrong expected priority are refused, and a person without order authority cannot change it. Once changed, the
/// expedited order finishes earlier and exactly the orders that were ahead of it on SLITTER-B finish later; nothing
/// else moves. Expectations come from ADR 0041 (priority governance) and the single-machine queue, never from Forge.
/// </summary>
internal static class ExpeditedOrderScenario
{
    public const string Name = "S12-expedited-order";
    private const string Adr = "ADR 0041";

    public static EventLog Plan(int seed)
    {
        var log = new EventLog(Name, seed);
        var count = log.Between(3, 5);
        for (var i = 0; i < count; i++)
        {
            log.Add(TimeSpan.FromMinutes(i), "Order", ("quantity", log.Between(60, 160)), ("dueInDays", 10 + i));
        }

        log.Add(TimeSpan.FromMinutes(10), "Baseline");
        log.Add(TimeSpan.FromMinutes(15), "StalePreview");
        log.Add(TimeSpan.FromMinutes(30), "Expedite");
        return log;
    }

    public static async Task<(SimReport Report, EventLog Log, string Hash)> RunAsync(PostgresFixture postgres, int seed, Mutation mutation = Mutation.None)
    {
        var log = Plan(seed);
        var report = new SimReport(mutation == Mutation.None ? Name : Name + RunSummary.CanaryMarker + mutation, seed);
        await using var f = await SimFactory.BuildPlanningAsync(postgres, "s12-" + seed.ToString(CultureInfo.InvariantCulture));
        report.Evidence(f.Api);
        report.Covers(Coverage.Scheduling, Coverage.Execution, Coverage.Demand);
        var planner = f.Planner;
        var parts = new[] { SimFactory.BulkPart };
        var orders = new List<long>();
        Dictionary<long, (DateTimeOffset? Start, DateTimeOffset? End)> before = new();
        KpiSnapshot previous = Kpis.Empty;
        long target = 0;

        async Task<Dictionary<long, (DateTimeOffset? Start, DateTimeOffset? End)>> ScheduleAsync()
        {
            await f.Api.Send(planner, "/api/planning/schedules", new { horizonDays = 30, reason = "Simulation reading" });
            var result = (await SimApi.Get(planner, "/api/planning/schedules/proposed")).GetProperty("result");
            var ends = result.GetProperty("orders").EnumerateArray().ToDictionary(o => o.GetProperty("orderNumber").GetInt64(),
                o => o.TryGetProperty("projectedCompletion", out var p) && p.ValueKind == JsonValueKind.String ? p.GetDateTimeOffset() : (DateTimeOffset?)null);
            var starts = result.GetProperty("operations").EnumerateArray().Where(o => o.TryGetProperty("start", out var s) && s.ValueKind == JsonValueKind.String)
                .GroupBy(o => o.GetProperty("orderNumber").GetInt64()).ToDictionary(g => g.Key, g => g.Min(o => o.GetProperty("start").GetDateTimeOffset()));
            return ends.ToDictionary(kv => kv.Key, kv => (starts.TryGetValue(kv.Key, out var s) ? (DateTimeOffset?)s : null, kv.Value));
        }

        async Task<Outcome> ChangeAsync(HttpClient who, JsonElement preview, int expected) =>
            await f.Api.Send(who, $"/api/orders/{target}/priority", new
            {
                priority = 1, expectedPriority = expected, previewAsOf = preview.GetProperty("asOf").GetDateTimeOffset(),
                previewFingerprint = preview.GetProperty("fingerprint").GetString(), reason = "Customer expedite",
            });

        JsonElement stale = default;
        var sinceStart = TimeSpan.Zero;
        foreach (var ev in log.Events)
        {
            f.Clock.Advance(ev.At - sinceStart);
            sinceStart = ev.At;
            var step = $"#{ev.Index} {ev.Type}";
            switch (ev.Type)
            {
                case "Order":
                    report.Rule(Basis.Plan, "Setup: released ROLL-300 orders, which only SLITTER-B can run");
                    orders.Add(await f.ReleasedOrderAsync(SimFactory.WidePart, ev.Number("quantity"), (int)ev.Number("dueInDays")));
                    break;

                case "Baseline":
                {
                    previous = report.Kpi(await Kpis.CollectAsync(f, "baseline"));
                    before = await ScheduleAsync();
                    report.Rule(Basis.Plan, "Plan: every order is scheduled on SLITTER-B");
                    report.Expect($"{step}: all orders scheduled", orders.Count.ToString(CultureInfo.InvariantCulture), before.Count(o => o.Value.Start is not null).ToString(CultureInfo.InvariantCulture));
                    target = orders.Where(before.ContainsKey).OrderByDescending(o => before[o].Start ?? DateTimeOffset.MinValue).First();
                    report.Note($"Expedited order: {target}, the last on SLITTER-B in the baseline schedule.");
                    break;
                }

                case "StalePreview":
                    report.Rule(Basis.Plan, "Setup: a planner previews the change");
                    stale = (await f.Api.Send(planner, $"/api/orders/{target}/priority-preview", new { priority = 1 })).Json;
                    break;

                case "Expedite":
                {
                    report.Rule(Basis.Spec, Adr + ": a preview older than five minutes is refused");
                    report.Expect($"{step}: stale preview refused", "422", ((int)(await ChangeAsync(planner, stale, 3)).Status).ToString(CultureInfo.InvariantCulture));
                    var fresh = await f.Api.Send(planner, $"/api/orders/{target}/priority-preview", new { priority = 1 });
                    report.Expect($"{step}: fresh preview", "200", fresh.Label);
                    report.Rule(Basis.Spec, Adr + ": the command requires the expected current priority");
                    report.Expect($"{step}: wrong expected priority refused", "422", ((int)(await ChangeAsync(planner, fresh.Json, 5)).Status).ToString(CultureInfo.InvariantCulture));
                    report.Rule(Basis.Spec, Adr + ": only order authority (execution.orders.manage) changes priority");
                    report.Expect($"{step}: material handler cannot change priority", "403", ((int)(await ChangeAsync(f.StoresUser, fresh.Json, 3)).Status).ToString(CultureInfo.InvariantCulture), "Authorization");
                    report.Rule(Basis.Spec, Adr + ": a reviewed change succeeds");
                    report.Expect($"{step}: priority changed", "200", (await ChangeAsync(planner, fresh.Json, 3)).Label);

                    var after = await ScheduleAsync();
                    var ahead = orders.Where(o => o != target && before[o].Start < before[target].Start).Order().ToList();
                    report.Rule(Basis.Spec, Adr + ": priority changes queue order only; on one machine the expedited order finishes earlier");
                    report.Expect($"{step}: expedited order finishes earlier", "True", after[target].End < before[target].End ? "True" : "False");
                    report.Rule(Basis.Spec, Adr + ": the displaced orders are exactly those that were ahead of it on SLITTER-B");
                    var later = orders.Where(o => o != target && after[o].End > before[o].End).Order().ToList();
                    report.Expect($"{step}: orders finishing later", string.Join(",", ahead), string.Join(",", later));
                    report.Expect($"{step}: the preview named every displaced order", "True",
                        ahead.All(o => fresh.Body.Contains(o.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)) ? "True" : "False");
                    var kpi = report.Kpi(await Kpis.CollectAsync(f, "after expedite"));
                    report.ImpactOf("Order expedited", previous, kpi);
                    break;
                }
            }

            await Invariants.Inventory(report, step, f.StoresUser, parts);
        }

        await Invariants.AuditChain(report, "End", f.Plant.Admin);
        return (report, log, await Invariants.StateHash(f.StoresUser, parts));
    }
}
