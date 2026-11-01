using System.Text.Json;
using Forge.Connectivity.Contracts;
using Forge.Kernel.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Connectivity.Infrastructure;

internal sealed class PostgresConnectivityStore(PostgresUnitOfWork uow) : IConnectivityStore, IConnectivityLookup
{
    private const string ConnectionSql = "SELECT machine_code, kind, settings::text, poll_seconds, stop_after_seconds, active, updated_by, updated_at FROM connectivity.machine_connection";

    private const string StatusSql = "SELECT machine_code, state, since, last_seen, part_count, scrap_count, alarm, last_error, stop_reported FROM connectivity.machine_status";

    private static NpgsqlParameter Text(string? v) => new() { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };

    private static NpgsqlParameter Long(long? v) => new() { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint };

    private static NpgsqlParameter At(DateTimeOffset? v) => new() { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz };

    public async Task UpsertConnectionAsync(MachineConnection c, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            """
            INSERT INTO connectivity.machine_connection (machine_code, kind, settings, poll_seconds, stop_after_seconds, active, updated_by, updated_at)
            VALUES ($1, $2, $3::jsonb, $4, $5, $6, $7, $8)
            ON CONFLICT (machine_code) DO UPDATE SET kind = EXCLUDED.kind, settings = EXCLUDED.settings, poll_seconds = EXCLUDED.poll_seconds,
                stop_after_seconds = EXCLUDED.stop_after_seconds, active = EXCLUDED.active, updated_by = EXCLUDED.updated_by, updated_at = EXCLUDED.updated_at
            """,
            c.MachineCode, c.Kind.ToString(), JsonSerializer.Serialize(c.Settings), c.PollSeconds, c.StopAfterSeconds, c.Active, c.UpdatedBy, c.UpdatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<MachineStatus?> LockStatusAsync(string machineCode, CancellationToken ct) =>
        (await ReadAsync(StatusSql + " WHERE machine_code = $1 FOR UPDATE", ReadStatus, ct, machineCode)).SingleOrDefault();

    public async Task SaveStatusAsync(MachineStatus s, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            """
            INSERT INTO connectivity.machine_status (machine_code, state, since, last_seen, part_count, scrap_count, alarm, last_error, stop_reported)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
            ON CONFLICT (machine_code) DO UPDATE SET state = EXCLUDED.state, since = EXCLUDED.since, last_seen = EXCLUDED.last_seen, part_count = EXCLUDED.part_count,
                scrap_count = EXCLUDED.scrap_count, alarm = EXCLUDED.alarm, last_error = EXCLUDED.last_error, stop_reported = EXCLUDED.stop_reported
            """,
            s.MachineCode, s.State.ToString(), s.Since, At(s.LastSeen), Long(s.PartCount), Long(s.ScrapCount), Text(s.Alarm), Text(s.LastError), s.StopReported);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertReadingsAsync(string machineCode, IReadOnlyList<MachineReading> readings, CancellationToken ct)
    {
        foreach (var r in readings)
        {
            await using var cmd = uow.Command(
                "INSERT INTO connectivity.machine_reading (machine_code, at, state, part_count, scrap_count, alarm) VALUES ($1, $2, $3, $4, $5, $6)",
                machineCode, r.At, r.State.ToString(), Long(r.PartCount), Long(r.ScrapCount), Text(r.Alarm));
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public Task<IReadOnlyList<MachineConnection>> ListConnectionsAsync(CancellationToken ct) => ReadAsync(ConnectionSql + " ORDER BY machine_code", ReadConnection, ct);

    public async Task<MachineConnection?> FindConnectionAsync(string machineCode, CancellationToken ct) =>
        (await ReadAsync(ConnectionSql + " WHERE machine_code = $1", ReadConnection, ct, machineCode)).SingleOrDefault();

    public Task<IReadOnlyList<MachineStatus>> ListStatusAsync(CancellationToken ct) => ReadAsync(StatusSql + " ORDER BY machine_code", ReadStatus, ct);

    public Task<IReadOnlyList<(string MachineCode, MachineReading Reading)>> ListReadingsAsync(string? machineCode, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        ReadAsync(
            "SELECT machine_code, at, state, part_count, scrap_count, alarm FROM connectivity.machine_reading WHERE ($1::text IS NULL OR machine_code = $1) AND at >= $2 AND at < $3 ORDER BY machine_code, at, reading_id",
            r => (r.GetString(0), new MachineReading(r.GetFieldValue<DateTimeOffset>(1), Enum.Parse<MachineState>(r.GetString(2)), r.IsDBNull(3) ? null : r.GetInt64(3),
                r.IsDBNull(4) ? null : r.GetInt64(4), r.IsDBNull(5) ? null : r.GetString(5))),
            ct, Text(machineCode), from, to);

    private static MachineConnection ReadConnection(NpgsqlDataReader r) => new(
        r.GetString(0), Enum.Parse<ConnectorKind>(r.GetString(1)), JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(2)) ?? [], r.GetInt32(3), r.GetInt32(4),
        r.GetBoolean(5), r.GetString(6), r.GetFieldValue<DateTimeOffset>(7));

    private static MachineStatus ReadStatus(NpgsqlDataReader r) => new(
        r.GetString(0), Enum.Parse<MachineState>(r.GetString(1)), r.GetFieldValue<DateTimeOffset>(2), r.IsDBNull(3) ? null : r.GetFieldValue<DateTimeOffset>(3),
        r.IsDBNull(4) ? null : r.GetInt64(4), r.IsDBNull(5) ? null : r.GetInt64(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetBoolean(8));

    private async Task<IReadOnlyList<T>> ReadAsync<T>(string sql, Func<NpgsqlDataReader, T> map, CancellationToken ct, params object?[] args)
    {
        var list = new List<T>();
        await using var cmd = uow.Command(sql, args);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(map(reader));
        return list;
    }
}

public static class ConnectivityRegistration
{
    public static IServiceCollection AddForgeConnectivity(this IServiceCollection services)
    {
        services.AddForgeMigrations(typeof(ConnectivityRegistration).Assembly, "connectivity");
        services.AddForgeStore<IConnectivityStore>(uow => new PostgresConnectivityStore(uow));
        services.AddForgeStore<IConnectivityLookup>(uow => new PostgresConnectivityStore(uow));
        services.AddSingleton<ConnectivityService>();
        return services;
    }
}
