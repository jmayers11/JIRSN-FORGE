using System.Globalization;
using System.Text;
using Forge.Simulation.Tests.Engine;

namespace Forge.Simulation.Tests.Factory;

/// <summary>One executed factory run: what it did, what it checked, its API timings and its wall time.</summary>
internal sealed record FactoryResult(string Name, FactoryRun Run, SimReport Report, IReadOnlyList<Timing> Timings, TimeSpan WallTime, string StateHash);

/// <summary>
/// Writes the Phase C deliverable from executed runs only: the amendment's outputs 1 to 10 in one page
/// (.forge-logs/simulation/factory/factory-report.md), with the day-by-day tables, KPI comparison and timings as CSV.
/// Nothing here is estimated: every number comes from a run's own records, Forge's answers or the API timings.
/// </summary>
internal static class FactoryReport
{
    /// <summary>The KPIs the consequence tables lead with.</summary>
    private static readonly string[] Headline =
    [
        "sim.demand.arrived", "sim.demand.shippedComplete", "sim.demand.shippedOnTime", "sim.demand.pastDueOpen", "sim.demand.unitsShipped",
        "sim.orders.created", "sim.orders.completed", "sim.orders.completedLate", "sim.orders.open", "sim.scrap.units", "sim.quality.ncrs",
        "sim.segments.planned", "sim.segments.started", "sim.segments.startRefused", "sim.segments.skipped",
        "schedule.orders.OnTime", "schedule.orders.AtRisk", "schedule.orders.Late", "schedule.orders.Unschedulable",
        "readiness.operations.notExecutable", "inventory.bought.Released", "inventory.intermediate.Released", "inventory.finished.Released",
    ];

    public static string Write(FactorySpec spec, FactoryResult baseline, FactoryResult disrupted, FactoryDisruptions library)
    {
        var folder = Path.Combine(SimReport.OutputRoot(), "factory", "seed-" + spec.Size.Seed.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "baseline-days.csv"), baseline.Run.DaysCsv());
        File.WriteAllText(Path.Combine(folder, "disrupted-days.csv"), disrupted.Run.DaysCsv());
        File.WriteAllText(Path.Combine(folder, "kpi-comparison.csv"), ComparisonCsv(baseline, disrupted));
        File.WriteAllText(Path.Combine(folder, "timings.csv"), TimingsCsv(baseline.Timings.Concat(disrupted.Timings)));

        var md = new StringBuilder();
        md.Append("# V5 simulation Phase C: full-factory results\n\n");
        md.Append("Executed runs only. Seed ").Append(I(spec.Size.Seed)).Append("; ").Append(spec.Size.ToString()).Append(". Two runs on the same seed and the same organization: ")
          .Append("a **baseline** (normal operation) and a **disrupted** run (the same, plus the disruption library in §3). Wall time: baseline ")
          .Append(Minutes(baseline.WallTime)).Append(", disrupted ").Append(Minutes(disrupted.WallTime)).Append(". Money is not measured (owner decision: quantities and time only).\n\n");

        Section1Coverage(md, baseline, disrupted);
        Section2Factory(md, spec, baseline);
        Section3Library(md, library);
        Section4Impact(md, disrupted);
        Section5Comparison(md, baseline, disrupted);
        Section6Consequences(md, baseline, disrupted, library);
        Section7Expectations(md, baseline, disrupted);
        Section8Defects(md, baseline, disrupted);
        Section9Performance(md, baseline, disrupted);
        Section10Scorecard(md, baseline, disrupted);

        var path = Path.Combine(folder, "factory-report.md");
        File.WriteAllText(path, md.ToString());
        return path;
    }

    // 1 ------------------------------------------------------------------------------------------------
    private static void Section1Coverage(StringBuilder md, FactoryResult baseline, FactoryResult disrupted)
    {
        md.Append("## 1. Coverage inventory\n\nEach of the amendment's 26 areas: whether the factory runs exercised it through the real API, and the Phase A/B scenarios that cover it individually (I).\n\n");
        md.Append("| Area | Baseline | Disrupted | Scenario (I) |\n|---|---|---|---|\n");
        foreach (var area in Coverage.Areas)
        {
            var scenarios = string.Join(", ", Coverage.Subject.Where(kv => kv.Value == area).Select(kv => kv.Key[..3]).Order(StringComparer.Ordinal));
            md.Append("| ").Append(area).Append(" | ").Append(baseline.Report.Covered.Contains(area) ? "yes" : "-").Append(" | ")
              .Append(disrupted.Report.Covered.Contains(area) ? "yes" : "-").Append(" | ").Append(scenarios.Length == 0 ? "-" : scenarios).Append(" |\n");
        }

        var missing = Coverage.Areas.Where(a => !baseline.Report.Covered.Contains(a) && !disrupted.Report.Covered.Contains(a)).ToList();
        md.Append("\nNot exercised in the factory (scenario level only): ").Append(missing.Count == 0 ? "none" : string.Join("; ", missing)).Append(".\n\n");
    }

    // 2 ------------------------------------------------------------------------------------------------
    private static void Section2Factory(StringBuilder md, FactorySpec spec, FactoryResult baseline)
    {
        md.Append("## 2. Factory and event generator\n\n");
        foreach (var line in spec.Describe().Split('\n'))
        {
            md.Append("- ").Append(line).Append('\n');
        }

        md.Append("\n| Department | Work | Shifts | People | Planned load |\n|---|---|---|---|---|\n");
        foreach (var d in spec.Departments)
        {
            md.Append("| ").Append(d.Area).Append(" | ").Append(d.MachineWork ? string.Join(", ", d.Machines) : "crew work (" + d.Activity + ")").Append(" | ").Append(string.Join("+", d.Shifts))
              .Append(" | ").Append(string.Join(", ", spec.Workers.Where(w => w.Department == d.Area).GroupBy(w => w.Shift).Select(g => g.Key + " " + I(g.Count()))))
              .Append(" | ").Append(Pct(spec.PlannedLoad.GetValueOrDefault(d.Area))).Append(" |\n");
        }

        md.Append("\nGenerator: the organization, the demand stream (arrival day, customer, product, quantity, need date, priority) and every random decision ")
          .Append("(scrap, test failure) are derived from the seed. A decision is keyed by what it is about (for example `scrap/PROD-03/order/sequence`), so the baseline and ")
          .Append("the disrupted run make the same decision for the same work and differ only where a disruption changes the work. The run follows Forge's approved schedule ")
          .Append("each day: people clock in for their shift, material is issued, every scheduled segment is started and reported by the operator Forge booked, tested work is ")
          .Append("inspected, finished orders are received and shipped. Build time ").Append(Minutes(baseline.Run.Plant.SetupWallTime)).Append(".\n\n");
    }

    // 3 ------------------------------------------------------------------------------------------------
    private static void Section3Library(StringBuilder md, FactoryDisruptions library)
    {
        md.Append("## 3. Scenario library\n\nThe disrupted run's events, applied in the morning by the person who would do it. The 27 individual scenarios (S01-S27, Phases A and B) remain the scenario library for each area; these are the factory-scale events.\n\n");
        md.Append("| Day | Event | Starts in (I) | Expected consequences (X) |\n|---|---|---|---|\n");
        foreach (var e in library.Plan.OrderBy(e => e.Day))
        {
            md.Append("| ").Append(I(e.Day + 1)).Append(" | ").Append(SimReport.Cell(e.Name)).Append(" | ").Append(e.Area).Append(" | ").Append(SimReport.Cell(e.Expected)).Append(" |\n");
        }

        md.Append('\n');
    }

    // 4 ------------------------------------------------------------------------------------------------
    private static void Section4Impact(StringBuilder md, FactoryResult disrupted)
    {
        md.Append("## 4. Impact matrix\n\nFor each disruption day: how many readings moved in each module, between a reading just before and just after (proposed schedule outcome, 7-day capacity and labour, readiness blockers).\n\n");
        var modules = disrupted.Report.Impacts.Select(i => i.Module).Distinct().Order(StringComparer.Ordinal).ToList();
        if (modules.Count == 0)
        {
            md.Append("No disruption moved a reading.\n\n");
            return;
        }

        md.Append("| Event | ").Append(string.Join(" | ", modules)).Append(" |\n|---|").Append(string.Concat(modules.Select(_ => "---|"))).Append('\n');
        foreach (var g in disrupted.Report.Impacts.GroupBy(i => i.Event))
        {
            md.Append("| ").Append(SimReport.Cell(g.Key)).Append(" | ").Append(string.Join(" | ", modules.Select(m => g.Count(i => i.Module == m) is var c && c > 0 ? I(c) : "-"))).Append(" |\n");
        }

        md.Append("\nLargest moves per event:\n\n");
        foreach (var g in disrupted.Report.Impacts.GroupBy(i => i.Event))
        {
            md.Append("- **").Append(SimReport.Cell(g.Key)).Append("**: ")
              .Append(string.Join("; ", g.OrderByDescending(i => Math.Abs(i.After - i.Before)).Take(5).Select(i => $"{i.Key} {Q(i.Before)} → {Q(i.After)}"))).Append('\n');
        }

        md.Append('\n');
    }

    // 5 ------------------------------------------------------------------------------------------------
    private static void Section5Comparison(StringBuilder md, FactoryResult baseline, FactoryResult disrupted)
    {
        md.Append("## 5. Baseline vs scenario KPI comparison\n\nAt each reading (end of the day named). Full table: `kpi-comparison.csv`.\n\n");
        var pairs = baseline.Run.Checkpoints.Zip(disrupted.Run.Checkpoints).ToList();
        md.Append("| KPI | ").Append(string.Join(" | ", pairs.Select(p => p.First.Label))).Append(" |\n|---|").Append(string.Concat(pairs.Select(_ => "---|"))).Append('\n');
        foreach (var key in Headline)
        {
            md.Append("| ").Append(key).Append(" | ")
              .Append(string.Join(" | ", pairs.Select(p => p.First.Get(key) == p.Second.Get(key) ? Q(p.First.Get(key)) : $"{Q(p.First.Get(key))} / **{Q(p.Second.Get(key))}**"))).Append(" |\n");
        }

        md.Append("\nCells show baseline / **disrupted** where they differ.\n\n");
    }

    // 6 ------------------------------------------------------------------------------------------------
    private static void Section6Consequences(StringBuilder md, FactoryResult baseline, FactoryResult disrupted, FactoryDisruptions library)
    {
        md.Append("## 6. Immediate, 7, 30 and 90-day consequences\n\n");
        md.Append("Immediate: §4 (before and after each event). Later consequences: the disrupted run minus the baseline at each reading, every KPI that differs, largest first.\n\n");
        foreach (var (b, d) in baseline.Run.Checkpoints.Zip(disrupted.Run.Checkpoints))
        {
            var changed = Kpis.Changed(b, d).OrderByDescending(x => Math.Abs(x.After - x.Before)).ToList();
            var events = library.Plan.Count(e => TimeSpan.FromDays(e.Day).Add(TimeSpan.FromHours(6)) <= d.At);
            md.Append("- **").Append(b.Label).Append("** (").Append(I(events)).Append(" events so far): ")
              .Append(changed.Count == 0 ? "no difference from the baseline" : string.Join("; ", changed.Take(12).Select(x => $"{x.Key} {Q(x.Before)} → {Q(x.After)} ({Signed(x.After - x.Before)})")))
              .Append('\n');
        }

        static (int Shipped, int OnTime, int PastDue) Delivery(FactoryRun r) =>
            (r.Demands.Count(x => x.CompletedDay is not null), r.Demands.Count(x => x.CompletedDay is { } c && c <= x.NeedDay), r.Demands.Count(x => x.Open && x.NeedDay < r.Spec.Size.Days - 1));
        var (bs, bo, bp) = Delivery(baseline.Run);
        var (ds, dOn, dp) = Delivery(disrupted.Run);
        md.Append("\nAt the end of the run: customer orders shipped complete ").Append(I(bs)).Append(" → ").Append(I(ds)).Append(", of them on time ").Append(I(bo)).Append(" → ").Append(I(dOn))
          .Append(", open past their need date ").Append(I(bp)).Append(" → ").Append(I(dp)).Append(".\n\n");
    }

    // 7 ------------------------------------------------------------------------------------------------
    private static void Section7Expectations(StringBuilder md, FactoryResult baseline, FactoryResult disrupted)
    {
        md.Append("## 7. Expected vs actual\n\nEvery check the runs made (full tables in each run's `results.md`).\n\n| Run | Basis | Category | Checks | Passed | Failed |\n|---|---|---|---|---|---|\n");
        foreach (var r in new[] { baseline, disrupted })
        {
            foreach (var g in r.Report.Expectations.GroupBy(e => (e.Basis, e.Category)).OrderBy(g => g.Key.Basis, StringComparer.Ordinal).ThenBy(g => g.Key.Category, StringComparer.Ordinal))
            {
                md.Append("| ").Append(r.Name).Append(" | ").Append(g.Key.Basis).Append(" | ").Append(g.Key.Category).Append(" | ").Append(I(g.Count())).Append(" | ")
                  .Append(I(g.Count(e => e.Passed))).Append(" | ").Append(g.Count(e => !e.Passed) is var f && f > 0 ? "**" + I(f) + "**" : "0").Append(" |\n");
            }
        }

        var failures = baseline.Report.Failures.Select(f => (Run: baseline.Name, f)).Concat(disrupted.Report.Failures.Select(f => (Run: disrupted.Name, f))).ToList();
        md.Append('\n').Append(failures.Count == 0 ? "No check failed.\n\n" : "Failed checks:\n\n");
        foreach (var (run, f) in failures.Take(40))
        {
            md.Append("- ").Append(run).Append(" #").Append(I(f.Number)).Append(' ').Append(SimReport.Cell(f.Name)).Append(": expected `").Append(SimReport.Cell(f.Expected)).Append("`, actual `").Append(SimReport.Cell(f.Actual)).Append("`\n");
        }

        md.Append('\n');
    }

    // 8 ------------------------------------------------------------------------------------------------
    private static void Section8Defects(StringBuilder md, FactoryResult baseline, FactoryResult disrupted)
    {
        md.Append("## 8. Defect register\n\nCandidates only; each needs triage against the specification before it is called a defect. Evidence: the run's `calls.jsonl` (every refused or failed command, with its idempotency key).\n\n");
        var n = 0;
        md.Append("| # | Run | Kind | Count | What | Evidence key |\n|---|---|---|---|---|---|\n");
        foreach (var r in new[] { baseline, disrupted })
        {
            foreach (var e in r.Run.ServerErrors.Take(20))
            {
                md.Append("| ").Append(I(++n)).Append(" | ").Append(r.Name).Append(" | Server error | 1 | ").Append(SimReport.Cell(e)).Append(" | - |\n");
            }

            foreach (var f in r.Report.Failures)
            {
                md.Append("| ").Append(I(++n)).Append(" | ").Append(r.Name).Append(" | Failed check (").Append(f.Basis).Append(") | 1 | ").Append(SimReport.Cell(f.Name + ": " + f.Actual)).Append(" | - |\n");
            }

            foreach (var v in r.Run.ScheduleViolationExamples)
            {
                md.Append("| ").Append(I(++n)).Append(" | ").Append(r.Name).Append(" | Infeasible schedule | - | ").Append(SimReport.Cell(v)).Append(" | - |\n");
            }
        }

        md.Append("\n### Refused commands (schedule and floor disagreements)\n\nForge approved a schedule and then refused part of it, or the organization's routine was refused. Each kind is a candidate until triaged: some are correct refusals of a disrupted plant.\n\n");
        md.Append("| Command and answer | Baseline | Disrupted | Example key |\n|---|---|---|---|\n");
        foreach (var kind in baseline.Run.Refusals.Keys.Union(disrupted.Run.Refusals.Keys).Order(StringComparer.Ordinal))
        {
            md.Append("| ").Append(SimReport.Cell(kind)).Append(" | ").Append(I(baseline.Run.Refusals.GetValueOrDefault(kind))).Append(" | ").Append(I(disrupted.Run.Refusals.GetValueOrDefault(kind)))
              .Append(" | ").Append(disrupted.Run.RefusalExample.GetValueOrDefault(kind) ?? baseline.Run.RefusalExample.GetValueOrDefault(kind) ?? "-").Append(" |\n");
        }

        md.Append("\n### Orders the last schedule could not place\n\nBy the constraint Forge named for each order's first unplaced operation.\n\n| Constraint | Baseline | Disrupted | Forge's explanation (example) |\n|---|---|---|---|\n");
        foreach (var why in baseline.Run.Unschedulable.Keys.Union(disrupted.Run.Unschedulable.Keys).Order(StringComparer.Ordinal))
        {
            md.Append("| ").Append(why).Append(" | ").Append(I(baseline.Run.Unschedulable.GetValueOrDefault(why))).Append(" | ").Append(I(disrupted.Run.Unschedulable.GetValueOrDefault(why)))
              .Append(" | ").Append(SimReport.Cell(disrupted.Run.UnschedulableExample.GetValueOrDefault(why) ?? baseline.Run.UnschedulableExample.GetValueOrDefault(why) ?? "-")).Append(" |\n");
        }

        var findings = baseline.Report.Findings.Concat(disrupted.Report.Findings).Distinct().ToList();
        md.Append("\n### Findings\n\n").Append(findings.Count == 0 ? "- None recorded by the runs.\n" : string.Concat(findings.Select(f => "- " + f + "\n")));
        md.Append("- Workforce moves (ADR 0057 §5): the week in Packing is a temporary move with an estimated end date; projected capacity counts the movers in Packing until then and back in Assembly afterwards. The shift change and assignment are still separate commands.\n\n");
    }

    // 9 ------------------------------------------------------------------------------------------------
    private static void Section9Performance(StringBuilder md, FactoryResult baseline, FactoryResult disrupted)
    {
        md.Append("## 9. Performance, concurrency, resilience and recovery\n\n");
        md.Append("In-process host on a disposable PostgreSQL database on the owner's PC; times are the API's response times as the client saw them. Full table: `timings.csv`.\n\n");
        var all = baseline.Timings.Concat(disrupted.Timings).ToList();
        md.Append("Requests: ").Append(I(all.Count)).Append(" (baseline ").Append(I(baseline.Timings.Count)).Append(", disrupted ").Append(I(disrupted.Timings.Count)).Append(").\n\n");
        md.Append("| Endpoint | Method | Requests | p50 ms | p95 ms | p99 ms | max ms |\n|---|---|---|---|---|---|---|\n");
        foreach (var g in all.GroupBy(t => (t.Group, t.Method)).OrderByDescending(g => g.Count()).Take(30))
        {
            var ms = g.Select(t => t.Milliseconds).Order().ToList();
            md.Append("| ").Append(g.Key.Group).Append(" | ").Append(g.Key.Method).Append(" | ").Append(I(ms.Count)).Append(" | ").Append(F(P(ms, 0.5))).Append(" | ").Append(F(P(ms, 0.95)))
              .Append(" | ").Append(F(P(ms, 0.99))).Append(" | ").Append(F(ms[^1])).Append(" |\n");
        }

        md.Append("\nSlowest endpoints by p95 (at least 10 requests):\n\n");
        foreach (var g in all.GroupBy(t => (t.Group, t.Method)).Where(g => g.Count() >= 10).OrderByDescending(g => P(g.Select(t => t.Milliseconds).Order().ToList(), 0.95)).Take(8))
        {
            md.Append("- ").Append(g.Key.Method).Append(' ').Append(g.Key.Group).Append(": p95 ").Append(F(P(g.Select(t => t.Milliseconds).Order().ToList(), 0.95))).Append(" ms over ").Append(I(g.Count())).Append(" requests\n");
        }

        foreach (var r in new[] { baseline, disrupted })
        {
            var days = r.Run.Days.Where(d => d.ScheduledOrders > 0).ToList();
            if (days.Count > 0)
            {
                var ms = days.Select(d => d.ScheduleMilliseconds).Order().ToList();
                md.Append('\n').Append(r.Name).Append(": daily schedule proposals for ").Append(I(days.Min(d => d.ScheduledOrders))).Append('-').Append(I(days.Max(d => d.ScheduledOrders)))
                  .Append(" released orders took p50 ").Append(F(P(ms, 0.5))).Append(" ms, max ").Append(F(ms[^1])).Append(" ms (horizon ").Append(I(FactoryRun.HorizonDays)).Append(" days).");
            }
        }

        md.Append("\n\n| Check | Baseline | Disrupted |\n|---|---|---|\n");
        md.Append("| Server errors (5xx) | ").Append(I(baseline.Run.ServerErrors.Count)).Append(" | ").Append(I(disrupted.Run.ServerErrors.Count)).Append(" |\n");
        md.Append("| Lost responses retried with the same key | ").Append(I(baseline.Run.NetworkResult.Retried)).Append(" | ").Append(I(disrupted.Run.NetworkResult.Retried)).Append(" |\n");
        md.Append("| Concurrent final reports (races / applied twice) | ").Append(I(baseline.Run.RaceResult.Races)).Append(" / ").Append(I(baseline.Run.RaceResult.Doubled)).Append(" | ")
          .Append(I(disrupted.Run.RaceResult.Races)).Append(" / ").Append(I(disrupted.Run.RaceResult.Doubled)).Append(" |\n");
        md.Append("| Reports applied exactly once (checked at every reading) | ").Append(Held(baseline, "each report applied once")).Append(" | ").Append(Held(disrupted, "each report applied once")).Append(" |\n");
        md.Append("| Stock conservation and no negative stock (every reading) | ").Append(Held(baseline, "balances equal")).Append(" / ").Append(Held(baseline, "no negative stock")).Append(" | ")
          .Append(Held(disrupted, "balances equal")).Append(" / ").Append(Held(disrupted, "no negative stock")).Append(" |\n");
        md.Append("| Audit chain intact at the end | ").Append(Held(baseline, "audit chain")).Append(" | ").Append(Held(disrupted, "audit chain")).Append(" |\n");
        md.Append("| State hash at the end | `").Append(baseline.StateHash[..12]).Append("` | `").Append(disrupted.StateHash[..12]).Append("` |\n");
        md.Append("\nRecovery is exercised at scenario level: S17 restores a backup and carries on, S18 starts a new host on the same database mid-shift. The factory runs keep one host per run.\n\n");
    }

    // 10 -----------------------------------------------------------------------------------------------
    private static void Section10Scorecard(StringBuilder md, FactoryResult baseline, FactoryResult disrupted)
    {
        md.Append("## 10. Readiness scorecard\n\n");
        var runs = new[] { baseline, disrupted };
        var invariantsHeld = runs.All(r => r.Report.Expectations.Where(e => e.Category == "Invariant").All(e => e.Passed));
        var allChecks = runs.All(r => !r.Report.Failures.Any());
        var noServerErrors = runs.All(r => r.Run.ServerErrors.Count == 0);
        var feasible = runs.All(r => r.Run.ScheduleViolations == 0);
        double Adherence(FactoryRun r) => r.Days.Sum(d => d.SegmentsPlanned) is var planned && planned == 0 ? 0 : (double)r.Days.Sum(d => d.SegmentsStarted) / planned;
        md.Append("| Gate | Result |\n|---|---|\n");
        md.Append("| Outputs 1-10 delivered from executed runs | yes (this page) |\n");
        md.Append("| Owner sizes (300 orders, 70 operators, 90 days) | ").Append(baseline.Run.Spec.Size is { Orders: >= 300, Operators: >= 70, Days: >= 90 } ? "yes" : "**no: " + baseline.Run.Spec.Size + "**").Append(" |\n");
        md.Append("| Invariants held (conservation, no negative stock, reports once, audit chain, schedule feasibility) | ").Append(invariantsHeld ? "yes" : "**no**").Append(" |\n");
        md.Append("| Every check passed | ").Append(allChecks ? "yes" : "**no**").Append(" |\n");
        md.Append("| No server errors | ").Append(noServerErrors ? "yes" : "**no**").Append(" |\n");
        md.Append("| Approved schedules feasible (independent check) | ").Append(feasible ? "yes" : "**no**").Append(" |\n");
        md.Append("| Scheduled segments started as booked | baseline ").Append(Pct((decimal)Adherence(baseline.Run))).Append(", disrupted ").Append(Pct((decimal)Adherence(disrupted.Run))).Append(" |\n");
        md.Append("\n| Area | Factory | Result |\n|---|---|---|\n");
        foreach (var area in Coverage.Areas)
        {
            var inFactory = runs.Any(r => r.Report.Covered.Contains(area));
            var individual = Coverage.Subject.Any(kv => kv.Value == area);
            md.Append("| ").Append(area).Append(" | ").Append(inFactory ? "exercised" : "-").Append(" | ")
              .Append(inFactory ? (allChecks ? "passing" : "see §7") : individual ? "scenario level only" : Coverage.NotImplemented.Any(n => n.Area == area) ? "not implemented in Forge" : "**not covered**").Append(" |\n");
        }

        md.Append('\n');
    }

    // helpers ------------------------------------------------------------------------------------------
    private static string ComparisonCsv(FactoryResult baseline, FactoryResult disrupted)
    {
        var sb = new StringBuilder("reading,kpi,baseline,disrupted,difference\n");
        foreach (var (b, d) in baseline.Run.Checkpoints.Zip(disrupted.Run.Checkpoints))
        {
            foreach (var key in b.Values.Keys.Union(d.Values.Keys).Order(StringComparer.Ordinal))
            {
                sb.Append(b.Label).Append(',').Append(key).Append(',').Append(Q(b.Get(key))).Append(',').Append(Q(d.Get(key))).Append(',').Append(Q(d.Get(key) - b.Get(key))).Append('\n');
            }
        }

        return sb.ToString();
    }

    private static string TimingsCsv(IEnumerable<Timing> timings)
    {
        var sb = new StringBuilder("endpoint,method,requests,p50ms,p95ms,p99ms,maxms,errors\n");
        foreach (var g in timings.GroupBy(t => (t.Group, t.Method)).OrderBy(g => g.Key.Group, StringComparer.Ordinal))
        {
            var ms = g.Select(t => t.Milliseconds).Order().ToList();
            sb.Append(g.Key.Group).Append(',').Append(g.Key.Method).Append(',').Append(I(ms.Count)).Append(',').Append(F(P(ms, 0.5))).Append(',').Append(F(P(ms, 0.95))).Append(',')
              .Append(F(P(ms, 0.99))).Append(',').Append(F(ms[^1])).Append(',').Append(I(g.Count(t => t.Status >= 500))).Append('\n');
        }

        return sb.ToString();
    }

    private static string Held(FactoryResult r, string fragment)
    {
        var checks = r.Report.Expectations.Where(e => e.Name.Contains(fragment, StringComparison.Ordinal)).ToList();
        return checks.Count == 0 ? "not checked" : checks.All(e => e.Passed) ? $"held ({I(checks.Count)})" : $"**{I(checks.Count(e => !e.Passed))} of {I(checks.Count)} failed**";
    }

    private static double P(List<double> sorted, double q) => sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Count) - 1, 0, sorted.Count - 1)];

    private static string F(double ms) => ms.ToString(ms < 10 ? "0.0" : "0", CultureInfo.InvariantCulture);

    private static string I(int n) => n.ToString(CultureInfo.InvariantCulture);

    private static string Q(decimal v) => Invariants.Q(v);

    private static string Signed(decimal v) => (v > 0 ? "+" : string.Empty) + Q(v);

    private static string Pct(decimal fraction) => (fraction * 100m).ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string Minutes(TimeSpan t) => t.TotalMinutes.ToString("0.0", CultureInfo.InvariantCulture) + " min";
}
