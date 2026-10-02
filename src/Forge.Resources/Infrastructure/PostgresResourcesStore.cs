using Forge.Kernel.Postgres;
using Forge.Resources.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Forge.Resources.Infrastructure;

internal sealed class PostgresResourcesStore(PostgresUnitOfWork uow) : IResourcesStore, IResourcesLookup
{
    public async Task<bool> InsertAsync(string kind, string code, string name, string? parent, string? description, DateTimeOffset at, string actor, CancellationToken ct)
    {
        var sql = kind switch
        {
            "Site" => "INSERT INTO resources.site (code, name, created_at, created_by) VALUES ($1, $2, $4, $5) ON CONFLICT DO NOTHING",
            "SafetyArea" => "INSERT INTO resources.safety_area (code, name, site_code, created_at, created_by) VALUES ($1, $2, $3, $4, $5) ON CONFLICT DO NOTHING",
            "WorkCenter" => "INSERT INTO resources.work_center (code, name, safety_area_code, created_at, created_by) VALUES ($1, $2, $3, $4, $5) ON CONFLICT DO NOTHING",
            "Machine" => "INSERT INTO resources.machine (code, name, work_center_code, created_at, created_by) VALUES ($1, $2, $3, $4, $5) ON CONFLICT DO NOTHING",
            "ActivityType" => "INSERT INTO resources.activity_type (code, name, description, created_at, created_by) VALUES ($1, $2, $3, $4, $5) ON CONFLICT DO NOTHING",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown resource kind."),
        };
        var third = kind == "ActivityType" ? description : parent;
        // $3 is unused for sites; it is typed as text so PostgreSQL still accepts it.
        await using var cmd = uow.Command(sql, code, name, Text(third), at, actor);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task UpsertMachineActivityAsync(MachineActivityInfo profile, DateTimeOffset at, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await using var cmd = uow.Command(
            """
            INSERT INTO resources.machine_activity (machine_code, activity_code, operator_qualifications, updated_at, updated_by)
            VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT (machine_code, activity_code) DO UPDATE SET operator_qualifications = EXCLUDED.operator_qualifications,
                updated_at = EXCLUDED.updated_at, updated_by = EXCLUDED.updated_by
            """,
            profile.MachineCode, profile.ActivityCode, TextArray(profile.OperatorQualifications), at, actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> InsertWorkCenterActivityAsync(WorkCenterActivityInfo activity, DateTimeOffset at, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(activity);
        await using var cmd = uow.Command(
            "INSERT INTO resources.work_center_activity (work_center_code, activity_code, created_at, created_by) VALUES ($1, $2, $3, $4) ON CONFLICT DO NOTHING",
            activity.WorkCenterCode, activity.ActivityCode, at, actor);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<ResourceRecord?> FindAnyAsync(string kind, string code, CancellationToken ct)
    {
        var sql = kind switch
        {
            "Site" => "SELECT code, name, NULL, true, time_zone, NULL FROM resources.site WHERE code = $1",
            "SafetyArea" => "SELECT code, name, site_code, active, NULL, NULL FROM resources.safety_area WHERE code = $1",
            "WorkCenter" => "SELECT code, name, safety_area_code, active, NULL, picture FROM resources.work_center WHERE code = $1",
            "Machine" => "SELECT code, name, work_center_code, active, NULL, NULL FROM resources.machine WHERE code = $1",
            "ActivityType" => "SELECT code, name, NULL, active, description, NULL FROM resources.activity_type WHERE code = $1",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown resource kind."),
        };
        return (await Read(
            sql,
            r => new ResourceRecord(kind, r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetBoolean(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)),
            ct,
            code)).SingleOrDefault();
    }

    public async Task UpdateAsync(ResourceRecord record, DateTimeOffset at, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        var sql = record.Kind switch
        {
            // Every statement receives the same eight parameters; each uses the ones that apply to its table.
            "Site" => "UPDATE resources.site SET name = $2, time_zone = $5, updated_at = $7, updated_by = $8 WHERE code = $1",
            "SafetyArea" => "UPDATE resources.safety_area SET name = $2, active = $4, updated_at = $7, updated_by = $8 WHERE code = $1",
            "WorkCenter" => "UPDATE resources.work_center SET name = $2, safety_area_code = $3, active = $4, picture = $6, updated_at = $7, updated_by = $8 WHERE code = $1",
            "Machine" => "UPDATE resources.machine SET name = $2, work_center_code = $3, active = $4, updated_at = $7, updated_by = $8 WHERE code = $1",
            "ActivityType" => "UPDATE resources.activity_type SET name = $2, active = $4, description = $5, updated_at = $7, updated_by = $8 WHERE code = $1",
            _ => throw new ArgumentOutOfRangeException(nameof(record), record.Kind, "Unknown resource kind."),
        };
        await using var cmd = uow.Command(sql, record.Code, record.Name, Text(record.Parent), record.Active, Text(record.Detail), Text(record.Picture), at, actor);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1)
        {
            throw new InvalidOperationException($"{record.Kind} {record.Code} was not updated.");
        }
    }

    public async Task<bool> DeleteMachineActivityAsync(string machineCode, string activityCode, CancellationToken ct)
    {
        await using var cmd = uow.Command("DELETE FROM resources.machine_activity WHERE machine_code = $1 AND activity_code = $2", machineCode, activityCode);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> DeleteWorkCenterActivityAsync(string workCenterCode, string activityCode, CancellationToken ct)
    {
        await using var cmd = uow.Command("DELETE FROM resources.work_center_activity WHERE work_center_code = $1 AND activity_code = $2", workCenterCode, activityCode);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> DeleteWorkCenterShiftAsync(WorkCenterShiftInfo link, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(link);
        await using var cmd = uow.Command("DELETE FROM resources.work_center_shift WHERE work_center_code = $1 AND shift_code = $2", link.WorkCenterCode, link.ShiftCode);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task SetWorkCenterPictureAsync(string code, string? picture, CancellationToken ct)
    {
        await using var cmd = uow.Command("UPDATE resources.work_center SET picture = $2 WHERE code = $1", code, Text(picture));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task<IReadOnlyList<WorkCenterActivityInfo>> ListWorkCenterActivitiesAsync(CancellationToken ct) =>
        Read("SELECT work_center_code, activity_code FROM resources.work_center_activity ORDER BY work_center_code, activity_code", r => new WorkCenterActivityInfo(r.GetString(0), r.GetString(1)), ct);

    public Task<IReadOnlyList<SiteInfo>> ListSitesAsync(CancellationToken ct) =>
        Read("SELECT code, name, time_zone FROM resources.site ORDER BY code", r => new SiteInfo(r.GetString(0), r.GetString(1), r.GetString(2)), ct);

    public async Task SetSiteTimeZoneAsync(string siteCode, string timeZone, CancellationToken ct)
    {
        await using var cmd = uow.Command("UPDATE resources.site SET time_zone = $2 WHERE code = $1", siteCode, timeZone);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> InsertShiftAsync(ShiftInfo shift, DateTimeOffset at, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(shift);
        await using var cmd = uow.Command(
            "INSERT INTO resources.shift (code, site_code, name, start_time, duration_minutes, days, created_at, created_by) VALUES ($1, $2, $3, $4, $5, $6, $7, $8) ON CONFLICT DO NOTHING",
            shift.Code, shift.SiteCode, shift.Name, shift.Start, shift.DurationMinutes,
            new NpgsqlParameter { Value = shift.Days.Select(d => (short)d).ToArray(), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Smallint },
            at, actor);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> InsertWorkCenterShiftAsync(WorkCenterShiftInfo link, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(link);
        await using var cmd = uow.Command("INSERT INTO resources.work_center_shift (work_center_code, shift_code) VALUES ($1, $2) ON CONFLICT DO NOTHING", link.WorkCenterCode, link.ShiftCode);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> InsertSiteClosureAsync(SiteClosureInfo closure, DateTimeOffset at, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(closure);
        await using var cmd = uow.Command(
            "INSERT INTO resources.site_closure (site_code, closed_on, note, created_at, created_by) VALUES ($1, $2, $3, $4, $5) ON CONFLICT DO NOTHING",
            closure.SiteCode, closure.Date, Text(closure.Note), at, actor);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public Task<IReadOnlyList<ShiftInfo>> ListShiftsAsync(CancellationToken ct) =>
        Read(
            "SELECT code, site_code, name, start_time, duration_minutes, days FROM resources.shift ORDER BY code",
            r => new ShiftInfo(r.GetString(0), r.GetString(1), r.GetString(2), r.GetFieldValue<TimeOnly>(3), r.GetInt32(4), r.GetFieldValue<short[]>(5).Select(d => (int)d).ToList()),
            ct);

    public Task<IReadOnlyList<WorkCenterShiftInfo>> ListWorkCenterShiftsAsync(CancellationToken ct) =>
        Read("SELECT work_center_code, shift_code FROM resources.work_center_shift ORDER BY work_center_code, shift_code", r => new WorkCenterShiftInfo(r.GetString(0), r.GetString(1)), ct);

    public Task<IReadOnlyList<SiteClosureInfo>> ListSiteClosuresAsync(CancellationToken ct) =>
        Read("SELECT site_code, closed_on, note FROM resources.site_closure ORDER BY site_code, closed_on", r => new SiteClosureInfo(r.GetString(0), r.GetFieldValue<DateOnly>(1), r.IsDBNull(2) ? null : r.GetString(2)), ct);

    public Task<IReadOnlyList<SafetyAreaInfo>> ListSafetyAreasAsync(CancellationToken ct) =>
        Read(AreaSql + " ORDER BY code", ReadArea, ct);

    public Task<IReadOnlyList<WorkCenterInfo>> ListWorkCentersAsync(CancellationToken ct) =>
        Read(WorkCenterSql + " ORDER BY code", ReadWorkCenter, ct);

    public Task<IReadOnlyList<MachineInfo>> ListMachinesAsync(CancellationToken ct) =>
        Read(MachineSql + " ORDER BY m.code", ReadMachine, ct);

    public Task<IReadOnlyList<ActivityTypeInfo>> ListActivityTypesAsync(CancellationToken ct) =>
        Read(ActivitySql + " ORDER BY code", ReadActivity, ct);

    public Task<IReadOnlyList<MachineActivityInfo>> ListMachineActivitiesAsync(CancellationToken ct) =>
        Read("SELECT machine_code, activity_code, operator_qualifications FROM resources.machine_activity ORDER BY machine_code, activity_code", ReadMachineActivity, ct);

    public async Task<SafetyAreaInfo?> FindSafetyAreaAsync(string code, CancellationToken ct) =>
        (await Read(AreaSql + " WHERE code = $1 AND active", ReadArea, ct, code)).SingleOrDefault();

    public async Task<WorkCenterInfo?> FindWorkCenterAsync(string code, CancellationToken ct) =>
        (await Read(WorkCenterSql + " WHERE code = $1 AND active", ReadWorkCenter, ct, code)).SingleOrDefault();

    public async Task<MachineInfo?> FindMachineAsync(string code, CancellationToken ct) =>
        (await Read(MachineSql + " WHERE m.code = $1 AND m.active", ReadMachine, ct, code)).SingleOrDefault();

    public async Task<bool> ActivityTypeExistsAsync(string code, CancellationToken ct) =>
        (await Read("SELECT 1 FROM resources.activity_type WHERE code = $1 AND active", _ => true, ct, code)).Count > 0;

    public async Task<MachineActivityInfo?> FindMachineActivityAsync(string machineCode, string activityCode, CancellationToken ct) =>
        (await Read(
            "SELECT machine_code, activity_code, operator_qualifications FROM resources.machine_activity WHERE machine_code = $1 AND activity_code = $2",
            ReadMachineActivity,
            ct,
            machineCode,
            activityCode)).SingleOrDefault();

    private const string MachineSql =
        "SELECT m.code, m.work_center_code, w.safety_area_code, m.name, m.active FROM resources.machine m JOIN resources.work_center w ON w.code = m.work_center_code";

    private const string AreaSql = "SELECT code, site_code, name, active FROM resources.safety_area";

    private const string WorkCenterSql = "SELECT code, safety_area_code, name, active, picture FROM resources.work_center";

    private const string ActivitySql = "SELECT code, name, description, active FROM resources.activity_type";

    private static MachineInfo ReadMachine(NpgsqlDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4));

    private static SafetyAreaInfo ReadArea(NpgsqlDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetBoolean(3));

    private static WorkCenterInfo ReadWorkCenter(NpgsqlDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), r.IsDBNull(4) ? null : r.GetString(4));

    private static ActivityTypeInfo ReadActivity(NpgsqlDataReader r) => new(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetBoolean(3));

    private static MachineActivityInfo ReadMachineActivity(NpgsqlDataReader r) => new(r.GetString(0), r.GetString(1), r.GetFieldValue<string[]>(2));

    private static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text };

    private static NpgsqlParameter TextArray(IEnumerable<string> values) =>
        new() { Value = values.ToArray(), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text };

    private async Task<IReadOnlyList<T>> Read<T>(string sql, Func<NpgsqlDataReader, T> map, CancellationToken ct, params object?[] args)
    {
        var list = new List<T>();
        await using var cmd = uow.Command(sql, args);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(map(reader));
        }

        return list;
    }
}

public static class ResourcesRegistration
{
    public static IServiceCollection AddForgeResources(this IServiceCollection services)
    {
        services.AddForgeMigrations(typeof(ResourcesRegistration).Assembly, "resources");
        services.AddForgeStore<IResourcesStore>(uow => new PostgresResourcesStore(uow));
        services.AddForgeStore<IResourcesLookup>(uow => new PostgresResourcesStore(uow));
        services.AddSingleton<ResourcesService>();
        return services;
    }
}
