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

    // Recorded downtime, plus a derived lock-out for every lock-out PM work order overdue past its grace
    // days: from the end of the last grace day (site time) until the work is completed. Its id is the
    // negative work order number, so it is never confused with a recorded window.
    private const string DowntimeSql =
        """
        SELECT * FROM (
            SELECT downtime_id, machine_code, kind, starts_at, ends_at, reason FROM maintenance.downtime
            UNION ALL
            SELECT -w.work_order_id, w.machine_code, 'MaintenanceOverdue',
                   ((w.due_date + p.grace_days + 1)::timestamp AT TIME ZONE COALESCE((SELECT time_zone FROM resources.site ORDER BY code LIMIT 1), 'UTC')),
                   NULL::timestamptz,
                   'Preventive maintenance ' || w.work_order_id || ' (' || w.title || ') was due ' || to_char(w.due_date, 'YYYY-MM-DD')
                       || CASE WHEN p.grace_days > 0 THEN ' with ' || p.grace_days || ' day(s) grace' ELSE '' END
                       || '; the machine is locked out until it is done'
            FROM maintenance.work_order w JOIN maintenance.pm_plan p ON p.plan_id = w.plan_id
            WHERE p.lock_out_when_overdue AND w.status IN ('Open', 'Scheduled', 'InProgress')
            UNION ALL
            -- A restricted or decommissioned production asset takes its machine out of capacity from that
            -- change until it is returned to service (V2.8). Ids below -1e12 never meet a work order's.
            SELECT -1000000000000 - o.event_id, a.machine_code, 'AssetOutOfService', o.at, o.until,
                   'Asset ' || a.asset_code || ' is ' || lower(o.status) || COALESCE(': ' || o.reason, '')
            FROM (
                SELECT e.event_id, e.asset_code, e.status, e.at, e.reason,
                       (SELECT min(n.at) FROM maintenance.asset_event n
                        WHERE n.asset_code = e.asset_code AND n.event_id > e.event_id AND n.status NOT IN ('Restricted', 'Decommissioned')) AS until
                FROM maintenance.asset_event e
                WHERE e.status IN ('Restricted', 'Decommissioned') AND e.from_status NOT IN ('Restricted', 'Decommissioned')
            ) o JOIN maintenance.asset a ON a.asset_code = o.asset_code
            WHERE a.machine_code IS NOT NULL
        ) d
        WHERE starts_at < $2 AND (ends_at IS NULL OR ends_at > $1)
        ORDER BY machine_code, starts_at, downtime_id
        """;

    private const string PlanSql = "SELECT plan_id, machine_code, title, interval_days, duration_minutes, tasks, lock_out_when_overdue, grace_days, active, interval_run_hours, grace_run_hours FROM maintenance.pm_plan";

    private const string WorkOrderSql =
        """
        SELECT work_order_id, machine_code, kind, plan_id, title, description, tasks, due_date, status, scheduled_start, scheduled_end, downtime_id,
               created_by, created_at, started_by, started_at, completed_by, completed_at, findings, closed_by, closed_at, cancel_reason, due_run_hours, run_hours_from,
               asset_code, procedure_document, procedure_revision, required_qualifications
        FROM maintenance.work_order
        """;

    public async Task<IReadOnlyList<DowntimeWindow>> ListDowntimeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var windows = (await ReadAsync(DowntimeSql, ct, from, to)).ToList();

        // Lock-out PM due by run hours: locked out from the instant the machine reached the due hours
        // (plus grace). Hours come from shop-floor labor; once locked out the machine stops accruing them.
        var due = await ReadRowsAsync(
            """
            SELECT w.work_order_id, w.machine_code, w.title, w.due_run_hours + p.grace_run_hours, w.run_hours_from
            FROM maintenance.work_order w JOIN maintenance.pm_plan p ON p.plan_id = w.plan_id
            WHERE p.lock_out_when_overdue AND w.due_run_hours IS NOT NULL AND w.status IN ('Open', 'Scheduled', 'InProgress')
            """,
            r => (Id: r.GetInt64(0), Machine: r.GetString(1), Title: r.GetString(2), Hours: Dec(r, 3), From: r.GetFieldValue<DateTimeOffset>(4)),
            ct);
        if (due.Count == 0)
        {
            return windows;
        }

        var usage = uow.Store<IMachineUsageLookup>();
        // "Now" is the transaction's clock, so every reader in one transaction sees the same instant.
        DateTimeOffset now;
        await using (var clock = uow.Command("SELECT now()"))
        {
            var value = await clock.ExecuteScalarAsync(ct);
            now = value is DateTimeOffset at ? at : new DateTimeOffset(DateTime.SpecifyKind((DateTime)value!, DateTimeKind.Utc));
        }

        var asOf = now < to ? now : to;
        foreach (var d in due)
        {
            if (windows.Any(w => w.Id == -d.Id))
            {
                continue; // already locked out by its calendar due date
            }

            if (await usage.ReachedAsync(d.Machine, d.From, d.Hours, asOf, ct) is { } reached && reached < to)
            {
                windows.Add(new DowntimeWindow(-d.Id, d.Machine, DowntimeKind.MaintenanceOverdue, reached, null,
                    $"Preventive maintenance {d.Id} ({d.Title}) reached its run-hour limit; the machine is locked out until it is done"));
            }
        }

        return windows.OrderBy(w => w.MachineCode, StringComparer.Ordinal).ThenBy(w => w.StartsAt).ThenBy(w => w.Id).ToList();
    }

    public async Task<long> InsertPlanAsync(PmPlanInfo plan, DateTimeOffset at, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await using var cmd = uow.Command(
            """
            INSERT INTO maintenance.pm_plan (machine_code, title, interval_days, duration_minutes, tasks, lock_out_when_overdue, grace_days, active, created_at, created_by, interval_run_hours, grace_run_hours)
            VALUES ($1, $2, $3, $4, $5, $6, $7, true, $8, $9, $10, $11) RETURNING plan_id
            """,
            plan.MachineCode, plan.Title, Int(plan.IntervalDays), plan.DurationMinutes, plan.Tasks.ToArray(), plan.LockOutWhenOverdue, plan.GraceDays, at, actor, Num(plan.IntervalRunHours), plan.GraceRunHours);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task UpdatePlanAsync(PmPlanInfo plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await using var cmd = uow.Command(
            "UPDATE maintenance.pm_plan SET title = $2, interval_days = $3, duration_minutes = $4, tasks = $5, lock_out_when_overdue = $6, grace_days = $7, active = $8, interval_run_hours = $9, grace_run_hours = $10 WHERE plan_id = $1",
            plan.PlanId, plan.Title, Int(plan.IntervalDays), plan.DurationMinutes, plan.Tasks.ToArray(), plan.LockOutWhenOverdue, plan.GraceDays, plan.Active, Num(plan.IntervalRunHours), plan.GraceRunHours);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> InsertWorkOrderAsync(WorkOrderInfo order, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(order);
        await using var cmd = uow.Command(
            """
            INSERT INTO maintenance.work_order (machine_code, kind, plan_id, title, description, tasks, due_date, status, downtime_id, created_at, created_by, due_run_hours, run_hours_from,
                asset_code, procedure_document, procedure_revision, required_qualifications)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17) RETURNING work_order_id
            """,
            Text(order.MachineCode), order.Kind.ToString(), Long(order.PlanId), order.Title, Text(order.Description), order.Tasks.ToArray(), Date(order.DueDate), order.Status.ToString(), Long(order.DowntimeId), order.CreatedAt, order.CreatedBy,
            Num(order.DueRunHours), Time(order.RunHoursFrom), Text(order.AssetCode), Text(order.ProcedureDocument), Text(order.ProcedureRevision), order.Qualifications.ToArray());
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task UpdateWorkOrderAsync(WorkOrderInfo order, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(order);
        await using var cmd = uow.Command(
            """
            UPDATE maintenance.work_order SET status = $2, scheduled_start = $3, scheduled_end = $4, downtime_id = $5, started_by = $6, started_at = $7,
                completed_by = $8, completed_at = $9, findings = $10, closed_by = $11, closed_at = $12, cancel_reason = $13, due_date = $14, title = $15, tasks = $16,
                asset_code = $17, procedure_document = $18, procedure_revision = $19, required_qualifications = $20
            WHERE work_order_id = $1
            """,
            order.WorkOrderId, order.Status.ToString(), Time(order.ScheduledStart), Time(order.ScheduledEnd), Long(order.DowntimeId), Text(order.StartedBy), Time(order.StartedAt),
            Text(order.CompletedBy), Time(order.CompletedAt), Text(order.Findings), Text(order.ClosedBy), Time(order.ClosedAt), Text(order.CancelReason), Date(order.DueDate), order.Title, order.Tasks.ToArray(),
            Text(order.AssetCode), Text(order.ProcedureDocument), Text(order.ProcedureRevision), order.Qualifications.ToArray());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ReplaceRequiredPartsAsync(long workOrderId, IReadOnlyList<WorkOrderPartRequirement> parts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(parts);
        await using (var delete = uow.Command("DELETE FROM maintenance.work_order_part WHERE work_order_id = $1", workOrderId))
        {
            await delete.ExecuteNonQueryAsync(ct);
        }

        foreach (var part in parts)
        {
            await using var insert = uow.Command(
                "INSERT INTO maintenance.work_order_part (work_order_id, line_number, part_number, quantity, position) VALUES ($1, $2, $3, $4, $5)",
                workOrderId, part.LineNumber, part.PartNumber, part.Quantity, Text(part.Position));
            await insert.ExecuteNonQueryAsync(ct);
        }
    }

    public Task<IReadOnlyList<WorkOrderPartRequirement>> ListRequiredPartsAsync(long? workOrderId, CancellationToken ct) =>
        ReadRowsAsync(
            """
            SELECT p.work_order_id, p.line_number, p.part_number, p.quantity, p.position FROM maintenance.work_order_part p
            JOIN maintenance.work_order w ON w.work_order_id = p.work_order_id
            WHERE ($1::bigint IS NULL AND w.status IN ('Open', 'Scheduled', 'InProgress', 'Completed')) OR p.work_order_id = $1
            ORDER BY p.work_order_id, p.line_number
            """,
            r => new WorkOrderPartRequirement(r.GetInt64(0), r.GetInt32(1), r.GetString(2), Dec(r, 3), NText(r, 4)),
            ct,
            Long(workOrderId));

    private const string OutageSql =
        "SELECT outage_id, machine_code, starts_at, ends_at, reason, work_order_id, status, proposed_by, proposed_at, decided_by, decided_at, decision_reason, impact, downtime_id FROM maintenance.outage";

    public Task<IReadOnlyList<OutageInfo>> ListOutagesAsync(bool includeDecided, CancellationToken ct) =>
        ReadRowsAsync(
            OutageSql + (includeDecided ? string.Empty : " WHERE status = 'Proposed'") + " ORDER BY CASE WHEN status = 'Proposed' THEN 0 ELSE 1 END, starts_at DESC, outage_id DESC LIMIT 500",
            ReadOutage,
            ct);

    public async Task<OutageInfo?> FindOutageAsync(long outageId, CancellationToken ct) =>
        (await ReadRowsAsync(OutageSql + " WHERE outage_id = $1", ReadOutage, ct, outageId)).SingleOrDefault();

    public async Task<long> InsertOutageAsync(OutageInfo outage, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(outage);
        await using var cmd = uow.Command(
            "INSERT INTO maintenance.outage (machine_code, starts_at, ends_at, reason, work_order_id, status, proposed_by, proposed_at) VALUES ($1, $2, $3, $4, $5, 'Proposed', $6, $7) RETURNING outage_id",
            outage.MachineCode, outage.StartsAt, outage.EndsAt, outage.Reason, Long(outage.WorkOrderId), outage.ProposedBy, outage.ProposedAt);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task DecideOutageAsync(OutageInfo outage, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(outage);
        await using var cmd = uow.Command(
            "UPDATE maintenance.outage SET status = $2, decided_by = $3, decided_at = $4, decision_reason = $5, impact = $6, downtime_id = $7 WHERE outage_id = $1",
            outage.OutageId, outage.Status.ToString(), Text(outage.DecidedBy), Time(outage.DecidedAt), Text(outage.DecisionReason), Text(outage.Impact), Long(outage.DowntimeId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static OutageInfo ReadOutage(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2), r.GetFieldValue<DateTimeOffset>(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetInt64(5),
        Enum.Parse<OutageStatus>(r.GetString(6)), r.GetString(7), r.GetFieldValue<DateTimeOffset>(8), NText(r, 9), NTime(r, 10), NText(r, 11), NText(r, 12), r.IsDBNull(13) ? null : r.GetInt64(13));

    public async Task SetPlannedWindowAsync(long downtimeId, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        await using var cmd = uow.Command("UPDATE maintenance.downtime SET starts_at = $2, ends_at = $3 WHERE downtime_id = $1 AND kind = 'Planned'", downtimeId, start, end);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RemovePlannedWindowAsync(long downtimeId, CancellationToken ct)
    {
        await using var cmd = uow.Command("DELETE FROM maintenance.downtime WHERE downtime_id = $1 AND kind = 'Planned'", downtimeId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task<IReadOnlyList<PmPlanInfo>> ListPlansAsync(CancellationToken ct) => ReadRowsAsync(PlanSql + " ORDER BY machine_code, plan_id", ReadPlan, ct);

    public async Task<PmPlanInfo?> FindPlanAsync(long planId, CancellationToken ct) => (await ReadRowsAsync(PlanSql + " WHERE plan_id = $1", ReadPlan, ct, planId)).SingleOrDefault();

    public Task<IReadOnlyList<WorkOrderInfo>> ListWorkOrdersAsync(bool includeClosed, CancellationToken ct) =>
        ReadRowsAsync(
            WorkOrderSql + (includeClosed ? string.Empty : " WHERE status IN ('Open', 'Scheduled', 'InProgress', 'Completed')")
                + " ORDER BY CASE WHEN status IN ('Open', 'Scheduled', 'InProgress', 'Completed') THEN 0 ELSE 1 END, due_date, work_order_id LIMIT 500",
            ReadWorkOrder,
            ct);

    public async Task<WorkOrderInfo?> FindWorkOrderAsync(long workOrderId, CancellationToken ct) =>
        (await ReadRowsAsync(WorkOrderSql + " WHERE work_order_id = $1", ReadWorkOrder, ct, workOrderId)).SingleOrDefault();

    public async Task<WorkOrderInfo?> FindOpenWorkOrderForOutageAsync(long downtimeId, CancellationToken ct)
    {
        var rows = await ReadRowsAsync(WorkOrderSql + " WHERE downtime_id = $1 AND kind = 'Corrective' AND status IN ('Open', 'Scheduled', 'InProgress', 'Completed') ORDER BY work_order_id LIMIT 1", ReadWorkOrder, ct, downtimeId);
        return rows.Count > 0 ? rows[0] : null;
    }

    private static PmPlanInfo ReadPlan(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetInt32(3), r.GetInt32(4), r.GetFieldValue<string[]>(5), r.GetBoolean(6), r.GetInt32(7), r.GetBoolean(8),
        NDec(r, 9), Dec(r, 10));

    private static WorkOrderInfo ReadWorkOrder(NpgsqlDataReader r) => new(
        r.GetInt64(0), NText(r, 1), Enum.Parse<WorkOrderKind>(r.GetString(2)), r.IsDBNull(3) ? null : r.GetInt64(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
        r.GetFieldValue<string[]>(6), r.IsDBNull(7) ? null : r.GetFieldValue<DateOnly>(7), Enum.Parse<WorkOrderStatus>(r.GetString(8)), NTime(r, 9), NTime(r, 10), r.IsDBNull(11) ? null : r.GetInt64(11),
        r.GetString(12), r.GetFieldValue<DateTimeOffset>(13), NText(r, 14), NTime(r, 15), NText(r, 16), NTime(r, 17), NText(r, 18), NText(r, 19), NTime(r, 20), NText(r, 21),
        NDec(r, 22), NTime(r, 23), NText(r, 24), NText(r, 25), NText(r, 26), r.GetFieldValue<string[]>(27));

    private static decimal Dec(NpgsqlDataReader r, int i) => r.GetDecimal(i) / 1.000000000000000000000000000000000m;

    private static decimal? NDec(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : Dec(r, i);

    private static NpgsqlParameter Int(int? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer };

    private static NpgsqlParameter Num(decimal? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Numeric };

    private static NpgsqlParameter Date(DateOnly? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date };

    private static string? NText(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static DateTimeOffset? NTime(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);

    private static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text };

    private static NpgsqlParameter Long(long? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint };

    private static NpgsqlParameter Time(DateTimeOffset? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz };

    private async Task<IReadOnlyList<T>> ReadRowsAsync<T>(string sql, Func<NpgsqlDataReader, T> map, CancellationToken ct, params object?[] args)
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
        services.AddForgeStore<IAssetStore>(uow => new PostgresAssetStore(uow));
        services.AddForgeStore<IAssetLookup>(uow => new PostgresAssetStore(uow));
        services.AddSingleton<AssetService>();
        return services;
    }
}
