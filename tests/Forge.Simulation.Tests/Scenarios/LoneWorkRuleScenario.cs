using System.Globalization;
using Forge.Integration.Tests;
using Forge.Simulation.Tests.Engine;

namespace Forge.Simulation.Tests.Scenarios;

/// <summary>
/// Scenario 19 (lone-work rule): EHS makes painting a two-person job. A drafted rule changes nothing; its author cannot
/// publish it; once a safety approver publishes it, Jennifer alone can no longer paint and the booth's projected hours
/// drop to zero, because nobody else in Paint can be the responder. Borrowing Sarah from Slitting as responder lets
/// painting run now but stops slitting (Michael is then alone), and projected capacity does not move (ADR 0056 §1).
/// Sending Sarah back reverses both. Expectations come from the Phase 2 safety-rule specification (rules apply when
/// published, by someone other than their author; slots and lone-work are evaluated for the people present) and the
/// <see cref="Crew"/> oracle, never from Forge.
/// </summary>
internal static class LoneWorkRuleScenario
{
    public const string Name = "S19-lone-work-rule";
    private const string Spec = "Phase 2 safety rules";
    private const string Paint = "capacity.PAINT-BOOTH.PAINT.executableHours.7d";
    private const string SlitB = "capacity.SLITTER-B.RUN_SLITTER.executableHours.7d";

    public static EventLog Plan(int seed)
    {
        var log = new EventLog(Name, seed);
        log.Add(TimeSpan.Zero, "Baseline");
        log.Add(TimeSpan.FromMinutes(log.Between(5, 20)), "Draft");
        log.Add(TimeSpan.FromMinutes(30), "Publish");
        log.Add(TimeSpan.FromMinutes(40), "Borrow", ("person", "sarah"));
        log.Add(TimeSpan.FromMinutes(50), "Return", ("person", "sarah"));
        return log;
    }

    public static async Task<(SimReport Report, EventLog Log, string Hash)> RunAsync(PostgresFixture postgres, int seed, Mutation mutation = Mutation.None)
    {
        var log = Plan(seed);
        var report = new SimReport(mutation == Mutation.None ? Name : Name + RunSummary.CanaryMarker + mutation, seed);
        await using var f = await SimFactory.BuildPlanningAsync(postgres, "s19-" + seed.ToString(CultureInfo.InvariantCulture));
        report.Evidence(f.Api);
        report.Covers(Coverage.Safety, Coverage.Workforce, Coverage.Scheduling);
        var crew = new Crew(f.Clock.GetUtcNow());
        var (e, approver) = (f.Plant.Engineer, f.Plant.Approver);
        await f.Plant.Ok(e, "/api/resources/activity-types", new { code = "MASK", name = "Mask parts" });
        await f.Plant.Ok(e, "/api/resources/work-centers/PAINT-WC/activities", new { activityCode = "MASK" });
        var parts = new[] { SimFactory.BulkPart };
        var published = false;
        KpiSnapshot previous = Kpis.Empty;

        async Task ExpectNow(string step)
        {
            var paintable = published ? crew.PaintPairRunnable() : crew.PaintRunnable();
            report.Expect($"{step}: PAINT-BOOTH now", paintable ? "executable" : "blocked: Safety", Crew.Label(await Crew.MachineAsync(f, "PAINT-BOOTH", "PAINT")), "Safety");
            report.Expect($"{step}: SLITTER-B now", crew.SlittingRunnable() ? "executable" : "blocked: Safety", Crew.Label(await Crew.MachineAsync(f, "SLITTER-B", "RUN_SLITTER")), "Safety");
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
                case "Baseline":
                    report.Rule(Basis.Spec, Spec + ": before the rule, the booth needs only its PAINT-I operator");
                    await ExpectNow(step);
                    previous = report.Kpi(await Kpis.CollectAsync(f, "baseline", proposeSchedule: false));
                    break;

                case "Draft":
                {
                    report.Rule(Basis.Spec, Spec + ": a draft rule has no effect until it is published");
                    report.Expect($"{step}: rule drafted", "200", (await f.Api.Send(e, "/api/safety-rules/drafts", new
                    {
                        code = "PAINT-PAIR", title = "Two-person painting", scopeKind = "SafetyArea", scopeCode = "PAINT", activityCodes = new[] { "PAINT" },
                        minimumPhysicalPeople = 2, prohibitLoneWork = true,
                        slots = new object[]
                        {
                            new { name = "Operator", count = 1, satisfiedBy = new[] { "PAINT-I" }, traineeMayFill = false },
                            new { name = "Safety responder", count = 1, satisfiedBy = new[] { "FIRST-AID" }, traineeMayFill = false },
                        },
                        reason = "EHS procedure SP-31: solvent booth",
                    })).Label);
                    await ExpectNow(step);
                    break;
                }

                case "Publish":
                {
                    report.Rule(Basis.Spec, Spec + ": the author cannot publish their own rule; a safety approver can");
                    report.Expect($"{step}: author cannot publish", "403",
                        ((int)(await f.Api.Send(e, "/api/safety-rules/PAINT-PAIR/versions/1/publish", new { reason = "Mine" })).Status).ToString(CultureInfo.InvariantCulture), "Authorization");
                    report.Expect($"{step}: approver publishes", "200", (await f.Api.Send(approver, "/api/safety-rules/PAINT-PAIR/versions/1/publish", new { reason = "Reviewed against SP-31" })).Label);
                    published = true;
                    report.Rule(Basis.Spec, Spec + ": with the rule in force Jennifer alone cannot paint; nobody else in Paint can be the responder, so the booth has no projected hours");
                    await ExpectNow(step);
                    var after = report.Kpi(await Kpis.CollectAsync(f, "rule published", proposeSchedule: false));
                    report.Expect($"{step}: PAINT-BOOTH executable hours over 7 days", 0m, after.Get(Paint), "Capacity");
                    report.Expect($"{step}: SLITTER-B executable hours unchanged", previous.Get(SlitB), after.Get(SlitB), "Capacity");
                    report.ImpactOf("Two-person painting rule published", previous, after);
                    previous = after;
                    break;
                }

                case "Borrow":
                case "Return":
                {
                    var person = ev.Text("person");
                    var toPaint = ev.Type == "Borrow";
                    object assignment = toPaint
                        ? new { workCenterCode = "PAINT-WC", activityCode = "MASK", reason = "Responder for painting" }
                        : new { workCenterCode = "SLIT-WC", activityCode = "STAGE_ROLL", reason = "Back to Slitting" };
                    report.Rule(Basis.Plan, "Setup: the supervisor moves Sarah by assignment");
                    report.Expect($"{step}: {person} assigned", "200", (await f.Api.Send(e, $"/api/workforce/workers/{f.Plant.People[person]}/assignment", assignment)).Label);
                    crew.MoveTo(person, toPaint ? "PAINT" : "SLITTING");
                    report.Rule(Basis.Spec, Spec + ": the rules are evaluated for the people present in each area now");
                    await ExpectNow(step);
                    var after = report.Kpi(await Kpis.CollectAsync(f, $"{person} {(toPaint ? "borrowed by Paint" : "back in Slitting")}", proposeSchedule: false));
                    report.Rule(Basis.Spec, "ADR 0056 §1: an assignment does not move projected hours");
                    report.Expect($"{step}: PAINT-BOOTH projected hours unchanged", previous.Get(Paint), after.Get(Paint), "Capacity");
                    report.Expect($"{step}: SLITTER-B projected hours unchanged", previous.Get(SlitB), after.Get(SlitB), "Capacity");
                    report.ImpactOf(toPaint ? "Responder borrowed from Slitting" : "Responder returned to Slitting", previous, after);
                    previous = after;
                    break;
                }
            }

            await Invariants.Inventory(report, step, f.StoresUser, parts);
        }

        await Invariants.AuditChain(report, "End", f.Plant.Admin);
        return (report, log, await Invariants.StateHash(f.StoresUser, parts));
    }
}
