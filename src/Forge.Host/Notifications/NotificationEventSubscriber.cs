using System.Text.Json;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Postgres;
using Npgsql;

namespace Forge.Host.Notifications;

/// <summary>Builds alert state from the authoritative event log. Delivery occurs on a separate worker.</summary>
public sealed class NotificationEventSubscriber(NpgsqlDataSource dataSource, TimeProvider time) : IEventSubscriber
{
    public string Name => "forge-notifications";
    public bool Handles(string eventType) => eventType is not nameof(NotificationRuleChanged) and not nameof(NotificationAcknowledged);

    public async Task HandleAsync(long sequence, EventEnvelope envelope, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var rules = new List<RuleRow>();
        await using (var cmd = new NpgsqlCommand("SELECT rule_id,name,trigger_event_type,clear_event_type,scope_type,scope_id,condition_field,condition_equals,severity,confidence,recipient_roles,escalation_roles,channels,template,resolution_template,deep_link_template,delay_seconds,reminder_seconds,escalation_seconds,quiet_start,quiet_end,time_zone,dedup_seconds FROM kernel.notification_rule WHERE active AND (trigger_event_type=$1 OR clear_event_type=$1) ORDER BY rule_id", connection, transaction))
        {
            cmd.Parameters.AddWithValue(envelope.EventType);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) rules.Add(ReadRule(reader));
        }

        var payload = TryJson(envelope.Payload);
        foreach (var rule in rules)
        {
            if (!MatchesScope(rule, envelope, payload)) continue;
            var conditionKey = $"{envelope.SourceContext}/{envelope.EntityType}/{envelope.EntityId}";
            if (rule.ClearEventType == envelope.EventType)
            {
                await ResolveAsync(connection, transaction, rule, envelope, conditionKey, ct);
            }
            else if (rule.TriggerEventType == envelope.EventType && MatchesCondition(rule, payload))
            {
                await OpenAsync(connection, transaction, rule, envelope, conditionKey, ct);
            }
        }
        await transaction.CommitAsync(ct);
    }

    private async Task OpenAsync(NpgsqlConnection connection, NpgsqlTransaction tx, RuleRow rule, EventEnvelope evt, string key, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using (var existing = new NpgsqlCommand("SELECT 1 FROM kernel.notification_alert WHERE rule_id=$1 AND condition_key=$2 AND state='Open' UNION ALL SELECT 1 FROM kernel.notification_alert WHERE rule_id=$1 AND condition_key=$2 AND opened_at >= $3 LIMIT 1", connection, tx))
        {
            existing.Parameters.AddWithValue(rule.Id);
            existing.Parameters.AddWithValue(key);
            existing.Parameters.AddWithValue(now.AddSeconds(-rule.DedupSeconds));
            if (await existing.ExecuteScalarAsync(ct) is not null) return;
        }

        var generation = 1;
        await using (var gen = new NpgsqlCommand("SELECT COALESCE(max(generation),0)+1 FROM kernel.notification_alert WHERE rule_id=$1 AND condition_key=$2", connection, tx))
        {
            gen.Parameters.AddWithValue(rule.Id);
            gen.Parameters.AddWithValue(key);
            generation = Convert.ToInt32(await gen.ExecuteScalarAsync(ct));
        }
        var alertId = ForgeIds.New(time);
        var title = rule.Name;
        var message = Render(rule.Template, evt);
        var deepLink = Render(rule.DeepLinkTemplate, evt);
        if (!NotificationCommands.IsLocalLink(deepLink)) deepLink = "#/notifications";
        var visibleAt = now.AddSeconds(rule.DelaySeconds);
        await using (var insert = new NpgsqlCommand("INSERT INTO kernel.notification_alert(alert_id,rule_id,generation,condition_key,state,severity,confidence,program_id,markings,source_context,entity_type,entity_id,source_event_id,source_event_type,title,message,deep_link,opened_at,visible_at) VALUES($1,$2,$3,$4,'Open',$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18) ON CONFLICT DO NOTHING", connection, tx))
        {
            insert.Parameters.AddWithValue(alertId);
            insert.Parameters.AddWithValue(rule.Id);
            insert.Parameters.AddWithValue(generation);
            insert.Parameters.AddWithValue(key);
            insert.Parameters.AddWithValue(rule.Severity);
            insert.Parameters.AddWithValue(rule.Confidence);
            insert.Parameters.AddWithValue(evt.ProgramId?.Value ?? (object)DBNull.Value);
            insert.Parameters.AddWithValue((int)evt.Markings);
            insert.Parameters.AddWithValue(evt.SourceContext);
            insert.Parameters.AddWithValue(evt.EntityType);
            insert.Parameters.AddWithValue(evt.EntityId);
            insert.Parameters.AddWithValue(evt.EventId);
            insert.Parameters.AddWithValue(evt.EventType);
            insert.Parameters.AddWithValue(title);
            insert.Parameters.AddWithValue(message);
            insert.Parameters.AddWithValue(deepLink);
            insert.Parameters.AddWithValue(now);
            insert.Parameters.AddWithValue(visibleAt);
            if (await insert.ExecuteNonQueryAsync(ct) == 0) return;
        }

        var recipients = await RecipientsAsync(connection, tx, rule.RecipientRoles, evt.ProgramId?.Value, ct);
        foreach (var recipient in recipients)
        {
            await using (var inbox = new NpgsqlCommand("INSERT INTO kernel.notification_inbox(alert_id,person_id,recipient_role) VALUES($1,$2,$3) ON CONFLICT DO NOTHING", connection, tx))
            {
                inbox.Parameters.AddWithValue(alertId); inbox.Parameters.AddWithValue(recipient.PersonId); inbox.Parameters.AddWithValue(recipient.Role);
                await inbox.ExecuteNonQueryAsync(ct);
            }
            if (rule.Channels.Contains("Email", StringComparer.Ordinal))
                await QueueEmailAsync(connection, tx, time, alertId, recipient, "Alert", visibleAt, evt.EventId, ct);
        }
        if (rule.Channels.Contains("Email", StringComparer.Ordinal) && rule.EscalationSeconds is { } escalation)
        {
            foreach (var recipient in await RecipientsAsync(connection, tx, rule.EscalationRoles, evt.ProgramId?.Value, ct))
                await QueueEmailAsync(connection, tx, time, alertId, recipient, "Escalation", visibleAt.AddSeconds(escalation), evt.EventId, ct);
        }
        // A resolution closes outstanding delayed notices before they are dispatched.
        if (rule.ReminderSeconds is { } reminder && rule.Channels.Contains("Email", StringComparer.Ordinal))
        {
            foreach (var recipient in recipients)
                await QueueEmailAsync(connection, tx, time, alertId, recipient, "Reminder", visibleAt.AddSeconds(reminder), evt.EventId, ct);
        }
    }

    private async Task ResolveAsync(NpgsqlConnection connection, NpgsqlTransaction tx, RuleRow rule, EventEnvelope evt, string key, CancellationToken ct)
    {
        var resolution = Render(rule.ResolutionTemplate, evt);
        var alertId = Guid.Empty;
        await using (var cmd = new NpgsqlCommand("UPDATE kernel.notification_alert SET state='Resolved',resolved_at=$3,resolution_message=$4 WHERE rule_id=$1 AND condition_key=$2 AND state='Open' RETURNING alert_id", connection, tx))
        {
            cmd.Parameters.AddWithValue(rule.Id); cmd.Parameters.AddWithValue(key); cmd.Parameters.AddWithValue(time.GetUtcNow()); cmd.Parameters.AddWithValue(resolution);
            var result = await cmd.ExecuteScalarAsync(ct);
            if (result is not Guid id) return;
            alertId = id;
        }
        await using (var notice = new NpgsqlCommand("UPDATE kernel.notification_inbox SET resolution_acknowledged_at=NULL WHERE alert_id=$1", connection, tx))
        {
            notice.Parameters.AddWithValue(alertId);
            await notice.ExecuteNonQueryAsync(ct);
        }
        await using (var cancel = new NpgsqlCommand("UPDATE kernel.notification_delivery SET status='Suppressed',sent_at=now(),last_error='Suppressed because the condition cleared before delivery' WHERE alert_id=$1 AND status IN ('Pending','Failed') AND kind IN ('Alert','Reminder','Escalation')", connection, tx))
        {
            cancel.Parameters.AddWithValue(alertId);
            await cancel.ExecuteNonQueryAsync(ct);
        }
        var recipients = new List<Recipient>();
        await using (var read = new NpgsqlCommand("SELECT person_id,recipient_role FROM kernel.notification_inbox WHERE alert_id=$1", connection, tx))
        {
            read.Parameters.AddWithValue(alertId);
            await using var reader = await read.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) recipients.Add(new Recipient(reader.GetGuid(0), reader.GetString(1)));
        }
        if (rule.Channels.Contains("Email", StringComparer.Ordinal))
            foreach (var recipient in recipients) await QueueEmailAsync(connection, tx, time, alertId, recipient, "Resolution", time.GetUtcNow(), evt.EventId, ct);
    }

    private static async Task<List<Recipient>> RecipientsAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string[] roles, Guid? programId, CancellationToken ct)
    {
        if (roles.Length == 0) return [];
        var sql = "SELECT p.person_id, min(ra.role) FROM kernel.person p JOIN kernel.role_assignment ra ON ra.person_id=p.person_id WHERE p.is_active AND ra.role=ANY($1) AND (ra.expires_at IS NULL OR ra.expires_at>now()) AND ($2::uuid IS NULL OR EXISTS(SELECT 1 FROM kernel.program_access pa WHERE pa.person_id=p.person_id AND pa.program_id=$2)) GROUP BY p.person_id ORDER BY p.person_id";
        // Older installs may have no expiry column only before V3.15, which is outside supported state.
        var found = new List<Recipient>();
        await using var cmd = new NpgsqlCommand(sql, connection, tx);
        cmd.Parameters.AddWithValue(roles); cmd.Parameters.AddWithValue(programId ?? (object)DBNull.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) found.Add(new Recipient(reader.GetGuid(0), reader.GetString(1)));
        return found;
    }

    private static async Task QueueEmailAsync(NpgsqlConnection connection, NpgsqlTransaction tx, TimeProvider time, Guid alertId, Recipient recipient, string kind, DateTimeOffset due, Guid causeEvent, CancellationToken ct)
    {
        var key = $"notify:{alertId:D}:{recipient.PersonId:D}:{kind}:{due.ToUnixTimeSeconds()}";
        await using var cmd = new NpgsqlCommand("INSERT INTO kernel.notification_delivery(delivery_id,alert_id,person_id,recipient_role,channel,kind,idempotency_key,due_at) VALUES($1,$2,$3,$4,'Email',$5,$6,$7) ON CONFLICT DO NOTHING", connection, tx);
        cmd.Parameters.AddWithValue(ForgeIds.New(time)); cmd.Parameters.AddWithValue(alertId); cmd.Parameters.AddWithValue(recipient.PersonId); cmd.Parameters.AddWithValue(recipient.Role); cmd.Parameters.AddWithValue(kind); cmd.Parameters.AddWithValue(key); cmd.Parameters.AddWithValue(due);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static JsonDocument? TryJson(string value)
    {
        try { return JsonDocument.Parse(value); } catch (JsonException) { return null; }
    }

    private static bool MatchesCondition(RuleRow rule, JsonDocument? payload)
    {
        if (rule.ConditionField is null) return true;
        if (payload is null) return false;
        if (!payload.RootElement.TryGetProperty(rule.ConditionField, out var value)) return false;
        return string.Equals(value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString(), rule.ConditionEquals, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesScope(RuleRow rule, EventEnvelope evt, JsonDocument? payload) => rule.ScopeType switch
    {
        "Global" => true,
        "Program" => Guid.TryParse(rule.ScopeId, out var id) && evt.ProgramId?.Value == id,
        "Entity" => string.Equals(rule.ScopeId, $"{evt.EntityType}/{evt.EntityId}", StringComparison.OrdinalIgnoreCase) || string.Equals(rule.ScopeId, evt.EntityId, StringComparison.OrdinalIgnoreCase),
        "WorkCenter" => string.Equals(evt.EntityType, "WorkCenter", StringComparison.OrdinalIgnoreCase) && string.Equals(evt.EntityId, rule.ScopeId, StringComparison.OrdinalIgnoreCase)
            || payload is not null && payload.RootElement.TryGetProperty("workCenterCode", out var center) && string.Equals(center.GetString(), rule.ScopeId, StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private static string Render(string template, EventEnvelope evt)
    {
        var substitutions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["eventType"] = evt.EventType,
            ["entityType"] = evt.EntityType,
            ["entityId"] = evt.EntityId,
            ["sourceContext"] = evt.SourceContext,
            ["actor"] = evt.Actor,
            ["recordedAt"] = evt.RecordedAt.ToString("O"),
        };
        foreach (var (key, value) in substitutions) template = template.Replace("{" + key + "}", value, StringComparison.Ordinal);
        return template;
    }

    private static RuleRow ReadRule(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetFieldValue<string[]>(10),
        reader.GetFieldValue<string[]>(11), reader.GetFieldValue<string[]>(12), reader.GetString(13), reader.GetString(14),
        reader.GetString(15), reader.GetInt32(16), reader.IsDBNull(17) ? null : reader.GetInt32(17), reader.IsDBNull(18) ? null : reader.GetInt32(18),
        reader.IsDBNull(19) ? null : reader.GetFieldValue<TimeOnly>(19), reader.IsDBNull(20) ? null : reader.GetFieldValue<TimeOnly>(20),
        reader.GetString(21), reader.GetInt32(22));

    private sealed record Recipient(Guid PersonId, string Role);
    private sealed record RuleRow(Guid Id, string Name, string TriggerEventType, string? ClearEventType, string ScopeType, string? ScopeId,
        string? ConditionField, string? ConditionEquals, string Severity, string Confidence, string[] RecipientRoles, string[] EscalationRoles,
        string[] Channels, string Template, string ResolutionTemplate, string DeepLinkTemplate, int DelaySeconds, int? ReminderSeconds,
        int? EscalationSeconds, TimeOnly? QuietStart, TimeOnly? QuietEnd, string TimeZone, int DedupSeconds);
}
