using System.Globalization;
using System.Net;
using System.Text.Json;
using Npgsql;

namespace Forge.Integration.Tests;

/// <summary>
/// V2.6 through the real API: alternates are proposed by one person and approved by another; proposed ones
/// are never used by MRP, issue or purchasing; an automatic BOM line lets MRP substitute an approved
/// equivalent up to the shortage and record why (scenario 2); an unapproved substitute stays apart
/// (scenario 3); a program condition is honoured per program (scenario 9); expired alternates stop.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class V26Tests(PostgresFixture postgres)
{
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

    private static async Task Rejected(SlittingPlant.Plant plant, HttpClient client, string url, object body, string code)
    {
        var (status, text) = await Try(plant, client, url, body);
        Assert.True(status == HttpStatusCode.UnprocessableEntity, $"{url} -> {(int)status}: {text}");
        Assert.Contains(code, text, StringComparison.Ordinal);
    }

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static async Task BoughtAsync(SlittingPlant.Plant plant, string part, string uom = "EA")
    {
        await plant.Ok(plant.Engineer, "/api/engineering/items", new { partNumber = part, description = part, unitOfMeasure = uom, makeBuy = "Buy", leadTimeDays = 10 });
        await plant.Ok(plant.Engineer, $"/api/engineering/items/{part}/revisions", new { revision = "A" });
        await plant.Ok(plant.Approver, $"/api/engineering/items/{part}/revisions/A/release", new { reason = "Baseline" });
    }

    private static async Task<JsonElement> MrpAsync(HttpClient planner) => (await Get(planner, "/api/planning/mrp")).GetProperty("output");

    private static IEnumerable<JsonElement> Planned(JsonElement mrp, string part) => mrp.GetProperty("plannedOrders").EnumerateArray().Where(p => p.GetProperty("partNumber").GetString() == part);

    [Fact]
    public async Task Alternates_are_approved_by_someone_else_and_mrp_substitutes_only_where_the_line_allows()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var (e, approver) = (plant.Engineer, plant.Approver);
        await plant.Person("bob", "Buyer");
        await plant.Person("lead", "Buyer", "EngineeringApprover");
        var (bob, lead, stores) = (plant.As("bob"), plant.As("lead"), plant.As("stores"));

        // SPOOL is made from CORE-1. There are 8 CORE-1 and 20 CORE-EQ in stores; a planned order needs 20 CORE-1.
        await BoughtAsync(plant, "CORE-1");
        await BoughtAsync(plant, "CORE-EQ");
        await BoughtAsync(plant, "CORE-X");
        await BoughtAsync(plant, "CORE-KG", "KG");
        await plant.Ok(e, "/api/engineering/items", new { partNumber = "SPOOL", description = "Spool", unitOfMeasure = "EA", makeBuy = "Make", leadTimeDays = 2 });
        await plant.Ok(e, "/api/engineering/items/SPOOL/revisions", new { revision = "A" });
        await plant.Ok(e, "/api/engineering/items/SPOOL/revisions/A/bom", new { lineNumber = 1, componentPartNumber = "CORE-1", quantityPer = 1m, scrapPercent = 0m });
        await plant.Ok(e, "/api/engineering/items/SPOOL/revisions/A/routing", new { sequence = 10, description = "Wind", workCenterCode = "SLIT-WC", activityCode = "RUN_SLITTER", eligibleMachines = new[] { "SLITTER-A" }, setupMinutes = 0m, runMinutesPerUnit = 1m, crewSize = 1 });
        await plant.Ok(approver, "/api/engineering/items/SPOOL/revisions/A/release", new { reason = "Baseline" });
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "CORE-1", quantity = 8m, locationCode = "STORES", reason = "Opening" });
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "CORE-EQ", quantity = 20m, locationCode = "STORES", reason = "Opening" });
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "CORE-X", quantity = 50m, locationCode = "STORES", reason = "Opening" });
        var order = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "SPOOL", quantity = 20m, dueDate = Day(12), priority = 3 }));

        // Proposals: validated, recorded as proposed, never decided by the proposer or without authority.
        await Rejected(plant, bob, "/api/engineering/alternates", new { primaryPart = "CORE-1", alternatePart = "CORE-1", kind = "Equivalent" }, "same_part");
        await Rejected(plant, bob, "/api/engineering/alternates", new { primaryPart = "CORE-1", alternatePart = "CORE-KG", kind = "Equivalent", factor = 1m }, "conversion_required");
        await Rejected(plant, bob, "/api/engineering/alternates", new { primaryPart = "CORE-1", alternatePart = "CORE-EQ", kind = "Temporary" }, "end_date_required");
        await Rejected(plant, bob, "/api/engineering/alternates", new { primaryPart = "CORE-1", alternatePart = "CORE-EQ", kind = "Equivalent", parentPart = "ROLL-100" }, "not_in_bom");
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, planner, "/api/engineering/alternates", new { primaryPart = "CORE-1", alternatePart = "CORE-EQ", kind = "Equivalent" })).Status);
        var eq = Id(await plant.Ok(bob, "/api/engineering/alternates", new { primaryPart = "CORE-1", alternatePart = "CORE-EQ", kind = "Equivalent", factor = 1m, evidence = "Same supplier drawing, lot test TR-12" }));
        await Rejected(plant, bob, "/api/engineering/alternates", new { primaryPart = "CORE-1", alternatePart = "CORE-EQ", kind = "Equivalent" }, "alternate_exists");
        await plant.Ok(bob, "/api/engineering/alternates", new { primaryPart = "CORE-1", alternatePart = "CORE-KG", kind = "ApprovedAlternate", factor = 0.25m });

        // Proposed: MRP neither substitutes nor suggests it; candidates list it apart.
        var mrp = await MrpAsync(planner);
        Assert.Equal(12m, Assert.Single(Planned(mrp, "CORE-1")).GetProperty("quantity").GetDecimal());
        Assert.Empty(mrp.GetProperty("substitutions").EnumerateArray());
        Assert.DoesNotContain(mrp.GetProperty("exceptions").EnumerateArray(), x => x.GetProperty("kind").GetString() == "AlternateSuggested");
        var candidates = await Get(planner, "/api/engineering/alternates/candidates/CORE-1");
        Assert.Empty(candidates.GetProperty("approved").EnumerateArray());
        Assert.Equal(2, candidates.GetProperty("requiresApproval").GetArrayLength());

        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, bob, $"/api/engineering/alternates/{eq}/approve", new { })).Status);
        await plant.Ok(approver, $"/api/engineering/alternates/{eq}/approve", new { reason = "TR-12 reviewed" });
        await Rejected(plant, approver, $"/api/engineering/alternates/{eq}/approve", new { }, "invalid_status");

        // Approved, but the line only suggests: MRP lists it and still plans the purchase.
        mrp = await MrpAsync(planner);
        Assert.Equal(12m, Assert.Single(Planned(mrp, "CORE-1")).GetProperty("quantity").GetDecimal());
        Assert.Contains(mrp.GetProperty("exceptions").EnumerateArray(), x => x.GetProperty("kind").GetString() == "AlternateSuggested" && x.GetProperty("detail").GetString()!.Contains("CORE-EQ has 20 free", StringComparison.Ordinal));

        // Scenario 2: the line becomes automatic (approval authority and a reason). MRP allocates the alternate up to the shortage and says why.
        var policy = "/api/engineering/items/SPOOL/revisions/A/bom/1/policy";
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, e, policy, new { policy = "Automatic", reason = "x" })).Status);
        await Rejected(plant, approver, policy, new { policy = "Automatic" }, "reason_required");
        await plant.Ok(approver, policy, new { policy = "Automatic", reason = "Equivalents qualified for spools" });
        mrp = await MrpAsync(planner);
        Assert.Empty(Planned(mrp, "CORE-1"));
        var sub = Assert.Single(mrp.GetProperty("substitutions").EnumerateArray());
        Assert.Equal(("CORE-1", "CORE-EQ", eq, "Automatic"), (sub.GetProperty("primaryPart").GetString(), sub.GetProperty("alternatePart").GetString(), sub.GetProperty("relationshipId").GetInt64(), sub.GetProperty("policy").GetString()));
        Assert.Equal((12m, 20m, 12m), (sub.GetProperty("shortage").GetDecimal(), sub.GetProperty("alternateAvailable").GetDecimal(), sub.GetProperty("alternateQuantity").GetDecimal()));

        // Where-used, both directions.
        var used = await Get(planner, "/api/engineering/alternates/where-used/CORE-1");
        var line = Assert.Single(used.GetProperty("usedIn").EnumerateArray());
        Assert.Equal(("SPOOL", "Automatic", true), (line.GetProperty("parentPart").GetString(), line.GetProperty("policy").GetString(), line.GetProperty("inForce").GetBoolean()));
        Assert.Equal(2, used.GetProperty("alternates").GetArrayLength());
        var back = Assert.Single((await Get(planner, "/api/engineering/alternates/where-used/CORE-EQ")).GetProperty("alternateFor").EnumerateArray());
        Assert.Equal("CORE-1", back.GetProperty("primaryPart").GetString());

        // Scenario 3: an unapproved substitute stays apart and cannot be issued; its proposer never approves it.
        var x = Id(await plant.Ok(lead, "/api/engineering/alternates", new { primaryPart = "CORE-1", alternatePart = "CORE-X", kind = "EngineeringApprovalRequired", qualityNotes = "Different coating" }));
        await Rejected(plant, lead, $"/api/engineering/alternates/{x}/approve", new { }, "separation_of_duties");
        candidates = await Get(planner, "/api/engineering/alternates/candidates/CORE-1?parent=SPOOL");
        Assert.Equal(["CORE-EQ"], candidates.GetProperty("approved").EnumerateArray().Select(c => c.GetProperty("offeredPart").GetString()!).ToArray());
        Assert.Contains(candidates.GetProperty("requiresApproval").EnumerateArray(), c => c.GetProperty("offeredPart").GetString() == "CORE-X");

        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Customer order" });
        var issue = "/api/inventory/issues";
        await Rejected(plant, stores, issue, new { orderNumber = order, partNumber = "CORE-X", quantity = 5m, locationCode = "STORES", alternateFor = "CORE-1" }, "alternate_not_approved");
        await Rejected(plant, stores, issue, new { orderNumber = order, partNumber = "CORE-EQ", quantity = 5m, locationCode = "STORES" }, "not_on_order");
        await plant.Ok(stores, issue, new { orderNumber = order, partNumber = "CORE-1", quantity = 8m, locationCode = "STORES" });
        await plant.Ok(stores, issue, new { orderNumber = order, partNumber = "CORE-EQ", quantity = 5m, locationCode = "STORES", alternateFor = "CORE-1" });
        await Rejected(plant, stores, issue, new { orderNumber = order, partNumber = "CORE-EQ", quantity = 8m, locationCode = "STORES", alternateFor = "CORE-1" }, "over_issue");
        await plant.Ok(stores, issue, new { orderNumber = order, partNumber = "CORE-EQ", quantity = 7m, locationCode = "STORES", alternateFor = "CORE-1" });
        await Rejected(plant, stores, issue, new { orderNumber = order, partNumber = "CORE-1", quantity = 1m, locationCode = "STORES" }, "over_issue");
        await Rejected(plant, stores, "/api/inventory/returns", new { orderNumber = order, partNumber = "CORE-EQ", quantity = 2m, locationCode = "STORES", reason = "x" }, "over_return");
        await plant.Ok(stores, "/api/inventory/returns", new { orderNumber = order, partNumber = "CORE-EQ", quantity = 2m, locationCode = "STORES", alternateFor = "CORE-1", reason = "Two left over" });
        var ledger = (await Get(stores, "/api/inventory/transactions?part=CORE-EQ")).EnumerateArray().Where(t => t.GetProperty("orderNumber").ValueKind == JsonValueKind.Number).ToList();
        Assert.Equal(3, ledger.Count);
        Assert.All(ledger, t => Assert.Equal(("CORE-1", eq), (t.GetProperty("substitutesFor").GetString(), t.GetProperty("alternateId").GetInt64())));

        // Expired alternates stop: no more issues, no longer offered.
        await Rejected(plant, approver, $"/api/engineering/alternates/{eq}/expire", new { }, "reason_required");
        await plant.Ok(approver, $"/api/engineering/alternates/{eq}/expire", new { reason = "CORE-EQ supplier lost its approval" });
        await Rejected(plant, stores, issue, new { orderNumber = order, partNumber = "CORE-EQ", quantity = 1m, locationCode = "STORES", alternateFor = "CORE-1" }, "alternate_not_approved");
        Assert.DoesNotContain((await Get(planner, "/api/engineering/alternates/candidates/CORE-1")).GetProperty("approved").EnumerateArray(), c => c.GetProperty("offeredPart").GetString() == "CORE-EQ");
        await plant.Ok(approver, $"/api/engineering/alternates/{x}/reject", new { reason = "Coating not qualified" });
        Assert.Single((await Get(planner, "/api/reports/alternates?status=Expired")).GetProperty("rows").EnumerateArray());
        Assert.Single((await Get(planner, "/api/reports/alternates?status=Rejected")).GetProperty("rows").EnumerateArray());

        await using var db = new NpgsqlConnection(app.ConnectionString);
        await db.OpenAsync();
        foreach (var sql in new[]
        {
            "DELETE FROM engineering.alternate",
            $"UPDATE engineering.alternate SET factor = 2 WHERE alternate_id = {x}",
            $"UPDATE engineering.alternate SET status = 'Approved' WHERE alternate_id = {eq}",
            "UPDATE engineering.bom_line_policy SET policy = 'Never'",
        })
        {
            await using var tamper = new NpgsqlCommand(sql, db);
            await Assert.ThrowsAsync<PostgresException>(() => tamper.ExecuteNonQueryAsync());
        }
    }

    [Fact]
    public async Task Scenario_9_a_program_condition_is_approved_for_one_program_and_requires_approval_for_another()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("pm", "ProgramManager");
        await plant.Person("bob", "Buyer");
        var (pm, bob, approver) = (plant.As("pm"), plant.As("bob"), plant.Approver);
        var a = Guid.Parse((await plant.Ok(pm, "/api/programs/", new { code = "ALPHA", name = "Program Alpha" })).GetProperty("entityId").GetString()!);
        var b = Guid.Parse((await plant.Ok(pm, "/api/programs/", new { code = "BRAVO", name = "Program Bravo" })).GetProperty("entityId").GetString()!);
        foreach (var who in new[] { "pm", "bob", "approver", "eng" })
        {
            foreach (var program in new[] { a, b })
            {
                await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People[who]}/programs", new { programId = program, reason = "Program team" });
            }
        }

        await BoughtAsync(plant, "CHIP");
        await BoughtAsync(plant, "CHIP-2");
        await Rejected(plant, bob, "/api/engineering/alternates", new { primaryPart = "CHIP", alternatePart = "CHIP-2", kind = "ProgramSpecific" }, "program_required");
        var alt = Id(await plant.Ok(bob, "/api/engineering/alternates", new { primaryPart = "CHIP", alternatePart = "CHIP-2", kind = "ProgramSpecific", programId = a, evidence = "Alpha customer concession C-9" }));
        await plant.Ok(approver, $"/api/engineering/alternates/{alt}/approve", new { reason = "Concession C-9" });

        // For Alpha the alternate is approved; for Bravo, or with no program, it requires approval — never mixed.
        var forA = await Get(bob, $"/api/engineering/alternates/candidates/CHIP?program={a}");
        Assert.Equal("CHIP-2", Assert.Single(forA.GetProperty("approved").EnumerateArray()).GetProperty("offeredPart").GetString());
        Assert.Empty(forA.GetProperty("requiresApproval").EnumerateArray());
        var forB = await Get(bob, $"/api/engineering/alternates/candidates/CHIP?program={b}");
        Assert.Empty(forB.GetProperty("approved").EnumerateArray());
        Assert.Contains("another program", Assert.Single(forB.GetProperty("requiresApproval").EnumerateArray()).GetProperty("reasons")[0].GetString(), StringComparison.Ordinal);
        Assert.Single((await Get(bob, "/api/engineering/alternates/candidates/CHIP")).GetProperty("requiresApproval").EnumerateArray());

        // Program segregation: someone without Alpha access does not see the relationship at all.
        Assert.Empty((await Get(planner, "/api/engineering/alternates?part=CHIP")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await planner.GetAsync($"/api/engineering/alternates/{alt}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await planner.GetAsync($"/api/engineering/alternates/candidates/CHIP?program={a}")).StatusCode);
        Assert.Single((await Get(bob, "/api/engineering/alternates?part=CHIP")).EnumerateArray());
    }
}
