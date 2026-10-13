using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace Forge.Integration.Tests;

/// <summary>
/// V3.10 through the real API: holds of every kind with a scope, source, evidence, affected records and required
/// resolution; each kind blocks its configured downstream activities and has its own release authority; the stock,
/// NCR and inspection holds other contexts keep appear in the same register.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class V310Tests(PostgresFixture postgres)
{
    private static string Day(int fromToday) => DateTime.UtcNow.AddDays(fromToday).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    private static async Task Rejected(SlittingPlant.Plant plant, HttpClient client, string url, object body, string code, HttpStatusCode status = HttpStatusCode.UnprocessableEntity)
    {
        var response = await plant.Try(client, url, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"{url} -> {(int)response.StatusCode}: {text}");
        Assert.Contains(code, text, StringComparison.Ordinal);
    }

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    [Fact]
    public async Task Legacy_production_holds_keep_their_ids_release_history_and_identity_sequence_when_migrated()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var order = Id(await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = "ROLL-100", quantity = 10m, dueDate = Day(10), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Customer order" });
        var assembly = typeof(Forge.Execution.Infrastructure.ExecutionRegistration).Assembly;
        async Task<string> Migration(string suffix)
        {
            var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith(suffix, StringComparison.Ordinal));
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync();
        }

        await using var db = new NpgsqlConnection(app.ConnectionString);
        await db.OpenAsync();
        // Recreate the retained V3.9 schema and exercise the actual upgrade SQL with existing data.
        await using (var legacy = new NpgsqlCommand(await Migration("0007_production_holds.sql"), db))
        {
            await legacy.ExecuteNonQueryAsync();
        }

        await using (var seed = new NpgsqlCommand("""
            INSERT INTO execution.production_hold
                (hold_id, scope, order_number, sequence, part_number, work_center_code, machine_code, reason, placed_at, placed_by, released_at, released_by, release_reason)
            OVERRIDING SYSTEM VALUE VALUES
                (901, 'Operation', $1, 10, NULL, NULL, NULL, 'Legacy active', '2026-10-01T12:00:00Z', 'legacy-engineer', NULL, NULL, NULL),
                (902, 'WorkCenter', NULL, NULL, NULL, 'SLIT-WC', NULL, 'Legacy released', '2026-10-01T12:00:00Z', 'legacy-engineer', '2026-10-02T12:00:00Z', 'legacy-planner', 'Guard repaired')
            """, db))
        {
            seed.Parameters.AddWithValue(order);
            await seed.ExecuteNonQueryAsync();
        }

        await using (var upgrade = new NpgsqlCommand(await Migration("0008_holds_move_to_kernel.sql"), db))
        {
            await upgrade.ExecuteNonQueryAsync();
        }

        var register = (await Get(planner, "/api/holds?includeReleased=true")).EnumerateArray().Where(h => h.GetProperty("holdId").ValueKind == JsonValueKind.Number).ToDictionary(h => h.GetProperty("holdId").GetInt64());
        Assert.Equal(("Production", true, $"operation {order}/10"), (register[901].GetProperty("kind").GetString(), register[901].GetProperty("active").GetBoolean(), register[901].GetProperty("target").GetString()));
        Assert.Equal((false, "legacy-planner", "Guard repaired"), (register[902].GetProperty("active").GetBoolean(), register[902].GetProperty("releasedBy").GetString(), register[902].GetProperty("releaseReason").GetString()));
        Assert.Equal(DateTimeOffset.Parse("2026-10-02T12:00:00Z", CultureInfo.InvariantCulture), register[902].GetProperty("releasedAt").GetDateTimeOffset());
        var next = Id(await plant.Ok(plant.Engineer, "/api/holds", new { kind = "Safety", targetType = "Machine", targetId = "SLITTER-B", reason = "New safety review" }));
        Assert.True(next > 902);
        await using var dropped = new NpgsqlCommand("SELECT to_regclass('execution.production_hold') IS NULL", db);
        Assert.True((bool)(await dropped.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Program_holds_are_hidden_and_cannot_be_released_without_program_access()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("pm", "ProgramManager");
        await plant.Person("outsider", "Planner");
        await plant.Person("hold-auditor", "Auditor", "ProgramManager");
        var program = Guid.Parse((await plant.Ok(plant.As("pm"), "/api/programs/", new { code = "PRIVATE", name = "Private program", customer = "Customer" })).GetProperty("entityId").GetString()!);
        await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["planner"]}/programs", new { programId = program, reason = "Program team" });
        await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["hold-auditor"]}/programs", new { programId = program, reason = "Independent current-source audit review" });
        var order = Id(await plant.Ok(planner, "/api/orders/", new { partNumber = "ROLL-100", quantity = 10m, dueDate = Day(10), priority = 3, programId = program }));
        var hold = Id(await plant.Ok(planner, "/api/holds", new { kind = "Production", targetType = "Order", targetId = order.ToString(CultureInfo.InvariantCulture), reason = "Private production review" }));
        var outsider = plant.As("outsider");
        var auditor = plant.As("hold-auditor");
        var url = $"/api/holds/{hold}/release";

        Assert.Empty((await Get(outsider, "/api/holds")).EnumerateArray());
        await Rejected(plant, outsider, $"/api/holds/{hold}/release", new { reason = "Attempt outside program" }, "hold_not_found", HttpStatusCode.NotFound);
        await Rejected(plant, outsider, $"/api/holds/{long.MaxValue}/release", new { reason = "Absent record has the same concealed result" }, "hold_not_found", HttpStatusCode.NotFound);
        Assert.Contains((await Get(planner, "/api/holds")).EnumerateArray(), h => h.GetProperty("holdId").GetInt64() == hold && h.GetProperty("active").GetBoolean());
        var release = new { reason = "Program review complete" };
        const string releaseKey = "private-hold-release-once";
        var completed = await planner.CommandAsync(url, release, releaseKey);
        var completedBody = await completed.Content.ReadAsStringAsync();
        Assert.True(completed.IsSuccessStatusCode, completedBody);
        var repeated = await planner.CommandAsync(url, release, releaseKey);
        Assert.True(repeated.IsSuccessStatusCode, await repeated.Content.ReadAsStringAsync());
        Assert.Equal(completedBody, await repeated.Content.ReadAsStringAsync());
        await Rejected(plant, outsider, $"/api/holds/{hold}/release", new { reason = "Retry outside program" }, "hold_not_found", HttpStatusCode.NotFound);
        var released = Assert.Single((await Get(auditor, "/api/holds?includeReleased=true")).EnumerateArray(), h =>
            h.GetProperty("holdId").ValueKind == JsonValueKind.Number && h.GetProperty("holdId").GetInt64() == hold);
        Assert.False(released.GetProperty("active").GetBoolean());
        var releasedBytes = released.GetRawText();
        await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["planner"]}/programs/{program}/revoke", new { reason = "Withdraw membership after the successful source release" });
        var revokedReplay = await planner.CommandAsync(url, release, releaseKey);
        var revokedBody = await revokedReplay.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.NotFound, revokedReplay.StatusCode);
        Assert.Equal("hold_not_found", JsonDocument.Parse(revokedBody).RootElement.GetProperty("code").GetString());
        Assert.Equal(releasedBytes, Assert.Single((await Get(auditor, "/api/holds?includeReleased=true")).EnumerateArray(), h =>
            h.GetProperty("holdId").ValueKind == JsonValueKind.Number && h.GetProperty("holdId").GetInt64() == hold).GetRawText());
        var audit = (await Get(auditor, $"/api/kernel/audit?entityType=Hold&entityId={hold}")).EnumerateArray()
            .Select(row => row.GetProperty("entry")).Where(entry => entry.GetProperty("action").GetString() == "ReleaseHold").ToArray();
        Assert.Single(audit, entry => entry.GetProperty("outcome").GetString() == "Succeeded");
        var denials = audit.Where(entry => entry.GetProperty("outcome").GetString() == "Denied").ToArray();
        Assert.Equal(3, denials.Length);
        Assert.All(denials, entry => Assert.Equal(program, entry.GetProperty("programId").GetProperty("value").GetGuid()));
        Assert.Contains(denials, entry => entry.GetProperty("actor").GetString() == "person:" + plant.People["planner"]);
        Assert.True((await Get(plant.Admin, "/api/kernel/audit/verify")).GetProperty("intact").GetBoolean());
    }

    [Fact]
    public async Task Inspection_holds_appear_only_after_work_reaches_the_inspection_and_clear_when_it_passes()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("qe", "QualityEngineer");
        await plant.Person("insp", "Inspector");
        await plant.Person("kiosk", "ShopFloorTerminal");
        var e = plant.Engineer;
        var kiosk = plant.As("kiosk");
        await plant.Ok(plant.As("qe"), "/api/quality/plans", new { partNumber = "ROLL-100", sequence = 10, title = "Slit edge", characteristics = new[] { new { name = "No burrs", kind = "Attribute" } } });
        await plant.Ok(e, "/api/resources/shifts", new { code = "ALL", siteCode = "PLANT1", name = "All day", start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 } });
        await plant.Ok(e, "/api/resources/work-centers/SLIT-WC/shifts", new { shiftCode = "ALL" });
        await plant.Ok(e, $"/api/workforce/workers/{plant.People["sarah"]}/shift", new { shiftCode = "ALL" });
        await plant.Ok(plant.Admin, $"/api/workforce/people/{plant.People["sarah"]}/badge", new { badge = "V310-SARAH" });
        var order = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "ROLL-100", quantity = 10m, dueDate = Day(10), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Customer order" });
        var reference = $"Inspection {order}/10";
        Assert.DoesNotContain((await Get(planner, "/api/holds")).EnumerateArray(), h => h.GetProperty("reference").GetString() == reference);
        await plant.Ok(kiosk, "/api/shopfloor/clock-in", new { badge = "V310-SARAH" });
        await plant.Ok(kiosk, "/api/shopfloor/start", new { badge = "V310-SARAH", orderNumber = order, sequence = 10, machineCode = "SLITTER-A" });
        await plant.Ok(kiosk, "/api/shopfloor/report", new { badge = "V310-SARAH", orderNumber = order, sequence = 10, good = 10m, scrap = 0m, complete = true });
        Assert.Contains((await Get(planner, "/api/holds")).EnumerateArray(), h => h.GetProperty("reference").GetString() == reference && h.GetProperty("releasedThrough").GetString() == "Passing inspection");
        await plant.Ok(plant.As("insp"), "/api/quality/inspections", new { orderNumber = order, sequence = 10, quantity = 10m, findings = new[] { new { number = 1, accepted = true } } });
        Assert.DoesNotContain((await Get(planner, "/api/holds")).EnumerateArray(), h => h.GetProperty("reference").GetString() == reference);
    }

    [Fact]
    public async Task Each_kind_blocks_its_activities_and_is_released_only_with_its_authority()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("qe", "QualityEngineer");
        await plant.Person("bob", "Buyer");
        await plant.Person("pat", "Buyer", "PurchasingManager");
        var (e, qe, stores, bob, pat, admin) = (plant.Engineer, plant.As("qe"), plant.As("stores"), plant.As("bob"), plant.As("pat"), plant.Admin);

        // A quality hold on the coil, with evidence and the records it affects, stops issuing it.
        var order = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "ROLL-100", quantity = 10m, dueDate = Day(10), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Customer order" });
        await Rejected(plant, qe, "/api/holds", new { kind = "Quality", targetType = "Part", targetId = "NOPE", reason = "x" }, "hold_target_not_found", HttpStatusCode.NotFound);
        var lotHold = Id(await plant.Ok(qe, "/api/holds", new
        {
            kind = "Quality", targetType = "Part", targetId = "coil-al", source = "Supplier notice SN-12",
            evidence = "Mill certificate shows tensile below spec", affectedRecords = new[] { "part:COIL-AL", $"order:{order}" }, reason = "Suspect tensile strength",
        }));
        await Rejected(plant, stores, "/api/inventory/issues", new { orderNumber = order, partNumber = "COIL-AL", quantity = 26m, locationCode = "STORES" }, "Quality hold");

        // Release authority: stores cannot release a quality hold; the quality engineer who placed it holds quality
        // disposition and can.
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(stores, $"/api/holds/{lotHold}/release", new { reason = "x" })).StatusCode);
        await plant.Ok(qe, $"/api/holds/{lotHold}/release", new { reason = "Retest passed, certificate LAB-90" });
        await plant.Ok(stores, "/api/inventory/issues", new { orderNumber = order, partNumber = "COIL-AL", quantity = 26m, locationCode = "STORES" });

        // An engineering hold on a part stops releasing its orders, not issuing to them.
        var next = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "ROLL-100", quantity = 5m, dueDate = Day(12), priority = 3 }));
        var engHold = Id(await plant.Ok(e, "/api/holds", new { kind = "Engineering", targetType = "Part", targetId = "ROLL-100", reason = "Drawing under change ECN-9" }));
        await Rejected(plant, planner, $"/api/orders/{next}/release", new { reason = "Customer order" }, "Engineering hold");
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(e, $"/api/holds/{engHold}/release", new { reason = "x" })).StatusCode);
        await plant.Ok(plant.Approver, $"/api/holds/{engHold}/release", new { reason = "ECN-9 released" });
        await plant.Ok(planner, $"/api/orders/{next}/release", new { reason = "Customer order" });

        // A procurement hold on a supplier stops approving orders to it.
        await plant.Ok(bob, "/api/procurement/suppliers", new { code = "ALCOA", name = "Alcoa" });
        var po = Id(await plant.Ok(bob, "/api/procurement/purchase-orders", new { supplier = "ALCOA", lines = new[] { new { partNumber = "COIL-AL", quantity = 50m, promisedDate = Day(7) } } }));
        var supplierHold = Id(await plant.Ok(bob, "/api/holds", new { kind = "Procurement", targetType = "Supplier", targetId = "ALCOA", reason = "Late certificates three times this quarter" }));
        await Rejected(plant, pat, $"/api/procurement/purchase-orders/{po}/approve", new { reason = "Budget approved" }, "Procurement hold");
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(bob, $"/api/holds/{supplierHold}/release", new { reason = "x" })).StatusCode);
        await plant.Ok(pat, $"/api/holds/{supplierHold}/release", new { reason = "Corrective action accepted" });
        await plant.Ok(pat, $"/api/procurement/purchase-orders/{po}/approve", new { reason = "Budget approved" });

        // What a kind blocks is configured: production holds can also stop issuing material.
        var production = Id(await plant.Ok(e, "/api/holds", new { kind = "Production", targetType = "Part", targetId = "COIL-AL", reason = "Coil handling trial" }));
        await plant.Ok(stores, "/api/inventory/issues", new { orderNumber = next, partNumber = "COIL-AL", quantity = 1m, locationCode = "STORES" });
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(e, "/api/holds/kinds/Production", new { blocks = new[] { "StartWork", "ReportOutput", "IssueMaterial" }, reason = "x" })).StatusCode);
        await Rejected(plant, admin, "/api/holds/kinds/Production", new { blocks = new[] { "StartWork", "ReportOutput", "IssueMaterial" } }, "reason_required");
        await plant.Ok(admin, "/api/holds/kinds/Production", new { blocks = new[] { "StartWork", "ReportOutput", "IssueMaterial" }, reason = "Trials stop material too" });
        await Rejected(plant, stores, "/api/inventory/issues", new { orderNumber = next, partNumber = "COIL-AL", quantity = 1m, locationCode = "STORES" }, "Production hold");
        var kinds = (await Get(planner, "/api/holds/kinds")).EnumerateArray().ToDictionary(k => k.GetProperty("kind").GetString()!);
        Assert.Contains("IssueMaterial", kinds["Production"].GetProperty("blocks").EnumerateArray().Select(b => b.GetString()));
        Assert.Equal("procurement.approve", kinds["Procurement"].GetProperty("releasePermission").GetString());

        // The register shows every hold, released ones too, and the stock other contexts hold.
        await plant.Ok(stores, "/api/inventory/holds", new { partNumber = "COIL-AL", quantity = 10m, locationCode = "STORES", reason = "Damaged wrap" });
        var register = (await Get(planner, "/api/holds?includeReleased=true")).EnumerateArray().ToList();
        var lot = register.Single(h => h.GetProperty("reference").GetString() == lotHold.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(("Quality", "Supplier notice SN-12", false), (lot.GetProperty("kind").GetString(), lot.GetProperty("source").GetString(), lot.GetProperty("active").GetBoolean()));
        Assert.Equal(2, lot.GetProperty("affectedRecords").GetArrayLength());
        Assert.Contains(register, h => h.GetProperty("releasedThrough").GetString() == "Quality release of stock" && h.GetProperty("target").GetString()!.Contains("COIL-AL at STORES", StringComparison.Ordinal));
        Assert.Contains(register, h => h.GetProperty("reference").GetString() == production.ToString(CultureInfo.InvariantCulture) && h.GetProperty("active").GetBoolean());

        var report = await Get(planner, "/api/reports/holds?status=Procurement");
        Assert.Equal("No", Assert.Single(report.GetProperty("rows").EnumerateArray()).GetProperty("cells")[9].GetString());
        var answer = await (await planner.PostAsJsonAsync(new Uri("/api/analyst/ask", UriKind.Relative), new { question = "Which quality holds are open?" })).Content.ReadFromJsonAsync<JsonElement>();
        var request = answer.GetProperty("interpretation").GetProperty("report");
        Assert.Equal(("holds", "Quality"), (request.GetProperty("reportId").GetString(), request.GetProperty("parameters").GetProperty("status").GetString()));
    }
}
