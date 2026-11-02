using Forge.Hazcom.Contracts;
using Forge.Kernel.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Hazcom.Infrastructure;

internal sealed class PostgresHazcomStore(PostgresUnitOfWork uow) : IHazcomStore, IHazcomLookup
{
    private const string ChemicalSql =
        "SELECT part_number, hazardous, signal_word, pictograms, hazard_statements, ppe, storage, updated_by, updated_at, training_review_due, training_reviewed_by, training_reviewed_at, training_review_note FROM hazcom.chemical";

    private const string SdsSql =
        """
        SELECT sds_id, part_number, manufacturer, product_name, revision_date, file_sha256, file_name, status, submitted_by, submitted_at, decided_by, decided_at, decision_note,
               review_due, reviewed_by, reviewed_at
        FROM hazcom.sds
        """;

    private static NpgsqlParameter Text(string? v) => new() { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };

    private static NpgsqlParameter Date(DateOnly? v) => new() { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date };

    public async Task UpsertChemicalAsync(ChemicalInfo c, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            """
            INSERT INTO hazcom.chemical (part_number, hazardous, signal_word, pictograms, hazard_statements, ppe, storage, updated_by, updated_at, training_review_due)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $2)
            ON CONFLICT (part_number) DO UPDATE SET hazardous = EXCLUDED.hazardous, signal_word = EXCLUDED.signal_word, pictograms = EXCLUDED.pictograms,
                training_review_due = EXCLUDED.hazardous AND (hazcom.chemical.training_review_due OR NOT hazcom.chemical.hazardous),
                hazard_statements = EXCLUDED.hazard_statements, ppe = EXCLUDED.ppe, storage = EXCLUDED.storage, updated_by = EXCLUDED.updated_by, updated_at = EXCLUDED.updated_at
            """,
            c.PartNumber, c.Hazardous, c.SignalWord.ToString(), new NpgsqlParameter { Value = c.Pictograms.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text },
            Text(c.HazardStatements), Text(c.Ppe), Text(c.Storage), c.UpdatedBy, c.UpdatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ReviewTrainingAsync(string partNumber, string actor, DateTimeOffset at, string note, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE hazcom.chemical SET training_review_due = false, training_reviewed_by = $2, training_reviewed_at = $3, training_review_note = $4 WHERE part_number = $1",
            partNumber, actor, at, note);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> InsertSdsAsync(SdsInfo s, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "INSERT INTO hazcom.sds (part_number, manufacturer, product_name, revision_date, file_sha256, file_name, status, submitted_by, submitted_at) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9) RETURNING sds_id",
            s.PartNumber, s.Manufacturer, s.ProductName, s.RevisionDate, s.FileSha256, Text(s.FileName), s.Status.ToString(), s.SubmittedBy, s.SubmittedAt);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task SupersedeAsync(string partNumber, long exceptSdsId, CancellationToken ct)
    {
        await using var cmd = uow.Command("UPDATE hazcom.sds SET status = 'Superseded' WHERE part_number = $1 AND status = 'Approved' AND sds_id <> $2", partNumber, exceptSdsId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DecideSdsAsync(long sdsId, SdsStatus status, string actor, DateTimeOffset at, string? note, DateOnly? reviewDue, CancellationToken ct)
    {
        if (status == SdsStatus.Approved)
        {
            // The approved one moves first so the "one approved per material" rule holds at every step.
            var part = (await FindSdsAsync(sdsId, ct))!.PartNumber;
            await SupersedeAsync(part, sdsId, ct);
        }

        await using var cmd = uow.Command(
            "UPDATE hazcom.sds SET status = $2, decided_by = $3, decided_at = $4, decision_note = $5, review_due = $6 WHERE sds_id = $1",
            sdsId, status.ToString(), actor, at, Text(note), Date(reviewDue));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ReviewSdsAsync(long sdsId, string actor, DateTimeOffset at, string note, DateOnly reviewDue, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE hazcom.sds SET reviewed_by = $2, reviewed_at = $3, decision_note = $4, review_due = $5, flagged_for = NULL WHERE sds_id = $1", sdsId, actor, at, note, reviewDue);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<SdsInfo>> TakeDueAsync(DateOnly by, CancellationToken ct)
    {
        var due = await ReadSdsAsync(SdsSql + " WHERE status = 'Approved' AND review_due <= $1 AND flagged_for IS DISTINCT FROM review_due ORDER BY review_due FOR UPDATE", ct, by);
        foreach (var s in due)
        {
            await using var cmd = uow.Command("UPDATE hazcom.sds SET flagged_for = review_due WHERE sds_id = $1", s.SdsId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        return due;
    }

    public async Task<IReadOnlyList<ChemicalInfo>> ListChemicalsAsync(CancellationToken ct)
    {
        var list = new List<ChemicalInfo>();
        await using var cmd = uow.Command(ChemicalSql + " ORDER BY part_number");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadChemical(r));
        return list;
    }

    public async Task<ChemicalInfo?> FindChemicalAsync(string partNumber, CancellationToken ct)
    {
        await using var cmd = uow.Command(ChemicalSql + " WHERE part_number = $1", partNumber);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadChemical(r) : null;
    }

    public Task<IReadOnlyList<SdsInfo>> ListSdsAsync(string? partNumber, CancellationToken ct) =>
        ReadSdsAsync(SdsSql + " WHERE ($1::text IS NULL OR part_number = $1) ORDER BY part_number, revision_date DESC, sds_id DESC", ct, Text(partNumber));

    public async Task<SdsInfo?> FindSdsAsync(long sdsId, CancellationToken ct) => (await ReadSdsAsync(SdsSql + " WHERE sds_id = $1", ct, sdsId)).SingleOrDefault();

    public async Task<SdsInfo?> CurrentSdsAsync(string partNumber, CancellationToken ct) =>
        (await ReadSdsAsync(SdsSql + " WHERE part_number = $1 AND status = 'Approved'", ct, partNumber)).SingleOrDefault();

    private static ChemicalInfo ReadChemical(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetBoolean(1), Enum.Parse<SignalWord>(r.GetString(2)), r.GetFieldValue<string[]>(3), r.IsDBNull(4) ? null : r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetString(7), r.GetFieldValue<DateTimeOffset>(8),
        r.GetBoolean(9), r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11), r.IsDBNull(12) ? null : r.GetString(12));

    private async Task<IReadOnlyList<SdsInfo>> ReadSdsAsync(string sql, CancellationToken ct, params object?[] args)
    {
        var list = new List<SdsInfo>();
        await using var cmd = uow.Command(sql, args);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new SdsInfo(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateOnly>(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
                Enum.Parse<SdsStatus>(r.GetString(7)), r.GetString(8), r.GetFieldValue<DateTimeOffset>(9), r.IsDBNull(10) ? null : r.GetString(10),
                r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11), r.IsDBNull(12) ? null : r.GetString(12), r.IsDBNull(13) ? null : r.GetFieldValue<DateOnly>(13),
                r.IsDBNull(14) ? null : r.GetString(14), r.IsDBNull(15) ? null : r.GetFieldValue<DateTimeOffset>(15)));
        }

        return list;
    }
}

public static class HazcomRegistration
{
    public static IServiceCollection AddForgeHazcom(this IServiceCollection services)
    {
        services.AddForgeMigrations(typeof(HazcomRegistration).Assembly, "hazcom");
        services.AddForgeStore<IHazcomStore>(uow => new PostgresHazcomStore(uow));
        services.AddForgeStore<IHazcomLookup>(uow => new PostgresHazcomStore(uow));
        services.AddSingleton<HazcomService>();
        return services;
    }
}
