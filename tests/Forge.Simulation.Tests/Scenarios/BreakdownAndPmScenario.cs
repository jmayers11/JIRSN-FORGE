using System.Globalization;
using Forge.Integration.Tests;
using Forge.Simulation.Tests.Engine;

namespace Forge.Simulation.Tests.Scenarios;

/// <summary>
/// Scenario 4 (machine breakdown and overdue PM): a lock-out PM plan on SLITTER-A is already past its grace day, then
/// SLITTER-B breaks down. With both slitters out, no slitting order can be scheduled. The PM is done and signed off by a
/// different person (SLITTER-A returns, so ROLL-100 work is schedulable again, while ROLL-300, which only SLITTER-B can
/// run, is not), then the repair is completed and signed off (SLITTER-B returns and everything is on time again).
/// Expectations come from the Phase 4 and 7b specification (outage propagates to capacity, schedule and constraints;
/// an overdue lock-out PM stops the machine; work is signed off by someone else; the machine is back only on sign-off).
/// </summary>
internal static class BreakdownAndPmScenario
{
    public const string Name = "S04-breakdown-and-overdue-pm";
    private const string Spec = "Phase 4 spec §77 and Phase 7b maintenance";
    private const string A = "capacity.SLITTER-A.RUN_SLITTER.executableHours.7d";
    private const string B = "capacity.SLITTER-B.RUN_SLITTER.executableHours.7d";
    private const string AWork = "capacity.SLITTER-A.RUN_SLITTER.workingHours.7d";
    private const string BWork = "capacity.SLITTER-B.RUN_SLITTER.workingHours.7d";

    public static EventLog Plan(int seed)
    {
        var log = new EventLog(Name, seed);
        for (var i = 0; i < 2; i++)
        {
            log.Add(TimeSpan.Zero, "Order", ("part", "ROLL-100"), ("quantity", log.Between(20, 120)), ("dueInDays", log.Between(6, 12)));
        }

        log.Add(TimeSpan.Zero, "Order", ("part", "ROLL-300"), ("quantity", log.Between(20, 80)), ("dueInDays", log.Between(7, 12)));
        log.Add(TimeSpan.FromMinutes(5), "Baseline");
        log.Add(TimeSpan.FromMinutes(10), "OverduePm", ("machine", "SLITTER-A"), ("daysOverdue", log.Between(2, 5)));
        log.Add(TimeSpan.FromMinutes(20), "Breakdown", ("machine", "SLITTER-B"));
        log.Add(TimeSpan.FromMinutes(20 + log.Between(30, 90)), "PmDone", ("machine", "SLITTER-A"));
        log.Add(TimeSpan.FromMinutes(200 + log.Between(30, 120)), "RepairDone", ("machine", "SLITTER-B"));
        return log;
    }

    public static async Task<(SimReport Report, EventLog Log, string Hash)> RunAsync(PostgresFixture postgres, int seed, Mutation mutation = Mutation.None)
    {
        var log = Plan(seed);
        var report = new SimReport(mutation == Mutation.None ? Name : Name + RunSummary.CanaryMarker + mutation, seed);
        await using var f = await SimFactory.BuildPlanningAsync(postgres, "s04-" + seed.ToString(CultureInfo.InvariantCulture));
        report.Evidence(f.Api);
        report.Covers(Coverage.Maintenance, Coverage.Scheduling, Coverage.Reporting);
        await f.PeopleAsync(("mp", "MaintenancePlanner"));
        var (mp, tech, e) = (f.Api.As("mp"), f.Api.As("tech"), f.Plant.Engineer);
        var parts = new[] { SimFactory.BulkPart };
        var orders = new List<(long Number, string Part)>();
        KpiSnapshot baseline = Kpis.Empty, previous = Kpis.Empty;

        async Task<long> WorkOrderAsync(string kind) =>
            (await SimApi.Get(mp, "/api/maintenance/work-orders")).EnumerateArray().Select(v => v.GetProperty("order"))
                .Where(o => o.GetProperty("kind").GetString() == kind && o.GetProperty("status").GetString() != "Closed")
                .Select(o => o.GetProperty("workOrderId").GetInt64()).FirstOrDefault();

        string Statuses(KpiSnapshot k) =>
            $"OnTime {k.Get("schedule.orders.OnTime")}, Late {k.Get("schedule.orders.Late")}, Unschedulable {k.Get("schedule.orders.Unschedulable")}";

        var sinceStart = TimeSpan.Zero;
        foreach (var ev in log.Events)
        {
            f.Clock.Advance(ev.At - sinceStart);
            sinceStart = ev.At;
            var step = $"#{ev.Index} {ev.Type}";
            var flex = orders.Count(o => o.Part == "ROLL-100");
            var wide = orders.Count(o => o.Part == "ROLL-300");
            switch (ev.Type)
            {
                case "Order":
                    report.Rule(Basis.Plan, "Setup: released customer orders");
                    orders.Add((await f.ReleasedOrderAsync(ev.Text("part"), ev.Number("quantity"), (int)ev.Number("dueInDays")), ev.Text("part")));
                    break;

                case "Baseline":
                    baseline = previous = report.Kpi(await Kpis.CollectAsync(f, "baseline"));
                    report.Rule(Basis.Plan, "Plan arithmetic: with both slitters up every order fits before its due date");
                    report.Expect($"{step}: delivery outcome", $"OnTime {orders.Count}, Late 0, Unschedulable 0", Statuses(baseline));
                    break;

                case "OverduePm":
                {
                    var overdue = (int)ev.Number("daysOverdue");
                    report.Rule(Basis.Spec, Spec + ": a maintenance planner creates a lock-out PM plan");
                    var plan = await f.Api.Send(mp, "/api/maintenance/plans", new
                    {
                        machineCode = "SLITTER-A", title = "Knife and guard inspection", intervalDays = 30, firstDue = f.Day(-overdue), durationMinutes = 90,
                        tasks = new[] { "Lock out and tag out", "Inspect and rotate knives", "Check guard interlocks" }, lockOutWhenOverdue = true, graceDays = 1,
                    });
                    report.Expect($"{step}: PM plan recorded", "200", plan.Label);
                    var after = report.Kpi(await Kpis.CollectAsync(f, "after overdue PM"));
                    report.Rule(Basis.Spec, Spec + ": a lock-out PM past its grace day stops the machine until the work is signed off; the other slitter is unaffected");
                    report.Expect($"{step}: SLITTER-A executable hours over 7 days", 0m, after.Get(A), "Capacity");
                    report.Expect($"{step}: SLITTER-B executable hours unchanged", previous.Get(B), after.Get(B), "Capacity");
                    report.Rule(Basis.Plan, "Plan arithmetic: SLITTER-B alone still fits every order before its due date");
                    report.Expect($"{step}: delivery outcome", $"OnTime {orders.Count}, Late 0, Unschedulable 0", Statuses(after));
                    report.ImpactOf("Overdue lock-out PM", previous, after);
                    previous = after;
                    break;
                }

                case "Breakdown":
                {
                    report.Rule(Basis.Spec, Spec + ": recording a breakdown raises a corrective work order");
                    var down = await f.Api.Send(e, "/api/maintenance/machines/SLITTER-B/down", new { description = "Rewind motor tripping", reason = "Observed breakdown" });
                    report.Expect($"{step}: breakdown recorded", "200", down.Label);
                    report.Expect($"{step}: a corrective work order is open", "True", (await WorkOrderAsync("Corrective")) > 0 ? "True" : "False");
                    var after = report.Kpi(await Kpis.CollectAsync(f, "after breakdown"));
                    report.Rule(Basis.Spec, Spec + " §77: with both slitters out there is no slitting capacity, so no order can be scheduled, and the outage is a constraint");
                    report.Expect($"{step}: SLITTER-B executable hours over 7 days", 0m, after.Get(B), "Capacity");
                    report.Expect($"{step}: delivery outcome", $"OnTime 0, Late 0, Unschedulable {orders.Count}", Statuses(after));
                    report.Expect($"{step}: a maintenance constraint is open", "True", after.Get("constraints.open.Maintenance") > 0 ? "True" : "False");
                    report.ImpactOf("Breakdown with the other slitter locked out", previous, after);
                    previous = after;
                    break;
                }

                case "PmDone":
                {
                    var pm = await WorkOrderAsync("Preventive");
                    report.Rule(Basis.Spec, Spec + ": the technician completes every checklist task; only someone else can sign it off");
                    report.Expect($"{step}: PM started", "200", (await f.Api.Send(tech, $"/api/maintenance/work-orders/{pm}/start", new { })).Label);
                    // ADR 0056 §3: the plan's 90-minute job takes its 90 minutes.
                    f.Clock.Advance(TimeSpan.FromMinutes(90));
                    report.Expect($"{step}: PM completed", "200", (await f.Api.Send(tech, $"/api/maintenance/work-orders/{pm}/complete", new { findings = "Knives rotated, guards OK", tasksDone = new[] { true, true, true } })).Label);
                    report.Expect($"{step}: the technician cannot sign off", "403", ((int)(await f.Api.Send(tech, $"/api/maintenance/work-orders/{pm}/sign-off", new { })).Status).ToString(CultureInfo.InvariantCulture), "Authorization");
                    report.Expect($"{step}: the planner signs off", "200", (await f.Api.Send(mp, $"/api/maintenance/work-orders/{pm}/sign-off", new { })).Label);
                    var after = report.Kpi(await Kpis.CollectAsync(f, "after PM signed off"));
                    report.Rule(Basis.Spec, Spec + ": SLITTER-A is back; ROLL-100 work can run on it, ROLL-300 still needs SLITTER-B");
                    report.Expect($"{step}: SLITTER-A executable hours equal working hours", after.Get(AWork), after.Get(A), "Capacity");
                    report.Expect($"{step}: delivery outcome", $"OnTime {flex}, Late 0, Unschedulable {wide}", Statuses(after));
                    report.ImpactOf("PM signed off", previous, after);
                    previous = after;
                    break;
                }

                case "RepairDone":
                {
                    var repair = await WorkOrderAsync("Corrective");
                    report.Rule(Basis.Spec, Spec + ": the machine is back only when the repair is signed off");
                    report.Expect($"{step}: repair started", "200", (await f.Api.Send(tech, $"/api/maintenance/work-orders/{repair}/start", new { })).Label);
                    report.Expect($"{step}: repair completed", "200", (await f.Api.Send(tech, $"/api/maintenance/work-orders/{repair}/complete", new { findings = "Replaced overload relay", tasksDone = Array.Empty<bool>() })).Label);
                    var early = await f.Api.Send(e, "/api/maintenance/machines/SLITTER-B/restore", new { reason = "Looks fine" });
                    report.Expect($"{step}: restoring before sign-off is refused", "422 repair_in_progress", early.Body.Contains("repair_in_progress", StringComparison.Ordinal) ? "422 repair_in_progress" : early.Label);
                    report.Expect($"{step}: the planner signs off the repair", "200", (await f.Api.Send(mp, $"/api/maintenance/work-orders/{repair}/sign-off", new { })).Label);
                    var after = report.Kpi(await Kpis.CollectAsync(f, "after repair signed off"));
                    report.Rule(Basis.Spec, Spec + " §77: both slitters are back and every order is on time again");
                    report.Expect($"{step}: SLITTER-B executable hours equal working hours", after.Get(BWork), after.Get(B), "Capacity");
                    report.Expect($"{step}: delivery outcome", $"OnTime {orders.Count}, Late 0, Unschedulable 0", Statuses(after));
                    report.ImpactOf("Repair signed off", previous, after);
                    report.ImpactOf("Whole breakdown (baseline to final)", baseline, after);
                    break;
                }
            }

            await Invariants.Inventory(report, step, f.StoresUser, parts);
        }

        await Invariants.AuditChain(report, "End", f.Plant.Admin);
        report.Note("ADR 0056 §3: maintenance work takes time; each job runs for its planned duration before it is completed (same-instant completion is not a supported case).");
        return (report, log, await Invariants.StateHash(f.StoresUser, parts));
    }
}
