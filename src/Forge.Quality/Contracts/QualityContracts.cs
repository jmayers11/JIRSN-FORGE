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
/// <param name="Revision">The item revision the plan is for; null for every revision (V2.3).</param>
/// <param name="PlannedMinutes">How long the inspection is planned to take (ADR 0057 §2): a gap in the order's flow before the next operation.</param>
public sealed record InspectionPlanInfo(string PartNumber, int Sequence, string Title, IReadOnlyList<CharacteristicInfo> Characteristics, string? Revision = null, int PlannedMinutes = 0);

/// <summary>Which plans apply to work at a revision: a plan for that revision, otherwise the plan for every revision.</summary>
public static class InspectionPlans
{
    public static IReadOnlyList<InspectionPlanInfo> For(IEnumerable<InspectionPlanInfo> plans, string partNumber, string? revision)
    {
        ArgumentNullException.ThrowIfNull(plans);
        return plans.Where(p => p.PartNumber == partNumber && (p.Revision is null || p.Revision == revision))
            .GroupBy(p => p.Sequence)
            .Select(g => g.FirstOrDefault(p => p.Revision is not null) ?? g.First())
            .OrderBy(p => p.Sequence)
            .ToList();
    }
}

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
    /// <summary>Accepted as it is, nonconforming; needs a second person's approval.</summary>
    UseAsIs,

    /// <summary>Brought back to the drawing by more work; once reworked it conforms.</summary>
    Rework,

    Scrap,
    ReturnToSupplier,

    /// <summary>
    /// Made usable by an approved repair, but it will not conform to the drawing (V3.5). Distinct from rework;
    /// like use-as-is, it needs a second person's approval.
    /// </summary>
    Repair,
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
    DateTimeOffset? ClosedAt,
    long? ParentNcrId = null);

/// <summary>Operator-reported suspected scrap or quality concern. This is evidence only; it is not an NCR disposition or inventory transaction.</summary>
public enum SuspectReportKind { Scrap, QualityIssue }

public sealed record SuspectScrapReport(
    long ReportId,
    SuspectReportKind Kind,
    long OrderNumber,
    int Sequence,
    string PartNumber,
    string? Revision,
    string? LotNumber,
    string? SerialNumber,
    decimal Quantity,
    string? MachineCode,
    Guid ReporterPersonId,
    string ReasonCode,
    string Description,
    DateTimeOffset ReportedAt,
    long? NcrId = null);

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
    string? RootCause,
    string? Action,
    DateOnly DueDate,
    CapaStatus Status,
    string OpenedBy,
    DateTimeOffset OpenedAt,
    string? ImplementedBy,
    string? ImplementationNote,
    string? VerifiedBy,
    DateTimeOffset? VerifiedAt,
    bool Governed = false);

/// <summary>A gauge as Quality shows it. Since V3.8 a gauge is a tool of the Gauge kind (Resources).</summary>
public sealed record GaugeInfo(string Code, string Description, DateOnly CalibrationDue, string? Certificate, bool Active)
{
    public bool InCalibrationOn(DateOnly day) => Active && day <= CalibrationDue;

    public static IReadOnlyList<GaugeInfo> From(IEnumerable<Forge.Resources.Contracts.ToolInfo> tools, IEnumerable<Forge.Resources.Contracts.ToolTypeInfo> types)
    {
        var gauges = types.Where(t => t.Kind == Forge.Resources.Contracts.ToolKind.Gauge).Select(t => t.Code).ToHashSet(StringComparer.Ordinal);
        return tools.Where(t => gauges.Contains(t.TypeCode))
            .Select(t => new GaugeInfo(t.Code, t.Description, t.CalibrationDue ?? DateOnly.MinValue, t.Certificate, t.Status != Forge.Resources.Contracts.ToolStatus.Retired))
            .ToList();
    }
}

/// <summary>Quality state of one order as readiness and receiving need it.</summary>
/// <param name="InspectionMinutes">Planned inspection minutes per inspection point (ADR 0057 §2); missing means 0.</param>
/// <param name="FailedSequences">Inspection points whose latest inspection failed (a re-inspection is due once its NCR is decided).</param>
public sealed record OrderQuality(long OrderNumber, IReadOnlyList<int> PlannedSequences, IReadOnlyList<int> PassedSequences, IReadOnlyList<NcrInfo> OpenNcrs,
    IReadOnlyDictionary<int, int>? InspectionMinutes = null, IReadOnlyList<int>? FailedSequences = null)
{
    /// <summary>The planned minutes of the inspection after an operation.</summary>
    public int MinutesAfter(int sequence) => InspectionMinutes?.GetValueOrDefault(sequence) ?? 0;
}

/// <summary>What other contexts may ask Quality.</summary>
public interface IQualityLookup
{
    Task<InspectionInfo?> FindInspectionAsync(long inspectionId, CancellationToken ct);
    Task<IReadOnlyList<InspectionPlanInfo>> ListPlansAsync(string? partNumber, CancellationToken ct);

    Task<IReadOnlyList<InspectionInfo>> ListInspectionsAsync(long? orderNumber, int take, CancellationToken ct);

    /// <summary>NCRs, newest first; only those not closed unless asked.</summary>
    Task<IReadOnlyList<NcrInfo>> ListNcrsAsync(bool includeClosed, CancellationToken ct);

    /// <summary>Inspections recorded in [from, to), oldest first, without characteristic results (for reports).</summary>
    Task<IReadOnlyList<InspectionInfo>> ListInspectionsBetweenAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    /// <summary>NCRs raised in [from, to), oldest first (for reports).</summary>
    Task<IReadOnlyList<NcrInfo>> ListNcrsRaisedBetweenAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    Task<NcrInfo?> FindNcrAsync(long ncrId, CancellationToken ct);

    Task<SuspectScrapReport?> FindSuspectScrapReportAsync(long reportId, CancellationToken ct);

    Task<IReadOnlyList<SuspectScrapReport>> ListSuspectScrapReportsAsync(long? orderNumber, CancellationToken ct);

    Task<IReadOnlyList<CapaInfo>> ListCapasAsync(bool includeVerified, CancellationToken ct);


    /// <summary>Inspection points of the order's part, operations with a passing inspection, and NCRs not yet dispositioned.</summary>
    Task<OrderQuality> GetOrderQualityAsync(long orderNumber, string partNumber, CancellationToken ct);
}

public interface ISuspectScrapReportStore
{
    Task<long> InsertSuspectScrapReportAsync(SuspectScrapReport report, CancellationToken ct);
    Task<bool> LinkSuspectScrapReportNcrAsync(long reportId, long ncrId, CancellationToken ct);
}

public enum ReworkKind
{
    /// <summary>More work brings it back to the drawing.</summary>
    Rework,

    /// <summary>An approved repair makes it usable, not conforming.</summary>
    Repair,

    /// <summary>Good output an NCR's scrap disposition takes back for good (Sprint 10 WW-S10-24): recorded closed, all of it scrapped.</summary>
    Scrap,
}

/// <summary>
/// Good output taken back from an order operation for rework or repair (V3.5). The production reports it came
/// from are never changed; closing records how much came out good and how much was scrapped.
/// </summary>
public sealed record ReworkInfo(
    long ReworkId,
    ReworkKind Kind,
    long OrderNumber,
    int Sequence,
    string PartNumber,
    decimal Quantity,
    long? NcrId,
    string? ReasonCode,
    string Instructions,
    DateTimeOffset OpenedAt,
    string OpenedBy,
    DateTimeOffset? ClosedAt = null,
    string? ClosedBy = null,
    decimal? GoodQuantity = null,
    decimal? ScrapQuantity = null,
    string? CloseNote = null)
{
    public bool Open => ClosedAt is null;
}

public interface IReworkLookup
{
    /// <summary>Rework of one order (all orders when null), oldest first; only open ones when asked.</summary>
    Task<IReadOnlyList<ReworkInfo>> ListReworkAsync(long? orderNumber, bool openOnly, CancellationToken ct);
}

public interface IReworkStore
{
    Task<long> InsertReworkAsync(ReworkInfo rework, CancellationToken ct);

    Task CloseReworkAsync(long reworkId, decimal good, decimal scrap, string note, DateTimeOffset at, string actor, CancellationToken ct);
}

public enum ReworkPlanStatus { Proposed, Approved, Rejected }

/// <summary>One step a rework plan adds to the order: work or an inspection, with its own labor standard and required qualifications.</summary>
public sealed record ReworkPlanOperation(
    int Sequence,
    string Description,
    string WorkCenterCode,
    string ActivityCode,
    IReadOnlyList<string> EligibleMachines,
    IReadOnlyList<string> RequiredQualifications,
    decimal SetupMinutes,
    decimal RunMinutesPerUnit,
    int CrewSize,
    bool Inspection);

/// <summary>Material the added work consumes: released demand on the order once the plan is approved.</summary>
public sealed record ReworkPlanMaterial(string PartNumber, decimal Quantity, int ConsumedAtSequence);

/// <summary>
/// What carrying out a rework record adds to its order (Sprint 10 WW-S10-14/29): operations, inspection steps, material and the people
/// to train. Proposed by one person and approved by another; approval adds the lines to the order and asks for the training. The NCR
/// disposition and the rework record are never changed by a plan.
/// </summary>
public sealed record ReworkPlan(
    Guid PlanId,
    long ReworkId,
    long OrderNumber,
    ReworkPlanStatus Status,
    IReadOnlyList<ReworkPlanOperation> Operations,
    IReadOnlyList<ReworkPlanMaterial> Materials,
    IReadOnlyList<Guid> Trainees,
    string Rationale,
    string ProposedBy,
    Guid? ProposedByPersonId,
    DateTimeOffset ProposedAt,
    string? DecidedBy = null,
    Guid? DecidedByPersonId = null,
    DateTimeOffset? DecidedAt = null,
    string? DecisionNote = null,
    int Version = 1);

public interface IReworkPlanStore
{
    Task<IReadOnlyList<ReworkPlan>> ListPlansAsync(long? orderNumber, CancellationToken ct);

    Task<ReworkPlan?> FindPlanAsync(Guid planId, CancellationToken ct);

    /// <summary>False when the rework already has a proposed plan.</summary>
    Task<bool> InsertPlanAsync(ReworkPlan plan, CancellationToken ct);

    /// <summary>Decides a proposed plan; false when someone else changed it first.</summary>
    Task<bool> DecidePlanAsync(ReworkPlan plan, int expectedVersion, CancellationToken ct);
}
