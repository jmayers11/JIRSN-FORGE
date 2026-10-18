using System.Globalization;
using Forge.Integration.Tests;
using Forge.Simulation.Tests.Engine;

namespace Forge.Simulation.Tests.Scenarios;

/// <summary>
/// Scenario 13 (document error): a work instruction is required for slitting, but only drafted, never released. With
/// documentation enforced, the order's slitting is blocked on documentation; releasing it clears the block. Two approved
/// rules then disagree about which revision applies to the order: Forge must not guess, so the documentation is
/// indeterminate and the work is held, until one rule is withdrawn with a reason. Expectations come from the V2.3
/// documentation specification (missing documents block only where enforced; conflicting rules are never guessed).
/// </summary>
internal static class DocumentErrorScenario
{
    public const string Name = "S13-document-error";
    private const string Spec = "V2.3 document applicability";
    private const string Instruction = "WI-SIM";

    public static EventLog Plan(int seed)
    {
        var log = new EventLog(Name, seed);
        log.Add(TimeSpan.Zero, "Order", ("quantity", log.Between(10, 80)), ("dueInDays", log.Between(8, 14)));
        log.Add(TimeSpan.FromMinutes(10), "Enforce");
        log.Add(TimeSpan.FromHours(log.Between(1, 3)), "ReleaseInstruction", ("revision", "A"));
        log.Add(TimeSpan.FromHours(4), "ReleaseInstruction", ("revision", "B"));
        log.Add(TimeSpan.FromHours(5), "ConflictingRules");
        log.Add(TimeSpan.FromHours(6), "WithdrawRule");
        return log;
    }

    public static async Task<(SimReport Report, EventLog Log, string Hash)> RunAsync(PostgresFixture postgres, int seed, Mutation mutation = Mutation.None)
    {
        var log = Plan(seed);
        var report = new SimReport(mutation == Mutation.None ? Name : Name + RunSummary.CanaryMarker + mutation, seed);
        await using var f = await SimFactory.BuildPlanningAsync(postgres, "s13-" + seed.ToString(CultureInfo.InvariantCulture));
        report.Evidence(f.Api);
        report.Covers(Coverage.Documents, Coverage.Execution);
        var (e, approver) = (f.Plant.Engineer, f.Plant.Approver);
        var parts = new[] { SimFactory.BulkPart };
        long order = 0, ruleA = 0;
        KpiSnapshot previous = Kpis.Empty;

        report.Rule(Basis.Plan, "Setup: a work instruction required for slitting, drafted but not released");
        report.Expect("Setup: instruction created", "200", (await Documents.CreateAsync(f, Instruction, "WorkInstruction", "Slitting instruction")).Label);
        report.Expect("Setup: instruction required for operation 10", "200", (await f.Api.Send(e, "/api/engineering/items/ROLL-100/revisions/A/documents", new { documentNumber = Instruction, operationSequence = 10, reason = "Threading instruction" })).Label);
        report.Expect("Setup: rev A drafted only", "200", (await Documents.DraftAsync(f, Instruction, "A")).Label);

        var sinceStart = TimeSpan.Zero;
        foreach (var ev in log.Events)
        {
            f.Clock.Advance(ev.At - sinceStart);
            sinceStart = ev.At;
            var step = $"#{ev.Index} {ev.Type}";
            switch (ev.Type)
            {
                case "Order":
                    report.Rule(Basis.Plan, "Setup: a released order");
                    order = await f.ReleasedOrderAsync(SimFactory.FlexPart, ev.Number("quantity"), (int)ev.Number("dueInDays"));
                    report.Rule(Basis.Spec, Spec + ": with no released revision the instruction is missing; while documentation is not enforced it is not evaluated (never passed)");
                    report.Expect($"{step}: instruction state", "Missing", await Documents.StateAsync(f, order, Instruction));
                    report.Expect($"{step}: documentation", "not evaluated", await Documents.DocumentationOutcomeAsync(f, order, 10));
                    previous = report.Kpi(await Kpis.CollectAsync(f, "baseline", proposeSchedule: false));
                    break;

                case "Enforce":
                {
                    report.Rule(Basis.Spec, Spec + ": once enforced, the missing instruction blocks the operation");
                    report.Expect($"{step}: enforced", "200", (await f.Api.Send(approver, "/api/engineering/documentation-policy/Make", new { enforced = true, reason = "Documents loaded for made parts" })).Label);
                    report.Expect($"{step}: operation 10", "blocked: Documentation", await Documents.OperationAsync(f, order, 10));
                    var after = report.Kpi(await Kpis.CollectAsync(f, "enforced, instruction missing", proposeSchedule: false));
                    report.ImpactOf("Missing instruction blocks work", previous, after);
                    previous = after;
                    break;
                }

                case "ReleaseInstruction":
                {
                    var revision = ev.Text("revision");
                    report.Rule(Basis.Spec, Spec + ": a released instruction clears the block");
                    var released = revision == "A"
                        ? await ReleaseDraftedAsync(f, Instruction, "A")
                        : await Documents.ReleaseAsync(f, Instruction, revision);
                    report.Expect($"{step} {revision}: released", "200 200 200 200", released);
                    report.Expect($"{step} {revision}: documentation", "Pass", await Documents.DocumentationOutcomeAsync(f, order, 10));
                    var after = report.Kpi(await Kpis.CollectAsync(f, "instruction released " + revision, proposeSchedule: false));
                    report.ImpactOf("Instruction released " + revision, previous, after);
                    previous = after;
                    break;
                }

                case "ConflictingRules":
                {
                    report.Rule(Basis.Plan, "Setup: two approved rules name different revisions for the same order");
                    var a = await f.Api.Send(approver, $"/api/engineering/documents/{Instruction}/rules", new { revision = "A", condition = "Order", orderNumber = order, reference = "DV-SIM-A", reason = "Use rev A for this lot" });
                    ruleA = a.Succeeded ? a.Id : 0;
                    var b = await f.Api.Send(approver, $"/api/engineering/documents/{Instruction}/rules", new { revision = "B", condition = "Order", orderNumber = order, reference = "DV-SIM-B", reason = "Conflicting concession" });
                    report.Expect($"{step}: rules recorded", "200 200", a.Label + " " + b.Label);
                    report.Rule(Basis.Spec, Spec + ": conflicting rules are never guessed: the state requires review and the documentation is indeterminate, so the work does not run");
                    report.Expect($"{step}: instruction state", "RequiresReview", await Documents.StateAsync(f, order, Instruction));
                    report.Expect($"{step}: documentation", "Indeterminate", await Documents.DocumentationOutcomeAsync(f, order, 10));
                    report.Expect($"{step}: operation 10", "blocked: Documentation", await Documents.OperationAsync(f, order, 10));
                    var after = report.Kpi(await Kpis.CollectAsync(f, "conflicting rules", proposeSchedule: false));
                    report.ImpactOf("Conflicting applicability rules", previous, after);
                    previous = after;
                    break;
                }

                case "WithdrawRule":
                {
                    report.Rule(Basis.Spec, Spec + ": withdrawing a rule needs a reason; with one rule left the documentation is decided again");
                    report.Expect($"{step}: withdrawal without a reason refused", "422",
                        ((int)(await f.Api.Send(approver, $"/api/engineering/applicability-rules/{ruleA}/withdraw", new { })).Status).ToString(CultureInfo.InvariantCulture));
                    report.Expect($"{step}: rule withdrawn", "200", (await f.Api.Send(approver, $"/api/engineering/applicability-rules/{ruleA}/withdraw", new { reason = "DV-SIM-A cancelled" })).Label);
                    report.Expect($"{step}: documentation decided again", "True", await Documents.DocumentationOutcomeAsync(f, order, 10) != "Indeterminate" ? "True" : "False");
                    var after = report.Kpi(await Kpis.CollectAsync(f, "rule withdrawn", proposeSchedule: false));
                    report.ImpactOf("Conflicting rule withdrawn", previous, after);
                    break;
                }
            }

            await Invariants.Inventory(report, step, f.StoresUser, parts);
        }

        await Invariants.AuditChain(report, "End", f.Plant.Admin);
        return (report, log, await Invariants.StateHash(f.StoresUser, parts));
    }

    /// <summary>Submits, approves and releases a revision that is already drafted.</summary>
    private static async Task<string> ReleaseDraftedAsync(SimFactory f, string number, string revision)
    {
        var submitted = await f.Api.Send(f.Plant.Engineer, $"/api/engineering/documents/{number}/revisions/{revision}/submit", new { reason = "Submit" });
        var approved = await f.Api.Send(f.Plant.Approver, $"/api/engineering/documents/{number}/revisions/{revision}/approve", new { reason = "Approve" });
        var released = await f.Api.Send(f.Plant.Approver, $"/api/engineering/documents/{number}/revisions/{revision}/release", new { reason = "ECN" });
        return $"200 {submitted.Label} {approved.Label} {released.Label}";
    }
}
