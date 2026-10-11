namespace Forge.Quality.Contracts;

/// <summary>What a determination answers (V3.11). First article inspection is the first question.</summary>
public enum DeterminationQuestion
{
    Fai,
}

/// <summary>Whether something applies, as decided. Until someone decides, it is pending.</summary>
public enum DeterminationState
{
    Required,
    NotRequired,
    NotApplicable,
    PendingDetermination,
}

/// <summary>Where first article inspection stands for a part revision from a supplier (V3.11). Pending is never complete.</summary>
public enum FaiStatus
{
    /// <summary>No supplier (not bought), so FAI does not apply.</summary>
    NotApplicable,
    NotRequired,

    /// <summary>Nobody has decided yet whether FAI is required.</summary>
    PendingDetermination,

    /// <summary>Required, and no passing FAI is recorded yet.</summary>
    Required,

    /// <summary>Required, and a passing FAI backed by a released report is recorded.</summary>
    Complete,
}

public sealed record Determination(long DeterminationId, DeterminationQuestion Question, string Subject, DeterminationState State, string Basis, string DecidedBy, DateTimeOffset DecidedAt);

/// <summary>One measured or checked characteristic of an inspection.</summary>
public sealed record ReceivingCharacteristicResult(string Characteristic, string? Requirement, string? Measured, bool Pass);

public sealed record FaiRecord(
    long FaiId,
    string Subject,
    string PartNumber,
    string PartRevision,
    string SupplierCode,
    bool Passed,
    IReadOnlyList<ReceivingCharacteristicResult> Results,
    string DocumentNumber,
    string DocumentRevision,
    string RecordedBy,
    DateTimeOffset RecordedAt);

/// <summary>The FAI subject of a part revision from a supplier, and how its status is worked out.</summary>
public static class Fai
{
    public static string Subject(string partNumber, string revision, string supplierCode) => $"{partNumber}:{revision}@{supplierCode}";

    /// <summary>The status from the determination in force and the FAIs recorded (latest first).</summary>
    public static FaiStatus StatusOf(string? supplierCode, Determination? determination, IReadOnlyList<FaiRecord> fais)
    {
        ArgumentNullException.ThrowIfNull(fais);
        if (supplierCode is null)
        {
            return FaiStatus.NotApplicable;
        }

        return determination?.State switch
        {
            null or DeterminationState.PendingDetermination => FaiStatus.PendingDetermination,
            DeterminationState.NotRequired => FaiStatus.NotRequired,
            DeterminationState.NotApplicable => FaiStatus.NotApplicable,
            _ => fais.Count > 0 && fais[0].Passed ? FaiStatus.Complete : FaiStatus.Required,
        };
    }

    public static bool AllowsAcceptance(FaiStatus status) => status is FaiStatus.NotApplicable or FaiStatus.NotRequired or FaiStatus.Complete;

    public static string Explain(FaiStatus status, string subject) => status switch
    {
        FaiStatus.PendingDetermination => $"FAI for {subject} is pending determination: quality has not decided whether it is required.",
        FaiStatus.Required => $"FAI for {subject} is required and no passing FAI is recorded.",
        FaiStatus.Complete => $"FAI for {subject} passed.",
        FaiStatus.NotRequired => $"FAI for {subject} is not required.",
        _ => $"FAI does not apply to {subject}.",
    };
}

public interface IDeterminationLookup
{
    /// <summary>Determinations for a question and subject (or all), latest first.</summary>
    Task<IReadOnlyList<Determination>> ListDeterminationsAsync(DeterminationQuestion? question, string? subject, CancellationToken ct);

    /// <summary>FAIs for a subject (or all), latest first.</summary>
    Task<IReadOnlyList<FaiRecord>> ListFaisAsync(string? subject, CancellationToken ct);
}

public interface IDeterminationStore
{
    Task<long> InsertDeterminationAsync(Determination determination, CancellationToken ct);

    Task<long> InsertFaiAsync(FaiRecord fai, CancellationToken ct);
}


