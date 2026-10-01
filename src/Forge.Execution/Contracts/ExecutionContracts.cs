namespace Forge.Execution.Contracts;

/// <summary>
/// Production order lifecycle implemented in Phase 3. InProgress, Completed and Closed arrive with
/// shop-floor execution (Phase 5).
/// </summary>
public enum OrderStatus
{
    Planned,
    FirmPlanned,
    Released,
    Held,
    Cancelled,
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
    OperationStatus Status);

/// <summary>A material requirement, copied from the BOM with the component revision in force at release.</summary>
public sealed record OrderMaterialInfo(int LineNumber, string ComponentPartNumber, string? ComponentRevision, decimal RequiredQuantity, string UnitOfMeasure);

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
    string? ReleasedBy = null);

public interface IExecutionLookup
{
    Task<OrderInfo?> FindOrderAsync(long orderNumber, CancellationToken ct);

    /// <summary>Orders without their operations and materials, newest first.</summary>
    Task<IReadOnlyList<OrderInfo>> ListOrdersAsync(CancellationToken ct);
}
