using System.Globalization;
using Forge.Integration.Tests;
using Forge.Simulation.Tests.Engine;

namespace Forge.Simulation.Tests.Scenarios;

/// <summary>
/// Scenario 3 (staffing transfer): Sarah's PTO is recorded, so Slitting will lose its second person and both slitters lose
/// those hours ahead of time. A supervisor transfers Dana (no qualifications) and then Jennifer (FIRST-AID) to Slitting
/// by assignment, which is how Forge records a transfer: work now follows the assignment (paint stops, Slitting runs),
/// while projected capacity, which follows home work centers and shifts, does not move. A permanent change of home work
/// center has no command in Forge, and overtime is not a concept; both are reported as findings, not as passing.
/// Expectations come from the <see cref="Crew"/> oracle and the Phase 4 specification, never from Forge.
/// </summary>
internal static class StaffingTransferScenario
{
    public const string Name = "S03-staffing-transfer";
    private const string Spec = "Phase 4 spec §81 and the reference plant's SLIT-AREA rule";

    public static EventLog Plan(int seed)
    {
        var log = new EventLog(Name, seed);
        log.Add(TimeSpan.Zero, "Order", ("part", "ROLL-100"), ("quantity", log.Between(40, 160)), ("dueInDays", log.Between(6, 12)));
        log.Add(TimeSpan.FromMinutes(5), "Baseline");
        log.Add(TimeSpan.FromMinutes(10), "Pto", ("person", "sarah"), ("startsInHours", log.Between(2, 6)), ("hours", log.Between(16, 30)));
        log.Add(TimeSpan.FromMinutes(20), "Transfer", ("person", "dana"), ("area", "SLITTING"));
        log.Add(TimeSpan.FromMinutes(30), "Transfer", ("person", "jennifer"), ("area", "SLITTING"));
        log.Add(TimeSpan.FromMinutes(40), "Transfer", ("person", "jennifer"), ("area", "PAINT"));
        log.Add(TimeSpan.FromMinutes(45), "Transfer", ("person", "dana"), ("area", "ASSEMBLY"));
        return log;
    }

    public static async Task<(SimReport Report, EventLog Log, string Hash)> RunAsync(PostgresFixture postgres, int seed, Mutation mutation = Mutation.None)
    {
        var log = Plan(seed);
        var report = new SimReport(mutation == Mutation.None ? Name : Name + RunSummary.CanaryMarker + mutation, seed);
        await using var f = await SimFactory.BuildPlanningAsync(postgres, "s03-" + seed.ToString(CultureInfo.InvariantCulture));
        report.Evidence(f.Api);
        report.Covers(Coverage.Workforce, Coverage.Safety, Coverage.Scheduling);
        var crew = new Crew(f.Clock.GetUtcNow());
        var e = f.Plant.Engineer;
        var parts = new[] { SimFactory.BulkPart };
        KpiSnapshot previous = Kpis.Empty;
        var ptoHours = 0m;
        const string SlitA = "capacity.SLITTER-A.RUN_SLITTER.executableHours.7d";
        const string SlitB = "capacity.SLITTER-B.RUN_SLITTER.executableHours.7d";
        const string Paint = "capacity.PAINT-BOOTH.PAINT.executableHours.7d";

        async Task ExpectNow(string step)
        {
            report.Expect($"{step}: SLITTER-B now", crew.SlittingRunnable() ? "executable" : "blocked: Safety", Crew.Label(await Crew.MachineAsync(f, "SLITTER-B", "RUN_SLITTER")));
            report.Expect($"{step}: PAINT-BOOTH executable now", crew.PaintRunnable() ? "True" : "False", (await Crew.MachineAsync(f, "PAINT-BOOTH", "PAINT")).Executable ? "True" : "False");
        }

        var sinceStart = TimeSpan.Zero;
        foreach (var ev in log.Events)
        {
            f.Clock.Advance(ev.At - sinceStart);
            sinceStart = ev.At;
            crew.Now = f.Clock.GetUtcNow();
            var step = $"#{ev.Index} {ev.Type} {(ev.Data.ContainsKey("person") ? ev.Text("person") : string.Empty)}".TrimEnd();
            switch (ev.Type)
            {
                case "Order":
                    report.Rule(Basis.Plan, "Setup: a released customer order");
                    await f.ReleasedOrderAsync(ev.Text("part"), ev.Number("quantity"), (int)ev.Number("dueInDays"));
                    break;

                case "Baseline":
                    previous = report.Kpi(await Kpis.CollectAsync(f, "baseline", proposeSchedule: false));
                    break;

                case "Pto":
                {
                    ptoHours = ev.Number("hours");
                    var from = f.Clock.GetUtcNow().AddHours((double)ev.Number("startsInHours"));
                    report.Rule(Basis.Plan, "Setup: approved PTO");
                    report.Expect($"{step}: PTO recorded", "200", (await f.Api.Send(e, $"/api/workforce/workers/{f.Plant.People["sarah"]}/unavailability",
                        Crew.Window(from, from.AddHours((double)ptoHours), "ApprovedPto", "Approved PTO"))).Label);
                    var after = report.Kpi(await Kpis.CollectAsync(f, "after PTO recorded", proposeSchedule: false));
                    report.Rule(Basis.Spec, Spec + ": while Sarah is away Michael is alone in Slitting, so each slitter loses the PTO hours");
                    report.Expect($"{step}: SLITTER-A executable hours over 7 days", previous.Get(SlitA) - ptoHours, after.Get(SlitA), "Capacity");
                    report.Expect($"{step}: SLITTER-B executable hours over 7 days", previous.Get(SlitB) - ptoHours, after.Get(SlitB), "Capacity");
                    report.ImpactOf("PTO recorded", previous, after);
                    previous = after;
                    break;
                }

                case "Transfer":
                {
                    var person = ev.Text("person");
                    var area = ev.Text("area");
                    object assignment = area switch
                    {
                        "SLITTING" => new { workCenterCode = "SLIT-WC", activityCode = "STAGE_ROLL", reason = "Staffing transfer" },
                        "PAINT" => new { machineCode = "PAINT-BOOTH", activityCode = "PAINT", reason = "Transfer back" },
                        _ => new { workCenterCode = "ASSY-WC", activityCode = "ASSEMBLE", reason = "Transfer back" },
                    };
                    report.Rule(Basis.Plan, "Setup: the supervisor transfers the person by assignment");
                    report.Expect($"{step}: transfer recorded", "200", (await f.Api.Send(e, $"/api/workforce/workers/{f.Plant.People[person]}/assignment", assignment)).Label);
                    crew.MoveTo(person, area);
                    report.Rule(Basis.Spec, Spec + ": the work running now follows the assignment");
                    await ExpectNow(step);
                    var after = report.Kpi(await Kpis.CollectAsync(f, $"after transfer of {person} to {area}", proposeSchedule: false));
                    report.Rule(Basis.Spec, "ADR 0056 §1: projected capacity follows home work centers and shifts, not a current assignment, so an assignment does not move projected hours");
                    report.Expect($"{step}: SLITTER-B projected hours unchanged", previous.Get(SlitB), after.Get(SlitB), "Capacity");
                    report.Expect($"{step}: PAINT-BOOTH projected hours unchanged", previous.Get(Paint), after.Get(Paint), "Capacity");
                    report.ImpactOf($"Transfer {person} to {area}", previous, after);
                    previous = after;
                    break;
                }
            }

            await Invariants.Inventory(report, step, f.StoresUser, parts);
        }

        await Invariants.AuditChain(report, "End", f.Plant.Admin);
        report.Finding("A worker's home work center cannot be changed after registration (no command): a permanent transfer cannot be recorded, so projected capacity keeps the original department.");
        report.Finding("Overtime is not a concept in Forge; extra hours can only be expressed through shift assignment and presence.");
        return (report, log, await Invariants.StateHash(f.StoresUser, parts));
    }
}
