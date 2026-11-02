using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Forge.Host.Interchange;
using Forge.Host.Notifications;
using Forge.Interchange.Contracts;
using Forge.Kernel.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Forge.Integration.Tests;

/// <summary>
/// API and EDI through the real host (owner decisions 2026-11-01): ANSI X12 files through the inbox/outbox folder, a customer's 850
/// becoming a draft sales order a Sales Manager approves, unmappable orders flagged for review, 855/856/810 written for the customer and
/// 850 for the supplier, 997s both ways, partner API keys with scopes, and signed webhooks with retries.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class InterchangeTests(PostgresFixture postgres)
{
    private static string Day(int fromToday) => DateTime.UtcNow.AddDays(fromToday).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string EdiDay(int fromToday) => DateTime.UtcNow.AddDays(fromToday).ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private static long Id(JsonElement result) => long.Parse(result.GetProperty("entityId").GetString()!, CultureInfo.InvariantCulture);

    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task PropagateAsync(ForgeApp app)
    {
        var relay = app.Services.GetRequiredService<OutboxRelay>();
        for (var round = 0; round < 4; round++)
        {
            while (await relay.RelayBatchAsync(100, CancellationToken.None) > 0)
            {
            }

            await app.Services.GetRequiredService<SubscriptionPump>().PumpOnceAsync(1_000_000, CancellationToken.None);
        }

        var delivery = app.Services.GetRequiredService<NotificationDeliveryService>();
        while (await delivery.DispatchDueAsync(50, CancellationToken.None) > 0)
        {
        }
    }

    private static string Order850(string po, long control, params (string Part, decimal Qty, decimal? Price)[] lines)
    {
        var body = new List<X12Segment> { X12Segment.Of("BEG", "00", "SA", po, null, EdiDay(0)), X12Segment.Of("DTM", "002", EdiDay(5)), X12Segment.Of("N1", "ST", "Acme Dock 4"), X12Segment.Of("N3", "1 Main St") };
        var n = 0;
        foreach (var (part, qty, price) in lines)
            body.Add(X12Segment.Of("PO1", (++n).ToString(CultureInfo.InvariantCulture), X12.Number(qty), "EA", price is { } p ? X12.Number(p) : null, null, "BP", "CUST-" + part, "VP", part));
        body.Add(X12Segment.Of("CTT", n.ToString(CultureInfo.InvariantCulture)));
        return X12.Write("ZZ", "ACMEEDI", "ZZ", "FORGE", control, control, "850", body, DateTimeOffset.UtcNow);
    }

    private static JsonElement Doc(JsonElement docs, string set, string direction, Func<JsonElement, bool>? where = null) =>
        docs.EnumerateArray().Single(d => d.GetProperty("transactionSet").GetString() == set && d.GetProperty("direction").GetString() == direction && (where?.Invoke(d) ?? true));

    [Fact]
    public async Task Edi_orders_become_drafts_bad_ones_wait_for_review_and_forge_answers_with_855_856_810_and_997()
    {
        var folder = Path.Combine(Path.GetTempPath(), "forge-edi-" + Guid.NewGuid().ToString("N"));
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), configureBuilder: b => b.UseSetting("Forge:Interchange:Folder", folder));
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        foreach (var (subject, roles) in new[] { ("integ", new[] { "IntegrationAdministrator" }), ("rep", new[] { "SalesRepresentative" }), ("mgr", new[] { "SalesManager" }),
            ("bill", new[] { "BillingClerk" }), ("ship", new[] { "Shipper" }), ("bob", new[] { "Buyer" }), ("pat", new[] { "Buyer", "PurchasingManager" }) })
            await plant.Person(subject, roles);
        var (integ, rep, mgr, bill, ship, bob, pat, stores) = (plant.As("integ"), plant.As("rep"), plant.As("mgr"), plant.As("bill"), plant.As("ship"), plant.As("bob"), plant.As("pat"), plant.As("stores"));
        await plant.Ok(rep, "/api/sales/customers", new { code = "ACME", name = "Acme Corp", paymentTermsDays = 30, taxRatePercent = 6.25m, currency = "USD" });
        await plant.Ok(bob, "/api/procurement/suppliers", new { code = "ALCOA", name = "Alcoa" });

        // Only the integration administrator keeps partners; ids are checked.
        var customer = new { code = "ACME", name = "Acme Corp", kind = "Customer", partyCode = "ACME", ediQualifier = "ZZ", ediId = "ACMEEDI", ourQualifier = "ZZ", ourId = "FORGE",
            outbound = new[] { "855", "856", "810" }, active = true, test = false, reason = "Acme trades by EDI" };
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(rep, "/api/interchange/partners", customer)).StatusCode);
        var wrongSet = await plant.Try(integ, "/api/interchange/partners", customer with { outbound = new[] { "850" } });
        Assert.Contains("invalid_outbound", await wrongSet.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await plant.Ok(integ, "/api/interchange/partners", customer);
        await plant.Ok(integ, "/api/interchange/partners", new { code = "ALCOA", name = "Alcoa", kind = "Supplier", partyCode = "ALCOA", ediQualifier = "ZZ", ediId = "ALCOAEDI",
            ourQualifier = "ZZ", ourId = "FORGEBUY", outbound = new[] { "850" }, active = true, test = false, reason = "Alcoa trades by EDI" });

        // Two purchase orders arrive in the inbox; the second has a line without a price.
        Directory.CreateDirectory(Path.Combine(folder, "inbox"));
        await File.WriteAllTextAsync(Path.Combine(folder, "inbox", "acme-1.x12"), Order850("PO-EDI-1", 101, ("ROLL-100", 6m, 25.5m)));
        await File.WriteAllTextAsync(Path.Combine(folder, "inbox", "acme-2.x12"), Order850("PO-EDI-2", 102, ("ROLL-100", 2m, 25.5m), ("ROLL-100", 1m, null)));
        await File.WriteAllTextAsync(Path.Combine(folder, "inbox", "junk.x12"), "not an interchange");
        var run = await plant.Ok(integ, "/api/interchange/folder/run", new { });
        Assert.Equal((2, 1), (run.GetProperty("received").GetInt32(), run.GetProperty("failed").GetInt32()));
        Assert.Single(Directory.GetFiles(Path.Combine(folder, "failed"), "*.error.txt"));
        await PropagateAsync(app);

        var docs = await Get(rep, "/api/interchange/documents");
        var good = Doc(docs, "850", "Inbound", d => d.GetProperty("status").GetString() == "Processed");
        var bad = Doc(docs, "850", "Inbound", d => d.GetProperty("status").GetString() == "NeedsReview");
        Assert.Contains("no unit price", bad.GetRawText(), StringComparison.Ordinal);
        var order = long.Parse(good.GetProperty("businessRef").GetString()!.Split(':')[1], CultureInfo.InvariantCulture);
        var sales = (await Get(rep, "/api/sales/orders")).EnumerateArray().Select(o => o.TryGetProperty("order", out var inner) ? inner : o).Single(o => o.GetProperty("orderNumber").GetInt64() == order);
        Assert.Equal(("Draft", "PO-EDI-1", "system:edi"), (sales.GetProperty("status").GetString(), sales.GetProperty("customerPo").GetString(), sales.GetProperty("createdBy").GetString()));
        Assert.Contains((await Get(rep, "/api/notifications/inbox")).EnumerateArray(), a => a.GetProperty("title").GetString() == "EDI document needs review");
        Assert.Equal(2, docs.EnumerateArray().Count(d => d.GetProperty("transactionSet").GetString() == "997" && d.GetProperty("direction").GetString() == "Outbound"));

        // Received again, nothing is duplicated. A person rejects the bad order.
        await File.WriteAllTextAsync(Path.Combine(folder, "inbox", "acme-1-again.x12"), Order850("PO-EDI-1", 101, ("ROLL-100", 6m, 25.5m)));
        await plant.Ok(integ, "/api/interchange/folder/run", new { });
        await PropagateAsync(app);
        Assert.Single((await Get(rep, "/api/interchange/documents")).EnumerateArray(), d => d.GetProperty("transactionSet").GetString() == "850" && d.GetProperty("status").GetString() == "Processed");
        await plant.Ok(rep, $"/api/interchange/documents/{bad.GetProperty("documentId").GetInt64()}/reject", new { reason = "Asked Acme to resend with prices" });

        // The manager approves: Forge writes the 855 and places it in the outbox.
        await plant.Ok(mgr, $"/api/sales/orders/{order}/approve", new { });
        await PropagateAsync(app);
        await plant.Ok(integ, "/api/interchange/folder/run", new { });
        var outbox = Path.Combine(folder, "outbox");
        var ack = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(outbox, "ACME_855_*.x12")));
        Assert.Contains("BAK*00*AC*PO-EDI-1", ack, StringComparison.Ordinal);
        Assert.Contains("ISA*00*", ack, StringComparison.Ordinal);
        Assert.Single(X12.Parse(ack).Single().Groups.Single().Transactions);
        Assert.Empty(Directory.GetFiles(outbox, "*.tmp"));

        // Acme's 997 accepts our 855.
        var docs855 = Doc(await Get(integ, "/api/interchange/documents"), "855", "Outbound");
        Assert.Equal("Sent", docs855.GetProperty("status").GetString());
        var fa = X12.Write("ZZ", "ACMEEDI", "ZZ", "FORGE", 900, 900, "997",
            [X12Segment.Of("AK1", "PR", docs855.GetProperty("groupControl").GetString()), X12Segment.Of("AK9", "A", "1", "1", "1")], DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(Path.Combine(folder, "inbox", "acme-997.x12"), fa);
        await plant.Ok(integ, "/api/interchange/folder/run", new { });
        await PropagateAsync(app);
        Assert.Equal("Accepted", Doc(await Get(integ, "/api/interchange/documents"), "855", "Outbound").GetProperty("status").GetString());

        // Shipping writes the 856; invoicing writes the 810.
        var demand = (await Get(planner, "/api/planning/demand")).EnumerateArray().Single(d => d.GetProperty("sourceKey").ValueKind == JsonValueKind.String
            && d.GetProperty("sourceKey").GetString() == string.Create(CultureInfo.InvariantCulture, $"sales:{order}/1")).GetProperty("demandId").GetInt64();
        await plant.Ok(stores, "/api/inventory/locations", new { code = "FG", siteCode = "PLANT1", name = "Finished goods", kind = "Stores" });
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "ROLL-100", quantity = 10m, locationCode = "FG", reason = "Opening balance" });
        var shipment = Id(await plant.Ok(stores, "/api/inventory/shipments", new { demandId = demand, lines = new[] { new { partNumber = "ROLL-100", quantity = 4m, locationCode = "FG" } } }));
        await plant.Ok(stores, $"/api/inventory/shipments/{shipment}/pack", new { });
        await plant.Ok(ship, $"/api/inventory/shipments/{shipment}/ship", new { carrier = "UPS", tracking = "1Z-EDI" });
        await PropagateAsync(app);
        await plant.Ok(bill, "/api/sales/invoices", new { orderNumber = order, shipmentIds = new[] { shipment } });
        await PropagateAsync(app);
        await plant.Ok(integ, "/api/interchange/folder/run", new { });
        var asn = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(outbox, "ACME_856_*.x12")));
        Assert.Contains("PRF*PO-EDI-1", asn, StringComparison.Ordinal);
        Assert.Contains("SN1**4*EA", asn, StringComparison.Ordinal);
        Assert.Contains("REF*CN*1Z-EDI", asn, StringComparison.Ordinal);
        var inv = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(outbox, "ACME_810_*.x12")));
        Assert.Contains("IT1*1*4*EA*25.5**VP*ROLL-100", inv, StringComparison.Ordinal);
        Assert.Contains("TDS*10838", inv, StringComparison.Ordinal); // 102.00 + 6.38 tax

        // An approved purchase order to an EDI supplier goes out as an 850; the supplier's ship notice comes back through the API.
        var po = Id(await plant.Ok(bob, "/api/procurement/purchase-orders", new { supplier = "ALCOA", lines = new[] { new { partNumber = "COIL-AL", quantity = 500m, promisedDate = Day(7), unitPrice = 2.1m } } }));
        await plant.Ok(pat, $"/api/procurement/purchase-orders/{po}/approve", new { reason = "Budget approved" });
        await PropagateAsync(app);
        await plant.Ok(integ, "/api/interchange/folder/run", new { });
        var out850 = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(outbox, "ALCOA_850_*.x12")));
        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"BEG*00*SA*{po}"), out850, StringComparison.Ordinal);
        Assert.Contains("PO1*1*500*KG*2.1**BP*COIL-AL", out850, StringComparison.Ordinal);

        var issued = await plant.Ok(integ, "/api/interchange/keys", new { partnerCode = "ALCOA", scopes = new[] { "edi", "purchase-orders" }, reason = "Alcoa portal" });
        var supplierKey = issued.GetProperty("key").GetString()!;
        Assert.StartsWith("fk_", supplierKey, StringComparison.Ordinal);
        Assert.DoesNotContain(supplierKey, (await Get(integ, "/api/interchange/keys")).GetRawText(), StringComparison.Ordinal);
        using var anonymous = app.CreateClient();
        async Task<HttpResponseMessage> Partner(string? key, HttpMethod method, string url, HttpContent? content = null)
        {
            using var request = new HttpRequestMessage(method, url) { Content = content };
            if (key is not null) request.Headers.Add("X-Forge-Api-Key", key);
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            return await anonymous.SendAsync(request);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await Partner(null, HttpMethod.Get, "/api/partner/v1/purchase-orders")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Partner(supplierKey, HttpMethod.Get, "/api/partner/v1/orders")).StatusCode);
        var pos = JsonDocument.Parse(await (await Partner(supplierKey, HttpMethod.Get, "/api/partner/v1/purchase-orders")).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(po, Assert.Single(pos.EnumerateArray()).GetProperty("poNumber").GetInt64());
        var asnIn = X12.Write("ZZ", "ALCOAEDI", "ZZ", "FORGEBUY", 77, 77, "856",
            [X12Segment.Of("BSN", "00", "ASN-9", EdiDay(0), "1200"), X12Segment.Of("HL", "1", null, "S"), X12Segment.Of("DTM", "011", EdiDay(0)),
             X12Segment.Of("HL", "2", "1", "O"), X12Segment.Of("PRF", po.ToString(CultureInfo.InvariantCulture)), X12Segment.Of("HL", "3", "2", "I"),
             X12Segment.Of("LIN", "1", "BP", "COIL-AL"), X12Segment.Of("SN1", null, "500", "KG")], DateTimeOffset.UtcNow);
        var posted = await Partner(supplierKey, HttpMethod.Post, "/api/partner/v1/edi", new StringContent(asnIn, Encoding.UTF8, "text/plain"));
        Assert.True(posted.IsSuccessStatusCode, await posted.Content.ReadAsStringAsync());
        var spoofed = await Partner(supplierKey, HttpMethod.Post, "/api/partner/v1/edi", new StringContent(Order850("PO-SPOOF", 103, ("ROLL-100", 1m, 1m)), Encoding.UTF8, "text/plain"));
        Assert.Contains("wrong_sender", await spoofed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await PropagateAsync(app);
        var supplierAsn = Doc(await Get(bob, "/api/interchange/documents"), "856", "Inbound");
        Assert.Equal(("Processed", $"purchase-order:{po}"), (supplierAsn.GetProperty("status").GetString(), supplierAsn.GetProperty("businessRef").GetString()));
        Assert.Contains("500 shipped", supplierAsn.GetProperty("summary").GetString(), StringComparison.Ordinal);

        // A customer key submits an order as a draft and reads its orders with shipments; revoked, it stops working.
        var customerKey = (await plant.Ok(integ, "/api/interchange/keys", new { partnerCode = "ACME", scopes = new[] { "orders" }, reason = "Acme portal" })).GetProperty("key").GetString()!;
        var submitted = await Partner(customerKey, HttpMethod.Post, "/api/partner/v1/orders",
            JsonContent(new { customerPo = "PO-API-1", lines = new[] { new { partNumber = "ROLL-100", quantity = 3m, unitPrice = 25.5m, dueDate = Day(9) } } }));
        Assert.True(submitted.IsSuccessStatusCode, await submitted.Content.ReadAsStringAsync());
        var mine = JsonDocument.Parse(await (await Partner(customerKey, HttpMethod.Get, "/api/partner/v1/orders")).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(2, mine.GetArrayLength());
        Assert.Equal("Draft", mine.EnumerateArray().Single(o => o.GetProperty("customerPo").GetString() == "PO-API-1").GetProperty("status").GetString());
        Assert.Equal(4m, Assert.Single(mine.EnumerateArray().Single(o => o.GetProperty("customerPo").GetString() == "PO-EDI-1").GetProperty("shipments").EnumerateArray()).GetProperty("quantity").GetDecimal());
        var keyId = (await Get(integ, "/api/interchange/keys")).EnumerateArray().First(k => k.GetProperty("partnerCode").GetString() == "ACME").GetProperty("keyId").GetInt64();
        await plant.Ok(integ, $"/api/interchange/keys/{keyId}/revoke", new { reason = "Portal retired" });
        Assert.Equal(HttpStatusCode.Unauthorized, (await Partner(customerKey, HttpMethod.Get, "/api/partner/v1/orders")).StatusCode);
    }

    [Fact]
    public async Task Customer_part_numbers_map_buyer_parts_and_an_860_waits_for_a_person_to_apply_it()
    {
        var folder = Path.Combine(Path.GetTempPath(), "forge-edi-" + Guid.NewGuid().ToString("N"));
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), configureBuilder: b => b.UseSetting("Forge:Interchange:Folder", folder));
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("integ", "IntegrationAdministrator");
        await plant.Person("rep", "SalesRepresentative");
        var (integ, rep) = (plant.As("integ"), plant.As("rep"));
        await plant.Ok(rep, "/api/sales/customers", new { code = "ACME", name = "Acme Corp", paymentTermsDays = 30, taxRatePercent = 0m });
        await plant.Ok(integ, "/api/interchange/partners", new { code = "ACME", name = "Acme Corp", kind = "Customer", partyCode = "ACME", ediQualifier = "ZZ", ediId = "ACMEEDI", ourQualifier = "ZZ", ourId = "FORGE",
            outbound = new[] { "855" }, active = true, test = false, reason = "Acme trades by EDI" });
        Directory.CreateDirectory(Path.Combine(folder, "inbox"));
        string Order(string po, long control, string buyerPart) => X12.Write("ZZ", "ACMEEDI", "ZZ", "FORGE", control, control, "850",
            [X12Segment.Of("BEG", "00", "SA", po, null, EdiDay(0)), X12Segment.Of("DTM", "002", EdiDay(5)), X12Segment.Of("PO1", "1", "4", "EA", "25", null, "BP", buyerPart), X12Segment.Of("CTT", "1")], DateTimeOffset.UtcNow);

        // Without the customer's number on record, the order waits for review; recorded, it maps exactly.
        await File.WriteAllTextAsync(Path.Combine(folder, "inbox", "a.x12"), Order("PO-X-1", 201, "ACME-ROLL"));
        await plant.Ok(integ, "/api/interchange/folder/run", new { });
        await PropagateAsync(app);
        var waiting = Doc(await Get(rep, "/api/interchange/documents"), "850", "Inbound");
        Assert.Equal("NeedsReview", waiting.GetProperty("status").GetString());
        Assert.Contains("ACME-ROLL has no Forge part", waiting.GetRawText(), StringComparison.Ordinal);
        await plant.Ok(rep, "/api/sales/customers/ACME/parts", new { customerPart = "acme-roll", partNumber = "ROLL-100", description = "Their 100 mm roll" });
        Assert.Equal("ROLL-100", Assert.Single((await Get(rep, "/api/sales/customer-parts?customer=ACME")).EnumerateArray()).GetProperty("partNumber").GetString());
        await plant.Ok(rep, $"/api/interchange/documents/{waiting.GetProperty("documentId").GetInt64()}/reprocess", new { });
        await PropagateAsync(app);
        var mapped = Doc(await Get(rep, "/api/interchange/documents"), "850", "Inbound");
        Assert.Equal("Processed", mapped.GetProperty("status").GetString());
        var order = long.Parse(mapped.GetProperty("businessRef").GetString()!.Split(':')[1], CultureInfo.InvariantCulture);

        // The approval's 855 carries both numbers.
        await plant.Person("mgr", "SalesManager");
        await plant.Ok(plant.As("mgr"), $"/api/sales/orders/{order}/approve", new { });
        await PropagateAsync(app);
        await plant.Ok(integ, "/api/interchange/folder/run", new { });
        var ack = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(Path.Combine(folder, "outbox"), "ACME_855_*.x12")));
        Assert.Contains("VP*ROLL-100*BP*ACME-ROLL", ack, StringComparison.Ordinal);

        // An 860 is read and described, and waits for a person; it is closed once the change was made.
        var change = X12.Write("ZZ", "ACMEEDI", "ZZ", "FORGE", 202, 202, "860",
            [X12Segment.Of("BCH", "04", "SA", "PO-X-1", null, null, EdiDay(0)), X12Segment.Of("POC", "1", "QI", "6", "6", "EA", "25", null, "BP", "ACME-ROLL"), X12Segment.Of("DTM", "002", EdiDay(9)), X12Segment.Of("CTT", "1")], DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(Path.Combine(folder, "inbox", "c.x12"), change);
        await plant.Ok(integ, "/api/interchange/folder/run", new { });
        await PropagateAsync(app);
        var asked = Doc(await Get(rep, "/api/interchange/documents"), "860", "Inbound");
        Assert.Equal(("NeedsReview", $"sales-order:{order}"), (asked.GetProperty("status").GetString(), asked.GetProperty("businessRef").GetString()));
        Assert.Contains("increase quantity to 6 EA", asked.GetRawText(), StringComparison.Ordinal);
        await plant.Ok(rep, $"/api/interchange/documents/{asked.GetProperty("documentId").GetInt64()}/close", new { reason = "Raised line 1 to 6 on the sales order" });
        Assert.Equal("Processed", Doc(await Get(rep, "/api/interchange/documents"), "860", "Inbound").GetProperty("status").GetString());
    }

    private static StringContent JsonContent(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private sealed class RecordingTransport : IWebhookTransport
    {
        public List<(Uri Url, string Body, IReadOnlyDictionary<string, string> Headers)> Sent { get; } = [];

        public Queue<int> Answers { get; } = new();

        public Task<(int? StatusCode, string? Error)> PostAsync(Uri url, string body, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
        {
            Sent.Add((url, body, headers));
            return Task.FromResult<(int?, string?)>((Answers.Count > 0 ? Answers.Dequeue() : 200, null));
        }
    }

    [Fact]
    public async Task Webhooks_are_signed_limited_to_their_partner_retried_and_reported_when_they_give_up()
    {
        var transport = new RecordingTransport();
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), services =>
        {
            services.RemoveAll<IWebhookTransport>();
            services.AddSingleton<IWebhookTransport>(transport);
        });
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("integ", "IntegrationAdministrator");
        await plant.Person("rep", "SalesRepresentative");
        var (integ, rep) = (plant.As("integ"), plant.As("rep"));
        await plant.Ok(rep, "/api/sales/customers", new { code = "ACME", name = "Acme Corp", paymentTermsDays = 30, taxRatePercent = 0m });
        await plant.Ok(rep, "/api/sales/customers", new { code = "BETA", name = "Beta Ltd", paymentTermsDays = 30, taxRatePercent = 0m });
        await plant.Ok(integ, "/api/interchange/partners", new { code = "ACME", name = "Acme Corp", kind = "Customer", partyCode = "ACME", outbound = Array.Empty<string>(), active = true, test = false, reason = "Acme portal" });
        var insecure = await plant.Try(integ, "/api/interchange/webhooks", new { name = "Acme", url = "http://hooks.example/forge", events = new[] { "sales_order.changed" }, partnerCode = "ACME", active = true, reason = "x" });
        Assert.Contains("invalid_url", await insecure.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var hook = await plant.Ok(integ, "/api/interchange/webhooks", new { name = "Acme portal", url = "https://hooks.example/forge", events = new[] { "sales_order.changed" }, partnerCode = "ACME", active = true, reason = "Acme portal" });
        var secret = hook.GetProperty("secret").GetString()!;
        Assert.DoesNotContain(secret, (await Get(integ, "/api/interchange/webhooks")).GetRawText(), StringComparison.Ordinal);
        await using (var db = new NpgsqlConnection(app.ConnectionString))
        {
            await db.OpenAsync();
            await using var stored = new NpgsqlCommand("SELECT secret, secret_prefix FROM interchange.webhook", db);
            await using var row = await stored.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.StartsWith("enc:", row.GetString(0), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, row.GetString(0), StringComparison.Ordinal);
            Assert.Equal(secret[..10], row.GetString(1));
        }

        // Only Acme's order is sent.
        await plant.Ok(rep, "/api/sales/orders", new { customer = "BETA", customerPo = "B-1", lines = new[] { new { partNumber = "ROLL-100", quantity = 1m, unitPrice = 5m, dueDate = Day(5) } } });
        var acme = Id(await plant.Ok(rep, "/api/sales/orders", new { customer = "ACME", customerPo = "A-1", lines = new[] { new { partNumber = "ROLL-100", quantity = 1m, unitPrice = 5m, dueDate = Day(5) } } }));
        await PropagateAsync(app);
        var dispatcher = app.Services.GetRequiredService<WebhookDispatcher>();
        transport.Answers.Enqueue(503);
        Assert.Equal(1, await dispatcher.DispatchDueAsync(50, CancellationToken.None));
        var pending = Assert.Single((await Get(integ, "/api/interchange/webhooks/deliveries")).EnumerateArray());
        Assert.Equal(("Pending", 1, 503), (pending.GetProperty("status").GetString(), pending.GetProperty("attempts").GetInt32(), pending.GetProperty("lastStatusCode").GetInt32()));
        Assert.Equal(0, await dispatcher.DispatchDueAsync(50, CancellationToken.None)); // not due until the retry delay passes

        async Task MakeDue()
        {
            await using var db = new NpgsqlConnection(app.ConnectionString);
            await db.OpenAsync();
            await using var cmd = new NpgsqlCommand("UPDATE interchange.webhook_delivery SET next_attempt_at = now() - interval '1 minute' WHERE status = 'Pending'", db);
            await cmd.ExecuteNonQueryAsync();
        }

        await MakeDue();
        Assert.Equal(1, await dispatcher.DispatchDueAsync(50, CancellationToken.None));
        Assert.Equal("Delivered", Assert.Single((await Get(integ, "/api/interchange/webhooks/deliveries")).EnumerateArray()).GetProperty("status").GetString());
        var (url, body, headers) = transport.Sent[^1];
        Assert.Equal("https://hooks.example/forge", url.ToString());
        Assert.Equal(WebhookDispatcher.Signature(secret, long.Parse(headers["X-Forge-Timestamp"], CultureInfo.InvariantCulture), body), headers["X-Forge-Signature"]);
        using var payload = JsonDocument.Parse(body);
        Assert.Equal(("sales_order.changed", acme), (payload.RootElement.GetProperty("type").GetString(), payload.RootElement.GetProperty("data").GetProperty("orderNumber").GetInt64()));

        // A receiver that never answers 2xx is given up on after the last retry, and the integration administrator is told.
        await plant.Ok(rep, $"/api/sales/orders/{acme}/lines/1/date", new { dueDate = Day(8), reason = "Customer asked for later delivery" });
        await PropagateAsync(app);
        for (var attempt = 0; attempt <= 5; attempt++)
        {
            transport.Answers.Enqueue(500);
            await MakeDue();
            Assert.Equal(1, await dispatcher.DispatchDueAsync(50, CancellationToken.None));
        }

        var failed = (await Get(integ, "/api/interchange/webhooks/deliveries")).EnumerateArray().First();
        Assert.Equal(("Failed", 6), (failed.GetProperty("status").GetString(), failed.GetProperty("attempts").GetInt32()));
        await PropagateAsync(app);
        Assert.Contains((await Get(integ, "/api/notifications/inbox")).EnumerateArray(), a => a.GetProperty("title").GetString() == "Webhook delivery failed");
    }
}
