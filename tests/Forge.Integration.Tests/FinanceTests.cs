using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Forge.Host.Shipping;
using Forge.Kernel.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Forge.Integration.Tests;

/// <summary>
/// Owner decisions 2026-11-01 through the real host: features a company can turn off; automatic material deduction (backflush);
/// customer payments and credit holds; supplier invoices with the three-way match; shipping carriers (against a simulator).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FinanceTests(PostgresFixture postgres)
{
    private const string Sarah = "B-SARAH-FLUSH";

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }

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

    private static async Task PropagateAsync(ForgeApp app)
    {
        var relay = app.Services.GetRequiredService<OutboxRelay>();
        while (await relay.RelayBatchAsync(100, CancellationToken.None) > 0)
        {
        }

        await app.Services.GetRequiredService<SubscriptionPump>().PumpOnceAsync(1_000_000, CancellationToken.None);
    }

    [Fact]
    public async Task Backflush_deducts_what_an_operation_uses_flags_shortages_and_each_feature_can_be_turned_off()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("kiosk", "ShopFloorTerminal");
        var (e, kiosk, stores, admin) = (plant.Engineer, plant.As("kiosk"), plant.As("stores"), plant.Admin);

        // Every feature starts on; only a security administrator switches them.
        var features = await Get(planner, "/api/site/features");
        Assert.Equal(new[] { "receivables", "supplier-invoices", "shipping-carriers", "backflush" }, features.EnumerateArray().Select(f => f.GetProperty("code").GetString()).ToArray());
        Assert.All(features.EnumerateArray(), f => Assert.True(f.GetProperty("enabled").GetBoolean()));
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(planner, "/api/site/features/backflush", new { enabled = false, reason = "x" })).StatusCode);

        // Inventory sets coil to backflush from stores.
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(planner, "/api/inventory/backflush/settings/COIL-AL", new { enabled = true, locationCode = "STORES", reason = "x" })).StatusCode);
        await Rejected(plant, stores, "/api/inventory/backflush/settings/COIL-AL", new { enabled = true, locationCode = "NOWHERE", reason = "Point of use" }, "unknown_location");
        await plant.Ok(stores, "/api/inventory/backflush/settings/COIL-AL", new { enabled = true, locationCode = "STORES", reason = "Coil is used at the slitter" });

        // Ten rolls reported at the slitting operation (9 good, 1 scrap) deduct the coil they used: 10 × 2.5 kg + 4 % = 26 kg.
        var order = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "ROLL-100", quantity = 10m, dueDate = Day(10), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Customer order" });
        await plant.Ok(e, "/api/execution/reason-codes", new { code = "EDGE", kind = "Scrap", description = "Edge wave" });
        await plant.Ok(e, "/api/resources/shifts", new { code = "ALL", siteCode = "PLANT1", name = "Around the clock", start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 } });
        await plant.Ok(e, "/api/resources/work-centers/SLIT-WC/shifts", new { shiftCode = "ALL" });
        await plant.Ok(e, $"/api/workforce/workers/{plant.People["sarah"]}/shift", new { shiftCode = "ALL" });
        await plant.Ok(admin, $"/api/workforce/people/{plant.People["sarah"]}/badge", new { badge = Sarah });
        await plant.Ok(kiosk, "/api/shopfloor/clock-in", new { badge = Sarah });
        await plant.Ok(kiosk, "/api/shopfloor/start", new { badge = Sarah, orderNumber = order, sequence = 10, machineCode = "SLITTER-A" });
        await plant.Ok(kiosk, "/api/shopfloor/report", new { badge = Sarah, orderNumber = order, sequence = 10, good = 9m, scrap = 1m, scrapReasonCode = "EDGE", complete = true });
        await PropagateAsync(app);
        await PropagateAsync(app); // at least once: a replay deducts nothing more
        var issues = (await Get(stores, $"/api/inventory/transactions?order={order}&part=COIL-AL")).EnumerateArray().Where(t => t.GetProperty("kind").GetString() == "Issue").ToList();
        Assert.True(issues.Count == 1, (await Get(stores, "/api/inventory/backflush/exceptions")).GetRawText());
        Assert.Equal(26m, issues[0].GetProperty("quantity").GetDecimal());
        Assert.Empty((await Get(stores, "/api/inventory/backflush/exceptions")).EnumerateArray());

        // From a location with nothing in it, the next order's report goes on, and inventory controllers are told what was not deducted.
        await plant.Ok(stores, "/api/inventory/locations", new { code = "LINE-1", siteCode = "PLANT1", name = "Slitter point of use", kind = "Stores" });
        await plant.Ok(stores, "/api/inventory/backflush/settings/COIL-AL", new { enabled = true, locationCode = "LINE-1", reason = "Point of use" });
        var second = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "ROLL-100", quantity = 4m, dueDate = Day(10), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{second}/release", new { reason = "Customer order" });
        await plant.Ok(kiosk, "/api/shopfloor/start", new { badge = Sarah, orderNumber = second, sequence = 10, machineCode = "SLITTER-A" });
        await plant.Ok(kiosk, "/api/shopfloor/report", new { badge = Sarah, orderNumber = second, sequence = 10, good = 4m, scrap = 0m, complete = true });
        await PropagateAsync(app);
        await PropagateAsync(app);
        var shortage = Assert.Single((await Get(stores, "/api/inventory/backflush/exceptions")).EnumerateArray());
        Assert.Equal((second, "COIL-AL", 10.4m, 0m), (shortage.GetProperty("orderNumber").GetInt64(), shortage.GetProperty("partNumber").GetString(), shortage.GetProperty("quantity").GetDecimal(),
            shortage.GetProperty("deducted").GetDecimal()));
        Assert.Contains((await Get(stores, "/api/notifications/inbox?openOnly=true")).EnumerateArray(), a => a.GetProperty("title").GetString() == "Backflush short");
        await plant.Ok(stores, "/api/inventory/issues", new { orderNumber = second, partNumber = "COIL-AL", quantity = 10.4m, locationCode = "STORES" });
        await plant.Ok(stores, $"/api/inventory/backflush/exceptions/{shortage.GetProperty("exceptionId").GetInt64()}/resolve", new { note = "Issued from stores by hand" });
        Assert.Empty((await Get(stores, "/api/inventory/backflush/exceptions")).EnumerateArray());

        // A plant that does not backflush turns it off: nothing is deducted and the settings cannot change until it is on again.
        await plant.Ok(admin, "/api/site/features/backflush", new { enabled = false, reason = "We issue everything by hand" });
        Assert.False((await Get(planner, "/api/site/features")).EnumerateArray().Single(f => f.GetProperty("code").GetString() == "backflush").GetProperty("enabled").GetBoolean());
        await Rejected(plant, stores, "/api/inventory/backflush/settings/COIL-AL", new { enabled = false, reason = "x" }, "feature_disabled");
        await plant.Ok(admin, "/api/site/features/backflush", new { enabled = true, reason = "Back on" });
    }

    [Fact]
    public async Task Payments_reduce_balances_and_customers_over_their_limit_or_past_due_wait_for_a_credit_release()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), services => services.AddSingleton<TimeProvider>(clock));
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        foreach (var (subject, roles) in new[] { ("rep", new[] { "SalesRepresentative" }), ("mgr", new[] { "SalesManager" }), ("bill", new[] { "BillingClerk" }), ("credit", new[] { "CreditManager" }),
            ("ship", new[] { "Shipper" }) })
            await plant.Person(subject, roles);
        var (rep, mgr, bill, credit, ship, stores) = (plant.As("rep"), plant.As("mgr"), plant.As("bill"), plant.As("credit"), plant.As("ship"), plant.As("stores"));
        await plant.Ok(rep, "/api/sales/customers", new { code = "ACME", name = "Acme Corp", paymentTermsDays = 30, taxRatePercent = 0m });
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(mgr, "/api/receivables/customers/ACME/credit-limit", new { creditLimit = 200m, reason = "x" })).StatusCode);
        await plant.Ok(credit, "/api/receivables/customers/ACME/credit-limit", new { creditLimit = 200m, reason = "Credit application approved" });

        // 150 on order is within the 200 limit; another 75 would go over, so it waits until a sales manager releases it.
        var first = Id(await plant.Ok(rep, "/api/sales/orders", new { customer = "ACME", customerPo = "PO-1", lines = new[] { new { partNumber = "ROLL-100", quantity = 6m, unitPrice = 25m, dueDate = Day(5) } } }));
        await plant.Ok(mgr, $"/api/sales/orders/{first}/approve", new { });
        var second = Id(await plant.Ok(rep, "/api/sales/orders", new { customer = "ACME", customerPo = "PO-2", lines = new[] { new { partNumber = "ROLL-100", quantity = 3m, unitPrice = 25m, dueDate = Day(5) } } }));
        await Rejected(plant, mgr, $"/api/sales/orders/{second}/approve", new { }, "credit_hold");
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(rep, "/api/receivables/releases", new { customerCode = "ACME", salesOrderNumber = second, reason = "x" })).StatusCode);
        await plant.Ok(mgr, "/api/receivables/releases", new { customerCode = "ACME", salesOrderNumber = second, reason = "Customer prepaid by wire" });
        await plant.Ok(mgr, $"/api/sales/orders/{second}/approve", new { });
        await plant.Ok(credit, "/api/receivables/customers/ACME/credit-limit", new { creditLimit = 300m, reason = "Raised after the prepayment" });

        // Ship and invoice four rolls of the first order (100); a partial payment of 60 is applied to it.
        await PropagateAsync(app);
        var demandId = (await Get(planner, "/api/planning/demand")).EnumerateArray()
            .Single(d => d.GetProperty("sourceKey").ValueKind == JsonValueKind.String && d.GetProperty("sourceKey").GetString() == $"sales:{first}/1").GetProperty("demandId").GetInt64();
        await plant.Ok(stores, "/api/inventory/locations", new { code = "FG", siteCode = "PLANT1", name = "Finished goods", kind = "Stores" });
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "ROLL-100", quantity = 20m, locationCode = "FG", reason = "Opening balance" });
        var shipment = Id(await plant.Ok(stores, "/api/inventory/shipments", new { demandId, lines = new[] { new { partNumber = "ROLL-100", quantity = 4m, locationCode = "FG" } } }));
        await plant.Ok(stores, $"/api/inventory/shipments/{shipment}/pack", new { });
        await plant.Ok(ship, $"/api/inventory/shipments/{shipment}/ship", new { carrier = "UPS", tracking = "1Z-AR" });
        var invoice = Id(await plant.Ok(bill, "/api/sales/invoices", new { orderNumber = first, shipmentIds = new[] { shipment } }));
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(rep, "/api/receivables/payments", new { customerCode = "ACME", receivedOn = Day(-1), amount = 60m, method = "Check" })).StatusCode);
        await plant.Ok(bill, "/api/receivables/payments", new { customerCode = "ACME", receivedOn = Day(-1), amount = 60m, method = "Check", reference = "CHK-1001" });
        var view = await Get(credit, "/api/receivables/?customer=ACME");
        var open = view.GetProperty("openInvoices").EnumerateArray().Single(b => b.GetProperty("invoiceNumber").GetInt64() == invoice);
        Assert.Equal((100m, 60m, 40m), (open.GetProperty("total").GetDecimal(), open.GetProperty("paid").GetDecimal(), open.GetProperty("balance").GetDecimal()));
        Assert.False(view.GetProperty("customers")[0].GetProperty("onHold").GetBoolean());

        // Forty-five days on, the rest is 15 days past due; the plant holds after 10, so the next shipment waits for a release.
        await plant.Ok(credit, "/api/receivables/policy", new { pastDueDays = 10, includeOpenOrders = true, reason = "Tighter terms" });
        var next = Id(await plant.Ok(stores, "/api/inventory/shipments", new { demandId, lines = new[] { new { partNumber = "ROLL-100", quantity = 2m, locationCode = "FG" } } }));
        await plant.Ok(stores, $"/api/inventory/shipments/{next}/pack", new { });
        clock.Advance(TimeSpan.FromDays(45));
        var held = (await Get(credit, "/api/receivables/?customer=ACME")).GetProperty("customers")[0];
        Assert.True(held.GetProperty("onHold").GetBoolean());
        Assert.Contains("past due", held.GetProperty("reasons")[0].GetString(), StringComparison.Ordinal);
        Assert.Contains((await Get(credit, "/api/receivables/")).GetProperty("waiting").EnumerateArray(), w => w.GetProperty("number").GetInt64() == next);
        await Rejected(plant, ship, $"/api/inventory/shipments/{next}/ship", new { carrier = "UPS", tracking = "1Z-NEXT" }, "credit_hold");
        await plant.Ok(credit, "/api/receivables/releases", new { customerCode = "ACME", shipmentId = next, reason = "Payment promised Friday" });
        await plant.Ok(ship, $"/api/inventory/shipments/{next}/ship", new { carrier = "UPS", tracking = "1Z-NEXT" });

        // Paying the balance lifts the hold; the cash receipts export balances.
        await plant.Ok(bill, "/api/receivables/payments", new { customerCode = "ACME", receivedOn = clock.GetUtcNow().AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), amount = 40m, method = "Ach",
            applications = new[] { new { invoiceNumber = invoice, amount = 40m } } });
        Assert.False((await Get(credit, "/api/receivables/?customer=ACME")).GetProperty("customers")[0].GetProperty("onHold").GetBoolean());
        var csv = await (await bill.GetAsync(new Uri($"/api/accounting/export/cash-journal?from={Day(-3)}&to={clock.GetUtcNow().AddDays(1):yyyy-MM-dd}", UriKind.Relative))).Content.ReadAsStringAsync();
        Assert.Contains("Cash", csv, StringComparison.Ordinal);
        var amounts = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(l => l.Split(',')).ToList();
        Assert.Equal(4, amounts.Count);

        // A plant that does not use credit control turns it off: limits no longer hold orders.
        await plant.Ok(plant.Admin, "/api/site/features/receivables", new { enabled = false, reason = "Our accounting system handles credit" });
        await Rejected(plant, bill, "/api/receivables/payments", new { customerCode = "ACME", receivedOn = Day(-1), amount = 1m, method = "Check" }, "feature_disabled");
    }

    [Fact]
    public async Task Supplier_invoices_are_matched_to_the_po_and_receipts_and_exceptions_wait_for_a_purchasing_manager()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        foreach (var (subject, roles) in new[] { ("bob", new[] { "Buyer" }), ("pat", new[] { "PurchasingManager" }), ("ap", new[] { "AccountsPayableClerk" }) })
            await plant.Person(subject, roles);
        var (bob, pat, ap, stores) = (plant.As("bob"), plant.As("pat"), plant.As("ap"), plant.As("stores"));
        await plant.Ok(bob, "/api/procurement/suppliers", new { code = "ALCOA", name = "Alcoa" });
        var po = Id(await plant.Ok(bob, "/api/procurement/purchase-orders", new
        {
            supplier = "ALCOA",
            lines = new[] { new { partNumber = "COIL-AL", quantity = 100m, promisedDate = Day(7), unitPrice = 2.00m }, new { partNumber = "COIL-AL", quantity = 50m, promisedDate = Day(7), unitPrice = 3.00m } },
        }));
        await plant.Ok(pat, $"/api/procurement/purchase-orders/{po}/approve", new { reason = "Budget approved" });
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "COIL-AL", quantity = 100m, locationCode = "STORES", revision = "A", poNumber = po, poLine = 1 });
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "COIL-AL", quantity = 40m, locationCode = "STORES", revision = "A", poNumber = po, poLine = 2 });

        // Within tolerance (0.80 on 320, under 2 % and under 50): approved for payment without anyone deciding.
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(bob, "/api/payables/invoices", new { poNumber = po, supplierInvoiceNumber = "A-1", invoiceDate = Day(-1), freight = 0m, tax = 0m, lines = Array.Empty<object>() })).StatusCode);
        var good = await plant.Ok(ap, "/api/payables/invoices", new
        {
            poNumber = po, supplierInvoiceNumber = "A-100", invoiceDate = Day(-1), freight = 15m, tax = 0m,
            lines = new[] { new { poLine = 1, quantity = 100m, unitPrice = 2.00m }, new { poLine = 2, quantity = 40m, unitPrice = 3.02m } },
        });
        Assert.Contains("approved for payment", good.GetProperty("message").GetString(), StringComparison.Ordinal);
        await Rejected(plant, ap, "/api/payables/invoices", new { poNumber = po, supplierInvoiceNumber = "a-100", invoiceDate = Day(-1), freight = 0m, tax = 0m, lines = new[] { new { poLine = 2, quantity = 1m, unitPrice = 3m } } },
            "duplicate_invoice");

        // Billed for ten more than were received: an exception for a purchasing manager, who is told; the clerk cannot decide it.
        var over = Id(await plant.Ok(ap, "/api/payables/invoices", new
        {
            poNumber = po, supplierInvoiceNumber = "A-101", invoiceDate = Day(-1), freight = 0m, tax = 0m, lines = new[] { new { poLine = 2, quantity = 10m, unitPrice = 3.00m } },
        }));
        var invoices = await Get(ap, "/api/payables/invoices");
        Assert.Equal("Exception", invoices.EnumerateArray().Single(i => i.GetProperty("invoiceId").GetInt64() == over).GetProperty("status").GetString());
        Assert.Contains("only 0 received", invoices.EnumerateArray().Single(i => i.GetProperty("invoiceId").GetInt64() == over).GetProperty("matchSummary").GetString(), StringComparison.Ordinal);
        await PropagateAsync(app);
        Assert.Contains((await Get(pat, "/api/notifications/inbox?openOnly=true")).EnumerateArray(), a => a.GetProperty("title").GetString() == "Supplier invoice outside tolerance");
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(ap, $"/api/payables/invoices/{over}/decide", new { approve = true, note = "x" })).StatusCode);
        await plant.Ok(pat, $"/api/payables/invoices/{over}/decide", new { approve = false, note = "Ten were never received; ask Alcoa for a credit" });
        await PropagateAsync(app);
        Assert.DoesNotContain((await Get(pat, "/api/notifications/inbox?openOnly=true")).EnumerateArray(),
            a => a.GetProperty("title").GetString() == "Supplier invoice outside tolerance" && a.GetProperty("state").GetString() == "Open");

        // The approved invoice is paid by the accounting system and goes out in the payables journal, which balances.
        var goodId = Id(good);
        await plant.Ok(ap, $"/api/payables/invoices/{goodId}/paid", new { paidOn = Day(-1), reference = "ACH-5521" });
        var journal = await (await ap.GetAsync(new Uri($"/api/accounting/export/payables-journal?from={Day(-3)}&to={Day(1)}", UriKind.Relative))).Content.ReadAsStringAsync();
        var rows = journal.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var header = rows[0].Split(',');
        var (debit, creditColumn) = (Array.IndexOf(header, "Debit"), Array.IndexOf(header, "Credit"));
        Assert.True(debit >= 0 && creditColumn >= 0, journal);
        var lines = rows.Skip(1).Select(r => r.Split(',')).ToList();
        Assert.Equal(lines.Sum(l => decimal.Parse(l[debit], CultureInfo.InvariantCulture)), lines.Sum(l => decimal.Parse(l[creditColumn], CultureInfo.InvariantCulture)));
        Assert.Contains(lines, l => decimal.Parse(l[creditColumn], CultureInfo.InvariantCulture) == 335.80m);

        // The purchasing manager sets the plant's tolerance.
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(ap, "/api/payables/policy", new { tolerancePercent = 1m, toleranceAmount = 25m, reason = "x" })).StatusCode);
        await plant.Ok(pat, "/api/payables/policy", new { tolerancePercent = 1m, toleranceAmount = 25m, reason = "Audit recommendation" });
        Assert.Equal(25m, (await Get(ap, "/api/payables/policy")).GetProperty("toleranceAmount").GetDecimal());
    }

    /// <summary>A UPS simulator: OAuth, Shop rating, shipping and tracking.</summary>
    private sealed class UpsSimulator : ICarrierTransport
    {
        public string Status { get; set; } = "Label created";

        public List<(HttpMethod Method, string Path, string? Authorization, string Body)> Requests { get; } = [];

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((request.Method, path, request.Headers.Authorization?.ToString(), body));
            string json = path switch
            {
                "/security/v1/oauth/token" => """{"access_token":"SIM-TOKEN","expires_in":"14399"}""",
                "/api/rating/v2403/Shop" => """{"RateResponse":{"RatedShipment":[{"Service":{"Code":"03"},"TotalCharges":{"CurrencyCode":"USD","MonetaryValue":"12.34"}},{"Service":{"Code":"01"},"TotalCharges":{"CurrencyCode":"USD","MonetaryValue":"48.10"},"GuaranteedDelivery":{"BusinessDaysInTransit":"1"}}]}}""",
                "/api/shipments/v2409/ship" => "{\"ShipmentResponse\":{\"ShipmentResults\":{\"ShipmentIdentificationNumber\":\"1ZSIM0001\",\"ShipmentCharges\":{\"TotalCharges\":{\"CurrencyCode\":\"USD\",\"MonetaryValue\":\"12.34\"}},\"PackageResults\":{\"TrackingNumber\":\"1ZSIM0001\",\"ShippingLabel\":{\"GraphicImage\":\""
                    + Convert.ToBase64String(Encoding.ASCII.GetBytes("GIF89a-label")) + "\"}}}}}",
                _ when path.StartsWith("/api/track/v1/details/", StringComparison.Ordinal) =>
                    "{\"trackResponse\":{\"shipment\":[{\"package\":[{\"trackingNumber\":\"1ZSIM0001\",\"currentStatus\":{\"description\":\"" + Status + "\"},\"activity\":[{\"status\":{\"type\":\"" + (Status == "Delivered" ? "D" : "I") + "\",\"description\":\"" + Status + "\"}}]}]}]}}",
                _ => throw new InvalidOperationException("Unexpected carrier call " + path),
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Carriers_rate_label_and_track_shipments_and_freight_gets_a_bill_of_lading()
    {
        var ups = new UpsSimulator();
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), services =>
        {
            services.RemoveAll<ICarrierTransport>();
            services.AddSingleton<ICarrierTransport>(ups);
        });
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("integ", "IntegrationAdministrator");
        await plant.Person("ship", "Shipper");
        var (integ, ship, stores) = (plant.As("integ"), plant.As("ship"), plant.As("stores"));
        await plant.Ok(stores, "/api/inventory/locations", new { code = "FG", siteCode = "PLANT1", name = "Finished goods", kind = "Stores" });
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "ROLL-100", quantity = 20m, locationCode = "FG", reason = "Opening balance" });

        // The integration administrator sets up a UPS account; its credentials are encrypted and never shown again.
        var from = new Dictionary<string, string>
        {
            ["fromName"] = "Shipping dept", ["fromCompany"] = "WhiskerWorks", ["fromStreet1"] = "1 Plant Rd", ["fromCity"] = "Dayton", ["fromState"] = "OH", ["fromPostal"] = "45402",
            ["fromCountry"] = "US", ["fromPhone"] = "9375550100", ["baseUrl"] = "https://ups.simulator",
        };
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(ship, "/api/carriers/accounts", new { code = "UPS", kind = "Ups", name = "UPS", sandbox = true, active = true, reason = "x" })).StatusCode);
        await Rejected(plant, integ, "/api/carriers/accounts", new { code = "UPS", kind = "Ups", name = "UPS", accountNumber = "A1B2C3", sandbox = true, active = true, settings = from, reason = "Carrier" },
            "credentials_required");
        await plant.Ok(integ, "/api/carriers/accounts", new
        {
            code = "UPS", kind = "Ups", name = "UPS daily", accountNumber = "A1B2C3", sandbox = true, active = true, settings = from,
            credentials = new Dictionary<string, string> { ["clientId"] = "client-123", ["clientSecret"] = "very-secret-456" }, reason = "Daily UPS pickup",
        });
        var accounts = await Get(ship, "/api/carriers/accounts");
        Assert.True(accounts[0].GetProperty("hasCredentials").GetBoolean());
        Assert.DoesNotContain("very-secret-456", accounts.GetRawText(), StringComparison.Ordinal);
        await using (var db = new NpgsqlConnection(app.ConnectionString))
        {
            await db.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT credentials FROM inventory.carrier_account WHERE code = 'UPS'", db);
            var stored = (string)(await cmd.ExecuteScalarAsync())!;
            Assert.StartsWith("enc:", stored, StringComparison.Ordinal);
            Assert.DoesNotContain("very-secret-456", stored, StringComparison.Ordinal);
        }

        // A packed shipment is rated (cheapest first) and its label bought; the label downloads and tracking follows the parcel.
        var shipment = Id(await plant.Ok(stores, "/api/inventory/shipments", new { customer = "Acme Corp", shipTo = "Acme, Columbus", lines = new[] { new { partNumber = "ROLL-100", quantity = 4m, locationCode = "FG" } } }));
        await plant.Ok(stores, $"/api/inventory/shipments/{shipment}/pack", new { });
        var shipTo = new { name = "Receiving", company = "Acme Corp", street1 = "200 Market St", city = "Columbus", state = "OH", postalCode = "43215", country = "US", phone = "6145550100" };
        var packages = new[] { new { weightLb = 12m, lengthIn = 10m, widthIn = 8m, heightIn = 6m } };
        var quote = await plant.Ok(ship, $"/api/carriers/shipments/{shipment}/rates", new { shipTo, packages });
        var rates = quote.GetProperty("rates");
        Assert.Equal(("03", "UPS Ground", 12.34m), (rates[0].GetProperty("serviceCode").GetString(), rates[0].GetProperty("serviceName").GetString(), rates[0].GetProperty("amount").GetDecimal()));
        Assert.Equal("Bearer SIM-TOKEN", ups.Requests.Single(r => r.Path == "/api/rating/v2403/Shop").Authorization);
        Assert.Contains("43215", ups.Requests.Single(r => r.Path == "/api/rating/v2403/Shop").Body, StringComparison.Ordinal);
        Assert.StartsWith("Basic ", ups.Requests.First(r => r.Path == "/security/v1/oauth/token").Authorization, StringComparison.Ordinal);
        var rate = JsonSerializer.Deserialize<JsonElement>(rates[0].GetRawText());
        await plant.Ok(ship, $"/api/carriers/shipments/{shipment}/label", new { rate, shipTo, packages });
        await Rejected(plant, ship, $"/api/carriers/shipments/{shipment}/label", new { rate, shipTo, packages }, "already_labeled");
        var label = await ship.GetAsync(new Uri($"/api/carriers/shipments/{shipment}/label", UriKind.Relative));
        Assert.Equal("GIF89a-label", Encoding.ASCII.GetString(await label.Content.ReadAsByteArrayAsync()));
        var row = (await Get(ship, "/api/carriers/shipments")).EnumerateArray().Single(r => r.GetProperty("shipment").GetProperty("shipmentId").GetInt64() == shipment).GetProperty("carrier");
        Assert.Equal(("1ZSIM0001", "Labeled", 12.34m), (row.GetProperty("tracking").GetString(), row.GetProperty("status").GetString(), row.GetProperty("cost").GetDecimal()));
        await plant.Ok(ship, $"/api/inventory/shipments/{shipment}/ship", new { carrier = "UPS", tracking = "1ZSIM0001" });

        var monitor = app.Services.GetRequiredService<CarrierTrackingMonitor>();
        ups.Status = "Exception: address unknown";
        Assert.Equal(1, await monitor.RunOnceAsync(CancellationToken.None));
        await PropagateAsync(app);
        Assert.Contains((await Get(ship, "/api/notifications/inbox?openOnly=true")).EnumerateArray(), a => a.GetProperty("title").GetString() == "Carrier delivery problem");
        ups.Status = "Delivered";
        Assert.Equal(1, await monitor.RunOnceAsync(CancellationToken.None));
        Assert.Equal(0, await monitor.RunOnceAsync(CancellationToken.None)); // delivered is no longer tracked
        row = (await Get(ship, "/api/carriers/shipments")).EnumerateArray().Single(r => r.GetProperty("shipment").GetProperty("shipmentId").GetInt64() == shipment).GetProperty("carrier");
        Assert.Equal("Delivered", row.GetProperty("status").GetString());

        // Freight: the carrier and PRO number by hand, and a bill of lading to print.
        var freight = Id(await plant.Ok(stores, "/api/inventory/shipments", new { customer = "Acme Corp", lines = new[] { new { partNumber = "ROLL-100", quantity = 10m, locationCode = "FG" } } }));
        await plant.Ok(ship, $"/api/carriers/shipments/{freight}/manual", new { carrier = "Estes Express", proNumber = "pro-778899", shipTo, packages = new[] { new { weightLb = 420m } } });
        var bol = await (await ship.GetAsync(new Uri($"/api/carriers/shipments/{freight}/bol", UriKind.Relative))).Content.ReadAsStringAsync();
        Assert.Contains("Straight bill of lading", bol, StringComparison.Ordinal);
        Assert.Contains("PRO-778899", bol, StringComparison.Ordinal);
        Assert.Contains("1 Plant Rd", bol, StringComparison.Ordinal);

        // A plant without carriers turns them off.
        await plant.Ok(plant.Admin, "/api/site/features/shipping-carriers", new { enabled = false, reason = "We ship collect only" });
        await Rejected(plant, ship, $"/api/carriers/shipments/{freight}/rates", new { shipTo, packages }, "feature_disabled");
    }
}
