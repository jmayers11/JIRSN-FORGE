using Forge.Engineering.Contracts;
using Forge.Kernel.Postgres;
using NpgsqlTypes;

namespace Forge.Engineering.Infrastructure;

/// <summary>Training on controlled documents and the plant's training policy (V3.9).</summary>
internal sealed class PostgresDocumentTrainingStore(PostgresUnitOfWork uow) : IDocumentTrainingLookup, IDocumentTrainingStore
{
    public async Task<DocumentTrainingPolicy> GetTrainingPolicyAsync(CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT policy, document_types, changed_at, changed_by FROM engineering.document_training_policy WHERE policy_id = 1");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new DocumentTrainingPolicy(Enum.Parse<TrainingPolicy>(r.GetString(0)), r.GetFieldValue<string[]>(1).Select(x => Enum.Parse<DocumentType>(x)).ToList(), r.GetFieldValue<DateTimeOffset>(2), r.GetString(3))
            : new DocumentTrainingPolicy(TrainingPolicy.Off, [DocumentType.WorkInstruction, DocumentType.Procedure], DateTimeOffset.UnixEpoch, "system");
    }

    public async Task<IReadOnlyList<DocumentTraining>> ListTrainingAsync(IReadOnlyCollection<Guid>? people, CancellationToken ct)
    {
        var list = new List<DocumentTraining>();
        await using var cmd = uow.Command(
            """
            SELECT training_id, person_id, document_number, revision, trained_on, recorded_at, recorded_by
            FROM engineering.document_training WHERE $1::uuid[] IS NULL OR person_id = ANY($1) ORDER BY training_id
            """,
            new Npgsql.NpgsqlParameter { Value = (object?)people?.ToArray() ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new DocumentTraining(r.GetInt64(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateOnly>(4), r.GetFieldValue<DateTimeOffset>(5), r.GetString(6)));
        }

        return list;
    }

    public async Task<bool> InsertTrainingAsync(DocumentTraining training, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(training);
        await using var cmd = uow.Command(
            """
            INSERT INTO engineering.document_training (person_id, document_number, revision, trained_on, recorded_at, recorded_by)
            VALUES ($1, $2, $3, $4, $5, $6) ON CONFLICT (person_id, document_number, revision) DO NOTHING
            """,
            training.PersonId, training.DocumentNumber, training.Revision, training.TrainedOn, training.RecordedAt, training.RecordedBy);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task SetTrainingPolicyAsync(DocumentTrainingPolicy policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);
        await using var cmd = uow.Command(
            "UPDATE engineering.document_training_policy SET policy = $1, document_types = $2, changed_at = $3, changed_by = $4 WHERE policy_id = 1",
            policy.Policy.ToString(),
            new Npgsql.NpgsqlParameter { Value = policy.DocumentTypes.Select(t => t.ToString()).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text },
            policy.ChangedAt,
            policy.ChangedBy);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
