using Forge.Competency.Contracts;
using Forge.Kernel.Identity;
using Forge.Kernel.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Forge.Competency.Infrastructure;

internal sealed class PostgresCompetencyStore(PostgresUnitOfWork uow) : ICompetencyStore, ICompetencyLookup
{
    public async Task<QualificationTypeInfo?> FindTypeAsync(string code, CancellationToken ct) =>
        (await ReadTypes("WHERE t.code = $1", ct, code)).SingleOrDefault();

    public Task<IReadOnlyList<QualificationTypeInfo>> ListTypesAsync(CancellationToken ct) => ReadTypes(string.Empty, ct);

    public async Task<bool> QualificationTypeExistsAsync(string code, CancellationToken ct)
    {
        await using var cmd = uow.Command("SELECT EXISTS (SELECT 1 FROM competency.qualification_type WHERE code = $1)", code);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task InsertTypeAsync(QualificationTypeInfo type, DateTimeOffset at, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(type);
        await using var cmd = uow.Command(
            "INSERT INTO competency.qualification_type (code, name, kind, validity_days, created_at, created_by) VALUES ($1, $2, $3, $4, $5, $6)",
            type.Code, type.Name, type.Kind.ToString(), new NpgsqlParameter { Value = (object?)type.ValidityDays ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer }, at, actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> AddPrerequisiteAsync(string code, string prerequisite, CancellationToken ct)
    {
        await using var cmd = uow.Command("INSERT INTO competency.prerequisite (code, prerequisite_code) VALUES ($1, $2) ON CONFLICT DO NOTHING", code, prerequisite);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<IReadOnlyList<(string Code, string Prerequisite)>> ListPrerequisitesAsync(CancellationToken ct)
    {
        var edges = new List<(string, string)>();
        await using var cmd = uow.Command("SELECT code, prerequisite_code FROM competency.prerequisite ORDER BY code, prerequisite_code");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            edges.Add((reader.GetString(0), reader.GetString(1)));
        }

        return edges;
    }

    public async Task<PersonQualification?> FindAsync(PersonId person, string code, CancellationToken ct) =>
        (await ReadQualifications("WHERE person_id = $1 AND code = $2", ct, person.Value, code)).SingleOrDefault();

    public Task<IReadOnlyList<PersonQualification>> GetQualificationsAsync(IReadOnlyCollection<PersonId> people, CancellationToken ct) =>
        ReadQualifications("WHERE person_id = ANY($1)", ct, people.Select(p => p.Value).ToArray());

    public async Task UpsertAsync(PersonQualification record, string actor, string? evidence, DateTimeOffset at, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var cmd = uow.Command(
            """
            INSERT INTO competency.person_qualification (person_id, code, status, certified_at, expires_at, evidence, updated_at, updated_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            ON CONFLICT (person_id, code) DO UPDATE SET status = EXCLUDED.status, certified_at = EXCLUDED.certified_at,
                expires_at = EXCLUDED.expires_at, evidence = COALESCE(EXCLUDED.evidence, competency.person_qualification.evidence),
                updated_at = EXCLUDED.updated_at, updated_by = EXCLUDED.updated_by
            """,
            record.PersonId, record.Code, record.Status.ToString(),
            new NpgsqlParameter { Value = (object?)record.CertifiedAt ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz },
            new NpgsqlParameter { Value = (object?)record.ExpiresAt ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz },
            new NpgsqlParameter { Value = (object?)evidence ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text },
            at, actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<IReadOnlyList<QualificationTypeInfo>> ReadTypes(string where, CancellationToken ct, params object?[] args)
    {
        var list = new List<QualificationTypeInfo>();
        await using var cmd = uow.Command(
            $$"""
            SELECT t.code, t.name, t.kind, t.validity_days,
                   COALESCE(array_agg(p.prerequisite_code ORDER BY p.prerequisite_code) FILTER (WHERE p.prerequisite_code IS NOT NULL), '{}')
            FROM competency.qualification_type t
            LEFT JOIN competency.prerequisite p ON p.code = t.code
            {{where}}
            GROUP BY t.code ORDER BY t.code
            """,
            args);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new QualificationTypeInfo(
                reader.GetString(0),
                reader.GetString(1),
                Enum.Parse<QualificationKind>(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.GetFieldValue<string[]>(4)));
        }

        return list;
    }

    private async Task<IReadOnlyList<PersonQualification>> ReadQualifications(string where, CancellationToken ct, params object?[] args)
    {
        var list = new List<PersonQualification>();
        await using var cmd = uow.Command(
            $"SELECT person_id, code, status, certified_at, expires_at FROM competency.person_qualification {where} ORDER BY person_id, code",
            args);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new PersonQualification(
                reader.GetGuid(0),
                reader.GetString(1),
                Enum.Parse<QualificationStatus>(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return list;
    }
}

public static class CompetencyRegistration
{
    public static IServiceCollection AddForgeCompetency(this IServiceCollection services)
    {
        services.AddForgeMigrations(typeof(CompetencyRegistration).Assembly, "competency");
        services.AddForgeStore<ICompetencyStore>(uow => new PostgresCompetencyStore(uow));
        services.AddForgeStore<ICompetencyLookup>(uow => new PostgresCompetencyStore(uow));
        services.AddSingleton<CompetencyService>();
        return services;
    }
}
