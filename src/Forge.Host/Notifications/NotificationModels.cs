using Forge.Kernel.Commands;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;
using Forge.Kernel.Postgres;
using Forge.Kernel.Security;
using Npgsql;

namespace Forge.Host.Notifications;

public sealed record NotificationRule(
    Guid RuleId,
    string Name,
    bool Active,
    string TriggerEventType,
    string? ClearEventType,
    string ScopeType,
    string? ScopeId,
    string? ConditionField,
    string? ConditionEquals,
    string Severity,
    string Confidence,
    IReadOnlyList<string> RecipientRoles,
    IReadOnlyList<string> EscalationRoles,
    IReadOnlyList<string> Channels,
    string Template,
    string ResolutionTemplate,
    string DeepLinkTemplate,
    int DelaySeconds,
    int? ReminderSeconds,
    int? EscalationSeconds,
    TimeOnly? QuietStart,
    TimeOnly? QuietEnd,
    string TimeZone,
    int DedupSeconds,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset UpdatedAt,
    string UpdatedBy);

public sealed record NotificationRuleDraft(
    Guid? RuleId,
    string Name,
    bool Active,
    string TriggerEventType,
    string? ClearEventType,
    string ScopeType,
    string? ScopeId,
    string? ConditionField,
    string? ConditionEquals,
    string Severity,
    string Confidence,
    string[] RecipientRoles,
    string[]? EscalationRoles,
    string[] Channels,
    string Template,
    string ResolutionTemplate,
    string DeepLinkTemplate,
    int DelaySeconds,
    int? ReminderSeconds,
    int? EscalationSeconds,
    TimeOnly? QuietStart,
    TimeOnly? QuietEnd,
    string TimeZone,
    int DedupSeconds,
    string? Reason);

public sealed record NotificationInboxItem(
    Guid AlertId,
    Guid RuleId,
    string Severity,
    string Confidence,
    string State,
    string SourceContext,
    string EntityType,
    string EntityId,
    string Title,
    string Message,
    string? ResolutionMessage,
    string DeepLink,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ResolvedAt,
    DateTimeOffset? AcknowledgedAt,
    DateTimeOffset? ResolutionAcknowledgedAt,
    string RecipientRole);

public sealed record NotificationRuleChanged(Guid RuleId, string Name, bool Active, string TriggerEventType) : IDomainEvent
{
    public static string EventType => nameof(NotificationRuleChanged);
    public static int SchemaVersion => 1;
}

public sealed record NotificationAcknowledged(Guid AlertId, Guid PersonId) : IDomainEvent
{
    public static string EventType => nameof(NotificationAcknowledged);
    public static int SchemaVersion => 1;
}

public interface INotificationStore
{
    Task SaveRuleAsync(NotificationRule rule, bool isNew, CancellationToken ct);
    Task<bool> AcknowledgeAsync(Guid alertId, Guid personId, CancellationToken ct);
}

/// <summary>State changes from a user command participate in the kernel's audit/outbox transaction.</summary>
public sealed class NotificationStore(PostgresUnitOfWork uow) : INotificationStore
{
    public async Task SaveRuleAsync(NotificationRule rule, bool isNew, CancellationToken ct)
    {
        var expectedRoles = rule.RecipientRoles.Concat(rule.EscalationRoles).Distinct(StringComparer.Ordinal).ToArray();
        await using (var roles = uow.Command("SELECT role_name FROM kernel.role_definition WHERE is_active AND role_name=ANY($1) ORDER BY role_name", (object)expectedRoles))
        {
            var found = new HashSet<string>(StringComparer.Ordinal);
            await using var reader = await roles.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) found.Add(reader.GetString(0));
            if (found.Count != expectedRoles.Length)
                throw new CommandRejectedException("unknown_recipient_role", "Every recipient and escalation role must be active in Forge.");
        }
        var sql = isNew
            ? """
              INSERT INTO kernel.notification_rule
              (rule_id,name,active,trigger_event_type,clear_event_type,scope_type,scope_id,condition_field,condition_equals,severity,confidence,recipient_roles,escalation_roles,channels,template,resolution_template,deep_link_template,delay_seconds,reminder_seconds,escalation_seconds,quiet_start,quiet_end,time_zone,dedup_seconds,created_at,created_by,updated_at,updated_by)
              VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21,$22,$23,$24,$25,$26,$27,$28)
              """
            : """
              UPDATE kernel.notification_rule SET name=$2,active=$3,trigger_event_type=$4,clear_event_type=$5,scope_type=$6,scope_id=$7,condition_field=$8,condition_equals=$9,severity=$10,confidence=$11,recipient_roles=$12,escalation_roles=$13,channels=$14,template=$15,resolution_template=$16,deep_link_template=$17,delay_seconds=$18,reminder_seconds=$19,escalation_seconds=$20,quiet_start=$21,quiet_end=$22,time_zone=$23,dedup_seconds=$24,updated_at=$25,updated_by=$26 WHERE rule_id=$1
              """;
        var args = isNew
            ? new object?[] { rule.RuleId, rule.Name, rule.Active, rule.TriggerEventType, rule.ClearEventType, rule.ScopeType, rule.ScopeId,
                rule.ConditionField, rule.ConditionEquals, rule.Severity, rule.Confidence, rule.RecipientRoles.ToArray(), rule.EscalationRoles.ToArray(),
                rule.Channels.ToArray(), rule.Template, rule.ResolutionTemplate, rule.DeepLinkTemplate, rule.DelaySeconds, rule.ReminderSeconds,
                rule.EscalationSeconds, rule.QuietStart, rule.QuietEnd, rule.TimeZone, rule.DedupSeconds, rule.CreatedAt, rule.CreatedBy, rule.UpdatedAt, rule.UpdatedBy }
            : [rule.RuleId, rule.Name, rule.Active, rule.TriggerEventType, rule.ClearEventType, rule.ScopeType, rule.ScopeId,
                rule.ConditionField, rule.ConditionEquals, rule.Severity, rule.Confidence, rule.RecipientRoles.ToArray(), rule.EscalationRoles.ToArray(),
                rule.Channels.ToArray(), rule.Template, rule.ResolutionTemplate, rule.DeepLinkTemplate, rule.DelaySeconds, rule.ReminderSeconds,
                rule.EscalationSeconds, rule.QuietStart, rule.QuietEnd, rule.TimeZone, rule.DedupSeconds, rule.UpdatedAt, rule.UpdatedBy];
        await using var cmd = uow.Command(sql, args);
        if (await cmd.ExecuteNonQueryAsync(ct) == 0 && !isNew)
            throw new CommandRejectedException("notification_rule_missing", "The notification rule does not exist.");
    }

    public async Task<bool> AcknowledgeAsync(Guid alertId, Guid personId, CancellationToken ct)
    {
        await using var cmd = uow.Command("""
            UPDATE kernel.notification_inbox i
            SET acknowledged_at=CASE WHEN a.state='Open' AND i.acknowledged_at IS NULL THEN now() ELSE i.acknowledged_at END,
                resolution_acknowledged_at=CASE WHEN a.state='Resolved' AND i.resolution_acknowledged_at IS NULL THEN now() ELSE i.resolution_acknowledged_at END
            FROM kernel.notification_alert a
            WHERE i.alert_id=$1 AND i.person_id=$2 AND a.alert_id=i.alert_id
              AND ((a.state='Open' AND i.acknowledged_at IS NULL) OR (a.state='Resolved' AND a.resolution_message IS NOT NULL AND i.resolution_acknowledged_at IS NULL))
            """, alertId, personId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
}

public sealed class NotificationCommands(CommandPipeline pipeline)
{
    public Task<CommandResult> SaveRuleAsync(NotificationRuleDraft draft, CommandContext context, CancellationToken ct)
    {
        Validate(draft);
        var id = draft.RuleId ?? ForgeIds.New(pipeline.Time);
        var now = pipeline.Time.GetUtcNow();
        var actor = context.Actor;
        return pipeline.ExecuteAsync(
            new CommandSpec("Notifications", draft.RuleId is null ? "CreateNotificationRule" : "UpdateNotificationRule", Permissions.NotificationRulesManage, ResourceRef.Of("NotificationRule", id), ReasonRequired: true),
            draft,
            context,
            async (uow, at) =>
            {
                var rule = new NotificationRule(id, draft.Name.Trim(), draft.Active, draft.TriggerEventType.Trim(), NullIfBlank(draft.ClearEventType), draft.ScopeType,
                    NullIfBlank(draft.ScopeId), NullIfBlank(draft.ConditionField), draft.ConditionEquals, draft.Severity, draft.Confidence,
                    draft.RecipientRoles.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                    (draft.EscalationRoles ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                    draft.Channels.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), draft.Template.Trim(), draft.ResolutionTemplate.Trim(),
                    draft.DeepLinkTemplate.Trim(), draft.DelaySeconds, draft.ReminderSeconds, draft.EscalationSeconds, draft.QuietStart, draft.QuietEnd,
                    draft.TimeZone.Trim(), draft.DedupSeconds, now, actor, now, actor);
                var isNew = draft.RuleId is null;
                await uow.Store<INotificationStore>().SaveRuleAsync(rule, isNew, ct);
                var evt = pipeline.Event(new NotificationRuleChanged(id, rule.Name, rule.Active, rule.TriggerEventType), context, "Notifications", "NotificationRule", id.ToString("D"));
                return new CommandOutcome(CommandResult.Ok(id.ToString("D"), isNew ? "Notification rule created." : "Notification rule updated."), [evt], new AuditChange("NotificationRule", id.ToString("D"), null, Forge.Kernel.Serialization.CanonicalJson.Serialize(new { rule.Name, rule.Active, rule.TriggerEventType, rule.ClearEventType, rule.ScopeType, rule.ScopeId, rule.Severity, rule.Confidence, rule.RecipientRoles, rule.EscalationRoles, rule.Channels })));
            }, ct);
    }

    public Task<CommandResult> AcknowledgeAsync(Guid alertId, ProgramId? programId, CommandContext context, CancellationToken ct) =>
        pipeline.ExecuteAsync(
            new CommandSpec("Notifications", "AcknowledgeNotification", Permissions.NotificationsAcknowledge, new ResourceRef("NotificationAlert", alertId.ToString("D"), programId), ReasonRequired: false),
            new { alertId }, context,
            async (uow, _) =>
            {
                if (!await uow.Store<INotificationStore>().AcknowledgeAsync(alertId, context.Principal.PersonId.Value, ct))
                {
                    throw new CommandRejectedException("alert_unavailable", "The alert is not available for acknowledgment.");
                }
                var evt = pipeline.Event(new NotificationAcknowledged(alertId, context.Principal.PersonId.Value), context, "Notifications", "NotificationAlert", alertId.ToString("D"));
                return new CommandOutcome(CommandResult.Ok(alertId.ToString("D"), "Alert acknowledged."), [evt], new AuditChange("NotificationAlert", alertId.ToString("D"), "Unacknowledged", "Acknowledged"));
            }, ct);

    private static void Validate(NotificationRuleDraft draft)
    {
        static bool OneOf(string value, params string[] allowed) => allowed.Contains(value, StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(draft.Name) || draft.Name.Trim().Length > 120) throw new CommandRejectedException("invalid_rule_name", "Rule name is required and must be at most 120 characters.");
        if (string.IsNullOrWhiteSpace(draft.TriggerEventType) || draft.TriggerEventType.Length > 160) throw new CommandRejectedException("invalid_event_type", "A bounded trigger event type is required.");
        if (!OneOf(draft.ScopeType, "Global", "Program", "WorkCenter", "Entity") || (draft.ScopeType != "Global" && string.IsNullOrWhiteSpace(draft.ScopeId))) throw new CommandRejectedException("invalid_scope", "Choose Global or a scope type with a scope ID.");
        if (draft.ScopeType == "Program" && !Guid.TryParse(draft.ScopeId, out _)) throw new CommandRejectedException("invalid_scope", "Program scope IDs must be UUIDs.");
        if (draft.ClearEventType is not null && string.Equals(draft.ClearEventType, draft.TriggerEventType, StringComparison.Ordinal)) throw new CommandRejectedException("invalid_clear_event", "The trigger and clear event types must be different.");
        if (!OneOf(draft.Severity, "Information", "Watch", "ActionRequired", "Critical")) throw new CommandRejectedException("invalid_severity", "Unknown alert severity.");
        if (!OneOf(draft.Confidence, "Confirmed", "High", "Medium", "Low", "Unknown")) throw new CommandRejectedException("invalid_confidence", "Unknown confidence level.");
        if (draft.RecipientRoles is not { Length: > 0 } || draft.RecipientRoles.Any(string.IsNullOrWhiteSpace)) throw new CommandRejectedException("recipient_required", "At least one recipient role is required.");
        if (draft.Channels is not { Length: > 0 } || draft.Channels.Any(c => !OneOf(c, "InApp", "Email"))) throw new CommandRejectedException("invalid_channel", "Choose InApp, Email, or both.");
        if (string.IsNullOrWhiteSpace(draft.Template) || draft.Template.Length > 500 || string.IsNullOrWhiteSpace(draft.ResolutionTemplate) || draft.ResolutionTemplate.Length > 500) throw new CommandRejectedException("invalid_template", "Alert and resolution templates are required and limited to 500 characters.");
        if (!IsLocalLink(draft.DeepLinkTemplate)) throw new CommandRejectedException("invalid_deep_link", "Deep links must be an application-relative path or hash route.");
        if (draft.DelaySeconds is < 0 or > 259200 || draft.DedupSeconds is < 0 or > 2592000 || draft.ReminderSeconds is < 60 or > 2592000 || draft.EscalationSeconds is < 60 or > 2592000) throw new CommandRejectedException("invalid_timing", "Notification timing is outside supported bounds.");
        if ((draft.QuietStart is null) != (draft.QuietEnd is null) || (draft.QuietStart is not null && draft.QuietStart == draft.QuietEnd)) throw new CommandRejectedException("invalid_quiet_hours", "Quiet hours need two different times.");
        if (NodaTime.DateTimeZoneProviders.Tzdb.GetZoneOrNull(draft.TimeZone) is null)
            throw new CommandRejectedException("invalid_time_zone", "The quiet-hours time zone must be a valid IANA time-zone identifier.");
        if ((draft.ConditionField is null) != (draft.ConditionEquals is null)) throw new CommandRejectedException("invalid_condition", "A condition field and value must be supplied together.");
        if (draft.ConditionField is { Length: > 100 }) throw new CommandRejectedException("invalid_condition", "The condition field must be at most 100 characters.");
        if (draft.DeepLinkTemplate.Length > 300) throw new CommandRejectedException("invalid_deep_link", "Deep link is too long.");
    }

    internal static bool IsLocalLink(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= 300 && !value.Contains('\\') && !value.StartsWith("//", StringComparison.Ordinal)
        && (value.StartsWith("#/", StringComparison.Ordinal) || (value[0] == '/' && Uri.TryCreate(value, UriKind.Relative, out _)));

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
