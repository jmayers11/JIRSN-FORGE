using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Forge.Host.Hazcom;
using Forge.Host.Notifications;
using Forge.Kernel.Postgres;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Integration.Tests;

/// <summary>
/// Hazard communication through the real host (owner decisions 2026-11-01): the EHS Coordinator keeps the chemical register and approves
/// SDS; a hazardous material without an approved SDS cannot be bought or received; anyone signed in reads approved SDS, at the kiosk and
/// in the binder download; a newer revision supersedes the old; reviews fall due three years on and the EHS Coordinator is told.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class HazcomTests(PostgresFixture postgres)
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

    private static async Task<string> UploadAsync(HttpClient client, byte[] bytes, string name)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/engineering/document-files") { Content = new ByteArrayContent(bytes) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Add("X-File-Name", name);
        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        return JsonDocument.Parse(text).RootElement.GetProperty("entityId").GetString()!;
    }

    private static async Task PropagateAsync(ForgeApp app)
    {
        var relay = app.Services.GetRequiredService<OutboxRelay>();
        while (await relay.RelayBatchAsync(100, CancellationToken.None) > 0)
        {
        }

        await app.Services.GetRequiredService<SubscriptionPump>().PumpOnceAsync(1_000_000, CancellationToken.None);
        var delivery = app.Services.GetRequiredService<NotificationDeliveryService>();
        while (await delivery.DispatchDueAsync(50, CancellationToken.None) > 0)
        {
        }
    }

    [Fact]
    public async Task A_hazardous_material_needs_an_approved_sds_before_it_is_bought_or_received_and_everyone_can_read_it()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        foreach (var (subject, roles) in new[] { ("ehs", new[] { "EHSCoordinator" }), ("bob", new[] { "Buyer" }), ("pat", new[] { "Buyer", "PurchasingManager" }), ("kiosk", new[] { "ShopFloorTerminal" }) })
            await plant.Person(subject, roles);
        var (ehs, bob, pat, stores, sarah, kiosk, e) = (plant.As("ehs"), plant.As("bob"), plant.As("pat"), plant.As("stores"), plant.As("sarah"), plant.As("kiosk"), plant.Engineer);
        await plant.Ok(e, "/api/engineering/items", new { partNumber = "SOLV-1", description = "Cleaning solvent", unitOfMeasure = "L", makeBuy = "Buy", leadTimeDays = 3 });
        await plant.Ok(e, "/api/engineering/items/SOLV-1/revisions", new { revision = "A" });
        await plant.Ok(plant.Approver, "/api/engineering/items/SOLV-1/revisions/A/release", new { reason = "Approved solvent" });
        await plant.Ok(bob, "/api/procurement/suppliers", new { code = "CHEM", name = "Acme Chemical" });

        // Only the EHS Coordinator keeps the register.
        var chemical = new { hazardous = true, signalWord = "Danger", pictograms = new[] { "GHS02", "GHS07" }, hazardStatements = "Highly flammable liquid and vapour.", ppe = "Nitrile gloves, goggles", storage = "Flammables cabinet", reason = "New solvent" };
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(bob, "/api/hazcom/chemicals/SOLV-1", chemical)).StatusCode);
        Assert.Contains("invalid_pictogram", await (await plant.Try(ehs, "/api/hazcom/chemicals/SOLV-1", chemical with { pictograms = new[] { "GHS99" } })).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await plant.Ok(ehs, "/api/hazcom/chemicals/SOLV-1", chemical);

        // No approved SDS: it cannot be bought or received.
        var po = new { supplier = "CHEM", lines = new[] { new { partNumber = "SOLV-1", quantity = 20m, promisedDate = Day(5) } } };
        Assert.Contains("sds_required", await (await plant.Try(bob, "/api/procurement/purchase-orders", po)).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("sds_required", await (await plant.Try(stores, "/api/inventory/receipts", new { partNumber = "SOLV-1", quantity = 4m, locationCode = "STORES", reason = "Sample" })).Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // The buyer uploads the supplier's SDS; the EHS Coordinator is told and approves it.
        var pdf = "%PDF-1.4 Kleen-All SDS revision 2024"u8.ToArray();
        var sha = await UploadAsync(bob, pdf, "kleen-all-2024.pdf");
        var first = Id(await plant.Ok(bob, "/api/hazcom/sds", new { partNumber = "SOLV-1", manufacturer = "Acme Chemical", productName = "Kleen-All", revisionDate = Day(-200), fileSha256 = sha, fileName = "kleen-all-2024.pdf" }));
        await PropagateAsync(app);
        Assert.Contains((await Get(ehs, "/api/notifications/inbox")).EnumerateArray(), a => a.GetProperty("title").GetString() == "Safety data sheet waiting for review");
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(bob, $"/api/hazcom/sds/{first}/decide", new { approve = true })).StatusCode);
        await plant.Ok(ehs, $"/api/hazcom/sds/{first}/decide", new { approve = true });
        Id(await plant.Ok(bob, "/api/procurement/purchase-orders", po));
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "SOLV-1", quantity = 4m, locationCode = "STORES", reason = "Sample" });

        // Anyone signed in reads the register and opens the SDS; the binder holds it.
        var entry = Assert.Single((await Get(sarah, "/api/hazcom/chemicals")).EnumerateArray());
        Assert.Equal(("Danger", first, "STORES"), (entry.GetProperty("chemical").GetProperty("signalWord").GetString(), entry.GetProperty("current").GetProperty("sdsId").GetInt64(),
            entry.GetProperty("locations")[0].GetProperty("locationCode").GetString()));
        Assert.Equal(pdf, await sarah.GetByteArrayAsync(new Uri($"/api/hazcom/sds/{first}/file", UriKind.Relative)));
        using (var zip = new ZipArchive(new MemoryStream(await sarah.GetByteArrayAsync(new Uri("/api/hazcom/binder.zip?location=STORES", UriKind.Relative)))))
        {
            Assert.NotNull(zip.GetEntry("sds/SOLV-1.pdf"));
            using var index = new StreamReader(zip.GetEntry("index.html")!.Open());
            Assert.Contains("Kleen-All", await index.ReadToEndAsync(), StringComparison.Ordinal);
        }

        // At the kiosk: the hazardous materials of an order, with their SDS or plainly without one.
        await plant.Ok(ehs, "/api/hazcom/chemicals/COIL-AL", new { hazardous = true, signalWord = "Warning", pictograms = new[] { "GHS07" }, reason = "Coil lubricant residue" });
        var order = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "ROLL-100", quantity = 10m, dueDate = Day(10), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Customer order" }); // materials come from the BOM at release
        var atKiosk = Assert.Single((await Get(kiosk, $"/api/shopfloor/sds?order={order}")).EnumerateArray());
        Assert.Equal("COIL-AL", atKiosk.GetProperty("partNumber").GetString());
        Assert.True(!atKiosk.TryGetProperty("current", out var none) || none.ValueKind == JsonValueKind.Null);

        // A supplier revision supersedes the old SDS; an older one cannot replace a newer.
        var newer = Id(await plant.Ok(bob, "/api/hazcom/sds", new { partNumber = "SOLV-1", manufacturer = "Acme Chemical", productName = "Kleen-All", revisionDate = Day(-10), fileSha256 = await UploadAsync(bob, "%PDF-1.4 rev 2026"u8.ToArray(), "kleen-all-2026.pdf"), fileName = "kleen-all-2026.pdf" }));
        await plant.Ok(ehs, $"/api/hazcom/sds/{newer}/decide", new { approve = true });
        var older = Id(await plant.Ok(bob, "/api/hazcom/sds", new { partNumber = "SOLV-1", manufacturer = "Acme Chemical", productName = "Kleen-All", revisionDate = Day(-900), fileSha256 = sha, fileName = "old.pdf" }));
        Assert.Contains("older_revision", await (await plant.Try(ehs, $"/api/hazcom/sds/{older}/decide", new { approve = true })).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("reason", await (await plant.Try(ehs, $"/api/hazcom/sds/{older}/decide", new { approve = false })).Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        await plant.Ok(ehs, $"/api/hazcom/sds/{older}/decide", new { approve = false, reason = "Older than the approved revision" });
        var all = (await Get(ehs, "/api/hazcom/sds?part=SOLV-1")).EnumerateArray().ToDictionary(s => s.GetProperty("sdsId").GetInt64(), s => s.GetProperty("status").GetString());
        Assert.Equal(("Superseded", "Approved", "Rejected"), (all[first], all[newer], all[older]));
        Assert.Single((await Get(sarah, "/api/hazcom/sds?part=SOLV-1")).EnumerateArray());

        // Reviews: an SDS revised over three years ago is due at once; the EHS Coordinator is told and confirms it current.
        await plant.Ok(e, "/api/engineering/items", new { partNumber = "ACID-2", description = "Pickling acid", unitOfMeasure = "L", makeBuy = "Buy", leadTimeDays = 3 });
        await plant.Ok(ehs, "/api/hazcom/chemicals/ACID-2", new { hazardous = true, signalWord = "Danger", pictograms = new[] { "GHS05" }, reason = "Acid" });
        var aged = Id(await plant.Ok(ehs, "/api/hazcom/sds", new { partNumber = "ACID-2", manufacturer = "Acid Co", productName = "Pickle-5", revisionDate = Day(-1500), fileSha256 = sha, fileName = "pickle.pdf" }));
        await plant.Ok(ehs, $"/api/hazcom/sds/{aged}/decide", new { approve = true });
        await app.Services.GetRequiredService<HazcomReviewScanner>().RunAsync(CancellationToken.None);
        await PropagateAsync(app);
        Assert.Contains((await Get(ehs, "/api/notifications/inbox")).EnumerateArray(), a => a.GetProperty("title").GetString() == "Safety data sheet review due");
        await plant.Ok(ehs, $"/api/hazcom/sds/{aged}/review", new { note = "Checked the supplier site: still revision on file" });
        var reviewed = (await Get(ehs, "/api/hazcom/sds?part=ACID-2")).EnumerateArray().Single();
        Assert.Equal(DateTime.UtcNow.AddYears(3).Year, DateOnly.Parse(reviewed.GetProperty("reviewDue").GetString()!, CultureInfo.InvariantCulture).Year);
    }

    [Fact]
    public async Task HazCom_training_is_a_site_wide_qualification_and_onboarding_requirement_each_plant_can_change()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("ehs", "EHSCoordinator");
        await plant.Person("sup2", "Supervisor");
        var (ehs, sup, e) = (plant.As("ehs"), plant.As("sup2"), plant.Engineer); // the engineer is also a Training Coordinator

        // Out of the box: one of the site's qualifications (Safety, renewed every 365 days) and a requirement for every new employee.
        var type = (await Get(e, "/api/competency/qualification-types")).EnumerateArray().Single(q => q.GetProperty("code").GetString() == "HAZCOM");
        Assert.Equal("Safety", type.GetProperty("kind").GetString());
        Assert.Equal(365, type.GetProperty("validityDays").GetInt32());
        var site = (await Get(e, "/api/workforce/onboarding-requirements")).EnumerateArray().Single(r => r.GetProperty("qualificationCode").GetString() == "HAZCOM");
        Assert.Equal(JsonValueKind.Null, site.GetProperty("workCenterCode").ValueKind);
        Assert.Equal(7, site.GetProperty("dueDays").GetInt32());

        // A new hire gets a HazCom training request, and the HazCom training view shows them not yet trained.
        await plant.Worker("newbie", "SLIT-WC");
        var newbie = plant.People["newbie"];
        var opened = (await Get(e, $"/api/workforce/training-requests?personId={newbie}&open=true")).EnumerateArray().Single(r => r.GetProperty("qualificationCode").GetString() == "HAZCOM");
        Assert.Equal("Onboarding", opened.GetProperty("source").GetString());
        var view = await Get(ehs, "/api/hazcom/training");
        Assert.Equal(365, view.GetProperty("validityDays").GetInt32());
        var row = view.GetProperty("people").EnumerateArray().Single(p => p.GetProperty("personId").GetGuid() == newbie);
        Assert.Equal("Missing", row.GetProperty("status").GetString());
        Assert.Equal(opened.GetProperty("requestId").GetGuid(), row.GetProperty("openRequestId").GetGuid());
        await plant.Person("bob", "Buyer");
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.As("bob").GetAsync(new Uri("/api/hazcom/training", UriKind.Relative))).StatusCode);

        // Each plant sets its own renewal period and new-employee timing; supervisors cannot.
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(sup, "/api/competency/qualification-types/HAZCOM/validity", new { validityDays = 730, reason = "x" })).StatusCode);
        var changed = await plant.Ok(e, "/api/competency/qualification-types/HAZCOM/validity", new { validityDays = 730, reason = "Plant program: every two years" });
        Assert.Contains("now valid for 730 days (was 365 days)", changed.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal("no_change", (await plant.Ok(e, "/api/competency/qualification-types/HAZCOM/validity", new { validityDays = 730, reason = "Again" })).GetProperty("code").GetString());
        Assert.Contains("invalid_validity", await (await plant.Try(e, "/api/competency/qualification-types/HAZCOM/validity", new { validityDays = 0, reason = "x" })).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(730, (await Get(ehs, "/api/hazcom/training")).GetProperty("validityDays").GetInt32());
        await plant.Ok(e, $"/api/workforce/onboarding-requirements/{site.GetProperty("requirementId").GetString()}/retire", new { reason = "Train before day one instead" });
        await plant.Ok(e, "/api/workforce/onboarding-requirements", new { qualificationCode = "HAZCOM", dueDays = 1, note = "Before the first shift", reason = "Plant HazCom program" });
        Assert.Equal(1, (await Get(ehs, "/api/hazcom/training")).GetProperty("requirements").EnumerateArray().Single().GetProperty("dueDays").GetInt32());

        // A newly hazardous chemical asks the EHS and training coordinators to decide about training; the EHS Coordinator decides.
        var set = await plant.Ok(ehs, "/api/hazcom/chemicals/COIL-AL", new { hazardous = true, signalWord = "Warning", pictograms = new[] { "GHS07" }, reason = "Coil lubricant residue" });
        Assert.Contains("asked to review HazCom training", set.GetProperty("message").GetString(), StringComparison.Ordinal);
        await PropagateAsync(app);
        foreach (var who in new[] { ehs, e })
            Assert.Contains((await Get(who, "/api/notifications/inbox?openOnly=true")).EnumerateArray(), a => a.GetProperty("title").GetString() == "New hazardous chemical - review HazCom training");
        Assert.Equal("COIL-AL", (await Get(ehs, "/api/hazcom/training")).GetProperty("chemicalsAwaitingTrainingReview").EnumerateArray().Single().GetString());
        Assert.True((await Get(ehs, "/api/hazcom/chemicals")).EnumerateArray().Single(v => v.GetProperty("partNumber").GetString() == "COIL-AL").GetProperty("chemical").GetProperty("trainingReviewDue").GetBoolean());
        // Editing it again while still hazardous does not ask twice.
        await plant.Ok(ehs, "/api/hazcom/chemicals/COIL-AL", new { hazardous = true, signalWord = "Warning", pictograms = new[] { "GHS07" }, ppe = "Gloves", reason = "Add PPE" });
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(e, "/api/hazcom/chemicals/COIL-AL/training-review", new { refresherNeeded = true, note = "x" })).StatusCode);
        var reviewed = await plant.Ok(ehs, "/api/hazcom/chemicals/COIL-AL/training-review", new { refresherNeeded = true, note = "New irritant class for the slitting crew" });
        Assert.Contains("refresher training is needed", reviewed.GetProperty("message").GetString(), StringComparison.Ordinal);
        await PropagateAsync(app);
        Assert.DoesNotContain((await Get(ehs, "/api/notifications/inbox?openOnly=true")).EnumerateArray(), a => a.GetProperty("title").GetString() == "New hazardous chemical - review HazCom training" && a.GetProperty("state").GetString() == "Open");
        Assert.Empty((await Get(ehs, "/api/hazcom/training")).GetProperty("chemicalsAwaitingTrainingReview").EnumerateArray());
        var coil = (await Get(ehs, "/api/hazcom/chemicals")).EnumerateArray().Single(v => v.GetProperty("partNumber").GetString() == "COIL-AL").GetProperty("chemical");
        Assert.False(coil.GetProperty("trainingReviewDue").GetBoolean());
        Assert.StartsWith("Refresher training needed: ", coil.GetProperty("trainingReviewNote").GetString(), StringComparison.Ordinal);

        // Requesting refresher training for the people chosen: ordinary training requests; one already open is skipped.
        var dana = plant.People["dana"];
        var bulk = await plant.Ok(ehs, "/api/hazcom/training/requests", new { personIds = new[] { newbie, dana }, reason = "New irritant on the slitting line" });
        Assert.Equal(2, bulk.GetProperty("opened").GetInt32() + bulk.GetProperty("alreadyRequested").GetInt32());
        Assert.True(bulk.GetProperty("alreadyRequested").GetInt32() >= 1); // the new hire's onboarding request is still open
        var danaRow = (await Get(ehs, "/api/hazcom/training")).GetProperty("people").EnumerateArray().Single(p => p.GetProperty("personId").GetGuid() == dana);
        Assert.NotEqual(JsonValueKind.Null, danaRow.GetProperty("openRequestId").ValueKind);
    }
}
