using System.Globalization;
using System.Text.Json;
using Forge.Host.Notifications;
using Forge.Kernel.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Forge.Integration.Tests;

/// <summary>
/// Sprint 10 WW-S10-30 through the real API and delivery worker: a released order whose material cannot be covered becomes at risk,
/// and planners get one actionable notice (in the inbox and, once email is on, one email) with a link to the order, but only for
/// orders they may see. Checking again changes nothing; covering the material clears the notice.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MaterialRiskNotificationTests(PostgresFixture postgres)
{
    private const string RuleName = "Order at risk from material";

    private sealed class RecordingEmail : INotificationEmailProvider
    {
        public List<NotificationEmailMessage> Messages { get; } = [];

        public Task SendAsync(NotificationEmailMessage message, string idempotencyKey, CancellationToken ct)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
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

    private static async Task<List<JsonElement>> Alerts(HttpClient client) =>
        (await Get(client, "/api/notifications/inbox")).EnumerateArray().Where(a => a.GetProperty("title").GetString() == RuleName).ToList();

    [Fact]
    public async Task A_projected_shortage_notifies_each_permitted_planner_once_with_a_link_and_clears_when_covered()
    {
        var email = new RecordingEmail();
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), services =>
        {
            services.RemoveAll<INotificationEmailProvider>();
            services.AddSingleton<INotificationEmailProvider>(email);
        });
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var e = plant.Engineer;
        var stores = plant.As("stores");

        // A private program's planner sees its order; the plant planner does not.
        await plant.Person("programs", "ProgramManager");
        var programId = Guid.Parse((await plant.Ok(plant.As("programs"), "/api/programs/", new { code = "RISK-P", name = "Private material risk program" })).GetProperty("entityId").GetString()!);
        var programPlanner = await plant.Person("planner-p", "Planner");
        foreach (var person in new[] { programPlanner, plant.People["eng"] })
            await plant.Ok(plant.Admin, $"/api/kernel/people/{person:D}/programs", new { programId, reason = "Program access" });
        var plannerP = plant.As("planner-p");

        await plant.Ok(e, "/api/engineering/items", new { partNumber = "WIRE-9", description = "Lead wire", unitOfMeasure = "M", makeBuy = "Buy", leadTimeDays = 6 });
        await plant.Ok(e, "/api/engineering/items", new { partNumber = "LEAD-9", description = "Lead assembly", unitOfMeasure = "EA", makeBuy = "Make", leadTimeDays = 2 });
        foreach (var part in new[] { "WIRE-9", "LEAD-9" }) await plant.Ok(e, $"/api/engineering/items/{part}/revisions", new { revision = "A" });
        await plant.Ok(e, "/api/engineering/items/LEAD-9/revisions/A/bom", new { lineNumber = 1, componentPartNumber = "WIRE-9", quantityPer = 1m, scrapPercent = 0m });
        await plant.Ok(e, "/api/engineering/items/LEAD-9/revisions/A/routing", new
        {
            sequence = 10, description = "Cut", workCenterCode = "SLIT-WC", activityCode = "RUN_SLITTER", eligibleMachines = new[] { "SLITTER-A" }, setupMinutes = 0m, runMinutesPerUnit = 1m, crewSize = 1,
        });
        foreach (var part in new[] { "WIRE-9", "LEAD-9" }) await plant.Ok(plant.Approver, $"/api/engineering/items/{part}/revisions/A/release", new { reason = "Baseline" });
        var open = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "LEAD-9", quantity = 20m, dueDate = Day(8), priority = 3 }));
        var hidden = Id(await plant.Ok(e, "/api/orders/", new { partNumber = "LEAD-9", quantity = 5m, dueDate = Day(8), priority = 3, programId }));
        await plant.Ok(planner, $"/api/orders/{open}/release", new { reason = "Customer order" });
        await plant.Ok(plannerP, $"/api/orders/{hidden}/release", new { reason = "Program order" });

        // Email is switched on by an administrator once the sender is configured.
        var rule = (await Get(plant.Admin, "/api/notifications/rules")).EnumerateArray().Single(r => r.GetProperty("name").GetString() == RuleName);
        var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(rule.GetRawText())!.ToDictionary(p => p.Key, p => (object?)p.Value);
        body["channels"] = new[] { "InApp", "Email" };
        body["reason"] = "Verified sender configured";
        await plant.Ok(plant.Admin, "/api/notifications/rules", body);

        // No wire anywhere: both orders are at risk. Each planner hears about the orders they may see, once, with a link to the order.
        var scan = await plant.Ok(planner, "/api/planning/material-risk/scan", new { reason = "Morning check" });
        Assert.Contains("2 newly at risk", scan.GetProperty("message").GetString(), StringComparison.Ordinal);
        await PropagateAsync(app);
        var mine = Assert.Single(await Alerts(planner));
        Assert.Equal((open.ToString(CultureInfo.InvariantCulture), $"#/orders/{open}"), (mine.GetProperty("entityId").GetString(), mine.GetProperty("deepLink").GetString()));
        Assert.Contains("is short of material", mine.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("WIRE-9 needs 20", mine.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(2, (await Alerts(plannerP)).Count);
        Assert.Single((await Get(planner, "/api/planning/material-risk")).EnumerateArray());
        var toPlanner = email.Messages.Where(m => m.RecipientPersonId == plant.People["planner"]).ToList();
        Assert.Equal($"#/orders/{open}", Assert.Single(toPlanner).DeepLink);
        Assert.Equal(2, email.Messages.Count(m => m.RecipientPersonId == programPlanner));

        // Checking again while nothing changed raises nothing.
        var sent = email.Messages.Count;
        Assert.Equal("no_change", (await plant.Ok(planner, "/api/planning/material-risk/scan", new { reason = "Again" })).GetProperty("code").GetString());
        await PropagateAsync(app);
        Assert.Equal(sent, email.Messages.Count);
        Assert.Single(await Alerts(planner));

        // Wire arrives for both orders: the notices clear.
        await plant.Ok(stores, "/api/inventory/receipts", new { partNumber = "WIRE-9", quantity = 25m, locationCode = "STORES", reason = "Supplier delivery" });
        Assert.Contains("2 cleared", (await plant.Ok(planner, "/api/planning/material-risk/scan", new { reason = "After receipt" })).GetProperty("message").GetString(), StringComparison.Ordinal);
        await PropagateAsync(app);
        Assert.Equal("Resolved", Assert.Single(await Alerts(planner)).GetProperty("state").GetString());
        Assert.Empty((await Get(plannerP, "/api/planning/material-risk")).EnumerateArray());
    }
}
