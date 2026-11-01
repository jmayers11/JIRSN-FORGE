using System.Globalization;
using System.Net;
using System.Text.Json;
using Forge.Kernel.Postgres;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Integration.Tests;

/// <summary>
/// Stock reservations and promise dates through the real API (owner decision 2026-10-31): releasing a production order reserves its
/// material and issues draw it down; approving a sales order reserves finished stock for its demand, others cannot ship it, an inventory
/// controller can release a reservation with a reason, and the promise date counts only free stock and open supply.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class StockReservationTests(PostgresFixture postgres)
{
    private static string Day(int fromToday) => DateTime.UtcNow.AddDays(fromToday).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

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
    public async Task Released_orders_and_approved_sales_orders_hold_stock_others_cannot_take_it_and_promises_count_free_stock()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        foreach (var (subject, roles) in new[] { ("rep", new[] { "SalesRepresentative" }), ("mgr", new[] { "SalesManager" }), ("ship", new[] { "Shipper" }) })
            await plant.Person(subject, roles);
        var (rep, mgr, stores, e) = (plant.As("rep"), plant.As("mgr"), plant.As("stores"), plant.Engineer);
        async Task Propagate()
        {
            var relay = app.Services.GetRequiredService<OutboxRelay>();
            while (await relay.RelayBatchAsync(100, CancellationToken.None) > 0)
            {
            }

            await app.Services.GetRequiredService<SubscriptionPump>().PumpOnceAsync(1_000_000, CancellationToken.None);
            while (await relay.RelayBatchAsync(100, CancellationToken.None) > 0)
            {
            }

            await app.Services.GetRequiredService<SubscriptionPump>().PumpOnceAsync(1_000_000, CancellationToken.None);
        }

        // Releasing an order reserves its coil; issuing draws the reservation down.
        var order = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "ROLL-100", quantity = 100m, dueDate = Day(10), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Customer order" });
        await Propagate();
        var required = (await Get(planner, $"/api/orders/{order}")).GetProperty("materials").EnumerateArray()
            .Where(m => m.GetProperty("componentPartNumber").GetString() == "COIL-AL").Sum(m => m.GetProperty("requiredQuantity").GetDecimal());
        var held = Assert.Single((await Get(stores, $"/api/inventory/reservations?order={order}")).EnumerateArray());
        Assert.Equal((required, required), (held.GetProperty("reservation").GetProperty("quantity").GetDecimal(), held.GetProperty("outstanding").GetDecimal()));
        await plant.Ok(stores, "/api/inventory/issues", new { orderNumber = order, partNumber = "COIL-AL", quantity = 50m, locationCode = "STORES" });
        Assert.Equal(required - 50m, Assert.Single((await Get(stores, $"/api/inventory/reservations?order={order}")).EnumerateArray()).GetProperty("outstanding").GetDecimal());

        // Finished stock: an approved sales order holds 6 of the 10; nobody else may ship more than the free 4.
        await plant.Ok(stores, "/api/inventory/locations", new { code = "FG", siteCode = "PLANT1", name = "Finished goods", kind = "Stores" });
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "ROLL-100", quantity = 10m, locationCode = "FG", reason = "Opening balance" });
        var before = await Get(rep, "/api/sales/promise?part=ROLL-100&quantity=8");
        Assert.Equal((Day(0), 10m), (before.GetProperty("earliest").GetString(), before.GetProperty("freeNow").GetDecimal()));
        await plant.Ok(rep, "/api/sales/customers", new { code = "ACME", name = "Acme Corp", paymentTermsDays = 30, taxRatePercent = 0m });
        var so = Id(await plant.Ok(rep, "/api/sales/orders", new { customer = "ACME", customerPo = "PO-1", lines = new[] { new { partNumber = "ROLL-100", quantity = 6m, unitPrice = 25m, dueDate = Day(5) } } }));
        await plant.Ok(mgr, $"/api/sales/orders/{so}/approve", new { });
        await Propagate();
        var reserved = Assert.Single((await Get(stores, "/api/inventory/reservations?part=ROLL-100")).EnumerateArray());
        Assert.Equal(("Demand", 6m), (reserved.GetProperty("reservation").GetProperty("target").GetString(), reserved.GetProperty("outstanding").GetDecimal()));
        var reservationId = reserved.GetProperty("reservation").GetProperty("reservationId").GetInt64();
        var after = await Get(rep, "/api/sales/promise?part=ROLL-100&quantity=8");
        Assert.Equal(4m, after.GetProperty("freeNow").GetDecimal());
        Assert.NotEqual(Day(0), after.GetProperty("earliest").GetString());

        var other = Id(await plant.Ok(stores, "/api/inventory/shipments", new { customer = "Walk-in", lines = new[] { new { partNumber = "ROLL-100", quantity = 5m, locationCode = "FG" } } }));
        await Rejected(plant, stores, $"/api/inventory/shipments/{other}/pack", new { }, "reserved_for_other");
        var small = Id(await plant.Ok(stores, "/api/inventory/shipments", new { customer = "Walk-in", lines = new[] { new { partNumber = "ROLL-100", quantity = 4m, locationCode = "FG" } } }));
        await plant.Ok(stores, $"/api/inventory/shipments/{small}/pack", new { });

        // An inventory controller releases the reservation with a reason; then the stock is free.
        Assert.Contains("reason", await (await plant.Try(stores, $"/api/inventory/reservations/{reservationId}/release", new { })).Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(rep, $"/api/inventory/reservations/{reservationId}/release", new { reason = "Mine" })).StatusCode);
        await plant.Ok(stores, $"/api/inventory/reservations/{reservationId}/release", new { reason = "Walk-in customer has priority today" });
        await plant.Ok(stores, $"/api/inventory/shipments/{small}/cancel", new { reason = "Re-pack together" });
        await plant.Ok(stores, $"/api/inventory/shipments/{other}/pack", new { });
        Assert.Empty((await Get(stores, "/api/inventory/reservations?part=ROLL-100")).EnumerateArray());

        // Cancelling the production order releases what it still held.
        await plant.Ok(planner, $"/api/orders/{order}/cancel", new { reason = "Customer cancelled" });
        await Propagate();
        Assert.Empty((await Get(stores, $"/api/inventory/reservations?order={order}")).EnumerateArray());
    }
}
