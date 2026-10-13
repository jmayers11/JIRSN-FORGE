using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Integration.Tests;

/// <summary>V3.27 scale and extension gates: three large but valid factory variants reuse the live Forge services.</summary>
[Collection(PostgresCollection.Name)]
public sealed class V327Tests(PostgresFixture postgres)
{
    private static string Day(int days) => DateTime.UtcNow.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private sealed class ReferenceClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static JsonElement Cell(JsonElement answer, JsonElement row, string key)
    {
        var keys = answer.GetProperty("columns").EnumerateArray().Select(column => column.GetProperty("key").GetString()).ToList();
        var index = keys.IndexOf(key);
        Assert.True(index >= 0, $"The Analyst answer must expose the {key} column.");
        return row.GetProperty("cells")[index];
    }

    private static async Task<JsonElement> AskWithinBudget(HttpClient planner, string kind, string question, string reportId)
    {
        var stopwatch = Stopwatch.StartNew();
        var response = await planner.PostAsJsonAsync("/api/analyst/ask", new { question });
        var json = await response.Content.ReadAsStringAsync();
        stopwatch.Stop();
        Assert.True(response.IsSuccessStatusCode, $"{kind}: {question}: {json}");
        using var answer = JsonDocument.Parse(json);
        Assert.False(answer.RootElement.GetProperty("notAuthorized").GetBoolean(), $"{kind}: {question}");
        Assert.Equal(reportId, answer.RootElement.GetProperty("interpretation").GetProperty("report").GetProperty("reportId").GetString());
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"{kind}: {question} exceeded the 10-second Analyst budget with 200 actual records in each of all sixteen BN populations ({stopwatch.Elapsed}).");
        return answer.RootElement.Clone();
    }

    [Theory]
    [InlineData("healthy")]
    [InlineData("constrained")]
    [InlineData("unusual-valid")]
    public async Task Reference_factories_at_scale_answer_shared_questions_and_keep_integrity(string kind)
    {
        // Record a real shift through the API, then advance the host clock to ask about the completed shift.
        var clock = new ReferenceClock(new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-1).AddHours(16), TimeSpan.Zero));
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), services => services.AddSingleton<TimeProvider>(clock));
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var engineer = plant.Engineer;
        var stores = plant.As("stores");

        await plant.Ok(engineer, "/api/engineering/items", new { partNumber = "REF-COIL", description = "Reference lot-tracked coil", unitOfMeasure = "KG", makeBuy = "Buy", leadTimeDays = 7, lotTracked = true, serialized = false });
        await plant.Ok(engineer, "/api/engineering/items/REF-COIL/revisions", new { revision = "A" });
        await plant.Ok(plant.Approver, "/api/engineering/items/REF-COIL/revisions/A/release", new { reason = "V3.27 reference factory baseline" });

        // Additional slitting records retain the original variant extremes and focused factual questions.
        // The sixteen original BN populations are generated and verified by OriginalReferenceFactory below.
        long firstReleasedOrder = 0;
        for (var index = 1; index <= 100; index++)
        {
            var quantity = kind == "unusual-valid" && index == 100 ? 1_000_000m : kind == "unusual-valid" && index % 10 == 0 ? 0.001m : 10m + index;
            var created = await plant.Ok(engineer, "/api/orders/", new
            {
                partNumber = "ROLL-100",
                quantity,
                dueDate = Day(20 + index % 10),
                priority = 1 + index % 5,
            });
            var number = long.Parse(created.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);
            if (index <= 10)
            {
                await plant.Ok(planner, $"/api/orders/{number}/release", new { reason = $"V3.27 {kind} reference order" });
                if (firstReleasedOrder == 0) firstReleasedOrder = number;
            }

            await plant.Ok(stores, "/api/inventory/receipts", new
            {
                partNumber = "REF-COIL",
                quantity = kind == "unusual-valid" && index % 10 == 0 ? 0.001m : 100m,
                locationCode = "STORES",
                lotNumber = $"RF-{kind.ToUpperInvariant()}-{index:000}",
                supplierLot = $"SUP-{index % 7:00}",
                reason = $"V3.27 {kind} reference receipt",
            });
        }

        // Seed factual answers in addition to the scale records: 100 issued = 80 consumed + 5 scrap + 15 remnant.
        await plant.Ok(engineer, "/api/engineering/items", new { partNumber = "REF-STRIP", description = "Reference slit strip", unitOfMeasure = "KG", makeBuy = "Make", leadTimeDays = 2, lotTracked = true });
        await plant.Ok(engineer, "/api/engineering/items/REF-STRIP/revisions", new { revision = "A" });
        await plant.Ok(engineer, "/api/engineering/items/REF-STRIP/revisions/A/bom", new { lineNumber = 1, componentPartNumber = "REF-COIL", quantityPer = 1m, scrapPercent = 0m });
        await plant.Ok(engineer, "/api/engineering/items/REF-STRIP/revisions/A/routing", new { sequence = 10, description = "Reference slitting", workCenterCode = "SLIT-WC", activityCode = "RUN_SLITTER", eligibleMachines = new[] { "SLITTER-A", "SLITTER-B" }, setupMinutes = 0m, runMinutesPerUnit = 0.01m, crewSize = 1 });
        await plant.Ok(plant.Approver, "/api/engineering/items/REF-STRIP/revisions/A/release", new { reason = "Reference transformation definition" });
        var transformedOrder = Id(await plant.Ok(engineer, "/api/orders/", new { partNumber = "REF-STRIP", quantity = 100m, dueDate = Day(15), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{transformedOrder}/release", new { reason = "Reference shift work" });
        var sourceLot = $"RF-{kind.ToUpperInvariant()}-001";
        var remnantLot = sourceLot + "-R";
        await plant.Ok(stores, "/api/inventory/issues", new { orderNumber = transformedOrder, partNumber = "REF-COIL", quantity = 100m, locationCode = "STORES", lotNumber = sourceLot });
        await plant.Ok(stores, "/api/inventory/transformations", new
        {
            orderNumber = transformedOrder,
            entries = new object[]
            {
                new { role = "Consumed", partNumber = "REF-COIL", lotNumber = sourceLot, quantity = 80m },
                new { role = "Scrap", partNumber = "REF-COIL", lotNumber = sourceLot, quantity = 5m },
                new { role = "Remnant", partNumber = "REF-COIL", lotNumber = remnantLot, quantity = 15m, sourceLot, locationCode = "STORES" },
            },
            reason = "Reference slitting: measured output, edge scrap and reusable remnant",
        });

        await plant.Person("reference-kiosk", "ShopFloorTerminal");
        var kiosk = plant.As("reference-kiosk");
        const string badge = "RF-SARAH-327";
        await plant.Ok(engineer, "/api/resources/shifts", new { code = "REF-ALL", siteCode = "PLANT1", name = "Reference full-day shift", start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 } });
        await plant.Ok(engineer, "/api/resources/work-centers/SLIT-WC/shifts", new { shiftCode = "REF-ALL" });
        foreach (var subject in new[] { "sarah", "michael" })
        {
            await plant.Ok(engineer, $"/api/workforce/workers/{plant.People[subject]}/shift", new { shiftCode = "REF-ALL" });
        }
        await plant.Ok(plant.Admin, $"/api/workforce/people/{plant.People["sarah"]}/badge", new { badge });
        await plant.Ok(kiosk, "/api/shopfloor/clock-in", new { badge });
        await plant.Ok(kiosk, "/api/shopfloor/start", new { badge, orderNumber = transformedOrder, sequence = 10, machineCode = "SLITTER-A" });
        await plant.Ok(kiosk, "/api/shopfloor/report", new { badge, orderNumber = transformedOrder, sequence = 10, good = 80m, scrap = 5m, scrapReason = "Measured edge trim", complete = false });
        await plant.Ok(kiosk, "/api/shopfloor/stop", new { badge });
        var productionDay = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        await plant.Person("reference-buyer", "Buyer");
        await plant.Person("reference-manager", "PurchasingManager");
        var buyer = plant.As("reference-buyer");
        await plant.Ok(engineer, "/api/engineering/items", new { partNumber = "REF-SERVICE", description = "Reference outside processing", unitOfMeasure = "EA", makeBuy = "Buy", leadTimeDays = 7, @class = "Service" });
        await plant.Ok(buyer, "/api/procurement/suppliers", new { code = "REF-PROCESSOR", name = "Reference processor" });
        var serviceOrder = Id(await plant.Ok(buyer, "/api/procurement/purchase-orders", new { supplier = "REF-PROCESSOR", lines = new[] { new { partNumber = "REF-SERVICE", quantity = 5m, promisedDate = Day(7) } }, reason = "Reference service purchase" }));
        await plant.Ok(plant.As("reference-manager"), $"/api/procurement/purchase-orders/{serviceOrder}/approve", new { reason = "Approved reference service" });
        await plant.Ok(stores, "/api/inventory/locations", new { code = "REF-EXTERNAL", siteCode = "PLANT1", name = "Reference processor dock", kind = "ExternalProcessor", custodianSupplier = "REF-PROCESSOR" });
        var processorLot = $"RF-{kind.ToUpperInvariant()}-002";
        await plant.Ok(stores, "/api/inventory/outside-processing/dispatch", new { partNumber = "REF-COIL", quantity = 5m, fromLocation = "STORES", processorLocation = "REF-EXTERNAL", lotNumber = processorLot, purchaseOrder = serviceOrder, purchaseOrderLine = 1, reason = "Send identified material for reference processing" });

        var factory = await OriginalReferenceFactory.BuildAsync(plant, planner, kind, clock.GetUtcNow());
        await factory.VerifyPopulationsAsync(app);

        // A machine-only disruption preserves feasible work through the other eligible slitter.
        var beforeDisruption = await planner.GetFromJsonAsync<JsonElement[]>("/api/readiness/operations");
        var beforeOperation = (beforeDisruption ?? []).Single(operation => operation.GetProperty("orderNumber").GetInt64() == firstReleasedOrder && operation.GetProperty("sequence").GetInt32() == 10);
        Assert.True(beforeOperation.GetProperty("executable").GetBoolean());
        Assert.Equal("SLITTER-A", beforeOperation.GetProperty("machine").GetString());
        if (kind == "constrained")
        {
            await plant.Ok(engineer, "/api/holds", new
            {
                kind = "Production",
                targetType = "Machine",
                targetId = "SLITTER-A",
                source = "V3.27 injected reference disruption",
                reason = "Exercise a constrained but valid factory",
            });
            var afterDisruption = await planner.GetFromJsonAsync<JsonElement[]>("/api/readiness/operations");
            var afterOperation = (afterDisruption ?? []).Single(operation => operation.GetProperty("orderNumber").GetInt64() == firstReleasedOrder && operation.GetProperty("sequence").GetInt32() == 10);
            Assert.True(afterOperation.GetProperty("executable").GetBoolean());
            Assert.Equal("SLITTER-B", afterOperation.GetProperty("machine").GetString());
        }

        var orders = await planner.GetFromJsonAsync<JsonElement[]>("/api/orders/");
        Assert.NotNull(orders);
        Assert.True(orders!.Length >= 100, $"{kind}: expected 100 orders, found {orders.Length}.");
        if (kind == "unusual-valid")
        {
            Assert.Contains(orders, order => order.GetProperty("quantity").GetDecimal() == 1_000_000m && order.GetProperty("status").GetString() == "Planned");
            Assert.Contains(orders, order => order.GetProperty("quantity").GetDecimal() == 0.001m);
        }
        var stock = await stores.GetFromJsonAsync<JsonElement[]>("/api/inventory/stock?part=REF-COIL");
        Assert.NotNull(stock);
        Assert.True(stock!.Length >= 100, $"{kind}: expected 100 traceable stock positions, found {stock.Length}.");

        await plant.Ok(kiosk, "/api/shopfloor/clock-out", new { badge });
        clock.Advance(TimeSpan.FromDays(1));
        await factory.VerifyUniversalQuestionsAsync(plant, planner, kind, clock.GetUtcNow(), clock.Advance);
        var outside = await AskWithinBudget(planner, kind, "What material is at outside processors?", "stock-positions");
        var outsideRow = Assert.Single(outside.GetProperty("rows").EnumerateArray());
        Assert.Equal(("REF-COIL", processorLot, "REF-EXTERNAL", "Outside processor REF-PROCESSOR", 5m),
            (Cell(outside, outsideRow, "part").GetString(), Cell(outside, outsideRow, "lot").GetString(), Cell(outside, outsideRow, "location").GetString(), Cell(outside, outsideRow, "custody").GetString(), Cell(outside, outsideRow, "quantity").GetDecimal()));
        Assert.Contains("item:REF-COIL", outsideRow.GetProperty("trace").EnumerateArray().Select(reference => reference.GetString()));

        var material = await AskWithinBudget(planner, kind, $"What became of the material issued to order {transformedOrder}?", "material-reconciliation");
        var materialRow = Assert.Single(material.GetProperty("rows").EnumerateArray());
        Assert.Equal((transformedOrder.ToString(CultureInfo.InvariantCulture), sourceLot, 100m, 80m, 5m, 15m, 0m, "Balanced"),
            (Cell(material, materialRow, "order").GetString(), Cell(material, materialRow, "lot").GetString(), Cell(material, materialRow, "issued").GetDecimal(), Cell(material, materialRow, "consumed").GetDecimal(), Cell(material, materialRow, "scrap").GetDecimal(), Cell(material, materialRow, "remnant").GetDecimal(), Cell(material, materialRow, "unexplained").GetDecimal(), Cell(material, materialRow, "status").GetString()));
        Assert.Contains($"order:{transformedOrder}", materialRow.GetProperty("trace").EnumerateArray().Select(reference => reference.GetString()));

        var shift = await AskWithinBudget(planner, kind, "What happened last shift?", "production-output");
        var shiftRow = Assert.Single(shift.GetProperty("rows").EnumerateArray());
        Assert.Equal((transformedOrder.ToString(CultureInfo.InvariantCulture), "REF-ALL", 80m, 5m),
            (Cell(shift, shiftRow, "order").GetString(), Cell(shift, shiftRow, "shift").GetString(), Cell(shift, shiftRow, "good").GetDecimal(), Cell(shift, shiftRow, "scrap").GetDecimal()));
        Assert.Equal(productionDay, shift.GetProperty("interpretation").GetProperty("report").GetProperty("parameters").GetProperty("from").GetString());
        Assert.Contains(shiftRow.GetProperty("trace").EnumerateArray(), reference => reference.GetString()!.StartsWith("report:", StringComparison.Ordinal));

        var remnants = await AskWithinBudget(planner, kind, "Show remnants", "material-reconciliation");
        var remnantRow = Assert.Single(remnants.GetProperty("rows").EnumerateArray());
        Assert.Equal((sourceLot, 15m), (Cell(remnants, remnantRow, "lot").GetString(), Cell(remnants, remnantRow, "remnant").GetDecimal()));
        var lots = await stores.GetFromJsonAsync<JsonElement[]>("/api/inventory/stock?part=REF-COIL");
        Assert.Contains(lots ?? [], row => row.GetProperty("lotNumber").GetString() == remnantLot && row.GetProperty("quantity").GetDecimal() == 15m);

        var integrity = await planner.GetFromJsonAsync<JsonElement[]>("/api/integrity/exceptions");
        Assert.Empty(integrity ?? []);

        if (kind == "constrained")
        {
            var holds = await planner.GetFromJsonAsync<JsonElement[]>("/api/holds");
            Assert.Contains(holds ?? [], h => h.GetProperty("source").GetString() == "V3.27 injected reference disruption");
        }
    }

    [Fact]
    public async Task Consigned_fixture_repair_and_temporary_use_reuses_tool_and_service_history_with_an_explicit_limit()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        var engineer = plant.Engineer;
        var stores = plant.As("stores");

        await plant.Ok(engineer, "/api/resources/tool-types", new { code = "CUSTOMER-FIXTURE", name = "Customer fixture", kind = "Fixture", serviceUses = 20 });
        await plant.Ok(engineer, "/api/resources/tools", new { code = "CF-100", typeCode = "CUSTOMER-FIXTURE", description = "Acme customer-owned alignment fixture", serial = "ACME-771", location = "CUSTOMER-DOCK" });
        await plant.Ok(stores, "/api/resources/tools/CF-100/move", new { custody = "WithPerson", custodian = "Acme Customer", location = "CUSTOMER-DOCK", reason = "Customer loan LOAN-17 is logged" });
        await plant.Ok(stores, "/api/resources/tools/CF-100/move", new { custody = "InHouse", custodian = (string?)null, location = "TOOL-CRIB", reason = "Fixture received for repair and temporary use under LOAN-17" });
        await plant.Ok(stores, "/api/resources/tools/CF-100/service", new { vendor = "Acme Repair Center", reason = "Repair under customer loan authorization LOAN-17" });
        await plant.Ok(stores, "/api/resources/tools/CF-100/return", new { condition = "Good", location = "TOOL-CRIB" });
        await plant.Ok(engineer, "/api/resources/tools/CF-100/uses", new { uses = 1, machineCode = "SLITTER-A", activityCode = "RUN_SLITTER", reference = "Temporary customer-authorized fit check LOAN-17" });
        await plant.Ok(stores, "/api/resources/tools/CF-100/move", new { custody = "WithPerson", custodian = "Acme Customer", location = "CUSTOMER-DOCK", reason = "Return the fixture after the authorized fit check" });

        var tool = (await plant.As("stores").GetFromJsonAsync<JsonElement[]>("/api/resources/tools"))!.Single(t => t.GetProperty("tool").GetProperty("code").GetString() == "CF-100");
        Assert.Equal(("ACME-771", "WithPerson", "Available"), (
            tool.GetProperty("tool").GetProperty("serial").GetString(),
            tool.GetProperty("tool").GetProperty("custody").GetString(),
            tool.GetProperty("tool").GetProperty("status").GetString()));
        var history = await plant.As("stores").GetFromJsonAsync<JsonElement[]>("/api/resources/tools/CF-100/history");
        Assert.Equal("Registered,Moved,Moved,SentForService,Returned,Used,Moved", string.Join(',', (history ?? []).Select(e => e.GetProperty("kind").GetString())));
        Assert.DoesNotContain("owner", tool.GetProperty("tool").EnumerateObject().Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
    }
}
