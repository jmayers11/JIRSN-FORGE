using Forge.Kernel.Identity;

namespace Forge.Competency.Contracts;

public enum QualificationKind
{
    General,
    Process,
    Machine,
    SpecialProcess,
    Safety,
    Trainer,
}

public enum QualificationStatus
{
    /// <summary>Training has started. Grants no capability.</summary>
    InTraining,

    /// <summary>Certified by an authorized certifier. Valid until it expires, is suspended or revoked.</summary>
    Qualified,

    Suspended,
    Revoked,
}

/// <summary>A set of qualifications for which holding any one member satisfies this prerequisite group.</summary>
public sealed record QualificationPrerequisiteGroup(string GroupCode, string Name, IReadOnlyList<string> QualificationCodes);

public sealed record QualificationTypeInfo(
    string Code,
    string Name,
    QualificationKind Kind,
    int? ValidityDays,
    IReadOnlyList<string> Prerequisites,
    IReadOnlyList<QualificationPrerequisiteGroup>? PrerequisiteAlternatives = null);

public sealed record PersonQualification(Guid PersonId, string Code, QualificationStatus Status, DateTimeOffset? CertifiedAt, DateTimeOffset? ExpiresAt)
{
    /// <summary>Only a certified, unexpired qualification counts. Expiry is evaluated, never stored as a flag.</summary>
    public bool IsValidAt(DateTimeOffset at) => Status == QualificationStatus.Qualified && (ExpiresAt is null || at < ExpiresAt);
}

public sealed record TrainingCourseCompletion(Guid CompletionId, Guid PersonId, string CourseCode, string CourseVersion,
    Guid TrainerPersonId, DateTimeOffset CompletedAt, DateTimeOffset? ExpiresAt, string Evidence);

public sealed record PracticalAssessment(Guid AssessmentId, Guid PersonId, string QualificationCode, string MachineCode,
    string ActivityCode, Guid AssessorPersonId, bool Passed, string AssessmentVersion, DateTimeOffset AssessedAt,
    string Evidence, string? Restrictions);

public enum MachineAuthorizationStatus { Authorized, Suspended, Revoked }

public sealed record MachineAuthorization(Guid PersonId, string MachineCode, string ActivityCode, string QualificationCode,
    MachineAuthorizationStatus Status, bool IndependentOperationAllowed, DateTimeOffset AuthorizedAt, DateTimeOffset? ExpiresAt, int Revision,
    Guid CourseCompletionId, DateTimeOffset? CourseExpiresAt, Guid AssessmentId, string Evidence, string? Restrictions, string ApprovedBy)
{
    public bool IsValidAt(DateTimeOffset at) => Status == MachineAuthorizationStatus.Authorized && IndependentOperationAllowed
        && (ExpiresAt is null || at < ExpiresAt) && (CourseExpiresAt is null || at < CourseExpiresAt);
}

/// <summary>Machine activity requirements supplied by the Resources owner during competency commands.</summary>
public sealed record CompetencyMachineActivity(string MachineCode, string ActivityCode,
    IReadOnlyList<string> OperatorQualifications, bool RequiresMachineAuthorization);

/// <summary>Read-only machine requirement context used to validate practical assessments and authorization.</summary>
public interface ICompetencyMachineContext
{
    Task<CompetencyMachineActivity?> FindCompetencyMachineActivityAsync(string machineCode, string activityCode, CancellationToken ct);
}

/// <summary>What other contexts may ask Competency. Available through the unit of work.</summary>
public interface ICompetencyLookup
{
    Task<bool> QualificationTypeExistsAsync(string code, CancellationToken ct);

    Task<IReadOnlyList<QualificationTypeInfo>> ListTypesAsync(CancellationToken ct);

    /// <summary>All qualification records (any status) for the given people, sorted by person then code.</summary>
    Task<IReadOnlyList<PersonQualification>> GetQualificationsAsync(IReadOnlyCollection<PersonId> people, CancellationToken ct);

    Task<IReadOnlyList<MachineAuthorization>> GetMachineAuthorizationsAsync(IReadOnlyCollection<PersonId> people, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MachineAuthorization>>([]);

    Task<IReadOnlyList<TrainingCourseCompletion>> GetTrainingCourseCompletionsAsync(IReadOnlyCollection<PersonId> people, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TrainingCourseCompletion>>([]);

    Task<IReadOnlyList<PracticalAssessment>> GetPracticalAssessmentsAsync(IReadOnlyCollection<PersonId> people, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PracticalAssessment>>([]);
}
