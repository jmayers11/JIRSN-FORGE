using System.Globalization;
using System.Text;
using System.Text.Json;
using Forge.Integration.Tests;
using Forge.Simulation.Tests.Engine;

namespace Forge.Simulation.Tests.Scenarios;

/// <summary>
/// Scenario 8 (receiving with partial inspection and FAI required): a purchased lot arrives into inspection from a new
/// supplier. Nothing is accepted while FAI is pending determination or required without a passing FAI. After a signed FAI
/// it is inspected in part: some accepted, some rejected and returned to the supplier, the rest left for a later
/// inspection. The return ships, a replacement arrives as a new lot (the returned lot number is refused) and is accepted.
/// Expectations come from ADR 0035 (FAI states; partial acceptance; RTV and replacement as a new instance), computed
/// from the plan, never from Forge.
/// </summary>
internal static class ReceivingFaiScenario
{
    public const string Name = "S08-receiving-with-fai";
    private const string Adr = "ADR 0035";
    private const string Part = SimFactory.LotPart;
    private static int upload;

    public static EventLog Plan(int seed)
    {
        var log = new EventLog(Name, seed);
        var received = log.Between(60, 200);
        var rejected = log.Between(5, received / 4);
        var accepted = log.Between(10, received - rejected - 5);
        log.Add(TimeSpan.Zero, "Receive", ("quantity", received), ("lot", "SIM-F1"));
        log.Add(TimeSpan.FromMinutes(20), "InspectPending");
        log.Add(TimeSpan.FromMinutes(40), "DetermineRequired");
        log.Add(TimeSpan.FromHours(3), "RecordFai");
        log.Add(TimeSpan.FromHours(4), "InspectPart", ("accepted", accepted), ("rejected", rejected));
        log.Add(TimeSpan.FromHours(5), "ReturnRejected");
        log.Add(TimeSpan.FromHours(30), "Replacement", ("lot", "SIM-F2"));
        log.Add(TimeSpan.FromHours(31), "InspectRest", ("accepted", received - accepted - rejected));
        return log;
    }

    public static async Task<(SimReport Report, EventLog Log, string Hash)> RunAsync(PostgresFixture postgres, int seed, Mutation mutation = Mutation.None)
    {
        var log = Plan(seed);
        var report = new SimReport(mutation == Mutation.None ? Name : Name + RunSummary.CanaryMarker + mutation, seed);
        await using var f = await SimFactory.BuildAsync(postgres, "s08-" + seed.ToString(CultureInfo.InvariantCulture));
        report.Evidence(f.Api);
        report.Covers(Coverage.Receiving, Coverage.Procurement, Coverage.Mrb, Coverage.Documents);
        await f.PeopleAsync(("bob", "Buyer"));
        var (e, insp, qe, bob, pat, stores) = (f.Plant.Engineer, f.Api.As("insp"), f.Api.As("qe"), f.Api.As("bob"), f.Api.As("pat"), f.StoresUser);
        var parts = new[] { Part };
        long po = 0, receipt = 0, rtv = 0, replacementReceipt = 0;
        decimal rejected = 0m, returnedQty = 0m;
        var expected = new Dictionary<string, decimal>(StringComparer.Ordinal);

        async Task<JsonElement> ReceiptState(long id) =>
            (await SimApi.Get(insp, "/api/inventory/receiving")).EnumerateArray().Single(r => r.GetProperty("receipt").GetProperty("transactionId").GetInt64() == id);

        async Task CheckStock(string step)
        {
            var balances = await Invariants.ReportedBalances(stores, parts);
            foreach (var status in new[] { "Released", "Inspection", "Hold" })
            {
                report.Expect($"{step}: {status}", expected.GetValueOrDefault(status), balances.Where(b => b.Key.Status == status).Sum(b => b.Value));
            }
        }

        // Setup: a new supplier and an approved purchase order for the bracket.
        await f.Plant.Ok(bob, "/api/procurement/suppliers", new { code = "SIMACME", name = "Simulated new supplier" });
        var sinceStart = TimeSpan.Zero;
        foreach (var ev in log.Events)
        {
            f.Clock.Advance(ev.At - sinceStart);
            sinceStart = ev.At;
            var step = $"#{ev.Index} {ev.Type}";
            switch (ev.Type)
            {
                case "Receive":
                {
                    var quantity = ev.Number("quantity");
                    report.Rule(Basis.Plan, "Setup: an approved purchase order and its delivery into inspection");
                    var created = await f.Api.Send(bob, "/api/procurement/purchase-orders", new { supplier = "SIMACME", lines = new[] { new { partNumber = Part, quantity, promisedDate = f.Day(7) } }, reason = "First order from a new supplier" });
                    po = created.Succeeded ? created.Id : 0;
                    report.Expect($"{step}: purchase order approved", "200", (await f.Api.Send(pat, $"/api/procurement/purchase-orders/{po}/approve", new { reason = "Budget approved" })).Label);
                    var received = await f.Api.Send(stores, "/api/inventory/receipts", new { partNumber = Part, quantity, locationCode = SimFactory.Stores, lotNumber = ev.Text("lot"), poNumber = po, inspect = true, revision = "A", reason = "Delivery" });
                    report.Expect($"{step}: received into inspection", "200", received.Label);
                    receipt = received.Succeeded ? received.Id : 0;
                    expected["Inspection"] = quantity;
                    report.Rule(Basis.Spec, Adr + " §1–2: with no determination, FAI is pending, never complete");
                    report.Expect($"{step}: FAI status", "PendingDetermination", (await ReceiptState(receipt)).GetProperty("faiStatus").GetString()!);
                    await CheckStock(step);
                    break;
                }

                case "InspectPending":
                {
                    report.Rule(Basis.Spec, Adr + " §2–3: only NotApplicable, NotRequired and Complete allow acceptance");
                    var refused = await f.Api.Send(insp, $"/api/inventory/receiving/{receipt}/inspections", new { accepted = 1m, rejected = 0m, reason = "Try" });
                    report.Expect($"{step}: acceptance refused while pending", "422", ((int)refused.Status).ToString(CultureInfo.InvariantCulture));
                    report.Rule(Basis.Spec, Adr + " §3: the receiver does not inspect");
                    report.Expect($"{step}: material handler cannot inspect", "403", ((int)(await f.Api.Send(stores, $"/api/inventory/receiving/{receipt}/inspections", new { accepted = 1m, rejected = 0m, reason = "Mine" })).Status).ToString(CultureInfo.InvariantCulture), "Authorization");
                    await CheckStock(step);
                    break;
                }

                case "DetermineRequired":
                {
                    report.Rule(Basis.Spec, Adr + " §1–2: quality planning decides FAI is required; without a passing FAI nothing is accepted");
                    report.Expect($"{step}: determination recorded", "200", (await f.Api.Send(qe, "/api/quality/fai-determinations", new { partNumber = Part, revision = "A", supplierCode = "SIMACME", state = "Required", reason = "New supplier for a build-to-print part" })).Label);
                    report.Expect($"{step}: FAI status", "Required", (await ReceiptState(receipt)).GetProperty("faiStatus").GetString()!);
                    var refused = await f.Api.Send(insp, $"/api/inventory/receiving/{receipt}/inspections", new { accepted = 1m, rejected = 0m, reason = "Try" });
                    report.Expect($"{step}: acceptance refused without a passing FAI", "422", ((int)refused.Status).ToString(CultureInfo.InvariantCulture));
                    break;
                }

                case "RecordFai":
                {
                    report.Rule(Basis.Spec, Adr + " §2: the FAI names a released (signed) report and passes");
                    report.Expect($"{step}: report released", "True", await ReleaseReportAsync(f, "FAI-SIM-BRKT") ? "True" : "False");
                    var results = new[] { new { characteristic = "Hole spacing", requirement = "50 ±0.1", measured = "50.03", pass = true } };
                    report.Expect($"{step}: FAI recorded", "200", (await f.Api.Send(insp, "/api/quality/fai", new { partNumber = Part, revision = "A", supplierCode = "SIMACME", passed = true, results, documentNumber = "FAI-SIM-BRKT", documentRevision = "A", reason = "First article passed" })).Label);
                    report.Expect($"{step}: FAI status", "Complete", (await ReceiptState(receipt)).GetProperty("faiStatus").GetString()!);
                    break;
                }

                case "InspectPart":
                {
                    var accepted = ev.Number("accepted");
                    rejected = ev.Number("rejected");
                    report.Rule(Basis.Spec, Adr + " §3: accepted quantity is released, rejected quantity goes on hold, the rest stays in inspection");
                    report.Expect($"{step}: inspection recorded", "200", (await f.Api.Send(insp, $"/api/inventory/receiving/{receipt}/inspections",
                        new { accepted, rejected, defect = "Burrs on the punched edge", results = new[] { new { characteristic = "Finish", requirement = "No burrs", measured = "Burrs", pass = false } }, reason = "Partial inspection" })).Label);
                    expected["Released"] = accepted;
                    expected["Hold"] = rejected;
                    expected["Inspection"] -= accepted + rejected;
                    await CheckStock(step);
                    report.Rule(Basis.Spec, Adr + " §3: more than is left to inspect is refused");
                    var over = await f.Api.Send(insp, $"/api/inventory/receiving/{receipt}/inspections", new { accepted = expected["Inspection"] + 1m, rejected = 0m, reason = "Too many" });
                    report.Expect($"{step}: over-inspection refused", "422", ((int)over.Status).ToString(CultureInfo.InvariantCulture));
                    break;
                }

                case "ReturnRejected":
                {
                    report.Rule(Basis.Spec, Adr + " §4–5: return to vendor opens an RTV for the rejection; shipping posts it out of hold");
                    var rejection = (await SimApi.Get(qe, "/api/inventory/rejections?open=true")).EnumerateArray().Single().GetProperty("rejectionId").GetInt64();
                    report.Expect($"{step}: disposition", "200", (await f.Api.Send(qe, $"/api/inventory/rejections/{rejection}/disposition", new { disposition = "ReturnToVendor", reason = "Supplier to replace" })).Label);
                    var open = (await SimApi.Get(stores, "/api/inventory/rtvs?open=true")).EnumerateArray().Single();
                    rtv = open.GetProperty("rtvId").GetInt64();
                    returnedQty = open.GetProperty("quantity").GetDecimal();
                    report.Expect($"{step}: RTV supplier is the purchase order's supplier", "SIMACME", open.GetProperty("supplierCode").GetString() ?? "none");
                    report.Expect($"{step}: RTV shipped", "200", (await f.Api.Send(stores, $"/api/inventory/rtvs/{rtv}/ship", new { carrier = "Simulated carrier", reason = "Return" })).Label);
                    expected["Hold"] -= rejected;
                    await CheckStock(step);
                    break;
                }

                case "Replacement":
                {
                    report.Rule(Basis.Spec, Adr + " §5: a replacement is a new instance; the returned lot number is refused");
                    var sameLot = await f.Api.Send(stores, $"/api/inventory/rtvs/{rtv}/replacement", new { quantity = returnedQty, lotNumber = "SIM-F1", locationCode = SimFactory.Stores, reason = "Replacement" });
                    report.Expect($"{step}: replacement under the returned lot refused", "422", ((int)sameLot.Status).ToString(CultureInfo.InvariantCulture));
                    var replaced = await f.Api.Send(stores, $"/api/inventory/rtvs/{rtv}/replacement", new { quantity = returnedQty, lotNumber = ev.Text("lot"), locationCode = SimFactory.Stores, reason = "Replacement" });
                    report.Expect($"{step}: replacement received into inspection", "200", replaced.Label);
                    expected["Inspection"] += returnedQty;
                    await CheckStock(step);
                    replacementReceipt = (await SimApi.Get(insp, "/api/inventory/receiving")).EnumerateArray()
                        .Single(r => r.GetProperty("receipt").GetProperty("lotNumber").GetString() == ev.Text("lot")).GetProperty("receipt").GetProperty("transactionId").GetInt64();
                    report.Expect($"{step}: RTV closed as replaced", "Replaced",
                        (await SimApi.Get(stores, "/api/inventory/rtvs")).EnumerateArray().Single(r => r.GetProperty("rtvId").GetInt64() == rtv).GetProperty("status").GetString()!);
                    break;
                }

                case "InspectRest":
                {
                    report.Rule(Basis.Spec, Adr + " §3: with FAI complete, the rest of the first receipt and the replacement are accepted");
                    report.Expect($"{step}: rest of the first receipt accepted", "200", (await f.Api.Send(insp, $"/api/inventory/receiving/{receipt}/inspections", new { accepted = ev.Number("accepted"), rejected = 0m, reason = "Remaining" })).Label);
                    report.Expect($"{step}: replacement accepted", "200", (await f.Api.Send(insp, $"/api/inventory/receiving/{replacementReceipt}/inspections", new { accepted = returnedQty, rejected = 0m, reason = "Replacement good" })).Label);
                    expected["Released"] += ev.Number("accepted") + returnedQty;
                    expected["Inspection"] = 0m;
                    await CheckStock(step);
                    break;
                }
            }

            await Invariants.Inventory(report, step, stores, parts);
        }

        report.Rule(Basis.Spec, Adr + ": everything ordered is released in the end: the good part of the first lot and the replacement");
        var total = log.Events.Single(x => x.Type == "Receive").Number("quantity");
        report.Expect("End: released equals what was ordered", total, expected.GetValueOrDefault("Released"), "Inventory");
        await Invariants.AuditChain(report, "End", f.Plant.Admin);
        return (report, log, await Invariants.StateHash(stores, parts));
    }

    /// <summary>Uploads a report file, creates its revision and verifies the signature, as a controlled FAI report needs.</summary>
    private static async Task<bool> ReleaseReportAsync(SimFactory f, string number)
    {
        var e = f.Plant.Engineer;
        if (!(await f.Api.Send(e, "/api/engineering/documents", new { number, title = "FAI report " + number, type = "FaiReport", authority = "Reference", reason = "FAI report" })).Succeeded)
        {
            return false;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/engineering/document-files", UriKind.Relative)) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(number + " rev A")) };
        request.Headers.Add("Idempotency-Key", "sim-upload-" + Interlocked.Increment(ref upload).ToString(CultureInfo.InvariantCulture) + "-" + number);
        request.Headers.Add("X-File-Name", "file.pdf");
        using var response = await e.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var sha = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("entityId").GetString()!;
        var revision = await f.Api.Send(e, $"/api/engineering/documents/{number}/revisions", new { revision = "A", fileSha256 = sha, fileName = number + "_A.pdf", reason = "Report revision" });
        var signed = await f.Api.Send(f.Api.As("insp"), $"/api/engineering/documents/{number}/verify-signature",
            new { revision = "A", sourceSigner = "Supplier inspector", sourceSignedAt = f.Clock.GetUtcNow().AddDays(-1), meaning = "I certify these first article results", reason = "Reviewed the signed original report" });
        return revision.Succeeded && signed.Succeeded;
    }
}
