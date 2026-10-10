using System.Globalization;
using System.Net;
using System.Text.Json;
using Npgsql;

namespace Forge.Integration.Tests;

/// <summary>
/// V2.8 through the real API: assets with an immutable lifecycle history; components installed and removed
/// by work order; a restricted production asset takes its machine out of capacity; decommissioning needs a
/// second person; obsolescence impact states facts and never invents a replacement (scenario 7); an MRO item
/// at its reorder point gets a planned purchase up to its maximum.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class V28Tests(PostgresFixture postgres)
{
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

    private static async Task BoughtAsync(SlittingPlant.Plant plant, string part, int leadTimeDays = 10)
    {
        await plant.Ok(plant.Engineer, "/api/engineering/items", new { partNumber = part, description = part, unitOfMeasure = "EA", makeBuy = "Buy", leadTimeDays });
        await plant.Ok(plant.Engineer, $"/api/engineering/items/{part}/revisions", new { revision = "A" });
        await plant.Ok(plant.Approver, $"/api/engineering/items/{part}/revisions/A/release", new { reason = "Baseline" });
    }

    private static async Task<JsonElement> Downtime(HttpClient client)
    {
        var now = DateTimeOffset.UtcNow;
        return await Get(client, $"/api/maintenance/downtime?from={Uri.EscapeDataString(now.AddDays(-1).ToString("o"))}&to={Uri.EscapeDataString(now.AddDays(1).ToString("o"))}");
    }

    [Fact]
    public async Task Scenario_7_assets_components_and_obsolescence_from_records_only()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("mp", "MaintenancePlanner");
        await plant.Person("mp2", "MaintenancePlanner");
        await plant.Person("tech", "MaintenanceTechnician");
        await plant.Person("lead", "Buyer", "EngineeringApprover");
        var (mp, mp2, tech, lead, eng, approver) = (plant.As("mp"), plant.As("mp2"), plant.As("tech"), plant.As("lead"), plant.Engineer, plant.Approver);
        await BoughtAsync(plant, "SC-440");
        await BoughtAsync(plant, "SC-450");

        // An asset for machine SLITTER-B; one asset per machine, machines only on production assets.
        var assets = "/api/maintenance/assets";
        object Asset(string code, string type = "Production", string? machine = "SLITTER-B") => new { assetCode = code, name = "Slitter 2", type, machineCode = machine, criticality = "Critical", manufacturer = "Atlas", model = "S-1600", expectedLifeYears = 25, replacementCost = 850000m };
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, tech, assets, Asset("SL-02"))).Status);
        await Rejected(plant, mp, assets, Asset("SL-02", machine: "NOPE"), "unknown_machine");
        await Rejected(plant, mp, assets, Asset("BLDG-1", "Building"), "machine_needs_production");
        await plant.Ok(mp, assets, Asset("SL-02"));
        await Rejected(plant, mp, assets, Asset("SL-02B"), "machine_has_asset");
        await Rejected(plant, mp, assets, Asset("SL-02", machine: null), "asset_exists");
        var detail = await Get(tech, $"{assets}/SL-02");
        Assert.Equal(("Planned", "SLIT-WC"), (detail.GetProperty("asset").GetProperty("status").GetString(), detail.GetProperty("asset").GetProperty("workCenterCode").GetString()));

        // As delivered: two controllers, recorded while the asset is still Planned.
        await plant.Ok(mp, $"{assets}/SL-02/components", new { partNumber = "SC-440", revision = "A", position = "Cabinet slot 1", quantity = 1m });
        await plant.Ok(mp, $"{assets}/SL-02/components", new { partNumber = "SC-440", position = "Cabinet slot 2", quantity = 1m });
        await Rejected(plant, mp, $"{assets}/SL-02/components", new { partNumber = "SC-440", revision = "Z", position = "x", quantity = 1m }, "revision_not_released");
        await Rejected(plant, mp, $"{assets}/SL-02/status", new { status = "Decommissioned" }, "reason_required");
        await Rejected(plant, mp, $"{assets}/SL-02/status", new { status = "Decommissioned", reason = "x" }, "invalid_transition");
        await plant.Ok(mp, $"{assets}/SL-02/status", new { status = "Active", reason = "Site acceptance test passed" });

        // In service, components move only by an open work order on the asset's machine.
        await Rejected(plant, mp, $"{assets}/SL-02/components", new { partNumber = "SC-440", position = "Spare slot", quantity = 1m }, "work_order_required");
        await plant.Ok(eng, "/api/maintenance/machines/SLITTER-B/down", new { description = "Controller fault in slot 2" });
        await plant.Ok(eng, "/api/maintenance/machines/SLITTER-A/down", new { description = "Unrelated jam" });
        var orders = (await Get(mp, "/api/maintenance/work-orders")).EnumerateArray().Select(v => v.GetProperty("order")).ToList();
        var repair = orders.Single(o => o.GetProperty("machineCode").GetString() == "SLITTER-B").GetProperty("workOrderId").GetInt64();
        var other = orders.Single(o => o.GetProperty("machineCode").GetString() == "SLITTER-A").GetProperty("workOrderId").GetInt64();
        var components = (await Get(tech, $"{assets}/SL-02")).GetProperty("components").EnumerateArray().ToList();
        var slot2Id = components.Single(c => c.GetProperty("position").GetString() == "Cabinet slot 2").GetProperty("componentId").GetInt64();
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, mp, $"/api/maintenance/components/{slot2Id}/remove", new { workOrderId = repair, reason = "x" })).Status);
        await Rejected(plant, tech, $"/api/maintenance/components/{slot2Id}/remove", new { workOrderId = other, reason = "Faulty" }, "work_order_not_for_asset");
        await plant.Ok(tech, $"/api/maintenance/components/{slot2Id}/remove", new { workOrderId = repair, reason = "Faulty: no output on channel 3" });
        await Rejected(plant, tech, $"/api/maintenance/components/{slot2Id}/remove", new { workOrderId = repair, reason = "Again" }, "already_removed");
        await plant.Ok(tech, $"{assets}/SL-02/components", new { partNumber = "SC-440", position = "Cabinet slot 2", quantity = 1m, workOrderId = repair });

        detail = await Get(tech, $"{assets}/SL-02");
        Assert.Equal(["Created", "ComponentInstalled", "ComponentInstalled", "Commissioned", "ComponentRemoved", "ComponentInstalled"],
            detail.GetProperty("history").EnumerateArray().Select(h => h.GetProperty("kind").GetString()!).ToArray());
        var installed = detail.GetProperty("components").EnumerateArray().Where(c => c.GetProperty("removedAt").ValueKind == JsonValueKind.Null).ToList();
        Assert.Equal(2, installed.Count);
        Assert.Contains(installed, c => c.GetProperty("installedByWorkOrder").ValueKind == JsonValueKind.Number && c.GetProperty("installedByWorkOrder").GetInt64() == repair);

        // Scenario 7: SC-440 reaches end of support with no replacement; 2 installed on SL-02, 0 spares.
        var obsolescence = "/api/engineering/obsolescence";
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, tech, obsolescence, new { partNumber = "SC-440", status = "Obsolete", evidence = "x" })).Status);
        await Rejected(plant, eng, obsolescence, new { partNumber = "SC-440", status = "Obsolete", evidence = " " }, "evidence_required");
        await Rejected(plant, eng, obsolescence, new { partNumber = "SC-440", status = "LastTimeBuy", evidence = "Notice" }, "date_required");
        await Rejected(plant, eng, obsolescence, new { partNumber = "SC-440", status = "Obsolete", evidence = "Notice", replacementPart = "SC-440" }, "same_part");
        var record = Id(await plant.Ok(eng, obsolescence, new { partNumber = "SC-440", status = "Obsolete", endOfSupport = "2028-12-31", evidence = "Manufacturer notice PCN-2291" }));
        var impact = await Get(eng, $"{obsolescence}/{record}/impact");
        Assert.True(impact.GetProperty("actionRequired").GetBoolean());
        Assert.Equal((2m, 0m, 0m), (impact.GetProperty("installedQuantity").GetDecimal(), impact.GetProperty("sparesAvailable").GetDecimal(), impact.GetProperty("onOrder").GetDecimal()));
        Assert.Equal(["SL-02"], impact.GetProperty("assets").EnumerateArray().Select(a => a.GetString()!).ToArray());
        Assert.Equal(["SLIT-WC"], impact.GetProperty("workCenters").EnumerateArray().Select(a => a.GetString()!).ToArray());
        Assert.Empty(impact.GetProperty("approvedAlternates").EnumerateArray());
        Assert.Equal("None recorded", impact.GetProperty("replacement").GetString());
        Assert.StartsWith("Obsolescence action required: SC-440", impact.GetProperty("headline").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("SC-450", impact.GetRawText(), StringComparison.Ordinal);
        Assert.Contains((await Get(tech, $"{assets}/SL-02")).GetProperty("obsolescence").EnumerateArray(), o => o.GetProperty("recordId").GetInt64() == record);

        // A replacement is named by one person and approved by a different one with engineering authority.
        await plant.Ok(lead, $"{obsolescence}/{record}/replacement", new { replacementPart = "SC-450" });
        await Rejected(plant, lead, $"{obsolescence}/{record}/replacement/approve", new { reason = "Mine" }, "separation_of_duties");
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, eng, $"{obsolescence}/{record}/replacement/approve", new { reason = "x" })).Status);
        await plant.Ok(approver, $"{obsolescence}/{record}/replacement/approve", new { reason = "Pin-compatible; firmware 4.2 verified" });
        impact = await Get(eng, $"{obsolescence}/{record}/impact");
        Assert.False(impact.GetProperty("actionRequired").GetBoolean());
        Assert.StartsWith("SC-450 approved", impact.GetProperty("replacement").GetString(), StringComparison.Ordinal);

        // Restricting a production asset takes its machine out of capacity until it is returned to service.
        Assert.DoesNotContain((await Downtime(mp)).EnumerateArray(), w => w.GetProperty("kind").GetString() == "AssetOutOfService");
        await plant.Ok(mp, $"{assets}/SL-02/status", new { status = "Restricted", reason = "Guard interlock bypassed; do not run" });
        var window = Assert.Single((await Downtime(mp)).EnumerateArray(), w => w.GetProperty("kind").GetString() == "AssetOutOfService");
        Assert.Equal(("SLITTER-B", JsonValueKind.Null), (window.GetProperty("machineCode").GetString(), window.GetProperty("endsAt").ValueKind));
        await plant.Ok(mp, $"{assets}/SL-02/status", new { status = "Active", reason = "Interlock repaired and tested" });
        window = Assert.Single((await Downtime(mp)).EnumerateArray(), w => w.GetProperty("kind").GetString() == "AssetOutOfService");
        Assert.Equal(JsonValueKind.String, window.GetProperty("endsAt").ValueKind);

        // Decommissioning: requested by one person, decided by another; open work blocks it.
        await Rejected(plant, mp, $"{assets}/SL-02/decommission", new { reason = "Replace the line" }, "open_work_orders");
        await plant.Ok(mp, assets, new { assetCode = "COMP-1", name = "Air compressor", type = "Utility", criticality = "High", location = "Compressor room" });
        await plant.Ok(mp, $"{assets}/COMP-1/status", new { status = "Active", reason = "In use" });
        await plant.Ok(mp, $"{assets}/COMP-1/change", new { name = "Air compressor 1", criticality = "High", location = "Compressor room", reason = "Name it" });
        await plant.Ok(mp, $"{assets}/COMP-1/decommission", new { reason = "Replaced by the new screw compressor" });
        await Rejected(plant, mp, $"{assets}/COMP-1/decommission/decide", new { approve = true, reason = "Mine" }, "separation_of_duties");
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, tech, $"{assets}/COMP-1/decommission/decide", new { approve = true, reason = "x" })).Status);
        await plant.Ok(mp2, $"{assets}/COMP-1/decommission/decide", new { approve = true, reason = "Confirmed removed from site" });
        var comp = await Get(mp, $"{assets}/COMP-1");
        Assert.Equal("Decommissioned", comp.GetProperty("asset").GetProperty("status").GetString());
        Assert.Equal(["Created", "Commissioned", "Changed", "DecommissionRequested", "Decommissioned"], comp.GetProperty("history").EnumerateArray().Select(h => h.GetProperty("kind").GetString()!).ToArray());
        await Rejected(plant, mp, $"{assets}/COMP-1/status", new { status = "Active", reason = "Back" }, "asset_decommissioned");

        // History, installations and obsolescence records cannot be edited or deleted.
        await using var db = new NpgsqlConnection(app.ConnectionString);
        await db.OpenAsync();
        foreach (var sql in new[]
        {
            "UPDATE maintenance.asset_event SET detail = 'x'",
            "DELETE FROM maintenance.asset_event",
            "DELETE FROM maintenance.asset WHERE asset_code = 'COMP-1'",
            "UPDATE maintenance.asset SET name = 'x' WHERE asset_code = 'COMP-1'",
            "UPDATE maintenance.asset SET status = 'Restricted' WHERE asset_code = 'SL-02'",
            "UPDATE maintenance.asset_component SET part_number = 'SC-450'",
            $"UPDATE maintenance.asset_component SET removed_at = NULL, removed_by = NULL, removal_reason = NULL WHERE component_id = {slot2Id}",
            "DELETE FROM maintenance.asset_component",
            $"UPDATE engineering.obsolescence SET evidence = 'x' WHERE record_id = {record}",
            $"UPDATE engineering.obsolescence SET replacement_status = 'Rejected' WHERE record_id = {record}",
            "DELETE FROM engineering.obsolescence",
        })
        {
            await using var tamper = new NpgsqlCommand(sql, db);
            await Assert.ThrowsAsync<PostgresException>(() => tamper.ExecuteNonQueryAsync());
        }
    }

    [Fact]
    public async Task An_mro_item_at_its_reorder_point_is_bought_up_to_its_maximum_for_mro()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("mp", "MaintenancePlanner");
        await plant.Person("tech", "MaintenanceTechnician");
        var (mp, tech, stores) = (plant.As("mp"), plant.As("tech"), plant.As("stores"));
        await BoughtAsync(plant, "FILTER-10", 5);
        await BoughtAsync(plant, "BELT-7", 5);

        var url = "/api/inventory/mro/FILTER-10";
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(plant, tech, url, new { minQuantity = 2m, reorderPoint = 4m, maxQuantity = 12m, criticalSpare = false, reason = "x" })).Status);
        await Rejected(plant, mp, url, new { minQuantity = 2m, reorderPoint = 4m, maxQuantity = 12m, criticalSpare = false }, "reason_required");
        await Rejected(plant, mp, url, new { minQuantity = 2m, reorderPoint = 12m, maxQuantity = 12m, criticalSpare = false, reason = "x" }, "invalid_levels");
        await Rejected(plant, mp, "/api/inventory/mro/ROLL-100", new { minQuantity = 0m, reorderPoint = 1m, maxQuantity = 2m, criticalSpare = false, reason = "x" }, "not_purchased");
        await plant.Ok(mp, url, new { minQuantity = 2m, reorderPoint = 4m, maxQuantity = 12m, criticalSpare = false, reason = "Two changes a month per slitter" });
        await plant.Ok(planner, "/api/inventory/mro/BELT-7", new { minQuantity = 2m, reorderPoint = 3m, maxQuantity = 6m, criticalSpare = true, shelfLifeDays = 720, reason = "Line stops without one" });
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "FILTER-10", quantity = 3m, locationCode = "STORES", reason = "Opening" });
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "BELT-7", quantity = 1m, locationCode = "STORES", reason = "Opening" });

        var mrp = (await Get(planner, "/api/planning/mrp")).GetProperty("output");
        var filter = Assert.Single(mrp.GetProperty("plannedOrders").EnumerateArray(), p => p.GetProperty("partNumber").GetString() == "FILTER-10");
        Assert.Equal((9m, false), (filter.GetProperty("quantity").GetDecimal(), filter.GetProperty("pastDue").GetBoolean()));
        // MRP plans in site dates (the sample site is in New York), not UTC dates.
        var siteDue = NodaTime.Instant.FromDateTimeOffset(DateTimeOffset.UtcNow).InZone(NodaTime.DateTimeZoneProviders.Tzdb["America/New_York"]).Date.PlusDays(5);
        Assert.Equal(siteDue.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), filter.GetProperty("dueDate").GetString());
        Assert.Equal("MroReplenishment", Assert.Single(filter.GetProperty("pegging").EnumerateArray()).GetProperty("kind").GetString());
        var belt = Assert.Single(mrp.GetProperty("plannedOrders").EnumerateArray(), p => p.GetProperty("partNumber").GetString() == "BELT-7");
        Assert.Equal(5m, belt.GetProperty("quantity").GetDecimal());
        Assert.Contains(mrp.GetProperty("exceptions").EnumerateArray(), e => e.GetProperty("kind").GetString() == "CriticalSpareShort" && e.GetProperty("partNumber").GetString() == "BELT-7");

        // The plan becomes a requisition whose source is MRO replenishment of that item.
        var requisition = Id(await plant.Ok(planner, "/api/planning/mrp/requisitions", new { plannedOrder = filter.GetProperty("key").GetString() }));
        var r = (await Get(planner, "/api/procurement/requisitions")).EnumerateArray().Single(x => x.GetProperty("requisitionId").GetInt64() == requisition);
        Assert.Equal(("MroReplenishment", "mro:FILTER-10", 9m), (r.GetProperty("sourceKind").GetString(), r.GetProperty("sourceKey").GetString(), r.GetProperty("quantity").GetDecimal()));

        var levels = (await Get(mp, "/api/inventory/mro")).EnumerateArray().ToDictionary(x => x.GetProperty("policy").GetProperty("partNumber").GetString()!);
        Assert.True(levels["FILTER-10"].GetProperty("atOrBelowReorderPoint").GetBoolean());
        Assert.True(levels["BELT-7"].GetProperty("belowMinimum").GetBoolean());

        // Above the reorder point nothing is planned; a cleared policy stops replenishment, and every setting is kept.
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "FILTER-10", quantity = 6m, locationCode = "STORES", reason = "Found in the old crib" });
        await plant.Ok(mp, "/api/inventory/mro/BELT-7/clear", new { reason = "Line converted to direct drive" });
        await Rejected(plant, mp, "/api/inventory/mro/BELT-7/clear", new { reason = "Again" }, "no_policy");
        mrp = (await Get(planner, "/api/planning/mrp")).GetProperty("output");
        Assert.DoesNotContain(mrp.GetProperty("plannedOrders").EnumerateArray(), p => p.GetProperty("partNumber").GetString() is "FILTER-10" or "BELT-7");
        var history = (await Get(mp, "/api/inventory/mro/BELT-7/history")).EnumerateArray().ToList();
        Assert.Equal((2, true, false), (history.Count, history[0].GetProperty("cleared").GetBoolean(), history[1].GetProperty("cleared").GetBoolean()));
        Assert.DoesNotContain((await Get(mp, "/api/inventory/mro")).EnumerateArray(), x => x.GetProperty("policy").GetProperty("partNumber").GetString() == "BELT-7");

        await using var db = new NpgsqlConnection(app.ConnectionString);
        await db.OpenAsync();
        foreach (var sql in new[] { "UPDATE engineering.item_mro SET max_quantity = 99", "DELETE FROM engineering.item_mro" })
        {
            await using var tamper = new NpgsqlCommand(sql, db);
            await Assert.ThrowsAsync<PostgresException>(() => tamper.ExecuteNonQueryAsync());
        }
    }
}
