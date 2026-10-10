using Forge.Execution.Contracts;
using Forge.Kernel.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Execution.Infrastructure;

/// <summary>Reason codes, stoppages and root causes (V3.5).</summary>
internal sealed class PostgresProductionActualsStore(PostgresUnitOfWork uow) : IProductionActualsLookup, IProductionActualsStore
{
    private const string StoppageSql =
        """
        SELECT stoppage_id, machine_code, work_center_code, category, reason_code, order_number, sequence, expected_minutes, note, started_at, started_by, ended_at, ended_by
        FROM execution.stoppage
        """;

    private const string RootCauseSql =
        "SELECT root_cause_id, stoppage_id, cause, evidence, proposed_at, proposed_by, confirmed_at, confirmed_by FROM execution.root_cause";

    public Task<IReadOnlyList<ReasonCodeInfo>> ListReasonCodesAsync(CancellationToken ct) =>
        Read(
            "SELECT code, kind, category, description, active, changed_at, changed_by FROM execution.reason_code ORDER BY kind, code",
            r => new ReasonCodeInfo(r.GetString(0), Enum.Parse<ReasonKind>(r.GetString(1)), r.IsDBNull(2) ? null : Enum.Parse<StoppageCategory>(r.GetString(2)),
                r.GetString(3), r.GetBoolean(4), r.GetFieldValue<DateTimeOffset>(5), r.GetString(6)),
            ct);

    public Task<IReadOnlyList<StoppageInfo>> ListStoppagesAsync(DateTimeOffset from, DateTimeOffset to, bool openOnly, CancellationToken ct) => openOnly
        ? Read(StoppageSql + " WHERE ended_at IS NULL ORDER BY started_at", ReadStoppage, ct)
        : Read(StoppageSql + " WHERE started_at < $2 AND (ended_at IS NULL OR ended_at > $1) ORDER BY started_at, stoppage_id", ReadStoppage, ct, from, to);

    public async Task<StoppageInfo?> FindStoppageAsync(long stoppageId, CancellationToken ct) =>
        (await Read(StoppageSql + " WHERE stoppage_id = $1", ReadStoppage, ct, stoppageId)).SingleOrDefault();

    public Task<IReadOnlyList<RootCauseInfo>> ListRootCausesAsync(IReadOnlyCollection<long>? stoppageIds, CancellationToken ct) => stoppageIds is null
        ? Read(RootCauseSql + " ORDER BY root_cause_id", ReadRootCause, ct)
        : Read(RootCauseSql + " WHERE stoppage_id = ANY($1) ORDER BY root_cause_id", ReadRootCause, ct, stoppageIds.ToArray());

    public async Task<bool> InsertReasonCodeAsync(ReasonCodeInfo code, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(code);
        await using var cmd = uow.Command(
            """
            INSERT INTO execution.reason_code (code, kind, category, description, active, changed_at, changed_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7) ON CONFLICT (code) DO NOTHING
            """,
            code.Code, code.Kind.ToString(), Text(code.Category?.ToString()), code.Description, code.Active, code.ChangedAt, code.ChangedBy);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task UpdateReasonCodeAsync(ReasonCodeInfo code, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(code);
        await using var cmd = uow.Command(
            "UPDATE execution.reason_code SET description = $2, active = $3, changed_at = $4, changed_by = $5 WHERE code = $1",
            code.Code, code.Description, code.Active, code.ChangedAt, code.ChangedBy);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long?> InsertStoppageAsync(StoppageInfo stoppage, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stoppage);
        await using var cmd = uow.Command(
            """
            INSERT INTO execution.stoppage (machine_code, work_center_code, category, reason_code, order_number, sequence, expected_minutes, note, started_at, started_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
            ON CONFLICT (machine_code) WHERE ended_at IS NULL DO NOTHING
            RETURNING stoppage_id
            """,
            stoppage.MachineCode, stoppage.WorkCenterCode, stoppage.Category.ToString(), stoppage.ReasonCode,
            new NpgsqlParameter { Value = (object?)stoppage.OrderNumber ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint },
            new NpgsqlParameter { Value = (object?)stoppage.Sequence ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer },
            new NpgsqlParameter { Value = (object?)stoppage.ExpectedMinutes ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Numeric },
            Text(stoppage.Note), stoppage.StartedAt, stoppage.StartedBy);
        return await cmd.ExecuteScalarAsync(ct) is long id ? id : null;
    }

    public async Task EndStoppageAsync(long stoppageId, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command("UPDATE execution.stoppage SET ended_at = $2, ended_by = $3 WHERE stoppage_id = $1 AND ended_at IS NULL", stoppageId, at, actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> InsertRootCauseAsync(RootCauseInfo cause, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cause);
        await using var cmd = uow.Command(
            "INSERT INTO execution.root_cause (stoppage_id, cause, evidence, proposed_at, proposed_by) VALUES ($1, $2, $3, $4, $5) RETURNING root_cause_id",
            cause.StoppageId, cause.Cause, cause.Evidence, cause.ProposedAt, cause.ProposedBy);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task ConfirmRootCauseAsync(long rootCauseId, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using var cmd = uow.Command("UPDATE execution.root_cause SET confirmed_at = $2, confirmed_by = $3 WHERE root_cause_id = $1 AND confirmed_at IS NULL", rootCauseId, at, actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static StoppageInfo ReadStoppage(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), Enum.Parse<StoppageCategory>(r.GetString(3)), r.GetString(4),
        r.IsDBNull(5) ? null : r.GetInt64(5), r.IsDBNull(6) ? null : r.GetInt32(6), r.IsDBNull(7) ? null : r.GetDecimal(7) / 1.0000000000000000000000000000m,
        r.IsDBNull(8) ? null : r.GetString(8), r.GetFieldValue<DateTimeOffset>(9), r.GetString(10),
        r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11), r.IsDBNull(12) ? null : r.GetString(12));

    private static RootCauseInfo ReadRootCause(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateTimeOffset>(4), r.GetString(5),
        r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6), r.IsDBNull(7) ? null : r.GetString(7));

    private static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };

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
