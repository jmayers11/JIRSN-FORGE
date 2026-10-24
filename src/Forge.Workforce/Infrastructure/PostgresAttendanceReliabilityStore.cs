using Forge.Kernel.Postgres;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Workforce.Infrastructure;

internal sealed class PostgresAttendanceReliabilityStore(PostgresUnitOfWork uow) : IAttendanceReliabilityStore
{
    public async Task<IReadOnlyList<ScheduledAttendanceOpportunity>> ListAttendanceOpportunitiesAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var rows = new List<ScheduledAttendanceOpportunity>();
        await using var cmd = uow.Command("""
            SELECT opportunity_id, person_id, shift_code, scheduled_start, scheduled_end, state, supersedes_opportunity_id
            FROM workforce.attendance_opportunity
            WHERE scheduled_start < $2 AND scheduled_end > $1
            ORDER BY scheduled_start, person_id, opportunity_id
            """, from, to);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(ReadOpportunity(reader));
        return rows;
    }

    public async Task<ScheduledAttendanceOpportunity?> FindAttendanceOpportunityAsync(Guid personId, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = uow.Command("""
            SELECT opportunity_id, person_id, shift_code, scheduled_start, scheduled_end, state, supersedes_opportunity_id
            FROM workforce.attendance_opportunity
            WHERE person_id = $1 AND state = 'Scheduled'
              AND scheduled_start <= $2 + interval '4 hours' AND scheduled_end >= $2 - interval '4 hours'
            ORDER BY abs(extract(epoch FROM (scheduled_start - $2))), opportunity_id
            LIMIT 2
            """, personId, at);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var selected = ReadOpportunity(reader);
        return await reader.ReadAsync(ct) ? null : selected;
    }

    public async Task<bool> InsertAttendanceOpportunityAsync(ScheduledAttendanceOpportunity opportunity, string source, string sourceKey, DateTimeOffset recordedAt, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command("""
            INSERT INTO workforce.attendance_opportunity
                (opportunity_id, person_id, shift_code, scheduled_start, scheduled_end, state, source, source_key, supersedes_opportunity_id, recorded_at, recorded_by)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)
            ON CONFLICT (source_key) DO NOTHING
            """, opportunity.OpportunityId, opportunity.PersonId, opportunity.ShiftCode, opportunity.ScheduledStart, opportunity.ScheduledEnd,
            opportunity.State.ToString(), source, sourceKey, (object?)opportunity.SupersedesOpportunityId ?? DBNull.Value, recordedAt, actor);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<IReadOnlyList<AttendanceIntelligenceEvent>> ListAttendanceIntelligenceEventsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var rows = new List<AttendanceIntelligenceEvent>();
        await using var cmd = uow.Command("""
            SELECT e.event_id, e.person_id, e.opportunity_id, e.kind, e.observed_at, e.review_state, e.source, e.recorded_at,
                   e.supersedes_event_id, e.reason_code, e.recorded_by, e.source_record_id
            FROM workforce.attendance_intelligence_event e
            JOIN workforce.attendance_opportunity o USING (opportunity_id, person_id)
            WHERE o.scheduled_start >= $1 AND o.scheduled_start < $2
            ORDER BY o.scheduled_start, e.recorded_at, e.event_id
            """, from, to);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(ReadEvent(reader));
        return rows;
    }

    public async Task<AttendanceIntelligenceEvent?> FindAttendanceIntelligenceEventAsync(Guid eventId, CancellationToken ct)
    {
        await using var cmd = uow.Command("""
            SELECT event_id, person_id, opportunity_id, kind, observed_at, review_state, source, recorded_at,
                   supersedes_event_id, reason_code, recorded_by, source_record_id
            FROM workforce.attendance_intelligence_event WHERE event_id = $1
            """, eventId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadEvent(reader) : null;
    }

    public async Task InsertAttendanceIntelligenceEventAsync(AttendanceIntelligenceEvent item, CancellationToken ct)
    {
        await using var cmd = uow.Command("""
            INSERT INTO workforce.attendance_intelligence_event
                (event_id, opportunity_id, person_id, kind, observed_at, review_state, source, recorded_at, recorded_by, supersedes_event_id, reason_code)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)
            """, item.EventId, item.OpportunityId, item.PersonId, item.Kind.ToString(), (object?)item.ObservedAt ?? DBNull.Value,
            item.ReviewState.ToString(), item.Source.ToString(), item.RecordedAt, item.RecordedBy,
            (object?)item.SupersedesEventId ?? DBNull.Value, (object?)item.ReasonCode ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> InsertImportedAttendanceIntelligenceEventAsync(AttendanceIntelligenceEvent item, string sourceRecordId, CancellationToken ct)
    {
        await using var cmd = uow.Command("""
            INSERT INTO workforce.attendance_intelligence_event
                (event_id, opportunity_id, person_id, kind, observed_at, review_state, source, recorded_at, recorded_by,
                 supersedes_event_id, reason_code, source_record_id)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)
            ON CONFLICT (source, source_record_id) WHERE source_record_id IS NOT NULL DO NOTHING
            """, item.EventId, item.OpportunityId, item.PersonId, item.Kind.ToString(), (object?)item.ObservedAt ?? DBNull.Value,
            item.ReviewState.ToString(), item.Source.ToString(), item.RecordedAt, item.RecordedBy,
            (object?)item.SupersedesEventId ?? DBNull.Value, (object?)item.ReasonCode ?? DBNull.Value, sourceRecordId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<IReadOnlyList<AttendancePolicyVersion>> ListAttendancePoliciesAsync(CancellationToken ct)
    {
        var rows = new List<AttendancePolicyVersion>();
        await using var cmd = uow.Command("""
            SELECT policy_code, revision, effective_from, effective_through, grace_minutes, include_callouts_in_arrival_denominator
            FROM workforce.attendance_policy ORDER BY policy_code, revision
            """);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(new AttendancePolicyVersion(reader.GetString(0), reader.GetInt32(1),
            reader.GetFieldValue<DateTimeOffset>(2), reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3), reader.GetInt32(4), reader.GetBoolean(5)));
        return rows;
    }

    public async Task<bool> InsertAttendancePolicyAsync(AttendancePolicyVersion policy, DateTimeOffset recordedAt, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command("""
            INSERT INTO workforce.attendance_policy
                (policy_code, revision, effective_from, effective_through, grace_minutes, include_callouts_in_arrival_denominator, recorded_at, recorded_by)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8) ON CONFLICT (policy_code, revision) DO NOTHING
            """, policy.PolicyCode, policy.Revision, policy.EffectiveFrom, (object?)policy.EffectiveThrough ?? DBNull.Value,
            policy.GraceMinutes, policy.IncludeReportedCallOutsInArrivalDenominator, recordedAt, actor);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    private static ScheduledAttendanceOpportunity ReadOpportunity(Npgsql.NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3),
        reader.GetFieldValue<DateTimeOffset>(4), Enum.Parse<AttendanceOpportunityState>(reader.GetString(5)),
        reader.IsDBNull(6) ? null : reader.GetGuid(6));

    private static AttendanceIntelligenceEvent ReadEvent(Npgsql.NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), Enum.Parse<AttendanceEventKind>(reader.GetString(3)),
        reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4), Enum.Parse<AttendanceEventReviewState>(reader.GetString(5)),
        Enum.Parse<AttendanceEventSource>(reader.GetString(6)), reader.GetFieldValue<DateTimeOffset>(7),
        reader.IsDBNull(8) ? null : reader.GetGuid(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetString(10),
        reader.IsDBNull(11) ? null : reader.GetString(11));
}
