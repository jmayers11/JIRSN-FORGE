using System.Globalization;
using Forge.Integration.Tests;
using Forge.Simulation.Tests.Engine;

namespace Forge.Simulation.Tests.Scenarios;

/// <summary>
/// Scenario 20 (demand change): a customer orders spools, raises the quantity, a second customer orders more, and the
/// first cancels. After each change MRP plans exactly the spools still wanted and the cores they need beyond stock;
/// a change and a cancellation need a reason, and only demand authority records demand. Expectations come from the
/// Phase 6 specification (independent demand drives MRP lot-for-lot; on-hand stock nets the component; closed or
/// cancelled demand stops counting), computed from the plan, never from Forge.
/// </summary>
internal static class DemandChangeScenario
{
    public const string Name = "S20-demand-change";
    private const string Spec = "Phase 6 demand and MRP";
    private const string Spool = "SIM-SPOOL";
    private const string Core = "SIM-CORE";

    public static EventLog Plan(int seed)
    {
        var log = new EventLog(Name, seed);
        var first = log.Between(20, 60);
        log.Add(TimeSpan.Zero, "Demand", ("which", "first"), ("quantity", first), ("needInDays", log.Between(20, 30)));
        log.Add(TimeSpan.FromHours(1), "Change", ("which", "first"), ("quantity", first + log.Between(5, 30)));
        log.Add(TimeSpan.FromHours(2), "Demand", ("which", "second"), ("quantity", log.Between(10, 40)), ("needInDays", log.Between(20, 30)));
        log.Add(TimeSpan.FromHours(3), "Cancel", ("which", "first"));
        return log;
    }

    public static async Task<(SimReport Report, EventLog Log, string Hash)> RunAsync(PostgresFixture postgres, int seed, Mutation mutation = Mutation.None)
    {
        var log = Plan(seed);
        var report = new SimReport(mutation == Mutation.None ? Name : Name + RunSummary.CanaryMarker + mutation, seed);
        await using var f = await SimFactory.BuildPlanningAsync(postgres, "s20-" + seed.ToString(CultureInfo.InvariantCulture));
        report.Evidence(f.Api);
        report.Covers(Coverage.Demand, Coverage.Mrp);
        var (e, approver, planner) = (f.Plant.Engineer, f.Plant.Approver, f.Planner);
        var coreStock = 15m;
        await f.Plant.Ok(e, "/api/engineering/items", new { partNumber = Core, description = "Spool core", unitOfMeasure = "EA", makeBuy = "Buy", leadTimeDays = 10 });
        await f.Plant.Ok(e, "/api/engineering/items", new { partNumber = Spool, description = "Wound spool", unitOfMeasure = "EA", makeBuy = "Make", leadTimeDays = 2 });
        await f.Plant.Ok(e, $"/api/engineering/items/{Core}/revisions", new { revision = "A" });
        await f.Plant.Ok(e, $"/api/engineering/items/{Spool}/revisions", new { revision = "A" });
        await f.Plant.Ok(e, $"/api/engineering/items/{Spool}/revisions/A/bom", new { lineNumber = 1, componentPartNumber = Core, quantityPer = 1m, scrapPercent = 0m });
        await f.Plant.Ok(e, $"/api/engineering/items/{Spool}/revisions/A/routing", new { sequence = 10, description = "Wind", workCenterCode = "SLIT-WC", activityCode = "RUN_SLITTER", eligibleMachines = new[] { "SLITTER-A" }, setupMinutes = 0m, runMinutesPerUnit = 1m, crewSize = 1 });
        await f.Plant.Ok(approver, $"/api/engineering/items/{Core}/revisions/A/release", new { reason = "Simulation part release" });
        await f.Plant.Ok(approver, $"/api/engineering/items/{Spool}/revisions/A/release", new { reason = "Simulation part release" });
        await f.Plant.Ok(f.StoresUser, "/api/inventory/receipts", new { partNumber = Core, quantity = coreStock, locationCode = SimFactory.Stores, reason = "Opening" });
        var parts = new[] { Core };
        var demands = new Dictionary<string, (long Id, decimal Quantity, string Need)>(StringComparer.Ordinal);

        async Task<(decimal Spools, decimal Cores)> Mrp()
        {
            var output = (await SimApi.Get(planner, "/api/planning/mrp")).GetProperty("output").GetProperty("plannedOrders").EnumerateArray().ToList();
            decimal Sum(string part) => output.Where(p => p.GetProperty("partNumber").GetString() == part).Sum(p => p.GetProperty("quantity").GetDecimal());
            return (Sum(Spool), Sum(Core));
        }

        async Task ExpectMrp(string step)
        {
            var wanted = demands.Values.Sum(d => d.Quantity);
            var (spools, cores) = await Mrp();
            report.Rule(Basis.Spec, Spec + ": MRP plans the open demand lot-for-lot, and cores for it beyond stock");
            report.Expect($"{step}: planned spools", wanted, spools, "Planning");
            report.Expect($"{step}: planned cores", Math.Max(0m, wanted - coreStock), cores, "Planning");
        }

        var sinceStart = TimeSpan.Zero;
        foreach (var ev in log.Events)
        {
            f.Clock.Advance(ev.At - sinceStart);
            sinceStart = ev.At;
            var step = $"#{ev.Index} {ev.Type} {ev.Text("which")}";
            var which = ev.Text("which");
            switch (ev.Type)
            {
                case "Demand":
                {
                    var quantity = ev.Number("quantity");
                    var need = f.Day((int)ev.Number("needInDays"));
                    var body = new { partNumber = Spool, quantity, needDate = need, priority = 3, kind = "CustomerOrder", customer = "Customer " + which, reference = "PO-" + which, reason = "Customer order" };
                    report.Rule(Basis.Spec, Spec + ": only demand authority records demand");
                    report.Expect($"{step}: a maintenance technician cannot record it", "403",
                        ((int)(await f.Api.Send(f.Api.As("tech"), "/api/planning/demand", body)).Status).ToString(CultureInfo.InvariantCulture), "Authorization");
                    var recorded = await f.Api.Send(planner, "/api/planning/demand", body);
                    report.Expect($"{step}: demand recorded", "200", recorded.Label);
                    demands[which] = (recorded.Id, quantity, need);
                    await ExpectMrp(step);
                    break;
                }

                case "Change":
                {
                    var (id, _, need) = demands[which];
                    var quantity = ev.Number("quantity");
                    report.Rule(Basis.Spec, Spec + ": a demand change needs a reason");
                    report.Expect($"{step}: change without a reason refused", "422",
                        ((int)(await f.Api.Send(planner, $"/api/planning/demand/{id}/change", new { quantity, needDate = need, priority = 3 })).Status).ToString(CultureInfo.InvariantCulture));
                    report.Expect($"{step}: quantity raised", "200", (await f.Api.Send(planner, $"/api/planning/demand/{id}/change", new { quantity, needDate = need, priority = 3, reason = "Customer revised PO" })).Label);
                    demands[which] = (id, quantity, need);
                    await ExpectMrp(step);
                    break;
                }

                case "Cancel":
                {
                    var (id, _, _) = demands[which];
                    report.Rule(Basis.Spec, Spec + ": cancelling demand needs a reason; cancelled demand no longer counts and leaves the open list");
                    report.Expect($"{step}: cancel without a reason refused", "422",
                        ((int)(await f.Api.Send(planner, $"/api/planning/demand/{id}/cancel", new { })).Status).ToString(CultureInfo.InvariantCulture));
                    report.Expect($"{step}: cancelled", "200", (await f.Api.Send(planner, $"/api/planning/demand/{id}/cancel", new { reason = "Customer cancelled" })).Label);
                    demands.Remove(which);
                    var open = (await SimApi.Get(planner, "/api/planning/demand")).EnumerateArray().Select(d => d.GetProperty("demandId").GetInt64()).ToHashSet();
                    report.Expect($"{step}: no longer open", "False", open.Contains(id) ? "True" : "False");
                    await ExpectMrp(step);
                    break;
                }
            }

            await Invariants.Inventory(report, step, f.StoresUser, parts);
        }

        await Invariants.AuditChain(report, "End", f.Plant.Admin);
        return (report, log, await Invariants.StateHash(f.StoresUser, parts));
    }
}
