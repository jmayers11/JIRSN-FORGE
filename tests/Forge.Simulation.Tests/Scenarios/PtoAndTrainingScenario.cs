using System.Globalization;
using Forge.Integration.Tests;
using Forge.Simulation.Tests.Engine;

namespace Forge.Simulation.Tests.Scenarios;

/// <summary>
/// Scenario 2 (PTO and training restriction): Sarah's SLIT-II qualification is suspended (she keeps FIRST-AID), then
/// Michael's approved PTO is recorded for later in the day, then Sarah is re-certified. A suspension alone does not stop
/// Slitting (Michael operates, Sarah is the responder) but removes her SLIT-II labour; future PTO does not stop work now
/// but removes its hours from capacity ahead of time; during the PTO Slitting stops. Expectations come from the
/// <see cref="Crew"/> oracle and the Phase 2/4 specification, never from Forge.
/// </summary>
internal static class PtoAndTrainingScenario
{
    public const string Name = "S02-pto-and-training";
    private const string Spec = "Phase 2/4 spec §78, §81 and the reference plant's SLIT-AREA rule";

    public static EventLog Plan(int seed)
    {
        var log = new EventLog(Name, seed);
        var startsIn = log.Between(2, 6);
        var lasts = log.Between(16, 30);
        log.Add(TimeSpan.Zero, "Order", ("part", "ROLL-100"), ("quantity", log.Between(40, 160)), ("dueInDays", log.Between(6, 12)));
        log.Add(TimeSpan.Zero, "Order", ("part", "ROLL-300"), ("quantity", log.Between(30, 100)), ("dueInDays", log.Between(7, 12)));
        log.Add(TimeSpan.FromMinutes(5), "Baseline");
        log.Add(TimeSpan.FromMinutes(10), "Suspend", ("person", "sarah"), ("qualification", "SLIT-II"));
        log.Add(TimeSpan.FromMinutes(20), "Pto", ("person", "michael"), ("startsInHours", startsIn), ("hours", lasts));
        log.Add(TimeSpan.FromMinutes(30), "Recertify", ("person", "sarah"), ("qualification", "SLIT-II"));
        log.Add(TimeSpan.FromMinutes(20 + startsIn * 60 + 60), "DuringPto", ("person", "michael"));
        log.Add(TimeSpan.FromMinutes(20 + (startsIn + lasts) * 60 + 15), "AfterPto", ("person", "michael"));
        return log;
    }

    public static async Task<(SimReport Report, EventLog Log, string Hash)> RunAsync(PostgresFixture postgres, int seed, Mutation mutation = Mutation.None)
    {
        var log = Plan(seed);
        var report = new SimReport(mutation == Mutation.None ? Name : Name + RunSummary.CanaryMarker + mutation, seed);
        await using var f = await SimFactory.BuildPlanningAsync(postgres, "s02-" + seed.ToString(CultureInfo.InvariantCulture));
        report.Evidence(f.Api);
        report.Covers(Coverage.Workforce, Coverage.Training, Coverage.Safety, Coverage.Scheduling);
        var crew = new Crew(f.Clock.GetUtcNow());
        var parts = new[] { SimFactory.BulkPart };
        KpiSnapshot baseline = Kpis.Empty, previous = Kpis.Empty;
        decimal ptoHours = 0m;

        async Task ExpectSlitting(string step) =>
            report.Expect($"{step}: SLITTER-B", crew.SlittingRunnable() ? "executable" : "blocked: Safety", Crew.Label(await Crew.MachineAsync(f, "SLITTER-B", "RUN_SLITTER")));

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
                    report.Rule(Basis.Spec, Spec + ": with everyone present, Slitting runs");
                    baseline = previous = report.Kpi(await Kpis.CollectAsync(f, "baseline"));
                    await ExpectSlitting(step);
                    break;

                case "Suspend":
                {
                    report.Rule(Basis.Spec, "Competency: a certifier suspends a qualification with a reason");
                    var suspended = await f.Api.Send(f.Plant.Approver, $"/api/competency/people/{f.Plant.People["sarah"]}/suspensions", new { code = "SLIT-II", reason = "Retraining after a near miss" });
                    report.Expect($"{step}: suspension recorded", "200", suspended.Label);
                    crew.Suspend("sarah", "SLIT-II");
                    report.Rule(Basis.Spec, Spec + ": Michael (SLIT-II) operates and Sarah (FIRST-AID) is the responder, so Slitting still runs");
                    await ExpectSlitting(step);
                    var after = report.Kpi(await Kpis.CollectAsync(f, "after suspension", proposeSchedule: false));
                    report.Rule(Basis.Spec, Spec + ": Sarah's hours leave SLIT-II labour; Sarah and Michael are identical 24/7 SLIT-II holders, so that is half");
                    report.Expect($"{step}: SLIT-II labour hours over 7 days", previous.Get("labor.SLIT-II.hours.7d") / 2m, after.Get("labor.SLIT-II.hours.7d"), "Capacity");
                    report.Expect($"{step}: FIRST-AID labour hours over 7 days unchanged", previous.Get("labor.FIRST-AID.hours.7d"), after.Get("labor.FIRST-AID.hours.7d"), "Capacity");
                    report.ImpactOf("Qualification suspended", previous, after);
                    previous = after;
                    break;
                }

                case "Pto":
                {
                    ptoHours = ev.Number("hours");
                    var from = f.Clock.GetUtcNow().AddHours((double)ev.Number("startsInHours"));
                    report.Rule(Basis.Plan, "Setup: approved PTO from HR");
                    var recorded = await f.Api.Send(f.Plant.Engineer, $"/api/workforce/workers/{f.Plant.People["michael"]}/unavailability",
                        Crew.Window(from, from.AddHours((double)ptoHours), "ApprovedPto", "Approved PTO"));
                    report.Expect($"{step}: PTO recorded", "200", recorded.Label);
                    report.Rule(Basis.Spec, Spec + " §81: future PTO does not stop work now");
                    await ExpectSlitting(step);
                    var after = report.Kpi(await Kpis.CollectAsync(f, "after PTO recorded"));
                    report.Rule(Basis.Spec, Spec + " §81: while Michael is away Sarah is alone in Slitting, so each slitter loses the PTO hours ahead of time");
                    foreach (var machine in new[] { "SLITTER-A", "SLITTER-B" })
                    {
                        var key = $"capacity.{machine}.RUN_SLITTER.executableHours.7d";
                        report.Expect($"{step}: {machine} executable hours over 7 days", previous.Get(key) - ptoHours, after.Get(key), "Capacity");
                    }

                    report.Rule(Basis.Spec, Spec + ": Michael is the only SLIT-II holder while Sarah is suspended, so SLIT-II labour loses the PTO hours");
                    report.Expect($"{step}: SLIT-II labour hours over 7 days", previous.Get("labor.SLIT-II.hours.7d") - ptoHours, after.Get("labor.SLIT-II.hours.7d"), "Capacity");
                    report.ImpactOf("Approved PTO recorded", previous, after);
                    previous = after;
                    break;
                }

                case "Recertify":
                {
                    report.Rule(Basis.Spec, "Competency: a certifier re-certifies with evidence");
                    var certified = await f.Api.Send(f.Plant.Approver, $"/api/competency/people/{f.Plant.People["sarah"]}/certifications", new { code = "SLIT-II", evidence = "Retraining completed and assessed", reason = "Reassessment passed" });
                    report.Expect($"{step}: re-certified", "200", certified.Label);
                    crew.Certify("sarah", "SLIT-II");
                    var after = report.Kpi(await Kpis.CollectAsync(f, "after re-certification", proposeSchedule: false));
                    report.Rule(Basis.Spec, Spec + ": Sarah's SLIT-II hours return; slitter capacity during the PTO does not, because she is still alone then");
                    report.Expect($"{step}: SLIT-II labour hours over 7 days", previous.Get("labor.SLIT-II.hours.7d") + baseline.Get("labor.SLIT-II.hours.7d") / 2m, after.Get("labor.SLIT-II.hours.7d"), "Capacity");
                    report.Expect($"{step}: SLITTER-B executable hours unchanged", previous.Get("capacity.SLITTER-B.RUN_SLITTER.executableHours.7d"), after.Get("capacity.SLITTER-B.RUN_SLITTER.executableHours.7d"), "Capacity");
                    report.ImpactOf("Qualification restored", previous, after);
                    previous = after;
                    break;
                }

                case "DuringPto":
                    crew.Away("michael", true);
                    report.Rule(Basis.Spec, Spec + " §78: during the PTO only Sarah is in Slitting, so the slitters stop");
                    await ExpectSlitting(step);
                    break;

                case "AfterPto":
                {
                    crew.Away("michael", false);
                    report.Rule(Basis.Plan, "Shift handover: a supervisor confirms who is on site (presence evidence lasts eight hours)");
                    await crew.ConfirmPresentAsync(f, report, step);
                    report.Rule(Basis.Spec, Spec + ": the PTO has ended");
                    await ExpectSlitting(step);
                    var after = report.Kpi(await Kpis.CollectAsync(f, "after PTO"));
                    report.Rule(Basis.Spec, Spec + " §81: with no absence ahead, every slitter hour is executable again");
                    foreach (var machine in new[] { "SLITTER-A", "SLITTER-B" })
                    {
                        report.Expect($"{step}: {machine} executable hours equal working hours",
                            after.Get($"capacity.{machine}.RUN_SLITTER.workingHours.7d"), after.Get($"capacity.{machine}.RUN_SLITTER.executableHours.7d"), "Capacity");
                    }

                    report.Rule(Basis.Plan, "Plan arithmetic: the work fits before the due dates");
                    report.Expect($"{step}: no order late or unschedulable", "0", (after.Get("schedule.orders.Late") + after.Get("schedule.orders.Unschedulable")).ToString(CultureInfo.InvariantCulture));
                    report.ImpactOf("PTO over", previous, after);
                    break;
                }
            }

            await Invariants.Inventory(report, step, f.StoresUser, parts);
        }

        await Invariants.AuditChain(report, "End", f.Plant.Admin);
        return (report, log, await Invariants.StateHash(f.StoresUser, parts));
    }
}
