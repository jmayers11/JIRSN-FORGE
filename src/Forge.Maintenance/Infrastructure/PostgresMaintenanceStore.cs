using Forge.Kernel.Postgres;
using Forge.Maintenance.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Forge.Maintenance.Infrastructure;

internal sealed class PostgresMaintenanceStore(PostgresUnitOfWork uow) : IMaintenanceStore, IMaintenanceLookup
{
    private const string Select = "SELECT downtime_id, machine_code, kind, starts_at, ends_at, reason FROM maintenance.downtime";

    public async Task<DowntimeWindow?> FindOpenOutageAsync(string machineCode, CancellationToken ct) =>
        (await ReadAsync(Select + " WHERE machine_code = $1 AND ends_at IS NULL", ct, machineCode)).SingleOrDefault();

    public async Task<long> InsertDowntimeAsync(string machineCode, DowntimeKind kind, DateTimeOffset startsAt, DateTimeOffset? endsAt, string reason, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "INSERT INTO maintenance.downtime (machine_code, kind, starts_at, ends_at, reason, recorded_at, recorded_by) VALUES ($1, $2, $3, $4, $5, $6, $7) RETURNING downtime_id",
            machineCode, kind.ToString(), startsAt,
            new NpgsqlParameter { Value = (object?)endsAt ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz },
            reason, at, actor);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task CloseOutageAsync(long id, DateTimeOffset endsAt, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE maintenance.downtime SET ends_at = GREATEST($2, starts_at + interval '1 microsecond'), closed_by = $3 WHERE downtime_id = $1",
            id, endsAt, actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task<IReadOnlyList<DowntimeWindow>> ListDowntimeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        ReadAsync(Select + " WHERE starts_at < $2 AND (ends_at IS NULL OR ends_at > $1) ORDER BY machine_code, starts_at, downtime_id", ct, from, to);

    private async Task<IReadOnlyList<DowntimeWindow>> ReadAsync(string sql, CancellationToken ct, params object?[] args)
    {
        var list = new List<DowntimeWindow>();
        await using var cmd = uow.Command(sql, args);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new DowntimeWindow(
                reader.GetInt64(0),
                reader.GetString(1),
                Enum.Parse<DowntimeKind>(reader.GetString(2)),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetString(5)));
        }

        return list;
    }
}

public static class MaintenanceRegistration
{
    public static IServiceCollection AddForgeMaintenance(this IServiceCollection services)
    {
        services.AddForgeMigrations(typeof(MaintenanceRegistration).Assembly, "maintenance");
        services.AddForgeStore<IMaintenanceStore>(uow => new PostgresMaintenanceStore(uow));
        services.AddForgeStore<IMaintenanceLookup>(uow => new PostgresMaintenanceStore(uow));
        services.AddSingleton<MaintenanceService>();
        return services;
    }
}
