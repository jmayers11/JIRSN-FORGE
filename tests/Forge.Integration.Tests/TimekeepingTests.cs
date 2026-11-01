using System.Globalization;
using System.Net;
using System.Text.Json;
using Npgsql;

namespace Forge.Integration.Tests;

/// <summary>
/// DCAA-style timekeeping and actual job cost through the real API (owner decisions 2026-10-31): employees record their own hours daily
/// against an order operation or a charge code, late entries carry a reason, a day holds at most 24 hours, the employee signs the week and
/// someone else approves it, corrections keep the original and reopen a signed week, and the job cost shows material at standard and the
/// hours timesheets charged to the order.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TimekeepingTests(PostgresFixture postgres)
{
    private static string Day(int fromToday) => DateTime.UtcNow.AddDays(fromToday).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Monday(int fromToday)
    {
        var d = DateTime.UtcNow.Date.AddDays(fromToday);
        return d.AddDays(-(((int)d.DayOfWeek + 6) % 7)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task Rejected(SlittingPlant.Plant plant, HttpClient client, string url, object body, string code)
    {
        var response = await plant.Try(client, url, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{url} -> {(int)response.StatusCode}: {text}");
        Assert.Contains(code, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Employees_record_daily_sign_and_a_supervisor_approves_corrections_reopen_and_the_job_cost_shows_charged_hours()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        foreach (var (subject, roles) in new[] { ("emp", new[] { "Employee" }), ("sup", new[] { "Supervisor" }), ("lead", new[] { "Supervisor", "Employee" }), ("cost", new[] { "CostAccountant" }) })
            await plant.Person(subject, roles);
        var (emp, sup, lead, cost, stores, e) = (plant.As("emp"), plant.As("sup"), plant.As("lead"), plant.As("cost"), plant.As("stores"), plant.Engineer);

        // Charge codes belong to the cost accountant; a contract code names its contract.
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(emp, "/api/timekeeping/charge-codes", new { code = "SHOP", name = "Shop support", kind = "Indirect", reason = "Setup" })).StatusCode);
        await Rejected(plant, cost, "/api/timekeeping/charge-codes", new { code = "ENG", name = "Engineering", kind = "Contract", reason = "Setup" }, "contract_required");
        await plant.Ok(cost, "/api/timekeeping/charge-codes", new { code = "SHOP", name = "Shop support", kind = "Indirect", reason = "Setup" });
        await plant.Ok(cost, "/api/timekeeping/charge-codes", new { code = "ENG-101", name = "Program engineering", kind = "Contract", contractNumber = "W91-26-C-0001", clin = "0002", reason = "Setup" });
        await plant.Ok(cost, "/api/timekeeping/charge-codes", new { code = "OLD", name = "Retired code", kind = "Overhead", reason = "Setup" });
        await plant.Ok(cost, "/api/timekeeping/charge-codes/OLD/active", new { active = false, reason = "Retired" });

        // A released order to charge, with material issued and standard costs set.
        await plant.Ok(cost, "/api/costing/standard-costs", new { partNumber = "COIL-AL", material = 2m, labor = 0m, overhead = 0m, reason = "FY2026 standard" });
        await plant.Ok(cost, "/api/costing/standard-costs", new { partNumber = "ROLL-100", material = 10m, labor = 4m, overhead = 6m, reason = "FY2026 standard" });
        var order = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "ROLL-100", quantity = 100m, dueDate = Day(10), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Customer order" });
        await plant.Ok(stores, "/api/inventory/issues", new { orderNumber = order, partNumber = "COIL-AL", quantity = 260m, locationCode = "STORES" });

        // Recording: own time only, real charges, quarter hours, at most 24 h a day, nothing in the future, late time with a reason.
        await Rejected(plant, emp, "/api/timekeeping/entries", new { workDate = Day(0), hours = 2m }, "charge_required");
        await Rejected(plant, emp, "/api/timekeeping/entries", new { workDate = Day(0), hours = 2m, chargeCode = "OLD" }, "charge_code_inactive");
        await Rejected(plant, emp, "/api/timekeeping/entries", new { workDate = Day(0), hours = 2m, orderNumber = order, sequence = 99 }, "order_not_chargeable");
        await Rejected(plant, emp, "/api/timekeeping/entries", new { workDate = Day(0), hours = 1.1m, chargeCode = "SHOP" }, "invalid_hours");
        await Rejected(plant, emp, "/api/timekeeping/entries", new { workDate = Day(1), hours = 1m, chargeCode = "SHOP" }, "future_time");
        await plant.Ok(emp, "/api/timekeeping/entries", new { workDate = Day(0), hours = 6m, orderNumber = order, sequence = 10, note = "Slitting" });
        await plant.Ok(emp, "/api/timekeeping/entries", new { workDate = Day(0), hours = 2m, chargeCode = "SHOP" });
        await Rejected(plant, emp, "/api/timekeeping/entries", new { workDate = Day(0), hours = 16.25m, chargeCode = "ENG-101" }, "day_over_24");
        await Rejected(plant, emp, "/api/timekeeping/entries", new { workDate = Day(-3), hours = 4m, chargeCode = "ENG-101" }, "late_reason_required");
        await plant.Ok(emp, "/api/timekeeping/entries", new { workDate = Day(-3), hours = 4m, chargeCode = "ENG-101", lateReason = "Was at the customer site without access" });
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(stores, "/api/timekeeping/entries", new { workDate = Day(0), hours = 1m, chargeCode = "SHOP" })).StatusCode);

        var mine = await Get(emp, "/api/timekeeping/my");
        Assert.Equal(Monday(0), mine.GetProperty("weekStart").GetString());
        var sheet = mine.GetProperty("timesheet").GetProperty("timesheet");
        Assert.Equal("Open", sheet.GetProperty("status").GetString());
        var line = Assert.Single(mine.GetProperty("timesheet").GetProperty("reconciliation").EnumerateArray());
        Assert.Equal((6m, 0m, 6m), (line.GetProperty("timesheetHours").GetDecimal(), line.GetProperty("shopFloorHours").GetDecimal(), line.GetProperty("difference").GetDecimal()));
        var sheetId = sheet.GetProperty("timesheetId").GetString()!;
        var orderEntry = sheet.GetProperty("entries").EnumerateArray().Single(x => x.ValueKind == JsonValueKind.Object && x.GetProperty("orderNumber").ValueKind == JsonValueKind.Number).GetProperty("entryId").GetString()!;

        // Sign, then someone else approves; nobody approves their own week.
        await plant.Ok(emp, "/api/timekeeping/sign", new { weekStart = Monday(0) });
        await Rejected(plant, emp, "/api/timekeeping/entries", new { workDate = Day(0), hours = 1m, chargeCode = "SHOP" }, "timesheet_signed");
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(emp, $"/api/timekeeping/timesheets/{sheetId}/approve", new { })).StatusCode);
        await plant.Ok(lead, "/api/timekeeping/entries", new { workDate = Day(0), hours = 7.75m, chargeCode = "SHOP" });
        await plant.Ok(lead, "/api/timekeeping/sign", new { weekStart = Monday(0) });
        var leadSheet = (await Get(sup, $"/api/timekeeping/timesheets?week={Monday(0)}")).GetProperty("timesheets").EnumerateArray()
            .Single(v => v.GetProperty("hours").GetDecimal() == 7.75m).GetProperty("timesheet").GetProperty("timesheetId").GetString();
        await Rejected(plant, lead, $"/api/timekeeping/timesheets/{leadSheet}/approve", new { }, "separation_of_duties");
        await plant.Ok(sup, $"/api/timekeeping/timesheets/{sheetId}/approve", new { });

        // Job cost: 260 kg of coil at 2.00 = 520.00 material; the timesheets charge 6 h to the order, all approved.
        var job = await Get(cost, $"/api/costing/orders/{order}/job-cost");
        var material = job.GetProperty("elements").EnumerateArray().Single(x => x.GetProperty("element").GetString() == "Material");
        Assert.Equal(520m, material.GetProperty("actual").GetDecimal());
        Assert.Equal((6m, 6m), (job.GetProperty("timesheetHours").GetDecimal(), job.GetProperty("timesheetHoursApproved").GetDecimal()));
        Assert.Contains(job.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains("Timesheets charge 6", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Forbidden, (await emp.GetAsync(new Uri($"/api/costing/orders/{order}/job-cost", UriKind.Relative))).StatusCode);

        // A correction needs a reason, keeps the original and sends the approved week back for signature.
        Assert.Contains("reason", await (await plant.Try(emp, $"/api/timekeeping/entries/{orderEntry}/correct", new { hours = 5m, orderNumber = order, sequence = 10 })).Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        await plant.Ok(emp, $"/api/timekeeping/entries/{orderEntry}/correct", new { hours = 5m, orderNumber = order, sequence = 10, reason = "One hour was shop cleanup" });
        var reopened = (await Get(emp, "/api/timekeeping/my")).GetProperty("timesheet").GetProperty("timesheet");
        Assert.Equal("Open", reopened.GetProperty("status").GetString());
        var entries = reopened.GetProperty("entries").EnumerateArray().ToList();
        Assert.Contains(entries, x => x.GetProperty("entryId").GetString() == orderEntry && x.GetProperty("state").GetString() == "Superseded");
        Assert.Contains(entries, x => x.GetProperty("supersedesEntryId").ValueKind == JsonValueKind.String && x.GetProperty("correctionReason").GetString() == "One hour was shop cleanup");
        var after = await Get(cost, $"/api/costing/orders/{order}/job-cost");
        Assert.Equal((5m, 0m), (after.GetProperty("timesheetHours").GetDecimal(), after.GetProperty("timesheetHoursApproved").GetDecimal()));

        // The supervisor can return a signed week with a reason; recorded entries cannot be rewritten or deleted in the database.
        await plant.Ok(emp, "/api/timekeeping/sign", new { weekStart = Monday(0) });
        Assert.Contains("reason", await (await plant.Try(sup, $"/api/timekeeping/timesheets/{sheetId}/return", new { })).Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        await plant.Ok(sup, $"/api/timekeeping/timesheets/{sheetId}/return", new { reason = "Charge the cleanup hour to SHOP" });
        Assert.Equal("Returned", (await Get(emp, "/api/timekeeping/my")).GetProperty("timesheet").GetProperty("timesheet").GetProperty("status").GetString());
        await using var db = new NpgsqlConnection(app.ConnectionString);
        await db.OpenAsync();
        await using (var tamper = new NpgsqlCommand("UPDATE workforce.time_entry SET hours = 9 WHERE entry_id = @id", db))
        {
            tamper.Parameters.AddWithValue("id", Guid.Parse(orderEntry));
            await Assert.ThrowsAsync<PostgresException>(() => tamper.ExecuteNonQueryAsync());
        }

        await using var delete = new NpgsqlCommand("DELETE FROM workforce.time_entry", db);
        await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
    }
}
