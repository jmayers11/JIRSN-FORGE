using System.Globalization;
using System.Net;
using System.Text.Json;
using Npgsql;

namespace Forge.Integration.Tests;

/// <summary>
/// V2.10 through the real API: a facility project is approved by a second person; changes after approval need a
/// reason and a second person; its material is checked against stock, shortages become project demand and
/// requisitions, purchases and receipts carry the project line, issues and returns balance, and "why was this
/// purchased?" answers with the project line (scenario 6).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class V210Tests(PostgresFixture postgres)
{
    private const string Fac = "FAC-2027-014";

    private static string Day(int fromToday) => DateTime.UtcNow.AddDays(fromToday).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

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

    private static async Task Rejected(SlittingPlant.Plant plant, HttpClient client, string url, object body, string code, HttpStatusCode status = HttpStatusCode.UnprocessableEntity)
    {
        var (actual, text) = await Try(plant, client, url, body);
        Assert.True(actual == status, $"{url} -> {(int)actual}: {text}");
        Assert.Contains(code, text, StringComparison.Ordinal);
    }

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static async Task BoughtAsync(SlittingPlant.Plant plant, string part, bool lotTracked = false)
    {
        await plant.Ok(plant.Engineer, "/api/engineering/items", new { partNumber = part, description = part, unitOfMeasure = "EA", makeBuy = "Buy", leadTimeDays = 10, lotTracked });
        await plant.Ok(plant.Engineer, $"/api/engineering/items/{part}/revisions", new { revision = "A" });
        await plant.Ok(plant.Approver, $"/api/engineering/items/{part}/revisions/A/release", new { reason = "Baseline" });
    }

    private static async Task<JsonElement> Detail(HttpClient client, string code = Fac) => await Get(client, $"/api/projects/{code}");

    private static Dictionary<int, JsonElement> Material(JsonElement detail) =>
        detail.GetProperty("material").GetProperty("material").GetProperty("lines").EnumerateArray().ToDictionary(l => l.GetProperty("lineNumber").GetInt32());

    private static object Header(string? reference = null, decimal budget = 120000m) =>
        new { title = "Gas line for slitter 3", budget, startDate = Day(1), endDate = Day(90), financialReference = reference };

    [Fact]
    public async Task Scenario_6_a_facility_project_buys_receives_and_issues_against_its_lines_under_controlled_change()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("mp", "MaintenancePlanner");
        await plant.Person("fm", "FacilitiesManager");
        await plant.Person("fm2", "FacilitiesManager");
        await plant.Person("bob", "Buyer");
        await plant.Person("pat", "Buyer", "PurchasingManager");
        var (mp, fm, fm2, bob, pat, stores) = (plant.As("mp"), plant.As("fm"), plant.As("fm2"), plant.As("bob"), plant.As("pat"), plant.As("stores"));
        await BoughtAsync(plant, "PIPE-2");
        await BoughtAsync(plant, "VALVE-1");
        await BoughtAsync(plant, "REG-1", lotTracked: true);
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "PIPE-2", quantity = 30m, locationCode = "STORES", reason = "Opening" });
        await plant.Ok(bob, "/api/procurement/suppliers", new { code = "FERGUSON", name = "Ferguson" });

        // A draft: created and edited directly by whoever manages projects.
        var url = "/api/projects";
        object Create(string code, string type = "Facility", string funding = "Capital", string? start = null, string? end = null) =>
            new { code, title = "Gas line for slitter 3", type, funding, budget = 120000m, startDate = start ?? Day(1), endDate = end ?? Day(90), description = "Natural gas to the new slitter" };
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, bob, url, Create(Fac))).Status);
        await Rejected(plant, mp, url, Create(Fac, start: Day(10), end: Day(5)), "invalid_dates");
        await plant.Ok(mp, url, Create(Fac));
        await Rejected(plant, mp, url, Create(Fac), "project_exists");
        foreach (var (part, quantity) in new[] { ("PIPE-2", 50m), ("VALVE-1", 6m), ("REG-1", 3m) })
        {
            await plant.Ok(mp, $"{url}/{Fac}/lines", new { partNumber = part, quantity, needDate = Day(20), description = $"{part} for the gas line" });
        }

        await plant.Ok(mp, $"{url}/{Fac}/lines/3", new { quantity = 2m, needDate = Day(20) });
        await Rejected(plant, stores, $"{url}/{Fac}/issues", new { lineNumber = 1, partNumber = "PIPE-2", quantity = 1m, locationCode = "STORES" }, "project_not_open");

        // Approval: a different person with authority, against a budget and (capital funding) a financial reference.
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, mp, $"{url}/{Fac}/approve", new { reason = "x" })).Status);
        await Rejected(plant, fm, $"{url}/{Fac}/approve", new { reason = "Board approved" }, "financial_reference_required");
        await plant.Ok(mp, $"{url}/{Fac}", Header("CAPEX-2027-014"));
        await Rejected(plant, fm, $"{url}/{Fac}/approve", new { }, "reason_required");
        await plant.Ok(fm, $"{url}/{Fac}/approve", new { reason = "Capital committee 2026-10" });
        await Rejected(plant, fm2, $"{url}/{Fac}/approve", new { reason = "Again" }, "not_draft");
        var detail = await Detail(planner);
        Assert.Equal(("Approved", "CAPEX-2027-014"), (detail.GetProperty("project").GetProperty("status").GetString(), detail.GetProperty("project").GetProperty("financialReference").GetString()));

        // After approval every change needs a reason and is applied only when someone else approves it.
        await Rejected(plant, mp, $"{url}/{Fac}/lines", new { partNumber = "VALVE-1", quantity = 2m, needDate = Day(25) }, "reason_required");
        var addLine = Id(await plant.Ok(mp, $"{url}/{Fac}/lines", new { partNumber = "VALVE-1", quantity = 2m, needDate = Day(25), reason = "Bypass valves added in design review" }));
        var budget = Id(await plant.Ok(fm, $"{url}/{Fac}", new { title = "Gas line for slitter 3", budget = 150000m, startDate = Day(1), endDate = Day(90), financialReference = "CAPEX-2027-014", reason = "Contractor quote came in higher" }));
        await Rejected(plant, fm, $"{url}/{Fac}", new { title = "Gas line for slitter 3", budget = 160000m, startDate = Day(1), endDate = Day(90), financialReference = "CAPEX-2027-014", reason = "Again" }, "change_pending");
        Assert.Equal(3, (await Detail(planner)).GetProperty("lines").GetArrayLength());
        Assert.Equal(120000m, (await Detail(planner)).GetProperty("project").GetProperty("budget").GetDecimal());
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, mp, $"{url}/changes/{budget}/approve", new { reason = "x" })).Status);
        await Rejected(plant, fm, $"{url}/changes/{budget}/approve", new { reason = "x" }, "separation_of_duties");
        await Rejected(plant, fm2, $"{url}/changes/{budget}/approve", new { }, "reason_required");
        await plant.Ok(fm2, $"{url}/changes/{budget}/approve", new { reason = "Quote reviewed" });
        await plant.Ok(fm, $"{url}/changes/{addLine}/reject", new { reason = "Bypass not in the approved design" });
        await Rejected(plant, fm2, $"{url}/changes/{addLine}/approve", new { reason = "x" }, "change_decided");
        detail = await Detail(planner);
        Assert.Equal((150000m, 3), (detail.GetProperty("project").GetProperty("budget").GetDecimal(), detail.GetProperty("lines").GetArrayLength()));
        Assert.Equal(["Approved", "Rejected"], detail.GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("status").GetString()!).ToArray());

        // Scenario 6: material checked against stock; shortages become project demand and requisitions sourced to the line.
        var lines = Material(detail);
        Assert.Equal(("Short", 30m, 20m), (lines[1].GetProperty("status").GetString(), lines[1].GetProperty("fromStock").GetDecimal(), lines[1].GetProperty("shortage").GetDecimal()));
        Assert.Equal((6m, 2m), (lines[2].GetProperty("shortage").GetDecimal(), lines[3].GetProperty("shortage").GetDecimal()));
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, plant.As("pat"), $"{url}/{Fac}/request-material", new { })).Status);
        var asked = await plant.Ok(fm, $"{url}/{Fac}/request-material", new { reason = "Order the gas line material" });
        Assert.Equal(3, asked.GetProperty("lines").EnumerateArray().Count(l => l.GetProperty("outcome").GetString() == "Requested"));
        Assert.Empty((await plant.Ok(fm, $"{url}/{Fac}/request-material", new { reason = "Again" })).GetProperty("lines").EnumerateArray());
        var demand = (await Get(planner, "/api/planning/demand")).EnumerateArray().Single(x => x.GetProperty("sourceKey").GetString() == $"project:{Fac}/3");
        Assert.Equal(("FacilityProject", 2m), (demand.GetProperty("kind").GetString(), demand.GetProperty("quantity").GetDecimal()));
        var requisitions = (await Get(bob, "/api/procurement/requisitions")).EnumerateArray().ToDictionary(r => r.GetProperty("sourceKey").GetString()!);
        Assert.Equal(("FacilityProject", 20m), (requisitions[$"project:{Fac}/1"].GetProperty("sourceKind").GetString(), requisitions[$"project:{Fac}/1"].GetProperty("quantity").GetDecimal()));
        Assert.Contains($"{Fac} line 3", requisitions[$"project:{Fac}/3"].GetProperty("sourceLabel").GetString(), StringComparison.Ordinal);

        // The buyer orders them; a second person approves; receipts against the project's purchase lines record the line.
        var po = Id(await plant.Ok(bob, "/api/procurement/purchase-orders", new
        {
            supplier = "FERGUSON",
            lines = new[] { 1, 2, 3 }.Select(n => new { partNumber = new[] { "PIPE-2", "VALVE-1", "REG-1" }[n - 1], quantity = requisitions[$"project:{Fac}/{n}"].GetProperty("quantity").GetDecimal(), promisedDate = Day(14), requisitionId = (long?)requisitions[$"project:{Fac}/{n}"].GetProperty("requisitionId").GetInt64() }).ToArray(),
        }));
        await plant.Ok(pat, $"/api/procurement/purchase-orders/{po}/approve", new { reason = "Within project budget" });
        Assert.Equal(["On order", "On order", "On order"], Material(await Detail(planner)).Values.Select(l => l.GetProperty("status").GetString()!).ToArray());
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "REG-1", quantity = 2m, locationCode = "STORES", lotNumber = "R-77", poNumber = po, poLine = 3 });

        // No double counting: demand for each line is met by stock and the open purchase, so MRP plans nothing more.
        var mrp = (await Get(planner, "/api/planning/mrp")).GetProperty("output");
        Assert.DoesNotContain(mrp.GetProperty("plannedOrders").EnumerateArray(), p => p.GetProperty("partNumber").GetString() is "PIPE-2" or "VALVE-1" or "REG-1");

        // Issues and returns against the line, never more than it needs or more back than went out.
        var issues = $"{url}/{Fac}/issues";
        var returns = $"{url}/{Fac}/returns";
        await Rejected(plant, stores, issues, new { lineNumber = 2, partNumber = "REG-1", quantity = 1m, locationCode = "STORES", lotNumber = "R-77" }, "line_part");
        await Rejected(plant, stores, issues, new { lineNumber = 9, partNumber = "REG-1", quantity = 1m, locationCode = "STORES", lotNumber = "R-77" }, "line_not_found", HttpStatusCode.NotFound);
        await Rejected(plant, stores, issues, new { lineNumber = 3, partNumber = "REG-1", quantity = 3m, locationCode = "STORES", lotNumber = "R-77" }, "over_issue");
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, bob, issues, new { lineNumber = 3, partNumber = "REG-1", quantity = 2m, locationCode = "STORES", lotNumber = "R-77" })).Status);
        var issued = Id(await plant.Ok(stores, issues, new { lineNumber = 3, partNumber = "REG-1", quantity = 2m, locationCode = "STORES", lotNumber = "R-77" }));
        await Rejected(plant, stores, returns, new { lineNumber = 3, partNumber = "REG-1", quantity = 1m, locationCode = "STORES", lotNumber = "R-77" }, "reason_required");
        await Rejected(plant, stores, returns, new { lineNumber = 3, partNumber = "REG-1", quantity = 3m, locationCode = "STORES", lotNumber = "R-77", reason = "Spare" }, "over_return");
        await plant.Ok(stores, returns, new { lineNumber = 3, partNumber = "REG-1", quantity = 1m, locationCode = "STORES", lotNumber = "R-77", reason = "One regulator not needed after all" });
        detail = await Detail(planner);
        var line3 = Material(detail)[3];
        Assert.Equal((1m, 2m, "In stock"), (line3.GetProperty("issued").GetDecimal(), line3.GetProperty("received").GetDecimal(), line3.GetProperty("status").GetString()));
        Assert.Equal(["ProjectReturn", "ProjectIssue", "Receipt"], detail.GetProperty("material").GetProperty("movements").EnumerateArray().Select(t => t.GetProperty("kind").GetString()!).ToArray());
        var balance = (await Get(stores, "/api/inventory/stock?part=REG-1")).EnumerateArray().Sum(b => b.GetProperty("quantity").GetDecimal());
        Assert.Equal(1m, balance); // received 2, issued 2, returned 1

        // Why was this purchased? The project line, from the purchase line and from the issue.
        var why = await Get(bob, $"/api/procurement/purchase-orders/{po}/lines/3/why");
        Assert.Equal(("FacilityProject", $"project:{Fac}/3"), (why.GetProperty("sourceKind").GetString(), why.GetProperty("sourceKey").GetString()));
        Assert.Contains($"{Fac} line 3", why.GetProperty("answer").GetString(), StringComparison.Ordinal);
        Assert.Contains($"issued to project {Fac} line 3", why.GetProperty("answer").GetString(), StringComparison.Ordinal);
        var fromIssue = await Get(stores, $"/api/inventory/transactions/{issued}/why?part=REG-1");
        Assert.Equal($"project:{Fac}/3", Assert.Single(fromIssue.GetProperty("purchases").EnumerateArray()).GetProperty("sourceKey").GetString());

        // A line is never reduced below, or cancelled with, what is issued to it.
        await Rejected(plant, mp, $"{url}/{Fac}/lines/3", new { quantity = 0.5m, needDate = Day(20), reason = "Smaller" }, "below_issued");
        await Rejected(plant, mp, $"{url}/{Fac}/lines/3/cancel", new { reason = "Not needed" }, "below_issued");

        // Outages the project needs are proposals like any other; tasks and milestones are tracked.
        var outage = await plant.Ok(mp, $"{url}/{Fac}/outages", new { machineCode = "SLITTER-A", startsAt = DateTimeOffset.UtcNow.AddDays(10), endsAt = DateTimeOffset.UtcNow.AddDays(10).AddHours(6), description = "Tie-in of the gas line" });
        Assert.Contains("proposed", outage.GetProperty("message").GetString(), StringComparison.Ordinal);
        var listed = Assert.Single((await Detail(planner)).GetProperty("outages").EnumerateArray());
        Assert.Equal(("Proposed", Fac), (listed.GetProperty("status").GetString(), listed.GetProperty("projectCode").GetString()));
        var task = Id(await plant.Ok(mp, $"{url}/{Fac}/tasks", new { title = "Pressure test", dueDate = Day(40), milestone = true }));
        await Rejected(plant, mp, $"{url}/tasks/{task}/complete", new { completedOn = Day(5) }, "completed_in_future");
        await plant.Ok(mp, $"{url}/tasks/{task}/complete", new { completedOn = Day(0) });

        // Completed: nothing more is issued or changed; the records are kept as they were.
        await plant.Ok(mp, $"{url}/{Fac}/complete", new { reason = "Gas line commissioned" });
        await Rejected(plant, stores, issues, new { lineNumber = 1, partNumber = "PIPE-2", quantity = 1m, locationCode = "STORES" }, "project_not_open");
        await Rejected(plant, mp, $"{url}/{Fac}", Header("CAPEX-2027-014"), "project_closed");
        Assert.Equal("Completed", (await Get(planner, "/api/projects/")).EnumerateArray().Single().GetProperty("project").GetProperty("status").GetString());

        await using var db = new NpgsqlConnection(app.ConnectionString);
        await db.OpenAsync();
        foreach (var sql in new[]
        {
            "DELETE FROM projects.line",
            $"UPDATE projects.project SET budget = 1 WHERE code = '{Fac}'",
            $"UPDATE projects.change SET budget = 1 WHERE change_id = {budget}",
            "UPDATE inventory.transaction SET project_code = NULL WHERE kind = 'ProjectIssue'",
        })
        {
            await using var tamper = new NpgsqlCommand(sql, db);
            await Assert.ThrowsAsync<PostgresException>(() => tamper.ExecuteNonQueryAsync());
        }
    }

    [Fact]
    public async Task A_capital_project_buys_as_capital_project_demand_and_a_draft_is_cancelled_not_completed()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("me", "ManufacturingEngineer");
        await plant.Person("fm", "FacilitiesManager");
        var (me, fm) = (plant.As("me"), plant.As("fm"));
        await BoughtAsync(plant, "DRIVE-7");

        await plant.Ok(me, "/api/projects", new { code = "CAP-2027-002", title = "Rewinder drive upgrade", type = "Capital", funding = "Operating", startDate = Day(0), endDate = Day(60) });
        await plant.Ok(me, "/api/projects/CAP-2027-002/lines", new { partNumber = "DRIVE-7", quantity = 1m, needDate = Day(30) });
        await Rejected(plant, fm, "/api/projects/CAP-2027-002/approve", new { reason = "x" }, "budget_required");
        await Rejected(plant, me, "/api/projects/CAP-2027-002/complete", new { reason = "x" }, "not_approved");
        await plant.Ok(me, "/api/projects/CAP-2027-002", new { title = "Rewinder drive upgrade", budget = 48000m, startDate = Day(0), endDate = Day(60) });
        await plant.Ok(fm, "/api/projects/CAP-2027-002/approve", new { reason = "Operating budget line 14" });
        await plant.Ok(fm, "/api/projects/CAP-2027-002/request-material", new { reason = "Order the drive" });
        var requisition = (await Get(planner, "/api/procurement/requisitions")).EnumerateArray().Single();
        Assert.Equal(("CapitalProject", "project:CAP-2027-002/1"), (requisition.GetProperty("sourceKind").GetString(), requisition.GetProperty("sourceKey").GetString()));
        Assert.Equal("CapitalProject", (await Get(planner, "/api/planning/demand")).EnumerateArray().Single().GetProperty("kind").GetString());

        // A pending change blocks completion; only someone with approval authority cancels a project.
        await plant.Ok(me, "/api/projects/CAP-2027-002/lines/1", new { quantity = 2m, needDate = Day(30), reason = "Second drive for the unwind" });
        await Rejected(plant, me, "/api/projects/CAP-2027-002/complete", new { reason = "Done" }, "changes_pending");
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, me, "/api/projects/CAP-2027-002/cancel", new { reason = "x" })).Status);

        await plant.Ok(me, "/api/projects", new { code = "FAC-2027-099", title = "Roof drains", type = "Facility", funding = "Operating", budget = 9000m, startDate = Day(0), endDate = Day(30) });
        await plant.Ok(fm, "/api/projects/FAC-2027-099/cancel", new { reason = "Covered by the landlord" });
        Assert.Equal("Cancelled", (await Get(planner, "/api/projects/FAC-2027-099")).GetProperty("project").GetProperty("status").GetString());
        await Rejected(plant, me, "/api/projects/FAC-2027-099/lines", new { partNumber = "DRIVE-7", quantity = 1m, needDate = Day(5) }, "project_closed");
    }
}
