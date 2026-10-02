namespace Forge.Quality.Contracts;

public enum CharacteristicKind
{
    /// <summary>Measured against limits with a calibrated gauge.</summary>
    Variable,

    /// <summary>Pass or fail by inspection (visual, go/no-go).</summary>
    Attribute,
}

/// <summary>What is checked at an operation. Variable characteristics have limits; at least one limit is set.</summary>
public sealed record CharacteristicInfo(int Number, string Name, CharacteristicKind Kind, decimal? Nominal, decimal? Lower, decimal? Upper, string? Unit);

/// <summary>An inspection point: after operation <see cref="Sequence"/> of a part, check these characteristics.</summary>
public sealed record InspectionPlanInfo(string PartNumber, int Sequence, string Title, IReadOnlyList<CharacteristicInfo> Characteristics);

public enum InspectionResult
{
    Pass,
    Fail,
}

/// <summary>One characteristic as inspected: the limits in force at the time and what was found.</summary>
public sealed record CharacteristicResult(int Number, string Name, CharacteristicKind Kind, decimal? Lower, decimal? Upper, decimal? Measured, bool? Accepted, bool Pass);

public sealed record InspectionInfo(
    long InspectionId,
    long OrderNumber,
    int Sequence,
    string PartNumber,
    decimal QuantityInspected,
    InspectionResult Result,
    string? GaugeCode,
    string? Notes,
    string Inspector,
    DateTimeOffset At,
    IReadOnlyList<CharacteristicResult> Results,
    long? NcrId);

public enum NcrStatus
{
    /// <summary>Raised; nothing decided yet. Blocks the order's work.</summary>
    Open,

    /// <summary>A use-as-is disposition is waiting for a second person.</summary>
    AwaitingApproval,

    /// <summary>Disposition decided (and approved where needed); the work it calls for is being done.</summary>
    Dispositioned,

    Closed,
}

public enum Disposition
{
    UseAsIs,
    Rework,
    Scrap,
    ReturnToSupplier,
}

public sealed record NcrInfo(
    long NcrId,
    string PartNumber,
    long? OrderNumber,
    int? Sequence,
    string? LotNumber,
    decimal Quantity,
    string Defect,
    string Description,
    NcrStatus Status,
    Disposition? Disposition,
    string? DispositionNote,
    string RaisedBy,
    DateTimeOffset RaisedAt,
    string? DispositionedBy,
    string? ApprovedBy,
    string? ClosedBy,
    string? ClosureNote,
    DateTimeOffset? ClosedAt);

public enum CapaStatus
{
    Open,
    Implemented,
    Verified,
}

public sealed record CapaInfo(
    long CapaId,
    long? NcrId,
    string Title,
    string RootCause,
    string Action,
    DateOnly DueDate,
    CapaStatus Status,
    string OpenedBy,
    DateTimeOffset OpenedAt,
    string? ImplementedBy,
    string? ImplementationNote,
    string? VerifiedBy,
    DateTimeOffset? VerifiedAt);

public sealed record GaugeInfo(string Code, string Description, DateOnly CalibrationDue, string? Certificate, bool Active)
{
    public bool InCalibrationOn(DateOnly day) => Active && day <= CalibrationDue;
}

/// <summary>Quality state of one order as readiness and receiving need it.</summary>
public sealed record OrderQuality(long OrderNumber, IReadOnlyList<int> PlannedSequences, IReadOnlyList<int> PassedSequences, IReadOnlyList<NcrInfo> OpenNcrs);

/// <summary>What other contexts may ask Quality.</summary>
public interface IQualityLookup
{
    Task<IReadOnlyList<InspectionPlanInfo>> ListPlansAsync(string? partNumber, CancellationToken ct);

    Task<IReadOnlyList<InspectionInfo>> ListInspectionsAsync(long? orderNumber, int take, CancellationToken ct);

    /// <summary>NCRs, newest first; only those not closed unless asked.</summary>
    Task<IReadOnlyList<NcrInfo>> ListNcrsAsync(bool includeClosed, CancellationToken ct);

    /// <summary>Inspections recorded in [from, to), oldest first, without characteristic results (for reports).</summary>
    Task<IReadOnlyList<InspectionInfo>> ListInspectionsBetweenAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    /// <summary>NCRs raised in [from, to), oldest first (for reports).</summary>
    Task<IReadOnlyList<NcrInfo>> ListNcrsRaisedBetweenAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    Task<NcrInfo?> FindNcrAsync(long ncrId, CancellationToken ct);

    Task<IReadOnlyList<CapaInfo>> ListCapasAsync(bool includeVerified, CancellationToken ct);

    Task<IReadOnlyList<GaugeInfo>> ListGaugesAsync(CancellationToken ct);

    /// <summary>Inspection points of the order's part, operations with a passing inspection, and NCRs not yet dispositioned.</summary>
    Task<OrderQuality> GetOrderQualityAsync(long orderNumber, string partNumber, CancellationToken ct);
}
