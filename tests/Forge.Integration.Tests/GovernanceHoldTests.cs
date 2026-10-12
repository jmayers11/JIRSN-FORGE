using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Npgsql;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class GovernanceHoldTests(PostgresFixture postgres)
{
    private const string Meaning = "I reviewed the quality evidence and authorize this hold release.";
    private static long Id(JsonElement value) => long.Parse(value.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);
    private static string Day(int days) => DateTime.UtcNow.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Configured_quality_hold_requires_trusted_fresh_step_up_authority_and_an_explicit_version_bound_signature()
    {
        var cs = await postgres.CreateDatabaseAsync();
        await using var app = new ForgeApp(cs);
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("hold-quality", "QualityEngineer");
        var quality = plant.As("hold-quality");
        await plant.Ok(plant.Admin, "/api/holds/kinds/Quality", new
        {
            blocks = new[] { "IssueMaterial", "StartWork", "Ship" }, requireFreshAssurance = true,
            requireSignature = true, signatureMeaning = Meaning, reason = "Require high-assurance quality release",
        });
        var hold = Id(await plant.Ok(quality, "/api/holds", new { kind = "Quality", targetType = "Part", targetId = "COIL-AL", evidence = "LAB-PASS-42", reason = "Review material certificate" }));
        var path = $"/api/holds/{hold}/release";
        await Rejected(quality, path, new { reason = "Try implicit approval" }, "no-signature", HttpStatusCode.UnprocessableEntity, "stale_hold_version");
        await Rejected(quality, path, new { expectedVersion = 1, reason = "No explicit meaning" }, "no-meaning", HttpStatusCode.UnprocessableEntity, "signature_required");
        await Rejected(plant.As("stores"), path, new { expectedVersion = 1, signatureConfirmed = true, signatureMeaning = Meaning, reason = "Wrong authority" }, "wrong-authority", HttpStatusCode.Forbidden);
        var release = new { expectedVersion = 1, signatureConfirmed = true, signatureMeaning = Meaning, reason = "Certificate reviewed and accepted" };

        await using (var stale = new ForgeApp(cs, configureBuilder: b => b.UseSetting("Forge:Authentication:DevelopmentAuthTime", "1")))
            await Rejected(stale.As("hold-quality"), path, new
            {
                expectedVersion = 1, signatureConfirmed = true, signatureMeaning = Meaning, reason = "Client claims cannot replace provider proof",
                assurance = "urn:forge:assurance:high", authTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            }, "stale-provider", HttpStatusCode.Unauthorized, "step_up_required");
        await using (var weak = new ForgeApp(cs, configureBuilder: b => b.UseSetting("Forge:Authentication:DevelopmentAssurance", "urn:forge:assurance:low")))
            await Rejected(weak.As("hold-quality"), path, release, "weak-provider", HttpStatusCode.Unauthorized, "step_up_required");

        var released = await quality.CommandAsync(path, release, "explicit-release");
        Assert.True(released.IsSuccessStatusCode, await released.Content.ReadAsStringAsync());
        var retried = await quality.CommandAsync(path, release, "explicit-release");
        Assert.Equal(await released.Content.ReadAsStringAsync(), await retried.Content.ReadAsStringAsync());
        var register = await quality.GetFromJsonAsync<JsonElement[]>("/api/holds?includeReleased=true");
        var row = register!.Single(h => h.GetProperty("holdId").GetInt64() == hold);
        Assert.False(row.GetProperty("active").GetBoolean());
        Assert.Equal(2, row.GetProperty("recordVersion").GetInt32());
        var signoff = row.GetProperty("releaseSignature");
        Assert.Equal("hold-quality", signoff.GetProperty("subject").GetString());
        Assert.Equal("urn:forge:development", signoff.GetProperty("issuer").GetString());
        Assert.Equal(plant.People["hold-quality"], signoff.GetProperty("personId").GetGuid());
        Assert.Contains(signoff.GetProperty("signerRoles").EnumerateArray(), r => r.GetString() == "QualityEngineer");
        Assert.Equal("quality.disposition", signoff.GetProperty("authorityPermission").GetString());
        Assert.Equal("urn:forge:assurance:high", signoff.GetProperty("assurance").GetString());
        Assert.Equal(1, signoff.GetProperty("recordVersion").GetInt32());
        Assert.Equal(64, signoff.GetProperty("recordFingerprint").GetString()!.Length);
        Assert.Equal(64, signoff.GetProperty("policyFingerprint").GetString()!.Length);
        Assert.Equal("ReleaseHold", signoff.GetProperty("action").GetString());
        Assert.Equal(Meaning, signoff.GetProperty("meaning").GetString());
        Assert.Equal("Released", signoff.GetProperty("result").GetString());
        Assert.True(signoff.GetProperty("authenticatedAt").GetDateTimeOffset() <= signoff.GetProperty("signedAt").GetDateTimeOffset());
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync();
        await using (var count = new NpgsqlCommand("SELECT count(*) FROM kernel.hold_release_signoff WHERE hold_id=$1", db))
        {
            count.Parameters.AddWithValue(hold);
            Assert.Equal(1L, await count.ExecuteScalarAsync());
        }
        await using (var audit = new NpgsqlCommand("SELECT count(*) FROM kernel.audit_log WHERE action='ReleaseHold' AND outcome='Succeeded' AND entity_id=$1", db))
        {
            audit.Parameters.AddWithValue(hold.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(1L, await audit.ExecuteScalarAsync());
        }
        await using var tamper = new NpgsqlCommand("UPDATE kernel.hold_release_signoff SET evidence='changed' WHERE hold_id=$1", db);
        tamper.Parameters.AddWithValue(hold);
        await Assert.ThrowsAsync<PostgresException>(async () => await tamper.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Lot_hold_computes_actual_descendant_allocation_operation_order_shipment_and_goal_impact_and_blocks_only_configured_dependencies()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var stores = plant.As("stores");
        await plant.Person("propagation-quality", "QualityEngineer");
        await plant.Person("propagation-shipper", "Shipper");
        foreach (var (part, makeBuy) in new[] { ("HOLD-RAW", "Buy"), ("HOLD-OUT", "Make") })
        {
            await plant.Ok(plant.Engineer, "/api/engineering/items", new { partNumber = part, description = part, unitOfMeasure = "M", makeBuy, leadTimeDays = 1, lotTracked = true });
            await plant.Ok(plant.Engineer, $"/api/engineering/items/{part}/revisions", new { revision = "A" });
        }
        await plant.Ok(plant.Engineer, "/api/engineering/items/HOLD-OUT/revisions/A/bom", new { lineNumber = 1, componentPartNumber = "HOLD-RAW", quantityPer = 1m, scrapPercent = 0m });
        await plant.Ok(plant.Engineer, "/api/engineering/items/HOLD-OUT/revisions/A/routing", new { sequence = 10, description = "Slit material", workCenterCode = "SLIT-WC", activityCode = "RUN_SLITTER", eligibleMachines = new[] { "SLITTER-A" }, setupMinutes = 0m, runMinutesPerUnit = 0.01m, crewSize = 1 });
        foreach (var part in new[] { "HOLD-RAW", "HOLD-OUT" }) await plant.Ok(plant.Approver, $"/api/engineering/items/{part}/revisions/A/release", new { reason = "Release hold-propagation fixture" });
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "HOLD-RAW", quantity = 100m, locationCode = "STORES", lotNumber = "ROOT-HOLD", reason = "Traceable opening material" });
        await plant.Ok(stores, "/api/inventory/splits", new { partNumber = "HOLD-RAW", lotNumber = "ROOT-HOLD", locationCode = "STORES", status = "Released", results = new[] { new { lotNumber = "CHILD-HOLD", quantity = 80m }, new { lotNumber = "SIBLING-HOLD", quantity = 20m } }, reason = "Split master roll" });
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "HOLD-RAW", quantity = 20m, locationCode = "STORES", lotNumber = "UNRELATED-HOLD", reason = "Independent traceable material" });
        var order = Id(await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = "HOLD-OUT", quantity = 40m, dueDate = Day(12), priority = 5 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Hold impact target" });
        await plant.Ok(stores, "/api/inventory/issues", new { orderNumber = order, partNumber = "HOLD-RAW", quantity = 10m, locationCode = "STORES", lotNumber = "CHILD-HOLD" });
        await plant.Ok(stores, "/api/inventory/locations", new { code = "HOLD-STAGE", siteCode = "PLANT1", name = "Hold material staging", kind = "Staging", workCenterCode = "SLIT-WC" });
        var task = Id(await plant.Ok(stores, "/api/inventory/tasks", new { kind = "Stage", orderNumber = order, partNumber = "HOLD-RAW", quantity = 5m, fromLocation = "STORES", toLocation = "HOLD-STAGE", lotNumber = "CHILD-HOLD" }));
        var demand = Id(await plant.Ok(planner, "/api/planning/demand", new { partNumber = "HOLD-RAW", quantity = 2m, needDate = Day(15), kind = "CustomerOrder", customer = "Hold impact customer" }));
        var shipment = Id(await plant.Ok(stores, "/api/inventory/shipments", new { demandId = demand, customer = "Hold impact customer", lines = new[] { new { partNumber = "HOLD-RAW", quantity = 2m, locationCode = "STORES", lotNumber = "CHILD-HOLD" } }, reason = "Stage a downstream shipment" }));
        await plant.Ok(stores, $"/api/inventory/shipments/{shipment}/pack", new { reason = "Pack before quality review" });
        var productionGoal = Id(await plant.Ok(planner, "/api/planning/goals", new { kind = "Production", title = "Downstream build", orderNumber = order, requiredBy = Day(12) }));
        var deliveryGoal = Id(await plant.Ok(planner, "/api/planning/goals", new { kind = "Delivery", title = "Downstream delivery", demandId = demand, requiredBy = Day(15) }));
        await plant.Ok(plant.Admin, "/api/holds/kinds/Quality", new { blocks = new[] { "IssueMaterial", "StartWork", "ReportOutput", "Ship" }, propagateDownstream = true, reason = "Propagate factual material dependencies" });
        var hold = Id(await plant.Ok(plant.As("propagation-quality"), "/api/holds", new { kind = "Quality", targetType = "Lot", targetPart = "HOLD-RAW", targetId = "ROOT-HOLD", affectedRecords = new[] { "order:999999" }, reason = "Supplier recall on master lot" }));
        var register = await planner.GetFromJsonAsync<JsonElement[]>("/api/holds");
        var impact = register!.Single(h => h.GetProperty("holdId").GetInt64() == hold).GetProperty("downstreamImpact").EnumerateArray().ToArray();
        Assert.Contains(impact, i => i.GetProperty("recordType").GetString() == "Lot" && i.GetProperty("recordId").GetString() == "lot:HOLD-RAW/CHILD-HOLD");
        Assert.Contains(impact, i => i.GetProperty("recordType").GetString() == "Allocation" && i.GetProperty("recordId").GetString() == $"material-task:{task}" && i.GetProperty("quantity").GetDecimal() == 5m);
        foreach (var reference in new[] { $"operation:{order}/10", $"order:{order}", $"shipment:{shipment}", $"goal:{productionGoal}", $"goal:{deliveryGoal}" }) Assert.Contains(impact, i => i.GetProperty("recordId").GetString() == reference);
        Assert.DoesNotContain(impact, i => i.GetProperty("recordId").GetString() == "order:999999" || i.GetProperty("recordId").GetString() == "lot:HOLD-RAW/UNRELATED-HOLD");
        Assert.All(impact, i => Assert.False(string.IsNullOrWhiteSpace(i.GetProperty("evidence").GetString())));
        var readiness = await planner.GetFromJsonAsync<JsonElement[]>("/api/readiness/operations");
        var blockedOperation = readiness!.Single(o => o.GetProperty("orderNumber").GetInt64() == order && o.GetProperty("sequence").GetInt32() == 10);
        Assert.False(blockedOperation.GetProperty("executable").GetBoolean());
        Assert.Equal("Holds", blockedOperation.GetProperty("primaryConstraint").GetString());
        var report = await planner.GetFromJsonAsync<JsonElement>("/api/reports/holds?status=Quality");
        var reportRow = report.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("cells")[0].GetString() == hold.ToString(CultureInfo.InvariantCulture));
        Assert.Contains($"goal:{productionGoal}", reportRow.GetProperty("cells")[10].GetString(), StringComparison.Ordinal);
        await Rejected(stores, "/api/inventory/issues", new { orderNumber = order, partNumber = "HOLD-RAW", quantity = 1m, locationCode = "STORES", lotNumber = "CHILD-HOLD" }, "blocked-descendant-issue", HttpStatusCode.UnprocessableEntity, "on_hold");
        await Rejected(plant.As("propagation-shipper"), $"/api/inventory/shipments/{shipment}/ship", new { carrier = "Carrier" }, "blocked-descendant-shipment", HttpStatusCode.UnprocessableEntity, "on_hold");
        var independentOrder = Id(await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = "HOLD-OUT", quantity = 1m, dueDate = Day(12), priority = 5 }));
        await plant.Ok(planner, $"/api/orders/{independentOrder}/release", new { reason = "Independent order remains allowed" });
        await plant.Ok(stores, "/api/inventory/issues", new { orderNumber = independentOrder, partNumber = "HOLD-RAW", quantity = 1m, locationCode = "STORES", lotNumber = "UNRELATED-HOLD" });
        await plant.Ok(plant.As("propagation-quality"), $"/api/holds/{hold}/release", new { reason = "Supplier recall cleared with evidence" });
        await plant.Ok(stores, "/api/inventory/issues", new { orderNumber = order, partNumber = "HOLD-RAW", quantity = 1m, locationCode = "STORES", lotNumber = "CHILD-HOLD" });
    }

    private static async Task Rejected(HttpClient client, string path, object body, string key, HttpStatusCode status, string? code = null)
    {
        var response = await client.CommandAsync(path, body, key);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"{path}: {(int)response.StatusCode} {text}");
        if (code is not null) Assert.Contains(code, text, StringComparison.Ordinal);
    }
}
