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

public sealed record QualificationTypeInfo(string Code, string Name, QualificationKind Kind, int? ValidityDays, IReadOnlyList<string> Prerequisites);

public sealed record PersonQualification(Guid PersonId, string Code, QualificationStatus Status, DateTimeOffset? CertifiedAt, DateTimeOffset? ExpiresAt)
{
    /// <summary>Only a certified, unexpired qualification counts. Expiry is evaluated, never stored as a flag.</summary>
    public bool IsValidAt(DateTimeOffset at) => Status == QualificationStatus.Qualified && (ExpiresAt is null || at < ExpiresAt);
}

/// <summary>What other contexts may ask Competency. Available through the unit of work.</summary>
public interface ICompetencyLookup
{
    Task<bool> QualificationTypeExistsAsync(string code, CancellationToken ct);

    Task<IReadOnlyList<QualificationTypeInfo>> ListTypesAsync(CancellationToken ct);

    /// <summary>All qualification records (any status) for the given people, sorted by person then code.</summary>
    Task<IReadOnlyList<PersonQualification>> GetQualificationsAsync(IReadOnlyCollection<PersonId> people, CancellationToken ct);
}
