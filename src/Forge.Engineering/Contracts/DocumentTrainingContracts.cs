namespace Forge.Engineering.Contracts;

/// <summary>What readiness does with training on controlled documents (V3.9).</summary>
public enum TrainingPolicy
{
    /// <summary>Training is not checked.</summary>
    Off,

    /// <summary>Untrained operators are named; work is not stopped.</summary>
    Warn,

    /// <summary>An operation is not executable until enough trained operators are present.</summary>
    Enforce,
}

/// <summary>The plant's training policy and the kinds of document it covers.</summary>
public sealed record DocumentTrainingPolicy(TrainingPolicy Policy, IReadOnlyList<DocumentType> DocumentTypes, DateTimeOffset ChangedAt, string ChangedBy);

/// <summary>A person trained on one revision of a controlled document.</summary>
public sealed record DocumentTraining(long TrainingId, Guid PersonId, string DocumentNumber, string Revision, DateOnly TrainedOn, DateTimeOffset RecordedAt, string RecordedBy)
{
    public string Key => $"{DocumentNumber}@{Revision}";
}

public interface IDocumentTrainingLookup
{
    Task<DocumentTrainingPolicy> GetTrainingPolicyAsync(CancellationToken ct);

    /// <summary>Training records, for some people or everyone, oldest first.</summary>
    Task<IReadOnlyList<DocumentTraining>> ListTrainingAsync(IReadOnlyCollection<Guid>? people, CancellationToken ct);
}

public interface IDocumentTrainingStore
{
    /// <summary>False when the person is already trained on that revision.</summary>
    Task<bool> InsertTrainingAsync(DocumentTraining training, CancellationToken ct);

    Task SetTrainingPolicyAsync(DocumentTrainingPolicy policy, CancellationToken ct);
}
