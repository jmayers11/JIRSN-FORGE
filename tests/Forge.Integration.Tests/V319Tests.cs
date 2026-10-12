using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Host.Notifications;
using Forge.Kernel.Events;
using Forge.Kernel.Access;
using Forge.Kernel.Identity;
using Forge.Kernel.Postgres;
using Forge.Kernel.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class V319Tests(PostgresFixture postgres)
{
    [Fact]
    public async Task Notification_rules_deduplicate_resolve_respect_current_program_access_and_delivery_failure_is_separate()
    {
        var email = new RecordingEmailProvider { Fail = true };
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), services =>
        {
            services.RemoveAll<INotificationEmailProvider>();
            services.AddSingleton<INotificationEmailProvider>(email);
        });
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        var recipientId = await plant.Person("notification-supervisor", "Supervisor");
        var outsider = await plant.Person("notification-outsider", "Planner");
        var programId = ForgeIds.New(TimeProvider.System);
        await plant.Ok(plant.Admin, $"/api/kernel/people/{recipientId:D}/programs", new { programId, reason = "Allow scoped alert delivery" });

        var saved = await plant.Ok(plant.Admin, "/api/notifications/rules", new
        {
            name = "Slitter operation alert", active = true, triggerEventType = "OperationStarted", clearEventType = "WorkStopped",
            scopeType = "Program", scopeId = programId.ToString("D"), conditionField = "status", conditionEquals = "Started",
            severity = "ActionRequired", confidence = "Low", recipientRoles = new[] { "Supervisor" }, escalationRoles = new[] { "Supervisor" },
            channels = new[] { "InApp", "Email" }, template = "{eventType} on {entityType} {entityId}", resolutionTemplate = "Cleared by {eventType} on {entityId}",
            deepLinkTemplate = "#/orders/{entityId}", delaySeconds = 0, reminderSeconds = 600, escalationSeconds = 1200,
            quietStart = "22:00:00", quietEnd = "06:00:00", timeZone = "America/New_York", dedupSeconds = 3600, reason = "Create scoped delivery rule",
        });
        Assert.True(Guid.TryParse(saved.GetProperty("entityId").GetString(), out _));

        var at = DateTimeOffset.UtcNow;
        var alertEvent = new EventEnvelope
        {
            EventId = ForgeIds.New(TimeProvider.System), EventType = "OperationStarted", SchemaVersion = 1,
            OccurredAt = at, RecordedAt = at, SourceContext = "Execution", EntityType = "Order", EntityId = "74291",
            Actor = "person:test", CorrelationId = ForgeIds.New(TimeProvider.System), ProgramId = new ProgramId(programId),
            Payload = JsonSerializer.Serialize(new { status = "Started", workCenterCode = "SLIT-WC" }),
        };
        await PublishAsync(app, alertEvent); // the source command commits before the async subscriber and email provider run
        await app.Services.GetRequiredService<NotificationEventSubscriber>().HandleAsync(1, alertEvent, CancellationToken.None); // at-least-once replay deduplicates the open condition

        var recipient = plant.As("notification-supervisor");
        var inbox = await recipient.GetFromJsonAsync<JsonElement[]>("/api/notifications/inbox?openOnly=true");
        var alert = Assert.Single(inbox!);
        var alertId = alert.GetProperty("alertId").GetGuid();
        Assert.Equal("ActionRequired", alert.GetProperty("severity").GetString());
        Assert.Equal("Low", alert.GetProperty("confidence").GetString());
        Assert.Equal("#/orders/74291", alert.GetProperty("deepLink").GetString());
        Assert.Empty((await plant.As("notification-outsider").GetFromJsonAsync<JsonElement[]>("/api/notifications/inbox")) ?? []);

        await plant.Ok(recipient, $"/api/notifications/alerts/{alertId:D}/acknowledge", new { reason = "Review alert and acknowledge" });
        var acknowledged = (await recipient.GetFromJsonAsync<JsonElement[]>("/api/notifications/inbox?openOnly=true"))!.Single();
        Assert.True(acknowledged.GetProperty("acknowledgedAt").GetDateTimeOffset() <= DateTimeOffset.UtcNow);

        var clearEvent = alertEvent with
        {
            EventId = ForgeIds.New(TimeProvider.System), EventType = "WorkStopped", OccurredAt = at.AddMinutes(5), RecordedAt = at.AddMinutes(5),
            Payload = JsonSerializer.Serialize(new { status = "Stopped", workCenterCode = "SLIT-WC" }),
        };
        await PublishAsync(app, clearEvent);
        var resolved = (await recipient.GetFromJsonAsync<JsonElement[]>("/api/notifications/inbox"))!.Single();
        Assert.Equal("Resolved", resolved.GetProperty("state").GetString());
        Assert.Contains("WorkStopped", resolved.GetProperty("resolutionMessage").GetString(), StringComparison.Ordinal);
        Assert.Null(resolved.GetProperty("resolutionAcknowledgedAt").GetDateTimeOffsetOrNull());
        Assert.Single((await recipient.GetFromJsonAsync<JsonElement[]>("/api/notifications/inbox?unreadOnly=true"))!);
        await plant.Ok(recipient, $"/api/notifications/alerts/{alertId:D}/acknowledge", new { reason = "Acknowledge resolution notice" });
        Assert.Empty((await recipient.GetFromJsonAsync<JsonElement[]>("/api/notifications/inbox?unreadOnly=true")) ?? []);

        // Removing the recipient's program access immediately hides the historical alert and its deep link target.
        await plant.Ok(plant.Admin, $"/api/kernel/people/{recipientId:D}/programs/{programId:D}/revoke", new { reason = "Verify current access filtering" });
        Assert.Empty((await recipient.GetFromJsonAsync<JsonElement[]>("/api/notifications/inbox")) ?? []);
        var hiddenAck = await plant.Try(recipient, $"/api/notifications/alerts/{alertId:D}/acknowledge", new { reason = "Must not resolve hidden records" });
        Assert.Equal(HttpStatusCode.NotFound, hiddenAck.StatusCode);

        // The delivery provider fails after the event and alert transaction committed; retry state is durable and alert state stays resolved.
        Assert.Equal(1, await app.Services.GetRequiredService<NotificationDeliveryService>().DispatchDueAsync(20, CancellationToken.None));
        Assert.Single(email.Messages);
        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync();
        await using var check = new NpgsqlCommand("SELECT a.state,d.status FROM kernel.notification_alert a JOIN kernel.notification_delivery d ON d.alert_id=a.alert_id WHERE a.alert_id=$1 AND d.kind='Resolution'", connection);
        check.Parameters.AddWithValue(alertId);
        await using var reader = await check.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Resolved", reader.GetString(0));
        Assert.Equal("Failed", reader.GetString(1));
    }

    private static async Task PublishAsync(ForgeApp app, EventEnvelope envelope)
    {
        await using (var uow = await app.Services.GetRequiredService<IKernelUnitOfWorkFactory>().BeginAsync(CancellationToken.None))
        {
            await uow.Outbox.EnqueueAsync(envelope, CancellationToken.None);
            await uow.CommitAsync(CancellationToken.None);
        }
        await app.Services.GetRequiredService<OutboxRelay>().RelayBatchAsync(1_000_000, CancellationToken.None);
        await app.Services.GetRequiredService<SubscriptionPump>().PumpOnceAsync(1_000_000, CancellationToken.None);
    }

    private sealed class RecordingEmailProvider : INotificationEmailProvider
    {
        public bool Fail { get; init; }
        public List<NotificationEmailMessage> Messages { get; } = [];
        public Task SendAsync(NotificationEmailMessage message, string idempotencyKey, CancellationToken ct)
        {
            Messages.Add(message);
            return Fail ? Task.FromException(new HttpRequestException("test delivery unavailable")) : Task.CompletedTask;
        }
    }
}
