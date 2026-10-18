using System.Globalization;
using Forge.Integration.Tests;
using Forge.Simulation.Tests.Engine;

namespace Forge.Simulation.Tests.Scenarios;

/// <summary>
/// Scenario 11 (engineering revision mid-production): with documentation enforced for made parts, an order is released
/// against drawing rev A. Rev B is then released while the order is in work: the order keeps rev A as a legitimate
/// supersession and stays executable, and an order released afterwards uses rev B. A deviation that moves the second
/// order back to rev A is a mismatch that blocks it until its documentation is reissued by order-release authority.
/// Expectations come from the V2.3 documentation specification (orders follow the revision that applies;
/// documentation blocks only where enforced; a mismatch blocks until reissued), never from Forge.
/// </summary>
internal static class EngineeringRevisionScenario
{
    public const string Name = "S11-engineering-revision";
    private const string Spec = "V2.3 document applicability";
    private const string Drawing = "DWG-SIM";

    public static EventLog Plan(int seed)
    {
        var log = new EventLog(Name, seed);
        log.Add(TimeSpan.Zero, "Order", ("quantity", log.Between(10, 80)), ("dueInDays", log.Between(8, 14)), ("which", "first"));
        log.Add(TimeSpan.FromHours(log.Between(2, 6)), "ReviseDrawing", ("revision", "B"));
        log.Add(TimeSpan.FromHours(7), "Order", ("quantity", log.Between(10, 80)), ("dueInDays", log.Between(8, 14)), ("which", "second"));
        log.Add(TimeSpan.FromHours(7.5), "Deviation", ("revision", "A"), ("reference", "DV-SIM-1"));
        log.Add(TimeSpan.FromHours(8), "Reissue");
        return log;
    }

    public static async Task<(SimReport Report, EventLog Log, string Hash)> RunAsync(PostgresFixture postgres, int seed, Mutation mutation = Mutation.None)
    {
        var log = Plan(seed);
        var report = new SimReport(mutation == Mutation.None ? Name : Name + RunSummary.CanaryMarker + mutation, seed);
        await using var f = await SimFactory.BuildPlanningAsync(postgres, "s11-" + seed.ToString(CultureInfo.InvariantCulture));
        report.Evidence(f.Api);
        report.Covers(Coverage.EngineeringChange, Coverage.Documents, Coverage.Execution);
        var (e, approver, planner) = (f.Plant.Engineer, f.Plant.Approver, f.Planner);
        var parts = new[] { SimFactory.BulkPart };
        var orders = new Dictionary<string, long>(StringComparer.Ordinal);
        KpiSnapshot previous = Kpis.Empty;

        report.Rule(Basis.Plan, "Setup: drawing rev A released and required for ROLL-100 rev A; documentation enforced for made parts");
        report.Expect("Setup: drawing created", "200", (await Documents.CreateAsync(f, Drawing, "Drawing", "Slit roll drawing")).Label);
        report.Expect("Setup: drawing rev A released", "200 200 200 200", await Documents.ReleaseAsync(f, Drawing, "A"));
        report.Expect("Setup: drawing required by ROLL-100 rev A", "200", (await f.Api.Send(e, "/api/engineering/items/ROLL-100/revisions/A/documents", new { documentNumber = Drawing, reason = "Drawing baseline" })).Label);
        report.Expect("Setup: documentation enforced for made parts", "200", (await f.Api.Send(approver, "/api/engineering/documentation-policy/Make", new { enforced = true, reason = "Documents loaded for made parts" })).Label);
        previous = report.Kpi(await Kpis.CollectAsync(f, "baseline", proposeSchedule: false));

        var sinceStart = TimeSpan.Zero;
        foreach (var ev in log.Events)
        {
            f.Clock.Advance(ev.At - sinceStart);
            sinceStart = ev.At;
            var step = $"#{ev.Index} {ev.Type}";
            switch (ev.Type)
            {
                case "Order":
                {
                    var which = ev.Text("which");
                    report.Rule(Basis.Plan, "Setup: a released order");
                    orders[which] = await f.ReleasedOrderAsync(SimFactory.FlexPart, ev.Number("quantity"), (int)ev.Number("dueInDays"));
                    report.Rule(Basis.Spec, Spec + ": an order released now references the revision in force, and its documentation passes");
                    report.Expect($"{step} {which}: drawing state", "Current", await Documents.StateAsync(f, orders[which], Drawing));
                    report.Expect($"{step} {which}: documentation", "Pass", await Documents.DocumentationOutcomeAsync(f, orders[which], 10));
                    break;
                }

                case "ReviseDrawing":
                {
                    report.Rule(Basis.Plan, "Setup: rev B is released while the first order is in work");
                    report.Expect($"{step}: rev B released", "200 200 200 200", await Documents.ReleaseAsync(f, Drawing, ev.Text("revision")));
                    report.Rule(Basis.Spec, Spec + ": the first order keeps rev A as a legitimate supersession and is not blocked");
                    report.Expect($"{step}: first order drawing state", "LegitimatelySuperseded", await Documents.StateAsync(f, orders["first"], Drawing));
                    report.Expect($"{step}: first order documentation", "Pass", await Documents.DocumentationOutcomeAsync(f, orders["first"], 10));
                    var after = report.Kpi(await Kpis.CollectAsync(f, "after rev B", proposeSchedule: false));
                    report.ImpactOf("Drawing revision released mid-production", previous, after);
                    previous = after;
                    break;
                }

                case "Deviation":
                {
                    report.Rule(Basis.Plan, "Setup: an approved deviation moves the second order (released with rev B) back to rev A");
                    report.Expect($"{step}: deviation recorded", "200", (await f.Api.Send(approver, $"/api/engineering/documents/{Drawing}/rules",
                        new { revision = ev.Text("revision"), condition = "Order", orderNumber = orders["second"], reference = ev.Text("reference"), reason = "Use up rev A material" })).Label);
                    report.Rule(Basis.Spec, Spec + ": what the order was released with no longer matches what applies, so the operation is blocked on documentation");
                    report.Expect($"{step}: second order drawing state", "Mismatch", await Documents.StateAsync(f, orders["second"], Drawing));
                    report.Expect($"{step}: second order operation 10", "blocked: Documentation", await Documents.OperationAsync(f, orders["second"], 10));
                    var after = report.Kpi(await Kpis.CollectAsync(f, "after deviation", proposeSchedule: false));
                    report.ImpactOf("Deviation creates a documentation mismatch", previous, after);
                    previous = after;
                    break;
                }

                case "Reissue":
                {
                    report.Rule(Basis.Spec, Spec + ": reissuing an order's documentation needs order-release authority and a reason");
                    report.Expect($"{step}: engineer cannot reissue", "403",
                        ((int)(await f.Api.Send(e, $"/api/engineering/orders/{orders["second"]}/reissue-documentation", new { reason = "x" })).Status).ToString(CultureInfo.InvariantCulture), "Authorization");
                    report.Expect($"{step}: planner reissues", "200", (await f.Api.Send(planner, $"/api/engineering/orders/{orders["second"]}/reissue-documentation", new { reason = "Traveler reprinted under DV-SIM-1" })).Label);
                    report.Expect($"{step}: second order drawing state", "Current", await Documents.StateAsync(f, orders["second"], Drawing));
                    report.Expect($"{step}: second order documentation", "Pass", await Documents.DocumentationOutcomeAsync(f, orders["second"], 10));
                    var after = report.Kpi(await Kpis.CollectAsync(f, "after reissue", proposeSchedule: false));
                    report.ImpactOf("Documentation reissued", previous, after);
                    break;
                }
            }

            await Invariants.Inventory(report, step, f.StoresUser, parts);
        }

        await Invariants.AuditChain(report, "End", f.Plant.Admin);
        report.Note("Document-training gating and the effect on work already started are Phase C (they need shop-floor execution in the run).");
        return (report, log, await Invariants.StateHash(f.StoresUser, parts));
    }
}
