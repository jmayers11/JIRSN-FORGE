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
        await plant.Ok(plant.Admin, "/api/kernel/roles", new
        {
            role = "V319Recipient", displayName = "Dedicated scoped alert recipient",
            permissions = new[] { Permissions.OrdersRead, Permissions.NotificationsRead, Permissions.NotificationsAcknowledge },
            reason = "Isolate the rule recipient from the source author",
        });
        var recipientId = await plant.Person("notification-supervisor", "Supervisor", "V319Recipient");
        var outsider = await plant.Person("notification-outsider", "Planner");
        await plant.Person("notification-programs", "ProgramManager");
        var programId = Guid.Parse((await plant.Ok(plant.As("notification-programs"), "/api/programs/", new { code = "NOTIFY-A", name = "Actual notification source program" })).GetProperty("entityId").GetString()!);
        await plant.Ok(plant.Admin, $"/api/kernel/people/{recipientId:D}/programs", new { programId, reason = "Allow scoped alert delivery" });
        await plant.Ok(plant.Admin, $"/api/kernel/people/{plant.People["eng"]:D}/programs", new { programId, reason = "Create an actual private source" });
        var orderId = (await plant.Ok(plant.Engineer, "/api/orders/", new { partNumber = "ROLL-100", quantity = 10m, dueDate = DateTime.UtcNow.AddDays(10).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), priority = 3, programId })).GetProperty("entityId").GetString()!;

        var saved = await plant.Ok(plant.Admin, "/api/notifications/rules", new
        {
            name = "Slitter operation alert", active = true, triggerEventType = "OperationStarted", clearEventType = "WorkStopped",
            scopeType = "Program", scopeId = programId.ToString("D"), conditionField = "status", conditionEquals = "Started",
            severity = "ActionRequired", confidence = "Low", recipientRoles = new[] { "V319Recipient" }, escalationRoles = new[] { "V319Recipient" },
            channels = new[] { "InApp", "Email" }, template = "{eventType} on {entityType} {entityId}", resolutionTemplate = "Cleared by {eventType} on {entityId}",
            deepLinkTemplate = "#/orders/{entityId}", delaySeconds = 0, reminderSeconds = 600, escalationSeconds = 1200,
            quietStart = (TimeOnly?)null, quietEnd = (TimeOnly?)null, timeZone = "America/New_York", dedupSeconds = 3600, reason = "Create scoped delivery rule",
        });
        Assert.True(Guid.TryParse(saved.GetProperty("entityId").GetString(), out _));

        var at = DateTimeOffset.UtcNow;
        var alertEvent = new EventEnvelope
        {
            EventId = ForgeIds.New(TimeProvider.System), EventType = "OperationStarted", SchemaVersion = 1,
            OccurredAt = at, RecordedAt = at, SourceContext = "Execution", EntityType = "Order", EntityId = orderId,
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
        Assert.Equal("#/orders/" + orderId, alert.GetProperty("deepLink").GetString());
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

        // The configured provider can fail after source and alert state have committed.
        Assert.Equal(1, await app.Services.GetRequiredService<NotificationDeliveryService>().DispatchDueAsync(20, CancellationToken.None));
        Assert.Single(email.Messages);

        // Removing the recipient's program access immediately hides the historical alert and its deep link target.
        await plant.Ok(plant.Admin, $"/api/kernel/people/{recipientId:D}/programs/{programId:D}/revoke", new { reason = "Verify current access filtering" });
        Assert.Empty((await recipient.GetFromJsonAsync<JsonElement[]>("/api/notifications/inbox")) ?? []);
        var hiddenAck = await plant.Try(recipient, $"/api/notifications/alerts/{alertId:D}/acknowledge", new { reason = "Must not resolve hidden records" });
        Assert.Equal(HttpStatusCode.NotFound, hiddenAck.StatusCode);

        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync();
        await using (var retry = new NpgsqlCommand("UPDATE kernel.notification_delivery SET next_attempt_at=now()-interval '1 minute' WHERE alert_id=$1 AND kind='Resolution'", connection))
        {
            retry.Parameters.AddWithValue(alertId);
            await retry.ExecuteNonQueryAsync();
        }
        Assert.Equal(1, await app.Services.GetRequiredService<NotificationDeliveryService>().DispatchDueAsync(20, CancellationToken.None));
        Assert.Single(email.Messages); // a retry must not disclose to the now-revoked recipient
        await using var check = new NpgsqlCommand("SELECT a.state,d.status FROM kernel.notification_alert a JOIN kernel.notification_delivery d ON d.alert_id=a.alert_id WHERE a.alert_id=$1 AND d.kind='Resolution'", connection);
        check.Parameters.AddWithValue(alertId);
        await using var reader = await check.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Resolved", reader.GetString(0));
        Assert.Equal("Suppressed", reader.GetString(1));
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

    [Fact]
    public async Task Queued_email_and_resolution_recheck_active_identity_role_program_markings_and_actual_source_permission()
    {
        var email = new RecordingEmailProvider();
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), services =>
        {
            services.RemoveAll<INotificationEmailProvider>();
            services.AddSingleton<INotificationEmailProvider>(email);
        });
        var (plant, _) = await SlittingPlant.BuildEngineeredAsync(app);
        await plant.Person("queued-programs", "ProgramManager");
        var program = Guid.Parse((await plant.Ok(plant.As("queued-programs"), "/api/programs/", new { code = "QUEUED-ACCESS", name = "Actual queued document source" })).GetProperty("entityId").GetString()!);
        await plant.Ok(plant.Admin, "/api/kernel/roles", new { role = "QueuedRecipients", displayName = "Configured notification recipients", permissions = new[] { Permissions.PeopleRead }, reason = "Configure recipients independently from document authority" });
        foreach (var role in new[] { "QueuedSourceReaders", "QueuedRevocableReader" })
            await plant.Ok(plant.Admin, "/api/kernel/roles", new { role, displayName = role, permissions = new[] { Permissions.EngineeringRead, Permissions.NotificationsRead }, reason = "Source read and inbox access are independent from recipient assignment" });
        var people = new Dictionary<string, Guid>();
        foreach (var subject in new[] { "allowed", "program", "role", "inactive", "marking", "permission", "expired", "uncleared" })
        {
            var id = Guid.Parse((await plant.Ok(plant.Admin, "/api/kernel/people", new { idpSubject = "queued-" + subject, displayName = "Queued " + subject, markings = subject == "uncleared" ? Array.Empty<string>() : new[] { "ExportControlled" }, reason = "Recorded notification test identity" })).GetProperty("entityId").GetString()!);
            people.Add(subject, id);
            await plant.Ok(plant.Admin, $"/api/kernel/people/{id}/roles", new { role = "QueuedRecipients", reason = "Address the configured recipient" });
            await plant.Ok(plant.Admin, $"/api/kernel/people/{id}/roles", new { role = subject == "permission" ? "QueuedRevocableReader" : "QueuedSourceReaders", reason = "Separate current source read grant" });
            await plant.Ok(plant.Admin, $"/api/kernel/people/{id}/programs", new { programId = program, reason = "Independent program membership" });
        }
        var authorId = Guid.Parse((await plant.Ok(plant.Admin, "/api/kernel/people", new { idpSubject = "queued-author", displayName = "Cleared source author", markings = new[] { "ExportControlled" }, reason = "Controlled source authority" })).GetProperty("entityId").GetString()!);
        await plant.Ok(plant.Admin, $"/api/kernel/people/{authorId}/roles", new { role = "QualityEngineer", reason = "Existing document management authority" });
        await plant.Ok(plant.Admin, $"/api/kernel/people/{authorId}/programs", new { programId = program, reason = "Actual source access" });
        await plant.Ok(plant.Admin, "/api/notifications/rules", new
        {
            name = "Controlled document notice", active = true, triggerEventType = "DocumentCreated", clearEventType = "DocumentChanged",
            scopeType = "Program", scopeId = program.ToString("D"), severity = "ActionRequired", confidence = "High",
            recipientRoles = new[] { "QueuedRecipients" }, escalationRoles = Array.Empty<string>(), channels = new[] { "InApp", "Email" },
            template = "{entityType} {entityId}", resolutionTemplate = "Resolved {entityType} {entityId}", deepLinkTemplate = "#/engineering",
            delaySeconds = 0, dedupSeconds = 3600, timeZone = "UTC", reason = "Controlled notification rule",
        });
        var author = plant.As("queued-author");
        await plant.Ok(author, "/api/engineering/documents", new { number = "QUEUED-DOCUMENT", title = "PRIVATE QUEUED DOCUMENT", type = "Procedure", authority = "Internal", programId = program, markings = "ExportControlled", reason = "Recorded actual source" });
        await app.Services.GetRequiredService<OutboxRelay>().RelayBatchAsync(1_000_000, CancellationToken.None);
        await app.Services.GetRequiredService<SubscriptionPump>().PumpOnceAsync(1_000_000, CancellationToken.None);
        var inbox = (await plant.As("queued-allowed").GetFromJsonAsync<JsonElement[]>("/api/notifications/inbox"))!;
        var alertId = Assert.Single(inbox).GetProperty("alertId").GetGuid();
        Assert.Empty((await plant.As("queued-uncleared").GetFromJsonAsync<JsonElement[]>("/api/notifications/inbox")) ?? []);
        await plant.Ok(plant.Admin, $"/api/kernel/people/{people["program"]}/programs/{program}/revoke", new { reason = "Withdraw queued recipient source membership" });
        await plant.Ok(plant.Admin, $"/api/kernel/people/{people["role"]}/roles/QueuedRecipients/revoke", new { reason = "Withdraw recipient assignment" });
        await plant.Ok(plant.Admin, $"/api/kernel/roles/QueuedRevocableReader/permissions/{Permissions.EngineeringRead}/revoke", new { reason = "Withdraw actual source read authority" });
        await using (var connection = new NpgsqlConnection(app.ConnectionString))
        {
            await connection.OpenAsync();
            // Simulate trusted identity lifecycle/clearance and elapsed assignment state after queuing.
            foreach (var (sql, personId) in new[]
            {
                ("UPDATE kernel.person SET is_active=false WHERE person_id=$1", people["inactive"]),
                ("UPDATE kernel.person SET markings=0 WHERE person_id=$1", people["marking"]),
                ("UPDATE kernel.role_assignment SET expires_at=now()-interval '1 minute' WHERE person_id=$1 AND role='QueuedRecipients'", people["expired"]),
            })
            {
                await using var changed = new NpgsqlCommand(sql, connection);
                changed.Parameters.AddWithValue(personId);
                await changed.ExecuteNonQueryAsync();
            }
        }
        Assert.Equal(7, await app.Services.GetRequiredService<NotificationDeliveryService>().DispatchDueAsync(20, CancellationToken.None));
        Assert.Equal(people["allowed"], Assert.Single(email.Messages).RecipientPersonId);
        foreach (var subject in new[] { "program", "role", "marking", "permission", "expired", "uncleared" })
            Assert.Empty((await plant.As("queued-" + subject).GetFromJsonAsync<JsonElement[]>("/api/notifications/inbox")) ?? []);
        Assert.Equal(HttpStatusCode.Forbidden, (await plant.As("queued-inactive").GetAsync("/api/notifications/inbox")).StatusCode);

        await plant.Ok(author, "/api/engineering/documents/QUEUED-DOCUMENT", new { title = "Updated controlled title", markings = "ExportControlled", reason = "Actual source resolution event" });
        await app.Services.GetRequiredService<OutboxRelay>().RelayBatchAsync(1_000_000, CancellationToken.None);
        await app.Services.GetRequiredService<SubscriptionPump>().PumpOnceAsync(1_000_000, CancellationToken.None);
        Assert.Equal(7, await app.Services.GetRequiredService<NotificationDeliveryService>().DispatchDueAsync(20, CancellationToken.None));
        Assert.Equal(2, email.Messages.Count);
        Assert.All(email.Messages, message => Assert.Equal(people["allowed"], message.RecipientPersonId));
        await using var proofConnection = new NpgsqlConnection(app.ConnectionString);
        await proofConnection.OpenAsync();
        await using var proof = new NpgsqlCommand("SELECT count(*) FILTER(WHERE status='Sent'),count(*) FILTER(WHERE status='Suppressed') FROM kernel.notification_delivery WHERE alert_id=$1", proofConnection);
        proof.Parameters.AddWithValue(alertId);
        await using var reader = await proof.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal((2L, 12L), (reader.GetInt64(0), reader.GetInt64(1)));
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
