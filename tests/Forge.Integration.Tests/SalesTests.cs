using System.Globalization;
using System.Net;
using System.Text.Json;
using Forge.Kernel.Postgres;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Integration.Tests;

/// <summary>
/// Quote to cash through the real API (owner decisions 2026-10-31): a Sales Representative keeps the customer and drafts the quote and
/// order, a Sales Manager who did not write them approves them, the approved order becomes customer-order demand, a Billing Clerk
/// invoices only confirmed shipments at the customer's tax rate and terms, and the accounting export balances.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SalesTests(PostgresFixture postgres)
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

    private static async Task Forbidden(SlittingPlant.Plant plant, HttpClient client, string url, object body) =>
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(client, url, body)).StatusCode);

    private static JsonElement Order(JsonElement orders, long number) =>
        orders.EnumerateArray().Single(o => o.GetProperty("orderNumber").GetInt64() == number);

    private static JsonElement? Demand(JsonElement demand, string source) =>
        demand.EnumerateArray().Where(d => d.GetProperty("sourceKey").ValueKind == JsonValueKind.String && d.GetProperty("sourceKey").GetString() == source).Cast<JsonElement?>().SingleOrDefault();

    [Fact]
    public async Task A_quote_becomes_an_approved_order_its_demand_ships_and_only_shipped_quantities_are_invoiced_and_exported()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        foreach (var (subject, roles) in new[] { ("rep", new[] { "SalesRepresentative" }), ("mgr", new[] { "SalesManager" }), ("both", new[] { "SalesRepresentative", "SalesManager" }),
            ("bill", new[] { "BillingClerk" }), ("cost", new[] { "CostAccountant" }), ("ship", new[] { "Shipper" }) })
            await plant.Person(subject, roles);
        var (rep, mgr, both, bill, cost, ship, stores) = (plant.As("rep"), plant.As("mgr"), plant.As("both"), plant.As("bill"), plant.As("cost"), plant.As("ship"), plant.As("stores"));
        async Task Propagate()
        {
            var relay = app.Services.GetRequiredService<OutboxRelay>();
            while (await relay.RelayBatchAsync(100, CancellationToken.None) > 0)
            {
            }

            await app.Services.GetRequiredService<SubscriptionPump>().PumpOnceAsync(1_000_000, CancellationToken.None);
        }

        // Customers: the representative adds them; a tax-exempt customer pays no tax; only a manager puts one on hold.
        await Rejected(plant, rep, "/api/sales/customers", new { code = "BAD", name = "Bad tax", paymentTermsDays = 30, taxRatePercent = 5m, taxExemptCertificate = "EX-1" }, "exempt_with_rate");
        await Forbidden(plant, bill, "/api/sales/customers", new { code = "ACME", name = "Acme Corp", paymentTermsDays = 30, taxRatePercent = 6.25m });
        await plant.Ok(rep, "/api/sales/customers", new { code = "acme", name = "Acme Corp", billTo = "1 Main St", paymentTermsDays = 30, taxRatePercent = 6.25m, currency = "USD" });
        await plant.Ok(rep, "/api/sales/customers", new { code = "GOV", name = "Gov Depot", paymentTermsDays = 45, taxRatePercent = 0m, taxExemptCertificate = "RESALE-77" });
        await Rejected(plant, rep, "/api/sales/customers", new { code = "ACME2", name = "ACME CORP", paymentTermsDays = 30, taxRatePercent = 0m }, "customer_name_exists");
        await Forbidden(plant, rep, "/api/sales/customers/GOV/status", new { status = "OnHold", reason = "Credit review" });
        await plant.Ok(mgr, "/api/sales/customers/GOV/status", new { status = "OnHold", reason = "Credit review" });
        await Rejected(plant, rep, "/api/sales/quotes", new { customer = "GOV", validUntil = Day(30), lines = new[] { new { partNumber = "ROLL-100", quantity = 1m, unitPrice = 1m, leadTimeDays = 1 } } }, "customer_not_active");

        // Quotes: the author never approves their own, even holding both roles.
        var own = Id(await plant.Ok(both, "/api/sales/quotes", new { customer = "ACME", validUntil = Day(30), lines = new[] { new { partNumber = "ROLL-100", quantity = 2m, unitPrice = 20m, leadTimeDays = 3 } } }));
        await Rejected(plant, both, $"/api/sales/quotes/{own}/approve", new { }, "separation_of_duties");
        var quote = Id(await plant.Ok(rep, "/api/sales/quotes", new { customer = "ACME", validUntil = Day(30), lines = new[] { new { partNumber = "ROLL-100", quantity = 6m, unitPrice = 25.5m, leadTimeDays = 5 } } }));
        await Forbidden(plant, rep, $"/api/sales/quotes/{quote}/approve", new { });
        await Rejected(plant, rep, $"/api/sales/quotes/{quote}/order", new { customerPo = "PO-77" }, "quote_not_approved");
        await plant.Ok(mgr, $"/api/sales/quotes/{quote}/approve", new { });
        var order = Id(await plant.Ok(rep, $"/api/sales/quotes/{quote}/order", new { customerPo = "PO-77" }));
        var quoted = (await Get(rep, "/api/sales/quotes")).EnumerateArray().Single(q => q.GetProperty("quote").GetProperty("quoteNumber").GetInt64() == quote).GetProperty("quote");
        Assert.Equal(("Accepted", order), (quoted.GetProperty("status").GetString(), quoted.GetProperty("salesOrderNumber").GetInt64()));
        await Rejected(plant, rep, "/api/sales/orders", new { customer = "ACME", customerPo = "po-77", lines = new[] { new { partNumber = "ROLL-100", quantity = 1m, unitPrice = 1m, dueDate = Day(5) } } }, "duplicate_customer_po");

        // A draft order is not planned; approval by someone else makes its line customer-order demand.
        await Propagate();
        var source = string.Create(CultureInfo.InvariantCulture, $"sales:{order}/1");
        Assert.Null(Demand(await Get(planner, "/api/planning/demand"), source));
        await plant.Ok(mgr, $"/api/sales/orders/{order}/approve", new { });
        await Propagate();
        var demand = Demand(await Get(planner, "/api/planning/demand"), source) ?? throw new InvalidOperationException("No demand was planned for the approved order.");
        Assert.Equal(("CustomerOrder", "Acme Corp", 6m, Day(5)), (demand.GetProperty("kind").GetString(), demand.GetProperty("customer").GetString(), demand.GetProperty("quantity").GetDecimal(), demand.GetProperty("needDate").GetString()));
        var demandId = demand.GetProperty("demandId").GetInt64();
        await Rejected(plant, planner, $"/api/planning/demand/{demandId}/change", new { quantity = 9m, needDate = Day(9), priority = 3, reason = "Planner edit" }, "use_sales_order");
        await Rejected(plant, planner, "/api/planning/demand", new { partNumber = "ROLL-100", quantity = 1m, needDate = Day(5), priority = 3, kind = "CustomerOrder", sourceKey = $"sales:{order}/2" }, "use_sales_order");

        // Moving the line's date moves its demand.
        await plant.Ok(rep, $"/api/sales/orders/{order}/lines/1/date", new { dueDate = Day(8), reason = "Customer asked for later delivery" });
        await Propagate();
        Assert.Equal(Day(8), Demand(await Get(planner, "/api/planning/demand"), source)!.Value.GetProperty("needDate").GetString());

        // Ship four of the six against the order's demand.
        await plant.Ok(stores, "/api/inventory/locations", new { code = "FG", siteCode = "PLANT1", name = "Finished goods", kind = "Stores" });
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "ROLL-100", quantity = 10m, locationCode = "FG", reason = "Opening balance" });
        await plant.Ok(cost, "/api/costing/standard-costs", new { partNumber = "ROLL-100", material = 10m, labor = 2m, overhead = 3m, reason = "FY2026 standard" });
        var shipment = Id(await plant.Ok(stores, "/api/inventory/shipments", new { demandId, lines = new[] { new { partNumber = "ROLL-100", quantity = 4m, locationCode = "FG" } } }));
        await plant.Ok(stores, $"/api/inventory/shipments/{shipment}/pack", new { });
        var unshipped = Id(await plant.Ok(stores, "/api/inventory/shipments", new { demandId, lines = new[] { new { partNumber = "ROLL-100", quantity = 1m, locationCode = "FG" } } }));

        // Nothing has shipped yet, so there is nothing to invoice.
        await Rejected(plant, bill, "/api/sales/invoices", new { orderNumber = order, shipmentIds = new[] { shipment } }, "not_shipped");
        await plant.Ok(ship, $"/api/inventory/shipments/{shipment}/ship", new { carrier = "UPS", tracking = "1Z-SALES" });

        var view = Order(await Get(bill, "/api/sales/orders"), order);
        var line = view.GetProperty("lines")[0];
        Assert.Equal((4m, 0m, 2m), (line.GetProperty("shipped").GetDecimal(), line.GetProperty("invoiced").GetDecimal(), line.GetProperty("remaining").GetDecimal()));
        Assert.Equal(shipment, Assert.Single(view.GetProperty("uninvoicedShipments").EnumerateArray()).GetProperty("shipmentId").GetInt64());
        await Rejected(plant, rep, $"/api/sales/orders/{order}/cancel", new { reason = "Customer cancelled" }, "order_shipped");
        await Rejected(plant, rep, $"/api/sales/orders/{order}/lines/1/cancel", new { reason = "Customer cancelled" }, "line_shipped");

        // Only a billing clerk invoices, only shipped quantities, and each shipment once: 4 × 25.50 = 102.00, tax 6.25 % = 6.38, net 30.
        await Forbidden(plant, rep, "/api/sales/invoices", new { orderNumber = order, shipmentIds = new[] { shipment } });
        await Rejected(plant, bill, "/api/sales/invoices", new { orderNumber = order, shipmentIds = new[] { unshipped } }, "not_shipped");
        var invoice = Id(await plant.Ok(bill, "/api/sales/invoices", new { orderNumber = order, shipmentIds = new[] { shipment } }));
        await Rejected(plant, bill, "/api/sales/invoices", new { orderNumber = order, shipmentIds = new[] { shipment } }, "already_invoiced");
        var issued = (await Get(bill, "/api/sales/invoices")).EnumerateArray().Single(i => i.GetProperty("invoiceNumber").GetInt64() == invoice);
        Assert.Equal((102m, 6.38m, 108.38m), (issued.GetProperty("subtotal").GetDecimal(), issued.GetProperty("tax").GetDecimal(), issued.GetProperty("total").GetDecimal()));
        Assert.Equal(DateOnly.Parse(issued.GetProperty("invoiceDate").GetString()!, CultureInfo.InvariantCulture).AddDays(30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), issued.GetProperty("dueDate").GetString());
        Assert.Equal(4m, Order(await Get(bill, "/api/sales/orders"), order).GetProperty("lines")[0].GetProperty("invoiced").GetDecimal());

        // A void needs a reason, keeps the number and frees the shipment to be invoiced again.
        Assert.Contains("reason", await (await plant.Try(bill, $"/api/sales/invoices/{invoice}/void", new { })).Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        await plant.Ok(bill, $"/api/sales/invoices/{invoice}/void", new { reason = "Wrong customer PO on the invoice" });
        var reissued = Id(await plant.Ok(bill, "/api/sales/invoices", new { orderNumber = order, shipmentIds = new[] { shipment } }));
        Assert.NotEqual(invoice, reissued);

        // The export: the billing clerk sees the sales journal only; the cost accountant also gets inventory at standard cost.
        var period = $"from={Day(-1)}&to={Day(1)}";
        var billing = await Get(bill, $"/api/accounting/export?{period}");
        Assert.Equal((2, 1, true, false), (billing.GetProperty("invoices").GetInt32(), billing.GetProperty("voidedInPeriod").GetInt32(),
            billing.GetProperty("salesJournalBalances").GetBoolean(), billing.GetProperty("inventoryIncluded").GetBoolean()));
        Assert.Equal(HttpStatusCode.Forbidden, (await bill.GetAsync(new Uri($"/api/accounting/export/inventory-journal?{period}", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rep.GetAsync(new Uri($"/api/accounting/export?{period}", UriKind.Relative))).StatusCode);
        var journal = await (await bill.GetAsync(new Uri($"/api/accounting/export/sales-journal?{period}", UriKind.Relative))).Content.ReadAsStringAsync();
        Assert.StartsWith("Date,Entry,Account,AccountName,Debit,Credit", journal, StringComparison.Ordinal);
        Assert.Contains($"INV-{invoice},1200,Accounts receivable,108.38,0.00,USD", journal, StringComparison.Ordinal);
        Assert.Contains($"INV-{invoice}-VOID,1200,Accounts receivable,0.00,108.38,USD", journal, StringComparison.Ordinal);
        Assert.Contains($"INV-{reissued},2300,Sales tax payable,0.00,6.38,USD", journal, StringComparison.Ordinal);

        var costed = await Get(cost, $"/api/accounting/export?{period}");
        Assert.True(costed.GetProperty("inventoryIncluded").GetBoolean());
        Assert.True(costed.GetProperty("inventoryJournalBalances").GetBoolean());
        var shipped = costed.GetProperty("inventoryKinds").EnumerateArray().Single(k => k.GetProperty("kind").GetString() == "Shipment");
        Assert.Equal((1, 60m), (shipped.GetProperty("valued").GetInt32(), shipped.GetProperty("value").GetDecimal()));
        var inventory = await (await cost.GetAsync(new Uri($"/api/accounting/export/inventory-journal?{period}", UriKind.Relative))).Content.ReadAsStringAsync();
        Assert.Contains(",5000,Cost of goods sold,60.00,0.00,USD,Shipment,ROLL-100,4,", inventory, StringComparison.Ordinal);

        // Closing the order closes its demand; shipped work can still be invoiced afterwards.
        await plant.Ok(rep, $"/api/sales/orders/{order}/close", new { reason = "Customer took four" });
        await Propagate();
        Assert.Equal("Closed", Demand(await Get(planner, "/api/planning/demand?includeClosed=true"), source)!.Value.GetProperty("status").GetString());
    }
}
