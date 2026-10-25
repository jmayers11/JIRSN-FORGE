using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Kernel.Engines;
using Forge.Quality.Contracts;

namespace Forge.Planning.Engines;

/// <summary>
/// The order to evaluate, its revision as engineering sees it now, and (for a released order) its
/// material allocation and quality state. Without them, material and quality are listed as not evaluated.
/// </summary>
/// <param name="Documentation">The order's documentation (V2.3). When the item class is not enforced, Documentation is listed as not evaluated.</param>
/// <param name="PointOfUse">Stock at points of use (V3.9); with material, the first operation checks material is there.</param>
/// <param name="Training">The training policy and the document revisions that need training (V3.9).</param>
public sealed record OrderReadinessRequest(
    OrderInfo Order,
    RevisionInfo? RevisionNow,
    OrderMaterialStatus? Material = null,
    OrderQuality? Quality = null,
    OrderDocumentation? Documentation = null,
    OrderPointOfUse? PointOfUse = null,
    OrderTraining? Training = null,
    WipContinuationEvidence? Continuation = null);

/// <summary>How one eligible machine (or the work center, for work without a machine) looks for an operation.</summary>
public sealed record OperationCandidate(string? MachineCode, bool Executable, string? PrimaryConstraint, IReadOnlyList<ReadinessDimension> Dimensions);

public sealed record OperationReadiness(
    int Sequence,
    string Description,
    bool Executable,
    string? PrimaryConstraint,
    string? BestMachine,
    IReadOnlyList<ReadinessDimension> Dimensions,
    IReadOnlyList<OperationCandidate> Candidates,
    IReadOnlyList<string> NotEvaluated);

public sealed record OrderReadinessResult(long OrderNumber, string Status, IReadOnlyList<OperationReadiness> Operations, string Summary);

/// <summary>
/// Readiness of each operation of a production order, now. Unlike current-work readiness, the
/// workforce question is "could qualified people run this?", so any present, non-trainee person in
/// the safety area (or not yet assigned anywhere) counts as a candidate. Each eligible machine is
/// evaluated; the operation is executable if at least one machine is.
/// </summary>
public sealed class OrderReadinessEngine : IEngine<SiteSnapshot, OrderReadinessRequest, OrderReadinessResult>
{
    public static readonly IReadOnlyList<string> NotYetEvaluated = ["Material", "Quality"];

    public static readonly IReadOnlyList<string> NotYetEvaluatedWithMaterial = ["Quality"];

    public static readonly IReadOnlyList<string> NotYetEvaluatedWithQuality = ["Material"];

    public static readonly IReadOnlyList<string> NotYetEvaluatedWithBoth = [];

    // V3.9 adds Holds, PointOfUse, Tooling and Training. The order says which constraint is named first.
    private static readonly string[] Precedence =
        ["Order", "Holds", "Engineering", "Documentation", "Predecessor", "Quality", "Material", "PointOfUse", "RequirementProfile", "Equipment", "Tooling", "Safety", "Workforce", "Training"];

    private readonly PersonnelCompositionEngine composition = new();

    public string Name => "order-readiness";

    public const string CurrentVersion = "7";
    public string Version => CurrentVersion;

    public EngineEvaluation<OrderReadinessResult> Evaluate(SiteSnapshot snapshot, OrderReadinessRequest parameters, DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(parameters);
        var order = parameters.Order;

        var orderDimension = order.Status == OrderStatus.Released
            ? new ReadinessDimension("Order", EvidenceOutcome.Pass, $"Order {order.OrderNumber} is released.")
            : new ReadinessDimension("Order", EvidenceOutcome.Fail, $"Order {order.OrderNumber} is {order.Status}; only released orders can run.");

        var engineering = parameters.RevisionNow switch
        {
            null => new ReadinessDimension("Engineering", EvidenceOutcome.Fail, $"{order.PartNumber} rev {order.Revision} no longer exists."),
            { } r when r.IsEffectiveAt(asOf) => new ReadinessDimension("Engineering", EvidenceOutcome.Pass, $"{order.PartNumber} rev {order.Revision} is released and in force."),
            { } r when parameters.Continuation is { } continuation && continuation.Permits(order, r, asOf) => new ReadinessDimension(
                "Engineering", EvidenceOutcome.Pass,
                $"{order.PartNumber} rev {order.Revision}: released change {continuation.Change.Number}, WIP disposition {continuation.Action.ActionId} authorizes completing this exact order at its old revision."),
            { } r when r.EffectiveTo is { } to && to <= asOf => new ReadinessDimension(
                "Engineering",
                EvidenceOutcome.Fail,
                $"{order.PartNumber} rev {order.Revision} was superseded or withdrawn at {to:u}. Engineering must confirm the order may continue on its released snapshot."),
            _ => new ReadinessDimension("Engineering", EvidenceOutcome.Fail, $"{order.PartNumber} rev {order.Revision} is not in force."),
        };

        var started = order.Operations.Any(o => o.Status != OperationStatus.Pending);
        var material = parameters.Material switch
        {
            null => null,
            { State: MaterialState.Ready } m => new ReadinessDimension("Material", EvidenceOutcome.Pass, m.Summary),
            { } m when started => new ReadinessDimension("Material", EvidenceOutcome.Info, $"Work has started. Material still to issue: {m.Summary}"),
            { } m => new ReadinessDimension("Material", EvidenceOutcome.Fail, m.Summary),
        };
        var notEvaluated = (parameters.Material, parameters.Quality) switch
        {
            (null, null) => NotYetEvaluated,
            (not null, null) => NotYetEvaluatedWithMaterial,
            (null, not null) => NotYetEvaluatedWithQuality,
            _ => NotYetEvaluatedWithBoth,
        };
        if (parameters.Documentation is { Enforced: false })
        {
            notEvaluated = [.. notEvaluated, "Documentation"];
        }

        var operations = new List<OperationReadiness>();
        var nodes = new List<EvidenceNode>();
        foreach (var op in order.Operations.OrderBy(o => o.Sequence))
        {
            // Routed work waits only for earlier routed work; added rework work waits only for the earlier steps of the same rework (the output it
            // takes back was already produced). Neither holds the other up.
            var unfinished = order.Operations.Where(o => o.Sequence < op.Sequence && o.Status != OperationStatus.Complete && o.ReworkId == op.ReworkId).Select(o => o.Sequence).ToList();
            var predecessor = op.Status == OperationStatus.Complete
                ? new ReadinessDimension("Predecessor", EvidenceOutcome.Info, "Operation already complete.")
                : unfinished.Count == 0
                    ? new ReadinessDimension("Predecessor", EvidenceOutcome.Pass, "All earlier operations are complete.")
                    : new ReadinessDimension("Predecessor", EvidenceOutcome.Fail, $"Earlier operation(s) not complete: {string.Join(", ", unfinished)}.");

            var candidates = Candidates(snapshot, parameters, op, asOf).ToList();
            var best = candidates.OrderBy(c => c.Executable ? 0 : 1)
                .ThenBy(c => c.Dimensions.Count(d => d.Outcome is EvidenceOutcome.Fail or EvidenceOutcome.Indeterminate))
                .ThenBy(c => c.MachineCode ?? string.Empty, StringComparer.Ordinal)
                .First();

            var dimensions = new List<ReadinessDimension> { orderDimension, engineering, predecessor };
            if (parameters.Documentation is { Enforced: true } documentation && op.Status != OperationStatus.Complete)
            {
                dimensions.Add(documentation.DimensionFor(op.Sequence));
            }

            if (parameters.Quality is { } quality && op.Status != OperationStatus.Complete)
            {
                dimensions.Add(QualityCheck(quality, op.Sequence));
            }

            if (material is not null && op.Status != OperationStatus.Complete)
            {
                dimensions.Add(material);
            }

            // Material is used from the first operation; it must be at that operation's point of use before it starts.
            var first = order.Operations.Min(o => o.Sequence);
            if (op.Sequence == first && op.Status == OperationStatus.Pending && ExecutableChecks.PointOfUse(parameters.PointOfUse, parameters.Material, op.WorkCenterCode) is { } pointOfUse)
            {
                dimensions.Add(pointOfUse);
            }

            dimensions.AddRange(best.Dimensions);
            var failed = dimensions.Where(d => d.Outcome is EvidenceOutcome.Fail or EvidenceOutcome.Indeterminate).Select(d => d.Name).ToList();
            var executable = failed.Count == 0;
            var primary = Precedence.FirstOrDefault(failed.Contains);

            operations.Add(new OperationReadiness(op.Sequence, op.Description, executable, primary, best.MachineCode, dimensions, candidates, notEvaluated));
            nodes.Add(new EvidenceNode(
                "Operation",
                executable ? EvidenceOutcome.Pass : EvidenceOutcome.Fail,
                executable
                    ? $"Op {op.Sequence} {op.Description} can run" + (best.MachineCode is null ? "." : $" on {best.MachineCode}.")
                    : $"Op {op.Sequence} {op.Description} cannot run. Primary constraint: {primary}.")
            {
                Inputs = new(StringComparer.Ordinal) { ["sequence"] = op.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture), ["workCenter"] = op.WorkCenterCode, ["activity"] = op.ActivityCode },
                Children = dimensions.Select(d => new EvidenceNode(d.Name, d.Outcome, d.Detail)).ToList(),
            });
        }

        var next = operations.FirstOrDefault(o => order.Operations.First(x => x.Sequence == o.Sequence).Status != OperationStatus.Complete);
        var summary = operations.Count == 0
            ? $"Order {order.OrderNumber} has no operations (is it released?)."
            : next is null
                ? $"Order {order.OrderNumber}: all operations complete."
                : next.Executable
                    ? $"Order {order.OrderNumber}: next operation {next.Sequence} can run now."
                    : $"Order {order.OrderNumber}: next operation {next.Sequence} is blocked by {next.PrimaryConstraint}.";

        // The order's outcome is the outcome of its next unfinished operation.
        var outcome = next is null ? EvidenceOutcome.Info : next.Executable ? EvidenceOutcome.Pass : EvidenceOutcome.Fail;
        var root = new EvidenceNode("OrderReadiness", outcome, summary) { Children = nodes };
        return new EngineEvaluation<OrderReadinessResult>(new OrderReadinessResult(order.OrderNumber, order.Status.ToString(), operations, summary), root);
    }

    /// <summary>
    /// Quality holds an operation when an earlier inspection point has no passing inspection, or when
    /// the order has a nonconformance nobody has dispositioned yet (spec §45).
    /// </summary>
    public static ReadinessDimension QualityCheck(OrderQuality quality, int sequence)
    {
        ArgumentNullException.ThrowIfNull(quality);
        var problems = new List<string>();
        var uninspected = quality.PlannedSequences.Where(s => s < sequence && !quality.PassedSequences.Contains(s)).Order().ToList();
        if (uninspected.Count > 0)
        {
            problems.Add($"Inspection after operation {string.Join(", ", uninspected)} has not passed.");
        }

        if (quality.OpenNcrs.Count > 0)
        {
            problems.Add(string.Join(" ", quality.OpenNcrs.Select(n => $"NCR {n.NcrId} ({n.Defect}) is {(n.Status == NcrStatus.AwaitingApproval ? $"waiting for {(n.Disposition == Disposition.Repair ? "repair" : "use-as-is")} approval" : "not dispositioned")}.")));
        }

        if (problems.Count > 0)
        {
            return new ReadinessDimension("Quality", EvidenceOutcome.Fail, string.Join(" ", problems));
        }

        var passed = quality.PlannedSequences.Where(s => s < sequence).ToList();
        return new ReadinessDimension("Quality", EvidenceOutcome.Pass, passed.Count == 0
            ? "No inspection is due before this operation and no nonconformance is open."
            : $"Inspection after operation {string.Join(", ", passed)} passed; no nonconformance is open.");
    }

    private IEnumerable<OperationCandidate> Candidates(SiteSnapshot snapshot, OrderReadinessRequest request, OrderOperationInfo op, DateTimeOffset asOf)
    {
        var workCenter = snapshot.WorkCenters.FirstOrDefault(w => w.Code == op.WorkCenterCode);
        if (workCenter is not { Active: true })
        {
            yield return new OperationCandidate(null, false, "RequirementProfile", [new ReadinessDimension("RequirementProfile", EvidenceOutcome.Fail, $"Work center {op.WorkCenterCode} no longer exists.")]);
            yield break;
        }

        if (op.EligibleMachines.Count == 0)
        {
            yield return Candidate(snapshot, request, op, workCenter.SafetyAreaCode, null, [], false, asOf);
            yield break;
        }

        foreach (var machineCode in op.EligibleMachines.Order(StringComparer.Ordinal))
        {
            if (!snapshot.Machines.Any(m => m.Code == machineCode && m.Active))
            {
                yield return new OperationCandidate(machineCode, false, "Equipment", [new ReadinessDimension("Equipment", EvidenceOutcome.Fail, $"{machineCode} is inactive or no longer exists.")]);
                continue;
            }
            var profile = snapshot.MachineActivities.FirstOrDefault(m => m.MachineCode == machineCode && m.ActivityCode == op.ActivityCode);
            if (profile is null)
            {
                yield return new OperationCandidate(machineCode, false, "RequirementProfile", [new ReadinessDimension("RequirementProfile", EvidenceOutcome.Fail, $"{op.ActivityCode} is no longer defined for {machineCode}.")]);
                continue;
            }

            yield return Candidate(snapshot, request, op, workCenter.SafetyAreaCode, machineCode, profile.OperatorQualifications, profile.RequiresMachineAuthorization, asOf);
        }
    }

    private OperationCandidate Candidate(SiteSnapshot snapshot, OrderReadinessRequest request, OrderOperationInfo op, string area, string? machine, IReadOnlyList<string> operatorQualifications, bool requiresMachineAuthorization, DateTimeOffset asOf)
    {
        var dimensions = new List<ReadinessDimension>();
        if (op.Status != OperationStatus.Complete)
        {
            dimensions.Add(ExecutableChecks.Holds(snapshot, request.Order.OrderNumber, request.Order.PartNumber, op.Sequence, op.WorkCenterCode, machine, request.Order.ProgramId));
        }

        if (machine is not null)
        {
            dimensions.Add(new ReadinessDimension("RequirementProfile", EvidenceOutcome.Pass, $"{op.ActivityCode} on {machine} requires one of {string.Join(", ", operatorQualifications)}{(requiresMachineAuthorization ? " and a current machine-specific authorization" : string.Empty)}."));
            var equipment = OperationReadinessEngine.EquipmentCheck(snapshot, machine, asOf);
            dimensions.Add(new ReadinessDimension("Equipment", equipment.Outcome, equipment.Detail));
        }

        if (op.Status != OperationStatus.Complete)
        {
            dimensions.Add(ExecutableChecks.Tooling(snapshot, machine, op.ActivityCode, asOf));
        }

        // Candidates: present, not a trainee, in this safety area or not yet assigned anywhere.
        var qualified = snapshot.People
            .Where(p => p.CountsAsPresent && p.Assignment?.AsTrainee != true && (p.Assignment is null || p.Assignment.SafetyAreaCode == area))
            .Where(p => operatorQualifications.Count == 0 || operatorQualifications.Any(code => p.HoldsAt(code, asOf)))
            .Where(p => !requiresMachineAuthorization || (machine is not null && operatorQualifications.Any(code => p.HoldsAt(code, asOf)
                && p.AuthorizedForMachineAt(machine, op.ActivityCode, code, asOf))))
            .Where(p => op.RequiredQualifications.All(code => p.HoldsAt(code, asOf)))
            .OrderBy(p => p.PersonId)
            .ToList();
        var requirement = string.Join(" and ", new[]
        {
            operatorQualifications.Count == 0 ? null : "one of " + string.Join(", ", operatorQualifications),
            op.RequiredQualifications.Count == 0 ? null : string.Join(", ", op.RequiredQualifications),
        }.Where(s => s is not null));
        dimensions.Add(qualified.Count >= op.CrewSize
            ? new ReadinessDimension("Workforce", EvidenceOutcome.Pass, $"{qualified.Count} qualified and present (crew {op.CrewSize}): {string.Join(", ", qualified.Select(p => p.Name))}.")
            : new ReadinessDimension(
                "Workforce",
                EvidenceOutcome.Fail,
                $"Needs a crew of {op.CrewSize}{(requirement.Length == 0 ? string.Empty : " holding " + requirement)}; {qualified.Count} qualified and present in {area}" + (qualified.Count == 0 ? "." : $": {string.Join(", ", qualified.Select(p => p.Name))}.")));

        if (op.Status != OperationStatus.Complete && ExecutableChecks.Training(request.Training, op.Sequence, qualified, op.CrewSize) is { } training)
        {
            dimensions.Add(training);
        }

        var safety = composition.Evaluate(snapshot, new WorkItem(area, op.WorkCenterCode, machine, op.ActivityCode), asOf);
        dimensions.Add(new ReadinessDimension("Safety", safety.Evidence.Outcome, safety.Output.Status == CompositionStatus.NotSatisfied
            ? safety.Evidence.Detail + " " + string.Join(" ", safety.Output.Rules.SelectMany(r => r.Shortfalls))
            : safety.Evidence.Detail));

        var failed = dimensions.Where(d => d.Outcome is EvidenceOutcome.Fail or EvidenceOutcome.Indeterminate).Select(d => d.Name).ToList();
        return new OperationCandidate(machine, failed.Count == 0, Precedence.FirstOrDefault(failed.Contains), dimensions);
    }
}
