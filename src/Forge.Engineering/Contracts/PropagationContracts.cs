namespace Forge.Engineering.Contracts;

public enum PropagationStatus
{
    Running,
    Complete,

    /// <summary>Released, but a step failed or waits for the change's effective date. Retried; shown until it matches.</summary>
    Incomplete,
}

public enum PropagationStepKind
{
    /// <summary>A future or released-not-started order moved to the new revision.</summary>
    MoveOrder,

    /// <summary>A released-not-started order's documentation reissued with the revisions that apply now.</summary>
    ReissueDocumentation,

    /// <summary>A work-in-progress order: a disposition action raised, the order itself untouched.</summary>
    WipDisposition,

    /// <summary>An open purchase order line: a review action raised, the line and its package untouched.</summary>
    PurchaseReview,

    /// <summary>A person who works where a changed instruction is used: an acknowledgment action raised.</summary>
    TrainingAcknowledgment,

    /// <summary>A completed order: checked to be left exactly as it was.</summary>
    CompletedUnchanged,
}

public enum PropagationStepStatus
{
    Pending,
    Done,
    Failed,

    /// <summary>Waits for the change's effective date before it can be done.</summary>
    Waiting,
}

public enum ChangeActionKind
{
    WipDisposition,
    PurchaseReview,
    TrainingAcknowledgment,
}

public enum ChangeActionStatus
{
    Open,
    Resolved,
}

/// <summary>How a work-in-progress order is dispositioned after a change: decided by quality, never an edit to the order.</summary>
public enum WipResolution
{
    /// <summary>Finish at the old revision (with a deviation or concession reference).</summary>
    CompleteAtOldRevision,

    /// <summary>Rework the parts to the new revision.</summary>
    ReworkToNewRevision,

    /// <summary>Scrap the work.</summary>
    Scrap,

    /// <summary>Cancel the order and plan a new one at the new revision.</summary>
    ReplanAtNewRevision,
}

/// <summary>What the buyer decided for an open purchase order line after a change.</summary>
public enum PurchaseResolution
{
    /// <summary>The change does not affect what the supplier makes.</summary>
    NoImpact,

    /// <summary>The supplier was sent the new revision (reference required).</summary>
    SupplierNotified,

    /// <summary>The line was amended or replaced through the purchasing flow.</summary>
    LineAmended,

    /// <summary>The line was cancelled.</summary>
    LineCancelled,
}

public enum AcknowledgmentResolution
{
    Acknowledged,
}

public sealed record PropagationStepInfo(
    string ChangeNumber,
    string StepKey,
    PropagationStepKind Kind,
    string Target,
    string? PartNumber,
    string Detail,
    PropagationStepStatus Status,
    int Attempts,
    string? Error,
    DateTimeOffset PlannedAt,
    DateTimeOffset? DoneAt);

public sealed record ChangePropagationInfo(
    string ChangeNumber,
    PropagationStatus Status,
    Guid CorrelationId,
    int Runs,
    DateTimeOffset StartedAt,
    DateTimeOffset? LastRunAt,
    DateTimeOffset? CompletedAt,
    string? LastError,
    IReadOnlyList<PropagationStepInfo> Steps);

public sealed record ChangeActionInfo(
    long ActionId,
    string ChangeNumber,
    ChangeActionKind Kind,
    string Target,
    string? PartNumber,
    string? DocumentNumber,
    string? Revision,
    Guid? PersonId,
    Guid? ProgramId,
    string Detail,
    ChangeActionStatus Status,
    string? Resolution,
    string? Notes,
    string RaisedBy,
    DateTimeOffset RaisedAt,
    string? ResolvedBy,
    DateTimeOffset? ResolvedAt);

/// <summary>Expected against actual, per kind of step (V2 verification record).</summary>
public sealed record VerificationLine(PropagationStepKind Kind, int Expected, int Done, int Waiting, int Failed);

public sealed record PropagationVerification(
    string ChangeNumber,
    PropagationStatus Status,
    IReadOnlyList<VerificationLine> Lines,
    int ActionsRaised,
    int ActionsOpen,
    int Unresolved,
    string Summary)
{
    public static PropagationVerification Of(ChangePropagationInfo propagation, IReadOnlyCollection<ChangeActionInfo> actions)
    {
        ArgumentNullException.ThrowIfNull(propagation);
        ArgumentNullException.ThrowIfNull(actions);
        var lines = Enum.GetValues<PropagationStepKind>()
            .Select(k => propagation.Steps.Where(s => s.Kind == k).ToList())
            .Where(s => s.Count > 0)
            .Select(s => new VerificationLine(s[0].Kind, s.Count, s.Count(x => x.Status == PropagationStepStatus.Done), s.Count(x => x.Status == PropagationStepStatus.Waiting), s.Count(x => x.Status == PropagationStepStatus.Failed)))
            .ToList();
        var unresolved = propagation.Steps.Count(s => s.Status != PropagationStepStatus.Done);
        var open = actions.Count(a => a.Status == ChangeActionStatus.Open);
        var summary = propagation.Status == PropagationStatus.Complete
            ? $"Propagation complete: {propagation.Steps.Count} step(s) verified; {actions.Count} action(s) raised, {open} still open."
            : $"Propagation incomplete: {unresolved} of {propagation.Steps.Count} step(s) not done{(propagation.LastError is { } e ? $" (last error: {e})" : string.Empty)}.";
        return new PropagationVerification(propagation.ChangeNumber, propagation.Status, lines, actions.Count, open, unresolved, summary);
    }
}

public interface IPropagationLookup
{
    Task<ChangePropagationInfo?> FindPropagationAsync(string changeNumber, CancellationToken ct);

    Task<IReadOnlyList<ChangePropagationInfo>> ListIncompleteAsync(CancellationToken ct);

    Task<IReadOnlyList<ChangeActionInfo>> ListActionsAsync(string? changeNumber, bool openOnly, CancellationToken ct);

    Task<ChangeActionInfo?> FindActionAsync(long actionId, CancellationToken ct);
}
