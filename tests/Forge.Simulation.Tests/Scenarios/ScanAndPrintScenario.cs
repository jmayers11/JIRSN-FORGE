using System.Globalization;
using Forge.Host.Identification;
using Forge.Integration.Tests;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;
using Forge.Quality.Qms;
using Forge.Simulation.Tests.Engine;
using Microsoft.AspNetCore.Hosting;

namespace Forge.Simulation.Tests.Scenarios;

/// <summary>
/// Scenario 14 (incorrect scan and failed print): an operator scans codes that are not Forge's or name nothing, and a
/// label is printed through a printer jam. A wrong scan names nothing and reveals nothing. A label is only trusted once a
/// person has observed it printed and attached: an issued, approved or jammed label does not resolve, a jammed attempt
/// must be reconciled before another print, and a confirmed label can only be printed again through the governed reprint
/// path. Labels and scans never move stock. Expectations come from the V4.1 label lifecycle (independent approval,
/// observed printing and attachment) and identification rules (unknown codes are concealed), never from Forge.
/// </summary>
internal static class ScanAndPrintScenario
{
    public const string Name = "S14-incorrect-scan-and-failed-print";
    private const string Spec = "V4.1 label lifecycle";
    private const string Namespace = "SIM.EXAMPLE";

    public static EventLog Plan(int seed)
    {
        var log = new EventLog(Name, seed);
        log.Add(TimeSpan.Zero, "Order", ("quantity", log.Between(4, 40)), ("dueInDays", log.Between(8, 14)));
        log.Add(TimeSpan.FromMinutes(5), "WrongScan", ("code", $"forge:v1:{Namespace}:order:{log.Between(900_000, 999_999)}"));
        log.Add(TimeSpan.FromMinutes(6), "WrongScan", ("code", "4006381333931"));
        log.Add(TimeSpan.FromMinutes(10), "Issue");
        log.Add(TimeSpan.FromMinutes(15), "PrintJam");
        log.Add(TimeSpan.FromMinutes(25), "Reconcile");
        log.Add(TimeSpan.FromMinutes(30), "PrintAgain");
        return log;
    }

    public static async Task<(SimReport Report, EventLog Log, string Hash)> RunAsync(PostgresFixture postgres, int seed, Mutation mutation = Mutation.None)
    {
        var log = Plan(seed);
        var report = new SimReport(mutation == Mutation.None ? Name : Name + RunSummary.CanaryMarker + mutation, seed);
        await using var f = await SimFactory.BuildPlanningAsync(postgres, "s14-" + seed.ToString(CultureInfo.InvariantCulture), Configure);
        report.Evidence(f.Api);
        report.Covers(Coverage.Identity, Coverage.Printing);
        await SetupAsync(f.Plant);
        var (printer, approver) = (f.Api.As("label-printer"), f.Plant.Approver);
        var parts = new[] { SimFactory.BulkPart, SimFactory.LotPart };
        long order = 0;
        Guid label = Guid.Empty, attempt = Guid.Empty;
        var code = string.Empty;
        var ledger = string.Empty;

        async Task<string> ResolveAsync(string scanned)
        {
            using var response = await SimApi.PostJson(printer, "/api/identification/resolve", new { code = scanned });
            var outcome = new Outcome(response.StatusCode, await response.Content.ReadAsStringAsync());
            if (outcome.Succeeded)
            {
                return "resolves to order " + (outcome.Json.TryGetProperty("orderNumber", out var n) ? n.GetInt64().ToString(CultureInfo.InvariantCulture) : "?");
            }

            return outcome.Body.Contains(scanned, StringComparison.Ordinal) ? outcome.Label + " (code echoed)" : outcome.Label;
        }

        static Guid Entity(Outcome outcome) =>
            outcome.EntityId is { } id ? Guid.Parse(id) : Guid.Empty;

        Task<Outcome> Observe(int version, string outcome, bool printed, bool attached, string observation) =>
            f.Api.Send(printer, $"/api/identification/print-attempts/{attempt}/outcome", new { expectedVersion = version, outcome, observation, observedPrinted = printed, observedAttached = attached, reason = observation });

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

                case "WrongScan":
                {
                    var scanned = ev.Text("code");
                    var answer = await ResolveAsync(scanned);
                    if (scanned.StartsWith("forge:", StringComparison.Ordinal))
                    {
                        report.Rule(Basis.Spec, "V4.1 identification: a well-formed code that names nothing is not found, and the answer does not echo it");
                        report.Expect($"{step} {scanned}: resolve", "404 identification_not_found", answer, "Identity");
                    }
                    else
                    {
                        report.Rule(Basis.Inferred, "A foreign barcode (here an EAN) is refused as invalid (400 or 422) or not found (404), never matched to something, and not echoed");
                        report.Expect($"{step} {scanned}: refused without echo", "True", (answer.StartsWith("400", StringComparison.Ordinal) || answer.StartsWith("404", StringComparison.Ordinal) || answer.StartsWith("422", StringComparison.Ordinal)) && !answer.Contains("echoed", StringComparison.Ordinal) ? "True" : "False", "Identity");
                        report.Note($"{step}: a foreign barcode answered {answer}.");
                    }

                    break;
                }

                case "Issue":
                {
                    report.Rule(Basis.Spec, Spec + ": a label copy is issued from an approved template; the issuer cannot approve it");
                    var issued = await f.Api.Send(printer, "/api/identification/labels", new
                    {
                        subjectCode = $"forge:v1:{Namespace}:order:{order.ToString(CultureInfo.InvariantCulture)}", templateCode = "ORDER-LABEL", templateVersion = 1,
                        symbology = "QR", copies = 1, facilityCode = "PLANT1", reason = "Order label",
                    });
                    report.Expect($"{step}: issued", "200", issued.Label);
                    var copy = (await SimApi.Get(printer, $"/api/identification/labels/issuances/{Entity(issued)}")).GetProperty("copies").EnumerateArray().Single();
                    (label, code) = (copy.GetProperty("labelId").GetGuid(), copy.GetProperty("code").GetString() ?? string.Empty);
                    report.Expect($"{step}: an issued label does not resolve yet", "404 identification_not_found", await ResolveAsync(code), "Identity");
                    report.Expect($"{step}: issuer cannot approve", "422 separation_of_duties",
                        (await f.Api.Send(printer, $"/api/identification/labels/{label}/approve", new { expectedVersion = 1, reason = "Own label" })).Label, "Authorization");
                    report.Expect($"{step}: a second person approves", "200", (await f.Api.Send(approver, $"/api/identification/labels/{label}/approve", new { expectedVersion = 1, reason = "Independent approval" })).Label);
                    report.Rule(Basis.Spec, Spec + ": approval is not evidence that a label exists on the part");
                    report.Expect($"{step}: an approved label does not resolve yet", "404 identification_not_found", await ResolveAsync(code), "Identity");
                    break;
                }

                case "PrintJam":
                {
                    report.Rule(Basis.Plan, "Setup: the printer jams on the first attempt");
                    var printed = await f.Api.Send(printer, $"/api/identification/labels/{label}/print-attempts", new { expectedVersion = 2, printerCode = "LINE-PRINTER-1", reason = "Print order label" });
                    report.Expect($"{step}: print requested", "200", printed.Label);
                    attempt = Entity(printed);
                    report.Expect($"{step}: jam recorded as uncertain", "200", (await Observe(1, "Uncertain", false, false, "Printer jam; output to be inspected")).Label);
                    report.Rule(Basis.Spec, Spec + ": an uncertain attempt must be reconciled before another print, so no second physical label can appear unnoticed");
                    report.Expect($"{step}: another print refused", "422 print_reconciliation_required",
                        (await f.Api.Send(printer, $"/api/identification/labels/{label}/print-attempts", new { expectedVersion = 2, reason = "Try again" })).Label);
                    report.Expect($"{step}: a jammed label does not resolve", "404 identification_not_found", await ResolveAsync(code), "Identity");
                    break;
                }

                case "Reconcile":
                    report.Rule(Basis.Spec, Spec + ": printing and attachment are observed separately; only an attached label resolves");
                    report.Expect($"{step}: printed observed", "200", (await Observe(2, "Confirmed", true, false, "Cleared jam; exactly one correct label printed")).Label);
                    report.Expect($"{step}: printed but not attached does not resolve", "404 identification_not_found", await ResolveAsync(code), "Identity");
                    report.Expect($"{step}: attachment observed", "200", (await Observe(3, "Confirmed", true, true, "Attached to the order traveler")).Label);
                    report.Expect($"{step}: the attached label resolves", "resolves to order " + order.ToString(CultureInfo.InvariantCulture), await ResolveAsync(code), "Identity");
                    break;

                case "PrintAgain":
                    report.Rule(Basis.Spec, Spec + ": a confirmed label is printed again only through the governed reprint path");
                    report.Expect($"{step}: plain print refused", "422 label_reprint_required",
                        (await f.Api.Send(printer, $"/api/identification/labels/{label}/print-attempts", new { expectedVersion = 2, reason = "Extra copy" })).Label);
                    break;
            }

            report.Rule(Basis.Spec, Spec + ": labels and scans are presentation; they never post inventory");
            report.Expect($"{step}: inventory ledger unchanged by labelling", ledger, order == 0 ? ledger : await Invariants.StateHash(f.StoresUser, parts), "Inventory");
            await Invariants.Inventory(report, step, f.StoresUser, parts);
        }

        await Invariants.AuditChain(report, "End", f.Plant.Admin);
        report.Note("Physical printing is out of scope (see Not implemented); the run records attempts and human observations only.");
        return (report, log, await Invariants.StateHash(f.StoresUser, parts));
    }

    private static void Configure(IWebHostBuilder builder)
    {
        builder.UseSetting(CanonicalIdentification.NamespaceConfigurationKey, Namespace);
        builder.UseSetting(QmsService.ConfigurationKey, "SIM-ORG");
    }

    /// <summary>The V4.1 label setup: label authority, an adopted label-copy policy, and an approved order-label template.</summary>
    private static async Task SetupAsync(SlittingPlant.Plant plant)
    {
        string[] permissions =
        [
            Permissions.IdentificationRead, Permissions.IdentificationLabelsManage, Permissions.IdentificationLabelsApprove,
            Permissions.IdentificationLabelsPrint, Permissions.IdentificationAliasesManage, Permissions.OrdersRead, Permissions.InventoryRead,
            Permissions.ResourcesRead, Permissions.EngineeringRead, Permissions.QualityRead, Permissions.QmsRead, Permissions.DocumentsManage,
        ];
        await plant.Ok(plant.Admin, "/api/kernel/roles", new { role = "SimLabelAuthority", displayName = "Simulation label authority", permissions, reason = "Label scenario" });
        foreach (var person in new[] { plant.People["eng"], plant.People["approver"] })
        {
            await plant.Ok(plant.Admin, $"/api/kernel/people/{person}/roles", new { role = "SimLabelAuthority", reason = "Label scenario" });
        }

        await plant.Person("label-printer", "SimLabelAuthority", "QmsPolicyConsumer");
        var life = new QmsLabelLifecycleRules(QmsActiveCopyMode.SingleActive, 1, true, true, true, true, true,
            QmsLabelConfirmation.OperatorObservedPrintingAndAttachment, ["order", "traveler", "operation", "wip", "material"], [new("ORDER-LABEL", 1)],
            [QmsLabelSymbology.Code128, QmsLabelSymbology.QR, QmsLabelSymbology.DataMatrix], true, true, true);
        await QmsPolicyScenario.AdoptAsync(plant, "SIM-LABEL-POLICY", "SIM-LABEL-ADOPTION", "SIM.LABEL.PROCEDURE",
            new(QmsPolicyKind.LabelCopy, "One active label copy, independent approval and observed attachment", plant.People["eng"], "Recorded briefing", "On source change", true,
                new(LabelCopy: new(true, 1, true, life))), QmsPolicyPurpose.LabelCopy, new());
        await plant.Ok(plant.Engineer, "/api/identification/templates", new
        {
            code = "ORDER-LABEL", facilityCode = "PLANT1", width = 1200, height = 320, allowedKinds = new[] { "order", "traveler", "operation", "wip", "material" },
            allowedSymbologies = new[] { "Code128", "QR", "DataMatrix" }, documents = new[] { new { documentNumber = "SIM.LABEL.PROCEDURE", revision = "A" } }, reason = "Controlled order label",
        });
        await plant.Ok(plant.Approver, "/api/identification/templates/ORDER-LABEL/revisions/1/approve", new { expectedVersion = 1, reason = "Independent template approval" });
    }
}
