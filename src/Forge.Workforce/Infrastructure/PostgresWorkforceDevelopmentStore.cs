using System.Text.Json;
using Forge.Kernel.Postgres;
using Forge.Workforce.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Forge.Workforce.Infrastructure;

internal sealed class PostgresWorkforceDevelopmentStore(PostgresUnitOfWork uow) : IWorkforceDevelopmentStore
{
    private const string SelectColumns = "nomination_id, person_id, kind, pathway, rationale, expected_plant_benefit, evidence_references, estimated_training_hours, estimated_cost, currency, status, created_by, created_by_person_id, created_at, updated_at, version";

    public async Task<IReadOnlyList<DevelopmentNomination>> ListNominationsAsync(Guid? personId, CancellationToken ct)
    {
        var rows = new List<DevelopmentNomination>();
        await using var cmd = uow.Command($"SELECT {SelectColumns} FROM workforce.development_nomination WHERE ($1::uuid IS NULL OR person_id=$1) ORDER BY updated_at DESC, nomination_id", (object?)personId ?? DBNull.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(ReadNomination(reader));
        return rows;
    }

    public async Task<DevelopmentNomination?> FindNominationAsync(Guid nominationId, CancellationToken ct)
    {
        await using var cmd = uow.Command($"SELECT {SelectColumns} FROM workforce.development_nomination WHERE nomination_id=$1", nominationId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadNomination(reader) : null;
    }

    public async Task InsertNominationAsync(DevelopmentNomination nomination, CancellationToken ct)
    {
        await using var cmd = uow.Command("""
            INSERT INTO workforce.development_nomination
                (nomination_id,person_id,kind,pathway,rationale,expected_plant_benefit,evidence_references,estimated_training_hours,
                 estimated_cost,currency,status,created_by,created_by_person_id,created_at,updated_at,version)
            VALUES ($1,$2,$3,$4,$5,$6,$7::jsonb,$8,$9,$10,$11,$12,$13,$14,$15,$16)
            """,
            nomination.NominationId, nomination.PersonId, nomination.Kind.ToString(), nomination.Pathway, nomination.Rationale,
            nomination.ExpectedPlantBenefit, JsonSerializer.Serialize(nomination.EvidenceReferences), Num(nomination.EstimatedTrainingHours),
            Num(nomination.EstimatedCost), Text(nomination.Currency), nomination.Status.ToString(), nomination.CreatedBy,
            Uuid(nomination.CreatedByPersonId), nomination.CreatedAt, nomination.UpdatedAt, nomination.Version);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> AdvanceNominationAsync(DevelopmentNomination nomination, DevelopmentNominationStatus expectedStatus, int expectedVersion, CancellationToken ct)
    {
        await using var cmd = uow.Command("UPDATE workforce.development_nomination SET status=$2, updated_at=$3, version=$4 WHERE nomination_id=$1 AND status=$5 AND version=$6",
            nomination.NominationId, nomination.Status.ToString(), nomination.UpdatedAt, nomination.Version, expectedStatus.ToString(), expectedVersion);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task InsertNominationEventAsync(DevelopmentNominationEvent nominationEvent, CancellationToken ct)
    {
        await using var cmd = uow.Command("""
            INSERT INTO workforce.development_nomination_event
                (event_id,nomination_id,from_status,to_status,note,recorded_by,recorded_by_person_id,recorded_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8)
            """, nominationEvent.EventId, nominationEvent.NominationId, Text(nominationEvent.FromStatus?.ToString()),
            nominationEvent.ToStatus.ToString(), nominationEvent.Note, nominationEvent.RecordedBy, Uuid(nominationEvent.RecordedByPersonId), nominationEvent.RecordedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<DevelopmentNominationEvent>> ListNominationEventsAsync(Guid nominationId, CancellationToken ct)
    {
        var rows = new List<DevelopmentNominationEvent>();
        await using var cmd = uow.Command("SELECT event_id,nomination_id,from_status,to_status,note,recorded_by,recorded_by_person_id,recorded_at FROM workforce.development_nomination_event WHERE nomination_id=$1 ORDER BY recorded_at,event_id", nominationId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(new DevelopmentNominationEvent(reader.GetGuid(0), reader.GetGuid(1),
            reader.IsDBNull(2) ? null : Enum.Parse<DevelopmentNominationStatus>(reader.GetString(2)), Enum.Parse<DevelopmentNominationStatus>(reader.GetString(3)),
            reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetGuid(6), reader.GetFieldValue<DateTimeOffset>(7)));
        return rows;
    }

    private static DevelopmentNomination ReadNomination(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetGuid(1), Enum.Parse<DevelopmentNominationKind>(reader.GetString(2)), reader.GetString(3), reader.GetString(4),
        reader.GetString(5), JsonSerializer.Deserialize<string[]>(reader.GetString(6)) ?? [], reader.IsDBNull(7) ? null : reader.GetDecimal(7),
        reader.IsDBNull(8) ? null : reader.GetDecimal(8), reader.IsDBNull(9) ? null : reader.GetString(9),
        Enum.Parse<DevelopmentNominationStatus>(reader.GetString(10)), reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetGuid(12),
        reader.GetFieldValue<DateTimeOffset>(13), reader.GetFieldValue<DateTimeOffset>(14), reader.GetInt32(15));

    private static NpgsqlParameter Num(decimal? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Numeric };
    private static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text };
    private static NpgsqlParameter Uuid(Guid? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid };
}
