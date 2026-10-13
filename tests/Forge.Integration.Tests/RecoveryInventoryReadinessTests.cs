using System.Globalization;
using System.Text.Json;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class RecoveryInventoryReadinessTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Scenario_preloaded_material_keeps_hidden_released_demand_and_refreshes_current_stock()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        // Keep calendar capacity available so the only before/after binding change is
        // the recorded material supply. The base plant does not define shift calendars.
        await plant.Ok(plant.Engineer, "/api/resources/shifts", new
        {
            code = "MATERIAL-ALL", siteCode = "PLANT1", name = "Material regression calendar",
            start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 },
        });
        await plant.Ok(plant.Engineer, "/api/resources/work-centers/SLIT-WC/shifts", new { shiftCode = "MATERIAL-ALL" });
        foreach (var subject in new[] { "sarah", "michael" })
            await plant.Ok(plant.Engineer, $"/api/workforce/workers/{plant.People[subject]}/shift", new { shiftCode = "MATERIAL-ALL" });
        await plant.Person("material-programs", "ProgramManager");
        var program = Guid.Parse((await plant.Ok(plant.As("material-programs"), "/api/programs/",
            new { code = "PRIVATE-MATERIAL", name = "Private material commitment" })).GetProperty("entityId").GetString()!);
        foreach (var subject in new[] { "eng", "planner" })
            await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People[subject]}/programs",
                new { programId = program, reason = "Authorized source order creation and independent release" });
        var due = DateTime.UtcNow.AddDays(10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        // The existing million kilograms leave only 14 after this higher-priority requirement of 999,986.
        var hidden = Id(await plant.Ok(plant.Engineer, "/api/orders/", new
        {
            partNumber = "ROLL-100", quantity = 384_610m, dueDate = due, priority = 1, programId = program,
        }));
        await plant.Ok(planner, $"/api/orders/{hidden}/release", new { reason = "Independent release of higher-priority material commitment" });
        await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["planner"]}/programs/{program}/revoke",
            new { reason = "Planner retains global planning read but no longer sees the private commitment" });
        var visible = Id(await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = "ROLL-100", quantity = 10m, dueDate = due, priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{visible}/release", new { reason = "Independent release of visible 26-kilogram requirement" });
        var scenario = Guid.Parse((await plant.Ok(planner, "/api/planning/scenarios", new
        {
            name = "Current stock with hidden competing demand",
            change = new
            {
                kind = "MachineUnavailable", machineCode = "SLITTER-B", startsAt = DateTimeOffset.UtcNow, durationHours = 1m,
                recoverySelections = new[] { new { optionId = "alternate-machine", orderNumber = visible, sequence = 10, machineCode = "SLITTER-A" } },
            },
            reason = "Evaluate the visible target using current full material allocation",
        })).GetProperty("entityId").GetString()!);

        var before = await SourceStateAsync(app);
        var material = (await ReadAsync(planner, "/api/planning/material")).GetProperty("output");
        var current = Assert.Single(material.EnumerateArray(), m => m.GetProperty("orderNumber").GetInt64() == visible);
        Assert.Equal("Shortfall", current.GetProperty("state").GetString());
        Assert.DoesNotContain(material.EnumerateArray(), m => m.GetProperty("orderNumber").GetInt64() == hidden);
        var blocked = await RunAsync(planner, scenario);
        var option = Assert.Single(blocked.GetProperty("recoveryOptions").EnumerateArray(), o => o.GetProperty("id").GetString() == "alternate-machine");
        Assert.False(option.GetProperty("feasible").GetBoolean());
        Assert.Equal("Infeasible", option.GetProperty("status").GetString());
        Assert.Contains(option.GetProperty("constraints").EnumerateArray(), c => c.GetString()!.Contains("Material", StringComparison.Ordinal));
        Assert.DoesNotContain("Order " + hidden, blocked.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(before, await SourceStateAsync(app));

        await plant.Ok(plant.As("stores"), "/api/inventory/receipts", new
        {
            partNumber = "COIL-AL", quantity = 26m, locationCode = "STORES", reason = "Actual replenishment after the first private evaluation",
        });
        before = await SourceStateAsync(app);
        current = Assert.Single((await ReadAsync(planner, "/api/planning/material")).GetProperty("output").EnumerateArray(),
            m => m.GetProperty("orderNumber").GetInt64() == visible);
        Assert.Equal("Ready", current.GetProperty("state").GetString());
        option = Assert.Single((await RunAsync(planner, scenario)).GetProperty("recoveryOptions").EnumerateArray(), o => o.GetProperty("id").GetString() == "alternate-machine");
        Assert.True(option.GetProperty("feasible").GetBoolean(), option.GetRawText());
        Assert.Equal(before, await SourceStateAsync(app));
    }

    [Fact]
    public async Task Real_inventory_source_gates_run_in_a_read_only_snapshot_without_commands_or_source_changes()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var stores = plant.As("stores");
        await plant.Person("readiness-buyer", "Buyer");
        await plant.Person("readiness-manager", "PurchasingManager");
        await plant.Person("readiness-shipper", "Shipper");
        var buyer = plant.As("readiness-buyer");
        var manager = plant.As("readiness-manager");
        var future = DateTime.UtcNow.AddDays(10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var order = Id(await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = "ROLL-100", quantity = 10m, dueDate = future, priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Release source order for isolated issue readiness" });
        await plant.Ok(stores, "/api/inventory/locations", new { code = "READY-DEST", siteCode = "PLANT1", name = "Readiness transfer destination", kind = "Stores" });

        await plant.Ok(plant.Engineer, "/api/engineering/items", new
        {
            partNumber = "READY-MAT", description = "Identified source material", unitOfMeasure = "KG", makeBuy = "Buy", leadTimeDays = 7,
            lotTracked = true, serialized = false, @class = "RawMaterial",
        });
        await plant.Ok(plant.Engineer, "/api/engineering/items/READY-MAT/revisions", new { revision = "A" });
        await plant.Ok(plant.Approver, "/api/engineering/items/READY-MAT/revisions/A/release", new { reason = "Independent material configuration release" });
        await plant.Ok(stores, "/api/inventory/receipts", new
        {
            partNumber = "READY-MAT", quantity = 100m, locationCode = "STORES", lotNumber = "READY-LOT", revision = "A", reason = "Identified released opening stock",
        });
        await plant.Ok(plant.Engineer, "/api/engineering/items", new
        {
            partNumber = "READY-SVC", description = "Approved outside processing service", unitOfMeasure = "EA", makeBuy = "Buy", leadTimeDays = 7, @class = "Service",
        });
        await plant.Ok(buyer, "/api/procurement/suppliers", new { code = "READY-PROC", name = "Readiness processor" });
        var purchase = Id(await plant.Ok(buyer, "/api/procurement/purchase-orders", new
        {
            supplier = "READY-PROC", lines = new[] { new { partNumber = "READY-SVC", quantity = 10m, promisedDate = future } }, reason = "Buy processor service",
        }));
        await plant.Ok(manager, $"/api/procurement/purchase-orders/{purchase}/approve", new { reason = "Independent processor procurement review" });
        await plant.Ok(stores, "/api/inventory/locations", new
        {
            code = "READY-PROC-EXT", siteCode = "PLANT1", name = "Processor dock", kind = "ExternalProcessor", custodianSupplier = "READY-PROC",
        });

        var demand = Id(await plant.Ok(planner, "/api/planning/demand", new
        {
            partNumber = "READY-MAT", quantity = 4m, needDate = future, priority = 3, kind = "CustomerOrder", customer = "Readiness customer", reason = "Recorded partial delivery demand",
        }));
        var shipmentId = Id(await plant.Ok(stores, "/api/inventory/shipments", new
        {
            customer = "Readiness customer", demandId = demand,
            lines = new[] { new { partNumber = "READY-MAT", quantity = 2m, locationCode = "STORES", lotNumber = "READY-LOT" } }, reason = "Prepare identified partial shipment",
        }));
        await plant.Ok(stores, $"/api/inventory/shipments/{shipmentId}/pack", new { reason = "Packer checked physical stock" });

        var before = await SourceStateAsync(app);
        var factory = app.Services.GetRequiredService<IKernelUnitOfWorkFactory>();
        var readiness = app.Services.GetRequiredService<IInventoryRecoveryReadiness>();
        await using (var snapshot = await factory.BeginSnapshotAsync(CancellationToken.None))
        {
            var postgresSnapshot = Assert.IsType<PostgresUnitOfWork>(snapshot);
            await using (var mode = postgresSnapshot.Command("SHOW transaction_read_only"))
                Assert.Equal("on", (string)(await mode.ExecuteScalarAsync())!);
            var now = app.Services.GetRequiredService<TimeProvider>().GetUtcNow();
            var handler = (await snapshot.People.LoadPrincipalAsync(new PersonId(plant.People["stores"]), CancellationToken.None))!;
            var shipper = (await snapshot.People.LoadPrincipalAsync(new PersonId(plant.People["readiness-shipper"]), CancellationToken.None))!;
            var shipment = (await snapshot.Store<IInventoryLookup>().FindShipmentAsync(shipmentId, CancellationToken.None))!;
            Assert.Equal(ShipmentStatus.Packed, shipment.Status);
            Assert.NotEqual(shipment.PackedBy, "person:" + shipper.PersonId);
            Assert.NotNull(await snapshot.Store<Forge.Procurement.Contracts.ITechnicalPackageLookup>().FindPackageAsync(purchase, 1, CancellationToken.None));

            var issue = new StockIssueReadinessRequest(order, "COIL-AL", 1m, "STORES", null);
            var transfer = new StockTransferReadinessRequest("COIL-AL", 1m, "STORES", "READY-DEST", null, StockStatus.Released);
            var dispatch = new OutsideDispatchReadinessRequest("READY-MAT", 5m, "STORES", "READY-PROC-EXT", "READY-LOT", purchase, 1,
                MaterialRevision: "A", ExpectedReturnDate: DateOnly.ParseExact(future, "yyyy-MM-dd", CultureInfo.InvariantCulture));

            await readiness.RequireIssueAsync(snapshot, issue, handler, now, CancellationToken.None);
            await readiness.RequireTransferAsync(snapshot, transfer, CancellationToken.None);
            await readiness.RequireOutsideDispatchAsync(snapshot, dispatch, handler, now, CancellationToken.None);
            await readiness.RequireShipmentReleaseAsync(snapshot, shipment, "person:" + shipper.PersonId, now, CancellationToken.None);

            var missingStock = await Assert.ThrowsAsync<CommandRejectedException>(() =>
                readiness.RequireIssueAsync(snapshot, issue with { LocationCode = "READY-DEST" }, handler, now, CancellationToken.None));
            Assert.Equal("insufficient_stock", missingStock.Code);
            var excessiveTransfer = await Assert.ThrowsAsync<CommandRejectedException>(() =>
                readiness.RequireTransferAsync(snapshot, transfer with { Quantity = 1_000_001m }, CancellationToken.None));
            Assert.Equal("insufficient_stock", excessiveTransfer.Code);
            var wrongProcessor = await Assert.ThrowsAsync<CommandRejectedException>(() =>
                readiness.RequireOutsideDispatchAsync(snapshot, dispatch with { ProcessorLocation = "READY-DEST" }, handler, now, CancellationToken.None));
            Assert.Equal("processor_location_required", wrongProcessor.Code);
            var samePacker = await Assert.ThrowsAsync<CommandRejectedException>(() =>
                readiness.RequireShipmentReleaseAsync(snapshot, shipment, shipment.PackedBy!, now, CancellationToken.None));
            Assert.Equal("separation_of_duties", samePacker.Code);

            Assert.Equal(ShipmentStatus.Packed, (await snapshot.Store<IInventoryLookup>().FindShipmentAsync(shipmentId, CancellationToken.None))!.Status);
            Assert.Equal(OrderStatus.Released, (await snapshot.Store<IExecutionLookup>().FindOrderAsync(order, CancellationToken.None))!.Status);
            Assert.DoesNotContain(await snapshot.Store<IInventoryLookup>().ListOrderIssuesAsync(CancellationToken.None), i => i.OrderNumber == order);
            Assert.Empty(await snapshot.Store<IOutsideProcessLookup>().ListDispatchesAsync(null, CancellationToken.None));
        }
        Assert.Equal(before, await SourceStateAsync(app));
    }

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static async Task<JsonElement> ReadAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> RunAsync(HttpClient client, Guid scenario)
    {
        using var response = await client.PostAsync($"/api/planning/scenarios/{scenario:D}/run", null);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static async Task<string> SourceStateAsync(ForgeApp app)
    {
        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync();
        await using var query = new NpgsqlCommand("""
            SELECT jsonb_build_object(
                'ledger', (SELECT count(*) FROM inventory.transaction),
                'outbox', (SELECT count(*) FROM kernel.outbox),
                'audit', (SELECT count(*) FROM kernel.audit_log),
                'events', (SELECT count(*) FROM kernel.event_log),
                'idempotency', (SELECT count(*) FROM kernel.idempotency),
                'dispatches', (SELECT count(*) FROM inventory.outside_process_dispatch),
                'balances', (SELECT coalesce(jsonb_agg(to_jsonb(b) ORDER BY b.part_number, b.location_code, b.lot_number, b.status), '[]'::jsonb) FROM inventory.balance b),
                'shipments', (SELECT coalesce(jsonb_agg(to_jsonb(s) ORDER BY s.shipment_id), '[]'::jsonb) FROM inventory.shipment s),
                'orders', (SELECT coalesce(jsonb_agg(to_jsonb(o) ORDER BY o.order_number), '[]'::jsonb) FROM execution.production_order o),
                'purchases', (SELECT coalesce(jsonb_agg(to_jsonb(p) ORDER BY p.po_number), '[]'::jsonb) FROM procurement.purchase_order p),
                'demand', (SELECT coalesce(jsonb_agg(to_jsonb(d) ORDER BY d.demand_id), '[]'::jsonb) FROM planning.demand d)
            )::text
            """, connection);
        return (string)(await query.ExecuteScalarAsync())!;
    }
}
