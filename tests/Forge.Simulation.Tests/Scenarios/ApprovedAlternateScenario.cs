using System.Globalization;
using System.Text.Json;
using Forge.Integration.Tests;
using Forge.Simulation.Tests.Engine;

namespace Forge.Simulation.Tests.Scenarios;

/// <summary>
/// Scenario 26 (approved alternate): spools need cores and stores is short. An equivalent core is proposed by a buyer;
/// while it is only proposed MRP buys the shortage. Approved by engineering, MRP still buys while the BOM line only
/// suggests alternates; once engineering makes the line automatic (with a reason), MRP allocates the equivalent instead
/// of buying. On the floor the order is issued the primary first and then the equivalent, never more than it needs, and
/// an unapproved substitute cannot be issued. When the equivalent's approval expires it can no longer be issued and MRP
/// buys again. Expectations come from the V2.6 alternates specification, computed from the plan, never from Forge.
/// </summary>
internal static class ApprovedAlternateScenario
{
    public const string Name = "S26-approved-alternate";
    private const string Spec = "V2.6 alternates";
    private const string Spool = "SIM-SPOOL2";
    private const string Core = "SIM-CORE1";
    private const string Equivalent = "SIM-CORE-EQ";
    private const string Unapproved = "SIM-CORE-X";

    public static EventLog Plan(int seed)
    {
        var log = new EventLog(Name, seed);
        var need = log.Between(15, 40);
        log.Add(TimeSpan.Zero, "Stock", ("need", need), ("core", log.Between(2, need - 5)), ("equivalent", need + log.Between(0, 20)));
        log.Add(TimeSpan.FromMinutes(10), "Propose");
        log.Add(TimeSpan.FromMinutes(20), "Approve");
        log.Add(TimeSpan.FromMinutes(30), "Automatic");
        log.Add(TimeSpan.FromHours(1), "Issue");
        log.Add(TimeSpan.FromHours(2), "Expire");
        return log;
    }

    public static async Task<(SimReport Report, EventLog Log, string Hash)> RunAsync(PostgresFixture postgres, int seed, Mutation mutation = Mutation.None)
    {
        var log = Plan(seed);
        var report = new SimReport(mutation == Mutation.None ? Name : Name + RunSummary.CanaryMarker + mutation, seed);
        await using var f = await SimFactory.BuildPlanningAsync(postgres, "s26-" + seed.ToString(CultureInfo.InvariantCulture));
        report.Evidence(f.Api);
        report.Covers(Coverage.Allocation, Coverage.Mrp, Coverage.EngineeringChange, Coverage.Warehouse);
        await f.PeopleAsync(("bob", "Buyer"));
        var (e, approver, planner, bob, stores) = (f.Plant.Engineer, f.Plant.Approver, f.Planner, f.Api.As("bob"), f.StoresUser);
        foreach (var part in new[] { Core, Equivalent, Unapproved })
        {
            await f.Plant.Ok(e, "/api/engineering/items", new { partNumber = part, description = part, unitOfMeasure = "EA", makeBuy = "Buy", leadTimeDays = 10 });
            await f.Plant.Ok(e, $"/api/engineering/items/{part}/revisions", new { revision = "A" });
            await f.Plant.Ok(approver, $"/api/engineering/items/{part}/revisions/A/release", new { reason = "Baseline" });
        }

        await f.Plant.Ok(e, "/api/engineering/items", new { partNumber = Spool, description = "Spool", unitOfMeasure = "EA", makeBuy = "Make", leadTimeDays = 2 });
        await f.Plant.Ok(e, $"/api/engineering/items/{Spool}/revisions", new { revision = "A" });
        await f.Plant.Ok(e, $"/api/engineering/items/{Spool}/revisions/A/bom", new { lineNumber = 1, componentPartNumber = Core, quantityPer = 1m, scrapPercent = 0m });
        await f.Plant.Ok(e, $"/api/engineering/items/{Spool}/revisions/A/routing", new { sequence = 10, description = "Wind", workCenterCode = "SLIT-WC", activityCode = "RUN_SLITTER", eligibleMachines = new[] { "SLITTER-A" }, setupMinutes = 0m, runMinutesPerUnit = 1m, crewSize = 1 });
        await f.Plant.Ok(approver, $"/api/engineering/items/{Spool}/revisions/A/release", new { reason = "Baseline" });
        var parts = new[] { Core, Equivalent, Unapproved };
        decimal need = 0m, core = 0m, equivalent = 0m;
        long order = 0, alternate = 0;
        var allocates = false;

        async Task<JsonElement> Mrp() => (await SimApi.Get(planner, "/api/planning/mrp")).GetProperty("output");

        async Task ExpectMrp(string step)
        {
            var mrp = await Mrp();
            var planned = mrp.GetProperty("plannedOrders").EnumerateArray().Where(p => p.GetProperty("partNumber").GetString() == Core).Sum(p => p.GetProperty("quantity").GetDecimal());
            var substituted = mrp.GetProperty("substitutions").EnumerateArray().Where(s => s.GetProperty("alternatePart").GetString() == Equivalent).Sum(s => s.GetProperty("alternateQuantity").GetDecimal());
            var shortage = need - core;
            report.Rule(Basis.Spec, Spec + ": MRP allocates an approved equivalent only where the BOM line is automatic; otherwise it buys the shortage");
            report.Expect($"{step}: planned purchase of cores", allocates ? Math.Max(0m, shortage - equivalent) : shortage, planned, "Allocation");
            report.Expect($"{step}: equivalent allocated", allocates ? Math.Min(shortage, equivalent) : 0m, substituted, "Allocation");
        }

        var sinceStart = TimeSpan.Zero;
        foreach (var ev in log.Events)
        {
            f.Clock.Advance(ev.At - sinceStart);
            sinceStart = ev.At;
            var step = $"#{ev.Index} {ev.Type}";
            switch (ev.Type)
            {
                case "Stock":
                    (need, core, equivalent) = (ev.Number("need"), ev.Number("core"), ev.Number("equivalent"));
                    report.Rule(Basis.Plan, "Setup: cores and equivalents in stores, and a spool order needing more cores than stores has");
                    report.Expect($"{step}: stock received", "200 200 200",
                        $"{(await f.Api.Send(stores, "/api/inventory/receipts", new { partNumber = Core, quantity = core, locationCode = SimFactory.Stores, reason = "Opening" })).Label} {(await f.Api.Send(stores, "/api/inventory/receipts", new { partNumber = Equivalent, quantity = equivalent, locationCode = SimFactory.Stores, reason = "Opening" })).Label} {(await f.Api.Send(stores, "/api/inventory/receipts", new { partNumber = Unapproved, quantity = 50m, locationCode = SimFactory.Stores, reason = "Opening" })).Label}");
                    var created = await f.Api.Send(e, "/api/orders/", new { partNumber = Spool, quantity = need, dueDate = f.Day(12), priority = 3 });
                    report.Expect($"{step}: order created", "200", created.Label);
                    order = created.Id;
                    await ExpectMrp(step);
                    break;

                case "Propose":
                {
                    report.Rule(Basis.Spec, Spec + ": a buyer proposes an equivalent with evidence; a planner cannot; a proposed alternate changes nothing");
                    report.Expect($"{step}: planner cannot propose", "403",
                        ((int)(await f.Api.Send(planner, "/api/engineering/alternates", new { primaryPart = Core, alternatePart = Equivalent, kind = "Equivalent", factor = 1m })).Status).ToString(CultureInfo.InvariantCulture), "Authorization");
                    var proposed = await f.Api.Send(bob, "/api/engineering/alternates", new { primaryPart = Core, alternatePart = Equivalent, kind = "Equivalent", factor = 1m, evidence = "Same drawing, lot test TR-SIM" });
                    report.Expect($"{step}: proposed", "200", proposed.Label);
                    alternate = proposed.Succeeded ? proposed.Id : 0;
                    report.Expect($"{step}: a substitute needing engineering approval proposed", "200",
                        (await f.Api.Send(bob, "/api/engineering/alternates", new { primaryPart = Core, alternatePart = Unapproved, kind = "EngineeringApprovalRequired", qualityNotes = "Different coating" })).Label);
                    await ExpectMrp(step);
                    break;
                }

                case "Approve":
                    report.Rule(Basis.Spec, Spec + ": engineering approval, not the proposer; approved but only suggested on the line, MRP still buys");
                    report.Expect($"{step}: buyer cannot approve", "403", ((int)(await f.Api.Send(bob, $"/api/engineering/alternates/{alternate}/approve", new { reason = "Mine" })).Status).ToString(CultureInfo.InvariantCulture), "Authorization");
                    report.Expect($"{step}: approved", "200", (await f.Api.Send(approver, $"/api/engineering/alternates/{alternate}/approve", new { reason = "TR-SIM reviewed" })).Label, "EngineeringChange");
                    await ExpectMrp(step);
                    break;

                case "Automatic":
                {
                    var policy = $"/api/engineering/items/{Spool}/revisions/A/bom/1/policy";
                    report.Rule(Basis.Spec, Spec + ": making a released BOM line automatic is an engineering decision with a reason");
                    report.Expect($"{step}: engineer cannot change the policy", "403", ((int)(await f.Api.Send(e, policy, new { policy = "Automatic", reason = "x" })).Status).ToString(CultureInfo.InvariantCulture), "EngineeringChange");
                    report.Expect($"{step}: without a reason refused", "422 reason_required", (await f.Api.Send(approver, policy, new { policy = "Automatic" })).Label, "EngineeringChange");
                    report.Expect($"{step}: line made automatic", "200", (await f.Api.Send(approver, policy, new { policy = "Automatic", reason = "Equivalents qualified for spools" })).Label, "EngineeringChange");
                    allocates = true;
                    await ExpectMrp(step);
                    break;
                }

                case "Issue":
                {
                    var issue = "/api/inventory/issues";
                    report.Expect($"{step}: order released", "200", (await f.Api.Send(planner, $"/api/orders/{order}/release", new { reason = "Customer order" })).Label);
                    report.Rule(Basis.Spec, Spec + ": only approved alternates are issued, named as such, and never beyond what the order needs");
                    report.Expect($"{step}: unapproved substitute refused", "422 alternate_not_approved",
                        (await f.Api.Send(stores, issue, new { orderNumber = order, partNumber = Unapproved, quantity = 1m, locationCode = SimFactory.Stores, alternateFor = Core })).Label, "Allocation");
                    report.Expect($"{step}: equivalent not named as an alternate refused", "422 not_on_order",
                        (await f.Api.Send(stores, issue, new { orderNumber = order, partNumber = Equivalent, quantity = 1m, locationCode = SimFactory.Stores })).Label, "Allocation");
                    report.Expect($"{step}: all primary cores issued", "200", (await f.Api.Send(stores, issue, new { orderNumber = order, partNumber = Core, quantity = core, locationCode = SimFactory.Stores })).Label, "Allocation");
                    report.Expect($"{step}: more equivalent than the order still needs refused", "422 over_issue",
                        (await f.Api.Send(stores, issue, new { orderNumber = order, partNumber = Equivalent, quantity = need - core + 1m, locationCode = SimFactory.Stores, alternateFor = Core })).Label, "Allocation");
                    report.Expect($"{step}: the rest issued as the equivalent", "200",
                        (await f.Api.Send(stores, issue, new { orderNumber = order, partNumber = Equivalent, quantity = need - core, locationCode = SimFactory.Stores, alternateFor = Core })).Label, "Allocation");
                    report.Expect($"{step}: nothing more can be issued", "422 over_issue",
                        (await f.Api.Send(stores, issue, new { orderNumber = order, partNumber = Core, quantity = 1m, locationCode = SimFactory.Stores })).Label, "Allocation");
                    report.Rule(Basis.Spec, "Inventory: stores hold what was received minus what was issued");
                    var balances = await Invariants.ReportedBalances(stores, parts);
                    report.Expect($"{step}: cores left", 0m, balances.Where(b => b.Key.Part == Core).Sum(b => b.Value), "Warehouse");
                    report.Expect($"{step}: equivalents left", equivalent - (need - core), balances.Where(b => b.Key.Part == Equivalent).Sum(b => b.Value), "Warehouse");
                    (core, equivalent) = (0m, equivalent - (need - core));
                    break;
                }

                case "Expire":
                    report.Rule(Basis.Spec, Spec + ": an expired alternate is no longer issued or offered");
                    report.Expect($"{step}: expiry needs a reason", "422 reason_required", (await f.Api.Send(approver, $"/api/engineering/alternates/{alternate}/expire", new { })).Label, "EngineeringChange");
                    report.Expect($"{step}: expired", "200", (await f.Api.Send(approver, $"/api/engineering/alternates/{alternate}/expire", new { reason = "Supplier lost its approval" })).Label, "EngineeringChange");
                    var offered = (await SimApi.Get(planner, $"/api/engineering/alternates/candidates/{Core}")).GetProperty("approved").EnumerateArray().Any(c => c.GetProperty("offeredPart").GetString() == Equivalent);
                    report.Expect($"{step}: no longer offered", "False", offered ? "True" : "False", "Allocation");
                    break;
            }

            await Invariants.Inventory(report, step, stores, parts);
        }

        await Invariants.AuditChain(report, "End", f.Plant.Admin);
        return (report, log, await Invariants.StateHash(stores, parts));
    }
}
