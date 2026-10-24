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
        await using var cmd = uow.Command("""
            SELECT code, prerequisite_code FROM competency.prerequisite
            UNION
            SELECT code, prerequisite_code FROM competency.prerequisite_alternative_member
            ORDER BY code, prerequisite_code
            """);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            edges.Add((reader.GetString(0), reader.GetString(1)));
        }

        return edges;
    }

    public async Task DefinePrerequisiteAlternativesAsync(string code, string groupCode, string name, IReadOnlyList<string> prerequisites, string actor, DateTimeOffset at, CancellationToken ct)
    {
        await using (var cmd = uow.Command(
            "INSERT INTO competency.prerequisite_alternative_group (code, group_code, name, created_at, created_by) VALUES ($1, $2, $3, $4, $5)",
            code, groupCode, name, at, actor))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

        foreach (var prerequisite in prerequisites)
        {
            await using var cmd = uow.Command(
                "INSERT INTO competency.prerequisite_alternative_member (code, group_code, prerequisite_code) VALUES ($1, $2, $3)",
                code, groupCode, prerequisite);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<PersonQualification?> FindAsync(PersonId person, string code, CancellationToken ct) =>
        (await ReadQualifications("WHERE person_id = $1 AND code = $2", ct, person.Value, code)).SingleOrDefault();

    public Task<IReadOnlyList<PersonQualification>> GetQualificationsAsync(IReadOnlyCollection<PersonId> people, CancellationToken ct) =>
        ReadQualifications("WHERE person_id = ANY($1)", ct, people.Select(p => p.Value).ToArray());

    public Task<IReadOnlyList<TrainingCourseCompletion>> GetTrainingCourseCompletionsAsync(IReadOnlyCollection<PersonId> people, CancellationToken ct) =>
        ReadCourseCompletions("WHERE person_id = ANY($1)", ct, people.Select(p => p.Value).ToArray());

    public Task<IReadOnlyList<PracticalAssessment>> GetPracticalAssessmentsAsync(IReadOnlyCollection<PersonId> people, CancellationToken ct) =>
        ReadPracticalAssessments("WHERE person_id = ANY($1)", ct, people.Select(p => p.Value).ToArray());

    public Task<IReadOnlyList<MachineAuthorization>> GetMachineAuthorizationsAsync(IReadOnlyCollection<PersonId> people, CancellationToken ct) =>
        ReadMachineAuthorizations("WHERE person_id = ANY($1)", ct, people.Select(p => p.Value).ToArray());

    public async Task InsertTrainingCourseCompletionAsync(TrainingCourseCompletion completion, CancellationToken ct)
    {
        await using var cmd = uow.Command("""
            INSERT INTO competency.training_course_completion
                (completion_id, person_id, course_code, course_version, trainer_person_id, completed_at, expires_at, evidence)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8)
            """, completion.CompletionId, completion.PersonId, completion.CourseCode, completion.CourseVersion,
            completion.TrainerPersonId, completion.CompletedAt,
            new NpgsqlParameter { Value = (object?)completion.ExpiresAt ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz },
            completion.Evidence);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertPracticalAssessmentAsync(PracticalAssessment assessment, CancellationToken ct)
    {
        await using var cmd = uow.Command("""
            INSERT INTO competency.practical_assessment
                (assessment_id, person_id, qualification_code, machine_code, activity_code, assessor_person_id, passed,
                 assessment_version, assessed_at, evidence, restrictions)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)
            """, assessment.AssessmentId, assessment.PersonId, assessment.QualificationCode, assessment.MachineCode,
            assessment.ActivityCode, assessment.AssessorPersonId, assessment.Passed, assessment.AssessmentVersion,
            assessment.AssessedAt, assessment.Evidence, Text(assessment.Restrictions));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<MachineAuthorization?> FindMachineAuthorizationAsync(PersonId person, string machineCode, string activityCode, string qualificationCode, CancellationToken ct) =>
        (await ReadMachineAuthorizations("WHERE person_id = $1 AND machine_code = $2 AND activity_code = $3 AND qualification_code = $4",
            ct, person.Value, machineCode, activityCode, qualificationCode)).SingleOrDefault();

    public async Task UpsertMachineAuthorizationAsync(MachineAuthorization authorization, string reason, string actor, DateTimeOffset at, CancellationToken ct)
    {
        await using (var cmd = uow.Command("""
            INSERT INTO competency.machine_authorization
                (person_id, machine_code, activity_code, qualification_code, status, independent_operation_allowed, authorized_at, expires_at, revision,
                 course_completion_id, assessment_id, evidence, restrictions, approved_by, updated_at, updated_by)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16)
            ON CONFLICT (person_id, machine_code, activity_code, qualification_code) DO UPDATE SET
                status = EXCLUDED.status, authorized_at = EXCLUDED.authorized_at, expires_at = EXCLUDED.expires_at,
                independent_operation_allowed = EXCLUDED.independent_operation_allowed,
                revision = EXCLUDED.revision, course_completion_id = EXCLUDED.course_completion_id,
                assessment_id = EXCLUDED.assessment_id, evidence = EXCLUDED.evidence,
                restrictions = EXCLUDED.restrictions, approved_by = EXCLUDED.approved_by,
                updated_at = EXCLUDED.updated_at, updated_by = EXCLUDED.updated_by
            """, authorization.PersonId, authorization.MachineCode, authorization.ActivityCode, authorization.QualificationCode,
            authorization.Status.ToString(), authorization.IndependentOperationAllowed, authorization.AuthorizedAt,
            new NpgsqlParameter { Value = (object?)authorization.ExpiresAt ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz },
            authorization.Revision, authorization.CourseCompletionId, authorization.AssessmentId, authorization.Evidence, Text(authorization.Restrictions),
            Guid.Parse(authorization.ApprovedBy), at, actor))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using var history = uow.Command("""
            INSERT INTO competency.machine_authorization_event
                (person_id, machine_code, activity_code, qualification_code, status, independent_operation_allowed, revision, course_completion_id, assessment_id, reason, recorded_at, recorded_by)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)
            """, authorization.PersonId, authorization.MachineCode, authorization.ActivityCode, authorization.QualificationCode,
            authorization.Status.ToString(), authorization.IndependentOperationAllowed, authorization.Revision, authorization.CourseCompletionId, authorization.AssessmentId, reason, at, actor);
        await history.ExecuteNonQueryAsync(ct);
    }

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
        await using (var cmd = uow.Command(
            $$"""
            SELECT t.code, t.name, t.kind, t.validity_days,
                   COALESCE(array_agg(p.prerequisite_code ORDER BY p.prerequisite_code) FILTER (WHERE p.prerequisite_code IS NOT NULL), '{}')
            FROM competency.qualification_type t
            LEFT JOIN competency.prerequisite p ON p.code = t.code
            {{where}}
            GROUP BY t.code ORDER BY t.code
            """,
            args))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                list.Add(new QualificationTypeInfo(
                    reader.GetString(0),
                    reader.GetString(1),
                    Enum.Parse<QualificationKind>(reader.GetString(2)),
                    reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    reader.GetFieldValue<string[]>(4)));
            }
        }

        var alternatives = new Dictionary<(string Code, string GroupCode), (string Name, List<string> Codes)>();
        await using (var cmd = uow.Command("""
            SELECT g.code, g.group_code, g.name, m.prerequisite_code
            FROM competency.prerequisite_alternative_group g
            JOIN competency.prerequisite_alternative_member m USING (code, group_code)
            ORDER BY g.code, g.group_code, m.prerequisite_code
            """))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var key = (reader.GetString(0), reader.GetString(1));
                if (!alternatives.TryGetValue(key, out var group))
                {
                    group = (reader.GetString(2), []);
                    alternatives.Add(key, group);
                }

                group.Codes.Add(reader.GetString(3));
            }
        }

        return list.Select(type => type with
        {
            PrerequisiteAlternatives = alternatives
                .Where(pair => pair.Key.Code == type.Code)
                .Select(pair => new QualificationPrerequisiteGroup(pair.Key.GroupCode, pair.Value.Name, pair.Value.Codes))
                .ToArray(),
        }).ToArray();
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

    private async Task<IReadOnlyList<TrainingCourseCompletion>> ReadCourseCompletions(string where, CancellationToken ct, params object?[] args)
    {
        var result = new List<TrainingCourseCompletion>();
        await using var cmd = uow.Command($"SELECT completion_id, person_id, course_code, course_version, trainer_person_id, completed_at, expires_at, evidence FROM competency.training_course_completion {where} ORDER BY person_id, course_code, completed_at DESC", args);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new TrainingCourseCompletion(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetGuid(4), reader.GetFieldValue<DateTimeOffset>(5), reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6), reader.GetString(7)));
        return result;
    }

    private async Task<IReadOnlyList<PracticalAssessment>> ReadPracticalAssessments(string where, CancellationToken ct, params object?[] args)
    {
        var result = new List<PracticalAssessment>();
        await using var cmd = uow.Command($"SELECT assessment_id, person_id, qualification_code, machine_code, activity_code, assessor_person_id, passed, assessment_version, assessed_at, evidence, restrictions FROM competency.practical_assessment {where} ORDER BY person_id, assessed_at DESC", args);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new PracticalAssessment(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetGuid(5), reader.GetBoolean(6), reader.GetString(7), reader.GetFieldValue<DateTimeOffset>(8), reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10)));
        return result;
    }

    private async Task<IReadOnlyList<MachineAuthorization>> ReadMachineAuthorizations(string where, CancellationToken ct, params object?[] args)
    {
        var result = new List<MachineAuthorization>();
        await using var cmd = uow.Command($"SELECT a.person_id, a.machine_code, a.activity_code, a.qualification_code, a.status, a.independent_operation_allowed, a.authorized_at, a.expires_at, a.revision, a.course_completion_id, c.expires_at, a.assessment_id, a.evidence, a.restrictions, a.approved_by FROM competency.machine_authorization a JOIN competency.training_course_completion c ON c.completion_id = a.course_completion_id {where.Replace("person_id", "a.person_id", StringComparison.Ordinal)} ORDER BY a.person_id, a.machine_code, a.activity_code, a.qualification_code", args);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new MachineAuthorization(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), Enum.Parse<MachineAuthorizationStatus>(reader.GetString(4)), reader.GetBoolean(5), reader.GetFieldValue<DateTimeOffset>(6), reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7), reader.GetInt32(8), reader.GetGuid(9), reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10), reader.GetGuid(11), reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetString(13), reader.GetGuid(14).ToString("D")));
        return result;
    }

    private static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text };
}

public static class CompetencyRegistration
{
    public static IServiceCollection AddForgeCompetency(this IServiceCollection services)
    {
        services.AddForgeMigrations(typeof(CompetencyRegistration).Assembly, "competency");
        services.AddForgeStore<ICompetencyStore>(uow => new PostgresCompetencyStore(uow));
        services.AddForgeStore<ICompetencyLookup>(uow => new PostgresCompetencyStore(uow));
        services.AddSingleton<CompetencyService>();
        services.AddForgeStore<IRefresherPolicyStore>(uow => new PostgresRefresherPolicyStore(uow));
        services.AddForgeStore<IRefresherPolicyLookup>(uow => new PostgresRefresherPolicyStore(uow));
        services.AddSingleton<RefresherPolicyService>();
        return services;
    }
}
