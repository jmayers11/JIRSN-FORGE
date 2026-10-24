namespace Forge.Workforce.Contracts;

/// <summary>
/// What a coded interval of a worker's time was (Sprint 10 §16.1). Operation labor, machine downtime and stoppages are already
/// recorded by their own contexts; coded time covers what those records do not, so every paid minute can be accounted for.
/// </summary>
public enum CodedTimeCode
{
    Break,
    Meeting,
    IndirectTask,
    Cleaning,
    Training,
    MaterialShortage,
    WaitingForInspection,
    QualityHold,
    MachineDowntime,
    Other,
}

/// <summary>Coded time counts only once someone other than the recorder approved it; corrections supersede, never overwrite.</summary>
public enum CodedTimeState
{
    PendingReview,
    Approved,
    Rejected,
    Superseded,
}

/// <param name="Paid">Whether the interval is paid time (an unpaid meal break is not).</param>
/// <param name="SupersedesEntryId">The approved entry this one corrects; that entry stays in history as Superseded once this is approved.</param>
public sealed record CodedTimeEntry(
    Guid EntryId,
    Guid PersonId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    CodedTimeCode Code,
    bool Paid,
    string? WorkCenterCode,
    string? MachineCode,
    long? OrderNumber,
    int? Sequence,
    string Note,
    CodedTimeState State,
    string RecordedBy,
    DateTimeOffset RecordedAt,
    string? ReviewedBy,
    DateTimeOffset? ReviewedAt,
    Guid? SupersedesEntryId)
{
    /// <summary>Material, inspection, quality-hold and machine waits are outside the worker's control (§16.1).</summary>
    public bool ExternalDelay => Code is CodedTimeCode.MaterialShortage or CodedTimeCode.WaitingForInspection or CodedTimeCode.QualityHold or CodedTimeCode.MachineDowntime;
}

public interface ICodedTimeLookup
{
    /// <summary>Entries that overlap [from, to), every state, oldest first.</summary>
    Task<IReadOnlyList<CodedTimeEntry>> ListCodedTimeAsync(Guid? personId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    Task<CodedTimeEntry?> FindCodedTimeAsync(Guid entryId, CancellationToken ct);
}
