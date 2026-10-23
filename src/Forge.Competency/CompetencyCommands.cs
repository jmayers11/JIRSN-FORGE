using Forge.Competency.Contracts;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;

namespace Forge.Competency;

public sealed record DefineQualificationType(string Code, string Name, QualificationKind Kind, int? ValidityDays);

public sealed record AddPrerequisite(string Code, string PrerequisiteCode);

public sealed record DefinePrerequisiteAlternatives(string Code, string GroupCode, string Name, IReadOnlyList<string> PrerequisiteCodes);

public sealed record StartTraining(PersonId PersonId, string Code);

public sealed record CertifyQualification(PersonId PersonId, string Code, string Evidence);

public sealed record SuspendQualification(PersonId PersonId, string Code);

public sealed record RevokeQualification(PersonId PersonId, string Code);

public sealed record RecordTrainingCourseCompletion(PersonId PersonId, string CourseCode, string CourseVersion,
    DateTimeOffset? ExpiresAt, string Evidence);

public sealed record RecordPracticalAssessment(PersonId PersonId, string QualificationCode, string MachineCode,
    string ActivityCode, bool Passed, string AssessmentVersion, string Evidence, string? Restrictions);

public sealed record AuthorizeMachineOperation(PersonId PersonId, string MachineCode, string ActivityCode,
    string QualificationCode, Guid AssessmentId, DateTimeOffset? ExpiresAt, string Evidence, string? Restrictions, bool IndependentOperationAllowed);

public sealed record ChangeMachineAuthorization(PersonId PersonId, string MachineCode, string ActivityCode,
    string QualificationCode, MachineAuthorizationStatus Status);

public sealed record TrainingCourseCompletionRecorded(Guid CompletionId, Guid PersonId, string CourseCode, string CourseVersion, Guid TrainerPersonId, DateTimeOffset? ExpiresAt) : IDomainEvent
{
    public static string EventType => nameof(TrainingCourseCompletionRecorded);
    public static int SchemaVersion => 1;
}

public sealed record PracticalAssessmentRecorded(Guid AssessmentId, Guid PersonId, string QualificationCode, string MachineCode, string ActivityCode, Guid AssessorPersonId, bool Passed, string AssessmentVersion) : IDomainEvent
{
    public static string EventType => nameof(PracticalAssessmentRecorded);
    public static int SchemaVersion => 1;
}

public sealed record MachineAuthorizationChanged(Guid PersonId, string MachineCode, string ActivityCode, string QualificationCode, string Status, bool IndependentOperationAllowed, int Revision, Guid CourseCompletionId, Guid AssessmentId) : IDomainEvent
{
    public static string EventType => nameof(MachineAuthorizationChanged);
    public static int SchemaVersion => 1;
}

public sealed record QualificationTypeDefined(string Code, string Name, string Kind, int? ValidityDays) : IDomainEvent
{
    public static string EventType => nameof(QualificationTypeDefined);

    public static int SchemaVersion => 1;
}

public sealed record QualificationPrerequisiteAdded(string Code, string PrerequisiteCode) : IDomainEvent
{
    public static string EventType => nameof(QualificationPrerequisiteAdded);

    public static int SchemaVersion => 1;
}

public sealed record QualificationPrerequisiteAlternativesDefined(string Code, string GroupCode, string Name, IReadOnlyList<string> PrerequisiteCodes) : IDomainEvent
{
    public static string EventType => nameof(QualificationPrerequisiteAlternativesDefined);

    public static int SchemaVersion => 1;
}

public sealed record TrainingStarted(Guid PersonId, string Code) : IDomainEvent
{
    public static string EventType => nameof(TrainingStarted);

    public static int SchemaVersion => 1;
}

public sealed record QualificationGranted(Guid PersonId, string Code, DateTimeOffset? ExpiresAt, string CertifiedBy) : IDomainEvent
{
    public static string EventType => nameof(QualificationGranted);

    public static int SchemaVersion => 1;
}

public sealed record QualificationSuspended(Guid PersonId, string Code) : IDomainEvent
{
    public static string EventType => nameof(QualificationSuspended);

    public static int SchemaVersion => 1;
}

public sealed record QualificationRevoked(Guid PersonId, string Code) : IDomainEvent
{
    public static string EventType => nameof(QualificationRevoked);

    public static int SchemaVersion => 1;
}

/// <summary>Competency's own store, used by its commands.</summary>
public interface ICompetencyStore
{
    Task<QualificationTypeInfo?> FindTypeAsync(string code, CancellationToken ct);

    Task InsertTypeAsync(QualificationTypeInfo type, DateTimeOffset at, string actor, CancellationToken ct);

    Task<bool> AddPrerequisiteAsync(string code, string prerequisite, CancellationToken ct);

    Task DefinePrerequisiteAlternativesAsync(string code, string groupCode, string name, IReadOnlyList<string> prerequisites, string actor, DateTimeOffset at, CancellationToken ct);

    /// <summary>All prerequisite edges, for cycle detection.</summary>
    Task<IReadOnlyList<(string Code, string Prerequisite)>> ListPrerequisitesAsync(CancellationToken ct);

    Task<PersonQualification?> FindAsync(PersonId person, string code, CancellationToken ct);

    Task UpsertAsync(PersonQualification record, string actor, string? evidence, DateTimeOffset at, CancellationToken ct);

    Task InsertTrainingCourseCompletionAsync(TrainingCourseCompletion completion, CancellationToken ct) => Task.FromException(new NotSupportedException());

    Task InsertPracticalAssessmentAsync(PracticalAssessment assessment, CancellationToken ct) => Task.FromException(new NotSupportedException());

    Task<MachineAuthorization?> FindMachineAuthorizationAsync(PersonId person, string machineCode, string activityCode, string qualificationCode, CancellationToken ct) => Task.FromResult<MachineAuthorization?>(null);

    Task UpsertMachineAuthorizationAsync(MachineAuthorization authorization, string reason, string actor, DateTimeOffset at, CancellationToken ct) => Task.FromException(new NotSupportedException());
}
