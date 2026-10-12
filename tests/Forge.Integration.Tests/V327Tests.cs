using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace Forge.Integration.Tests;

/// <summary>V3.27 scale and extension gates: three large but valid factory variants reuse the live Forge services.</summary>
[Collection(PostgresCollection.Name)]
public sealed class V327Tests(PostgresFixture postgres)
{
    private static string Day(int days) => DateTime.UtcNow.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("healthy")]
    [InlineData("constrained")]
    [InlineData("unusual-valid")]
    public async Task Reference_factories_at_scale_answer_shared_questions_and_keep_integrity(string kind)
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var engineer = plant.Engineer;
        var stores = plant.As("stores");

        await plant.Ok(engineer, "/api/engineering/items", new { partNumber = "REF-COIL", description = "Reference lot-tracked coil", unitOfMeasure = "KG", makeBuy = "Buy", leadTimeDays = 7, lotTracked = true, serialized = false });
        await plant.Ok(engineer, "/api/engineering/items/REF-COIL/revisions", new { revision = "A" });
        await plant.Ok(plant.Approver, "/api/engineering/items/REF-COIL/revisions/A/release", new { reason = "V3.27 reference factory baseline" });

        // Two hundred explicit business records per variant, created through the same commands as the UI.
        // Ten orders are released so schedule/readiness questions exercise actual projections.
        for (var index = 1; index <= 100; index++)
        {
            var quantity = kind == "unusual-valid" && index % 10 == 0 ? 0.001m : index == 100 && kind == "unusual-valid" ? 1_000_000m : 10m + index;
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
        }

        var orders = await planner.GetFromJsonAsync<JsonElement[]>("/api/orders/");
        Assert.NotNull(orders);
        Assert.True(orders!.Length >= 100, $"{kind}: expected 100 orders, found {orders.Length}.");
        var stock = await stores.GetFromJsonAsync<JsonElement[]>("/api/inventory/stock?part=REF-COIL");
        Assert.NotNull(stock);
        Assert.True(stock!.Length >= 100, $"{kind}: expected 100 traceable stock positions, found {stock.Length}.");

        foreach (var (question, reportId) in new[]
        {
            ("What material is at outside processors?", "stock-positions"),
            ("What became of the material issued to order 10001?", "material-reconciliation"),
            ("What happened last shift?", "production-output"),
            ("Show remnants", "material-reconciliation"),
        })
        {
            var response = await planner.PostAsJsonAsync("/api/analyst/ask", new { question });
            var json = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{kind}: {question}: {json}");
            using var answer = JsonDocument.Parse(json);
            Assert.False(answer.RootElement.GetProperty("notAuthorized").GetBoolean(), $"{kind}: {question}");
            Assert.Equal(reportId, answer.RootElement.GetProperty("interpretation").GetProperty("report").GetProperty("reportId").GetString());
        }

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
