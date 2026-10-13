using Forge.Kernel.Security;
using Forge.Kernel.Access;
using Forge.Kernel.WorkAssignments;
using Npgsql;

namespace Forge.Host.Notifications;

public sealed class NotificationReadStore(NpgsqlDataSource dataSource, IKernelUnitOfWorkFactory unitOfWork,
    IWorkSourceResolver sources, IForgeAuthorizer authorizer)
{
    public async Task<IReadOnlyList<NotificationInboxItem>> InboxAsync(ForgePrincipal principal, bool openOnly, bool unreadOnly, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT a.alert_id,a.rule_id,a.severity,a.confidence,a.state,a.source_context,a.entity_type,a.entity_id,a.title,a.message,a.resolution_message,a.deep_link,a.opened_at,a.resolved_at,i.acknowledged_at,i.resolution_acknowledged_at,i.recipient_role
            FROM kernel.notification_alert a
            JOIN kernel.notification_inbox i ON i.alert_id=a.alert_id
            WHERE i.person_id=$1 AND i.recipient_role=ANY($2) AND a.visible_at<=now()
              AND ($3 OR a.state='Open')
              AND (NOT $6 OR CASE WHEN a.resolution_message IS NOT NULL THEN i.resolution_acknowledged_at IS NULL ELSE i.acknowledged_at IS NULL END)
              AND (a.program_id IS NULL OR a.program_id=ANY($4::uuid[]))
              AND (a.markings & $5)=a.markings
            ORDER BY (CASE WHEN a.resolution_message IS NOT NULL THEN i.resolution_acknowledged_at IS NULL ELSE i.acknowledged_at IS NULL END) DESC, a.opened_at DESC
            LIMIT 500
            """, connection);
        cmd.Parameters.AddWithValue(principal.PersonId.Value);
        cmd.Parameters.AddWithValue(principal.Roles.ToArray());
        cmd.Parameters.AddWithValue(!openOnly);
        cmd.Parameters.AddWithValue(principal.Programs.Select(p => p.Value).ToArray());
        cmd.Parameters.AddWithValue((int)principal.Markings);
        cmd.Parameters.AddWithValue(unreadOnly);
        var result = new List<NotificationInboxItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new NotificationInboxItem(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetString(11), reader.GetFieldValue<DateTimeOffset>(12), reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13), reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14), reader.IsDBNull(15) ? null : reader.GetFieldValue<DateTimeOffset>(15), reader.GetString(16)));
        await using var sourceSnapshot = await unitOfWork.BeginSnapshotAsync(ct);
        var visible = new List<NotificationInboxItem>();
        foreach (var item in result)
            if (await NotificationSourceAccess.CanSeeAsync(sourceSnapshot, sources, authorizer, principal, item.EntityType, item.EntityId, ct)) visible.Add(item);
        return visible;
    }

    public async Task<IReadOnlyList<NotificationRule>> RulesAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT rule_id,name,active,trigger_event_type,clear_event_type,scope_type,scope_id,condition_field,condition_equals,severity,confidence,recipient_roles,escalation_roles,channels,template,resolution_template,deep_link_template,delay_seconds,reminder_seconds,escalation_seconds,quiet_start,quiet_end,time_zone,dedup_seconds,created_at,created_by,updated_at,updated_by FROM kernel.notification_rule ORDER BY name", connection);
        var result = new List<NotificationRule>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(new NotificationRule(
            reader.GetGuid(0), reader.GetString(1), reader.GetBoolean(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetFieldValue<string[]>(11), reader.GetFieldValue<string[]>(12), reader.GetFieldValue<string[]>(13), reader.GetString(14), reader.GetString(15), reader.GetString(16), reader.GetInt32(17), reader.IsDBNull(18) ? null : reader.GetInt32(18), reader.IsDBNull(19) ? null : reader.GetInt32(19), reader.IsDBNull(20) ? null : reader.GetFieldValue<TimeOnly>(20), reader.IsDBNull(21) ? null : reader.GetFieldValue<TimeOnly>(21), reader.GetString(22), reader.GetInt32(23), reader.GetFieldValue<DateTimeOffset>(24), reader.GetString(25), reader.GetFieldValue<DateTimeOffset>(26), reader.GetString(27)));
        return result;
    }

    public async Task<(Guid? ProgramId, bool Addressed)> AlertAccessAsync(Guid alertId, ForgePrincipal principal, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT a.program_id,a.entity_type,a.entity_id FROM kernel.notification_alert a
            JOIN kernel.notification_inbox i ON i.alert_id=a.alert_id
            WHERE a.alert_id=$1 AND i.person_id=$2 AND i.recipient_role=ANY($3)
              AND (a.program_id IS NULL OR a.program_id=ANY($4::uuid[])) AND (a.markings & $5)=a.markings
            """, connection);
        cmd.Parameters.AddWithValue(alertId); cmd.Parameters.AddWithValue(principal.PersonId.Value); cmd.Parameters.AddWithValue(principal.Roles.ToArray()); cmd.Parameters.AddWithValue(principal.Programs.Select(p => p.Value).ToArray()); cmd.Parameters.AddWithValue((int)principal.Markings);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return (null, false);
        var program = reader.IsDBNull(0) ? (Guid?)null : reader.GetGuid(0);
        await using var sourceSnapshot = await unitOfWork.BeginSnapshotAsync(ct);
        var visible = await NotificationSourceAccess.CanSeeAsync(sourceSnapshot, sources, authorizer, principal, reader.GetString(1), reader.GetString(2), ct);
        return (program, visible);
    }

    public async Task<IReadOnlyList<string>> EventTypesAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT DISTINCT event_type FROM kernel.event_log ORDER BY event_type", connection);
        var result = new List<string>(); await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(reader.GetString(0));
        return result;
    }
}
