using System.Globalization;
using Forge.Integration.Tests;
using Forge.Simulation.Tests.Engine;

namespace Forge.Simulation.Tests.Scenarios;

/// <summary>
/// Scenario 23 (label printing failures): an order label fails to print, is printed and attached, gets damaged and is
/// replaced, and finally is voided. A failed attempt is final, but it does not block a new attempt; a confirmed label can
/// only be printed again through the governed path. A replacement ends the old label as soon as it is approved, and the
/// new one is trusted only once printed and attached. A voided label is never trusted again. After each step the scanner
/// answer is checked: only an active, printed and attached label resolves. Labels never move stock. Expectations come
/// from the V4.1 label lifecycle (ADR 0053/0054) and ADR 0056 §2, never from Forge.
/// </summary>
internal static class LabelPrintingScenario
{
    public const string Name = "S23-label-print-failures";
    private const string Spec = "V4.1 label lifecycle";

    public static EventLog Plan(int seed)
    {
        var log = new EventLog(Name, seed);
        log.Add(TimeSpan.Zero, "Order", ("quantity", log.Between(4, 40)), ("dueInDays", log.Between(8, 14)));
        log.Add(TimeSpan.FromMinutes(5), "Issue");
        log.Add(TimeSpan.FromMinutes(10), "PrintFails");
        log.Add(TimeSpan.FromMinutes(15), "PrintWorks");
        log.Add(TimeSpan.FromHours(log.Between(2, 8)), "Damaged");
        log.Add(TimeSpan.FromHours(9), "Void");
        return log;
    }

    public static async Task<(SimReport Report, EventLog Log, string Hash)> RunAsync(PostgresFixture postgres, int seed, Mutation mutation = Mutation.None)
    {
        var log = Plan(seed);
        var report = new SimReport(mutation == Mutation.None ? Name : Name + RunSummary.CanaryMarker + mutation, seed);
        await using var f = await SimFactory.BuildPlanningAsync(postgres, "s23-" + seed.ToString(CultureInfo.InvariantCulture), ScanAndPrintScenario.Configure);
        report.Evidence(f.Api);
        report.Covers(Coverage.Printing, Coverage.Identity);
        await ScanAndPrintScenario.SetupAsync(f.Plant);
        var (printer, approver) = (f.Api.As("label-printer"), f.Plant.Approver);
        var parts = new[] { SimFactory.BulkPart, SimFactory.LotPart };
        long order = 0;
        var ledger = string.Empty;
        (Guid Id, string Code) first = (Guid.Empty, string.Empty), second = (Guid.Empty, string.Empty);

        async Task<string> ResolveAsync(string scanned)
        {
            using var response = await SimApi.PostJson(printer, "/api/identification/resolve", new { code = scanned });
            var outcome = new Outcome(response.StatusCode, await response.Content.ReadAsStringAsync());
            return outcome.Succeeded ? "resolves" : outcome.Body.Contains(scanned, StringComparison.Ordinal) ? outcome.Label + " (code echoed)" : outcome.Label;
        }

        static Guid Entity(Outcome o) => o.EntityId is { } id ? Guid.Parse(id) : Guid.Empty;

        async Task<(Guid Id, string Code)> CopyOf(Outcome issued)
        {
            var copy = (await SimApi.Get(printer, $"/api/identification/labels/issuances/{Entity(issued)}")).GetProperty("copies").EnumerateArray().Single();
            return (copy.GetProperty("labelId").GetGuid(), copy.GetProperty("code").GetString() ?? string.Empty);
        }

        Task<Outcome> Observe(Guid attempt, int version, string outcome, bool printed, bool attached, string observation) =>
            f.Api.Send(printer, $"/api/identification/print-attempts/{attempt}/outcome", new { expectedVersion = version, outcome, observation, observedPrinted = printed, observedAttached = attached, reason = observation });

        async Task<string> PrintAndAttach(Guid label, int version)
        {
            var attempt = await f.Api.Send(printer, $"/api/identification/labels/{label}/print-attempts", new { expectedVersion = version, printerCode = "LINE-PRINTER-1", reason = "Print" });
            var printed = await Observe(Entity(attempt), 1, "Confirmed", true, false, "One correct label printed");
            var attached = await Observe(Entity(attempt), 2, "Confirmed", true, true, "Attached to the order traveler");
            return $"{attempt.Label} {printed.Label} {attached.Label}";
        }

        async Task ExpectScan(string step, string what, (Guid Id, string Code) label, bool trusted) =>
            report.Expect($"{step}: {what}", trusted ? "resolves" : "404 identification_not_found", await ResolveAsync(label.Code), "Identity");

        var sinceStart = TimeSpan.Zero;
        foreach (var ev in log.Events)
        {
            f.Clock.Advance(ev.At - sinceStart);
            sinceStart = ev.At;
            var step = $"#{ev.Index} {ev.Type}";
            switch (ev.Type)
            {
                case "Order":
                    report.Rule(Basis.Plan, "Setup: a released order to label");
                    order = await f.ReleasedOrderAsync(SimFactory.FlexPart, ev.Number("quantity"), (int)ev.Number("dueInDays"));
                    ledger = await Invariants.StateHash(f.StoresUser, parts);
                    break;

                case "Issue":
                {
                    report.Rule(Basis.Spec, Spec + ": a label copy is issued and approved by a second person");
                    var issued = await f.Api.Send(printer, "/api/identification/labels", new
                    {
                        subjectCode = $"forge:v1:{ScanAndPrintScenario.Namespace}:order:{order.ToString(CultureInfo.InvariantCulture)}",
                        templateCode = "ORDER-LABEL", templateVersion = 1, symbology = "QR", copies = 1, facilityCode = "PLANT1", reason = "Order label",
                    });
                    report.Expect($"{step}: issued", "200", issued.Label);
                    first = await CopyOf(issued);
                    report.Expect($"{step}: approved", "200", (await f.Api.Send(approver, $"/api/identification/labels/{first.Id}/approve", new { expectedVersion = 1, reason = "Independent approval" })).Label);
                    await ExpectScan(step, "an approved, unprinted label does not resolve", first, false);
                    break;
                }

                case "PrintFails":
                {
                    report.Rule(Basis.Spec, Spec + ": a failed print is recorded as observed, and it is final");
                    var attempt = await f.Api.Send(printer, $"/api/identification/labels/{first.Id}/print-attempts", new { expectedVersion = 2, printerCode = "LINE-PRINTER-1", reason = "Print" });
                    report.Expect($"{step}: print requested", "200", attempt.Label);
                    report.Expect($"{step}: failure recorded", "200", (await Observe(Entity(attempt), 1, "Failed", false, false, "Ribbon out; nothing printed")).Label);
                    report.Expect($"{step}: a failed attempt cannot be changed", "422 print_attempt_final", (await Observe(Entity(attempt), 2, "Confirmed", true, false, "Changing my mind")).Label);
                    await ExpectScan(step, "a label that failed to print does not resolve", first, false);
                    break;
                }

                case "PrintWorks":
                    report.Rule(Basis.Spec, Spec + ": a failed attempt does not block a new one; once printed and attached the label resolves");
                    report.Expect($"{step}: printed and attached", "200 200 200", await PrintAndAttach(first.Id, 2));
                    await ExpectScan(step, "the attached label resolves", first, true);
                    report.Rule(Basis.Spec, Spec + ": another physical copy needs the governed reprint or replacement path");
                    report.Expect($"{step}: plain print refused", "422 label_reprint_required",
                        (await f.Api.Send(printer, $"/api/identification/labels/{first.Id}/print-attempts", new { expectedVersion = 2, reason = "Extra copy" })).Label);
                    break;

                case "Damaged":
                {
                    report.Rule(Basis.Spec, Spec + ": a damaged label is replaced; the old one stays trusted until the replacement is approved");
                    var replaced = await f.Api.Send(printer, $"/api/identification/labels/{first.Id}/replace", new { expectedVersion = 2, reason = "Label torn" });
                    report.Expect($"{step}: replacement issued", "200", replaced.Label);
                    second = await CopyOf(replaced);
                    await ExpectScan(step, "the old label still resolves before approval", first, true);
                    report.Expect($"{step}: issuer cannot approve the replacement", "422 separation_of_duties",
                        (await f.Api.Send(printer, $"/api/identification/labels/{second.Id}/approve", new { expectedVersion = 1, reason = "Mine" })).Label, "Authorization");
                    report.Expect($"{step}: replacement approved", "200", (await f.Api.Send(approver, $"/api/identification/labels/{second.Id}/approve", new { expectedVersion = 1, reason = "Independent approval" })).Label);
                    report.Rule(Basis.Spec, Spec + ": approving the replacement ends the old label; the new one is trusted once printed and attached");
                    await ExpectScan(step, "the replaced label no longer resolves", first, false);
                    await ExpectScan(step, "the unprinted replacement does not resolve yet", second, false);
                    report.Expect($"{step}: replacement printed and attached", "200 200 200", await PrintAndAttach(second.Id, 2));
                    await ExpectScan(step, "the attached replacement resolves", second, true);
                    break;
                }

                case "Void":
                    report.Rule(Basis.Spec, Spec + ": a voided label is never trusted again, and cannot be voided twice");
                    report.Expect($"{step}: voided", "200", (await f.Api.Send(printer, $"/api/identification/labels/{second.Id}/void", new { expectedVersion = 2, reason = "Order cancelled at the line" })).Label);
                    await ExpectScan(step, "the voided label no longer resolves", second, false);
                    report.Expect($"{step}: second void refused", "422 label_state",
                        (await f.Api.Send(printer, $"/api/identification/labels/{second.Id}/void", new { expectedVersion = 3, reason = "Again" })).Label);
                    break;
            }

            report.Rule(Basis.Spec, Spec + ": labels are presentation; they never post inventory");
            report.Expect($"{step}: inventory ledger unchanged by labelling", ledger, order == 0 ? ledger : await Invariants.StateHash(f.StoresUser, parts), "Inventory");
            await Invariants.Inventory(report, step, f.StoresUser, parts);
        }

        await Invariants.AuditChain(report, "End", f.Plant.Admin);
        return (report, log, await Invariants.StateHash(f.StoresUser, parts));
    }
}
