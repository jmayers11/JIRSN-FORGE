using System.Globalization;
using System.Net;
using System.Text.Json;
using Forge.Kernel.Postgres;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Integration.Tests;

/// <summary>
/// Sprint 10 material-extension acceptance through the real API (WW-S10-23, 25, 27, 28, 33, 34, 35 and the role half of 38):
/// a shop-floor suspect scrap report is evidence for Quality, not scrap; one linked replacement request is queued for the stockroom;
/// a retried operator request is one request; additional (non-BOM) material waits for an independent decision without amending the
/// order; held stock is never allocated; the last stock cannot be reserved twice; and cancelling after a partial issue reopens only the
/// unfilled demand while the history stays.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MaterialRequestAcceptanceTests(PostgresFixture postgres)
{
    private const string SarahBadge = "B-SARAH-MAT10";
    private const string Tasks = "/api/inventory/tasks";

    private sealed record Floor(SlittingPlant.Plant Plant, HttpClient Planner, HttpClient Kiosk, HttpClient Stores, HttpClient Supervisor, long Order);

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

    private static async Task<JsonElement> TaskById(HttpClient client, long order, long taskId) =>
        (await Get(client, $"{Tasks}?open=false&order={order}")).EnumerateArray().Single(t => t.GetProperty("taskId").GetInt64() == taskId);

    private static async Task<decimal> Stock(HttpClient client, string part, string location, string status = "Released") =>
        (await Get(client, $"/api/inventory/stock?part={part}")).EnumerateArray()
            .Where(b => b.GetProperty("locationCode").GetString() == location && b.GetProperty("status").GetString() == status)
            .Sum(b => b.GetProperty("quantity").GetDecimal());

    /// <summary>A released harness order of 20 needing one CONN-8 each, with Sarah running its operation and the stores balance given.</summary>
    private static async Task<Floor> BuildAsync(ForgeApp app, decimal connectors)
    {
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var e = plant.Engineer;
        await plant.Ok(e, "/api/resources/shifts", new { code = "ALL", siteCode = "PLANT1", name = "Around the clock", start = "00:00:00", durationMinutes = 1440, days = new[] { 1, 2, 3, 4, 5, 6, 7 } });
        await plant.Ok(e, "/api/resources/work-centers/SLIT-WC/shifts", new { shiftCode = "ALL" });
        await plant.Ok(e, $"/api/workforce/workers/{plant.People["sarah"]}/shift", new { shiftCode = "ALL" });
        await plant.Ok(plant.Admin, $"/api/workforce/people/{plant.People["sarah"]}/badge", new { badge = SarahBadge });
        await plant.Person("kiosk", "ShopFloorTerminal");
        await plant.Person("sup", "Supervisor");

        foreach (var (part, makeBuy) in new[] { ("CONN-8", "Buy"), ("SLEEVE-2", "Buy"), ("HARN-1", "Make") })
        {
            await plant.Ok(e, "/api/engineering/items", new { partNumber = part, description = part, unitOfMeasure = "EA", makeBuy, leadTimeDays = 3 });
            await plant.Ok(e, $"/api/engineering/items/{part}/revisions", new { revision = "A" });
        }

        await plant.Ok(e, "/api/engineering/items/HARN-1/revisions/A/bom", new { lineNumber = 1, componentPartNumber = "CONN-8", quantityPer = 1m, scrapPercent = 0m });
        await plant.Ok(e, "/api/engineering/items/HARN-1/revisions/A/routing", new
        {
            sequence = 10, description = "Terminate", workCenterCode = "SLIT-WC", activityCode = "RUN_SLITTER", eligibleMachines = new[] { "SLITTER-A" },
            setupMinutes = 0m, runMinutesPerUnit = 1m, crewSize = 1,
        });
        foreach (var part in new[] { "CONN-8", "SLEEVE-2", "HARN-1" })
            await plant.Ok(plant.Approver, $"/api/engineering/items/{part}/revisions/A/release", new { reason = "Baseline" });

        var stores = plant.As("stores");
        await plant.Ok(stores, "/api/inventory/locations", new { code = "SL-STAGE", siteCode = "PLANT1", name = "Slitting staging", kind = "Staging", workCenterCode = "SLIT-WC" });
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "CONN-8", quantity = connectors, locationCode = "STORES", reason = "Opening balance" });
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "SLEEVE-2", quantity = 50m, locationCode = "STORES", reason = "Opening balance" });
        // The line already holds the order's 20 connectors (received straight to staging, so no task or issue covers the demand):
        // starting work needs them at the point of use.
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "CONN-8", quantity = 20m, locationCode = "SL-STAGE", reason = "Line-side balance" });
        await plant.EnsureCustodyAsync();

        var order = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "HARN-1", quantity = 20m, dueDate = DateTime.UtcNow.AddDays(10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), priority = 3 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Customer order" });

        var kiosk = plant.As("kiosk");
        await plant.Ok(kiosk, "/api/shopfloor/clock-in", new { badge = SarahBadge });
        await plant.Ok(kiosk, "/api/shopfloor/start", new { badge = SarahBadge, orderNumber = order, sequence = 10, machineCode = "SLITTER-A" });
        return new Floor(plant, planner, kiosk, stores, plant.As("sup"), order);
    }

    [Fact]
    public async Task Suspect_scrap_replacement_retried_requests_and_additional_material_stay_controlled()
    {
        var errors = new DiagnosticErrors();
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), services => services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(errors));
        var (plant, planner, kiosk, stores, sup, order) = await BuildAsync(app, 30m);
        await plant.Person("qe", "QualityEngineer");
        await plant.Person("ic2", "InventoryController");
        var (qe, ic2) = (plant.As("qe"), plant.As("ic2"));

        // WW-S10-23: eight suspect connectors are reported for Quality; nothing is scrapped or moved.
        var reported = await plant.Ok(kiosk, "/api/shopfloor/suspect-scrap", new
        {
            badge = SarahBadge, kind = "Scrap", orderNumber = order, sequence = 10, partNumber = "CONN-8", quantity = 8m,
            reasonCode = "PIN-BRIDGE", description = "Bridged pins on eight connectors", reason = "Operator report",
        });
        var reportId = Id(reported);
        Assert.Contains("no stock was deducted or dispositioned", reported.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(30m, await Stock(stores, "CONN-8", "STORES"));
        Assert.DoesNotContain((await Get(stores, "/api/inventory/transactions?part=CONN-8")).EnumerateArray(), t => t.GetProperty("kind").GetString() == "Scrap");
        var report = Assert.Single((await Get(qe, "/api/quality/suspect-scrap")).EnumerateArray());
        Assert.Equal((8m, JsonValueKind.Null, JsonValueKind.Null), (report.GetProperty("quantity").GetDecimal(), report.GetProperty("ncrId").ValueKind, report.GetProperty("replacementTaskId").ValueKind));
        await PropagateAsync(app);
        Assert.Contains((await Get(qe, "/api/notifications/inbox")).EnumerateArray(), a =>
            a.GetProperty("entityId").GetString() == reportId.ToString(CultureInfo.InvariantCulture) && a.GetProperty("message").GetString()!.Contains("needs Quality review", StringComparison.Ordinal));

        // WW-S10-25: one replacement, linked to the report, no larger than it and only once; it waits in the stockroom queue.
        var replacementUrl = $"/api/quality/suspect-scrap/{reportId}/replacement-request";
        await Rejected(plant, sup, replacementUrl, new { quantity = 9m, toLocation = "SL-STAGE", reason = "Replace" }, "replacement_request_context_mismatch");
        var replacement = Id(await plant.Ok(sup, replacementUrl, new { quantity = 5m, toLocation = "SL-STAGE", reason = "Replace the five confirmed bad" }));
        await Rejected(plant, sup, replacementUrl, new { quantity = 5m, toLocation = "SL-STAGE", reason = "Again" }, "replacement_already_requested");
        Assert.Equal(replacement, Assert.Single((await Get(qe, "/api/quality/suspect-scrap")).EnumerateArray()).GetProperty("replacementTaskId").GetInt64());
        var linked = await TaskById(stores, order, replacement);
        Assert.Equal(("Requested", reportId, 5m), (linked.GetProperty("status").GetString(), linked.GetProperty("sourceSuspectReportId").GetInt64(), linked.GetProperty("quantity").GetDecimal()));
        await PropagateAsync(app);
        Assert.Contains((await Get(stores, "/api/notifications/inbox")).EnumerateArray(), a =>
            a.GetProperty("entityId").GetString() == replacement.ToString(CultureInfo.InvariantCulture) && a.GetProperty("message").GetString()!.Contains("queued for stockroom fulfillment", StringComparison.Ordinal));

        // WW-S10-34: a terminal retry with the same key is the same request, not a second one.
        var operatorRequest = new { badge = SarahBadge, orderNumber = order, sequence = 10, partNumber = "CONN-8", quantity = 3m, toLocation = "SL-STAGE", reason = "Short at the line" };
        var first = await kiosk.CommandAsync("/api/shopfloor/material-request", operatorRequest, "mat10-retry-1");
        var retry = await kiosk.CommandAsync("/api/shopfloor/material-request", operatorRequest, "mat10-retry-1");
        Assert.True(first.IsSuccessStatusCode && retry.IsSuccessStatusCode, await retry.Content.ReadAsStringAsync());
        var firstId = Id(JsonDocument.Parse(await first.Content.ReadAsStringAsync()).RootElement);
        Assert.Equal(firstId, Id(JsonDocument.Parse(await retry.Content.ReadAsStringAsync()).RootElement));
        Assert.Single((await Get(stores, $"{Tasks}?open=false&order={order}")).EnumerateArray(), t => t.GetProperty("quantity").GetDecimal() == 3m);

        // WW-S10-28: additional material needs a reason and an independent decision; it cannot be allocated first and never amends the order.
        await Rejected(plant, kiosk, "/api/shopfloor/material-request",
            new { badge = SarahBadge, orderNumber = order, sequence = 10, partNumber = "SLEEVE-2", quantity = 4m, toLocation = "SL-STAGE", reason = "Sleeves for rework" }, "not_on_order");
        var extraResponse = await plant.Try(kiosk, "/api/shopfloor/material-request",
            new { badge = SarahBadge, orderNumber = order, sequence = 10, partNumber = "SLEEVE-2", quantity = 4m, toLocation = "SL-STAGE", reason = "Sleeves for rework", allowNonBomMaterial = true });
        var extraText = await extraResponse.Content.ReadAsStringAsync();
        Assert.True(extraResponse.IsSuccessStatusCode, extraText + "\n" + string.Join("\n", errors.Lines));
        var extra = JsonDocument.Parse(extraText).RootElement.Clone();
        var extraId = Id(extra);
        Assert.Contains("awaiting independent", extra.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal("ApprovalNeeded", (await TaskById(stores, order, extraId)).GetProperty("status").GetString());
        await Rejected(plant, stores, $"{Tasks}/{extraId}/allocate", new { fromLocation = "STORES" }, "wrong_step");
        await Rejected(plant, stores, Tasks, new { kind = "Stage", orderNumber = order, partNumber = "SLEEVE-2", quantity = 2m, toLocation = "SL-STAGE", fromLocation = "STORES", operationSequence = 10, allowNonBomMaterial = true, reason = "Pre-pick" }, "approval_before_allocation");
        var own = Id(await plant.Ok(stores, Tasks, new { kind = "Stage", orderNumber = order, partNumber = "SLEEVE-2", quantity = 2m, toLocation = "SL-STAGE", operationSequence = 10, allowNonBomMaterial = true, reason = "Spare sleeves" }));
        await Rejected(plant, stores, $"{Tasks}/{own}/review", new { approve = true, reason = "Mine" }, "independent_review_required");
        await PropagateAsync(app);
        Assert.Contains((await Get(planner, "/api/notifications/inbox")).EnumerateArray(), a =>
            a.GetProperty("entityId").GetString() == extraId.ToString(CultureInfo.InvariantCulture) && a.GetProperty("message").GetString()!.Contains("needs an independent decision", StringComparison.Ordinal));
        await plant.Ok(ic2, $"{Tasks}/{extraId}/review", new { approve = true, reason = "Rework sleeves approved" });
        await plant.Ok(ic2, $"{Tasks}/{own}/review", new { approve = false, reason = "Not needed" });
        Assert.Equal(("Requested", "Rejected"), ((await TaskById(stores, order, extraId)).GetProperty("status").GetString(), (await TaskById(stores, order, own)).GetProperty("status").GetString()));
        var materials = (await Get(planner, $"/api/orders/{order}")).GetProperty("materials").EnumerateArray().Select(m => m.GetProperty("componentPartNumber").GetString()).ToArray();
        Assert.Equal(new[] { "CONN-8" }, materials);

        // WW-S10-38 (roles): the worker, the terminal and the planner each stay inside their own step.
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(plant.As("sarah"), Tasks, new { kind = "Stage", orderNumber = order, partNumber = "CONN-8", quantity = 1m, toLocation = "SL-STAGE", reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(kiosk, $"{Tasks}/{replacement}/allocate", new { fromLocation = "STORES", reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(planner, $"{Tasks}/{firstId}/cancel", new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.Try(kiosk, $"/api/quality/suspect-scrap/{reportId}/ncr", new { reason = "x" })).StatusCode);
    }

    [Fact]
    public async Task Held_and_last_stock_is_never_double_reserved_and_cancelling_after_a_partial_issue_reopens_only_the_rest()
    {
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync());
        var (plant, _, _, stores, _, order) = await BuildAsync(app, 14m);
        await plant.Ok(stores, "/api/inventory/holds", new { partNumber = "CONN-8", quantity = 4m, locationCode = "STORES", reason = "Suspect supplier lot" });
        Assert.Equal((10m, 4m), (await Stock(stores, "CONN-8", "STORES"), await Stock(stores, "CONN-8", "STORES", "Hold")));
        object Stage(decimal quantity) => new { kind = "Stage", orderNumber = order, partNumber = "CONN-8", quantity, toLocation = "SL-STAGE", operationSequence = 10 };

        // WW-S10-33: 14 are on the shelf but 4 are held, so 12 cannot be allocated.
        var twelve = Id(await plant.Ok(stores, Tasks, Stage(12)));
        await Rejected(plant, stores, $"{Tasks}/{twelve}/allocate", new { fromLocation = "STORES" }, "insufficient_unallocated");
        await plant.Ok(stores, $"{Tasks}/{twelve}/cancel", new { reason = "Split into what is available" });

        // WW-S10-27: two requests race for the last 10 released connectors; only one reserves them.
        var (a, b) = (Id(await plant.Ok(stores, Tasks, Stage(10))), Id(await plant.Ok(stores, Tasks, Stage(10))));
        var race = await Task.WhenAll(
            plant.Try(plant.As("stores"), $"{Tasks}/{a}/allocate", new { fromLocation = "STORES" }),
            plant.Try(plant.As("stores"), $"{Tasks}/{b}/allocate", new { fromLocation = "STORES" }));
        Assert.Single(race, response => response.IsSuccessStatusCode);
        var allocated = (await Get(stores, $"{Tasks}?open=false&order={order}")).EnumerateArray().Where(t => t.GetProperty("status").GetString() == "Allocated").ToList();
        var winner = Assert.Single(allocated).GetProperty("taskId").GetInt64();
        var loser = winner == a ? b : a;
        await Rejected(plant, stores, $"{Tasks}/{loser}/allocate", new { fromLocation = "STORES" }, "insufficient_unallocated");

        // WW-S10-35: six of the ten are found, delivered, received and issued; the other request is cancelled. The unfilled demand
        // (20 - 6 = 14) opens again, nothing more, and the cancelled and short tasks stay in the history.
        await plant.Ok(stores, $"{Tasks}/{winner}/pick", new { scannedLocation = "STORES", quantity = 6m });
        await plant.Ok(stores, $"{Tasks}/{winner}/deliver", new { scannedLocation = "SL-STAGE" });
        await plant.ReceiveAsync(winner, "SL-STAGE", 6m);
        await plant.Ok(stores, "/api/inventory/issues", new { orderNumber = order, partNumber = "CONN-8", quantity = 6m, locationCode = "SL-STAGE" });
        await plant.Ok(stores, $"{Tasks}/{loser}/cancel", new { reason = "Stock exhausted; replan the rest" });
        await Rejected(plant, stores, Tasks, Stage(15), "over_request");
        var rest = Id(await plant.Ok(stores, Tasks, Stage(14)));
        var history = (await Get(stores, $"{Tasks}?open=false&order={order}")).EnumerateArray().ToDictionary(t => t.GetProperty("taskId").GetInt64());
        Assert.Equal(("Cancelled", "Stock exhausted; replan the rest"), (history[loser].GetProperty("status").GetString(), history[loser].GetProperty("cancelReason").GetString()));
        Assert.Equal(("Delivered", 6m), (history[winner].GetProperty("status").GetString(), history[winner].GetProperty("pickedQuantity").GetDecimal()));
        Assert.Equal("Cancelled", history[twelve].GetProperty("status").GetString());
        Assert.Equal("Requested", history[rest].GetProperty("status").GetString());
        Assert.Equal((4m, 4m), (await Stock(stores, "CONN-8", "STORES"), await Stock(stores, "CONN-8", "STORES", "Hold")));
    }

    private sealed class DiagnosticErrors : Microsoft.Extensions.Logging.ILoggerProvider, Microsoft.Extensions.Logging.ILogger
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Lines { get; } = new();

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= Microsoft.Extensions.Logging.LogLevel.Error;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) Lines.Enqueue(formatter(state, exception) + " " + exception);
        }

        public void Dispose()
        {
        }
    }
}
