using System.Text.Json;
using Forge.Kernel.Postgres;
using Forge.Kernel.Serialization;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Planning.Infrastructure;

internal sealed class PostgresForecastStore(PostgresUnitOfWork uow) : IForecastStore
{
    public async Task<IReadOnlyList<DemandHistoryRow>> ListHistoryAsync(bool includeSimulated, CancellationToken ct)
    {
        var list = new List<DemandHistoryRow>();
        await using var cmd = uow.Command(
            "SELECT part_number, month, quantity, source, recorded_by, recorded_at FROM planning.demand_history WHERE ($1 OR source <> 'Simulated') ORDER BY part_number, month", includeSimulated);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new DemandHistoryRow(r.GetString(0), r.GetFieldValue<DateOnly>(1), r.GetDecimal(2), Enum.Parse<DemandHistorySource>(r.GetString(3)), r.GetString(4), r.GetFieldValue<DateTimeOffset>(5)));
        return list;
    }

    public async Task UpsertHistoryAsync(IReadOnlyList<DemandHistoryRow> rows, CancellationToken ct)
    {
        foreach (var chunk in rows.Chunk(500))
        {
            await using var cmd = uow.Command(
                """
                INSERT INTO planning.demand_history (part_number, month, quantity, source, recorded_by, recorded_at)
                SELECT * FROM unnest($1::text[], $2::date[], $3::numeric[], $4::text[], $5::text[], $6::timestamptz[])
                ON CONFLICT (part_number, month, source) DO UPDATE SET quantity = EXCLUDED.quantity, recorded_by = EXCLUDED.recorded_by, recorded_at = EXCLUDED.recorded_at
                """,
                Arr(chunk.Select(r => r.PartNumber).ToArray(), NpgsqlDbType.Text), Arr(chunk.Select(r => r.Month).ToArray(), NpgsqlDbType.Date),
                Arr(chunk.Select(r => r.Quantity).ToArray(), NpgsqlDbType.Numeric), Arr(chunk.Select(r => r.Source.ToString()).ToArray(), NpgsqlDbType.Text),
                Arr(chunk.Select(r => r.RecordedBy).ToArray(), NpgsqlDbType.Text), Arr(chunk.Select(r => r.RecordedAt).ToArray(), NpgsqlDbType.TimestampTz));
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<int> DeleteHistoryAsync(DemandHistorySource source, CancellationToken ct)
    {
        await using var cmd = uow.Command("DELETE FROM planning.demand_history WHERE source = $1", source.ToString());
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<ForecastPolicy> ForecastPolicyAsync(CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT horizon, holdout, interval_percent, use_simulated, set_by, set_at FROM planning.forecast_policy ORDER BY policy_id DESC LIMIT 1");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new ForecastPolicy(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetBoolean(3), r.GetString(4), r.GetFieldValue<DateTimeOffset>(5)) : ForecastPolicy.Default;
    }

    public async Task InsertForecastPolicyAsync(ForecastPolicy p, CancellationToken ct)
    {
        await using var cmd = uow.Command("INSERT INTO planning.forecast_policy (horizon, holdout, interval_percent, use_simulated, set_by, set_at) VALUES ($1, $2, $3, $4, $5, $6)",
            p.Horizon, p.Holdout, p.IntervalPercent, p.UseSimulated, p.SetBy, p.SetAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> InsertRunAsync(ForecastRun run, IReadOnlyList<ForecastLine> lines, CancellationToken ct)
    {
        long id;
        await using (var cmd = uow.Command(
            "INSERT INTO planning.forecast_run (run_at, run_by, simulated, horizon, holdout, interval_percent, first_month) VALUES ($1, $2, $3, $4, $5, $6, $7) RETURNING run_id",
            run.RunAt, run.RunBy, run.Simulated, run.Horizon, run.Holdout, run.IntervalPercent, run.FirstMonth))
        {
            id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }

        foreach (var line in lines)
        {
            await using var cmd = uow.Command("INSERT INTO planning.forecast_line (run_id, part_number, simulated, forecast) VALUES ($1, $2, $3, $4::jsonb)",
                id, line.Forecast.PartNumber, line.Simulated, JsonSerializer.Serialize(line.Forecast, CanonicalJson.Options));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        return id;
    }

    public async Task<ForecastRun?> LatestRunAsync(CancellationToken ct) => (await RunsAsync("ORDER BY run_id DESC LIMIT 1", ct)).SingleOrDefault();

    public async Task<ForecastRun?> FindRunAsync(long runId, CancellationToken ct) => (await RunsAsync("WHERE run_id = $1", ct, runId)).SingleOrDefault();

    public async Task<IReadOnlyList<ForecastLine>> ListLinesAsync(long runId, CancellationToken ct)
    {
        var list = new List<ForecastLine>();
        await using var cmd = uow.Command("SELECT forecast::text, simulated, approved_by, approved_at FROM planning.forecast_line WHERE run_id = $1 ORDER BY part_number", runId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new ForecastLine(runId, JsonSerializer.Deserialize<PartForecast>(r.GetString(0), CanonicalJson.Options)!, r.GetBoolean(1), r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetFieldValue<DateTimeOffset>(3)));
        return list;
    }

    public async Task MarkApprovedAsync(long runId, string partNumber, string actor, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = uow.Command("UPDATE planning.forecast_line SET approved_by = $3, approved_at = $4 WHERE run_id = $1 AND part_number = $2", runId, partNumber, actor, at);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<IReadOnlyList<ForecastRun>> RunsAsync(string tail, CancellationToken ct, params object?[] args)
    {
        var list = new List<ForecastRun>();
        await using var cmd = uow.Command("SELECT run_id, run_at, run_by, simulated, horizon, holdout, interval_percent, first_month FROM planning.forecast_run " + tail, args);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new ForecastRun(r.GetInt64(0), r.GetFieldValue<DateTimeOffset>(1), r.GetString(2), r.GetBoolean(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetFieldValue<DateOnly>(7)));
        return list;
    }

    private static NpgsqlParameter Arr<T>(T[] values, NpgsqlDbType type) => new() { Value = values, NpgsqlDbType = NpgsqlDbType.Array | type };
}
