using Forge.Execution.Contracts;
using Forge.Kernel.Postgres;
using Forge.Quality.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Quality.Infrastructure;

internal sealed partial class PostgresQualityStore(PostgresUnitOfWork uow) : IQualityStore, IQualityLookup, IWorkHoldLookup, ISuspectScrapReportStore
{
    private const string NcrSql =
        """
        SELECT ncr_id, part_number, order_number, sequence, lot_number, quantity, defect, description, status, disposition, disposition_note,
               raised_by, raised_at, dispositioned_by, approved_by, closed_by, closure_note, closed_at, parent_ncr_id
        FROM quality.ncr
        """;

    private const string CapaSql =
        "SELECT capa_id, ncr_id, title, root_cause, action, due_date, status, opened_by, opened_at, implemented_by, implementation_note, verified_by, verified_at, EXISTS(SELECT 1 FROM quality.capa_governance g WHERE g.capa_id=quality.capa.capa_id) FROM quality.capa";

    private const string AllRevisions = "*";

    public async Task SavePlanAsync(InspectionPlanInfo plan, DateTimeOffset at, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await DeletePlanAsync(plan.PartNumber, plan.Sequence, plan.Revision, ct);
        await using (var cmd = uow.Command(
            "INSERT INTO quality.inspection_plan (part_number, sequence, title, updated_at, updated_by, revision, planned_minutes) VALUES ($1, $2, $3, $4, $5, $6, $7)",
            plan.PartNumber, plan.Sequence, plan.Title, at, actor, plan.Revision ?? AllRevisions, plan.PlannedMinutes))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

        foreach (var c in plan.Characteristics)
        {
            await using var cmd = uow.Command(
                "INSERT INTO quality.characteristic (part_number, sequence, number, name, kind, nominal, lower_limit, upper_limit, unit, revision) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)",
                plan.PartNumber, plan.Sequence, c.Number, c.Name, c.Kind.ToString(), Num(c.Nominal), Num(c.Lower), Num(c.Upper), Text(c.Unit), plan.Revision ?? AllRevisions);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<bool> DeletePlanAsync(string partNumber, int sequence, string? revision, CancellationToken ct)
    {
        await using var cmd = uow.Command("DELETE FROM quality.inspection_plan WHERE part_number = $1 AND sequence = $2 AND revision = $3", partNumber, sequence, revision ?? AllRevisions);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<long> InsertInspectionAsync(InspectionInfo inspection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        long id;
        await using (var cmd = uow.Command(
            """
            INSERT INTO quality.inspection (order_number, sequence, part_number, quantity_inspected, result, gauge_code, notes, inspector, at, ncr_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10) RETURNING inspection_id
            """,
            inspection.OrderNumber, inspection.Sequence, inspection.PartNumber, inspection.QuantityInspected, inspection.Result.ToString(),
            Text(inspection.GaugeCode), Text(inspection.Notes), inspection.Inspector, inspection.At, Long(inspection.NcrId)))
        {
            id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }

        foreach (var r in inspection.Results)
        {
            await using var cmd = uow.Command(
                "INSERT INTO quality.inspection_result (inspection_id, number, name, kind, lower_limit, upper_limit, measured, accepted, pass) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)",
                id, r.Number, r.Name, r.Kind.ToString(), Num(r.Lower), Num(r.Upper), Num(r.Measured),
                new NpgsqlParameter { Value = (object?)r.Accepted ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Boolean }, r.Pass);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        return id;
    }

    public async Task<long> InsertNcrAsync(NcrInfo ncr, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ncr);
        await using var cmd = uow.Command(
            """
            INSERT INTO quality.ncr (part_number, order_number, sequence, lot_number, quantity, defect, description, status, raised_by, raised_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10) RETURNING ncr_id
            """,
            ncr.PartNumber, Long(ncr.OrderNumber), Int(ncr.Sequence), Text(ncr.LotNumber), ncr.Quantity, ncr.Defect, ncr.Description, ncr.Status.ToString(), ncr.RaisedBy, ncr.RaisedAt);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<long?> SplitNcrAsync(long parentNcrId, decimal quantity, NcrInfo child, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(child);
        await using (var reduce = uow.Command("UPDATE quality.ncr SET quantity = quantity - $2 WHERE ncr_id = $1 AND status = 'Open' AND quantity > $2", parentNcrId, quantity))
        {
            if (await reduce.ExecuteNonQueryAsync(ct) != 1) return null;
        }

        await using var cmd = uow.Command(
            """
            INSERT INTO quality.ncr (part_number, order_number, sequence, lot_number, quantity, defect, description, status, raised_by, raised_at, parent_ncr_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, 'Open', $8, $9, $10) RETURNING ncr_id
            """,
            child.PartNumber, Long(child.OrderNumber), Int(child.Sequence), Text(child.LotNumber), quantity, child.Defect, child.Description, child.RaisedBy, child.RaisedAt, parentNcrId);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<long> InsertSuspectScrapReportAsync(SuspectScrapReport report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        await using var cmd = uow.Command(
            """
            INSERT INTO quality.suspect_scrap_report
                (kind, order_number, sequence, part_number, revision, lot_number, serial_number, quantity, machine_code,
                 reporter_person_id, reason_code, description, reported_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13) RETURNING report_id
            """,
            report.Kind.ToString(), report.OrderNumber, report.Sequence, report.PartNumber, Text(report.Revision), Text(report.LotNumber),
            Text(report.SerialNumber), report.Quantity, Text(report.MachineCode), report.ReporterPersonId, report.ReasonCode, report.Description, report.ReportedAt);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<bool> LinkSuspectScrapReportNcrAsync(long reportId, long ncrId, CancellationToken ct)
    {
        await using var cmd = uow.Command("UPDATE quality.suspect_scrap_report SET ncr_id=$2 WHERE report_id=$1 AND ncr_id IS NULL", reportId, ncrId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task UpdateNcrAsync(NcrInfo ncr, DateTimeOffset at, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ncr);
        await using var cmd = uow.Command(
            """
            UPDATE quality.ncr SET
                status = $2, disposition = $3, disposition_note = $4,
                dispositioned_by = $5,
                dispositioned_at = CASE WHEN $5::text IS NULL THEN NULL WHEN dispositioned_by IS DISTINCT FROM $5 OR dispositioned_at IS NULL THEN $8 ELSE dispositioned_at END,
                approved_by = $6,
                approved_at = CASE WHEN $6::text IS NULL THEN NULL WHEN approved_by IS NULL THEN $8 ELSE approved_at END,
                closed_by = $7, closure_note = $9,
                closed_at = CASE WHEN $7::text IS NULL THEN NULL WHEN closed_by IS NULL THEN $8 ELSE closed_at END
            WHERE ncr_id = $1
            """,
            ncr.NcrId, ncr.Status.ToString(), Text(ncr.Disposition?.ToString()), Text(ncr.DispositionNote), Text(ncr.DispositionedBy), Text(ncr.ApprovedBy), Text(ncr.ClosedBy), at, Text(ncr.ClosureNote));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> InsertCapaAsync(CapaInfo capa, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(capa);
        await using var cmd = uow.Command(
            "INSERT INTO quality.capa (ncr_id, title, root_cause, action, due_date, status, opened_by, opened_at) VALUES ($1, $2, $3, $4, $5, $6, $7, $8) RETURNING capa_id",
            Long(capa.NcrId), capa.Title, Text(capa.RootCause), Text(capa.Action), capa.DueDate, capa.Status.ToString(), capa.OpenedBy, capa.OpenedAt);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<CapaInfo?> FindCapaAsync(long capaId, CancellationToken ct) =>
        (await ReadAsync(CapaSql + " WHERE capa_id = $1", ReadCapa, ct, capaId)).SingleOrDefault();

    public async Task UpdateCapaAsync(CapaInfo capa, DateTimeOffset at, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(capa);
        await using var cmd = uow.Command(
            """
            UPDATE quality.capa SET status = $2,
                implemented_by = $3, implementation_note = $4,
                implemented_at = CASE WHEN $3::text IS NULL THEN NULL WHEN implemented_by IS NULL THEN $6 ELSE implemented_at END,
                verified_by = $5,
                verified_at = CASE WHEN $5::text IS NULL THEN NULL WHEN verified_by IS NULL THEN $6 ELSE verified_at END
            WHERE capa_id = $1
            """,
            capa.CapaId, capa.Status.ToString(), Text(capa.ImplementedBy), Text(capa.ImplementationNote), Text(capa.VerifiedBy), at);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<InspectionPlanInfo>> ListPlansAsync(string? partNumber, CancellationToken ct)
    {
        var plans = await ReadAsync(
            "SELECT part_number, sequence, title, revision, planned_minutes FROM quality.inspection_plan WHERE ($1::text IS NULL OR part_number = $1) ORDER BY part_number, sequence, revision",
            r => (Part: r.GetString(0), Sequence: r.GetInt32(1), Title: r.GetString(2), Revision: r.GetString(3), Minutes: r.GetInt32(4)),
            ct,
            Text(partNumber));
        var characteristics = await ReadAsync(
            "SELECT part_number, sequence, number, name, kind, nominal, lower_limit, upper_limit, unit, revision FROM quality.characteristic WHERE ($1::text IS NULL OR part_number = $1) ORDER BY part_number, sequence, number",
            r => (Part: r.GetString(0), Sequence: r.GetInt32(1), Revision: r.GetString(9), Info: new CharacteristicInfo(
                r.GetInt32(2), r.GetString(3), Enum.Parse<CharacteristicKind>(r.GetString(4)), NDec(r, 5), NDec(r, 6), NDec(r, 7), r.IsDBNull(8) ? null : r.GetString(8))),
            ct,
            Text(partNumber));
        var byPlan = characteristics.ToLookup(c => (c.Part, c.Sequence, c.Revision), c => c.Info);
        return plans.Select(p => new InspectionPlanInfo(p.Part, p.Sequence, p.Title, byPlan[(p.Part, p.Sequence, p.Revision)].ToList(), p.Revision == AllRevisions ? null : p.Revision, p.Minutes)).ToList();
    }

    public async Task<IReadOnlyList<InspectionInfo>> ListInspectionsAsync(long? orderNumber, int take, CancellationToken ct)
    {
        var heads = await ReadAsync(
            """
            SELECT inspection_id, order_number, sequence, part_number, quantity_inspected, result, gauge_code, notes, inspector, at, ncr_id
            FROM quality.inspection WHERE ($1::bigint IS NULL OR order_number = $1) ORDER BY inspection_id DESC LIMIT $2
            """,
            r => new InspectionInfo(
                r.GetInt64(0), r.GetInt64(1), r.GetInt32(2), r.GetString(3), Dec(r, 4), Enum.Parse<InspectionResult>(r.GetString(5)),
                r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetString(8), r.GetFieldValue<DateTimeOffset>(9), [], r.IsDBNull(10) ? null : r.GetInt64(10)),
            ct,
            Long(orderNumber),
            Math.Clamp(take, 1, 1000));
        if (heads.Count == 0)
        {
            return heads;
        }

        var ids = heads.Select(h => h.InspectionId).ToArray();
        var results = await ReadAsync(
            "SELECT inspection_id, number, name, kind, lower_limit, upper_limit, measured, accepted, pass FROM quality.inspection_result WHERE inspection_id = ANY($1) ORDER BY inspection_id, number",
            r => (Id: r.GetInt64(0), Result: new CharacteristicResult(
                r.GetInt32(1), r.GetString(2), Enum.Parse<CharacteristicKind>(r.GetString(3)), NDec(r, 4), NDec(r, 5), NDec(r, 6), r.IsDBNull(7) ? null : r.GetBoolean(7), r.GetBoolean(8))),
            ct,
            ids);
        var byId = results.ToLookup(r => r.Id, r => r.Result);
        return heads.Select(h => h with { Results = byId[h.InspectionId].ToList() }).ToList();
    }

    public Task<IReadOnlyList<NcrInfo>> ListNcrsAsync(bool includeClosed, CancellationToken ct) =>
        ReadAsync(NcrSql + (includeClosed ? string.Empty : " WHERE status <> 'Closed'") + " ORDER BY ncr_id DESC LIMIT 500", ReadNcr, ct);

    public Task<IReadOnlyList<InspectionInfo>> ListInspectionsBetweenAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        ReadAsync(
            """
            SELECT inspection_id, order_number, sequence, part_number, quantity_inspected, result, gauge_code, notes, inspector, at, ncr_id
            FROM quality.inspection WHERE at >= $1 AND at < $2 ORDER BY at, inspection_id
            """,
            r => new InspectionInfo(
                r.GetInt64(0), r.GetInt64(1), r.GetInt32(2), r.GetString(3), Dec(r, 4), Enum.Parse<InspectionResult>(r.GetString(5)),
                r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetString(8), r.GetFieldValue<DateTimeOffset>(9), [], r.IsDBNull(10) ? null : r.GetInt64(10)),
            ct,
            from,
            to);

    public Task<IReadOnlyList<NcrInfo>> ListNcrsRaisedBetweenAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        ReadAsync(NcrSql + " WHERE raised_at >= $1 AND raised_at < $2 ORDER BY raised_at, ncr_id", ReadNcr, ct, from, to);

    public async Task<NcrInfo?> FindNcrAsync(long ncrId, CancellationToken ct) =>
        (await ReadAsync(NcrSql + " WHERE ncr_id = $1", ReadNcr, ct, ncrId)).SingleOrDefault();

    public Task<IReadOnlyList<SuspectScrapReport>> ListSuspectScrapReportsAsync(long? orderNumber, CancellationToken ct) =>
        ReadAsync(
            """
            SELECT report_id, kind, order_number, sequence, part_number, revision, lot_number, serial_number, quantity,
                   machine_code, reporter_person_id, reason_code, description, reported_at, ncr_id
            FROM quality.suspect_scrap_report
            WHERE ($1::bigint IS NULL OR order_number = $1)
            ORDER BY reported_at DESC, report_id DESC LIMIT 1000
            """,
            r => new SuspectScrapReport(r.GetInt64(0), Enum.Parse<SuspectReportKind>(r.GetString(1)), r.GetInt64(2), r.GetInt32(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
                Dec(r, 8), r.IsDBNull(9) ? null : r.GetString(9), r.GetGuid(10), r.GetString(11), r.GetString(12),
                r.GetFieldValue<DateTimeOffset>(13), r.IsDBNull(14) ? null : r.GetInt64(14)), ct, Long(orderNumber));

    public async Task<SuspectScrapReport?> FindSuspectScrapReportAsync(long reportId, CancellationToken ct) =>
        (await ReadAsync(
            "SELECT report_id, kind, order_number, sequence, part_number, revision, lot_number, serial_number, quantity, machine_code, reporter_person_id, reason_code, description, reported_at, ncr_id FROM quality.suspect_scrap_report WHERE report_id=$1",
            r => new SuspectScrapReport(r.GetInt64(0), Enum.Parse<SuspectReportKind>(r.GetString(1)), r.GetInt64(2), r.GetInt32(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), Dec(r, 8),
                r.IsDBNull(9) ? null : r.GetString(9), r.GetGuid(10), r.GetString(11), r.GetString(12), r.GetFieldValue<DateTimeOffset>(13),
                r.IsDBNull(14) ? null : r.GetInt64(14)), ct, reportId)).SingleOrDefault();

    public Task<IReadOnlyList<CapaInfo>> ListCapasAsync(bool includeVerified, CancellationToken ct) =>
        ReadAsync(CapaSql + (includeVerified ? string.Empty : " WHERE status <> 'Verified'") + " ORDER BY capa_id DESC LIMIT 500", ReadCapa, ct);

    public async Task<OrderQuality> GetOrderQualityAsync(long orderNumber, string partNumber, CancellationToken ct)
    {
        // The plans for the revision the order builds (V2.3): that revision's, else the plan for every revision.
        var revision = (await uow.Store<IExecutionLookup>().FindOrderAsync(orderNumber, ct))?.Revision;
        // The revision's own plan decides the planned minutes where it has one (ADR 0057 §2).
        var points = await ReadAsync(
            "SELECT DISTINCT ON (sequence) sequence, planned_minutes FROM quality.inspection_plan WHERE part_number = $1 AND (revision = '*' OR revision = $2) ORDER BY sequence, (revision = '*')",
            r => (Sequence: r.GetInt32(0), Minutes: r.GetInt32(1)),
            ct,
            partNumber,
            Text(revision));
        var planned = points.Select(p => p.Sequence).ToList();

        // An operation counts as passed when its latest inspection passed.
        var latest = await ReadAsync(
            "SELECT DISTINCT ON (sequence) sequence, result FROM quality.inspection WHERE order_number = $1 ORDER BY sequence, inspection_id DESC",
            r => (Sequence: r.GetInt32(0), Passed: r.GetString(1) == "Pass"),
            ct,
            orderNumber);
        var passed = latest.Where(l => l.Passed).Select(l => l.Sequence).Order().ToList();
        var failed = latest.Where(l => !l.Passed).Select(l => l.Sequence).Order().ToList();
        var open = await ReadAsync(NcrSql + " WHERE order_number = $1 AND status IN ('Open', 'AwaitingApproval') ORDER BY ncr_id", ReadNcr, ct, orderNumber);
        return new OrderQuality(orderNumber, planned, passed, open, points.Where(p => p.Minutes > 0).ToDictionary(p => p.Sequence, p => p.Minutes), failed);
    }

    /// <summary>Quality's holds for the shop floor: undecided NCRs hold the whole order; an inspection point not yet passed holds the operations after it.</summary>
    public async Task<IReadOnlyList<WorkHold>> ListHoldsAsync(long orderNumber, string partNumber, CancellationToken ct)
    {
        var quality = await GetOrderQualityAsync(orderNumber, partNumber, ct);
        var holds = quality.OpenNcrs
            .Select(n => new WorkHold("Quality", $"NCR {n.NcrId}", null, $"NCR {n.NcrId} ({n.Defect}) is {(n.Status == NcrStatus.AwaitingApproval ? $"waiting for {(n.Disposition == Disposition.Repair ? "repair" : "use-as-is")} approval" : "waiting for a disposition")}."))
            .ToList();
        holds.AddRange(quality.PlannedSequences
            .Where(s => !quality.PassedSequences.Contains(s))
            .Select(s => new WorkHold("Quality", $"Inspection {orderNumber}/{s}", s, $"The inspection after operation {s} has not passed.")));
        return holds;
    }

    private static NcrInfo ReadNcr(NpgsqlDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        r.IsDBNull(2) ? null : r.GetInt64(2),
        r.IsDBNull(3) ? null : r.GetInt32(3),
        r.IsDBNull(4) ? null : r.GetString(4),
        Dec(r, 5),
        r.GetString(6),
        r.GetString(7),
        Enum.Parse<NcrStatus>(r.GetString(8)),
        r.IsDBNull(9) ? null : Enum.Parse<Disposition>(r.GetString(9)),
        r.IsDBNull(10) ? null : r.GetString(10),
        r.GetString(11),
        r.GetFieldValue<DateTimeOffset>(12),
        r.IsDBNull(13) ? null : r.GetString(13),
        r.IsDBNull(14) ? null : r.GetString(14),
        r.IsDBNull(15) ? null : r.GetString(15),
        r.IsDBNull(16) ? null : r.GetString(16),
        r.IsDBNull(17) ? null : r.GetFieldValue<DateTimeOffset>(17),
        r.IsDBNull(18) ? null : r.GetInt64(18));

    private static CapaInfo ReadCapa(NpgsqlDataReader r) => new(
        r.GetInt64(0),
        r.IsDBNull(1) ? null : r.GetInt64(1),
        r.GetString(2),
        r.IsDBNull(3) ? null : r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4),
        r.GetFieldValue<DateOnly>(5),
        Enum.Parse<CapaStatus>(r.GetString(6)),
        r.GetString(7),
        r.GetFieldValue<DateTimeOffset>(8),
        r.IsDBNull(9) ? null : r.GetString(9),
        r.IsDBNull(10) ? null : r.GetString(10),
        r.IsDBNull(11) ? null : r.GetString(11),
        r.IsDBNull(12) ? null : r.GetFieldValue<DateTimeOffset>(12),
        r.GetBoolean(13));

    private static decimal Normal(decimal value) => value / 1.000000000000000000000000000000000m;

    private static decimal Dec(NpgsqlDataReader r, int ordinal) => Normal(r.GetDecimal(ordinal));

    private static decimal? NDec(NpgsqlDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : Normal(r.GetDecimal(ordinal));

    private static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };

    private static NpgsqlParameter Long(long? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint };

    private static NpgsqlParameter Int(int? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer };

    private static NpgsqlParameter Num(decimal? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Numeric };

    private async Task<IReadOnlyList<T>> ReadAsync<T>(string sql, Func<NpgsqlDataReader, T> map, CancellationToken ct, params object?[] args)
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

public static class QualityRegistration
{
    public static IServiceCollection AddForgeQuality(this IServiceCollection services)
    {
        services.AddForgeMigrations(typeof(QualityRegistration).Assembly, "quality");
        services.AddForgeCommercialQuality();
        services.AddForgeStore<IQualityStore>(uow => new PostgresQualityStore(uow));
        services.AddForgeStore<IQualityLookup>(uow => new PostgresQualityStore(uow));
        services.AddForgeStore<ISuspectScrapReportStore>(uow => new PostgresQualityStore(uow));
        services.AddForgeStore<IWorkHoldLookup>(uow => new PostgresQualityStore(uow));
        services.AddSingleton<QualityService>();
        services.AddForgeStore<IFirstPassAttributionStore>(uow => new PostgresFirstPassAttributionStore(uow));
        services.AddForgeStore<IFirstPassAttributionLookup>(uow => new PostgresFirstPassAttributionStore(uow));
        services.AddSingleton<FirstPassAttributionService>();
        services.AddForgeStore<Forge.Quality.Contracts.IReturnAcceptanceLookup>(uow => new PostgresReturnCohortStore(uow));
        services.AddForgeStore<Forge.Quality.Contracts.IReturnCohortWriter>(uow => new PostgresReturnCohortStore(uow));
        services.AddSingleton<Forge.Quality.Acceptance.ReturnCohortReadService>();
        services.AddForgeStore<IQualityInvestigationLookup>(uow => new PostgresInvestigationStore(uow));
        services.AddForgeStore<IQualityInvestigationStore>(uow => new PostgresInvestigationStore(uow));
        services.AddForgeStore<IQualityInvestigationSourceLock>(uow => new PostgresInvestigationStore(uow));
        services.AddSingleton<Forge.Quality.Investigations.InvestigationService>();
        services.AddSingleton<Forge.Quality.Investigations.InvestigationReadService>();
        services.AddForgeStore<Forge.Quality.Contracts.IAcceptanceLookup>(uow => new PostgresAcceptanceStore(uow));
        services.AddForgeStore<Forge.Quality.Contracts.IAcceptanceStore>(uow => new PostgresAcceptanceStore(uow));
        services.AddSingleton<Forge.Quality.Acceptance.AcceptanceCriteriaService>();
        services.AddSingleton<Forge.Quality.Acceptance.ProductAcceptanceService>();
        services.AddSingleton<Forge.Quality.Contracts.IProductReleaseDecisionValidator>(sp => sp.GetRequiredService<Forge.Quality.Acceptance.ProductAcceptanceService>());
        services.AddSingleton<Forge.Quality.Contracts.IAcceptanceSourceAccess>(sp => sp.GetRequiredService<Forge.Quality.Acceptance.ProductAcceptanceService>());
        services.AddForgeStore<IReworkLookup>(uow => new PostgresReworkStore(uow));
        services.AddForgeStore<IReworkStore>(uow => new PostgresReworkStore(uow));
        services.AddForgeStore<IReworkPlanStore>(uow => new PostgresReworkPlanStore(uow));
        services.AddSingleton<ReworkService>();
        services.AddForgeStore<IDeterminationLookup>(uow => new PostgresDeterminationStore(uow));
        services.AddForgeStore<IDeterminationStore>(uow => new PostgresDeterminationStore(uow));
        services.AddSingleton<DeterminationService>();
        return services;
    }
}
