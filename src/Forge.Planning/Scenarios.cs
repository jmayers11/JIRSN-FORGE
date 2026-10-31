using Forge.Kernel.Identity;

namespace Forge.Planning;

public enum ScenarioChangeKind
{
    MachineUnavailable,
    PersonUnavailable,
    PriorityChange,
    OperationMachine,
    Overtime,
    SupplierDelay,
    AddOperator,
    AddTrainer,
}

public sealed record ScenarioChange(
    ScenarioChangeKind Kind,
    string? MachineCode = null,
    Guid? PersonId = null,
    long? OrderNumber = null,
    int? Sequence = null,
    int? Priority = null,
    string? WorkCenterCode = null,
    string? ActivityCode = null,
    DateTimeOffset? StartsAt = null,
    int? DurationHours = null,
    int? DelayDays = null,
    IReadOnlyList<RecoverySelection>? RecoverySelections = null);

/// <summary>
/// Explicit inputs for one independent recovery trial. Each trial starts from the same private
/// scenario; selections are never commands and are not combined into an implicitly approved plan.
/// Quantities use the selected source item's units. Dates and identities are checked against source records.
/// </summary>
public sealed record RecoverySelection(
    string OptionId,
    long? OrderNumber = null,
    int? Sequence = null,
    string? MachineCode = null,
    string? WorkCenterCode = null,
    Guid? PersonId = null,
    string? ShiftCode = null,
    int? AdditionalMinutes = null,
    int? Priority = null,
    string? PartNumber = null,
    string? LotNumber = null,
    string? FromLocation = null,
    string? ToLocation = null,
    decimal? Quantity = null,
    long? AlternateId = null,
    string? PrimaryPartNumber = null,
    long? PurchaseOrder = null,
    int? PurchaseOrderLine = null,
    DateOnly? ProposedDate = null,
    long? DowntimeId = null,
    DateTimeOffset? StartsAt = null,
    DateTimeOffset? EndsAt = null,
    long? ShipmentId = null,
    long? SourceOrderNumber = null,
    string? ProposedShiftCode = null,
    IReadOnlyList<Guid>? PersonIds = null,
    long? WorkOrderId = null,
    string? SpecificationDocument = null,
    string? SpecificationRevision = null,
    IReadOnlyList<decimal>? SplitQuantities = null);

public sealed record CreatePlanningScenario(string Name, ScenarioChange Change);
public sealed record DeletePlanningScenario(Guid ScenarioId);

public sealed record PlanningScenarioInfo(
    Guid ScenarioId,
    PersonId Owner,
    string Name,
    ScenarioChange Change,
    DateTimeOffset CreatedAt,
    DateTimeOffset ChangedAt);

public sealed record ScenarioOrderImpact(
    long OrderNumber,
    string PartNumber,
    string Change,
    DateTimeOffset? BaselineCompletion,
    DateTimeOffset? ProposedCompletion,
    decimal? ShiftHours,
    string BaselineStatus,
    string ProposedStatus,
    IReadOnlyList<string> Evidence);

public sealed record ScenarioCapacityImpact(string Qualification, DateOnly Day, decimal BaselineHours, decimal ProposedHours, IReadOnlyList<string> People);

public sealed record RecoveryOption(
    string Id,
    string Title,
    bool Feasible,
    string Feasibility,
    IReadOnlyList<string> Consequences,
    string? ApplyPath,
    string RequiredPermission,
    string Status,
    IReadOnlyList<long> AffectedOrders,
    IReadOnlyList<ScenarioOrderImpact> Projections,
    IReadOnlyList<string> Constraints,
    IReadOnlyList<string> RequiredInputs,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<RecoveryMachineAlternative> MachineAlternatives)
{
    /// <summary>Real source commands and their scoped permission decisions; a trial never invokes them.</summary>
    public IReadOnlyList<RecoverySourceAuthority> SourceAuthority { get; init; } = [];

    /// <summary>Whole-plant comparison of the scenario baseline and this option's trial schedule, when a trial ran.</summary>
    public ScenarioPlantImpact? PlantImpact { get; init; }
}

public sealed record RecoverySourceAuthority(
    string SourceContext,
    string Command,
    string Permission,
    string ResourceType,
    string? ResourceId,
    bool Permitted,
    string CommitRequirement)
{
    public Guid? ProgramId { get; init; }
}

public sealed record RecoveryMachineAlternative(
    long OrderNumber,
    int Sequence,
    string MachineCode,
    bool Feasible,
    DateTimeOffset? ProjectedCompletion,
    IReadOnlyList<string> Constraints,
    IReadOnlyList<string> Evidence);

public sealed record PlanningScenarioResult(
    Guid ScenarioId,
    DateTimeOffset AsOf,
    int HorizonDays,
    string ScenarioDescription,
    IReadOnlyList<ScenarioOrderImpact> Orders,
    IReadOnlyList<ScenarioCapacityImpact> SkillsCapacity,
    IReadOnlyList<RecoveryOption> RecoveryOptions,
    string EvidenceNote)
{
    /// <summary>Whole-plant comparison of the baseline and scenario schedules (output, labor, set-up, WIP, commitments, standard cost).</summary>
    public ScenarioPlantImpact? PlantImpact { get; init; }
}

public interface IPlanningScenarioStore
{
    Task<Guid> InsertAsync(PlanningScenarioInfo scenario, CancellationToken ct);
    Task<PlanningScenarioInfo?> FindAsync(Guid scenarioId, CancellationToken ct);
    Task<IReadOnlyList<PlanningScenarioInfo>> ListAsync(PersonId owner, CancellationToken ct);
    Task DeleteAsync(Guid scenarioId, PersonId owner, CancellationToken ct);
}
