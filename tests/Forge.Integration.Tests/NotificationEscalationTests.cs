using System.Globalization;
using System.Text.Json;
using Forge.Host.Notifications;
using Forge.Kernel.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Forge.Integration.Tests;

/// <summary>
/// Sprint 10 WW-S10-31 through the real API and delivery worker: an unfilled material request alerts the stockroom and planners once,
/// reminds them, escalates to the escalation role exactly once when the configured threshold passes, and resolves when the request is
/// fulfilled; a request fulfilled before the threshold never escalates.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class NotificationEscalationTests(PostgresFixture postgres)
{
    private const string RuleName = "Material request awaiting stockroom fulfillment";

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Set(DateTimeOffset at) => now = at;
    }

    private sealed class RecordingEmail : INotificationEmailProvider
    {
        public List<NotificationEmailMessage> Messages { get; } = [];

        public Task SendAsync(NotificationEmailMessage message, string idempotencyKey, CancellationToken ct)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

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
    }

    /// <summary>(kind, status) of every email delivery queued for the rule's alert about one task.</summary>
    private static async Task<List<(string Kind, string Status, Guid Person)>> DeliveriesAsync(ForgeApp app, long taskId)
    {
        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT d.kind, d.status, d.person_id FROM kernel.notification_delivery d
            JOIN kernel.notification_alert a ON a.alert_id = d.alert_id JOIN kernel.notification_rule r ON r.rule_id = a.rule_id
            WHERE r.name = $1 AND a.entity_id = $2 ORDER BY d.kind, d.person_id
            """, connection);
        cmd.Parameters.AddWithValue(RuleName);
        cmd.Parameters.AddWithValue(taskId.ToString(CultureInfo.InvariantCulture));
        var rows = new List<(string, string, Guid)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetGuid(2)));
        return rows;
    }

    [Fact]
    public async Task An_unfilled_request_escalates_once_at_the_threshold_and_resolves_when_fulfilled()
    {
        var t0 = DateTimeOffset.UtcNow;
        var clock = new Clock(t0);
        var email = new RecordingEmail();
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.RemoveAll<INotificationEmailProvider>();
            services.AddSingleton<INotificationEmailProvider>(email);
        });
        var (plant, planner) = await SlittingPlant.BuildEngineeredAsync(app);
        var stores = plant.As("stores");
        await plant.EnsureCustodyAsync();
        await plant.Ok(stores, "/api/inventory/locations", new { code = "SL-STAGE", siteCode = "PLANT1", name = "Slitting staging", kind = "Staging", workCenterCode = "SLIT-WC" });
        var order = Id(await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = "ROLL-100", quantity = 100m, dueDate = t0.AddDays(10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), priority = 1 }));
        await plant.Ok(planner, $"/api/orders/{order}/release", new { reason = "Rush order" });

        // The administrator turns on email for the seeded rule (two-hour escalation to planners), as the migration note says.
        var rule = (await Get(plant.Admin, "/api/notifications/rules")).EnumerateArray().Single(r => r.GetProperty("name").GetString() == RuleName);
        Assert.Equal(7200, rule.GetProperty("escalationSeconds").GetInt32());
        var draft = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(rule.GetRawText())!;
        var body = draft.ToDictionary(p => p.Key, p => (object?)p.Value);
        body["channels"] = new[] { "InApp", "Email" };
        body["reason"] = "Verified sender configured";
        await plant.Ok(plant.Admin, "/api/notifications/rules", body);

        object Stage(decimal quantity) => new { kind = "Stage", orderNumber = order, partNumber = "COIL-AL", quantity, toLocation = "SL-STAGE" };
        var unfilled = Id(await plant.Ok(stores, "/api/inventory/tasks", Stage(50m)));
        var quick = Id(await plant.Ok(stores, "/api/inventory/tasks", Stage(10m)));
        await PropagateAsync(app);
        var delivery = app.Services.GetRequiredService<NotificationDeliveryService>();
        async Task DispatchAt(TimeSpan after)
        {
            clock.Set(t0 + after);
            while (await delivery.DispatchDueAsync(50, CancellationToken.None) > 0)
            {
            }
        }

        await DispatchAt(TimeSpan.Zero);
        var recipients = (await DeliveriesAsync(app, unfilled)).Where(d => d.Kind == "Alert").Select(d => d.Person).ToHashSet();
        Assert.Contains(plant.People["planner"], recipients);
        Assert.Contains(plant.People["stores"], recipients);
        Assert.All(await DeliveriesAsync(app, unfilled), d => Assert.Equal(d.Kind == "Alert" ? "Sent" : "Pending", d.Status));
        var escalation = Assert.Single(await DeliveriesAsync(app, unfilled), d => d.Kind == "Escalation");
        Assert.Equal(plant.People["planner"], escalation.Person); // only the escalation role

        // The quick request is fulfilled within the hour: its alert resolves and its reminder and escalation are never sent.
        clock.Set(t0.AddMinutes(10));
        await plant.Ok(stores, $"/api/inventory/tasks/{quick}/allocate", new { fromLocation = "STORES" });
        await plant.Ok(stores, $"/api/inventory/tasks/{quick}/pick", new { scannedLocation = "STORES" });
        await plant.Ok(stores, $"/api/inventory/tasks/{quick}/deliver", new { scannedLocation = "SL-STAGE" });
        await plant.ReceiveAsync(quick, "SL-STAGE", 10m);
        await PropagateAsync(app);
        await DispatchAt(TimeSpan.FromMinutes(10));
        var quickRows = await DeliveriesAsync(app, quick);
        Assert.All(quickRows.Where(d => d.Kind is "Reminder" or "Escalation"), d => Assert.Equal("Suppressed", d.Status));
        Assert.All(quickRows.Where(d => d.Kind == "Resolution"), d => Assert.Equal("Sent", d.Status));

        // The unfilled one is reminded after an hour, still not escalated just before two hours, and escalated once after.
        await DispatchAt(TimeSpan.FromSeconds(3601));
        Assert.All((await DeliveriesAsync(app, unfilled)).Where(d => d.Kind == "Reminder"), d => Assert.Equal("Sent", d.Status));
        await DispatchAt(TimeSpan.FromSeconds(7100));
        Assert.Equal("Pending", Assert.Single(await DeliveriesAsync(app, unfilled), d => d.Kind == "Escalation").Status);
        var sentBefore = email.Messages.Count;
        await DispatchAt(TimeSpan.FromSeconds(7201));
        Assert.Equal("Sent", Assert.Single(await DeliveriesAsync(app, unfilled), d => d.Kind == "Escalation").Status);
        var escalated = Assert.Single(email.Messages.Skip(sentBefore));
        Assert.Equal(plant.People["planner"], escalated.RecipientPersonId);
        Assert.Equal("#/inventory?tab=tasks", escalated.DeepLink);
        await DispatchAt(TimeSpan.FromHours(5));
        Assert.Equal(sentBefore + 1, email.Messages.Count);

        // Fulfilling it resolves the alert in the inbox and sends the resolution; nothing escalates again.
        await plant.Ok(stores, $"/api/inventory/tasks/{unfilled}/allocate", new { fromLocation = "STORES" });
        await plant.Ok(stores, $"/api/inventory/tasks/{unfilled}/pick", new { scannedLocation = "STORES" });
        await plant.Ok(stores, $"/api/inventory/tasks/{unfilled}/deliver", new { scannedLocation = "SL-STAGE" });
        await plant.ReceiveAsync(unfilled, "SL-STAGE", 50m);
        await PropagateAsync(app);
        await DispatchAt(TimeSpan.FromHours(6));
        var alert = (await Get(planner, "/api/notifications/inbox")).EnumerateArray()
            .Single(a => a.GetProperty("title").GetString() == RuleName && a.GetProperty("entityId").GetString() == unfilled.ToString(CultureInfo.InvariantCulture));
        Assert.Equal("Resolved", alert.GetProperty("state").GetString());
        var rows = await DeliveriesAsync(app, unfilled);
        Assert.Single(rows, d => d.Kind == "Escalation");
        Assert.All(rows.Where(d => d.Kind == "Resolution"), d => Assert.Equal("Sent", d.Status));
    }
}
