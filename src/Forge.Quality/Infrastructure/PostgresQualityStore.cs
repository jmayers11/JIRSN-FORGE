using Forge.Kernel.Postgres;
using Forge.Quality.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Quality.Infrastructure;

internal sealed class PostgresQualityStore(PostgresUnitOfWork uow) : IQualityStore, IQualityLookup
{
    private const string NcrSql =
        """
        SELECT ncr_id, part_number, order_number, sequence, lot_number, quantity, defect, description, status, disposition, disposition_note,
               raised_by, raised_at, dispositioned_by, approved_by, closed_by, closure_note, closed_at
        FROM quality.ncr
        """;

    private const string CapaSql =
        "SELECT capa_id, ncr_id, title, root_cause, action, due_date, status, opened_by, opened_at, implemented_by, implementation_note, verified_by, verified_at FROM quality.capa";

    public async Task<bool> InsertGaugeAsync(GaugeInfo gauge, DateTimeOffset at, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gauge);
        await using var cmd = uow.Command(
            "INSERT INTO quality.gauge (code, description, calibration_due, certificate, active, created_at, created_by) VALUES ($1, $2, $3, $4, true, $5, $6) ON CONFLICT DO NOTHING",
            gauge.Code, gauge.Description, gauge.CalibrationDue, Text(gauge.Certificate), at, actor);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task RecordCalibrationAsync(string code, DateOnly calibratedOn, DateOnly due, string? certificate, DateTimeOffset at, string actor, CancellationToken ct)
    {
        await using (var insert = uow.Command(
            "INSERT INTO quality.calibration (gauge_code, calibrated_on, due_date, certificate, recorded_at, recorded_by) VALUES ($1, $2, $3, $4, $5, $6)",
            code, calibratedOn, due, Text(certificate), at, actor))
        {
            await insert.ExecuteNonQueryAsync(ct);
        }

        await using var update = uow.Command("UPDATE quality.gauge SET calibration_due = $2, certificate = $3 WHERE code = $1", code, due, Text(certificate));
        await update.ExecuteNonQueryAsync(ct);
    }

    public async Task SavePlanAsync(InspectionPlanInfo plan, DateTimeOffset at, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await DeletePlanAsync(plan.PartNumber, plan.Sequence, ct);
        await using (var cmd = uow.Command(
            "INSERT INTO quality.inspection_plan (part_number, sequence, title, updated_at, updated_by) VALUES ($1, $2, $3, $4, $5)",
            plan.PartNumber, plan.Sequence, plan.Title, at, actor))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

        foreach (var c in plan.Characteristics)
        {
            await using var cmd = uow.Command(
                "INSERT INTO quality.characteristic (part_number, sequence, number, name, kind, nominal, lower_limit, upper_limit, unit) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)",
                plan.PartNumber, plan.Sequence, c.Number, c.Name, c.Kind.ToString(), Num(c.Nominal), Num(c.Lower), Num(c.Upper), Text(c.Unit));
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<bool> DeletePlanAsync(string partNumber, int sequence, CancellationToken ct)
    {
        await using var cmd = uow.Command("DELETE FROM quality.inspection_plan WHERE part_number = $1 AND sequence = $2", partNumber, sequence);
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
            Long(capa.NcrId), capa.Title, capa.RootCause, capa.Action, capa.DueDate, capa.Status.ToString(), capa.OpenedBy, capa.OpenedAt);
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
            "SELECT part_number, sequence, title FROM quality.inspection_plan WHERE ($1::text IS NULL OR part_number = $1) ORDER BY part_number, sequence",
            r => (Part: r.GetString(0), Sequence: r.GetInt32(1), Title: r.GetString(2)),
            ct,
            Text(partNumber));
        var characteristics = await ReadAsync(
            "SELECT part_number, sequence, number, name, kind, nominal, lower_limit, upper_limit, unit FROM quality.characteristic WHERE ($1::text IS NULL OR part_number = $1) ORDER BY part_number, sequence, number",
            r => (Part: r.GetString(0), Sequence: r.GetInt32(1), Info: new CharacteristicInfo(
                r.GetInt32(2), r.GetString(3), Enum.Parse<CharacteristicKind>(r.GetString(4)), NDec(r, 5), NDec(r, 6), NDec(r, 7), r.IsDBNull(8) ? null : r.GetString(8))),
            ct,
            Text(partNumber));
        var byPlan = characteristics.ToLookup(c => (c.Part, c.Sequence), c => c.Info);
        return plans.Select(p => new InspectionPlanInfo(p.Part, p.Sequence, p.Title, byPlan[(p.Part, p.Sequence)].ToList())).ToList();
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

    public async Task<NcrInfo?> FindNcrAsync(long ncrId, CancellationToken ct) =>
        (await ReadAsync(NcrSql + " WHERE ncr_id = $1", ReadNcr, ct, ncrId)).SingleOrDefault();

    public Task<IReadOnlyList<CapaInfo>> ListCapasAsync(bool includeVerified, CancellationToken ct) =>
        ReadAsync(CapaSql + (includeVerified ? string.Empty : " WHERE status <> 'Verified'") + " ORDER BY capa_id DESC LIMIT 500", ReadCapa, ct);

    public Task<IReadOnlyList<GaugeInfo>> ListGaugesAsync(CancellationToken ct) =>
        ReadAsync(
            "SELECT code, description, calibration_due, certificate, active FROM quality.gauge ORDER BY code",
            r => new GaugeInfo(r.GetString(0), r.GetString(1), r.GetFieldValue<DateOnly>(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetBoolean(4)),
            ct);

    public async Task<OrderQuality> GetOrderQualityAsync(long orderNumber, string partNumber, CancellationToken ct)
    {
        var planned = await ReadAsync("SELECT sequence FROM quality.inspection_plan WHERE part_number = $1 ORDER BY sequence", r => r.GetInt32(0), ct, partNumber);

        // An operation counts as passed when its latest inspection passed.
        var passed = await ReadAsync(
            """
            SELECT sequence FROM (
                SELECT DISTINCT ON (sequence) sequence, result FROM quality.inspection WHERE order_number = $1 ORDER BY sequence, inspection_id DESC
            ) latest WHERE result = 'Pass' ORDER BY sequence
            """,
            r => r.GetInt32(0),
            ct,
            orderNumber);
        var open = await ReadAsync(NcrSql + " WHERE order_number = $1 AND status IN ('Open', 'AwaitingApproval') ORDER BY ncr_id", ReadNcr, ct, orderNumber);
        return new OrderQuality(orderNumber, planned, passed, open);
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
        r.IsDBNull(17) ? null : r.GetFieldValue<DateTimeOffset>(17));

    private static CapaInfo ReadCapa(NpgsqlDataReader r) => new(
        r.GetInt64(0),
        r.IsDBNull(1) ? null : r.GetInt64(1),
        r.GetString(2),
        r.GetString(3),
        r.GetString(4),
        r.GetFieldValue<DateOnly>(5),
        Enum.Parse<CapaStatus>(r.GetString(6)),
        r.GetString(7),
        r.GetFieldValue<DateTimeOffset>(8),
        r.IsDBNull(9) ? null : r.GetString(9),
        r.IsDBNull(10) ? null : r.GetString(10),
        r.IsDBNull(11) ? null : r.GetString(11),
        r.IsDBNull(12) ? null : r.GetFieldValue<DateTimeOffset>(12));

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
        services.AddForgeStore<IQualityStore>(uow => new PostgresQualityStore(uow));
        services.AddForgeStore<IQualityLookup>(uow => new PostgresQualityStore(uow));
        services.AddSingleton<QualityService>();
        return services;
    }
}
