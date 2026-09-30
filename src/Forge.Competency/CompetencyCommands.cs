using Forge.Competency.Contracts;
using Forge.Kernel.Events;
using Forge.Kernel.Identity;

namespace Forge.Competency;

public sealed record DefineQualificationType(string Code, string Name, QualificationKind Kind, int? ValidityDays);

public sealed record AddPrerequisite(string Code, string PrerequisiteCode);

public sealed record StartTraining(PersonId PersonId, string Code);

public sealed record CertifyQualification(PersonId PersonId, string Code, string Evidence);

public sealed record SuspendQualification(PersonId PersonId, string Code);

public sealed record RevokeQualification(PersonId PersonId, string Code);

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

    /// <summary>All prerequisite edges, for cycle detection.</summary>
    Task<IReadOnlyList<(string Code, string Prerequisite)>> ListPrerequisitesAsync(CancellationToken ct);

    Task<PersonQualification?> FindAsync(PersonId person, string code, CancellationToken ct);

    Task UpsertAsync(PersonQualification record, string actor, string? evidence, DateTimeOffset at, CancellationToken ct);
}
