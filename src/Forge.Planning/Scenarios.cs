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
    int? DelayDays = null);

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
    IReadOnlyList<RecoveryMachineAlternative> MachineAlternatives);

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
    string EvidenceNote);

public interface IPlanningScenarioStore
{
    Task<Guid> InsertAsync(PlanningScenarioInfo scenario, CancellationToken ct);
    Task<PlanningScenarioInfo?> FindAsync(Guid scenarioId, CancellationToken ct);
    Task<IReadOnlyList<PlanningScenarioInfo>> ListAsync(PersonId owner, CancellationToken ct);
    Task DeleteAsync(Guid scenarioId, PersonId owner, CancellationToken ct);
}
