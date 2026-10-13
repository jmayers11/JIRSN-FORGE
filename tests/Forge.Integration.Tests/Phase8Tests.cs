using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Forge.Integration.Tests;

/// <summary>
/// Phase 8 exit gate through the real API: standard reports with filters, grouping and traceability
/// to source records, limited by the reader's permissions and program access; saved definitions;
/// CSV and Excel exports that match the screen; and "What changed?" comparing the approved schedules
/// in force at two moments, explained with what was recorded in between.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class Phase8Tests(PostgresFixture postgres)
{
    private const string Sarah = "B-SARAH-0001";

    /// <summary>A date at the plant (the sample site is in New York): report periods are site dates.</summary>
    private static string Day(int fromToday) =>
        NodaTime.Instant.FromDateTimeOffset(DateTimeOffset.UtcNow).InZone(NodaTime.DateTimeZoneProviders.Tzdb["America/New_York"]).Date.PlusDays(fromToday)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    private static async Task<(HttpStatusCode Status, string Body)> GetStatus(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<(HttpStatusCode Status, string Body)> Try(SlittingPlant.Plant plant, HttpClient client, string url, object body)
    {
        var response = await plant.Try(client, url, body);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static int Column(JsonElement report, string key) =>
        report.GetProperty("columns").EnumerateArray().Select((c, i) => (c, i)).Single(x => x.c.GetProperty("key").GetString() == key).i;

    private static List<JsonElement> Rows(JsonElement report) => report.GetProperty("rows").EnumerateArray().ToList();

    private static async Task<(SlittingPlant.Plant Plant, HttpClient Planner, HttpClient Planner2)> PlantAsync(ForgeApp app)
    {
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var e = plant.Engineer;
        await plant.Ok(e, "/api/resources/shifts", new { code = "ALL", siteCode = "PLANT1", name = "Around the clock", start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 } });
        foreach (var wc in new[] { "SLIT-WC", "PAINT-WC", "ASSY-WC" })
        {
            await plant.Ok(e, $"/api/resources/work-centers/{wc}/shifts", new { shiftCode = "ALL" });
        }

        foreach (var person in new[] { "sarah", "michael", "jennifer", "dana" })
        {
            await plant.Ok(e, $"/api/workforce/workers/{plant.People[person]}/shift", new { shiftCode = "ALL" });
        }

        await plant.Ok(plant.Admin, $"/api/workforce/people/{plant.People["sarah"]}/badge", new { badge = Sarah });
        await plant.Person("planner2", "Planner");
        await plant.Person("tech", "MaintenanceTechnician");
        await plant.Person("kiosk", "ShopFloorTerminal");
        return (plant, planner, plant.As("planner2"));
    }

    private static async Task<long> ReleasedOrderAsync(SlittingPlant.Plant plant, HttpClient planner, decimal quantity, int dueInDays, Guid? program = null)
    {
        // The engineer sets the order up; a different person (the planner) releases it.
        var number = Id(await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = "ROLL-100", quantity, dueDate = Day(dueInDays), priority = 3, programId = program }));
        await plant.Ok(planner, $"/api/orders/{number}/release", new { reason = "Customer order" });
        return number;
    }

    [Fact]
    public async Task Standard_reports_filter_group_trace_and_export_within_the_readers_access()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner, planner2) = await PlantAsync(app);
        await plant.Person("pm", "ProgramManager");
        await plant.Person("ship", "Shipper");
        var (k, tech, pm, ship) = (plant.As("kiosk"), plant.As("tech"), plant.As("pm"), plant.As("ship"));

        // A program order the shipper cannot see, and a plain one.
        var falcon = Guid.Parse((await plant.Ok(pm, "/api/programs/", new { code = "FALCON", name = "Program Falcon" })).GetProperty("entityId").GetString()!);
        foreach (var who in new[] { "pm", "planner", "planner2", "eng" })
        {
            await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People[who]}/programs", new { programId = falcon, reason = "Falcon team" });
        }

        var plain = await ReleasedOrderAsync(plant, planner, 10m, 10);
        await ReleasedOrderAsync(plant, planner, 4m, 12, falcon);

        // The shop floor makes the plain order's first operation, with some scrap.
        await plant.Ok(k, "/api/shopfloor/clock-in", new { badge = Sarah });
        await plant.Ok(k, "/api/shopfloor/start", new { badge = Sarah, orderNumber = plain, sequence = 10, machineCode = "SLITTER-A" });
        await plant.Ok(k, "/api/shopfloor/report", new { badge = Sarah, orderNumber = plain, sequence = 10, good = 9m, scrap = 1m, scrapReason = "Edge damage", complete = true });

        // Downtime and a schedule with a constraint.
        await plant.Ok(tech, "/api/maintenance/machines/SLITTER-B/down", new { description = "Blade drive fault" });
        var version = Id(await plant.Ok(planner, "/api/planning/schedules", new { horizonDays = 14 }));
        await plant.Ok(planner2, $"/api/planning/schedules/{version}/approve", new { reason = "Reviewed" });

        // The catalog shows only reports the reader may run.
        var plannerCatalog = (await Get(planner, "/api/reports/catalog")).EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToList();
        Assert.Contains("production-output", plannerCatalog);
        Assert.Contains("constraint-history", plannerCatalog);
        var shipperCatalog = (await Get(ship, "/api/reports/catalog")).EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToList();
        Assert.Contains("delivery-performance", shipperCatalog);
        Assert.DoesNotContain("production-output", shipperCatalog);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetStatus(ship, "/api/reports/production-output")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await GetStatus(planner, "/api/reports/no-such-report")).Status);

        // Plan vs actual: today's output is unplanned (no schedule was in force when today began), and traceable.
        var output = await Get(planner, $"/api/reports/production-output?from={Day(0)}&to={Day(0)}&groupBy=workCenter");
        var row = Rows(output).Single(r => r.GetProperty("cells")[Column(output, "order")].GetString() == plain.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(9m, row.GetProperty("cells")[Column(output, "good")].GetDecimal());
        Assert.Equal(1m, row.GetProperty("cells")[Column(output, "scrap")].GetDecimal());
        Assert.Equal("Unplanned", row.GetProperty("cells")[Column(output, "status")].GetString());
        Assert.Contains($"order:{plain}", row.GetProperty("trace").EnumerateArray().Select(t => t.GetString()));
        Assert.Contains(row.GetProperty("trace").EnumerateArray(), t => t.GetString()!.StartsWith("report:", StringComparison.Ordinal));
        Assert.Contains("No schedule was approved before", output.GetProperty("summary")[0].GetString(), StringComparison.Ordinal);
        var group = Assert.Single(output.GetProperty("groups").EnumerateArray());
        Assert.Equal("SLIT-WC", group.GetProperty("key").GetString());
        Assert.Equal(9m, group.GetProperty("sums").GetProperty("good").GetDecimal());

        // Grouping is limited to what the report offers; the period to what it allows.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await GetStatus(planner, "/api/reports/production-output?groupBy=reason")).Status);
        Assert.Contains("invalid_period", (await GetStatus(planner, $"/api/reports/production-output?from={Day(-200)}&to={Day(0)}")).Body, StringComparison.Ordinal);
        Assert.Contains("invalid_period", (await GetStatus(planner, $"/api/reports/downtime?from={Day(1)}&to={Day(0)}")).Body, StringComparison.Ordinal);

        // Downtime: the open outage counts up to now; filters narrow by machine.
        var downtime = await Get(tech, $"/api/reports/downtime?lastDays=1&machine=SLITTER-B");
        var outage = Assert.Single(Rows(downtime));
        Assert.Equal("Unplanned", outage.GetProperty("cells")[Column(downtime, "kind")].GetString());
        Assert.Contains("machine:SLITTER-B", outage.GetProperty("trace").EnumerateArray().Select(t => t.GetString()));
        Assert.Empty(Rows(await Get(tech, "/api/reports/downtime?lastDays=1&machine=SLITTER-A")));

        // Constraint history: every row leads back to its constraint finding.
        var constraints = await Get(planner, "/api/reports/constraint-history?lastDays=1");
        Assert.All(Rows(constraints), r => Assert.Contains(r.GetProperty("trace").EnumerateArray(), t => t.GetString()!.StartsWith("constraint:", StringComparison.Ordinal)));
        Assert.Contains("constraints open at some time", constraints.GetProperty("summary")[0].GetString(), StringComparison.Ordinal);

        // Delivery performance: demand is segregated by program like everything else.
        await plant.Ok(planner, "/api/planning/demand", new { partNumber = "ROLL-100", quantity = 5m, needDate = Day(3), priority = 3, kind = "CustomerOrder", customer = "Acme" });
        await plant.Ok(planner, "/api/planning/demand", new { partNumber = "ROLL-100", quantity = 7m, needDate = Day(4), priority = 3, kind = "CustomerOrder", customer = "Falcon prime", programId = falcon });
        var plannerDelivery = await Get(planner, $"/api/reports/delivery-performance?from={Day(0)}&to={Day(10)}");
        Assert.Equal(2, Rows(plannerDelivery).Count);
        var shipperDelivery = await Get(ship, $"/api/reports/delivery-performance?from={Day(0)}&to={Day(10)}");
        var only = Assert.Single(Rows(shipperDelivery));
        Assert.Equal("Acme", only.GetProperty("cells")[Column(shipperDelivery, "customer")].GetString());
        Assert.Equal(5m, shipperDelivery.GetProperty("total").GetProperty("sums").GetProperty("quantity").GetDecimal());
        var falconOnly = await Get(planner, $"/api/reports/delivery-performance?from={Day(0)}&to={Day(10)}&programId={falcon}");
        Assert.Equal(7m, falconOnly.GetProperty("total").GetProperty("sums").GetProperty("quantity").GetDecimal());
        Assert.Equal(HttpStatusCode.NotFound, (await GetStatus(ship, $"/api/reports/delivery-performance?programId={falcon}")).Status);

        // Inventory transactions: the opening receipt, filtered by kind.
        var ledger = await Get(planner, "/api/reports/inventory-transactions?lastDays=1&status=Receipt");
        Assert.Contains(Rows(ledger), r => r.GetProperty("cells")[Column(ledger, "part")].GetString() == "COIL-AL");

        // CSV and Excel exports carry the same rows and their source records.
        var csv = await planner.GetAsync(new Uri($"/api/reports/production-output/export?format=csv&from={Day(0)}&to={Day(0)}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        var text = Encoding.UTF8.GetString(await csv.Content.ReadAsByteArrayAsync()).TrimStart('﻿');
        var all = text.Split("\r\n");
        Assert.StartsWith("Report,Plan vs actual output", all[0], StringComparison.Ordinal);
        Assert.Contains(all, l => l.StartsWith("Generated,", StringComparison.Ordinal));
        var lines = all.SkipWhile(l => l.Length > 0).Skip(1).Where(l => l.Length > 0).ToArray();
        Assert.StartsWith("Date,Order,Op,Work center,Part,Planned qty", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("Source records", lines[0], StringComparison.Ordinal);
        Assert.Contains(lines, l => l.Contains($"order:{plain}", StringComparison.Ordinal) && l.Contains(",9,1,", StringComparison.Ordinal));
        Assert.Equal(Rows(output).Count + 1, lines.Length);

        var xlsx = await planner.GetAsync(new Uri($"/api/reports/production-output/export?format=xlsx&from={Day(0)}&to={Day(0)}&groupBy=workCenter", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, xlsx.StatusCode);
        using (var zip = new ZipArchive(new MemoryStream(await xlsx.Content.ReadAsByteArrayAsync())))
        {
            Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
            using var sheet = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
            var xml = System.Xml.Linq.XDocument.Parse(await sheet.ReadToEndAsync());
            Assert.Equal(Rows(output).Count + 1, xml.Descendants().Count(e => e.Name.LocalName == "row"));
            using var summary = new StreamReader(zip.GetEntry("xl/worksheets/sheet2.xml")!.Open());
            Assert.Contains("SLIT-WC", await summary.ReadToEndAsync(), StringComparison.Ordinal);
        }

        // Exports apply the same access: the program's demand never reaches a shipper's file.
        Assert.DoesNotContain("Falcon prime", (await GetStatus(ship, $"/api/reports/delivery-performance/export?format=csv&from={Day(0)}&to={Day(10)}")).Body, StringComparison.Ordinal);
        Assert.Contains("Falcon prime", (await GetStatus(planner, $"/api/reports/delivery-performance/export?format=csv&from={Day(0)}&to={Day(10)}")).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Saved_report_definitions_belong_to_their_owner_and_can_be_shared()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner, planner2) = await PlantAsync(app);
        await plant.Person("ship", "Shipper");
        var ship = plant.As("ship");

        Assert.Contains("invalid_grouping", (await Try(plant, planner, "/api/reports/definitions", new { name = "Bad", reportId = "downtime", parameters = new { groupBy = "nope" } })).Body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await Try(plant, planner, "/api/reports/definitions", new { name = "x", reportId = "no-such-report" })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, ship, "/api/reports/definitions", new { name = "Mine", reportId = "production-output" })).Status);

        var weekly = Id(await plant.Ok(planner, "/api/reports/definitions", new { name = "Weekly downtime", reportId = "downtime", parameters = new { lastDays = 7, groupBy = "machine" }, shared = false }));
        Assert.Contains("duplicate_name", (await Try(plant, planner, "/api/reports/definitions", new { name = "weekly DOWNTIME", reportId = "downtime" })).Body, StringComparison.Ordinal);
        Assert.Contains("invalid_period", (await Try(plant, planner, "/api/reports/definitions", new { name = "Too long", reportId = "production-output", parameters = new { lastDays = 400 } })).Body, StringComparison.Ordinal);

        var mine = Assert.Single((await Get(planner, "/api/reports/definitions")).EnumerateArray());
        Assert.True(mine.GetProperty("mine").GetBoolean());
        Assert.Equal(7, mine.GetProperty("parameters").GetProperty("lastDays").GetInt32());
        Assert.Empty((await Get(planner2, "/api/reports/definitions")).EnumerateArray());

        // Shared: listed for others who can run it, but only the owner changes or deletes it.
        await plant.Ok(planner, "/api/reports/definitions", new { definitionId = weekly, name = "Weekly downtime", reportId = "downtime", parameters = new { lastDays = 7, groupBy = "machine" }, shared = true });
        var seen = Assert.Single((await Get(planner2, "/api/reports/definitions")).EnumerateArray());
        Assert.False(seen.GetProperty("mine").GetBoolean());
        Assert.Contains("not_owner", (await Try(plant, planner2, $"/api/reports/definitions/{weekly}/delete", new { })).Body, StringComparison.Ordinal);
        Assert.Contains("not_owner", (await Try(plant, planner2, "/api/reports/definitions", new { definitionId = weekly, name = "Mine now", reportId = "downtime", shared = true })).Body, StringComparison.Ordinal);
        Assert.Contains("report_changed", (await Try(plant, planner, "/api/reports/definitions", new { definitionId = weekly, name = "Weekly downtime", reportId = "constraint-history", shared = true })).Body, StringComparison.Ordinal);

        // The shipper cannot run downtime, so the shared definition is not listed for them.
        Assert.Empty((await Get(ship, "/api/reports/definitions")).EnumerateArray());

        await plant.Ok(planner, $"/api/reports/definitions/{weekly}/delete", new { });
        Assert.Empty((await Get(planner2, "/api/reports/definitions")).EnumerateArray());
        var audit = await Get(plant.Admin, "/api/kernel/audit?entityType=ReportDefinition");
        Assert.True(audit.GetArrayLength() >= 3);
    }

    [Fact]
    public async Task What_changed_compares_the_approved_schedules_and_shows_the_evidence()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner, planner2) = await PlantAsync(app);
        var tech = plant.As("tech");
        var order = await ReleasedOrderAsync(plant, planner, 20m, 10);

        var first = Id(await plant.Ok(planner, "/api/planning/schedules", new { horizonDays = 14 }));
        await plant.Ok(planner2, $"/api/planning/schedules/{first}/approve", new { reason = "Week plan" });
        var before = (await Get(planner, "/api/planning/schedules/approved")).GetProperty("result").GetProperty("operations").EnumerateArray()
            .Single(o => o.GetProperty("orderNumber").GetInt64() == order && o.GetProperty("sequence").GetInt32() == 10);
        var machine = before.GetProperty("machineCode").GetString()!;
        await Task.Delay(1100);
        var at1 = DateTimeOffset.UtcNow;
        await Task.Delay(1100);

        // The machine the slit was planned on goes down; the next approved schedule moves the work.
        await plant.Ok(tech, $"/api/maintenance/machines/{machine}/down", new { description = "Spindle bearing noise" });
        var second = Id(await plant.Ok(planner, "/api/planning/schedules", new { horizonDays = 14 }));
        await plant.Ok(planner2, $"/api/planning/schedules/{second}/approve", new { reason = "Replan around outage" });
        await Task.Delay(1100);
        var at2 = DateTimeOffset.UtcNow;

        string Q(DateTimeOffset a, DateTimeOffset b) => $"at1={Uri.EscapeDataString(a.ToString("o", CultureInfo.InvariantCulture))}&at2={Uri.EscapeDataString(b.ToString("o", CultureInfo.InvariantCulture))}";
        var compare = await Get(planner, "/api/planning/history/compare?" + Q(at1, at2));
        Assert.Equal(first, compare.GetProperty("before").GetProperty("versionId").GetInt64());
        Assert.Equal(second, compare.GetProperty("after").GetProperty("versionId").GetInt64());
        Assert.Equal(second, Assert.Single(compare.GetProperty("approvedBetween").EnumerateArray()).GetProperty("versionId").GetInt64());
        var change = compare.GetProperty("orders").EnumerateArray().Single(o => o.GetProperty("orderNumber").GetInt64() == order);
        Assert.NotEqual("Unchanged", change.GetProperty("change").GetString());
        var op = change.GetProperty("operations").EnumerateArray().Single(o => o.GetProperty("sequence").GetInt32() == 10);
        Assert.Equal(machine, op.GetProperty("machineBefore").GetString());
        Assert.NotEqual(machine, op.GetProperty("machineAfter").GetString());
        Assert.Contains($"moved from {machine}", op.GetProperty("explanation").GetString(), StringComparison.Ordinal);
        Assert.Contains(change.GetProperty("evidence").EnumerateArray(), e => e.GetProperty("kind").GetString() == "Downtime" && e.GetProperty("text").GetString()!.Contains("Spindle bearing noise", StringComparison.Ordinal));
        Assert.Contains(compare.GetProperty("siteEvidence").EnumerateArray(), e => e.GetProperty("kind").GetString() == "Schedule approved");
        Assert.False(compare.GetProperty("auditIncluded").GetBoolean());

        // Before any schedule, and with the same schedule at both moments, there is nothing to explain.
        var same = await Get(planner, "/api/planning/history/compare?" + Q(at2, DateTimeOffset.UtcNow.AddSeconds(1)));
        Assert.Contains("same schedule", same.GetProperty("headline").GetString(), StringComparison.Ordinal);
        Assert.Empty(same.GetProperty("orders").EnumerateArray());
        Assert.Contains("invalid_period", (await GetStatus(planner, "/api/planning/history/compare?" + Q(at2, at1))).Body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetStatus(plant.Admin, "/api/planning/history/compare?" + Q(at1, at2))).Status);
    }
}
