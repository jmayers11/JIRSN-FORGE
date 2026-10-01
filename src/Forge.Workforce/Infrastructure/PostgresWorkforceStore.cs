using Forge.Kernel.Identity;
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
        await using var cmd = uow.Command("SELECT person_id, revoked_at IS NOT NULL, pin_hash, pin_salt FROM workforce.badge WHERE badge_digest = $1", digest);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new BadgeRow(digest, reader.GetGuid(0), reader.GetBoolean(1), reader.IsDBNull(2) ? null : reader.GetFieldValue<byte[]>(2), reader.IsDBNull(3) ? null : reader.GetFieldValue<byte[]>(3))
            : null;
    }

    public async Task SetPinAsync(string digest, byte[] hash, byte[] salt, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = uow.Command("UPDATE workforce.badge SET pin_hash = $2, pin_salt = $3, pin_set_at = $4 WHERE badge_digest = $1", digest, hash, salt, at);
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

    public async Task<bool> VerifyBadgePinAsync(string badge, string pin, CancellationToken ct)
    {
        if (!Badges.TryNormalize(badge, out _) || !Badges.IsValidPin(pin))
        {
            return false;
        }

        var row = await FindBadgeAsync(Badges.Digest(badge), ct);
        return row is { Revoked: false, PinHash: { } hash, PinSalt: { } salt } && Badges.PinMatches(pin, hash, salt);
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
            SELECT w.person_id, w.display_name, w.home_work_center, p.is_active,
                   pr.kind, pr.valid_until,
                   un.kind,
                   a.safety_area_code, a.work_center_code, a.machine_code, a.activity_code, a.as_trainee, w.shift_code
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
                reader.IsDBNull(12) ? null : reader.GetString(12)));
        }

        return states;
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
        services.AddSingleton<WorkforceService>();
        services.AddSingleton<AttendanceService>();
        return services;
    }
}
