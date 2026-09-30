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

    public Task<IReadOnlyList<WorkCenterActivityInfo>> ListWorkCenterActivitiesAsync(CancellationToken ct) =>
        Read("SELECT work_center_code, activity_code FROM resources.work_center_activity ORDER BY work_center_code, activity_code", r => new WorkCenterActivityInfo(r.GetString(0), r.GetString(1)), ct);

    public Task<IReadOnlyList<SiteInfo>> ListSitesAsync(CancellationToken ct) =>
        Read("SELECT code, name FROM resources.site ORDER BY code", r => new SiteInfo(r.GetString(0), r.GetString(1)), ct);

    public Task<IReadOnlyList<SafetyAreaInfo>> ListSafetyAreasAsync(CancellationToken ct) =>
        Read("SELECT code, site_code, name FROM resources.safety_area ORDER BY code", r => new SafetyAreaInfo(r.GetString(0), r.GetString(1), r.GetString(2)), ct);

    public Task<IReadOnlyList<WorkCenterInfo>> ListWorkCentersAsync(CancellationToken ct) =>
        Read("SELECT code, safety_area_code, name FROM resources.work_center ORDER BY code", r => new WorkCenterInfo(r.GetString(0), r.GetString(1), r.GetString(2)), ct);

    public Task<IReadOnlyList<MachineInfo>> ListMachinesAsync(CancellationToken ct) =>
        Read(MachineSql + " ORDER BY m.code", ReadMachine, ct);

    public Task<IReadOnlyList<ActivityTypeInfo>> ListActivityTypesAsync(CancellationToken ct) =>
        Read("SELECT code, name, description FROM resources.activity_type ORDER BY code", r => new ActivityTypeInfo(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)), ct);

    public Task<IReadOnlyList<MachineActivityInfo>> ListMachineActivitiesAsync(CancellationToken ct) =>
        Read("SELECT machine_code, activity_code, operator_qualifications FROM resources.machine_activity ORDER BY machine_code, activity_code", ReadMachineActivity, ct);

    public async Task<SafetyAreaInfo?> FindSafetyAreaAsync(string code, CancellationToken ct) =>
        (await Read("SELECT code, site_code, name FROM resources.safety_area WHERE code = $1", r => new SafetyAreaInfo(r.GetString(0), r.GetString(1), r.GetString(2)), ct, code)).SingleOrDefault();

    public async Task<WorkCenterInfo?> FindWorkCenterAsync(string code, CancellationToken ct) =>
        (await Read("SELECT code, safety_area_code, name FROM resources.work_center WHERE code = $1", r => new WorkCenterInfo(r.GetString(0), r.GetString(1), r.GetString(2)), ct, code)).SingleOrDefault();

    public async Task<MachineInfo?> FindMachineAsync(string code, CancellationToken ct) =>
        (await Read(MachineSql + " WHERE m.code = $1", ReadMachine, ct, code)).SingleOrDefault();

    public async Task<bool> ActivityTypeExistsAsync(string code, CancellationToken ct) =>
        (await Read("SELECT 1 FROM resources.activity_type WHERE code = $1", _ => true, ct, code)).Count > 0;

    public async Task<MachineActivityInfo?> FindMachineActivityAsync(string machineCode, string activityCode, CancellationToken ct) =>
        (await Read(
            "SELECT machine_code, activity_code, operator_qualifications FROM resources.machine_activity WHERE machine_code = $1 AND activity_code = $2",
            ReadMachineActivity,
            ct,
            machineCode,
            activityCode)).SingleOrDefault();

    private const string MachineSql =
        "SELECT m.code, m.work_center_code, w.safety_area_code, m.name FROM resources.machine m JOIN resources.work_center w ON w.code = m.work_center_code";

    private static MachineInfo ReadMachine(NpgsqlDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3));

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
