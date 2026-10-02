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
        var pm = Assert.Single((await Get(mp, "/api/maintenance/work-orders")).EnumerateArray());
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
        var next = Assert.Single((await Get(mp, "/api/maintenance/work-orders")).EnumerateArray(), o => o.GetProperty("kind").GetString() == "Preventive");
        Assert.Equal("Open", next.GetProperty("status").GetString());

        // Skipping a PM needs a reason and raises the one after.
        var nextId = next.GetProperty("workOrderId").GetInt64();
        Assert.Contains("reason", (await Try(plant, mp, $"/api/maintenance/work-orders/{nextId}/cancel", new { })).Body, StringComparison.OrdinalIgnoreCase);
        var skipped = await plant.Ok(mp, $"/api/maintenance/work-orders/{nextId}/cancel", new { reason = "Machine idle this month" });
        Assert.Contains($"due {LocalDay(60)}", skipped.GetProperty("message").GetString(), StringComparison.Ordinal);

        // A breakdown raises a corrective order; the machine is back only when the repair is signed off.
        var down = await plant.Ok(eng, "/api/maintenance/machines/SLITTER-B/down", new { description = "Rewind motor tripping" });
        Assert.Contains("Corrective work order", down.GetProperty("message").GetString(), StringComparison.Ordinal);
        var repair = (await Get(mp, "/api/maintenance/work-orders")).EnumerateArray().Single(o => o.GetProperty("kind").GetString() == "Corrective").GetProperty("workOrderId").GetInt64();
        await plant.Ok(tech, $"/api/maintenance/work-orders/{repair}/start", new { });
        await plant.Ok(tech, $"/api/maintenance/work-orders/{repair}/complete", new { findings = "Replaced overload relay", tasksDone = Array.Empty<bool>() });
        Assert.Contains("repair_in_progress", (await Try(plant, eng, "/api/maintenance/machines/SLITTER-B/restore", new { reason = "x" })).Body, StringComparison.Ordinal);
        Assert.Contains((await Get(mp, "/api/maintenance/downtime?" + window)).EnumerateArray(), w => w.GetProperty("machineCode").GetString() == "SLITTER-B" && w.GetProperty("endsAt").ValueKind == JsonValueKind.Null);
        var back = await plant.Ok(mp, $"/api/maintenance/work-orders/{repair}/sign-off", new { });
        Assert.Contains("back in service", back.GetProperty("message").GetString(), StringComparison.Ordinal);
        var later = $"from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddSeconds(1).ToString("o"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(1).ToString("o"))}";
        Assert.DoesNotContain((await Get(mp, "/api/maintenance/downtime?" + later)).EnumerateArray(), w => w.GetProperty("machineCode").GetString() == "SLITTER-B");
    }
}
