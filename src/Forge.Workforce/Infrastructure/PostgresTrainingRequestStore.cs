using Forge.Kernel.Postgres;
using Npgsql;

namespace Forge.Workforce.Infrastructure;

internal sealed class PostgresTrainingRequestStore(PostgresUnitOfWork uow) : ITrainingRequestStore
{
    private const string RequirementColumns = "requirement_id, work_center_code, qualification_code, due_days, note, active, created_at, created_by, updated_at, updated_by";
    private const string RequestColumns = "request_id, person_id, source, source_key, qualification_code, title, reason, due_on, status, trainer_person_id, scheduled_start, scheduled_end, location, outcome_note, requested_by, requested_by_person_id, requested_at, updated_at, version";

    public async Task<IReadOnlyList<OnboardingRequirement>> ListRequirementsAsync(bool activeOnly, CancellationToken ct)
    {
        var rows = new List<OnboardingRequirement>();
        await using var cmd = uow.Command($"SELECT {RequirementColumns} FROM workforce.onboarding_requirement WHERE (NOT $1 OR active) ORDER BY work_center_code NULLS FIRST, qualification_code, created_at", activeOnly);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(ReadRequirement(reader));
        return rows;
    }

    public async Task<OnboardingRequirement?> FindRequirementAsync(Guid requirementId, CancellationToken ct)
    {
        await using var cmd = uow.Command($"SELECT {RequirementColumns} FROM workforce.onboarding_requirement WHERE requirement_id=$1", requirementId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRequirement(reader) : null;
    }

    public async Task InsertRequirementAsync(OnboardingRequirement requirement, CancellationToken ct)
    {
        await using var cmd = uow.Command($"INSERT INTO workforce.onboarding_requirement ({RequirementColumns}) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)",
            requirement.RequirementId, Text(requirement.WorkCenterCode), requirement.QualificationCode, requirement.DueDays, requirement.Note, requirement.Active,
            requirement.CreatedAt, requirement.CreatedBy, requirement.UpdatedAt, requirement.UpdatedBy);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> RetireRequirementAsync(Guid requirementId, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command("UPDATE workforce.onboarding_requirement SET active=false, updated_at=$2, updated_by=$3 WHERE requirement_id=$1 AND active", requirementId, at, actor);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<IReadOnlyList<TrainingRequest>> ListRequestsAsync(Guid? personId, bool openOnly, CancellationToken ct)
    {
        var rows = new List<TrainingRequest>();
        await using var cmd = uow.Command(
            $"SELECT {RequestColumns} FROM workforce.training_request WHERE ($1::uuid IS NULL OR person_id=$1) AND (NOT $2 OR status IN ('Requested','Scheduled')) ORDER BY due_on NULLS LAST, requested_at, request_id",
            Uuid(personId), openOnly);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(ReadRequest(reader));
        return rows;
    }

    public async Task<TrainingRequest?> FindRequestAsync(Guid requestId, CancellationToken ct)
    {
        await using var cmd = uow.Command($"SELECT {RequestColumns} FROM workforce.training_request WHERE request_id=$1", requestId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRequest(reader) : null;
    }

    public async Task<bool> InsertRequestAsync(TrainingRequest r, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            $"INSERT INTO workforce.training_request ({RequestColumns}) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19) ON CONFLICT (person_id, source, source_key) DO NOTHING",
            r.RequestId, r.PersonId, r.Source.ToString(), r.SourceKey, Text(r.QualificationCode), r.Title, r.Reason, Date(r.DueOn), r.Status.ToString(),
            Uuid(r.TrainerPersonId), Time(r.ScheduledStart), Time(r.ScheduledEnd), Text(r.Location), Text(r.OutcomeNote), r.RequestedBy, Uuid(r.RequestedByPersonId),
            r.RequestedAt, r.UpdatedAt, r.Version);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> UpdateRequestAsync(TrainingRequest r, int expectedVersion, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE workforce.training_request SET status=$2, trainer_person_id=$3, scheduled_start=$4, scheduled_end=$5, location=$6, outcome_note=$7, updated_at=$8, version=$9 WHERE request_id=$1 AND version=$10",
            r.RequestId, r.Status.ToString(), Uuid(r.TrainerPersonId), Time(r.ScheduledStart), Time(r.ScheduledEnd), Text(r.Location), Text(r.OutcomeNote), r.UpdatedAt, r.Version, expectedVersion);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task InsertRequestEventAsync(TrainingRequestEvent e, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "INSERT INTO workforce.training_request_event (event_id, request_id, from_status, to_status, note, recorded_by, recorded_by_person_id, recorded_at) VALUES ($1,$2,$3,$4,$5,$6,$7,$8)",
            e.EventId, e.RequestId, Text(e.FromStatus?.ToString()), e.ToStatus.ToString(), e.Note, e.RecordedBy, Uuid(e.RecordedByPersonId), e.RecordedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<TrainingRequestEvent>> ListRequestEventsAsync(Guid requestId, CancellationToken ct)
    {
        var rows = new List<TrainingRequestEvent>();
        await using var cmd = uow.Command("SELECT event_id, request_id, from_status, to_status, note, recorded_by, recorded_by_person_id, recorded_at FROM workforce.training_request_event WHERE request_id=$1 ORDER BY recorded_at, event_id", requestId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new TrainingRequestEvent(reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : Enum.Parse<TrainingRequestStatus>(reader.GetString(2)),
                Enum.Parse<TrainingRequestStatus>(reader.GetString(3)), reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetGuid(6),
                reader.GetFieldValue<DateTimeOffset>(7)));
        return rows;
    }

    private static OnboardingRequirement ReadRequirement(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetString(4), r.GetBoolean(5),
        r.GetFieldValue<DateTimeOffset>(6), r.GetString(7), r.GetFieldValue<DateTimeOffset>(8), r.GetString(9));

    private static TrainingRequest ReadRequest(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), Enum.Parse<TrainingRequestSource>(r.GetString(2)), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5),
        r.GetString(6), r.IsDBNull(7) ? null : r.GetFieldValue<DateOnly>(7), Enum.Parse<TrainingRequestStatus>(r.GetString(8)), r.IsDBNull(9) ? null : r.GetGuid(9),
        r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10), r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11), r.IsDBNull(12) ? null : r.GetString(12),
        r.IsDBNull(13) ? null : r.GetString(13), r.GetString(14), r.IsDBNull(15) ? null : r.GetGuid(15), r.GetFieldValue<DateTimeOffset>(16),
        r.GetFieldValue<DateTimeOffset>(17), r.GetInt32(18));

    private static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text };
    private static NpgsqlParameter Uuid(Guid? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid };
    private static NpgsqlParameter Date(DateOnly? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date };
    private static NpgsqlParameter Time(DateTimeOffset? value) => new() { Value = value is { } v ? v.ToUniversalTime() : (object)DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz };
}
