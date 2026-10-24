namespace Forge.Execution.Contracts;

/// <summary>
/// Production order lifecycle. An order becomes Completed when its last operation is reported
/// complete on the shop floor.
/// </summary>
public enum OrderStatus
{
    Planned,
    FirmPlanned,
    Released,
    Held,
    Cancelled,
    Completed,
}

public enum OperationStatus
{
    Pending,
    InProgress,
    Complete,
}

/// <summary>An order operation, copied from the routing when the order is released.</summary>
public sealed record OrderOperationInfo(
    int Sequence,
    string Description,
    string WorkCenterCode,
    string ActivityCode,
    IReadOnlyList<string> EligibleMachines,
    IReadOnlyList<string> RequiredQualifications,
    decimal PlannedMinutes,
    int CrewSize,
    OperationStatus Status,
    decimal GoodQuantity = 0,
    decimal ScrapQuantity = 0,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null,
    decimal? SetupMinutes = null,
    string? SetupFamily = null,
    decimal? FamilySetupMinutes = null);

/// <summary>A material requirement, copied from the BOM with the component revision in force at release.</summary>
/// <param name="ConsumedAtSequence">The operation that consumes the component (Sprint 10 §17), copied from the BOM at release; null means from the first operation.</param>
public sealed record OrderMaterialInfo(int LineNumber, string ComponentPartNumber, string? ComponentRevision, decimal RequiredQuantity, string UnitOfMeasure, int? ConsumedAtSequence = null);

public sealed record OrderInfo(
    long OrderNumber,
    string PartNumber,
    string Revision,
    decimal Quantity,
    DateOnly DueDate,
    int Priority,
    OrderStatus Status,
    OrderStatus? HeldFrom,
    Guid? ProgramId,
    DateTimeOffset? ReleasedAt,
    IReadOnlyList<OrderOperationInfo> Operations,
    IReadOnlyList<OrderMaterialInfo> Materials,
    string? CreatedBy = null,
    string? ReleasedBy = null,
    string? WbsCode = null,
    long? ContractId = null,
    string? ContractClin = null);

/// <summary>Who was on an operation, in what role. Direct labor: not attendance and not signoff.</summary>
public enum LaborRole
{
    Operator,
    Trainee,
    Trainer,
}

/// <summary>The production phase recorded for an operator interval. Training remains separately visible.</summary>
public enum LaborPhase
{
    Setup,
    Run,
    Inspection,
    Rework,
    Training,
}

public sealed record LaborEntry(
    long LaborId,
    Guid PersonId,
    long OrderNumber,
    int Sequence,
    string WorkCenterCode,
    string? MachineCode,
    LaborRole Role,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    long? TrainingSessionId,
    IReadOnlyList<string>? Qualifications = null,
    LaborPhase Phase = LaborPhase.Run);

/// <summary>Quantity reported against an operation. Scrap always carries a reason.</summary>
public sealed record ProductionReport(
    long ReportId,
    long OrderNumber,
    int Sequence,
    string WorkCenterCode,
    Guid PersonId,
    decimal GoodQuantity,
    decimal ScrapQuantity,
    string? ScrapReason,
    bool CompletedOperation,
    bool FinalOperation,
    DateTimeOffset ReportedAt,
    string? ScrapReasonCode = null);

/// <summary>A trainer's evaluation of a supervised session. It never grants a qualification.</summary>
public enum TrainingEvaluation
{
    NeedsMoreTraining,
    ReadyForAssessment,
}

public sealed record TrainingSession(
    long SessionId,
    Guid TraineeId,
    Guid TrainerId,
    IReadOnlyList<string> Qualifications,
    long OrderNumber,
    int Sequence,
    string? MachineCode,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    TrainingEvaluation? Evaluation,
    DateTimeOffset? EvaluatedAt,
    string? EvaluationNotes);

/// <summary>A released, unfinished operation: the shop floor's work queue.</summary>
public sealed record OpenOperation(long OrderNumber, string PartNumber, decimal OrderQuantity, DateOnly DueDate, int Priority, Guid? ProgramId, OrderOperationInfo Operation);

/// <summary>
/// Something another context says must stop work on an order. <see cref="AfterSequence"/> null holds
/// every operation; otherwise operations after that sequence wait (an inspection after it has not passed).
/// </summary>
public sealed record WorkHold(string Source, string Reference, int? AfterSequence, string Detail)
{
    public bool Blocks(int sequence) => AfterSequence is not { } after || sequence > after;
}

/// <summary>
/// Holds placed on orders by other contexts (Quality). Execution owns this port so it never depends on
/// them; they implement it. Starting and reporting work check it.
/// </summary>
public interface IWorkHoldLookup
{
    Task<IReadOnlyList<WorkHold>> ListHoldsAsync(long orderNumber, string partNumber, CancellationToken ct);
}

public interface IExecutionLookup
{
    /// <summary>Operations of released orders that are not complete, by order then sequence.</summary>
    Task<IReadOnlyList<OpenOperation>> ListOpenOperationsAsync(CancellationToken ct);

    Task<IReadOnlyList<LaborEntry>> ListOpenLaborAsync(CancellationToken ct);

    /// <summary>Labor overlapping [from, to), or every entry of one order when <paramref name="orderNumber"/> is given.</summary>
    Task<IReadOnlyList<LaborEntry>> ListLaborAsync(DateTimeOffset from, DateTimeOffset to, long? orderNumber, CancellationToken ct);

    Task<IReadOnlyList<ProductionReport>> ListReportsAsync(DateTimeOffset from, DateTimeOffset to, long? orderNumber, CancellationToken ct);

    Task<TrainingSession?> FindTrainingSessionAsync(long sessionId, CancellationToken ct);

    Task<IReadOnlyList<TrainingSession>> ListTrainingSessionsAsync(DateTimeOffset from, DateTimeOffset to, long? orderNumber, CancellationToken ct);

    Task<OrderInfo?> FindOrderAsync(long orderNumber, CancellationToken ct);

    /// <summary>Orders without their operations and materials, newest first.</summary>
    Task<IReadOnlyList<OrderInfo>> ListOrdersAsync(CancellationToken ct);
}

public enum AsBuiltKind
{
    Issued,
    Returned,
}

/// <summary>
/// One line of an order's as-built record (V2.11), written when a component is issued to (or returned from) the
/// order, with what was true then: the component revision, lot or serial, the approved alternate it was issued
/// through and who approved it, the supplier and purchase order it was bought on. Never changed afterwards.
/// </summary>
/// <param name="Serial">The lot number is the component's serial number.</param>
/// <param name="AlternateConditions">The conditions the alternate was approved under, as they read at issue.</param>
public sealed record AsBuiltLine(
    long AsBuiltId,
    long OrderNumber,
    long TransactionId,
    AsBuiltKind Kind,
    string ComponentPart,
    string? ComponentRevision,
    string? LotNumber,
    bool Serial,
    decimal Quantity,
    string? SubstitutesFor,
    decimal? PrimaryQuantity,
    long? AlternateId,
    string? AlternateKind,
    string? AlternateApprovedBy,
    DateTimeOffset? AlternateApprovedAt,
    string? AlternateConditions,
    string? SupplierCode,
    string? SupplierName,
    long? PoNumber,
    int? PoLine,
    DateTimeOffset? ReceivedAt,
    DateTimeOffset RecordedAt,
    string RecordedBy);

/// <summary>The as-built record (V2.11). Execution owns it; Inventory appends to it in the same transaction as the issue.</summary>
public interface IAsBuiltRecord
{
    Task AppendAsync(AsBuiltLine line, CancellationToken ct);

    /// <summary>Lines of one order, or the lines that used one lot or serial of a component (where-used).</summary>
    Task<IReadOnlyList<AsBuiltLine>> ListAsync(long? orderNumber, string? componentPart, string? lotNumber, CancellationToken ct);
}
