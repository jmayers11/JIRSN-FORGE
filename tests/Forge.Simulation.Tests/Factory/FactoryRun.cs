using System.Globalization;
using System.Text;
using System.Text.Json;
using Forge.Simulation.Tests.Engine;

namespace Forge.Simulation.Tests.Factory;

/// <summary>A customer order as the simulation tracks it.</summary>
internal sealed class DemandState(DemandSpec spec)
{
    public DemandSpec Spec { get; } = spec;

    public long DemandId { get; set; }

    public decimal Quantity { get; set; } = spec.Quantity;

    public int NeedDay { get; set; } = spec.NeedDay;

    public int Priority { get; set; } = spec.Priority;

    public decimal Shipped { get; set; }

    public int? CompletedDay { get; set; }

    public bool Cancelled { get; set; }

    public List<long> Orders { get; } = [];

    public bool Open => !Cancelled && DemandId != 0 && Shipped < Quantity;
}

/// <summary>A production order as the simulation tracks it: good quantity per operation, as reported.</summary>
internal sealed class OrderState(long number, string part, decimal quantity, int dueDay, bool finished)
{
    public long Number { get; } = number;

    public string Part { get; } = part;

    public decimal Quantity { get; } = quantity;

    public int DueDay { get; } = dueDay;

    public bool Finished { get; } = finished;

    public bool Issued { get; set; }

    public Dictionary<int, decimal> Good { get; } = [];

    public HashSet<int> Complete { get; } = [];

    public decimal Scrap { get; set; }

    public int? CompletedDay { get; set; }

    public bool Received { get; set; }

    public bool Cancelled { get; set; }
}

internal sealed record PoState(long Po, string Part, decimal Quantity, int PromisedDay)
{
    public int PromisedDay { get; set; } = PromisedDay;

    public bool Received { get; set; }
}

/// <summary>What one simulated day did, for the day-by-day consequence table.</summary>
internal sealed class DayStats
{
    public int Day { get; init; }

    public int Arrived { get; set; }

    public int OrdersCreated { get; set; }

    public int PurchaseOrders { get; set; }

    public int Receipts { get; set; }

    public int SegmentsPlanned { get; set; }

    public int SegmentsStarted { get; set; }

    public int StartsRefused { get; set; }

    public int SegmentsSkipped { get; set; }

    public int ReportsRefused { get; set; }

    public int OperationsCompleted { get; set; }

    public int OrdersCompleted { get; set; }

    public decimal UnitsShipped { get; set; }

    public int Inspections { get; set; }

    public int Ncrs { get; set; }

    public int InspectionsRefusedCalibration { get; set; }

    /// <summary>Inspections Forge listed as due that the floor routine had not done; the morning sweep did them.</summary>
    public int InspectionsCaughtUp { get; set; }

    /// <summary>Re-plans Forge made after a failed inspection or a new NCR (ADR 0057 §2.4), and the orders they moved.</summary>
    public int Replans { get; set; }

    public int ReplanOrdersMoved { get; set; }

    public int ScheduledOrders { get; set; }

    public int OnTime { get; set; }

    public int AtRisk { get; set; }

    public int Late { get; set; }

    public int Unschedulable { get; set; }

    public double ScheduleMilliseconds { get; set; }

    public string Events { get; set; } = string.Empty;
}

/// <summary>
/// Runs the factory day by day as its people would, through the real API, following Forge's own approved schedule:
/// every morning customer orders arrive and get production orders, deliveries arrive, MRP's due planned orders become
/// production orders and purchase orders, quality decisions are made, finished goods ship, and a planner proposes a
/// schedule that a second planner approves. The day's work then happens exactly as scheduled: people clock in for their
/// shift, material is issued, each scheduled segment is started and reported by the operator it was booked for, tested
/// work is inspected, and finished orders are received. Forge decides what is allowed; the simulation records every
/// refusal. The checks are independent of Forge: conservation of stock, no negative stock, the audit chain, and that no
/// approved schedule books a machine or a person twice, or books a machine the simulation broke, a person it sent home
/// or a qualification it suspended.
/// </summary>
internal sealed class FactoryRun
{
    public const int HorizonDays = 21;
    private const string ExecutionRule = "V5 amendment: the approved schedule is what the shop floor does";

    private readonly FactoryPlant p;
    private readonly FactorySpec spec;
    private readonly SimReport report;
    private readonly FactoryDisruptions? disruptions;
    private readonly List<string> parts;
    private int day;
    private int checkpointFrom;

    public FactoryRun(FactoryPlant plant, SimReport report, bool disrupted)
    {
        p = plant;
        spec = plant.Spec;
        this.report = report;
        Disrupted = disrupted;
        Demands = spec.Demand.Select(d => new DemandState(d)).ToList();
        disruptions = disrupted ? new FactoryDisruptions(this) : null;
        parts = spec.Raw.Select(r => r.Part).Concat(spec.AllMade.Select(m => m.Part)).ToList();
        ShiftOf = spec.Workers.ToDictionary(w => w.Subject, w => w.Shift, StringComparer.Ordinal);
    }

    /// <summary>Each worker's shift now (a transfer can change it).</summary>
    public Dictionary<string, string> ShiftOf { get; }

    public bool Disrupted { get; }

    /// <summary>The disruption library (disrupted run only).</summary>
    public FactoryDisruptions? Disruptions => disruptions;

    public FactoryPlant Plant => p;

    public FactorySpec Spec => spec;

    public SimReport Report => report;

    public int Today => day;

    public List<DemandState> Demands { get; }

    public Dictionary<long, OrderState> Orders { get; } = [];

    public List<PoState> Pos { get; } = [];

    public List<DayStats> Days { get; } = [];

    public List<KpiSnapshot> Checkpoints { get; } = [];

    /// <summary>Refused commands by "what code", for the defect register.</summary>
    public SortedDictionary<string, int> Refusals { get; } = new(StringComparer.Ordinal);

    /// <summary>One example key per refusal kind: the idempotency key in calls.jsonl that shows it.</summary>
    public Dictionary<string, string> RefusalExample { get; } = new(StringComparer.Ordinal);

    public List<string> ServerErrors { get; } = [];

    /// <summary>People the simulation has sent home, with the days (inclusive) they are away.</summary>
    public Dictionary<string, (int From, int To)> Away { get; } = new(StringComparer.Ordinal);

    /// <summary>Machines the simulation has broken or locked out, with the simulated time span (end null while down).</summary>
    public List<(string Machine, DateTimeOffset From, DateTimeOffset? To)> Down { get; } = [];

    /// <summary>Qualifications the simulation has suspended, by person.</summary>
    public HashSet<(string Person, string Qualification)> Suspended { get; } = [];

    /// <summary>Lots under a quality hold the simulation placed ("part/lot").</summary>
    public HashSet<string> HeldLots { get; } = new(StringComparer.Ordinal);

    /// <summary>Test failure probability at inspection (raised by the inspection-failure disruption).</summary>
    public double InspectionFailure { get; set; } = NormalInspectionFailure;

    /// <summary>The usual share of tests that fail: high enough to exercise NCRs, concessions, re-tests and re-plans every few days.</summary>
    public const double NormalInspectionFailure = 0.05;

    /// <summary>Drop every n-th shop-floor report's response and retry with the same key (network disruption); 0 = never.</summary>
    public int LoseEveryNthReport { get; set; }

    /// <summary>Send the next final-operation completion from two terminals at once (concurrency disruption).</summary>
    public bool RaceNextFinalReport { get; set; }

    /// <summary>The day the quality engineer recalibrates the test gauge (the disruption moves it past the due day).</summary>
    public int RecalibrationDay { get; set; }

    /// <summary>PM work orders the maintenance team skips (by machine) until the given day.</summary>
    public Dictionary<string, int> SkipPmUntil { get; } = new(StringComparer.Ordinal);

    public int ScrapUnits { get; private set; }

    public int NcrsRaised { get; private set; }

    public int Inspections { get; private set; }

    public int ScheduleViolations { get; private set; }

    public List<string> ScheduleViolationExamples { get; } = [];

    /// <summary>Lost responses retried with the same key, and how many applied more than once.</summary>
    public (int Retried, int Doubled) NetworkResult { get; set; }

    /// <summary>Concurrent final reports sent, and how many produced more than one effect.</summary>
    public (int Races, int Doubled) RaceResult { get; set; }

    private readonly List<(long Order, int Seq)> pendingInspections = [];
    private readonly List<long> pendingReceipts = [];
    private readonly HashSet<long> knownNcrs = [];
    private DayStats stats = new() { Day = 0 };
    private int reportCount;

    private DateTimeOffset Start => p.Clock.Start;

    /// <summary>A local time at the site (America/New_York), on the given day of the run.</summary>
    private DateTimeOffset At(int dayNumber, double hours)
    {
        return FactorySpec.Local(FactorySpec.LocalDate(Start).AddDays(dayNumber), hours);
    }

    /// <summary>Why the latest schedule could not place orders: the constraint that stopped each order's first unscheduled operation.</summary>
    public SortedDictionary<string, int> Unschedulable { get; } = new(StringComparer.Ordinal);

    /// <summary>One explanation per constraint, as Forge wrote it.</summary>
    public Dictionary<string, string> UnschedulableExample { get; } = new(StringComparer.Ordinal);

    private void AdvanceTo(DateTimeOffset t)
    {
        var now = p.Clock.GetUtcNow();
        if (t > now)
        {
            p.Clock.Advance(t - now);
        }
    }

    /// <summary>A command whose refusal is recorded (never thrown); a 5xx is a defect candidate.</summary>
    public async Task<Outcome> Cmd(HttpClient who, string url, object body, string what, string? key = null)
    {
        var outcome = await p.Api.Send(who, url, body, key);
        if ((int)outcome.Status >= 500)
        {
            ServerErrors.Add($"day {day}: {what} {url} -> {(int)outcome.Status}: {(outcome.Body.Length > 300 ? outcome.Body[..300] : outcome.Body)}");
        }

        if (!outcome.Succeeded)
        {
            var kind = what + " " + outcome.Label;
            Refusals[kind] = Refusals.GetValueOrDefault(kind) + 1;
            RefusalExample.TryAdd(kind, p.Api.Calls.Count > 0 ? p.Api.Calls[^1].Key : "?");
        }

        return outcome;
    }

    /// <summary>A deterministic chance for one decision, the same in the baseline and the disrupted run.</summary>
    public bool Chance(string key, double probability)
    {
        var hash = 14695981039346656037UL;
        foreach (var c in key + "#" + spec.Size.Seed.ToString(CultureInfo.InvariantCulture))
        {
            hash = (hash ^ c) * 1099511628211UL;
        }

        return (hash % 1_000_000UL) / 1_000_000d < probability;
    }

    public string Date(int dayNumber) => p.Day(dayNumber);

    public async Task RunAsync()
    {
        RecalibrationDay = disruptions?.RecalibrationDay ?? FactorySpec.Weekday(spec.GaugeDueDay - 2, forward: false);
        report.Rule(Basis.Plan, "Setup: the factory built through the API");
        report.Note($"Factory: {spec.Size}. Built in {p.SetupWallTime.TotalSeconds:0} s of wall time.");
        report.Covers(Coverage.Demand, Coverage.Scheduling, Coverage.Mrp, Coverage.Procurement, Coverage.Receiving, Coverage.Allocation, Coverage.Execution,
            Coverage.Workforce, Coverage.Training, Coverage.Safety, Coverage.Maintenance, Coverage.Calibration, Coverage.Warehouse, Coverage.Shipping,
            Coverage.Mrb, Coverage.Concessions, Coverage.Reporting);
        await CheckpointAsync("start", end: false);
        for (day = 0; day < spec.Size.Days; day++)
        {
            stats = new DayStats { Day = day };
            Days.Add(stats);
            AdvanceTo(At(day, 6));
            if (disruptions is not null)
            {
                stats.Events = await disruptions.ApplyAsync(day);
            }

            await MaintenanceAsync();
            await ArrivalsAsync();
            if (FactorySpec.IsWeekday(day))
            {
                await CalibrationAsync();
                await ReceiveAsync();
                await QualityAsync();
                await CatchUpInspectionsAsync();
                await MrpAsync();
                await ShipAsync();
                var schedule = await ScheduleAsync();
                if (schedule is { } s)
                {
                    await ExecuteAsync(s);
                }

                await ReplansAsync();
            }

            if (IsCheckpoint(day))
            {
                await CheckpointAsync("day " + (day + 1).ToString(CultureInfo.InvariantCulture), end: true);
            }
        }

        // A run that never released, scheduled or ran anything checked nothing: the plan itself must have happened.
        report.Rule(Basis.Plan, "Plan: the factory actually ran (orders released, schedules approved, work started, material bought)");
        report.Expect("End: production orders created and released", "more than 0", Orders.Count > 0 ? "more than 0" : "0", "Execution");
        report.Expect("End: days with an approved schedule", "more than 0", Days.Any(d => d.ScheduledOrders > 0) ? "more than 0" : "0", "Execution");
        report.Expect("End: scheduled segments started on the floor", "more than 0", Days.Sum(d => d.SegmentsStarted) > 0 ? "more than 0" : "0", "Execution");
        report.Expect("End: operations completed", "more than 0", Days.Sum(d => d.OperationsCompleted) > 0 ? "more than 0" : "0", "Execution");
        report.Note($"Inspections: {Days.Sum(d => d.Inspections)} recorded ({Days.Sum(d => d.InspectionsCaughtUp)} by the morning sweep of Forge's due list), {Days.Sum(d => d.Ncrs)} failed and raised an NCR; "
            + $"{Days.Sum(d => d.Replans)} re-plan(s) after quality events moved {Days.Sum(d => d.ReplanOrdersMoved)} order projection(s) (ADR 0057 §2).");
        await Invariants.AuditChain(report, "End", p.F.Plant.Admin);
        report.Rule(Basis.Invariant, "Amendment: no server errors");
        report.ExpectInvariant("End: commands answered without a server error", "0", ServerErrors.Count.ToString(CultureInfo.InvariantCulture), "Amendment: resilience (no 5xx)");
    }

    /// <summary>Readings after day 1, 2, 7, 30, 60 and the last day (the amendment's immediate, 7, 30 and 90-day consequences).</summary>
    public bool IsCheckpoint(int d) => d == 0 || d == 1 || d == 6 || d == 29 || d == 59 || d == spec.Size.Days - 1;

    // ---------------------------------------------------------------- morning routine

    private async Task ArrivalsAsync()
    {
        foreach (var d in Demands.Where(d => d.Spec.ArrivalDay == day))
        {
            var recorded = await Cmd(p.Planner, "/api/planning/demand", new
            {
                partNumber = d.Spec.Part, quantity = d.Quantity, needDate = Date(d.NeedDay), priority = d.Priority, kind = "CustomerOrder",
                customer = d.Spec.Customer, reference = $"CO-{d.Spec.Index:0000}", reason = "Customer order",
            }, "demand");
            if (!recorded.Succeeded)
            {
                continue;
            }

            d.DemandId = recorded.Id;
            stats.Arrived++;
            if (await CreateOrderAsync(d.Spec.Part, d.Quantity, d.NeedDay, d.Priority, finished: true) is { } order)
            {
                d.Orders.Add(order);
            }
        }
    }

    /// <summary>Creates and releases a production order; the planner does both.</summary>
    public async Task<long?> CreateOrderAsync(string part, decimal quantity, int dueDay, int priority, bool finished)
    {
        // Two people: the engineer creates the order and the planner releases it (separation of duties).
        var created = await Cmd(p.Engineer, "/api/orders/", new { partNumber = part, quantity, dueDate = Date(Math.Max(dueDay, day)), priority, reason = finished ? "Customer order" : "MRP planned order" }, "order");
        if (!created.Succeeded)
        {
            return null;
        }

        var number = long.Parse(created.EntityId!, CultureInfo.InvariantCulture);
        if (!(await Cmd(p.Planner, $"/api/orders/{number}/release", new { reason = "Released to the floor" }, "release")).Succeeded)
        {
            return null;
        }

        Orders[number] = new OrderState(number, part, quantity, dueDay, finished);
        stats.OrdersCreated++;
        return number;
    }

    private async Task ReceiveAsync()
    {
        foreach (var po in Pos.Where(x => !x.Received && x.PromisedDay <= day).ToList())
        {
            var lotTracked = spec.Raw.FirstOrDefault(r => r.Part == po.Part)?.LotTracked == true;
            var received = await Cmd(p.Stores, "/api/inventory/receipts", new
            {
                partNumber = po.Part, quantity = po.Quantity, locationCode = SimFactory.Stores, lotNumber = lotTracked ? "L" + po.Po.ToString(CultureInfo.InvariantCulture) : null,
                poNumber = po.Po, poLine = 1, revision = "A", reason = "Supplier delivery",
            }, "receipt");
            if (received.Succeeded)
            {
                po.Received = true;
                stats.Receipts++;
            }
        }
    }

    private async Task MrpAsync()
    {
        var mrp = await p.Api.TryRead(p.Planner, "/api/planning/mrp");
        if (mrp is null)
        {
            ServerErrors.Add($"day {day}: MRP could not be read");
            return;
        }

        var today = DateOnly.Parse(Date(day), CultureInfo.InvariantCulture);
        foreach (var planned in mrp.Value.GetProperty("output").GetProperty("plannedOrders").EnumerateArray().ToList())
        {
            var release = DateOnly.Parse(planned.GetProperty("releaseDate").GetString()!, CultureInfo.InvariantCulture);
            if (release > today)
            {
                continue;
            }

            var part = planned.GetProperty("partNumber").GetString()!;
            var quantity = planned.GetProperty("quantity").GetDecimal();
            var due = DateOnly.Parse(planned.GetProperty("dueDate").GetString()!, CultureInfo.InvariantCulture);
            var dueDay = due.DayNumber - DateOnly.FromDateTime(Start.UtcDateTime).DayNumber;
            if (planned.GetProperty("makeBuy").GetString() == "Make")
            {
                await CreateOrderAsync(part, quantity, dueDay, 3, finished: false);
                continue;
            }

            var raw = spec.Raw.FirstOrDefault(r => r.Part == part);
            var supplier = raw?.Supplier ?? spec.Suppliers[0].Code;
            var lead = spec.Suppliers.Single(s => s.Code == supplier).LeadTimeDays;
            var requisition = await Cmd(p.Planner, "/api/planning/mrp/requisitions", new { plannedOrder = planned.GetProperty("key").GetString(), supplierCode = supplier, reason = "MRP planned purchase" }, "requisition");
            if (!requisition.Succeeded)
            {
                continue;
            }

            var promised = Math.Max(dueDay, day + lead);
            var po = await Cmd(p.Buyer, "/api/procurement/purchase-orders", new
            {
                supplier, reason = "MRP planned purchase",
                lines = new[] { new { partNumber = part, quantity, promisedDate = Date(promised), requisitionId = (long?)requisition.Id } },
            }, "purchase order");
            if (!po.Succeeded)
            {
                continue;
            }

            if ((await Cmd(p.Purchasing, $"/api/procurement/purchase-orders/{po.Id}/approve", new { reason = "Planned need" }, "PO approval")).Succeeded)
            {
                Pos.Add(new PoState(po.Id, part, quantity, promised));
                stats.PurchaseOrders++;
            }
        }
    }

    private async Task QualityAsync()
    {
        // Open NCRs on the factory's orders: the quality engineer accepts the sample under concession, the quality
        // manager approves, the inspector re-tests, and the NCR is closed.
        var ncrs = await p.Api.TryRead(p.QualityEngineer, "/api/quality/ncrs");
        foreach (var ncr in ncrs?.EnumerateArray().Where(n => n.GetProperty("status").GetString() == "Open").ToList() ?? [])
        {
            var id = ncr.GetProperty("ncrId").GetInt64();
            var disposition = await Cmd(p.QualityEngineer, $"/api/quality/ncrs/{id}/disposition", new { disposition = "UseAsIs", note = "Leak rate within the customer's tolerance (concession CC-F)", reason = "Customer concession CC-F" }, "NCR disposition");
            if (!disposition.Succeeded || !(await Cmd(p.QualityManager, $"/api/quality/ncrs/{id}/approve", new { reason = "Concession CC-F" }, "NCR approval")).Succeeded)
            {
                continue;
            }

            if (ncr.TryGetProperty("orderNumber", out var o) && o.ValueKind == JsonValueKind.Number && ncr.TryGetProperty("sequence", out var s) && s.ValueKind == JsonValueKind.Number
                && Orders.ContainsKey(o.GetInt64()) && !await InspectAsync(o.GetInt64(), s.GetInt32(), forcePass: true))
            {
                pendingInspections.Add((o.GetInt64(), s.GetInt32()));
            }

            await Cmd(p.QualityEngineer, $"/api/quality/ncrs/{id}/close", new { note = "Accepted under concession CC-F", reason = "Complete" }, "NCR close");
        }

        foreach (var (order, seq) in pendingInspections.Distinct().ToList())
        {
            if (await InspectAsync(order, seq, forcePass: true))
            {
                pendingInspections.RemoveAll(x => x == (order, seq));
            }
        }

        foreach (var order in pendingReceipts.ToList())
        {
            if (await ReceiveProductionAsync(Orders[order]))
            {
                pendingReceipts.Remove(order);
            }
        }
    }

    private async Task CalibrationAsync()
    {
        if (day != RecalibrationDay)
        {
            return;
        }

        var due = FactorySpec.Weekday(day + 60, forward: true);
        await Cmd(p.QualityEngineer, $"/api/quality/gauges/{FactoryPlant.Gauge}/calibrations", new { calibratedOn = Date(day), dueDate = Date(due), certificate = $"CAL-TG-{day}", reason = "Periodic calibration" }, "calibration");
    }

    /// <summary>On Saturdays the maintenance team does the preventive work that is due (unless a disruption skips it).</summary>
    private async Task MaintenanceAsync()
    {
        if (day % 7 != 5)
        {
            return;
        }

        var today = DateOnly.Parse(Date(day), CultureInfo.InvariantCulture);
        var orders = await p.Api.TryRead(p.MaintenancePlanner, "/api/maintenance/work-orders");
        foreach (var wo in orders?.EnumerateArray().Select(v => v.GetProperty("order")).ToList() ?? [])
        {
            var kind = wo.GetProperty("kind").GetString();
            var status = wo.GetProperty("status").GetString();
            var machine = wo.TryGetProperty("machineCode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : string.Empty;
            var due = wo.TryGetProperty("dueDate", out var d) && d.ValueKind == JsonValueKind.String ? DateOnly.Parse(d.GetString()!, CultureInfo.InvariantCulture) : (DateOnly?)null;
            if (kind != "Preventive" || status is not ("Open" or "Scheduled") || due is null || due > today.AddDays(1) || SkipPmUntil.GetValueOrDefault(machine, -1) > day)
            {
                continue;
            }

            await DoWorkOrderAsync(wo.GetProperty("workOrderId").GetInt64(), "Preventive maintenance done", 3, TimeSpan.FromMinutes(60));
        }
    }

    /// <summary>A technician starts and completes a work order (taking its time, ADR 0056 §3); a maintenance planner signs it off.</summary>
    public async Task DoWorkOrderAsync(long id, string findings, int tasks, TimeSpan takes)
    {
        await Cmd(p.Technician, $"/api/maintenance/work-orders/{id}/start", new { }, "work order start");
        p.Clock.Advance(takes);
        await Cmd(p.Technician, $"/api/maintenance/work-orders/{id}/complete", new { findings, tasksDone = Enumerable.Repeat(true, tasks).ToArray() }, "work order complete");
        await Cmd(p.MaintenancePlanner, $"/api/maintenance/work-orders/{id}/sign-off", new { }, "work order sign-off");
    }

    private async Task ShipAsync()
    {
        var stockByPart = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var d in Demands.Where(d => d.Open && d.NeedDay - 7 <= day).OrderBy(d => d.NeedDay).ThenBy(d => d.Priority).ThenBy(d => d.Spec.Index))
        {
            if (!stockByPart.TryGetValue(d.Spec.Part, out var available))
            {
                available = (await Invariants.ReportedBalances(p.Stores, [d.Spec.Part]))
                    .Where(b => b.Key.Location == FactoryPlant.FinishedGoods && b.Key.Status == "Released").Sum(b => b.Value);
            }

            var quantity = Math.Min(available, d.Quantity - d.Shipped);
            if (quantity <= 0)
            {
                stockByPart[d.Spec.Part] = available;
                continue;
            }

            var created = await Cmd(p.Stores, "/api/inventory/shipments", new { demandId = d.DemandId, shipTo = d.Spec.Customer, lines = new[] { new { partNumber = d.Spec.Part, quantity, locationCode = FactoryPlant.FinishedGoods } }, reason = "Customer order" }, "shipment");
            if (created.Succeeded
                && (await Cmd(p.Stores, $"/api/inventory/shipments/{created.Id}/pack", new { reason = "Packed" }, "pack")).Succeeded
                && (await Cmd(p.Shipper, $"/api/inventory/shipments/{created.Id}/ship", new { carrier = "Road freight", reason = "Shipped" }, "ship")).Succeeded)
            {
                d.Shipped += quantity;
                available -= quantity;
                stats.UnitsShipped += quantity;
                if (d.Shipped >= d.Quantity)
                {
                    d.CompletedDay = day;
                }
            }

            stockByPart[d.Spec.Part] = available;
        }
    }

    // ---------------------------------------------------------------- the schedule

    private sealed record Segment(DateTimeOffset Start, DateTimeOffset End, Guid? Operator);

    private sealed class OpRun
    {
        public required long Order { get; init; }

        public required int Seq { get; init; }

        public string? Machine { get; init; }

        public required List<Segment> Segments { get; init; }

        public double TotalMinutes { get; init; }

        public double DoneMinutes { get; set; }

        public decimal Target { get; set; }

        public decimal Reported { get; set; }

        public bool Derailed { get; set; }

        public string? Badge { get; set; }

        public bool Continuing { get; set; }
    }

    private async Task<JsonElement?> ScheduleAsync()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var proposed = await Cmd(p.Planner, "/api/planning/schedules", new { horizonDays = HorizonDays, reason = "Daily schedule" }, "schedule proposal");
        stats.ScheduleMilliseconds = watch.Elapsed.TotalMilliseconds;
        if (!proposed.Succeeded)
        {
            return null;
        }

        var version = proposed.EntityId;
        if (!(await Cmd(p.Planner2, $"/api/planning/schedules/{version}/approve", new { reason = "Reviewed" }, "schedule approval")).Succeeded)
        {
            return null;
        }

        var result = (await p.Api.Read(p.Planner, $"/api/planning/schedules/{version}")).GetProperty("result");
        var orders = result.GetProperty("orders").EnumerateArray().ToList();
        stats.ScheduledOrders = orders.Count;
        stats.OnTime = orders.Count(o => o.GetProperty("status").GetString() == "OnTime");
        stats.AtRisk = orders.Count(o => o.GetProperty("status").GetString() == "AtRisk");
        stats.Late = orders.Count(o => o.GetProperty("status").GetString() == "Late");
        stats.Unschedulable = orders.Count(o => o.GetProperty("status").GetString() == "Unschedulable");
        CheckFeasibility(result);
        Unschedulable.Clear();
        foreach (var g in result.GetProperty("operations").EnumerateArray().Where(o => !o.GetProperty("scheduled").GetBoolean())
                     .GroupBy(o => o.GetProperty("orderNumber").GetInt64()))
        {
            var first = g.OrderBy(o => o.GetProperty("sequence").GetInt32()).First();
            var why = first.TryGetProperty("bindingConstraint", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "Unknown";
            if (why == "Predecessor")
            {
                continue;
            }

            Unschedulable[why] = Unschedulable.GetValueOrDefault(why) + 1;
            if (Unschedulable[why] == 1 && first.TryGetProperty("bindingDetail", out var detail) && detail.ValueKind == JsonValueKind.String)
            {
                UnschedulableExample[why] = detail.GetString()!;
            }
        }
        return result;
    }

    private static List<Segment> Segments(JsonElement op) =>
        op.TryGetProperty("segments", out var segs) && segs.ValueKind == JsonValueKind.Array
            ? segs.EnumerateArray().Select(s => new Segment(s.GetProperty("start").GetDateTimeOffset(), s.GetProperty("end").GetDateTimeOffset(),
                s.GetProperty("operators").EnumerateArray().Select(g => (Guid?)g.GetGuid()).FirstOrDefault())).Where(s => s.End > s.Start).ToList()
            : [];

    /// <summary>
    /// The independent schedule checks: no machine or person booked twice at once, and nothing booked on a machine the
    /// simulation broke, for a person it sent home, or for a qualification it suspended.
    /// </summary>
    private void CheckFeasibility(JsonElement result)
    {
        var machine = new List<(string Key, DateTimeOffset S, DateTimeOffset E, string What)>();
        var person = new List<(string Key, DateTimeOffset S, DateTimeOffset E, string What)>();
        var found = new List<string>();
        foreach (var op in result.GetProperty("operations").EnumerateArray())
        {
            var what = $"{op.GetProperty("orderNumber").GetInt64()}/{op.GetProperty("sequence").GetInt32()}";
            var code = op.TryGetProperty("machineCode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            var activity = op.GetProperty("activityCode").GetString();
            var qualification = spec.Departments.FirstOrDefault(d => d.Activity == activity)?.Qualification;
            foreach (var s in Segments(op))
            {
                if (code is not null)
                {
                    machine.Add((code, s.Start, s.End, what));
                    if (Down.Any(d => d.Machine == code && d.From < s.End && (d.To is null || d.To > s.Start)))
                    {
                        found.Add($"{what} booked on {code} while it is down ({s.Start:u})");
                    }
                }

                if (s.Operator is { } id && p.Subjects.TryGetValue(id, out var subject))
                {
                    person.Add((subject, s.Start, s.End, what));
                    var dayNumber = (int)(s.Start - Start).TotalDays;
                    if (Away.TryGetValue(subject, out var away) && dayNumber >= away.From && dayNumber <= away.To)
                    {
                        found.Add($"{what} booked for {subject}, who is away ({s.Start:u})");
                    }

                    if (qualification is not null && Suspended.Contains((subject, qualification)))
                    {
                        found.Add($"{what} booked for {subject}, whose {qualification} is suspended");
                    }
                }
            }
        }

        foreach (var list in new[] { machine, person })
        {
            foreach (var g in list.GroupBy(x => x.Key))
            {
                var ordered = g.OrderBy(x => x.S).ToList();
                for (var i = 1; i < ordered.Count; i++)
                {
                    if (ordered[i].S < ordered[i - 1].E && ordered[i].What != ordered[i - 1].What)
                    {
                        found.Add($"{g.Key} booked twice: {ordered[i - 1].What} and {ordered[i].What} at {ordered[i].S:u}");
                    }
                }
            }
        }

        ScheduleViolations += found.Count;
        foreach (var f in found.Take(Math.Max(0, 20 - ScheduleViolationExamples.Count)))
        {
            ScheduleViolationExamples.Add($"day {day}: {f}");
        }
    }

    // ---------------------------------------------------------------- the shop floor

    private async Task ExecuteAsync(JsonElement result)
    {
        var windowStart = At(day, 6);
        var windowEnd = At(day + 1, 6);
        var runs = new List<OpRun>();
        foreach (var op in result.GetProperty("operations").EnumerateArray())
        {
            if (!op.GetProperty("scheduled").GetBoolean())
            {
                continue;
            }

            var number = op.GetProperty("orderNumber").GetInt64();
            if (!Orders.TryGetValue(number, out var order) || order.Cancelled)
            {
                continue;
            }

            var segments = Segments(op);
            if (!segments.Any(s => s.Start >= windowStart && s.Start < windowEnd))
            {
                continue;
            }

            var seq = op.GetProperty("sequence").GetInt32();
            runs.Add(new OpRun
            {
                Order = number, Seq = seq, Machine = op.TryGetProperty("machineCode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null,
                Segments = segments, TotalMinutes = segments.Sum(s => (s.End - s.Start).TotalMinutes),
            });
        }

        // Events in time order: ends before clock-outs before clock-ins before starts at the same moment.
        var events = new List<(DateTimeOffset At, int Kind, string Key, Func<Task> Act)>();
        foreach (var run in runs)
        {
            for (var i = 0; i < run.Segments.Count; i++)
            {
                var s = run.Segments[i];
                if (s.Start < windowStart || s.Start >= windowEnd)
                {
                    continue;
                }

                var index = i;
                stats.SegmentsPlanned++;
                events.Add((s.Start, 3, $"{run.Order:D9}{run.Seq:D4}{i:D3}", () => StartSegmentAsync(run, index)));
                events.Add((s.End, 0, $"{run.Order:D9}{run.Seq:D4}{i:D3}", () => EndSegmentAsync(run, index)));
            }
        }

        var weekday = (int)(windowStart.DayOfWeek);
        if (weekday is >= 1 and <= 5)
        {
            foreach (var w in spec.Workers)
            {
                if (Away.TryGetValue(w.Subject, out var away) && day >= away.From && day <= away.To)
                {
                    continue;
                }

                var shift = FactorySpec.Shifts.Single(s => s.Code == ShiftOf[w.Subject]);
                var badge = p.Badges[w.Subject];
                var clockIn = At(day, shift.StartHour);
                events.Add((clockIn, 2, w.Subject, () => Cmd(p.Kiosk, "/api/shopfloor/clock-in", new { badge }, "clock-in")));
                events.Add((clockIn.AddHours(shift.Hours), 1, w.Subject, () => ClockOutAsync(runs, badge)));
            }
        }

        foreach (var e in events.OrderBy(e => e.At).ThenBy(e => e.Kind).ThenBy(e => e.Key, StringComparer.Ordinal))
        {
            AdvanceTo(e.At);
            await e.Act();
        }
    }

    private async Task ClockOutAsync(List<OpRun> runs, string badge)
    {
        foreach (var open in runs.Where(r => r.Badge == badge))
        {
            await Cmd(p.Kiosk, "/api/shopfloor/stop", new { badge }, "stop at shift end");
            open.Badge = null;
            open.Continuing = false;
        }

        await Cmd(p.Kiosk, "/api/shopfloor/clock-out", new { badge }, "clock-out");
    }

    private async Task StartSegmentAsync(OpRun run, int index)
    {
        var order = Orders[run.Order];
        var made = spec.Made(order.Part);
        var sequences = made.Routing.Select(o => o.Sequence).ToList();
        var position = sequences.IndexOf(run.Seq);
        if (run.Derailed || order.Complete.Contains(run.Seq) || (position > 0 && !order.Complete.Contains(sequences[position - 1])))
        {
            stats.SegmentsSkipped++;
            run.Derailed = true;
            return;
        }

        if (run.Continuing)
        {
            run.Continuing = false;
            return;
        }

        var segment = run.Segments[index];
        if (segment.Operator is not { } id || !p.Subjects.TryGetValue(id, out var subject))
        {
            stats.SegmentsSkipped++;
            run.Derailed = true;
            return;
        }

        if (position == 0 && !order.Issued)
        {
            await IssueAsync(order);
        }

        if (run.Reported == 0 && run.DoneMinutes == 0)
        {
            var before = position == 0 ? order.Quantity : order.Good.GetValueOrDefault(sequences[position - 1]);
            run.Target = Math.Max(0, before - order.Good.GetValueOrDefault(run.Seq));
        }

        var badge = p.Badges[subject];
        var started = await Cmd(p.Kiosk, "/api/shopfloor/start", new { badge, orderNumber = run.Order, sequence = run.Seq, machineCode = run.Machine }, "start");
        if (started.Succeeded)
        {
            stats.SegmentsStarted++;
            run.Badge = badge;
        }
        else
        {
            stats.StartsRefused++;
            run.Derailed = true;
        }
    }

    private async Task EndSegmentAsync(OpRun run, int index)
    {
        if (run.Badge is not { } badge)
        {
            return;
        }

        var order = Orders[run.Order];
        var made = spec.Made(order.Part);
        var segment = run.Segments[index];
        run.DoneMinutes += (segment.End - segment.Start).TotalMinutes;
        var last = index == run.Segments.Count - 1;
        decimal good, scrap = 0m;
        if (last)
        {
            good = run.Target - run.Reported;
            if (good >= 2 && Chance($"scrap/{order.Part}/{order.Number}/{run.Seq}", 0.03))
            {
                scrap = 1m;
                good -= 1m;
            }
        }
        else
        {
            good = Math.Floor(run.Target * (decimal)(run.DoneMinutes / run.TotalMinutes)) - run.Reported;
        }

        if (good < 0)
        {
            good = 0;
        }

        var next = last ? null : run.Segments[index + 1];
        var continuing = next is not null && next.Start == segment.End && next.Operator == segment.Operator;
        if (good > 0 || scrap > 0 || last)
        {
            var body = new { badge, orderNumber = run.Order, sequence = run.Seq, good, scrap, scrapReason = scrap > 0 ? "Damaged in handling" : null, complete = last };
            var outcome = last && made.Routing[^1].Sequence == run.Seq && RaceNextFinalReport
                ? await RaceAsync(body)
                : await ReportAsync(body);
            if (!outcome.Succeeded)
            {
                stats.ReportsRefused++;
                await Cmd(p.Kiosk, "/api/shopfloor/stop", new { badge }, "stop after refused report");
                run.Badge = null;
                run.Derailed = true;
                return;
            }

            run.Reported += good;
            order.Good[run.Seq] = order.Good.GetValueOrDefault(run.Seq) + good;
            order.Scrap += scrap;
            ScrapUnits += (int)scrap;
        }

        if (last)
        {
            run.Badge = null;
            order.Complete.Add(run.Seq);
            stats.OperationsCompleted++;
            await AfterOperationAsync(order, run.Seq);
            return;
        }

        if (continuing)
        {
            run.Continuing = true;
            return;
        }

        await Cmd(p.Kiosk, "/api/shopfloor/stop", new { badge }, "stop");
        run.Badge = null;
    }

    /// <summary>A shop-floor report; during the network disruption every n-th one loses its response and is retried with the same key.</summary>
    private async Task<Outcome> ReportAsync(object body)
    {
        reportCount++;
        if (LoseEveryNthReport > 0 && reportCount % LoseEveryNthReport == 0)
        {
            var key = p.Api.NextKey();
            await SimApi.SendAndLoseResponse(p.Kiosk, "/api/shopfloor/report", body, key);
            var retry = await Cmd(p.Kiosk, "/api/shopfloor/report", body, "report (retry after lost response)", key);
            NetworkResult = (NetworkResult.Retried + 1, NetworkResult.Doubled);
            return retry;
        }

        return await Cmd(p.Kiosk, "/api/shopfloor/report", body, "report");
    }

    /// <summary>The same completion sent from two terminals at once, with different keys: exactly one may take effect.</summary>
    private async Task<Outcome> RaceAsync(object body)
    {
        RaceNextFinalReport = false;
        var first = Cmd(p.Kiosk, "/api/shopfloor/report", body, "report (terminal 1 of a race)");
        var second = Cmd(p.Kiosk2, "/api/shopfloor/report", body, "report (terminal 2 of a race)");
        var outcomes = await Task.WhenAll(first, second);
        var won = outcomes.Count(o => o.Succeeded);
        RaceResult = (RaceResult.Races + 1, RaceResult.Doubled + (won > 1 ? 1 : 0));
        report.Rule(Basis.Spec, "Phase 5 and 7 shop floor: an operation is never reported beyond its quantity; two terminals reporting the rest at once leave exactly one report");
        report.Expect($"Day {day + 1}: concurrent final reports take effect once", "1", won.ToString(CultureInfo.InvariantCulture), "Concurrency");
        return outcomes.FirstOrDefault(o => o.Succeeded) ?? outcomes[0];
    }

    private async Task AfterOperationAsync(OrderState order, int seq)
    {
        var made = spec.Made(order.Part);
        if (order.Finished && made.Routing.Single(o => o.Sequence == seq).Department == "TESTING")
        {
            if (!await InspectAsync(order.Number, seq, forcePass: false))
            {
                pendingInspections.Add((order.Number, seq));
            }
        }

        if (made.Routing[^1].Sequence == seq)
        {
            order.CompletedDay = day;
            stats.OrdersCompleted++;
            if (!await ReceiveProductionAsync(order))
            {
                pendingReceipts.Add(order.Number);
            }
        }
    }

    /// <summary>
    /// The morning sweep: every inspection Forge lists as due that has never failed is done now, with the usual chance
    /// of failing (ADR 0057 §2). Failed ones wait for their NCR decision, which re-tests them. Counted, so a gap in the
    /// floor routine shows in the results.
    /// </summary>
    private async Task CatchUpInspectionsAsync()
    {
        if (await p.Api.TryRead(p.Inspector, "/api/quality/due") is not { ValueKind: JsonValueKind.Array } due)
        {
            return;
        }

        foreach (var item in due.EnumerateArray().Where(d => !d.GetProperty("lastFailed").GetBoolean() && d.GetProperty("operationStatus").GetString() == "Complete").ToList())
        {
            var number = item.GetProperty("orderNumber").GetInt64();
            var seq = item.GetProperty("sequence").GetInt32();
            if (Orders.ContainsKey(number) && await InspectAsync(number, seq, forcePass: false))
            {
                stats.InspectionsCaughtUp++;
                pendingInspections.RemoveAll(x => x == (number, seq));
            }
        }
    }

    /// <summary>
    /// At the end of the working day Forge's events are delivered, so a failed inspection's re-plan (ADR 0057 §2.4)
    /// happens as it would in the plant. The re-plan is counted with the orders it moved; the next morning's proposal
    /// replaces it, as a planner's would.
    /// </summary>
    private async Task ReplansAsync()
    {
        var before = (await p.Api.TryRead(p.Planner, "/api/planning/schedules/proposed"))?.GetProperty("versionId").GetInt64();
        await p.F.DeliverEventsAsync();
        if (await p.Api.TryRead(p.Planner, "/api/planning/schedules/proposed") is { } proposed
            && proposed.GetProperty("versionId").GetInt64() != before
            && proposed.GetProperty("proposedBy").GetString() == "system:quality-replan")
        {
            stats.Replans++;
            var summary = proposed.GetProperty("summary").GetString() ?? string.Empty;
            var marker = summary.IndexOf(" order(s) move", StringComparison.Ordinal);
            var start = summary.LastIndexOf(':', Math.Max(0, marker)) + 1;
            if (marker > 0 && int.TryParse(summary.AsSpan(start, marker - start).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var moved))
            {
                stats.ReplanOrdersMoved += moved;
            }
        }
    }

    /// <summary>The inspector tests a sample at the gauge; a seeded share fail and raise an NCR. Answers whether a result was recorded.</summary>
    private async Task<bool> InspectAsync(long number, int seq, bool forcePass)
    {
        var order = Orders[number];
        var sample = Math.Max(1m, Math.Min(5m, order.Good.GetValueOrDefault(seq)));
        var fail = !forcePass && Chance($"inspect/{number}/{seq}", InspectionFailure);
        var outcome = await Cmd(p.Inspector, "/api/quality/inspections", new
        {
            orderNumber = number, sequence = seq, quantity = sample, gaugeCode = FactoryPlant.Gauge, rejectedQuantity = fail ? 1m : (decimal?)null, notes = fail ? "Leak rate high" : null,
            findings = new object[] { new { number = 1, measured = fail ? 2.6m : 1.0m }, new { number = 2, accepted = true } },
        }, "inspection");
        if (!outcome.Succeeded && outcome.Label.Contains("gauge_out_of_calibration", StringComparison.Ordinal))
        {
            stats.InspectionsRefusedCalibration++;
        }

        if (outcome.Succeeded)
        {
            Inspections++;
            stats.Inspections++;
            if (fail)
            {
                NcrsRaised++;
                stats.Ncrs++;
            }
        }

        return outcome.Succeeded;
    }

    private async Task<bool> ReceiveProductionAsync(OrderState order)
    {
        var made = spec.Made(order.Part);
        var quantity = order.Good.GetValueOrDefault(made.Routing[^1].Sequence);
        if (quantity <= 0)
        {
            order.Received = true;
            return true;
        }

        var location = order.Finished ? FactoryPlant.FinishedGoods : SimFactory.Stores;
        var received = await Cmd(p.Stores, "/api/inventory/production-receipts", new { orderNumber = order.Number, quantity, locationCode = location, reason = "Order complete" }, "production receipt");
        order.Received = received.Succeeded;
        return received.Succeeded;
    }

    /// <summary>Issues an order's full BOM requirement from stores, oldest lot first, substituting the approved equivalent for sheet steel when short.</summary>
    private async Task IssueAsync(OrderState order)
    {
        order.Issued = true;
        var definition = await p.Api.Read(p.Planner, $"/api/orders/{order.Number}");
        foreach (var line in definition.GetProperty("materials").EnumerateArray())
        {
            var part = line.GetProperty("componentPartNumber").GetString()!;
            var need = line.GetProperty("requiredQuantity").GetDecimal();
            need -= await IssueFromAsync(order.Number, part, need, null);
            if (need > 0 && part == "SHEET-STEEL")
            {
                need -= await IssueFromAsync(order.Number, "SHEET-STEEL-EQ", need, "SHEET-STEEL");
            }

            if (need > 0)
            {
                Refusals["material short at issue"] = Refusals.GetValueOrDefault("material short at issue") + 1;
            }
        }
    }

    private async Task<decimal> IssueFromAsync(long order, string part, decimal need, string? alternateFor)
    {
        var lotTracked = spec.Raw.FirstOrDefault(r => r.Part == part)?.LotTracked == true;
        var buckets = (await Invariants.ReportedBalances(p.Stores, [part]))
            .Where(b => b.Key.Location == SimFactory.Stores && b.Key.Status == "Released" && b.Value > 0 && !HeldLots.Contains(part + "/" + b.Key.Lot))
            .OrderBy(b => b.Key.Lot?.StartsWith("OPEN", StringComparison.Ordinal) == true ? 0 : 1)
            .ThenBy(b => b.Key.Lot?.Length ?? 0).ThenBy(b => b.Key.Lot, StringComparer.Ordinal).ToList();
        var issued = 0m;
        foreach (var (bucket, available) in buckets)
        {
            if (issued >= need)
            {
                break;
            }

            var quantity = Math.Min(available, need - issued);
            var outcome = await Cmd(p.Stores, "/api/inventory/issues", new { orderNumber = order, partNumber = part, quantity, locationCode = SimFactory.Stores, lotNumber = lotTracked ? bucket.Lot : null, alternateFor, reason = "Kit for the order" }, alternateFor is null ? "issue" : "issue (approved alternate)");
            if (outcome.Succeeded)
            {
                issued += quantity;
            }
        }

        return issued;
    }

    // ---------------------------------------------------------------- readings

    /// <summary>
    /// A reading at the end of a day: Forge's own numbers (schedule outcome, reports, readiness, capacity, stock by status)
    /// next to the simulation's counts, then the invariants for every part.
    /// </summary>
    public async Task CheckpointAsync(string label, bool end)
    {
        var v = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
        var today = Math.Min(day, spec.Size.Days - 1);
        var arrived = Demands.Where(d => d.DemandId != 0).ToList();
        v["sim.demand.arrived"] = arrived.Count;
        v["sim.demand.units"] = arrived.Sum(d => d.Quantity);
        v["sim.demand.shippedComplete"] = arrived.Count(d => d.CompletedDay is not null);
        v["sim.demand.shippedOnTime"] = arrived.Count(d => d.CompletedDay is { } c && c <= d.NeedDay);
        v["sim.demand.pastDueOpen"] = arrived.Count(d => d.Open && d.NeedDay < today);
        v["sim.demand.unitsShipped"] = arrived.Sum(d => d.Shipped);
        v["sim.demand.cancelled"] = arrived.Count(d => d.Cancelled);
        v["sim.orders.created"] = Orders.Count;
        v["sim.orders.completed"] = Orders.Values.Count(o => o.CompletedDay is not null);
        v["sim.orders.open"] = Orders.Values.Count(o => o.CompletedDay is null && !o.Cancelled);
        v["sim.orders.completedLate"] = Orders.Values.Count(o => o.CompletedDay is { } c && c > o.DueDay);
        v["sim.scrap.units"] = ScrapUnits;
        v["sim.quality.inspections"] = Inspections;
        v["sim.quality.ncrs"] = NcrsRaised;
        v["sim.segments.planned"] = Days.Sum(d => d.SegmentsPlanned);
        v["sim.segments.started"] = Days.Sum(d => d.SegmentsStarted);
        v["sim.segments.startRefused"] = Days.Sum(d => d.StartsRefused);
        v["sim.segments.skipped"] = Days.Sum(d => d.SegmentsSkipped);
        v["sim.purchasing.orders"] = Pos.Count;
        v["sim.purchasing.received"] = Pos.Count(x => x.Received);
        if (Days.LastOrDefault(d => d.ScheduledOrders > 0) is { } s)
        {
            v["schedule.orders.total"] = s.ScheduledOrders;
            v["schedule.orders.OnTime"] = s.OnTime;
            v["schedule.orders.AtRisk"] = s.AtRisk;
            v["schedule.orders.Late"] = s.Late;
            v["schedule.orders.Unschedulable"] = s.Unschedulable;
        }

        var from = Date(0);
        var to = Date(today + 1);
        foreach (var id in new[] { "delivery-performance", "production-output", "ncr-summary", "quantity-states", "pm-compliance", "downtime" })
        {
            var r = await p.Api.TryRead(p.Planner, $"/api/reports/{id}?from={from}&to={to}");
            if (r is not { } rep || !rep.TryGetProperty("rows", out var rows))
            {
                v[$"report.{id}.unavailable"] = 1;
                continue;
            }

            var columns = rep.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("key").GetString()!).ToList();
            v[$"report.{id}.rows"] = rows.GetArrayLength();
            foreach (var row in rows.EnumerateArray())
            {
                var cells = row.GetProperty("cells");
                for (var i = 0; i < columns.Count && i < cells.GetArrayLength(); i++)
                {
                    if (cells[i].ValueKind == JsonValueKind.Number)
                    {
                        var key = $"report.{id}.{columns[i]}";
                        v[key] = v.GetValueOrDefault(key) + cells[i].GetDecimal();
                    }
                }
            }
        }

        var now = p.Clock.GetUtcNow();
        if (await p.Api.TryRead(p.Planner, $"/api/planning/capacity?from={Iso(now)}&to={Iso(now.AddDays(7))}") is { } capacity && capacity.TryGetProperty("output", out var output))
        {
            foreach (var m in output.GetProperty("machines").EnumerateArray())
            {
                var area = spec.Departments.FirstOrDefault(d => d.Machines.Contains(m.GetProperty("machineCode").GetString()!))?.Area ?? "OTHER";
                var key = $"capacity.{area}.executableHours.7d";
                v[key] = v.GetValueOrDefault(key) + m.GetProperty("executableHours").GetDecimal();
            }

            foreach (var q in output.GetProperty("qualifiedLabor").EnumerateArray())
            {
                var key = $"labor.{q.GetProperty("qualification").GetString()}.hours.7d";
                v[key] = v.GetValueOrDefault(key) + q.GetProperty("hours").GetDecimal();
            }
        }

        if (await p.Api.TryRead(p.Planner, "/api/readiness/operations") is { } readiness)
        {
            var operations = readiness.EnumerateArray().ToList();
            v["readiness.operations.total"] = operations.Count;
            v["readiness.operations.notExecutable"] = operations.Count(o => !o.GetProperty("executable").GetBoolean());
        }

        var balances = await Invariants.ReportedBalances(p.Stores, parts);
        foreach (var (bucket, quantity) in balances)
        {
            var category = spec.Raw.Any(r => r.Part == bucket.Part) ? "bought" : spec.Intermediates.Any(i => i.Part == bucket.Part) ? "intermediate" : "finished";
            var key = $"inventory.{category}.{bucket.Status}";
            v[key] = v.GetValueOrDefault(key) + quantity;
        }

        foreach (var (why, count) in Unschedulable)
        {
            v[$"schedule.unschedulableBy.{why}"] = count;
        }

        if (Unschedulable.Count > 0)
        {
            report.Note($"{label}: orders the latest schedule could not place, by constraint: " + string.Join("; ", Unschedulable.Select(kv => $"{kv.Key} {kv.Value} (e.g. \"{UnschedulableExample.GetValueOrDefault(kv.Key)}\")")));
        }

        var snapshot = report.Kpi(new KpiSnapshot(label, p.Clock.Elapsed, v));
        Checkpoints.Add(snapshot);
        await Invariants.Inventory(report, label, p.Stores, parts);

        // Every shop-floor report applied exactly once: Forge's good quantity per operation equals what the floor reported.
        var differences = new List<string>();
        foreach (var order in Orders.Values.Where(o => o.Good.Count > 0))
        {
            var reports = (await p.Api.Read(p.Planner, $"/api/orders/{order.Number}/shopfloor")).GetProperty("reports").EnumerateArray()
                .GroupBy(r => r.GetProperty("sequence").GetInt32()).ToDictionary(g => g.Key, g => g.Sum(r => r.GetProperty("goodQuantity").GetDecimal()));
            foreach (var (seq, good) in order.Good)
            {
                if (reports.GetValueOrDefault(seq) != good)
                {
                    differences.Add($"{order.Number}/{seq}: floor {Invariants.Q(good)}, Forge {Invariants.Q(reports.GetValueOrDefault(seq))}");
                }
            }
        }

        report.ExpectInvariant($"{label}: good quantity per operation equals what the floor reported (each report applied once)", "no differences",
            differences.Count == 0 ? "no differences" : $"{differences.Count}: " + string.Join("; ", differences.Take(10)), "Amendment: quantity conservation; ADR 0055 §1");
        if (end)
        {
            report.Rule(Basis.Invariant, "Amendment: schedule feasibility (independent check)");
            report.ExpectInvariant($"{label}: days {checkpointFrom + 1}-{today + 1}: approved schedules book no machine or person twice, and nothing on a broken machine, an absent person or a suspended qualification",
                "0", ScheduleViolations.ToString(CultureInfo.InvariantCulture), "Amendment: schedule feasibility");
            checkpointFrom = today + 1;
        }
    }

    /// <summary>A light reading before and after a disruption: the proposed schedule's outcome, capacity and readiness.</summary>
    public async Task<KpiSnapshot> GlanceAsync(string label)
    {
        var v = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
        var proposed = await Cmd(p.Planner, "/api/planning/schedules", new { horizonDays = HorizonDays, reason = "Impact reading: " + label }, "schedule proposal (reading)");
        if (proposed.Succeeded && await p.Api.TryRead(p.Planner, $"/api/planning/schedules/{proposed.EntityId}") is { } s)
        {
            var orders = s.GetProperty("result").GetProperty("orders").EnumerateArray().ToList();
            foreach (var status in new[] { "OnTime", "AtRisk", "Late", "Unschedulable" })
            {
                v[$"schedule.orders.{status}"] = orders.Count(o => o.GetProperty("status").GetString() == status);
            }

            v["schedule.lateHours"] = Math.Round(orders.Where(o => o.TryGetProperty("projectedCompletion", out var pc) && pc.ValueKind == JsonValueKind.String)
                .Sum(o => (decimal)Math.Max(0, (o.GetProperty("projectedCompletion").GetDateTimeOffset() - o.GetProperty("dueAt").GetDateTimeOffset()).TotalHours)), 1);
        }

        var now = p.Clock.GetUtcNow();
        if (await p.Api.TryRead(p.Planner, $"/api/planning/capacity?from={Iso(now)}&to={Iso(now.AddDays(7))}") is { } capacity && capacity.TryGetProperty("output", out var output))
        {
            foreach (var m in output.GetProperty("machines").EnumerateArray())
            {
                var area = spec.Departments.FirstOrDefault(d => d.Machines.Contains(m.GetProperty("machineCode").GetString()!))?.Area ?? "OTHER";
                var key = $"capacity.{area}.executableHours.7d";
                v[key] = v.GetValueOrDefault(key) + m.GetProperty("executableHours").GetDecimal();
            }

            foreach (var q in output.GetProperty("qualifiedLabor").EnumerateArray())
            {
                var key = $"labor.{q.GetProperty("qualification").GetString()}.hours.7d";
                v[key] = v.GetValueOrDefault(key) + q.GetProperty("hours").GetDecimal();
            }
        }

        if (await p.Api.TryRead(p.Planner, "/api/readiness/operations") is { } readiness)
        {
            foreach (var g in readiness.EnumerateArray().Where(o => !o.GetProperty("executable").GetBoolean())
                         .GroupBy(o => o.TryGetProperty("primaryConstraint", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "Unknown", StringComparer.Ordinal))
            {
                v[$"readiness.blockedBy.{g.Key}"] = g.Count();
            }
        }

        return new KpiSnapshot(label, p.Clock.Elapsed, v);
    }

    public static string Iso(DateTimeOffset t) => Uri.EscapeDataString(t.ToString("o", CultureInfo.InvariantCulture));

    /// <summary>The day-by-day table as CSV.</summary>
    public string DaysCsv()
    {
        var sb = new StringBuilder("day,arrived,ordersCreated,purchaseOrders,receipts,segmentsPlanned,segmentsStarted,startsRefused,segmentsSkipped,reportsRefused,operationsCompleted,ordersCompleted,unitsShipped,inspections,inspectionsRefusedCalibration,inspectionsCaughtUp,ncrs,replans,replanOrdersMoved,scheduledOrders,onTime,atRisk,late,unschedulable,scheduleMs,events\n");
        foreach (var d in Days)
        {
            sb.Append(string.Join(',', new object[]
            {
                d.Day + 1, d.Arrived, d.OrdersCreated, d.PurchaseOrders, d.Receipts, d.SegmentsPlanned, d.SegmentsStarted, d.StartsRefused, d.SegmentsSkipped, d.ReportsRefused,
                d.OperationsCompleted, d.OrdersCompleted, d.UnitsShipped, d.Inspections, d.InspectionsRefusedCalibration, d.InspectionsCaughtUp, d.Ncrs, d.Replans, d.ReplanOrdersMoved, d.ScheduledOrders, d.OnTime, d.AtRisk, d.Late, d.Unschedulable,
                Math.Round(d.ScheduleMilliseconds), "\"" + d.Events.Replace("\"", "'", StringComparison.Ordinal) + "\"",
            }.Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)))).Append('\n');
        }

        return sb.ToString();
    }
}
