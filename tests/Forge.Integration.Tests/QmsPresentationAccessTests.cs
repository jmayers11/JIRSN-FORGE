using System.Net;
using System.Text;
using System.Text.Json;
using Forge.Host.Notifications;
using Forge.Kernel.Postgres;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;
using Forge.Quality.Qms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Forge.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class QmsPresentationAccessTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Facility_scoped_audit_and_notifications_follow_current_linked_source_access_without_rewriting_history()
    {
        var email = new RecordingEmail();
        await using var app = new ForgeApp(await postgres.CreateDatabaseAsync(), services =>
        {
            services.RemoveAll<INotificationEmailProvider>();
            services.AddSingleton<INotificationEmailProvider>(email);
        }, builder => builder.UseSetting(QmsService.ConfigurationKey, "TEST-ORG"));
        var plant = new SlittingPlant.Plant(app);
        async Task<Guid> Person(string subject, params string[] roles)
        {
            var id = Guid.Parse((await plant.Ok(plant.Admin, "/api/kernel/people", new
            {
                idpSubject = subject, displayName = subject,
                markings = subject == "qms-view-reader" ? Array.Empty<string>() : new[] { "Proprietary" },
                reason = "Actual presentation authority"
            })).GetProperty("entityId").GetString()!);
            plant.People[subject] = id;
            foreach (var role in roles) await plant.Ok(plant.Admin, $"/api/kernel/people/{id}/roles", new { role, reason = "Explicit role assignment" });
            return id;
        }
        await Person("qms-view-author", Roles.QmsAuthor, Roles.ManufacturingEngineer, Roles.ProgramManager);
        await Person("qms-view-reviewer", Roles.QmsReviewer, Roles.QmsApprover, Roles.DocumentController);
        var author = plant.As("qms-view-author");
        var reviewer = plant.As("qms-view-reviewer");
        await plant.Ok(author, "/api/resources/sites", new { code = "QMS-PLANT", name = "Actual governed facility", timeZone = TestSite.TimeZone });
        const string role = "QmsPresentationReader";
        var permissions = new[] { Permissions.QmsRead, Permissions.EngineeringRead, Permissions.AuditRead, Permissions.NotificationsRead, Permissions.NotificationsAcknowledge };
        await plant.Ok(plant.Admin, "/api/kernel/roles", new { role, displayName = "Governance presentation reader", permissions, reason = "Scope real presentation access" });
        foreach (var permission in new[] { Permissions.QmsRead, Permissions.AuditRead })
            await plant.Ok(plant.Admin, $"/api/kernel/roles/{role}/permission-scopes", new { permission, scopeType = "facility", scopeId = "QMS-PLANT", reason = "Only the owning facility" });
        await Person("qms-view-reader", role);
        var reader = plant.As("qms-view-reader");

        using var upload = new HttpRequestMessage(HttpMethod.Post, "/api/engineering/document-files") { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("Actual local governed source evidence")) };
        upload.Headers.Add("Idempotency-Key", "qms-presentation-file");
        upload.Headers.Add("X-File-Name", "source.txt");
        var sha = (await Json(await author.SendAsync(upload))).GetProperty("entityId").GetString();
        await plant.Ok(author, "/api/engineering/documents", new { number = "QMS.VIEW", title = "Governed source", type = "Procedure", authority = "Internal" });
        await plant.Ok(author, "/api/engineering/documents/QMS.VIEW/revisions", new { revision = "A", fileSha256 = sha, fileName = "source.txt", changeNote = "Original actual source" });
        await plant.Ok(author, "/api/engineering/documents/QMS.VIEW/revisions/A/submit", new { });
        await plant.Ok(reviewer, "/api/engineering/documents/QMS.VIEW/revisions/A/approve", new { });
        await plant.Ok(reviewer, "/api/engineering/documents/QMS.VIEW/revisions/A/release", new { reason = "Independent real source release" });
        var content = new QmsContent(Process: new("Protected production definition", ["Released requirements"], ["Accepted outputs"],
            plant.People["qms-view-author"], [], ["Qualified personnel"], ["Supply interruption"],
            [new("Acceptance", "Inspect actual outputs", "Recorded inspection", "Monthly")]), Documents: [new("QMS.VIEW", "A")]);
        await plant.Ok(author, "/api/quality/qms/records", new { code = "VIEW-PROCESS", kind = "Process", facilityCode = "QMS-PLANT", markings = "None", content, reason = "Actual governed process" });
        await plant.Ok(plant.Admin, "/api/notifications/rules", new
        {
            name = "Actual QMS review required", active = true, triggerEventType = "QmsRevisionChanged", clearEventType = (string?)null,
            scopeType = "Global", scopeId = (string?)null, conditionField = "action", conditionEquals = "Submit",
            severity = "ActionRequired", confidence = "High", recipientRoles = new[] { role }, escalationRoles = new[] { role },
            channels = new[] { "InApp", "Email" }, template = "{eventType} on {entityType} {entityId}", resolutionTemplate = "Resolved {entityId}",
            deepLinkTemplate = "#/qms", delaySeconds = 0, reminderSeconds = 600, escalationSeconds = 1200,
            quietStart = (TimeOnly?)null, quietEnd = (TimeOnly?)null, timeZone = "America/New_York", dedupSeconds = 3600,
            reason = "Notify only the actual governed source"
        });
        await plant.Ok(author, "/api/quality/qms/records/VIEW-PROCESS/revisions/1/submit", new { expectedVersion = 1, note = "Ready for independent review", reason = "Actual human submission" });
        await app.Services.GetRequiredService<OutboxRelay>().RelayBatchAsync(1_000_000, CancellationToken.None);
        await app.Services.GetRequiredService<SubscriptionPump>().PumpOnceAsync(1_000_000, CancellationToken.None);
        const string auditUrl = "/api/kernel/audit?entityType=QmsRevision&entityId=VIEW-PROCESS%2F1";
        var originalAudit = await Json(await reader.GetAsync(auditUrl));
        Assert.Equal(2, originalAudit.GetArrayLength());
        Assert.Contains("Protected production definition", originalAudit.GetRawText(), StringComparison.Ordinal);
        var originalInbox = await Json(await reader.GetAsync("/api/notifications/inbox"));
        var alertId = Assert.Single(originalInbox.EnumerateArray()).GetProperty("alertId").GetGuid();
        var originalCanonical = await CanonicalAsync(app);

        await plant.Ok(author, "/api/engineering/documents/QMS.VIEW", new { title = "Governed source", markings = "Proprietary", reason = "Current linked source classification changed" });
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync("/api/quality/qms/records/VIEW-PROCESS/revisions/1")).StatusCode);
        Assert.Empty((await Json(await reader.GetAsync(auditUrl))).EnumerateArray());
        Assert.Empty((await Json(await reader.GetAsync("/api/notifications/inbox"))).EnumerateArray());
        using (var hiddenAck = await plant.Try(reader, $"/api/notifications/alerts/{alertId}/acknowledge", new { reason = "No hidden source acknowledgment" }))
            Assert.Equal(HttpStatusCode.NotFound, hiddenAck.StatusCode);
        Assert.Equal(1, await app.Services.GetRequiredService<NotificationDeliveryService>().DispatchDueAsync(20, CancellationToken.None));
        Assert.Empty(email.Messages);
        Assert.Equal(originalCanonical, await CanonicalAsync(app));

        await plant.Ok(author, "/api/engineering/documents/QMS.VIEW", new { title = "Governed source", markings = "None", reason = "Restore current linked source visibility" });
        Assert.Equal(originalAudit.GetRawText(), (await Json(await reader.GetAsync(auditUrl))).GetRawText());
        Assert.Equal(originalInbox.GetRawText(), (await Json(await reader.GetAsync("/api/notifications/inbox"))).GetRawText());
        Assert.Equal(originalCanonical, await CanonicalAsync(app));
        await plant.Ok(plant.Admin, $"/api/kernel/roles/{role}/permissions/{Permissions.QmsRead}/revoke", new { reason = "Withdraw owner read permission" });
        Assert.Empty((await Json(await reader.GetAsync(auditUrl))).EnumerateArray());
        Assert.Empty((await Json(await reader.GetAsync("/api/notifications/inbox"))).EnumerateArray());
        Assert.Equal(originalCanonical, await CanonicalAsync(app));
        await plant.Ok(plant.Admin, $"/api/kernel/roles/{role}/permissions", new { permission = Permissions.QmsRead, reason = "Restore actual governed source permission" });
        await plant.Ok(plant.Admin, $"/api/kernel/roles/{role}/permission-scopes", new { permission = Permissions.QmsRead, scopeType = "facility", scopeId = "QMS-PLANT", reason = "Restore the same owning scope" });
        Assert.Equal(originalAudit.GetRawText(), (await Json(await reader.GetAsync(auditUrl))).GetRawText());
        Assert.Equal(originalCanonical, await CanonicalAsync(app));
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
    private static async Task<string> CanonicalAsync(ForgeApp app)
    {
        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync();
        const string sql = "SELECT jsonb_build_object('revisions',(SELECT jsonb_agg(to_jsonb(r) ORDER BY code,revision) FROM quality.qms_revision r),'sources',(SELECT jsonb_agg(to_jsonb(s) ORDER BY code,revision,kind,source_key,source_revision) FROM quality.qms_revision_source s),'audit',(SELECT jsonb_agg(to_jsonb(a) ORDER BY sequence) FROM kernel.audit_log a WHERE entity_type IN ('QmsRecord','QmsRevision') AND outcome='Succeeded'),'alerts',(SELECT jsonb_agg(to_jsonb(n) ORDER BY alert_id) FROM kernel.notification_alert n))::text";
        await using var command = new NpgsqlCommand(sql, connection);
        return (string)(await command.ExecuteScalarAsync())!;
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
}
