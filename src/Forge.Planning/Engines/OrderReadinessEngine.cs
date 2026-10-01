using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Kernel.Engines;

namespace Forge.Planning.Engines;

/// <summary>The order to evaluate, and its revision as engineering sees it now.</summary>
public sealed record OrderReadinessRequest(OrderInfo Order, RevisionInfo? RevisionNow);

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
    public static readonly IReadOnlyList<string> NotYetEvaluated = ["Material", "Tooling", "Quality"];

    private static readonly string[] Precedence = ["Order", "Engineering", "Predecessor", "RequirementProfile", "Equipment", "Safety", "Workforce"];

    private readonly PersonnelCompositionEngine composition = new();

    public string Name => "order-readiness";

    public string Version => "1";

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
            { } r when r.EffectiveTo is { } to && to <= asOf => new ReadinessDimension(
                "Engineering",
                EvidenceOutcome.Fail,
                $"{order.PartNumber} rev {order.Revision} was superseded or withdrawn at {to:u}. Engineering must confirm the order may continue on its released snapshot."),
            _ => new ReadinessDimension("Engineering", EvidenceOutcome.Fail, $"{order.PartNumber} rev {order.Revision} is not in force."),
        };

        var operations = new List<OperationReadiness>();
        var nodes = new List<EvidenceNode>();
        foreach (var op in order.Operations.OrderBy(o => o.Sequence))
        {
            var unfinished = order.Operations.Where(o => o.Sequence < op.Sequence && o.Status != OperationStatus.Complete).Select(o => o.Sequence).ToList();
            var predecessor = op.Status == OperationStatus.Complete
                ? new ReadinessDimension("Predecessor", EvidenceOutcome.Info, "Operation already complete.")
                : unfinished.Count == 0
                    ? new ReadinessDimension("Predecessor", EvidenceOutcome.Pass, "All earlier operations are complete.")
                    : new ReadinessDimension("Predecessor", EvidenceOutcome.Fail, $"Earlier operation(s) not complete: {string.Join(", ", unfinished)}.");

            var candidates = Candidates(snapshot, op, asOf).ToList();
            var best = candidates.OrderBy(c => c.Executable ? 0 : 1)
                .ThenBy(c => c.Dimensions.Count(d => d.Outcome is EvidenceOutcome.Fail or EvidenceOutcome.Indeterminate))
                .ThenBy(c => c.MachineCode ?? string.Empty, StringComparer.Ordinal)
                .First();

            var dimensions = new List<ReadinessDimension> { orderDimension, engineering, predecessor };
            dimensions.AddRange(best.Dimensions);
            var failed = dimensions.Where(d => d.Outcome is EvidenceOutcome.Fail or EvidenceOutcome.Indeterminate).Select(d => d.Name).ToList();
            var executable = failed.Count == 0;
            var primary = Precedence.FirstOrDefault(failed.Contains);

            operations.Add(new OperationReadiness(op.Sequence, op.Description, executable, primary, best.MachineCode, dimensions, candidates, NotYetEvaluated));
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

    private IEnumerable<OperationCandidate> Candidates(SiteSnapshot snapshot, OrderOperationInfo op, DateTimeOffset asOf)
    {
        var workCenter = snapshot.WorkCenters.FirstOrDefault(w => w.Code == op.WorkCenterCode);
        if (workCenter is null)
        {
            yield return new OperationCandidate(null, false, "RequirementProfile", [new ReadinessDimension("RequirementProfile", EvidenceOutcome.Fail, $"Work center {op.WorkCenterCode} no longer exists.")]);
            yield break;
        }

        if (op.EligibleMachines.Count == 0)
        {
            yield return Candidate(snapshot, op, workCenter.SafetyAreaCode, null, [], asOf);
            yield break;
        }

        foreach (var machineCode in op.EligibleMachines.Order(StringComparer.Ordinal))
        {
            var profile = snapshot.MachineActivities.FirstOrDefault(m => m.MachineCode == machineCode && m.ActivityCode == op.ActivityCode);
            if (profile is null)
            {
                yield return new OperationCandidate(machineCode, false, "RequirementProfile", [new ReadinessDimension("RequirementProfile", EvidenceOutcome.Fail, $"{op.ActivityCode} is no longer defined for {machineCode}.")]);
                continue;
            }

            yield return Candidate(snapshot, op, workCenter.SafetyAreaCode, machineCode, profile.OperatorQualifications, asOf);
        }
    }

    private OperationCandidate Candidate(SiteSnapshot snapshot, OrderOperationInfo op, string area, string? machine, IReadOnlyList<string> operatorQualifications, DateTimeOffset asOf)
    {
        var dimensions = new List<ReadinessDimension>();
        if (machine is not null)
        {
            dimensions.Add(new ReadinessDimension("RequirementProfile", EvidenceOutcome.Pass, $"{op.ActivityCode} on {machine} requires one of {string.Join(", ", operatorQualifications)}."));
            var equipment = OperationReadinessEngine.EquipmentCheck(snapshot, machine, asOf);
            dimensions.Add(new ReadinessDimension("Equipment", equipment.Outcome, equipment.Detail));
        }

        // Candidates: present, not a trainee, in this safety area or not yet assigned anywhere.
        var qualified = snapshot.People
            .Where(p => p.CountsAsPresent && p.Assignment?.AsTrainee != true && (p.Assignment is null || p.Assignment.SafetyAreaCode == area))
            .Where(p => operatorQualifications.Count == 0 || operatorQualifications.Any(p.Holds))
            .Where(p => op.RequiredQualifications.All(p.Holds))
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

        var safety = composition.Evaluate(snapshot, new WorkItem(area, op.WorkCenterCode, machine, op.ActivityCode), asOf);
        dimensions.Add(new ReadinessDimension("Safety", safety.Evidence.Outcome, safety.Output.Status == CompositionStatus.NotSatisfied
            ? safety.Evidence.Detail + " " + string.Join(" ", safety.Output.Rules.SelectMany(r => r.Shortfalls))
            : safety.Evidence.Detail));

        var failed = dimensions.Where(d => d.Outcome is EvidenceOutcome.Fail or EvidenceOutcome.Indeterminate).Select(d => d.Name).ToList();
        return new OperationCandidate(machine, failed.Count == 0, Precedence.FirstOrDefault(failed.Contains), dimensions);
    }
}
