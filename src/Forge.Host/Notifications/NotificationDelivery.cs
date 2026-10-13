using System.Net.Http.Json;
using Forge.Kernel.Postgres;
using Forge.Kernel.Access;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.WorkAssignments;
using Microsoft.Extensions.Hosting;
using NodaTime;
using Npgsql;

namespace Forge.Host.Notifications;

public sealed record NotificationEmailMessage(Guid RecipientPersonId, string Subject, string Body, string DeepLink, string AlertSeverity, string Confidence);

/// <summary>Provider boundary: identity/email address resolution belongs to the configured mail provider.</summary>
public interface INotificationEmailProvider
{
    Task SendAsync(NotificationEmailMessage message, string idempotencyKey, CancellationToken ct);
}

public sealed class ConfiguredEmailProvider(HttpClient client, Uri endpoint) : INotificationEmailProvider
{
    public async Task SendAsync(NotificationEmailMessage message, string idempotencyKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(message) };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
    }
}

public sealed class UnconfiguredEmailProvider : INotificationEmailProvider
{
    public Task SendAsync(NotificationEmailMessage message, string idempotencyKey, CancellationToken ct) =>
        throw new InvalidOperationException("No notification email provider is configured.");
}

/// <summary>Retries email independently of business commands; provider failures are durable delivery state.</summary>
public sealed class NotificationDeliveryService(NpgsqlDataSource dataSource, INotificationEmailProvider email, TimeProvider time,
    IKernelUnitOfWorkFactory unitOfWork, IWorkSourceResolver sources, IForgeAuthorizer authorizer)
{
    public async Task<int> DispatchDueAsync(int limit, CancellationToken ct)
    {
        var jobs = new List<DeliveryJob>();
        await using (var connection = await dataSource.OpenConnectionAsync(ct))
        await using (var tx = await connection.BeginTransactionAsync(ct))
        {
            await using var select = new NpgsqlCommand("""
                SELECT d.delivery_id,d.alert_id,d.person_id,d.kind,d.idempotency_key,a.state,a.severity,a.confidence,a.title,a.message,a.resolution_message,a.deep_link,
                       r.quiet_start,r.quiet_end,r.time_zone,i.acknowledged_at,d.attempts,
                       d.recipient_role,a.source_context,a.entity_type,a.entity_id,a.program_id,a.markings
                FROM kernel.notification_delivery d
                JOIN kernel.notification_alert a ON a.alert_id=d.alert_id
                JOIN kernel.notification_rule r ON r.rule_id=a.rule_id
                LEFT JOIN kernel.notification_inbox i ON i.alert_id=d.alert_id AND i.person_id=d.person_id
                WHERE ((d.status IN ('Pending','Failed') AND COALESCE(d.next_attempt_at,d.due_at) <= $1)
                    OR (d.status='Processing' AND d.lease_until <= $1))
                ORDER BY COALESCE(d.next_attempt_at,d.due_at), d.delivery_id
                LIMIT $2 FOR UPDATE OF d SKIP LOCKED
                """, connection, tx);
            select.Parameters.AddWithValue(time.GetUtcNow()); select.Parameters.AddWithValue(Math.Clamp(limit, 1, 200));
            await using (var reader = await select.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                    jobs.Add(new DeliveryJob(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetFieldValue<TimeOnly>(12), reader.IsDBNull(13) ? null : reader.GetFieldValue<TimeOnly>(13), reader.GetString(14), reader.IsDBNull(15) ? null : reader.GetFieldValue<DateTimeOffset>(15), reader.GetInt32(16), reader.GetString(17), reader.GetString(18), reader.GetString(19), reader.GetString(20), reader.IsDBNull(21) ? null : reader.GetGuid(21), (DataMarking)reader.GetInt32(22)));
            }
            foreach (var job in jobs)
            {
                await using var claim = new NpgsqlCommand("UPDATE kernel.notification_delivery SET status='Processing',attempts=attempts+1,lease_until=$2,next_attempt_at=NULL WHERE delivery_id=$1", connection, tx);
                claim.Parameters.AddWithValue(job.DeliveryId); claim.Parameters.AddWithValue(time.GetUtcNow().AddMinutes(2)); await claim.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }

        foreach (var job in jobs)
        {
            if (job.State != "Open" && job.Kind != "Resolution")
            {
                await CompleteAsync(job.DeliveryId, "Suppressed", "Condition cleared before delivery.", ct);
                continue;
            }
            if (job.Kind == "Reminder" && job.AcknowledgedAt is not null)
            {
                await CompleteAsync(job.DeliveryId, "Suppressed", "Recipient already acknowledged the alert.", ct);
                continue;
            }
            var quietUntil = QuietUntil(job, time.GetUtcNow());
            if (quietUntil is { } until)
            {
                await RescheduleAsync(job.DeliveryId, until, ct);
                continue;
            }
            var subject = job.Kind == "Resolution" ? $"Resolved: {job.Title}" : job.Title;
            var body = job.Kind == "Resolution" ? job.ResolutionMessage ?? "The condition has cleared." : job.Message;
            try
            {
                if (!await CanDeliverAsync(job, ct))
                {
                    await CompleteAsync(job.DeliveryId, "Suppressed", "Recipient no longer has current access to this notification's source.", ct);
                    continue;
                }
                await email.SendAsync(new NotificationEmailMessage(job.PersonId, subject, body, job.DeepLink, job.Severity, job.Confidence), job.IdempotencyKey, ct);
                await CompleteAsync(job.DeliveryId, "Sent", null, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var backoff = Math.Min(3600, 5 * (1 << Math.Min(job.Attempts, 9)));
                await FailAsync(job.DeliveryId, ex.Message, time.GetUtcNow().AddSeconds(backoff), ct);
            }
        }
        return jobs.Count;
    }

    private async Task<bool> CanDeliverAsync(DeliveryJob job, CancellationToken ct)
    {
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var principal = await uow.People.LoadPrincipalAsync(new PersonId(job.PersonId), ct);
        if (principal is not { IsActive: true } || !principal.Roles.Contains(job.RecipientRole)
            || job.ProgramId is { } program && !principal.Programs.Contains(new ProgramId(program))
            || (job.Markings & ~principal.Markings) != DataMarking.None) return false;
        return await NotificationSourceAccess.CanSeeAsync(uow, sources, authorizer, principal, job.EntityType, job.EntityId, ct);
    }

    private static DateTimeOffset? QuietUntil(DeliveryJob job, DateTimeOffset now)
    {
        if (job.QuietStart is not { } start || job.QuietEnd is not { } end) return null;
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(job.TimeZone);
        if (zone is null) return now.AddHours(1);
        var local = Instant.FromDateTimeOffset(now).InZone(zone).LocalDateTime;
        var time = local.TimeOfDay;
        var quietStart = LocalTime.FromTicksSinceMidnight(start.Ticks);
        var quietEnd = LocalTime.FromTicksSinceMidnight(end.Ticks);
        var inQuiet = quietStart < quietEnd ? time >= quietStart && time < quietEnd : time >= quietStart || time < quietEnd;
        if (!inQuiet) return null;
        var date = local.Date;
        if (quietStart >= quietEnd && time >= quietStart) date = date.PlusDays(1);
        return zone.AtLeniently(date.At(quietEnd)).ToDateTimeOffset().ToUniversalTime();
    }

    private async Task CompleteAsync(Guid id, string state, string? reason, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("UPDATE kernel.notification_delivery SET status=$2,sent_at=now(),lease_until=NULL,last_error=$3,next_attempt_at=NULL WHERE delivery_id=$1", connection);
        cmd.Parameters.AddWithValue(id); cmd.Parameters.AddWithValue(state); cmd.Parameters.AddWithValue(reason ?? (object)DBNull.Value); await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task RescheduleAsync(Guid id, DateTimeOffset due, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("UPDATE kernel.notification_delivery SET status='Pending',lease_until=NULL,next_attempt_at=$2 WHERE delivery_id=$1", connection);
        cmd.Parameters.AddWithValue(id); cmd.Parameters.AddWithValue(due); await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task FailAsync(Guid id, string detail, DateTimeOffset retryAt, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("UPDATE kernel.notification_delivery SET status='Failed',lease_until=NULL,last_error=$2,next_attempt_at=$3 WHERE delivery_id=$1", connection);
        cmd.Parameters.AddWithValue(id); cmd.Parameters.AddWithValue(detail.Length > 500 ? detail[..500] : detail); cmd.Parameters.AddWithValue(retryAt); await cmd.ExecuteNonQueryAsync(ct);
    }

    private sealed record DeliveryJob(Guid DeliveryId, Guid AlertId, Guid PersonId, string Kind, string IdempotencyKey, string State,
        string Severity, string Confidence, string Title, string Message, string? ResolutionMessage, string DeepLink,
        TimeOnly? QuietStart, TimeOnly? QuietEnd, string TimeZone, DateTimeOffset? AcknowledgedAt, int Attempts,
        string RecipientRole, string SourceContext, string EntityType, string EntityId, Guid? ProgramId, DataMarking Markings);
}

public sealed class NotificationDeliveryWorker(NotificationDeliveryService service, TimeProvider time, ILogger<NotificationDeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await service.DispatchDueAsync(100, stoppingToken) == 0)
                    await Task.Delay(TimeSpan.FromSeconds(2), time, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (NpgsqlException ex)
            {
                logger.LogError(ex, "Notification delivery scan failed; retrying.");
                await Task.Delay(TimeSpan.FromSeconds(5), time, stoppingToken);
            }
        }
    }
}
