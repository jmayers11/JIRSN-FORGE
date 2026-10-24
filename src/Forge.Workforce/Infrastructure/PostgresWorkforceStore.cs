using Forge.Kernel.Identity;
using Forge.Kernel.Commands;
using Forge.Kernel.Postgres;
using Forge.Workforce.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Forge.Workforce.Infrastructure;

internal sealed class PostgresWorkforceStore(PostgresUnitOfWork uow) : IWorkforceStore, IWorkforceLookup, IAttendanceStore
{
    public async Task<bool> InsertBadgeAsync(string digest, PersonId person, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "INSERT INTO workforce.badge (badge_digest, person_id, issued_at, issued_by) VALUES ($1, $2, $3, $4) ON CONFLICT (badge_digest) DO NOTHING",
            digest, person.Value, at, actor);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<int> RevokeBadgesAsync(PersonId person, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE workforce.badge SET revoked_at = $2, revoked_by = $3 WHERE person_id = $1 AND revoked_at IS NULL",
            person.Value, at, actor);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<BadgeRow?> FindBadgeAsync(string digest, CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT b.person_id, b.revoked_at IS NOT NULL OR NOT p.is_active, b.pin_hash, b.pin_salt FROM workforce.badge b JOIN kernel.person p ON p.person_id = b.person_id WHERE b.badge_digest = $1", digest);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new BadgeRow(digest, reader.GetGuid(0), reader.GetBoolean(1), reader.IsDBNull(2) ? null : reader.GetFieldValue<byte[]>(2), reader.IsDBNull(3) ? null : reader.GetFieldValue<byte[]>(3))
            : null;
    }

    public async Task SetPinAsync(string digest, byte[] hash, byte[] salt, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = uow.Command("UPDATE workforce.badge SET pin_hash = $2, pin_salt = $3, pin_set_at = $4, pin_failed_attempts = 0, pin_locked_until = NULL WHERE badge_digest = $1", digest, hash, salt, at);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> OpenAttendanceAsync(PersonId person, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "INSERT INTO workforce.attendance (person_id, clock_in_at, clock_in_by) VALUES ($1, $2, $3) RETURNING attendance_id",
            person.Value, at, actor);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task CloseAttendanceAsync(long attendanceId, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command("UPDATE workforce.attendance SET clock_out_at = $2, clock_out_by = $3 WHERE attendance_id = $1", attendanceId, at, actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<BadgeHolder?> FindBadgeHolderAsync(string badge, CancellationToken ct)
    {
        if (!Badges.TryNormalize(badge, out _))
        {
            return null;
        }

        await using var cmd = uow.Command(
            """
            SELECT b.person_id, p.display_name, w.person_id IS NOT NULL, b.pin_hash IS NOT NULL
            FROM workforce.badge b
            JOIN kernel.person p ON p.person_id = b.person_id AND p.is_active
            LEFT JOIN workforce.worker w ON w.person_id = b.person_id
            WHERE b.badge_digest = $1 AND b.revoked_at IS NULL
            """,
            Badges.Digest(badge));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new BadgeHolder(reader.GetGuid(0), reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3)) : null;
    }

    public async Task<AttendanceRecord?> FindOpenAttendanceAsync(Guid personId, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "SELECT attendance_id, person_id, clock_in_at, clock_out_at FROM workforce.attendance WHERE person_id = $1 AND clock_out_at IS NULL", personId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadAttendance(reader) : null;
    }

    public async Task<IReadOnlyList<AttendanceRecord>> ListAttendanceAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var list = new List<AttendanceRecord>();
        await using var cmd = uow.Command(
            "SELECT attendance_id, person_id, clock_in_at, clock_out_at FROM workforce.attendance WHERE clock_in_at < $2 AND (clock_out_at IS NULL OR clock_out_at > $1) ORDER BY clock_in_at, attendance_id",
            from, to);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(ReadAttendance(reader));
        }

        return list;
    }

    private static AttendanceRecord ReadAttendance(NpgsqlDataReader r) =>
        new(r.GetInt64(0), r.GetGuid(1), r.GetFieldValue<DateTimeOffset>(2), r.IsDBNull(3) ? null : r.GetFieldValue<DateTimeOffset>(3));

    public async Task<bool> InsertWorkerAsync(PersonId person, string displayName, string homeWorkCenter, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "INSERT INTO workforce.worker (person_id, display_name, home_work_center, created_at, created_by) VALUES ($1, $2, $3, $4, $5) ON CONFLICT DO NOTHING",
            person.Value, displayName, homeWorkCenter, at, actor);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> WorkerExistsAsync(Guid personId, CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT EXISTS (SELECT 1 FROM workforce.worker WHERE person_id = $1)", personId);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<WorkerPhotoInfo?> FindWorkerPhotoAsync(Guid personId, CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT photo_sha256, photo_content_type FROM workforce.worker WHERE person_id = $1 AND photo_sha256 IS NOT NULL", personId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new WorkerPhotoInfo(reader.GetString(0), reader.GetString(1)) : null;
    }

    public async Task SetWorkerPhotoAsync(PersonId person, string? sha256, string? contentType, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command("UPDATE workforce.worker SET photo_sha256 = $2, photo_content_type = $3, photo_updated_at = $4, photo_updated_by = $5 WHERE person_id = $1",
            person.Value, Text(sha256), Text(contentType), at, actor);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException($"Worker {person} was not updated.");
    }

    public async Task<WorkAssignment?> FindAssignmentAsync(PersonId person, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "SELECT safety_area_code, work_center_code, machine_code, activity_code, as_trainee FROM workforce.assignment WHERE person_id = $1",
            person.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadAssignment(reader, 0) : null;
    }

    public async Task SetAssignmentAsync(PersonId person, WorkAssignment? assignment, DateTimeOffset at, string actor, CancellationToken ct)
    {
        if (assignment is null)
        {
            await using var delete = uow.Command("DELETE FROM workforce.assignment WHERE person_id = $1", person.Value);
            await delete.ExecuteNonQueryAsync(ct);
            return;
        }

        await using var cmd = uow.Command(
            """
            INSERT INTO workforce.assignment (person_id, safety_area_code, work_center_code, machine_code, activity_code, as_trainee, assigned_at, assigned_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            ON CONFLICT (person_id) DO UPDATE SET safety_area_code = EXCLUDED.safety_area_code, work_center_code = EXCLUDED.work_center_code,
                machine_code = EXCLUDED.machine_code, activity_code = EXCLUDED.activity_code, as_trainee = EXCLUDED.as_trainee,
                assigned_at = EXCLUDED.assigned_at, assigned_by = EXCLUDED.assigned_by
            """,
            person.Value, assignment.SafetyAreaCode, Text(assignment.WorkCenterCode), Text(assignment.MachineCode), assignment.ActivityCode, assignment.AsTrainee, at, actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertPresenceAsync(PersonId person, PresenceKind kind, DateTimeOffset recordedAt, DateTimeOffset validUntil, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "INSERT INTO workforce.presence (person_id, kind, recorded_at, valid_until, recorded_by) VALUES ($1, $2, $3, $4, $5)",
            person.Value, kind.ToString(), recordedAt, validUntil, actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> EndPresenceAsync(PersonId person, DateTimeOffset at, CancellationToken ct)
    {
        // Ends current evidence by moving its end to now; the record itself is kept.
        await using var cmd = uow.Command(
            "UPDATE workforce.presence SET valid_until = GREATEST($2, recorded_at + interval '1 microsecond') WHERE person_id = $1 AND recorded_at <= $2 AND valid_until > $2",
            person.Value, at);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> SetShiftAsync(PersonId person, string? shiftCode, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE workforce.worker w SET shift_code = $2 FROM workforce.worker old WHERE old.person_id = w.person_id AND w.person_id = $1 RETURNING old.shift_code",
            person.Value, Text(shiftCode));
        var previous = await cmd.ExecuteScalarAsync(ct);
        return previous is DBNull or null ? null : (string)previous;
    }

    public async Task<string?> SetJobTitleAsync(PersonId person, string? jobTitle, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE workforce.worker w SET job_title = $2, updated_at = $3, updated_by = $4 FROM workforce.worker old WHERE old.person_id = w.person_id AND w.person_id = $1 AND w.job_title IS DISTINCT FROM $2 RETURNING old.job_title",
            person.Value, Text(jobTitle), at, actor);
        var previous = await cmd.ExecuteScalarAsync(ct);
        if (previous is null)
        {
            if (!await WorkerExistsAsync(person.Value, ct))
                throw new CommandRejectedException("worker_not_found", "This person is not a registered worker.");
            return jobTitle;
        }
        return previous is DBNull ? null : (string)previous;
    }

    public async Task<string?> SetDepartmentAsync(PersonId person, string? department, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE workforce.worker w SET department = $2, updated_at = $3, updated_by = $4 FROM workforce.worker old WHERE old.person_id = w.person_id AND w.person_id = $1 AND w.department IS DISTINCT FROM $2 RETURNING old.department",
            person.Value, Text(department), at, actor);
        var previous = await cmd.ExecuteScalarAsync(ct);
        if (previous is null)
        {
            if (!await WorkerExistsAsync(person.Value, ct))
                throw new CommandRejectedException("worker_not_found", "This person is not a registered worker.");
            return department;
        }
        return previous is DBNull ? null : (string)previous;
    }

    public async Task<IReadOnlyList<UnavailabilityWindow>> ListUnavailabilityAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var list = new List<UnavailabilityWindow>();
        await using var cmd = uow.Command(
            "SELECT person_id, kind, starts_at, ends_at FROM workforce.unavailability WHERE starts_at < $2 AND ends_at > $1 ORDER BY person_id, starts_at, unavailability_id",
            from, to);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new UnavailabilityWindow(reader.GetGuid(0), Enum.Parse<UnavailabilityKind>(reader.GetString(1)), reader.GetFieldValue<DateTimeOffset>(2), reader.GetFieldValue<DateTimeOffset>(3)));
        }

        return list;
    }

    public async Task InsertUnavailabilityAsync(PersonId person, UnavailabilityKind kind, DateTimeOffset startsAt, DateTimeOffset endsAt, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "INSERT INTO workforce.unavailability (person_id, kind, starts_at, ends_at, recorded_at, recorded_by) VALUES ($1, $2, $3, $4, $5, $6)",
            person.Value, kind.ToString(), startsAt.ToUniversalTime(), endsAt.ToUniversalTime(), at, actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<WorkerState>> GetWorkerStatesAsync(DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            """
            SELECT w.person_id, w.display_name, COALESCE(h.work_center, w.home_work_center), p.is_active,
                   pr.kind, pr.valid_until,
                   un.kind,
                   a.safety_area_code, a.work_center_code, a.machine_code, a.activity_code, a.as_trainee, w.shift_code, w.job_title, w.photo_sha256 IS NOT NULL, w.department
            FROM workforce.worker w
            JOIN kernel.person p ON p.person_id = w.person_id
            LEFT JOIN LATERAL (
                SELECT kind, valid_until FROM workforce.presence
                WHERE person_id = w.person_id AND recorded_at <= $1 AND valid_until > $1
                ORDER BY valid_until DESC LIMIT 1) pr ON true
            LEFT JOIN LATERAL (
                SELECT kind FROM workforce.unavailability
                WHERE person_id = w.person_id AND starts_at <= $1 AND ends_at > $1
                ORDER BY starts_at, unavailability_id LIMIT 1) un ON true
            LEFT JOIN workforce.assignment a ON a.person_id = w.person_id
            -- The home in force on the home site's date (ADR 0057 §5): the latest transfer that has taken effect.
            LEFT JOIN LATERAL (
                SELECT work_center FROM workforce.home_history
                WHERE person_id = w.person_id
                  AND effective_from <= ($1 AT TIME ZONE COALESCE((SELECT time_zone FROM resources.site ORDER BY created_at, code LIMIT 1), 'America/New_York'))::date
                ORDER BY effective_from DESC LIMIT 1) h ON true
            ORDER BY w.person_id
            """,
            at);
        var states = new List<WorkerState>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            states.Add(new WorkerState(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                reader.IsDBNull(4) ? null : Enum.Parse<PresenceKind>(reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
                reader.IsDBNull(6) ? null : Enum.Parse<UnavailabilityKind>(reader.GetString(6)),
                reader.IsDBNull(7) ? null : ReadAssignment(reader, 7),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.GetBoolean(14),
                reader.IsDBNull(15) ? null : reader.GetString(15)));
        }

        return states;
    }

    private const string MoveSql = """
        SELECT m.move_id, m.person_id, w.display_name, m.from_work_center, m.to_work_center, m.starts_on, m.ends_on, m.status, m.created_at, m.created_by,
               m.reminded_for, m.closed_at, m.closed_by
        FROM workforce.temporary_move m JOIN workforce.worker w ON w.person_id = m.person_id
        """;

    public async Task<long> InsertTemporaryMoveAsync(Guid person, string from, string to, DateOnly startsOn, DateOnly endsOn, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "INSERT INTO workforce.temporary_move (person_id, from_work_center, to_work_center, starts_on, ends_on, status, created_at, created_by) VALUES ($1, $2, $3, $4, $5, 'Active', $6, $7) RETURNING move_id",
            person, from, to, startsOn, endsOn, at, actor);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<TemporaryMoveInfo?> FindTemporaryMoveAsync(long moveId, CancellationToken ct) =>
        await ReadMovesAsync(MoveSql + " WHERE m.move_id = $1 FOR UPDATE OF m", ct, moveId) is [var move, ..] ? move : null;

    public Task<IReadOnlyList<TemporaryMoveInfo>> ListTemporaryMovesAsync(Guid? personId, bool includeClosed, CancellationToken ct) =>
        ReadMovesAsync(MoveSql + " WHERE ($1::uuid IS NULL OR m.person_id = $1) AND ($2 OR m.status = 'Active') ORDER BY m.move_id DESC", ct,
            new NpgsqlParameter { Value = (object?)personId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid }, includeClosed);

    private async Task<IReadOnlyList<TemporaryMoveInfo>> ReadMovesAsync(string sql, CancellationToken ct, params object[] args)
    {
        var rows = new List<TemporaryMoveInfo>();
        await using (var cmd = uow.Command(sql, args))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                rows.Add(new TemporaryMoveInfo(r.GetInt64(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetFieldValue<DateOnly>(5), r.GetFieldValue<DateOnly>(6),
                    Enum.Parse<TemporaryMoveStatus>(r.GetString(7)), r.GetFieldValue<DateTimeOffset>(8), r.GetString(9),
                    r.IsDBNull(10) ? null : r.GetFieldValue<DateOnly>(10), r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11),
                    r.IsDBNull(12) ? null : r.GetString(12), []));
            }
        }

        if (rows.Count == 0)
        {
            return rows;
        }

        var changes = new List<(long MoveId, TemporaryMoveChange Change)>();
        await using (var cmd = uow.Command(
            "SELECT move_id, change_number, kind, previous_ends_on, ends_on, changed_at, changed_by FROM workforce.temporary_move_change WHERE move_id = ANY($1) ORDER BY move_id, change_number",
            rows.Select(m => m.MoveId).ToArray()))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                changes.Add((r.GetInt64(0), new TemporaryMoveChange(r.GetInt32(1), r.GetString(2), r.GetFieldValue<DateOnly>(3), r.GetFieldValue<DateOnly>(4),
                    r.GetFieldValue<DateTimeOffset>(5), r.GetString(6))));
            }
        }

        var byMove = changes.ToLookup(c => c.MoveId, c => c.Change);
        return rows.Select(m => m with { Changes = byMove[m.MoveId].ToList() }).ToList();
    }

    public async Task ChangeTemporaryMoveAsync(long moveId, string kind, DateOnly previousEndsOn, DateOnly endsOn, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using (var insert = uow.Command(
            """
            INSERT INTO workforce.temporary_move_change (move_id, change_number, kind, previous_ends_on, ends_on, changed_at, changed_by)
            SELECT $1, COALESCE(max(change_number), 0) + 1, $2, $3, $4, $5, $6 FROM workforce.temporary_move_change WHERE move_id = $1
            """,
            moveId, kind, previousEndsOn, endsOn, at, actor))
        {
            await insert.ExecuteNonQueryAsync(ct);
        }

        await using var update = uow.Command("UPDATE workforce.temporary_move SET ends_on = $2, reminded_for = NULL WHERE move_id = $1", moveId, endsOn);
        await update.ExecuteNonQueryAsync(ct);
    }

    public async Task CloseTemporaryMoveAsync(long moveId, TemporaryMoveStatus status, DateOnly endsOn, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE workforce.temporary_move SET status = $2, ends_on = $3, closed_at = $4, closed_by = $5 WHERE move_id = $1 AND status = 'Active'",
            moveId, status.ToString(), endsOn, at, actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetMoveRemindedAsync(long moveId, DateOnly endsOn, CancellationToken ct)
    {
        await using var cmd = uow.Command("UPDATE workforce.temporary_move SET reminded_for = $2 WHERE move_id = $1", moveId, endsOn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long?> InsertTransferAsync(Guid person, string from, string to, DateOnly effectiveFrom, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            """
            INSERT INTO workforce.home_history (person_id, from_work_center, work_center, effective_from, recorded_at, recorded_by)
            VALUES ($1, $2, $3, $4, $5, $6) ON CONFLICT (person_id, effective_from) DO NOTHING RETURNING transfer_id
            """,
            person, from, to, effectiveFrom, at, actor);
        return await cmd.ExecuteScalarAsync(ct) is long id ? id : null;
    }

    public async Task<IReadOnlyList<HomeTransfer>> ListTransfersAsync(Guid? personId, CancellationToken ct)
    {
        var list = new List<HomeTransfer>();
        await using var cmd = uow.Command(
            "SELECT transfer_id, person_id, from_work_center, work_center, effective_from, recorded_at, recorded_by FROM workforce.home_history WHERE ($1::uuid IS NULL OR person_id = $1) ORDER BY person_id, effective_from",
            new NpgsqlParameter { Value = (object?)personId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new HomeTransfer(r.GetInt64(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateOnly>(4), r.GetFieldValue<DateTimeOffset>(5), r.GetString(6)));
        }

        return list;
    }

    public async Task<IReadOnlyList<WorkCenterPeriod>> ListWorkCenterPeriodsAsync(CancellationToken ct)
    {
        var homes = new List<(Guid Person, string Center)>();
        await using (var cmd = uow.Command("SELECT person_id, home_work_center FROM workforce.worker ORDER BY person_id"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                homes.Add((r.GetGuid(0), r.GetString(1)));
            }
        }

        var transfers = (await ListTransfersAsync(null, ct)).ToLookup(t => t.PersonId);
        var moves = (await ListTemporaryMovesAsync(null, true, ct)).Where(m => m.EndsOn >= m.StartsOn).ToLookup(m => m.PersonId);
        var periods = new List<WorkCenterPeriod>();
        foreach (var (person, registered) in homes)
        {
            var center = registered;
            var from = DateOnly.MinValue;
            foreach (var transfer in transfers[person].OrderBy(t => t.EffectiveFrom))
            {
                periods.Add(new WorkCenterPeriod(person, center, from, transfer.EffectiveFrom.AddDays(-1), "Home"));
                (center, from) = (transfer.WorkCenter, transfer.EffectiveFrom);
            }

            periods.Add(new WorkCenterPeriod(person, center, from, null, "Home"));
            periods.AddRange(moves[person].OrderBy(m => m.StartsOn).Select(m => new WorkCenterPeriod(person, m.ToWorkCenter, m.StartsOn, m.EndsOn, "TemporaryMove", m.MoveId)));
        }

        return periods.OrderBy(p => p.PersonId).ThenBy(p => p.From).ThenBy(p => p.Kind, StringComparer.Ordinal).ToList();
    }

    private static WorkAssignment ReadAssignment(NpgsqlDataReader r, int o) => new(
        r.GetString(o),
        r.IsDBNull(o + 1) ? null : r.GetString(o + 1),
        r.IsDBNull(o + 2) ? null : r.GetString(o + 2),
        r.GetString(o + 3),
        r.GetBoolean(o + 4));

    private static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text };
}

public static class WorkforceRegistration
{
    public static IServiceCollection AddForgeWorkforce(this IServiceCollection services)
    {
        services.AddForgeMigrations(typeof(WorkforceRegistration).Assembly, "workforce");
        services.AddForgeStore<IWorkforceStore>(uow => new PostgresWorkforceStore(uow));
        services.AddForgeStore<IWorkforceLookup>(uow => new PostgresWorkforceStore(uow));
        services.AddForgeStore<IAttendanceStore>(uow => new PostgresWorkforceStore(uow));
        services.AddForgeStore<IWorkforceDevelopmentStore>(uow => new PostgresWorkforceDevelopmentStore(uow));
        services.AddForgeStore<ICodedTimeStore>(uow => new PostgresCodedTimeStore(uow));
        services.AddForgeStore<ICodedTimeLookup>(uow => new PostgresCodedTimeStore(uow));
        services.AddSingleton<CodedTimeService>();
        services.AddForgeStore<ICoverageStore>(uow => new PostgresCoverageStore(uow));
        services.AddForgeStore<ICoverageLookup>(uow => new PostgresCoverageStore(uow));
        services.AddSingleton<CoverageService>();
        services.AddSingleton<DevelopmentNominationService>();
        services.AddForgeStore<IAttendanceReliabilityStore>(uow => new PostgresAttendanceReliabilityStore(uow));
        services.AddSingleton<IBadgePinVerifier, PostgresBadgePinVerifier>();
        services.AddSingleton<WorkforceService>();
        services.AddSingleton<AttendanceService>();
        services.AddSingleton<AttendanceIntelligenceService>();
        return services;
    }
}
