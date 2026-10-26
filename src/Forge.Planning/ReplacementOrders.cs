using Forge.Kernel.Events;

namespace Forge.Planning;

public enum ReplacementOrderStatus { Proposed, Approved, Rejected }

/// <summary>A person who could run the work, from the workforce recovery check, with what moving them would leave uncovered.</summary>
public sealed record ReplacementCandidate(Guid PersonId, string PersonName, bool RequiresSourceBackfill, IReadOnlyList<string> SourceImpacts, IReadOnlyList<string> Evidence);

/// <summary>
/// One routing operation (on one eligible machine, or at its work center) of a proposed replacement order, checked when it was proposed:
/// whether it could run with the people there now, who else is qualified and authorized, and what each move would cost the source work.
/// </summary>
public sealed record ReplacementOperationCheck(
    int Sequence,
    string Description,
    string WorkCenterCode,
    string? MachineCode,
    string ActivityCode,
    bool ReadyNow,
    string? CurrentConstraint,
    IReadOnlyList<ReplacementCandidate> Candidates,
    IReadOnlyList<string> Limitations)
{
    /// <summary>Someone can run it: it is ready now, or at least one qualified person can be moved to it.</summary>
    public bool Covered => ReadyNow || Candidates.Count > 0;
}

/// <summary>
/// A proposed manufacturing order that replaces scrapped or lost production (Sprint 10 WW-S10-35). It carries the operator and
/// source-center check made when it was proposed; a different person approves or rejects it, and only approval creates a planned order.
/// </summary>
public sealed record ReplacementOrderProposal(
    Guid ProposalId,
    string PartNumber,
    string Revision,
    decimal Quantity,
    DateOnly DueDate,
    int Priority,
    long? NcrId,
    long? ReplacesOrderNumber,
    Guid? ProgramId,
    ReplacementOrderStatus Status,
    IReadOnlyList<ReplacementOperationCheck> Checks,
    DateTimeOffset CheckedAt,
    string Rationale,
    string ProposedBy,
    Guid? ProposedByPersonId,
    DateTimeOffset ProposedAt,
    string? DecidedBy = null,
    Guid? DecidedByPersonId = null,
    DateTimeOffset? DecidedAt = null,
    string? DecisionNote = null,
    long? OrderNumber = null,
    int Version = 1);

public interface IReplacementOrderStore
{
    Task<IReadOnlyList<ReplacementOrderProposal>> ListAsync(bool openOnly, CancellationToken ct);

    Task<ReplacementOrderProposal?> FindAsync(Guid proposalId, CancellationToken ct);

    Task InsertAsync(ReplacementOrderProposal proposal, CancellationToken ct);

    /// <summary>Records the decision when the proposal is still open at <paramref name="expectedVersion"/>; false when someone decided first.</summary>
    Task<bool> DecideAsync(ReplacementOrderProposal proposal, int expectedVersion, CancellationToken ct);
}

/// <summary>A replacement order was proposed; planners who may approve it are told.</summary>
public sealed record ReplacementOrderProposed(Guid ProposalId, string PartNumber, decimal Quantity, string Message) : IDomainEvent
{
    public static string EventType => nameof(ReplacementOrderProposed);

    public static int SchemaVersion => 1;
}

/// <summary>A replacement order proposal was approved (and its planned order created) or rejected; clears the planners' notice.</summary>
public sealed record ReplacementOrderDecided(Guid ProposalId, string Status, long? OrderNumber, string Message) : IDomainEvent
{
    public static string EventType => nameof(ReplacementOrderDecided);

    public static int SchemaVersion => 1;
}
