using System.Globalization;
using System.Text.Json;
using Forge.Integration.Tests;
using Forge.Simulation.Tests.Engine;

namespace Forge.Simulation.Tests.Scenarios;

/// <summary>
/// Scenario 1 (operator callout): Sarah, the only Slitting person with first aid, calls out. The two-person SLIT-AREA rule
/// then cannot be met, so both slitters lose executable time. A trainee is no cover. A reallocation preview names what
/// moving Jennifer would stop, and committing it trades paint capacity for slitting. When Sarah returns, everything
/// recovers. Expectations come from the reference plant's definitions (the SLIT-AREA rule, who holds which qualification,
/// a 24/7 shift) and the Phase 2/4 specification (§78 absence stops work with evidence, §79 preview without committing,
/// §81 capacity loses the absent hours), computed by the <see cref="Crew"/> oracle, never read back from Forge.
/// </summary>
internal static class OperatorCalloutScenario
{
    public const string Name = "S01-operator-callout";
    private const string Spec = "Phase 2/4 spec §78–§81 and the reference plant's SLIT-AREA rule";

    public static EventLog Plan(int seed)
    {
        var log = new EventLog(Name, seed);
        var hours = log.Between(6, 10);
        var at = TimeSpan.Zero;
        for (var i = 0; i < 3; i++)
        {
            log.Add(at, "Order", ("part", "ROLL-100"), ("quantity", log.Between(40, 160)), ("dueInDays", log.Between(6, 12)));
        }

        log.Add(at, "Order", ("part", "ROLL-300"), ("quantity", log.Between(30, 100)), ("dueInDays", log.Between(7, 12)));
        log.Add(at += TimeSpan.FromMinutes(5), "Baseline");
        log.Add(at += TimeSpan.FromMinutes(5), "CallOut", ("person", "sarah"), ("hours", hours));
        log.Add(at += TimeSpan.FromMinutes(10), "TraineeCover", ("person", "dana"));
        log.Add(at += TimeSpan.FromMinutes(10), "PreviewMove", ("person", "jennifer"));
        log.Add(at += TimeSpan.FromMinutes(10), "CommitMove", ("person", "jennifer"));
        log.Add(TimeSpan.FromMinutes(5 + hours * 60 + 15), "Return", ("person", "sarah"));
        log.Add(TimeSpan.FromMinutes(5 + hours * 60 + 30), "RestorePaint", ("person", "jennifer"));
        return log;
    }

    public static async Task<(SimReport Report, EventLog Log, string Hash)> RunAsync(PostgresFixture postgres, int seed, Mutation mutation = Mutation.None)
    {
        var log = Plan(seed);
        var report = new SimReport(mutation == Mutation.None ? Name : Name + RunSummary.CanaryMarker + mutation, seed);
        await using var f = await SimFactory.BuildPlanningAsync(postgres, "s01-" + seed.ToString(CultureInfo.InvariantCulture));
        report.Evidence(f.Api);
        report.Covers(Coverage.Workforce, Coverage.Safety, Coverage.Scheduling, Coverage.Reporting);
        var crew = new Crew(f.Clock.GetUtcNow());
        var e = f.Plant.Engineer;
        var parts = new[] { SimFactory.BulkPart };
        KpiSnapshot baseline = Kpis.Empty, previous = Kpis.Empty;
        var hours = 0m;

        async Task ExpectMachines(string step)
        {
            report.Expect($"{step}: SLITTER-B", crew.SlittingRunnable() ? "executable" : "blocked: Safety", Crew.Label(await Crew.MachineAsync(f, "SLITTER-B", "RUN_SLITTER")));
            report.Expect($"{step}: PAINT-BOOTH executable", crew.PaintRunnable() ? "True" : "False", (await Crew.MachineAsync(f, "PAINT-BOOTH", "PAINT")).Executable ? "True" : "False");
        }

        var sinceStart = TimeSpan.Zero;
        foreach (var ev in log.Events)
        {
            f.Clock.Advance(ev.At - sinceStart);
            sinceStart = ev.At;
            crew.Now = f.Clock.GetUtcNow();
            var step = $"#{ev.Index} {ev.Type}";
            switch (ev.Type)
            {
                case "Order":
                    report.Rule(Basis.Plan, "Setup: released customer orders");
                    await f.ReleasedOrderAsync(ev.Text("part"), ev.Number("quantity"), (int)ev.Number("dueInDays"));
                    break;

                case "Baseline":
                    report.Rule(Basis.Spec, Spec + ": with everyone present, both Slitting people satisfy the rule");
                    baseline = previous = report.Kpi(await Kpis.CollectAsync(f, "baseline"));
                    await ExpectMachines(step);
                    report.Rule(Basis.Plan, "Plan arithmetic: every order fits well within its due date on a 24/7 calendar");
                    report.Expect($"{step}: no order late or unschedulable", "0", (baseline.Get("schedule.orders.Late") + baseline.Get("schedule.orders.Unschedulable")).ToString(CultureInfo.InvariantCulture));
                    break;

                case "CallOut":
                {
                    hours = ev.Number("hours");
                    var now = f.Clock.GetUtcNow();
                    report.Rule(Basis.Plan, "Setup: a supervisor records the callout");
                    var recorded = await f.Api.Send(e, $"/api/workforce/workers/{f.Plant.People[mutation == Mutation.CallOutWrongPerson ? "michael" : "sarah"]}/unavailability",
                        Crew.Window(now, now.AddHours((double)hours), "CallOut", "Called out sick"));
                    report.Expect($"{step}: callout recorded", "200", recorded.Label);
                    crew.Away("sarah", true);
                    report.Rule(Basis.Spec, Spec + " §78: without a second qualified person in Slitting, the slitters stop with the safety rule as evidence");
                    await ExpectMachines(step);
                    var after = report.Kpi(await Kpis.CollectAsync(f, "after callout"));
                    report.Rule(Basis.Spec, Spec + " §81: each slitter loses exactly the absent hours of executable time (machine windows are each feasible on their own)");
                    foreach (var machine in new[] { "SLITTER-A", "SLITTER-B" })
                    {
                        var key = $"capacity.{machine}.RUN_SLITTER.executableHours.7d";
                        report.Expect($"{step}: {machine} executable hours over 7 days", previous.Get(key) - hours, after.Get(key), "Capacity");
                    }

                    report.Rule(Basis.Spec, Spec + ": Sarah's absent hours leave the projected SLIT-II and FIRST-AID labour");
                    report.ExpectHours($"{step}: SLIT-II labour hours over 7 days", previous.Get("labor.SLIT-II.hours.7d") - hours, after.Get("labor.SLIT-II.hours.7d"), "Capacity");
                    report.ExpectHours($"{step}: FIRST-AID labour hours over 7 days", previous.Get("labor.FIRST-AID.hours.7d") - hours, after.Get("labor.FIRST-AID.hours.7d"), "Capacity");
                    report.Rule(Basis.Spec, Spec + ": the paint booth does not depend on Slitting people");
                    report.Expect($"{step}: PAINT-BOOTH executable hours unchanged", previous.Get("capacity.PAINT-BOOTH.PAINT.executableHours.7d"), after.Get("capacity.PAINT-BOOTH.PAINT.executableHours.7d"), "Capacity");
                    report.Rule(Basis.Plan, "Plan arithmetic: the work still fits before the due dates");
                    report.Expect($"{step}: no order late or unschedulable", "0", (after.Get("schedule.orders.Late") + after.Get("schedule.orders.Unschedulable")).ToString(CultureInfo.InvariantCulture));
                    report.ImpactOf("Operator callout", previous, after);
                    previous = after;
                    break;
                }

                case "TraineeCover":
                {
                    report.Rule(Basis.Spec, Spec + ": a trainee cannot fill a safety slot (traineeMayFill = false), and Dana holds no qualification");
                    var assigned = await f.Api.Send(e, $"/api/workforce/workers/{f.Plant.People["dana"]}/assignment", new { workCenterCode = "SLIT-WC", activityCode = "STAGE_ROLL", asTrainee = true, reason = "Cover attempt" });
                    report.Expect($"{step}: trainee assigned", "200", assigned.Label);
                    crew.MoveTo("dana", "SLITTING", asTrainee: true);
                    await ExpectMachines(step);
                    await f.Api.Send(e, $"/api/workforce/workers/{f.Plant.People["dana"]}/assignment", new { workCenterCode = "ASSY-WC", activityCode = "ASSEMBLE", reason = "Back to assembly" });
                    crew.MoveTo("dana", "ASSEMBLY");
                    break;
                }

                case "PreviewMove":
                {
                    report.Rule(Basis.Spec, Spec + " §79: the preview names the work the move would stop, and commits nothing");
                    var preview = await f.Api.Send(e, $"/api/readiness/reallocation-preview/{f.Plant.People["jennifer"]}", new { workCenterCode = "SLIT-WC", activityCode = "STAGE_ROLL" });
                    report.Expect($"{step}: preview answered", "200", preview.Label);
                    var impacts = preview.Succeeded
                        ? preview.Json.GetProperty("output").GetProperty("impacts").EnumerateArray()
                            .Where(i => i.GetProperty("work").TryGetProperty("machineCode", out var m) && m.ValueKind == JsonValueKind.String)
                            .Select(i => (Kind: i.GetProperty("kind").GetString()!, Machine: i.GetProperty("work").GetProperty("machineCode").GetString()!)).ToList()
                        : [];
                    report.Expect($"{step}: the paint booth would be left unstaffed (Jennifer is the only person on it)", "WouldBeUnstaffed",
                        impacts.Where(i => i.Machine == "PAINT-BOOTH").Select(i => i.Kind).FirstOrDefault() ?? "not listed");
                    report.Expect($"{step}: SLITTER-B would start (Jennifer's FIRST-AID completes the Slitting rule)", "WouldStart",
                        impacts.Where(i => i.Machine == "SLITTER-B").Select(i => i.Kind).FirstOrDefault() ?? "not listed");
                    await ExpectMachines(step);
                    break;
                }

                case "CommitMove":
                {
                    report.Rule(Basis.Spec, Spec + ": Michael operates and Jennifer (FIRST-AID) is the responder, so Slitting runs again; paint stops");
                    var moved = await f.Api.Send(e, $"/api/workforce/workers/{f.Plant.People["jennifer"]}/assignment", new { workCenterCode = "SLIT-WC", activityCode = "STAGE_ROLL", reason = "Cover the Slitting safety slot" });
                    report.Expect($"{step}: move committed", "200", moved.Label);
                    crew.MoveTo("jennifer", "SLITTING");
                    await ExpectMachines(step);
                    var after = report.Kpi(await Kpis.CollectAsync(f, "after moving Jennifer"));
                    report.ImpactOf("Supervisor moves Jennifer to Slitting", previous, after);
                    previous = after;
                    break;
                }

                case "Return":
                {
                    crew.Away("sarah", false);
                    report.Rule(Basis.Plan, "Shift handover: a supervisor confirms who is on site, including Sarah on her return (presence evidence lasts eight hours)");
                    await crew.ConfirmPresentAsync(f, report, step);
                    report.Rule(Basis.Spec, Spec + ": the callout has ended");
                    await ExpectMachines(step);
                    var after = report.Kpi(await Kpis.CollectAsync(f, "after Sarah returns"));
                    report.Rule(Basis.Spec, Spec + " §81: with no absence ahead, every slitter hour is executable again");
                    foreach (var machine in new[] { "SLITTER-A", "SLITTER-B" })
                    {
                        report.Expect($"{step}: {machine} executable hours over 7 days equal its working hours",
                            after.Get($"capacity.{machine}.RUN_SLITTER.workingHours.7d"), after.Get($"capacity.{machine}.RUN_SLITTER.executableHours.7d"), "Capacity");
                    }

                    report.ImpactOf("Callout ends", previous, after);
                    previous = after;
                    break;
                }

                case "RestorePaint":
                {
                    report.Rule(Basis.Spec, Spec + ": everyone back where they started");
                    var moved = await f.Api.Send(e, $"/api/workforce/workers/{f.Plant.People["jennifer"]}/assignment", new { machineCode = "PAINT-BOOTH", activityCode = "PAINT", reason = "Back to paint" });
                    report.Expect($"{step}: Jennifer back to paint", "200", moved.Label);
                    crew.MoveTo("jennifer", "PAINT");
                    await ExpectMachines(step);
                    var after = report.Kpi(await Kpis.CollectAsync(f, "final"));
                    report.Rule(Basis.Plan, "Plan arithmetic: no order is late after the disruption is over");
                    report.Expect($"{step}: no order late or unschedulable", "0", (after.Get("schedule.orders.Late") + after.Get("schedule.orders.Unschedulable")).ToString(CultureInfo.InvariantCulture));
                    report.ImpactOf("Paint restored", previous, after);
                    report.ImpactOf("Whole callout (baseline to final)", baseline, after);
                    break;
                }
            }

            await Invariants.Inventory(report, step, f.StoresUser, parts);
        }

        await Invariants.AuditChain(report, "End", f.Plant.Admin);
        var hash = await Invariants.StateHash(f.StoresUser, parts);
        report.Note($"Callout of {Invariants.Q(hours)} h. Overtime is not modeled in Forge; recovery is by reassignment only.");
        return (report, log, hash);
    }
}
