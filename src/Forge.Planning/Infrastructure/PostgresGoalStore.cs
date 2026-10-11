using Forge.Kernel.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Planning.Infrastructure;

/// <summary>Goals and their forecast history (V3.7).</summary>
internal sealed class PostgresGoalStore(PostgresUnitOfWork uow) : IGoalStore
{
    private const string GoalSql =
        """
        SELECT goal_id, kind, title, parent_goal_id, order_number, demand_id, required_by, lead_days, program_id, status, created_at, created_by, changed_at, changed_by
        FROM planning.goal
        """;

    public async Task<long> InsertAsync(GoalInfo goal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(goal);
        await using var cmd = uow.Command(
            """
            INSERT INTO planning.goal (kind, title, parent_goal_id, order_number, demand_id, required_by, lead_days, program_id, status, created_at, created_by, changed_at, changed_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13) RETURNING goal_id
            """,
            goal.Kind.ToString(), goal.Title, Long(goal.ParentGoalId), Long(goal.OrderNumber), Long(goal.DemandId), Date(goal.RequiredBy), Int(goal.LeadDays), Uuid(goal.ProgramId),
            goal.Status.ToString(), goal.CreatedAt, goal.CreatedBy, goal.ChangedAt, goal.ChangedBy);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task UpdateAsync(GoalInfo goal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(goal);
        await using var cmd = uow.Command(
            "UPDATE planning.goal SET title = $2, required_by = $3, lead_days = $4, status = $5, changed_at = $6, changed_by = $7 WHERE goal_id = $1",
            goal.GoalId, goal.Title, Date(goal.RequiredBy), Int(goal.LeadDays), goal.Status.ToString(), goal.ChangedAt, goal.ChangedBy);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<GoalInfo>> ListAsync(bool includeClosed, CancellationToken ct)
    {
        var list = new List<GoalInfo>();
        await using var cmd = uow.Command(GoalSql + " WHERE $1 OR status = 'Open' ORDER BY goal_id", includeClosed);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new GoalInfo(
                r.GetInt64(0), Enum.Parse<GoalKind>(r.GetString(1)), r.GetString(2), r.IsDBNull(3) ? null : r.GetInt64(3), r.IsDBNull(4) ? null : r.GetInt64(4), r.IsDBNull(5) ? null : r.GetInt64(5),
                r.IsDBNull(6) ? null : r.GetFieldValue<DateOnly>(6), r.IsDBNull(7) ? null : r.GetInt32(7), r.IsDBNull(8) ? null : r.GetGuid(8), Enum.Parse<GoalStatus>(r.GetString(9)),
                r.GetFieldValue<DateTimeOffset>(10), r.GetString(11), r.GetFieldValue<DateTimeOffset>(12), r.GetString(13)));
        }

        return list;
    }

    public async Task InsertForecastAsync(GoalForecast forecast, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(forecast);
        await using var cmd = uow.Command(
            """
            INSERT INTO planning.goal_forecast (goal_id, at, required_by, projected, state, behind_plan, attainment, recorded_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            """,
            forecast.GoalId, forecast.At, forecast.RequiredBy, new NpgsqlParameter { Value = (object?)forecast.Projected ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz },
            forecast.State.ToString(), forecast.BehindPlan, forecast.Attainment, forecast.RecordedBy);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<GoalForecast>> ListForecastsAsync(long? goalId, CancellationToken ct)
    {
        var list = new List<GoalForecast>();
        await using var cmd = uow.Command(
            "SELECT forecast_id, goal_id, at, required_by, projected, state, behind_plan, attainment, recorded_by FROM planning.goal_forecast WHERE $1::bigint IS NULL OR goal_id = $1 ORDER BY forecast_id",
            Long(goalId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new GoalForecast(r.GetInt64(0), r.GetInt64(1), r.GetFieldValue<DateTimeOffset>(2), r.GetFieldValue<DateOnly>(3), r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4),
                Enum.Parse<GoalState>(r.GetString(5)), r.GetBoolean(6), r.GetDecimal(7) / 1.0000000000000000000000000000m, r.GetString(8)));
        }

        return list;
    }

    private static NpgsqlParameter Long(long? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint };

    private static NpgsqlParameter Int(int? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer };

    private static NpgsqlParameter Date(DateOnly? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date };

    private static NpgsqlParameter Uuid(Guid? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid };
}
