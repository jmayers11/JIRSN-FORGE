using System.Globalization;
using System.Text.Json;
using Forge.Integration.Tests;
using Forge.Simulation.Tests.Engine;

namespace Forge.Simulation.Tests.Scenarios;

/// <summary>
/// Scenario 24 (reports): the standard reports must tell the same story as the records they summarize. The run posts
/// receipts and scrap, plans a maintenance window, makes an order on the shop floor with some scrap, and ships part of a
/// customer order. Each report is then read and compared with what the plan posted: inventory transactions, machine
/// downtime hours, plan-versus-actual output, and delivery performance. A reader without the report's permission is
/// refused. Expectations come from the Phase 8 reporting specification (reports are read-only views of their source
/// records and filtered by the reader's access), computed from the plan, never from Forge.
/// </summary>
internal static class ReportsScenario
{
    public const string Name = "S24-reports-match-records";
    private const string Spec = "Phase 8 reports";
    private const string Part = SimFactory.LotPart;
    private const string Fg = "FG";

    public static EventLog Plan(int seed)
    {
        var log = new EventLog(Name, seed);
        log.Add(TimeSpan.Zero, "Receipts", ("count", log.Between(2, 5)), ("quantity", log.Between(10, 40)));
        log.Add(TimeSpan.FromMinutes(10), "Scrap", ("quantity", log.Between(1, 5)));
        log.Add(TimeSpan.FromMinutes(20), "PlannedDowntime", ("startsInHours", log.Between(1, 2)), ("hours", log.Between(2, 8)));
        log.Add(TimeSpan.FromMinutes(30), "Produce", ("quantity", log.Between(10, 30)), ("scrap", log.Between(1, 3)));
        log.Add(TimeSpan.FromMinutes(60), "Ship", ("demand", log.Between(20, 40)), ("ship", log.Between(5, 15)));
        log.Add(TimeSpan.FromHours(12), "ReadReports");
        return log;
    }

    public static async Task<(SimReport Report, EventLog Log, string Hash)> RunAsync(PostgresFixture postgres, int seed, Mutation mutation = Mutation.None)
    {
        var log = Plan(seed);
        var report = new SimReport(mutation == Mutation.None ? Name : Name + RunSummary.CanaryMarker + mutation, seed);
        await using var f = await SimFactory.BuildPlanningAsync(postgres, "s24-" + seed.ToString(CultureInfo.InvariantCulture));
        report.Evidence(f.Api);
        report.Covers(Coverage.Reporting, Coverage.Maintenance, Coverage.Warehouse, Coverage.Execution, Coverage.Shipping);
        await f.PeopleAsync(("mp", "MaintenancePlanner"), ("mp2", "MaintenancePlanner"));
        var kiosk = await Floor.SetUpAsync(f);
        var (stores, mp, tech, planner, ship) = (f.StoresUser, f.Api.As("mp"), f.Api.As("tech"), f.Planner, f.Api.As("ship"));
        await f.Plant.Ok(stores, "/api/inventory/locations", new { code = Fg, siteCode = "PLANT1", name = "Finished goods", kind = "Stores" });
        var parts = new[] { Part, SimFactory.BulkPart, SimFactory.FlexPart };
        decimal received = 0m, receipts = 0m, scrapped = 0m, downtimeHours = 0m, good = 0m, scrap = 0m, demanded = 0m, shipped = 0m;
        long order = 0;

        static int Column(JsonElement r, string key) =>
            r.GetProperty("columns").EnumerateArray().Select((c, i) => (c, i)).Single(x => x.c.GetProperty("key").GetString() == key).i;

        static List<JsonElement> Rows(JsonElement r) => r.GetProperty("rows").EnumerateArray().ToList();

        static decimal Num(JsonElement row, int column)
        {
            var cell = row.GetProperty("cells")[column];
            return cell.ValueKind == JsonValueKind.Number ? cell.GetDecimal() : 0m;
        }

        static string Txt(JsonElement row, int column)
        {
            var cell = row.GetProperty("cells")[column];
            return cell.ValueKind == JsonValueKind.String ? cell.GetString() ?? string.Empty : cell.ToString();
        }

        var sinceStart = TimeSpan.Zero;
        foreach (var ev in log.Events)
        {
            f.Clock.Advance(ev.At - sinceStart);
            sinceStart = ev.At;
            var step = $"#{ev.Index} {ev.Type}";
            switch (ev.Type)
            {
                case "Receipts":
                    report.Rule(Basis.Plan, "Setup: several receipts of brackets");
                    for (var i = 0; i < (int)ev.Number("count"); i++)
                    {
                        report.Expect($"{step}: receipt {i + 1}", "200", (await f.Api.Send(stores, "/api/inventory/receipts", new { partNumber = Part, quantity = ev.Number("quantity"), locationCode = SimFactory.Stores, lotNumber = "REP-1", revision = "A", reason = "Delivery" })).Label);
                        received += ev.Number("quantity");
                        receipts++;
                    }

                    break;

                case "Scrap":
                    report.Rule(Basis.Plan, "Setup: some brackets are scrapped");
                    report.Expect($"{step}: scrapped", "200", (await f.Api.Send(stores, "/api/inventory/scrap", new { partNumber = Part, quantity = ev.Number("quantity"), locationCode = SimFactory.Stores, lotNumber = "REP-1", reason = "Damaged" })).Label);
                    scrapped += ev.Number("quantity");
                    break;

                case "PlannedDowntime":
                {
                    var from = f.Clock.GetUtcNow().AddHours((double)ev.Number("startsInHours"));
                    downtimeHours = ev.Number("hours");
                    report.Rule(Basis.Plan, "Setup: a planned maintenance window on SLITTER-B, proposed by one planner and approved by another");
                    var proposed = await f.Api.Send(mp, "/api/maintenance/machines/SLITTER-B/planned", new
                    {
                        startsAt = from.ToString("o", CultureInfo.InvariantCulture), endsAt = from.AddHours((double)downtimeHours).ToString("o", CultureInfo.InvariantCulture),
                        description = "Bearing change", reason = "Planned maintenance",
                    });
                    report.Expect($"{step}: window proposed", "200", proposed.Label);
                    report.Expect($"{step}: window approved by a second planner", "200",
                        (await f.Api.Send(f.Api.As("mp2"), $"/api/maintenance/outages/{proposed.Id}/approve", new { reason = "Preview reviewed" })).Label);
                    break;
                }

                case "Produce":
                {
                    good = ev.Number("quantity");
                    scrap = ev.Number("scrap");
                    report.Rule(Basis.Plan, "Setup: an order's slitting is made with some scrap");
                    order = await f.ReleasedOrderAsync(SimFactory.FlexPart, good + scrap, 10);
                    report.Expect($"{step}: clock-in", "200", (await f.Api.Send(kiosk, "/api/shopfloor/clock-in", new { badge = Floor.Sarah })).Label);
                    report.Expect($"{step}: started", "200", (await f.Api.Send(kiosk, "/api/shopfloor/start", new { badge = Floor.Sarah, orderNumber = order, sequence = 10, machineCode = "SLITTER-A" })).Label);
                    report.Expect($"{step}: reported", "200", (await f.Api.Send(kiosk, "/api/shopfloor/report", new { badge = Floor.Sarah, orderNumber = order, sequence = 10, good, scrap, scrapReason = "Edge damage", complete = true })).Label);
                    break;
                }

                case "Ship":
                {
                    demanded = ev.Number("demand");
                    shipped = ev.Number("ship");
                    report.Rule(Basis.Plan, "Setup: a customer order for rolls, part of it shipped from finished goods");
                    report.Expect($"{step}: rolls in finished goods", "200", (await f.Api.Send(stores, "/api/inventory/receipts", new { partNumber = SimFactory.FlexPart, quantity = shipped, locationCode = Fg, reason = "Opening" })).Label);
                    var demand = await f.Api.Send(planner, "/api/planning/demand", new { partNumber = SimFactory.FlexPart, quantity = demanded, needDate = f.Day(3), priority = 3, kind = "CustomerOrder", customer = "Report customer", reference = "PO-R", reason = "Customer order" });
                    report.Expect($"{step}: demand recorded", "200", demand.Label);
                    var created = await f.Api.Send(stores, "/api/inventory/shipments", new { demandId = demand.Id, shipTo = "Dock", lines = new[] { new { partNumber = SimFactory.FlexPart, quantity = shipped, locationCode = Fg } }, reason = "Order" });
                    report.Expect($"{step}: shipment", "200 200 200", $"{created.Label} {(await f.Api.Send(stores, $"/api/inventory/shipments/{created.Id}/pack", new { reason = "Packed" })).Label} {(await f.Api.Send(ship, $"/api/inventory/shipments/{created.Id}/ship", new { carrier = "UPS", reason = "Ship" })).Label}");
                    break;
                }

                case "ReadReports":
                {
                    report.Rule(Basis.Spec, Spec + ": the inventory-transaction report lists every receipt posted, with its quantity");
                    var ledger = await SimApi.Get(planner, $"/api/reports/inventory-transactions?from={f.Day(-1)}&to={f.Day(1)}&status=Receipt");
                    var mine = Rows(ledger).Where(r => Txt(r, Column(ledger, "part")) == Part).ToList();
                    report.Expect($"{step}: receipt rows for brackets", receipts, mine.Count, "Reporting");
                    report.Expect($"{step}: received quantity in the report", received, mine.Sum(r => Num(r, Column(ledger, "quantity"))), "Reporting");
                    var scraps = await SimApi.Get(planner, $"/api/reports/inventory-transactions?from={f.Day(-1)}&to={f.Day(1)}&status=Scrap");
                    report.Expect($"{step}: scrapped quantity in the report", scrapped,
                        Rows(scraps).Where(r => Txt(r, Column(scraps, "part")) == Part).Sum(r => Math.Abs(Num(r, Column(scraps, "quantity")))), "Reporting");

                    report.Rule(Basis.Spec, Spec + ": the downtime report counts a window's hours within the period and up to now; the window is over by now");
                    var downtime = await SimApi.Get(tech, $"/api/reports/downtime?from={f.Day(-1)}&to={f.Day(2)}&machine=SLITTER-B");
                    var windows = Rows(downtime).Where(r => Txt(r, Column(downtime, "kind")) == "Planned").ToList();
                    report.Expect($"{step}: planned downtime hours", downtimeHours, windows.Sum(r => Num(r, Column(downtime, "hours"))), "Maintenance");

                    report.Rule(Basis.Spec, Spec + ": plan-versus-actual output shows what the shop floor reported");
                    var output = await SimApi.Get(planner, $"/api/reports/production-output?from={f.Day(-1)}&to={f.Day(1)}");
                    var row = Rows(output).Where(r => Txt(r, Column(output, "order")) == order.ToString(CultureInfo.InvariantCulture)).ToList();
                    report.Expect($"{step}: good in the output report", good, row.Sum(r => Num(r, Column(output, "good"))), "Execution");
                    report.Expect($"{step}: scrap in the output report", scrap, row.Sum(r => Num(r, Column(output, "scrap"))), "Execution");

                    report.Rule(Basis.Spec, Spec + ": delivery performance shows the order and what has shipped against it");
                    var delivery = await SimApi.Get(planner, $"/api/reports/delivery-performance?from={f.Day(0)}&to={f.Day(10)}");
                    var line = Rows(delivery).Where(r => Txt(r, Column(delivery, "customer")) == "Report customer").ToList();
                    report.Expect($"{step}: ordered quantity in the delivery report", demanded, line.Sum(r => Num(r, Column(delivery, "quantity"))), "Shipping");
                    report.Expect($"{step}: shipped quantity in the delivery report", shipped, line.Sum(r => Num(r, Column(delivery, "shipped"))), "Shipping");

                    report.Rule(Basis.Spec, Spec + ": a reader without the report's permission is refused");
                    using var denied = await f.Api.As("ship").GetAsync(new Uri("/api/reports/production-output", UriKind.Relative));
                    report.Expect($"{step}: shipper cannot read production output", "403", ((int)denied.StatusCode).ToString(CultureInfo.InvariantCulture), "Authorization");
                    break;
                }
            }

            await Invariants.Inventory(report, step, stores, parts);
        }

        await Invariants.AuditChain(report, "End", f.Plant.Admin);
        return (report, log, await Invariants.StateHash(stores, parts));
    }
}
