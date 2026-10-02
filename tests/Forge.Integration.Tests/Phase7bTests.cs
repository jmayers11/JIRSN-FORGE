using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Forge.Integration.Tests;

/// <summary>
/// Phase 7b exit gate through the real API: programs, contracts, CLINs and deliveries that become
/// demand and show on Program Horizon, segregated by program; preventive maintenance with lock-out,
/// work orders done by a technician and signed off by someone else, and breakdowns repaired through
/// corrective work orders.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class Phase7bTests(PostgresFixture postgres)
{
    private static string Day(int fromToday) => DateTime.UtcNow.AddDays(fromToday).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>A date at the plant (the sample site is in New York), as maintenance due dates are.</summary>
    private static string LocalDay(int fromToday) =>
        NodaTime.Instant.FromDateTimeOffset(DateTimeOffset.UtcNow).InZone(NodaTime.DateTimeZoneProviders.Tzdb["America/New_York"]).Date.PlusDays(fromToday)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    private static async Task<(HttpStatusCode Status, string Body)> Try(SlittingPlant.Plant plant, HttpClient client, string url, object body)
    {
        var response = await plant.Try(client, url, body);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>The work orders as stored (each list entry wraps one with its run hours and parts).</summary>
    private static async Task<List<JsonElement>> WorkOrders(HttpClient client) =>
        (await Get(client, "/api/maintenance/work-orders")).EnumerateArray().Select(v => v.GetProperty("order")).ToList();

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    [Fact]
    public async Task Contract_deliveries_become_demand_and_roll_up_on_program_horizon()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("pm", "ProgramManager");
        await plant.Person("ship", "Shipper");
        var (pm, stores, ship, eng) = (plant.As("pm"), plant.As("stores"), plant.As("ship"), plant.Engineer);

        // A new program is invisible, even to its creator, until a security administrator grants access.
        var falcon = Guid.Parse((await plant.Ok(pm, "/api/programs/", new { code = "FALCON", name = "Program Falcon", customer = "USAF" })).GetProperty("entityId").GetString()!);
        Assert.Empty((await Get(pm, "/api/programs/")).EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, pm, $"/api/programs/{falcon}/contracts", new { number = "X", customer = "X", title = "X", periodStart = Day(0), periodEnd = Day(1) })).Status);
        foreach (var who in new[] { "pm", "planner", "stores" })
        {
            await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People[who]}/programs", new { programId = falcon, reason = "Falcon team" });
        }

        var directory = await Get(plant.Admin, "/api/programs/directory");
        Assert.Contains(directory.EnumerateArray(), p => p.GetProperty("code").GetString() == "FALCON");

        // Contract, CLINs (a SLIN under 0001, a data CLIN with no part), deliveries inside the period.
        var contract = Id(await plant.Ok(pm, $"/api/programs/{falcon}/contracts", new { number = "FA8650-26-C-0042", customer = "USAF", title = "Falcon rolls", periodStart = Day(0), periodEnd = Day(365), ceiling = 250000m }));
        await plant.Ok(pm, $"/api/programs/contracts/{contract}/clins", new { clin = "0001", description = "Slit rolls", partNumber = "ROLL-100", quantity = 30m, unitPrice = 1200m });
        await plant.Ok(pm, $"/api/programs/contracts/{contract}/clins", new { clin = "0001AA", parentClin = "0001", description = "First article" });
        await plant.Ok(pm, $"/api/programs/contracts/{contract}/clins", new { clin = "0002", description = "Test data" });
        Assert.Contains("clin_has_no_part", (await Try(plant, pm, $"/api/programs/contracts/{contract}/deliveries", new { clin = "0002", quantity = 1m, dueDate = Day(10) })).Body, StringComparison.Ordinal);
        var first = Id(await plant.Ok(pm, $"/api/programs/contracts/{contract}/deliveries", new { clin = "0001", quantity = 20m, dueDate = Day(30) }));
        await plant.Ok(pm, $"/api/programs/contracts/{contract}/deliveries", new { clin = "0001", quantity = 10m, dueDate = Day(60) });
        Assert.Contains("over_clin_quantity", (await Try(plant, pm, $"/api/programs/contracts/{contract}/deliveries", new { clin = "0001", quantity = 1m, dueDate = Day(90) })).Body, StringComparison.Ordinal);
        Assert.Contains("outside_period", (await Try(plant, pm, $"/api/programs/contracts/{contract}/deliveries", new { clin = "0001", quantity = 1m, dueDate = Day(400) })).Body, StringComparison.Ordinal);
        await plant.Ok(pm, $"/api/programs/contracts/{contract}/requirements", new { code = "AS9102", text = "First article inspection report with the first delivery." });
        await plant.Ok(pm, $"/api/programs/{falcon}/milestones", new { title = "Critical design review", dueDate = Day(20) });

        // A planner turns a delivery into demand, once; it carries the program.
        var demand = Id(await plant.Ok(planner, $"/api/planning/demand/from-delivery/{first}", new { priority = 2 }));
        Assert.Equal("no_change", (await plant.Ok(planner, $"/api/planning/demand/from-delivery/{first}", new { })).GetProperty("code").GetString());
        var demandRow = (await Get(planner, "/api/planning/demand")).EnumerateArray().Single(d => d.GetProperty("demandId").GetInt64() == demand);
        Assert.Equal("ContractDelivery", demandRow.GetProperty("kind").GetString());
        Assert.Equal(falcon, demandRow.GetProperty("programId").GetGuid());
        Assert.DoesNotContain((await Get(eng, "/api/planning/demand")).EnumerateArray(), d => d.GetProperty("demandId").GetInt64() == demand);

        // The contract changes; the demand says so until the planner brings it back in step.
        Assert.Contains("reason", (await Try(plant, pm, $"/api/programs/deliveries/{first}/change", new { quantity = 18m, dueDate = Day(30) })).Body, StringComparison.OrdinalIgnoreCase);
        await plant.Ok(pm, $"/api/programs/deliveries/{first}/change", new { quantity = 18m, dueDate = Day(30), reason = "Mod P00001" });
        demandRow = (await Get(planner, "/api/planning/demand")).EnumerateArray().Single(d => d.GetProperty("demandId").GetInt64() == demand);
        Assert.Contains("Contract now says 18", demandRow.GetProperty("contractNote").GetString(), StringComparison.Ordinal);
        await plant.Ok(planner, $"/api/planning/demand/from-delivery/{first}", new { });
        demandRow = (await Get(planner, "/api/planning/demand")).EnumerateArray().Single(d => d.GetProperty("demandId").GetInt64() == demand);
        Assert.Equal(18m, demandRow.GetProperty("quantity").GetDecimal());
        Assert.Equal(2, demandRow.GetProperty("priority").GetInt32());
        Assert.Equal(JsonValueKind.Null, demandRow.GetProperty("contractNote").ValueKind);

        // Orders on the program; a closed program takes none, and an unknown one is refused.
        var order = Id(await plant.Ok(planner, "/api/orders/", new { partNumber = "ROLL-100", quantity = 18m, dueDate = Day(25), priority = 2, programId = falcon }));
        // A program nobody has access to (or that does not exist) is refused before anything else.
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, planner, "/api/orders/", new { partNumber = "ROLL-100", quantity = 1m, dueDate = Day(25), priority = 2, programId = Guid.Parse("018f0000-0000-7000-8000-0000000000aa") })).Status);

        // Ship the first delivery against its demand.
        await plant.Ok(stores, "/api/inventory/locations", new { code = "FG", siteCode = "PLANT1", name = "Finished goods", kind = "Stores" });
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "ROLL-100", quantity = 18m, locationCode = "FG", reason = "Built ahead" });
        var shipment = Id(await plant.Ok(stores, "/api/inventory/shipments", new { demandId = demand, lines = new[] { new { partNumber = "ROLL-100", quantity = 18m, locationCode = "FG" } } }));
        await plant.Ok(stores, $"/api/inventory/shipments/{shipment}/pack", new { });
        await plant.Ok(ship, $"/api/inventory/shipments/{shipment}/ship", new { carrier = "FedEx Freight" });

        // Program Horizon counts it all from the records.
        var horizon = await Get(pm, $"/api/programs/{falcon}");
        Assert.Equal(28m, horizon.GetProperty("unitsRequired").GetDecimal());
        Assert.Equal(18m, horizon.GetProperty("unitsDelivered").GetDecimal());
        Assert.Equal(18m, horizon.GetProperty("unitsPlanned").GetDecimal());
        var states = horizon.GetProperty("deliveries").EnumerateArray().Select(d => d.GetProperty("state").GetString()).ToList();
        Assert.Equal(new[] { "Delivered", "Not planned" }, states);
        var listed = Assert.Single(horizon.GetProperty("orders").EnumerateArray());
        Assert.Equal(order, listed.GetProperty("orderNumber").GetInt64());
        Assert.Equal("NotScheduled", listed.GetProperty("delivery").GetString());
        Assert.Single(horizon.GetProperty("milestones").EnumerateArray());
        Assert.Equal(36000m, horizon.GetProperty("contracts")[0].GetProperty("value").GetDecimal());

        // Someone without Falcon access sees no Falcon program at all.
        await plant.Person("pm2", "ProgramManager");
        Assert.Empty((await Get(plant.As("pm2"), "/api/programs/")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await plant.As("pm2").GetAsync(new Uri($"/api/programs/{falcon}", UriKind.Relative))).StatusCode);

        // Closing the program stops new orders.
        await plant.Ok(pm, $"/api/programs/{falcon}/close", new { reason = "Contract complete" });
        Assert.Contains("program_closed", (await Try(plant, planner, "/api/orders/", new { partNumber = "ROLL-100", quantity = 1m, dueDate = Day(25), priority = 2, programId = falcon })).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preventive_maintenance_locks_out_overdue_machines_and_work_is_signed_off_by_someone_else()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("mp", "MaintenancePlanner");
        await plant.Person("tech", "MaintenanceTechnician");
        var (mp, tech, eng) = (plant.As("mp"), plant.As("tech"), plant.Engineer);

        // A lock-out PM plan that is already overdue past its grace day.
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, tech, "/api/maintenance/plans", new { machineCode = "SLITTER-A", title = "x", intervalDays = 30, firstDue = Day(-3), durationMinutes = 60, tasks = new[] { "x" }, lockOutWhenOverdue = true, graceDays = 1 })).Status);
        Assert.Contains("tasks_required", (await Try(plant, mp, "/api/maintenance/plans", new { machineCode = "SLITTER-A", title = "Knife check", intervalDays = 30, firstDue = Day(-3), durationMinutes = 60, tasks = Array.Empty<string>(), lockOutWhenOverdue = true, graceDays = 1 })).Body, StringComparison.Ordinal);
        await plant.Ok(mp, "/api/maintenance/plans", new
        {
            machineCode = "SLITTER-A",
            title = "Knife and guard inspection",
            intervalDays = 30,
            firstDue = Day(-3),
            durationMinutes = 90,
            tasks = new[] { "Lock out and tag out", "Inspect and rotate knives", "Check guard interlocks" },
            lockOutWhenOverdue = true,
            graceDays = 1,
        });
        var pm = Assert.Single(await WorkOrders(mp));
        var pmId = pm.GetProperty("workOrderId").GetInt64();
        Assert.Equal(Day(-3), pm.GetProperty("dueDate").GetString());

        var now = DateTimeOffset.UtcNow;
        var window = $"from={Uri.EscapeDataString(now.ToString("o"))}&to={Uri.EscapeDataString(now.AddHours(1).ToString("o"))}";
        Assert.Contains((await Get(mp, "/api/maintenance/downtime?" + window)).EnumerateArray(), w => w.GetProperty("machineCode").GetString() == "SLITTER-A" && w.GetProperty("kind").GetString() == "MaintenanceOverdue");
        var order = Id(await plant.Ok(eng, "/api/orders/", new { partNumber = "ROLL-100", quantity = 10m, dueDate = Day(10), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Customer order" });
        var readiness = await Get(planner, $"/api/orders/{order}/readiness");
        var slitterA = readiness.GetProperty("output").GetProperty("operations")[0].GetProperty("candidates").EnumerateArray().Single(c => c.GetProperty("machineCode").GetString() == "SLITTER-A");
        Assert.Contains(slitterA.GetProperty("dimensions").EnumerateArray(), d => d.GetProperty("name").GetString() == "Equipment" && d.GetProperty("detail").GetString()!.Contains("locked out", StringComparison.Ordinal));

        // The technician does the work and confirms every task; someone else signs it off.
        await plant.Ok(tech, $"/api/maintenance/work-orders/{pmId}/start", new { });
        var partial = await Try(plant, tech, $"/api/maintenance/work-orders/{pmId}/complete", new { findings = "Knives rotated", tasksDone = new[] { true, true, false } });
        Assert.Contains("checklist_incomplete", partial.Body, StringComparison.Ordinal);
        await plant.Ok(tech, $"/api/maintenance/work-orders/{pmId}/complete", new { findings = "Knives rotated, guards OK", tasksDone = new[] { true, true, true } });
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, tech, $"/api/maintenance/work-orders/{pmId}/sign-off", new { })).Status);
        var signed = await plant.Ok(mp, $"/api/maintenance/work-orders/{pmId}/sign-off", new { });
        Assert.Contains($"due {LocalDay(30)}", signed.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain((await Get(mp, "/api/maintenance/downtime?" + window)).EnumerateArray(), w => w.GetProperty("kind").GetString() == "MaintenanceOverdue");
        var next = Assert.Single(await WorkOrders(mp), o => o.GetProperty("kind").GetString() == "Preventive");
        Assert.Equal("Open", next.GetProperty("status").GetString());

        // Skipping a PM needs a reason and raises the one after.
        var nextId = next.GetProperty("workOrderId").GetInt64();
        Assert.Contains("reason", (await Try(plant, mp, $"/api/maintenance/work-orders/{nextId}/cancel", new { })).Body, StringComparison.OrdinalIgnoreCase);
        var skipped = await plant.Ok(mp, $"/api/maintenance/work-orders/{nextId}/cancel", new { reason = "Machine idle this month" });
        Assert.Contains($"due {LocalDay(60)}", skipped.GetProperty("message").GetString(), StringComparison.Ordinal);

        // A breakdown raises a corrective order; the machine is back only when the repair is signed off.
        var down = await plant.Ok(eng, "/api/maintenance/machines/SLITTER-B/down", new { description = "Rewind motor tripping" });
        Assert.Contains("Corrective work order", down.GetProperty("message").GetString(), StringComparison.Ordinal);
        var repair = (await WorkOrders(mp)).Single(o => o.GetProperty("kind").GetString() == "Corrective").GetProperty("workOrderId").GetInt64();
        await plant.Ok(tech, $"/api/maintenance/work-orders/{repair}/start", new { });
        await plant.Ok(tech, $"/api/maintenance/work-orders/{repair}/complete", new { findings = "Replaced overload relay", tasksDone = Array.Empty<bool>() });
        Assert.Contains("repair_in_progress", (await Try(plant, eng, "/api/maintenance/machines/SLITTER-B/restore", new { reason = "x" })).Body, StringComparison.Ordinal);
        Assert.Contains((await Get(mp, "/api/maintenance/downtime?" + window)).EnumerateArray(), w => w.GetProperty("machineCode").GetString() == "SLITTER-B" && w.GetProperty("endsAt").ValueKind == JsonValueKind.Null);
        var back = await plant.Ok(mp, $"/api/maintenance/work-orders/{repair}/sign-off", new { });
        Assert.Contains("back in service", back.GetProperty("message").GetString(), StringComparison.Ordinal);
        var later = $"from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddSeconds(1).ToString("o"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(1).ToString("o"))}";
        Assert.DoesNotContain((await Get(mp, "/api/maintenance/downtime?" + later)).EnumerateArray(), w => w.GetProperty("machineCode").GetString() == "SLITTER-B");
    }

    [Fact]
    public async Task Funding_wbs_spare_parts_and_run_hour_pm_follow_adr_0009()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("pm", "ProgramManager");
        await plant.Person("mp", "MaintenancePlanner");
        await plant.Person("tech", "MaintenanceTechnician");
        var (pm, mp, tech, eng, stores) = (plant.As("pm"), plant.As("mp"), plant.As("tech"), plant.Engineer, plant.As("stores"));

        var falcon = Guid.Parse((await plant.Ok(pm, "/api/programs/", new { code = "FALCON", name = "Program Falcon" })).GetProperty("entityId").GetString()!);
        foreach (var who in new[] { "pm", "planner", "eng" })
        {
            await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People[who]}/programs", new { programId = falcon, reason = "Falcon team" });
        }

        // Funding: obligations need a reason, stay within the ceiling and never go below zero.
        var contract = Id(await plant.Ok(pm, $"/api/programs/{falcon}/contracts", new { number = "FA-0042", customer = "USAF", title = "Falcon rolls", periodStart = Day(0), periodEnd = Day(365), ceiling = 50000m }));
        await plant.Ok(pm, $"/api/programs/contracts/{contract}/clins", new { clin = "0001", description = "Slit rolls", partNumber = "ROLL-100", quantity = 30m, unitPrice = 1200m });
        await plant.Ok(pm, $"/api/programs/contracts/{contract}/deliveries", new { clin = "0001", quantity = 20m, dueDate = Day(30) });
        Assert.Contains("reason", (await Try(plant, pm, $"/api/programs/contracts/{contract}/funding", new { modification = "P00001", amount = 20000m, obligatedOn = Day(0) })).Body, StringComparison.OrdinalIgnoreCase);
        await plant.Ok(pm, $"/api/programs/contracts/{contract}/funding", new { clin = "0001", modification = "P00001", amount = 20000m, obligatedOn = Day(0), reason = "Initial obligation" });
        Assert.Contains("over_ceiling", (await Try(plant, pm, $"/api/programs/contracts/{contract}/funding", new { modification = "P00002", amount = 40000m, obligatedOn = Day(0), reason = "x" })).Body, StringComparison.Ordinal);
        Assert.Contains("negative_funding", (await Try(plant, pm, $"/api/programs/contracts/{contract}/funding", new { modification = "P00003", amount = -25000m, obligatedOn = Day(0), reason = "x" })).Body, StringComparison.Ordinal);

        // WBS: dotted numbers under an existing parent.
        await plant.Ok(pm, $"/api/programs/{falcon}/wbs", new { code = "1", title = "Falcon production" });
        await plant.Ok(pm, $"/api/programs/{falcon}/wbs", new { code = "1.1", parentCode = "1", title = "Slit rolls" });
        Assert.Contains("invalid_wbs", (await Try(plant, pm, $"/api/programs/{falcon}/wbs", new { code = "2.1", parentCode = "1", title = "x" })).Body, StringComparison.Ordinal);
        Assert.Contains("invalid_wbs", (await Try(plant, pm, $"/api/programs/{falcon}/wbs", new { code = "A", title = "x" })).Body, StringComparison.Ordinal);
        Assert.Contains("wbs_exists", (await Try(plant, pm, $"/api/programs/{falcon}/wbs", new { code = "1", title = "x" })).Body, StringComparison.Ordinal);
        Assert.Contains("unknown_wbs", (await Try(plant, pm, $"/api/programs/{falcon}/wbs", new { code = "3.1", parentCode = "3", title = "x" })).Body, StringComparison.Ordinal);

        var order = Id(await plant.Ok(planner, "/api/orders/", new { partNumber = "ROLL-100", quantity = 20m, dueDate = Day(25), priority = 2, programId = falcon }));
        var plain = Id(await plant.Ok(planner, "/api/orders/", new { partNumber = "ROLL-100", quantity = 5m, dueDate = Day(25), priority = 3 }));
        Assert.Contains("unknown_wbs", (await Try(plant, pm, $"/api/orders/{order}/wbs", new { wbsCode = "9", reason = "x" })).Body, StringComparison.Ordinal);
        Assert.Contains("not_program_order", (await Try(plant, pm, $"/api/orders/{plain}/wbs", new { wbsCode = "1.1", reason = "x" })).Body, StringComparison.Ordinal);
        await plant.Ok(pm, $"/api/orders/{order}/wbs", new { wbsCode = "1.1", reason = "Charge to slit rolls" });

        var horizon = await Get(pm, $"/api/programs/{falcon}");
        var ev = horizon.GetProperty("earnedValue");
        Assert.Equal(36000m, ev.GetProperty("budgetAtCompletion").GetDecimal());
        Assert.Equal(20000m, ev.GetProperty("funded").GetDecimal());
        Assert.Equal(24000m, ev.GetProperty("scheduledValue").GetDecimal());
        Assert.Equal(4000m, ev.GetProperty("scheduledBeyondFunding").GetDecimal());
        Assert.Equal(20000m, horizon.GetProperty("contracts")[0].GetProperty("funded").GetDecimal());
        var wbs = horizon.GetProperty("wbs").EnumerateArray().ToDictionary(w => w.GetProperty("code").GetString()!);
        Assert.Equal(1, wbs["1"].GetProperty("orders").GetInt32());
        Assert.Equal(order, wbs["1.1"].GetProperty("orderNumbers")[0].GetInt64());
        Assert.Equal("1.1", horizon.GetProperty("orders").EnumerateArray().Single(o => o.GetProperty("orderNumber").GetInt64() == order).GetProperty("wbsCode").GetString());

        // Spare parts issued to a repair and returned; never more back than went out.
        await plant.Ok(eng, "/api/maintenance/machines/SLITTER-B/down", new { description = "Rewind motor tripping" });
        var repair = (await WorkOrders(mp)).Single(o => o.GetProperty("kind").GetString() == "Corrective").GetProperty("workOrderId").GetInt64();
        await plant.Ok(tech, $"/api/maintenance/work-orders/{repair}/parts", new { partNumber = "COIL-AL", quantity = 5m, locationCode = "STORES" });
        Assert.Contains("over_return", (await Try(plant, tech, $"/api/maintenance/work-orders/{repair}/parts/return", new { partNumber = "COIL-AL", quantity = 6m, locationCode = "STORES", reason = "Unused" })).Body, StringComparison.Ordinal);
        await plant.Ok(stores, $"/api/maintenance/work-orders/{repair}/parts/return", new { partNumber = "COIL-AL", quantity = 2m, locationCode = "STORES", reason = "Unused" });
        var view = (await Get(mp, "/api/maintenance/work-orders")).EnumerateArray().Single(v => v.GetProperty("order").GetProperty("workOrderId").GetInt64() == repair);
        var part = Assert.Single(view.GetProperty("parts").EnumerateArray());
        Assert.Equal(3m, part.GetProperty("netIssued").GetDecimal());

        // A run-hour PM plan: due after the machine has run 250 hours, with no calendar date.
        await plant.Ok(mp, "/api/maintenance/plans", new { machineCode = "SLITTER-A", title = "Bearing lubrication", intervalRunHours = 250m, durationMinutes = 30, tasks = new[] { "Grease main bearings" }, lockOutWhenOverdue = true, graceDays = 0, graceRunHours = 25m });
        var lube = (await Get(mp, "/api/maintenance/work-orders")).EnumerateArray().Single(v => v.GetProperty("order").GetProperty("title").GetString() == "Bearing lubrication");
        Assert.Equal(JsonValueKind.Null, lube.GetProperty("order").GetProperty("dueDate").ValueKind);
        Assert.Equal(250m, lube.GetProperty("order").GetProperty("dueRunHours").GetDecimal());
        Assert.True(lube.GetProperty("runHours").GetDecimal() < 1m);
        Assert.False(lube.GetProperty("dueByRunHours").GetBoolean());
        Assert.Contains("run hours", (await Try(plant, mp, "/api/maintenance/plans", new { machineCode = "SLITTER-A", title = "x", intervalRunHours = 0m, durationMinutes = 30, tasks = new[] { "x" }, lockOutWhenOverdue = false, graceDays = 0 })).Body, StringComparison.Ordinal);
    }
}
