using System.Text.Json;
using Forge.Kernel.Postgres;
using Forge.Quality.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Forge.Quality.Infrastructure;

/// <summary>Determinations and first article inspections (V3.11).</summary>
internal sealed class PostgresDeterminationStore(PostgresUnitOfWork uow) : IDeterminationLookup, IDeterminationStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<Determination>> ListDeterminationsAsync(DeterminationQuestion? question, string? subject, CancellationToken ct)
    {
        var list = new List<Determination>();
        await using var cmd = uow.Command(
            """
            SELECT determination_id, question, subject, state, basis, decided_by, decided_at FROM quality.determination
            WHERE ($1::text IS NULL OR question = $1) AND ($2::text IS NULL OR subject = $2) ORDER BY determination_id DESC
            """,
            Text(question is null ? null : "FAI"), Text(subject));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new Determination(r.GetInt64(0), DeterminationQuestion.Fai, r.GetString(2), Enum.Parse<DeterminationState>(r.GetString(3)), r.GetString(4), r.GetString(5), r.GetFieldValue<DateTimeOffset>(6)));
        }

        return list;
    }

    public async Task<IReadOnlyList<FaiRecord>> ListFaisAsync(string? subject, CancellationToken ct)
    {
        var list = new List<FaiRecord>();
        await using var cmd = uow.Command(
            """
            SELECT fai_id, subject, part_number, part_revision, supplier_code, passed, results::text, document_number, document_revision, recorded_by, recorded_at
            FROM quality.fai WHERE $1::text IS NULL OR subject = $1 ORDER BY fai_id DESC
            """,
            Text(subject));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new FaiRecord(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetBoolean(5),
                JsonSerializer.Deserialize<List<ReceivingCharacteristicResult>>(r.GetString(6), Json) ?? [], r.GetString(7), r.GetString(8), r.GetString(9), r.GetFieldValue<DateTimeOffset>(10)));
        }

        return list;
    }

    public async Task<long> InsertDeterminationAsync(Determination determination, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(determination);
        await using var cmd = uow.Command(
            "INSERT INTO quality.determination (question, subject, state, basis, decided_by, decided_at) VALUES ('FAI', $1, $2, $3, $4, $5) RETURNING determination_id",
            determination.Subject, determination.State.ToString(), determination.Basis, determination.DecidedBy, determination.DecidedAt);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<long> InsertFaiAsync(FaiRecord fai, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fai);
        await using var cmd = uow.Command(
            """
            INSERT INTO quality.fai (subject, part_number, part_revision, supplier_code, passed, results, document_number, document_revision, recorded_by, recorded_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10) RETURNING fai_id
            """,
            fai.Subject, fai.PartNumber, fai.PartRevision, fai.SupplierCode, fai.Passed,
            new NpgsqlParameter { Value = JsonSerializer.Serialize(fai.Results, Json), NpgsqlDbType = NpgsqlDbType.Jsonb },
            fai.DocumentNumber, fai.DocumentRevision, fai.RecordedBy, fai.RecordedAt);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };
}

