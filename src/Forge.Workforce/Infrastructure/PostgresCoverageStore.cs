using Forge.Kernel.Postgres;
using Forge.Workforce.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Workforce.Infrastructure;

/// <summary>Coverage assignments and their goal versions (Sprint 10 §22). Goal rows are protected from change by a trigger.</summary>
internal sealed class PostgresCoverageStore(PostgresUnitOfWork uow) : ICoverageStore, ICoverageLookup
{
    private const string Columns = "assignment_id, person_id, origin_work_center, destination_work_center, machine_code, activity_code, order_number, sequence, planned_start, planned_end, actual_start, actual_end, reason, forecast_conservative, forecast_expected, forecast_optimistic, forecast_confidence, forecast_basis, state, comparable, exclusion_reason, reviewed_by, reviewed_at, approved_by, approved_at";

    public async Task InsertCoverageAsync(CoverageAssignment a, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(a);
        await using var cmd = uow.Command(
            $"INSERT INTO workforce.coverage_assignment ({Columns}) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20, $21, $22, $23, $24, $25)",
            a.AssignmentId, a.PersonId, Text(a.OriginWorkCenter), a.DestinationWorkCenter, Text(a.MachineCode), a.ActivityCode, Long(a.OrderNumber), Int(a.Sequence),
            a.PlannedStart, a.PlannedEnd, Time(a.ActualStart), Time(a.ActualEnd), a.Reason, Num(a.ForecastConservative), Num(a.ForecastExpected), Num(a.ForecastOptimistic),
            Text(a.ForecastConfidence), Text(a.ForecastBasis), a.State.ToString(), a.Comparable, Text(a.ExclusionReason), Text(a.ReviewedBy), Time(a.ReviewedAt),
            a.ApprovedBy, a.ApprovedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> UpdateCoverageAsync(CoverageAssignment a, CoverageState expected, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(a);
        await using var cmd = uow.Command(
            """
            UPDATE workforce.coverage_assignment SET state = $3, actual_start = $4, actual_end = $5, comparable = $6, exclusion_reason = $7, reviewed_by = $8, reviewed_at = $9
            WHERE assignment_id = $1 AND state = $2
            """,
            a.AssignmentId, expected.ToString(), a.State.ToString(), Time(a.ActualStart), Time(a.ActualEnd), a.Comparable, Text(a.ExclusionReason), Text(a.ReviewedBy), Time(a.ReviewedAt));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task InsertCoverageGoalAsync(CoverageGoal g, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(g);
        await using var cmd = uow.Command(
            """
            INSERT INTO workforce.coverage_goal (assignment_id, version, kind, good_units, planned_productive_minutes, rationale, after_start, set_by, set_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
            """,
            g.AssignmentId, g.Version, g.Kind.ToString(), g.GoodUnits, g.PlannedProductiveMinutes, g.Rationale, g.AfterStart, g.SetBy, g.SetAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<CoverageAssignment>> ListCoverageAsync(Guid? personId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var rows = new List<CoverageAssignment>();
        await using var cmd = uow.Command(
            $"SELECT {Columns} FROM workforce.coverage_assignment WHERE ($1::uuid IS NULL OR person_id = $1) AND planned_start < $3 AND COALESCE(actual_end, planned_end) > $2 ORDER BY planned_start, assignment_id",
            new NpgsqlParameter { Value = (object?)personId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid }, from, to);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(Read(reader));
        return rows;
    }

    public async Task<CoverageAssignment?> FindCoverageAsync(Guid assignmentId, CancellationToken ct)
    {
        await using var cmd = uow.Command($"SELECT {Columns} FROM workforce.coverage_assignment WHERE assignment_id = $1", assignmentId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<CoverageGoal>> ListCoverageGoalsAsync(IReadOnlyCollection<Guid> assignmentIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(assignmentIds);
        var rows = new List<CoverageGoal>();
        if (assignmentIds.Count == 0) return rows;
        await using var cmd = uow.Command(
            "SELECT goal_id, assignment_id, version, kind, good_units, planned_productive_minutes, rationale, after_start, set_by, set_at FROM workforce.coverage_goal WHERE assignment_id = ANY($1) ORDER BY assignment_id, version",
            assignmentIds.ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new CoverageGoal(reader.GetInt64(0), reader.GetGuid(1), reader.GetInt32(2), Enum.Parse<CoverageGoalKind>(reader.GetString(3)),
                reader.GetDecimal(4) / 1.0000000000000000000000000000m, reader.GetInt32(5), reader.GetString(6), reader.GetBoolean(7), reader.GetString(8),
                reader.GetFieldValue<DateTimeOffset>(9)));
        }

        return rows;
    }

    private static CoverageAssignment Read(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), NText(r, 2), r.GetString(3), NText(r, 4), r.GetString(5),
        r.IsDBNull(6) ? null : r.GetInt64(6), r.IsDBNull(7) ? null : r.GetInt32(7),
        r.GetFieldValue<DateTimeOffset>(8), r.GetFieldValue<DateTimeOffset>(9), NTime(r, 10), NTime(r, 11), r.GetString(12),
        NNum(r, 13), NNum(r, 14), NNum(r, 15), NText(r, 16), NText(r, 17), Enum.Parse<CoverageState>(r.GetString(18)), r.GetBoolean(19), NText(r, 20),
        NText(r, 21), NTime(r, 22), r.GetString(23), r.GetFieldValue<DateTimeOffset>(24));

    private static string? NText(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static DateTimeOffset? NTime(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);

    private static decimal? NNum(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDecimal(i) / 1.0000000000000000000000000000m;

    private static NpgsqlParameter Text(string? v) => new() { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };

    private static NpgsqlParameter Long(long? v) => new() { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint };

    private static NpgsqlParameter Int(int? v) => new() { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer };

    private static NpgsqlParameter Time(DateTimeOffset? v) => new() { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz };

    private static NpgsqlParameter Num(decimal? v) => new() { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Numeric };
}
