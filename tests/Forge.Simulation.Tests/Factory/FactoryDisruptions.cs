using System.Globalization;
using System.Text.Json;
using Forge.Simulation.Tests.Engine;

namespace Forge.Simulation.Tests.Factory;

/// <summary>One disruption in the library: when, what, the module it starts in (I) and where its consequences should show (X).</summary>
internal sealed record Disruption(int Day, string Name, string Area, string Expected, Func<Task<string?>> Apply);

/// <summary>
/// The disruptive events of the disrupted run, placed at fixed fractions of the run so a short run keeps the same story.
/// Each is applied in the morning, before the day's schedule, by the person who would do it in the plant. Each is read
/// before and after (proposed schedule outcome, capacity, readiness), which gives its immediate impact; the baseline run
/// on the same seed gives its 7, 30 and 90-day consequences.
/// </summary>
internal sealed class FactoryDisruptions
{
    private readonly FactoryRun run;
    private readonly List<Disruption> plan = [];
    private long hold;
    private string? heldLot;
    private int lapseFrom = -1;

    public FactoryDisruptions(FactoryRun run)
    {
        this.run = run;
        var spec = run.Spec;
        var days = spec.Size.Days;

        int D(double fraction) => Math.Min(days - 1, FactorySpec.Weekday(Math.Clamp((int)Math.Round(fraction * days), 1, Math.Max(1, days - 2)), forward: true));
        int After(int from, int n) => Math.Min(days - 1, FactorySpec.Weekday(from + n, forward: true));
        string Worker(string area, string shift, int skip = 0) =>
            spec.Workers.Where(w => w.Department == area && w.Shift == shift && !w.Existing).Select(w => w.Subject).Skip(skip).FirstOrDefault()
            ?? spec.Workers.First(w => w.Department == area).Subject;

        // Workforce: a welder calls out sick for three days.
        var callout = D(0.05);
        var welder = Worker("WELDING", FactorySpec.Day);
        Add(callout, $"{welder} (welder, day shift) calls out sick for 3 days", Coverage.Workforce, "Welding capacity and the two-person rule; welded products late; delivery",
            () => AwayAsync(welder, callout, callout + 2, "CallOut", "Called out sick"));

        // Maintenance: CNC-1 breaks down; repaired two days later.
        var breakdown = D(0.08);
        Add(breakdown, "CNC-1 breaks down", Coverage.Maintenance, "Machining capacity; frames, shafts and housings; downstream assembly and delivery", () => DownAsync("CNC-1", "Spindle fault"));
        Add(After(breakdown, 2), "CNC-1 repaired and signed off", Coverage.Maintenance, "Machining capacity back", () => RepairAsync("CNC-1"));

        // Procurement: the steel supplier slips a delivery by ten days; the approved equivalent covers.
        Add(D(0.12), "Steel supplier slips a delivery by 10 days", Coverage.Procurement, "MRP, material readiness, alternate issue (SHEET-STEEL-EQ), schedule", SlipAsync);

        // Training: a tester's TEST-I is suspended for a week.
        var suspension = D(0.18);
        var tester = Worker("TESTING", FactorySpec.Day);
        Add(suspension, $"{tester}'s TEST-I suspended", Coverage.Training, "Test capacity; inspection queue; delivery", () => SuspendAsync(tester, "TEST-I"));
        Add(After(suspension, 7), $"{tester} recertified for TEST-I", Coverage.Training, "Test capacity back", () => RecertifyAsync(tester, "TEST-I"));

        // Demand: an urgent customer order; a customer raises an order and another cancels.
        Add(D(0.2), "Urgent customer order (priority 1, due in 6 days)", Coverage.Demand, "Schedule sequence; other orders pushed; MRP", UrgentAsync);
        Add(D(0.25), "A customer raises an order by half; another cancels", Coverage.Demand, "MRP replans; cancelled order's capacity freed; delivery", ChangeAndCancelAsync);

        // Holds: a quality hold on a steel lot, released four days later.
        var holdDay = D(0.3);
        Add(holdDay, "Quality hold on a sheet-steel lot", Coverage.Holds, "Material availability; alternate issue; MRP", HoldAsync);
        Add(After(holdDay, 4), "Hold on the sheet-steel lot released", Coverage.Holds, "Material available again", ReleaseHoldAsync);

        // Calibration: the test gauge is not recalibrated before it falls due; it is recalibrated three days late.
        var due = spec.GaugeDueDay;
        RecalibrationDay = Math.Min(days - 1, FactorySpec.Weekday(due + 3, forward: true));
        if (due < days)
        {
            lapseFrom = due;
            Add(due, "Test gauge TG-1 calibration lapses (not recalibrated in time)", Coverage.Calibration, "Inspections refused; packing waits; delivery", () => Task.FromResult<string?>(null));
            Add(RecalibrationDay, "Test gauge TG-1 recalibrated (late)", Coverage.Calibration, "Inspection queue clears", CheckLapseAsync);
        }

        // Warehouse: a cycle count finds fastener kits short.
        Add(D(0.4), "Cycle count finds fastener kits 10% short", Coverage.Warehouse, "Stock; MRP buys more; assembly readiness", CountAsync);

        // Maintenance: a preventive job is skipped; the machine locks out after its grace days, and the job is done a week late.
        if (SkippedPm(D(0.45)) is { } pm)
        {
            run.SkipPmUntil[pm.Machine] = pm.Due + 7;
            Add(pm.Due, $"{pm.Machine}'s preventive maintenance is skipped", Coverage.Maintenance, "Lock-out after the grace days; capacity", () => Task.FromResult<string?>(null));
            Add(After(pm.Due, 3), $"{pm.Machine} locked out (PM overdue past its grace days)", Coverage.Maintenance, "Capacity of its department; schedule", () => Task.FromResult<string?>(null));
        }

        // Quality: the test rig drifts and a quarter of tests fail for four days.
        var drift = D(0.5);
        Add(drift, "Test failures rise to 25% for 4 days", Coverage.Mrb, "NCRs; concessions; packing waits; delivery", () => { run.InspectionFailure = 0.25; return Task.FromResult<string?>(null); });
        Add(After(drift, 4), "Test failures back to normal", Coverage.Mrb, "NCR queue clears", () => { run.InspectionFailure = 0.02; return Task.FromResult<string?>(null); });

        // Network: for one day every seventh floor report loses its response and is retried with the same key.
        var network = D(0.6);
        Add(network, "Network drops floor responses for a day (retried with the same key)", Coverage.Network, "Quantities counted once", () => { run.LoseEveryNthReport = 7; return Task.FromResult<string?>(null); });
        Add(After(network, 1), "Network restored", Coverage.Network, "-", () => { run.LoseEveryNthReport = 0; return Task.FromResult<string?>(null); });

        // Concurrency: two terminals report the same final completion at once.
        Add(D(0.65), "Two terminals report the same completion at once", Coverage.Concurrency, "Exactly one report; quantities", () => { run.RaceNextFinalReport = true; return Task.FromResult<string?>(null); });

        // Workforce: two swing-shift assemblers move to Packing (day shift) for a week.
        var transfer = D(0.7);
        var movers = new[] { Worker("ASSEMBLY", FactorySpec.Swing), Worker("ASSEMBLY", FactorySpec.Swing, 1) };
        Add(transfer, "Two swing-shift assemblers move to Packing for a week", Coverage.Workforce, "Assembly capacity down, packing up; schedule", () => TransferAsync(movers, "PACKING", FactorySpec.Day));
        Add(After(transfer, 7), "The assemblers return to Assembly", Coverage.Workforce, "Capacity back", () => TransferAsync(movers, "ASSEMBLY", FactorySpec.Swing));

        // Maintenance: WELD-1 is down for four days.
        var weld = D(0.8);
        Add(weld, "WELD-1 breaks down", Coverage.Maintenance, "Welding capacity; welded products late", () => DownAsync("WELD-1", "Wire feeder failure"));
        Add(After(weld, 4), "WELD-1 repaired and signed off", Coverage.Maintenance, "Welding capacity back", () => RepairAsync("WELD-1"));

        // Workforce: three machinists (one per shift) on approved PTO for five days.
        var pto = D(0.85);
        var machinists = new[] { Worker("MACHINING", FactorySpec.Day), Worker("MACHINING", FactorySpec.Swing), Worker("MACHINING", FactorySpec.Night) };
        Add(pto, "Three machinists on approved PTO for 5 days", Coverage.Workforce, "Machining capacity; intermediates; delivery",
            async () =>
            {
                foreach (var m in machinists.Distinct())
                {
                    await AwayAsync(m, pto, pto + 4, "ApprovedPto", "Approved leave");
                }

                return null;
            });

        // Scheduling: a customer escalation raises an existing order to priority 1.
        Add(D(0.9), "An existing order is expedited to priority 1", Coverage.Scheduling, "Schedule sequence; other orders pushed", ExpediteAsync);
    }

    /// <summary>The day the quality engineer recalibrates the gauge in the disrupted run (after it lapses).</summary>
    public int RecalibrationDay { get; }

    /// <summary>The disruption library, for the report (output 3).</summary>
    public IReadOnlyList<Disruption> Plan => plan;

    private void Add(int day, string name, string area, string expected, Func<Task<string?>> apply) => plan.Add(new Disruption(day, name, area, expected, apply));

    /// <summary>Applies the day's disruptions, measuring the proposed schedule, capacity and readiness before and after. Returns their names.</summary>
    public async Task<string> ApplyAsync(int day)
    {
        var due = plan.Where(e => e.Day == day).ToList();
        if (due.Count == 0)
        {
            return string.Empty;
        }

        var names = string.Join("; ", due.Select(e => e.Name));
        var before = run.Report.Kpi(await run.GlanceAsync($"day {day + 1} before: {names}"));
        var notes = new List<string>();
        foreach (var e in due)
        {
            run.Report.Covers(e.Area);
            if (await e.Apply() is { } note)
            {
                notes.Add(note);
            }
        }

        var after = run.Report.Kpi(await run.GlanceAsync($"day {day + 1} after: {names}"));
        run.Report.ImpactOf($"Day {day + 1}: {names}", before, after);
        foreach (var n in notes)
        {
            run.Report.Note($"Day {day + 1}: {n}");
        }

        return names;
    }

    private async Task<string?> AwayAsync(string subject, int from, int to, string kind, string reason)
    {
        var p = run.Plant;
        var window = Crew.Window(p.Clock.Start.AddDays(from), p.Clock.Start.AddDays(to + 1).AddHours(8), kind, reason);
        var recorded = await run.Cmd(p.Engineer, $"/api/workforce/workers/{p.PersonId(subject)}/unavailability", window, "unavailability");
        run.Report.Rule(Basis.Plan, "Setup: a supervisor records the absence");
        run.Report.Expect($"Day {from + 1}: {kind} recorded for {subject}", "200", recorded.Label, "Workforce");
        run.Away[subject] = (from, to);
        return null;
    }

    private async Task<string?> DownAsync(string machine, string description)
    {
        var p = run.Plant;
        var down = await run.Cmd(p.Engineer, $"/api/maintenance/machines/{machine}/down", new { description, reason = "Observed breakdown" }, "machine down");
        run.Report.Rule(Basis.Plan, "Setup: the breakdown is recorded");
        run.Report.Expect($"Day {run.Today + 1}: {machine} breakdown recorded", "200", down.Label, "Maintenance");
        run.Down.Add((machine, p.Clock.GetUtcNow(), null));
        return null;
    }

    private async Task<string?> RepairAsync(string machine)
    {
        var p = run.Plant;
        var orders = await p.Api.Read(p.MaintenancePlanner, "/api/maintenance/work-orders");
        var repair = orders.EnumerateArray().Select(v => v.GetProperty("order"))
            .Where(o => o.GetProperty("kind").GetString() == "Corrective" && o.GetProperty("status").GetString() is "Open" or "Scheduled"
                && o.TryGetProperty("machineCode", out var m) && m.GetString() == machine)
            .Select(o => o.GetProperty("workOrderId").GetInt64()).FirstOrDefault();
        if (repair == 0)
        {
            run.Report.Finding($"Day {run.Today + 1}: no open corrective work order for {machine} after its breakdown");
            return $"no corrective work order found for {machine}";
        }

        await run.DoWorkOrderAsync(repair, "Repaired and tested", 0, TimeSpan.FromMinutes(30));
        var index = run.Down.FindLastIndex(d => d.Machine == machine && d.To is null);
        if (index >= 0)
        {
            run.Down[index] = run.Down[index] with { To = p.Clock.GetUtcNow() };
        }

        var state = await Crew.MachineAsync(p.F, machine, run.Spec.Departments.Single(d => d.Machines.Contains(machine)).Activity);
        run.Report.Rule(Basis.Spec, "Phase 4 spec §77 and Phase 7b maintenance: the machine is back only when the repair is signed off");
        var stillDown = !state.Executable && (state.Constraint.Contains("Equipment", StringComparison.Ordinal) || state.Constraint.Contains("Maintenance", StringComparison.Ordinal));
        run.Report.Expect($"Day {run.Today + 1}: {machine} after sign-off", "no longer down", stillDown ? Crew.Label(state) : "no longer down", "Maintenance");
        return null;
    }

    private async Task<string?> SlipAsync()
    {
        var p = run.Plant;
        var po = run.Pos.Where(x => !x.Received && x.Part == "SHEET-STEEL" && x.PromisedDay >= run.Today).OrderBy(x => x.PromisedDay).FirstOrDefault();
        if (po is null)
        {
            return "no open sheet-steel purchase order to slip";
        }

        var changed = await run.Cmd(p.Buyer, $"/api/procurement/purchase-orders/{po.Po}/lines/1/date", new { promisedDate = run.Date(po.PromisedDay + 10), reason = "Supplier confirmed a delay" }, "PO date change");
        run.Report.Rule(Basis.Plan, "Setup: the buyer records the supplier's new date");
        run.Report.Expect($"Day {run.Today + 1}: PO {po.Po} promised date moved", "200", changed.Label, "Procurement");
        if (changed.Succeeded)
        {
            po.PromisedDay += 10;
        }

        return $"PO {po.Po} ({Invariants.Q(po.Quantity)} kg sheet steel) moved to day {po.PromisedDay + 1}";
    }

    private async Task<string?> SuspendAsync(string subject, string qualification)
    {
        var p = run.Plant;
        var suspended = await run.Cmd(p.Approver, $"/api/competency/people/{p.PersonId(subject)}/suspensions", new { code = qualification, reason = "Retraining after a test escape" }, "suspension");
        run.Report.Rule(Basis.Plan, "Setup: the certifier suspends the qualification");
        run.Report.Expect($"Day {run.Today + 1}: {qualification} suspended for {subject}", "200", suspended.Label, "Training");
        run.Suspended.Add((subject, qualification));
        return null;
    }

    private async Task<string?> RecertifyAsync(string subject, string qualification)
    {
        var p = run.Plant;
        var certified = await run.Cmd(p.Approver, $"/api/competency/people/{p.PersonId(subject)}/certifications", new { code = qualification, evidence = "Retraining completed and assessed", reason = "Reassessment passed" }, "certification");
        run.Report.Expect($"Day {run.Today + 1}: {qualification} recertified for {subject}", "200", certified.Label, "Training");
        run.Suspended.Remove((subject, qualification));
        return null;
    }

    private async Task<string?> UrgentAsync()
    {
        var p = run.Plant;
        var spec = new DemandSpec(9001, run.Today, "Customer 01", "PROD-03", 40m, run.Today + 6, 1);
        var d = new DemandState(spec);
        var recorded = await run.Cmd(p.Planner, "/api/planning/demand", new
        {
            partNumber = spec.Part, quantity = spec.Quantity, needDate = run.Date(spec.NeedDay), priority = 1, kind = "CustomerOrder", customer = spec.Customer, reference = "CO-URGENT", reason = "Line-down at the customer",
        }, "demand");
        run.Report.Rule(Basis.Plan, "Setup: the planner records the urgent order");
        run.Report.Expect($"Day {run.Today + 1}: urgent demand recorded", "200", recorded.Label, "Demand");
        if (recorded.Succeeded)
        {
            d.DemandId = recorded.Id;
            run.Demands.Add(d);
            if (await run.CreateOrderAsync(spec.Part, spec.Quantity, spec.NeedDay, 1, finished: true) is { } order)
            {
                d.Orders.Add(order);
            }
        }

        return null;
    }

    private async Task<string?> ChangeAndCancelAsync()
    {
        var p = run.Plant;
        var notes = new List<string>();
        bool NotStarted(DemandState d) => d.Orders.All(o => run.Orders.TryGetValue(o, out var s) && s.Good.Count == 0 && !s.Issued);
        var raise = run.Demands.FirstOrDefault(d => d.Open && d.Shipped == 0 && d.NeedDay > run.Today + 10 && d.Spec.Index < 9000);
        if (raise is not null)
        {
            var extra = Math.Ceiling(raise.Quantity / 2m);
            var changed = await run.Cmd(p.Planner, $"/api/planning/demand/{raise.DemandId}/change", new { quantity = raise.Quantity + extra, needDate = run.Date(raise.NeedDay), priority = raise.Priority, reason = "Customer raised the order" }, "demand change");
            run.Report.Rule(Basis.Plan, "Setup: the planner records the change and orders the extra quantity");
            run.Report.Expect($"Day {run.Today + 1}: demand {raise.Spec.Index} raised", "200", changed.Label, "Demand");
            if (changed.Succeeded)
            {
                raise.Quantity += extra;
                if (await run.CreateOrderAsync(raise.Spec.Part, extra, raise.NeedDay, raise.Priority, finished: true) is { } order)
                {
                    raise.Orders.Add(order);
                }

                notes.Add($"demand {raise.Spec.Index} raised by {Invariants.Q(extra)}");
            }
        }

        var cancel = run.Demands.FirstOrDefault(d => d != raise && d.Open && d.Shipped == 0 && d.NeedDay > run.Today + 5 && d.Spec.Index < 9000 && NotStarted(d));
        if (cancel is not null)
        {
            var cancelled = await run.Cmd(p.Planner, $"/api/planning/demand/{cancel.DemandId}/cancel", new { reason = "Customer cancelled" }, "demand cancel");
            run.Report.Expect($"Day {run.Today + 1}: demand {cancel.Spec.Index} cancelled", "200", cancelled.Label, "Demand");
            if (cancelled.Succeeded)
            {
                cancel.Cancelled = true;
                foreach (var order in cancel.Orders)
                {
                    var outcome = await run.Cmd(p.Planner, $"/api/orders/{order}/cancel", new { reason = "Customer cancelled" }, "order cancel");
                    run.Report.Expect($"Day {run.Today + 1}: order {order} cancelled with its demand", "200", outcome.Label, "Demand");
                    if (outcome.Succeeded)
                    {
                        run.Orders[order].Cancelled = true;
                    }
                }

                notes.Add($"demand {cancel.Spec.Index} cancelled");
            }
        }

        return notes.Count == 0 ? "no suitable demand to change or cancel" : string.Join("; ", notes);
    }

    private async Task<string?> HoldAsync()
    {
        var p = run.Plant;
        var lots = (await Invariants.ReportedBalances(p.Stores, ["SHEET-STEEL"])).Where(b => b.Value > 0 && b.Key.Status == "Released" && b.Key.Lot is not null)
            .OrderByDescending(b => b.Value).Select(b => b.Key.Lot!).ToList();
        if (lots.Count == 0)
        {
            return "no sheet-steel lot in stock to hold";
        }

        heldLot = lots[0];
        var placed = await run.Cmd(p.QualityEngineer, "/api/holds", new { kind = "Quality", targetType = "Lot", targetPart = "SHEET-STEEL", targetId = heldLot, source = "Supplier notice", reason = "Mill reported a chemistry deviation" }, "hold");
        run.Report.Rule(Basis.Plan, "Setup: the quality engineer places the hold");
        run.Report.Expect($"Day {run.Today + 1}: hold on SHEET-STEEL lot {heldLot}", "200", placed.Label, "Holds");
        if (placed.Succeeded)
        {
            hold = placed.Id;
            run.HeldLots.Add("SHEET-STEEL/" + heldLot);
        }

        return $"SHEET-STEEL lot {heldLot} held";
    }

    private async Task<string?> ReleaseHoldAsync()
    {
        if (hold == 0)
        {
            return "no hold to release";
        }

        var p = run.Plant;
        var released = await run.Cmd(p.QualityEngineer, $"/api/holds/{hold}/release", new { reason = "Mill certificate re-issued" }, "hold release");
        run.Report.Rule(Basis.Spec, "ADR 0034 §4: a Quality hold is released with quality.disposition");
        run.Report.Expect($"Day {run.Today + 1}: hold released by the quality engineer", "200", released.Label, "Holds");
        run.HeldLots.Remove("SHEET-STEEL/" + heldLot);
        return null;
    }

    /// <summary>
    /// While the gauge was out of calibration (from the site's midnight after its due date until it was recalibrated), no
    /// inspection was recorded with it. Separately, inspections refused on the due date itself mean Forge expired the gauge
    /// before the site's day ended: it compares the due date with the UTC date, which at a New York site rolls over at
    /// 20:00 (19:00 in winter). That is recorded as a finding for the defect register.
    /// </summary>
    private async Task<string?> CheckLapseAsync()
    {
        var during = run.Days.Where(d => d.Day > lapseFrom && d.Day < run.Today).ToList();
        run.Report.Rule(Basis.Spec, "Phase 7 spec: a measured characteristic needs a gauge in calibration");
        run.Report.Expect($"Day {run.Today + 1}: inspections recorded with TG-1 after its due date and before recalibration", "0",
            during.Sum(d => d.Inspections).ToString(CultureInfo.InvariantCulture), "Calibration");
        var dueDay = run.Days.FirstOrDefault(d => d.Day == lapseFrom);
        if (dueDay is { InspectionsRefusedCalibration: > 0 })
        {
            run.Report.Finding($"Calibration due dates are evaluated against the UTC date, not the site's: on TG-1's due date (day {lapseFrom + 1}) {dueDay.InspectionsRefusedCalibration} inspection(s) late in the site's day were refused as out of calibration while {dueDay.Inspections} earlier that day were accepted (QualityService compares the due date with DateOnly.FromDateTime(now.UtcDateTime); at a New York site the UTC date changes at 20:00, 19:00 in winter). The same UTC-date pattern appears in about 50 places across the modules.");
        }

        run.Report.Note($"Days {lapseFrom + 2}-{run.Today}: {during.Sum(d => d.InspectionsRefusedCalibration)} inspections refused while TG-1 was out of calibration.");
        await Task.CompletedTask;
        return null;
    }

    private async Task<string?> CountAsync()
    {
        var p = run.Plant;
        var book = (await Invariants.ReportedBalances(p.Stores, ["FASTENER-KIT"])).Where(b => b.Key.Location == SimFactory.Stores && b.Key.Status == "Released").Sum(b => b.Value);
        if (book <= 0)
        {
            return "no fastener kits in stores to count";
        }

        var counted = Math.Floor(book * 0.9m);
        var recorded = await run.Cmd(p.Stores, "/api/inventory/counts", new { partNumber = "FASTENER-KIT", locationCode = SimFactory.Stores, counted }, "count");
        run.Report.Rule(Basis.Spec, "Phase 6 spec: counts, approvals and material readiness: a count posts its variance only when a different person approves it");
        run.Report.Expect($"Day {run.Today + 1}: count recorded", "200", recorded.Label, "Warehouse");
        if (recorded.Succeeded)
        {
            run.Report.Expect($"Day {run.Today + 1}: count approved by the inventory controller", "200", (await run.Cmd(p.Counter, $"/api/inventory/requests/{recorded.Id}/approve", new { reason = "Recount agreed" }, "count approval")).Label, "Warehouse");
            var after = (await Invariants.ReportedBalances(p.Stores, ["FASTENER-KIT"])).Where(b => b.Key.Location == SimFactory.Stores && b.Key.Status == "Released").Sum(b => b.Value);
            run.Report.Expect($"Day {run.Today + 1}: fastener kits in stores after the count", counted, after, "Warehouse");
        }

        return $"fastener kits counted {Invariants.Q(counted)} against {Invariants.Q(book)} on the book";
    }

    private async Task<string?> TransferAsync(string[] people, string area, string shift)
    {
        var p = run.Plant;
        var dept = run.Spec.Department(area);
        foreach (var subject in people.Distinct())
        {
            var id = p.PersonId(subject);
            var onShift = await run.Cmd(p.Engineer, $"/api/workforce/workers/{id}/shift", new { shiftCode = shift, reason = "Staffing transfer" }, "shift change");
            var assigned = await run.Cmd(p.Engineer, $"/api/workforce/workers/{id}/assignment", new { safetyAreaCode = area, activityCode = dept.Activity, reason = "Staffing transfer" }, "assignment");
            // ADR 0056: there is no command to change a home work center, so a transfer is a shift change and an assignment.
            run.Report.Rule(Basis.Plan, "Setup: the supervisor transfers the person (shift and assignment; ADR 0056 consequences)");
            run.Report.Expect($"Day {run.Today + 1}: {subject} to {area}", "200 200", $"{onShift.Label} {assigned.Label}", "Workforce");
            run.ShiftOf[subject] = shift;
        }

        return null;
    }

    private async Task<string?> ExpediteAsync()
    {
        var p = run.Plant;
        var target = run.Orders.Values.Where(o => o.Finished && o.CompletedDay is null && !o.Cancelled && o.Good.Count == 0).OrderBy(o => o.DueDay).Skip(3).FirstOrDefault();
        if (target is null)
        {
            return "no unstarted order to expedite";
        }

        var preview = await run.Cmd(p.Planner, $"/api/orders/{target.Number}/priority-preview", new { priority = 1 }, "priority preview");
        if (!preview.Succeeded)
        {
            return $"priority preview for {target.Number} refused: {preview.Label}";
        }

        var json = preview.Json;
        var current = run.Demands.FirstOrDefault(d => d.Orders.Contains(target.Number))?.Priority ?? 3;
        var changed = await run.Cmd(p.Planner, $"/api/orders/{target.Number}/priority", new
        {
            priority = 1, expectedPriority = current, previewAsOf = json.GetProperty("asOf").GetDateTimeOffset(), previewFingerprint = json.GetProperty("fingerprint").GetString(), reason = "Customer escalation",
        }, "priority change");
        run.Report.Rule(Basis.Plan, "Setup: the planner expedites from a fresh preview");
        run.Report.Expect($"Day {run.Today + 1}: order {target.Number} raised to priority 1", "200", changed.Label, "Scheduling");
        return $"order {target.Number} expedited";
    }

    /// <summary>The first preventive job due on a Saturday on or after the given day, from the builder's plan (every 28 days, staggered by machine).</summary>
    private (string Machine, int Due)? SkippedPm(int fromDay)
    {
        var machines = run.Spec.Departments.Where(d => d.Area is not ("SLITTING" or "PAINT")).SelectMany(d => d.Machines).ToList();
        var dues = machines.Select((m, i) => (Machine: m, Due: Enumerable.Range(0, 10).Select(k => 5 + 7 * (i % 4) + 28 * k).First(d => d >= fromDay)))
            .Where(x => x.Due + 7 < run.Spec.Size.Days).OrderBy(x => x.Due).ThenBy(x => x.Machine, StringComparer.Ordinal).ToList();
        return dues.Count == 0 ? null : dues[0];
    }

    public static string Json(object o) => JsonSerializer.Serialize(o);
}
