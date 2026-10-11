using Forge.Kernel.Access;
using Forge.Kernel.Identity;
using Forge.Kernel.Events;
using Forge.Kernel.Security;

namespace Forge.Execution.Contracts;

public sealed record ShiftChecklistItem(string Code, string Description, bool Required, int SortOrder, bool Active = true);
public sealed record ShiftChecklistEntry(string Code, string Description, bool Required, string State, string? Note, string? RecordedBy, DateTimeOffset? RecordedAt);
public sealed record ShiftChecklistResponse(string Code, string State, string? Note);
public sealed record ShiftOperationalFact(string Kind, string Reference, string Status, string Summary, Guid? ProgramId = null,
    decimal? GoodQuantity = null, decimal? ScrapQuantity = null, bool? CompletedOperation = null);
public sealed record ShiftProductionTotals(int ReportCount, decimal GoodQuantity, decimal ScrapQuantity, int CompletedOperations, int OpenOperations);
public sealed record ShiftCloseoutFacts(
    string WorkCenterCode,
    string ShiftCode,
    DateTimeOffset From,
    DateTimeOffset Until,
    DateTimeOffset AsOf,
    ShiftProductionTotals Production,
    IReadOnlyList<ShiftOperationalFact> Facts,
    IReadOnlyList<string> Unknowns);

public sealed record ShiftHandoff(
    long HandoffId,
    string WorkCenterCode,
    string ShiftCode,
    DateTimeOffset ShiftFrom,
    DateTimeOffset ShiftUntil,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    string? Context,
    DateTimeOffset? ReviewedAt,
    string? ReviewedBy,
    ShiftCloseoutFacts Snapshot,
    IReadOnlyList<ShiftChecklistEntry> Checklist,
    IReadOnlyList<ShiftOperationalFact> Facts);

public enum ShiftActionStatus { Assigned, Acknowledged, InProgress, Completed }
public sealed record ShiftAction(long ActionId, long HandoffId, string Description, string? SourceKind, string? SourceReference, Guid Assignee, ShiftActionStatus Status, DateTimeOffset CreatedAt, string CreatedBy,
    DateTimeOffset? AcknowledgedAt, string? AcknowledgedBy, DateTimeOffset? InProgressAt, string? InProgressBy, DateTimeOffset? CompletedAt, string? CompletedBy, Guid? ProgramId);
public sealed record ShiftAcknowledgment(long HandoffId, Guid PersonId, DateTimeOffset AcknowledgedAt, string? Note);
public sealed record ShiftCloseout(long CloseoutId, string WorkCenterCode, string ShiftCode, DateTimeOffset ShiftFrom, DateTimeOffset ShiftUntil, DateTimeOffset ClosedAt, string ClosedBy, ShiftCloseoutFacts Facts, string? Note);

public interface IShiftContinuityStore
{
    Task<IReadOnlyList<ShiftChecklistItem>> ListChecklistAsync(string workCenter, CancellationToken ct);
    Task<bool> SaveChecklistItemAsync(string workCenter, ShiftChecklistItem item, DateTimeOffset at, string actor, CancellationToken ct);
    Task<IReadOnlyList<ShiftHandoff>> ListHandoffsAsync(string? workCenter, int limit, CancellationToken ct);
    Task<ShiftHandoff?> FindHandoffAsync(long handoffId, CancellationToken ct);
    Task<long?> InsertHandoffAsync(string workCenter, string shiftCode, DateTimeOffset from, DateTimeOffset until, DateTimeOffset at, string actor, string? context, ShiftCloseoutFacts snapshot, IReadOnlyList<ShiftChecklistEntry> checklist, CancellationToken ct);
    Task<bool> MarkReviewedAsync(long handoffId, DateTimeOffset at, string actor, CancellationToken ct);
    Task<bool> InsertAcknowledgmentAsync(ShiftAcknowledgment acknowledgment, CancellationToken ct);
    Task<IReadOnlyList<ShiftAcknowledgment>> ListAcknowledgmentsAsync(long handoffId, CancellationToken ct);
    Task<long> InsertActionAsync(long handoffId, string description, string? sourceKind, string? sourceReference, Guid assignee, Guid? programId, DateTimeOffset at, string actor, CancellationToken ct);
    Task<ShiftAction?> FindActionAsync(long actionId, CancellationToken ct);
    Task<bool> UpdateActionStatusAsync(long actionId, ShiftActionStatus expected, ShiftActionStatus status, DateTimeOffset at, string actor, CancellationToken ct);
    Task<IReadOnlyList<ShiftAction>> ListActionsAsync(long handoffId, CancellationToken ct);
    Task<long?> InsertCloseoutAsync(string workCenter, string shiftCode, DateTimeOffset from, DateTimeOffset until, DateTimeOffset at, string actor, ShiftCloseoutFacts facts, string? note, CancellationToken ct);
    Task<IReadOnlyList<ShiftCloseout>> ListCloseoutsAsync(string? workCenter, int limit, CancellationToken ct);
}

/// <summary>Planning composes authoritative facts from a single kernel snapshot; Execution persists the handoff.</summary>
public interface IShiftFactsProvider
{
    Task<ShiftCloseoutFacts> BuildAsync(IKernelUnitOfWork uow, string workCenter, string shiftCode, DateTimeOffset from, DateTimeOffset until, ForgePrincipal principal, CancellationToken ct);
}

public sealed record ConfigureShiftChecklistItem(string WorkCenterCode, string Code, string Description, bool Required, int SortOrder, bool Active);
public sealed record CreateShiftHandoff(string WorkCenterCode, string ShiftCode, DateTimeOffset ShiftFrom, DateTimeOffset ShiftUntil, string? Context, IReadOnlyList<ShiftChecklistResponse>? Checklist = null);
public sealed record ReviewShiftHandoff(long HandoffId);
public sealed record AcknowledgeShiftHandoff(long HandoffId, string? Note);
public sealed record AssignShiftAction(long HandoffId, string Description, string? SourceKind, string? SourceReference, Guid Assignee);
public sealed record TransitionShiftAction(long ActionId, ShiftActionStatus Expected, ShiftActionStatus Status);
public sealed record CloseShift(string WorkCenterCode, string ShiftCode, DateTimeOffset ShiftFrom, DateTimeOffset ShiftUntil, string? Note);

public sealed record ShiftHandoffCreated(long HandoffId, string WorkCenterCode, string ShiftCode, DateTimeOffset ShiftFrom, DateTimeOffset ShiftUntil) : IDomainEvent
{
    public static string EventType => nameof(ShiftHandoffCreated);
    public static int SchemaVersion => 1;
}
public sealed record ShiftHandoffReviewed(long HandoffId) : IDomainEvent
{
    public static string EventType => nameof(ShiftHandoffReviewed);
    public static int SchemaVersion => 1;
}
public sealed record ShiftHandoffAcknowledged(long HandoffId, Guid PersonId) : IDomainEvent
{
    public static string EventType => nameof(ShiftHandoffAcknowledged);
    public static int SchemaVersion => 1;
}
public sealed record ShiftActionChanged(long HandoffId, long ActionId, ShiftActionStatus Status) : IDomainEvent
{
    public static string EventType => nameof(ShiftActionChanged);
    public static int SchemaVersion => 1;
}
public sealed record ShiftClosed(long CloseoutId, string WorkCenterCode, string ShiftCode, DateTimeOffset ShiftFrom, DateTimeOffset ShiftUntil) : IDomainEvent
{
    public static string EventType => nameof(ShiftClosed);
    public static int SchemaVersion => 1;
}
public sealed record ShiftChecklistChanged(string WorkCenterCode, string Code, bool Active) : IDomainEvent
{
    public static string EventType => nameof(ShiftChecklistChanged);
    public static int SchemaVersion => 1;
}

