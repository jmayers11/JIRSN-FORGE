using System.Globalization;
using System.Net;
using System.Text.Json;
using Forge.Host.Identification;
using Forge.Kernel.Postgres;
using Forge.Quality.Contracts;
using Forge.Quality.Qms;
using Npgsql;
using static Forge.Integration.Tests.ScanSessionScenario;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class V41ScanMaterialMovementTests(PostgresFixture postgres)
{
    private const string Lot = "SCAN-COIL-1";
    private const string PointOfUse = "SCAN-POU";

    private static string MaterialCode => $"forge:v1:{Namespace}:material:{CanonicalIdentification.EncodeKey("SCAN-RAW")}:{CanonicalIdentification.EncodeKey(Lot)}";
    private static string LocationCode(string location) => $"forge:v1:{Namespace}:location:{CanonicalIdentification.EncodeKey(location)}";
    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    [Fact]
    public async Task Scan_session_binds_actual_material_pick_and_delivery_to_exact_lot_order_task_and_location()
    {
        await using var flow = await CreateAsync(postgres);
        await flow.Plant.Ok(flow.Plant.Admin, $"/api/kernel/people/{flow.Plant.People["scan-terminal"]}/roles",
            new { role = "MaterialHandler", reason = "This authenticated terminal also performs the scoped material movement in the acceptance scenario" });
        var order = await CreateLotTrackedOperationAsync(flow);
        var stores = flow.Plant.As("stores");
        await flow.Plant.Ok(stores, "/api/inventory/receipts", new
        {
            partNumber = "SCAN-RAW", quantity = 35m, locationCode = "STORES", lotNumber = Lot,
            revision = "A", reason = "Record the exact lot used by the scan-bound material movement"
        });
        await flow.Plant.Ok(stores, "/api/inventory/locations", new
        {
            code = PointOfUse, siteCode = "PLANT1", name = "Slitting point of use", kind = "Staging", workCenterCode = "SLIT-WC",
            reason = "Use the actual operation work center as the material task destination"
        });
        await flow.Plant.Ok(stores, "/api/inventory/locations", new
        {
            code = "SCAN-TRANSIT", siteCode = "PLANT1", name = "Non-nettable handoff transit", kind = "InTransit", nettable = false,
            reason = "Keep dispatched material unavailable until a different worker confirms receipt"
        });
        await flow.Plant.Ok(stores, "/api/inventory/locations", new
        {
            code = "SCAN-QUARANTINE", siteCode = "PLANT1", name = "Non-nettable receipt quarantine", kind = "Quarantine", nettable = false,
            reason = "Rejected or corrected handoff material must remain unavailable for production"
        });
        var task = Id(await flow.Plant.Ok(stores, "/api/inventory/tasks", new
        {
            kind = "Stage", orderNumber = order, partNumber = "SCAN-RAW", quantity = 25m,
            toLocation = PointOfUse, fromLocation = "STORES", lotNumber = Lot,
            reason = "Allocate the actual exact-lot task to this production order"
        }));

        var session = await CreateSessionAsync(flow, order);
        session = await StageAsync(flow, session, Operation(order));
        session = await StageAsync(flow, session, MaterialCode);
        session = await StageAsync(flow, session, LocationCode("STORES"));

        var pickPath = $"/api/inventory/tasks/{task}/pick";
        var pick = new
        {
            scannedLocation = "STORES", scannedLot = Lot, quantity = 25m,
            scannedMaterial = new { partNumber = "SCAN-RAW", lotNumber = Lot }, reason = "Bind the pick to the currently staged exact lot and source"
        };
        await RejectedAsync(await SendAsync(flow.Terminal, pickPath, pick with { scannedLocation = "WRONG-STORES" }, "scan-pick-wrong-location", session),
            "scan_material_location_mismatch");
        await RejectedAsync(await SendAsync(flow.Terminal, pickPath, pick with { scannedMaterial = new { partNumber = "WRONG-PART", lotNumber = Lot } }, "scan-pick-wrong-material", session),
            "scan_material_identity_mismatch");
        const string privateScanValue = "RAW-SCAN-ATTEMPT-SECRET-418";
        using (var legacyMismatch = await SendAsync(flow.Terminal, pickPath, new
        {
            scannedLocation = privateScanValue, scannedLot = Lot, quantity = 25m,
            scannedMaterial = new { partNumber = "SCAN-RAW", lotNumber = Lot }, reason = "A failed scan value must not enter user-facing or retained diagnostics"
        }, "scan-pick-redacted-legacy"))
        {
            var diagnostic = await legacyMismatch.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.UnprocessableEntity, legacyMismatch.StatusCode);
            Assert.Contains("scan_mismatch", diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain(privateScanValue, diagnostic, StringComparison.Ordinal);
        }
        Assert.DoesNotContain(privateScanValue, string.Join("\n", await RowsAsync(flow, "kernel.audit_log")), StringComparison.Ordinal);
        Assert.Equal("Allocated", await TaskStatusAsync(flow, task, order));

        var pickCorrelation = Guid.NewGuid();
        var picked = await JsonAsync(await SendAsync(flow.Terminal, pickPath, pick, "scan-pick-exact", session, pickCorrelation));
        Assert.Equal("Picked", await TaskStatusAsync(flow, task, order));
        var afterPick = await ReadAsync(flow.Terminal, session.GetProperty("id").GetGuid());
        var pickAcceptance = Assert.Single(afterPick.GetProperty("accepted").EnumerateArray());
        Assert.Equal("PickMaterial", pickAcceptance.GetProperty("commandType").GetString());
        Assert.Equal(pickCorrelation, pickAcceptance.GetProperty("correlationId").GetGuid());
        Assert.NotEqual(Guid.Empty, pickAcceptance.GetProperty("auditId").GetGuid());
        Assert.NotEmpty(pickAcceptance.GetProperty("eventIds").EnumerateArray());
        Assert.Equal(picked.GetRawText(), (await JsonAsync(await SendAsync(flow.Terminal, pickPath, pick, "scan-pick-exact", session))).GetRawText());
        Assert.Single((await ReadAsync(flow.Terminal, session.GetProperty("id").GetGuid())).GetProperty("accepted").EnumerateArray());

        var start = new { badge = SarahBadge, orderNumber = order, sequence = 10, machineCode = "SLITTER-A" };
        await RejectedAsync(await SendAsync(flow.Terminal, "/api/shopfloor/start", start, "scan-start-before-delivery", session),
            "scan_material_not_delivered");

        session = await StageAsync(flow, session, LocationCode(PointOfUse));
        var deliverPath = $"/api/inventory/tasks/{task}/deliver";
        var deliver = new { scannedLocation = PointOfUse, reason = "Bind delivery to the exact staged point of use" };
        await RejectedAsync(await SendAsync(flow.Terminal, deliverPath, deliver with { scannedLocation = "OTHER-POU" }, "scan-deliver-wrong-location", session),
            "scan_material_location_mismatch");
        Assert.Equal("Picked", await TaskStatusAsync(flow, task, order));

        var deliverCorrelation = Guid.NewGuid();
        var delivered = await JsonAsync(await SendAsync(flow.Terminal, deliverPath, deliver, "scan-deliver-exact", session, deliverCorrelation));
        Assert.Equal("InTransit", await TaskStatusAsync(flow, task, order));
        Assert.Equal(0m, await BalanceAsync(flow, "SCAN-RAW", PointOfUse, Lot));
        await flow.Plant.Ok(flow.Plant.Admin, $"/api/kernel/people/{flow.Plant.People["scan-terminal"]}/roles",
            new { role = "MaterialHandler", reason = "The authenticated stockroom terminal performs the dispatch step in this custody test" });
        const string receiverPin = "60428173";
        await flow.Plant.Ok(flow.Terminal, "/api/shopfloor/pin", new { badge = SarahBadge, newPin = receiverPin });
        await flow.Plant.Ok(flow.Terminal, $"/api/inventory/tasks/{task}/receive", new
        {
            scannedLocation = PointOfUse, badge = SarahBadge, pin = receiverPin, acceptedQuantity = 25m, rejectedQuantity = 0m,
            quarantineLocation = (string?)null, reason = "Distinct authenticated floor worker confirms the complete delivery"
        });
        Assert.Equal("Delivered", await TaskStatusAsync(flow, task, order));
        await AssertActualTransferAsync(flow, order);
        var afterDelivery = await ReadAsync(flow.Terminal, session.GetProperty("id").GetGuid());
        Assert.Equal(2, afterDelivery.GetProperty("accepted").GetArrayLength());
        var deliveryAcceptance = afterDelivery.GetProperty("accepted").EnumerateArray().Single(row => row.GetProperty("commandType").GetString() == "DeliverMaterial");
        Assert.Equal(deliverCorrelation, deliveryAcceptance.GetProperty("correlationId").GetGuid());
        Assert.NotEmpty(deliveryAcceptance.GetProperty("eventIds").EnumerateArray());
        Assert.Equal(delivered.GetRawText(), (await JsonAsync(await SendAsync(flow.Terminal, deliverPath, deliver, "scan-deliver-exact", session))).GetRawText());
        Assert.Equal(2, (await ReadAsync(flow.Terminal, session.GetProperty("id").GetGuid())).GetProperty("accepted").GetArrayLength());

        await JsonAsync(await SendAsync(flow.Terminal, "/api/shopfloor/start", start, "scan-start-after-delivery", session));
        var final = await ReadAsync(flow.Terminal, session.GetProperty("id").GetGuid());
        Assert.Equal(3, final.GetProperty("accepted").GetArrayLength());
        Assert.Contains(final.GetProperty("accepted").EnumerateArray(), row => row.GetProperty("commandType").GetString() == "StartOperation");

        var partialTask = Id(await flow.Plant.Ok(flow.Terminal, "/api/inventory/tasks", new
        {
            kind = "Replenish", partNumber = "SCAN-RAW", quantity = 10m, toLocation = PointOfUse, fromLocation = "STORES", lotNumber = Lot,
            reason = "Stage a separate replenishment for partial custody acceptance"
        }));
        await flow.Plant.Ok(flow.Terminal, $"/api/inventory/tasks/{partialTask}/pick", new { scannedLocation = "STORES", scannedLot = Lot, quantity = 10m, reason = "Pick exact replenishment lot" });
        await flow.Plant.Ok(flow.Terminal, $"/api/inventory/tasks/{partialTask}/deliver", new { scannedLocation = PointOfUse, reason = "Dispatch replenishment to controlled transit custody" });
        await flow.Plant.Ok(flow.Terminal, $"/api/inventory/tasks/{partialTask}/receive", new
        {
            scannedLocation = PointOfUse, badge = SarahBadge, pin = receiverPin, acceptedQuantity = 7m, rejectedQuantity = 3m,
            quarantineLocation = "SCAN-QUARANTINE", reason = "Three units have damaged packaging and are rejected into quarantine"
        });
        var settledTasks = await JsonAsync(await flow.Terminal.GetAsync("/api/inventory/tasks?open=false"));
        var partial = Assert.Single(settledTasks.EnumerateArray(), row => row.GetProperty("taskId").GetInt64() == partialTask);
        Assert.Equal(("Delivered", 7m, 3m), (partial.GetProperty("status").GetString(), partial.GetProperty("receivedQuantity").GetDecimal(), partial.GetProperty("rejectedQuantity").GetDecimal()));
        var custody = await JsonAsync(await flow.Terminal.GetAsync($"/api/inventory/tasks/{partialTask}/custody"));
        Assert.Contains(custody.EnumerateArray(), row => row.GetProperty("kind").GetString() == "RejectedAtReceipt" && row.GetProperty("quantity").GetDecimal() == 3m);

        await flow.Plant.Ok(flow.Plant.Admin, $"/api/inventory/tasks/{partialTask}/return", new { quantity = 1m, locationCode = "STORES", reason = "Return one unused accepted unit through the authorized correction path" });
        await flow.Plant.Ok(flow.Plant.Admin, $"/api/inventory/tasks/{partialTask}/reverse-receipt", new { quantity = 1m, locationCode = "SCAN-QUARANTINE", reason = "Correct one miscounted acceptance into controlled hold" });
        settledTasks = await JsonAsync(await flow.Plant.Admin.GetAsync("/api/inventory/tasks?open=false"));
        partial = Assert.Single(settledTasks.EnumerateArray(), row => row.GetProperty("taskId").GetInt64() == partialTask);
        Assert.Equal((1m, 1m), (partial.GetProperty("returnedQuantity").GetDecimal(), partial.GetProperty("reversedQuantity").GetDecimal()));
    }

    private static async Task<string> TaskStatusAsync(Flow flow, long taskId, long order)
    {
        var tasks = await JsonAsync(await flow.Terminal.GetAsync($"/api/inventory/tasks?open=false&order={order.ToString(CultureInfo.InvariantCulture)}"));
        return tasks.EnumerateArray().Single(row => row.GetProperty("taskId").GetInt64() == taskId).GetProperty("status").GetString()!;
    }

    private static async Task AssertActualTransferAsync(Flow flow, long order)
    {
        await using var connection = new NpgsqlConnection(flow.App.ConnectionString);
        await connection.OpenAsync();
        foreach (var (from, to) in new[] { ("STORES", "SCAN-TRANSIT"), ("SCAN-TRANSIT", PointOfUse) })
        {
            await using var command = new NpgsqlCommand("SELECT count(*), COALESCE(sum(quantity),0) FROM inventory.transaction WHERE order_number=$1 AND kind='Transfer' AND part_number='SCAN-RAW' AND lot_number=$2 AND from_location=$3 AND to_location=$4", connection);
            command.Parameters.AddWithValue(order);
            command.Parameters.AddWithValue(Lot);
            command.Parameters.AddWithValue(from);
            command.Parameters.AddWithValue(to);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(25m, reader.GetDecimal(1));
        }
    }

    private static async Task<decimal> BalanceAsync(Flow flow, string part, string location, string lot)
    {
        await using var connection = new NpgsqlConnection(flow.App.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT COALESCE(sum(quantity),0) FROM inventory.transaction WHERE part_number=$1 AND lot_number=$2 AND to_location=$3", connection);
        command.Parameters.AddWithValue(part);
        command.Parameters.AddWithValue(lot);
        command.Parameters.AddWithValue(location);
        return Convert.ToDecimal(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<long> CreateLotTrackedOperationAsync(Flow flow)
    {
        var plant = flow.Plant;
        foreach (var (part, unit, makeBuy) in new[] { ("SCAN-RAW", "KG", "Buy"), ("SCAN-ROLL", "EA", "Make") })
        {
            await plant.Ok(plant.Engineer, "/api/engineering/items", new
            { partNumber = part, description = "Material movement scan acceptance " + part, unitOfMeasure = unit, makeBuy,
                leadTimeDays = 1, lotTracked = true, serialized = false });
            await plant.Ok(plant.Engineer, $"/api/engineering/items/{part}/revisions", new { revision = "A" });
        }
        await plant.Ok(plant.Engineer, "/api/engineering/items/SCAN-ROLL/revisions/A/bom", new
        { lineNumber = 1, componentPartNumber = "SCAN-RAW", quantityPer = 2.5m, scrapPercent = 0m });
        await plant.Ok(plant.Engineer, "/api/engineering/items/SCAN-ROLL/revisions/A/routing", new
        { sequence = 10, description = "Slit", workCenterCode = "SLIT-WC", activityCode = "RUN_SLITTER",
            eligibleMachines = new[] { "SLITTER-A" }, setupMinutes = 0m, runMinutesPerUnit = 0.01m, crewSize = 1 });
        foreach (var part in new[] { "SCAN-RAW", "SCAN-ROLL" })
            await plant.Ok(plant.Approver, $"/api/engineering/items/{part}/revisions/A/release", new { reason = "Release exact test engineering source" });

        var selector = new QmsOperationSelector(WorkCenterCode: "SLIT-WC", ActivityCode: "RUN_SLITTER",
            OperationSequence: 10, PartNumber: "SCAN-ROLL", PartRevision: "A");
        var presentation = new QmsExecutionPresentationRules(120, true, false, true, QmsProviderAssurance.Authenticated,
            [new("SCAN-CURRENT", selector, null, null, [QmsRequiredScanKind.Operation], true, false, true)]);
        var policy = new QmsPolicyContent(QmsPolicyKind.Execution, "An explicit operation scan policy for the material movement acceptance source",
            plant.People["eng"], "Released shop procedure", "When operation source changes", true,
            new(Execution: new(true, true, true, true, presentation)));
        await QmsPolicyScenario.AdoptAsync(plant, "SCAN-MOVE-POLICY", "SCAN-MOVE-EXEC", "SCAN-MOVE-PROCEDURE",
            policy, QmsPolicyPurpose.Execution, selector);

        var planner = plant.As("planner");
        var order = Id(await plant.Ok(plant.Engineer, "/api/orders/", new
        {
            partNumber = "SCAN-ROLL", quantity = 10m,
            dueDate = flow.Time.UtcNow.AddDays(10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), priority = 3
        }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Release the exact lot-tracked operation for scan movement acceptance" });
        return order;
    }

    private static async Task<JsonElement> CreateSessionAsync(Flow flow, long order)
    {
        var created = await JsonAsync(await SendAsync(flow.Terminal, "/api/identification/scan-sessions", new
        {
            operationCode = Operation(order), badge = SarahBadge, machineCode = "SLITTER-A", previousSessionId = (Guid?)null,
            reason = "Open the authenticated session for the actual lot-tracked operation"
        }, "scan-movement-session-" + Guid.NewGuid().ToString("N")));
        return await ReadAsync(flow.Terminal, Guid.Parse(created.GetProperty("entityId").GetString()!));
    }
}
