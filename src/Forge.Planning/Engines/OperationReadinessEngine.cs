using Forge.Kernel.Engines;

namespace Forge.Planning.Engines;

public sealed record ReadinessDimension(string Name, EvidenceOutcome Outcome, string Detail);

/// <summary>
/// Whether a piece of work can run now, dimension by dimension. There is no percentage score.
/// <see cref="NotEvaluated"/> names dimensions Forge cannot check yet; they are never reported as passing.
/// </summary>
public sealed record ReadinessResult(
    WorkItem Work,
    bool Executable,
    string? PrimaryConstraint,
    IReadOnlyList<ReadinessDimension> Dimensions,
    IReadOnlyList<string> NotEvaluated,
    CompositionResult Safety);

/// <summary>
/// Operation readiness for Phase 2: the machine requirement profile, qualified operator (workforce)
/// and safety composition. Equipment, material, engineering, tooling and quality arrive with their
/// bounded contexts; until then they are listed as not evaluated.
/// </summary>
public sealed class OperationReadinessEngine : IEngine<SiteSnapshot, WorkItem, ReadinessResult>
{
    /// <summary>Dimensions that exist in the architecture but have no data in Forge yet.</summary>
    public static readonly IReadOnlyList<string> NotYetEvaluated = ["Material", "Engineering", "Quality"];

    // Fixed, documented order used to name the primary constraint when several dimensions fail.
    private static readonly string[] Precedence = ["Holds", "RequirementProfile", "Equipment", "Tooling", "Safety", "Workforce"];

    /// <summary>Equipment readiness from recorded downtime. Calibration and tooling arrive with their contexts.</summary>
    public static EvidenceNode EquipmentCheck(SiteSnapshot snapshot, string machineCode, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var down = snapshot.DowntimeAt(machineCode, at);
        return down is null
            ? new EvidenceNode("Equipment", EvidenceOutcome.Pass, $"No recorded downtime on {machineCode}.")
            : new EvidenceNode(
                "Equipment",
                EvidenceOutcome.Fail,
                down.Kind switch
                {
                    Maintenance.Contracts.DowntimeKind.Planned => $"{machineCode} is in planned maintenance until {down.EndsAt:u}: {down.Reason}.",
                    Maintenance.Contracts.DowntimeKind.MaintenanceOverdue => $"{machineCode} is locked out: {down.Reason}.",
                    Maintenance.Contracts.DowntimeKind.AssetOutOfService => $"{machineCode} is out of service: {down.Reason}.",
                    _ => $"{machineCode} is down since {down.StartsAt:u}: {down.Reason}.",
                })
            {
                Inputs = new(StringComparer.Ordinal) { ["downtimeId"] = down.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), ["kind"] = down.Kind.ToString() },
            };
    }

    private readonly PersonnelCompositionEngine composition = new();

    public string Name => "operation-readiness";

    public string Version => "2";

    public EngineEvaluation<ReadinessResult> Evaluate(SiteSnapshot snapshot, WorkItem parameters, DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(parameters);

        var nodes = new List<EvidenceNode>();
        var dimensions = new List<ReadinessDimension>();

        void Add(string name, EvidenceNode node)
        {
            nodes.Add(node);
            dimensions.Add(new ReadinessDimension(name, node.Outcome, node.Detail));
        }

        if (parameters.MachineCode is { } machineCode)
        {
            var profile = snapshot.MachineActivities.FirstOrDefault(m => m.MachineCode == machineCode && m.ActivityCode == parameters.ActivityCode);
            if (profile is null)
            {
                Add("RequirementProfile", new EvidenceNode("RequirementProfile", EvidenceOutcome.Fail, $"{parameters.ActivityCode} is not defined for {machineCode}."));
            }
            else
            {
                Add("RequirementProfile", new EvidenceNode("RequirementProfile", EvidenceOutcome.Pass, $"{parameters.ActivityCode} on {machineCode} requires one of {string.Join(", ", profile.OperatorQualifications)}.")
                {
                    Inputs = new(StringComparer.Ordinal) { ["operatorQualifications"] = string.Join(",", profile.OperatorQualifications) },
                });
                Add("Equipment", EquipmentCheck(snapshot, machineCode, asOf));
                var tooling = ExecutableChecks.Tooling(snapshot, machineCode, parameters.ActivityCode, asOf);
                Add("Tooling", new EvidenceNode("Tooling", tooling.Outcome, tooling.Detail));
                Add("Workforce", WorkforceCheck(snapshot, parameters, profile.OperatorQualifications));
            }
        }
        else
        {
            Add("Workforce", new EvidenceNode("Workforce", EvidenceOutcome.Info, $"{parameters.ActivityCode} is not machine work; no operator qualification is defined for it."));
        }

        if (ExecutableChecks.EquipmentHolds(snapshot, parameters.WorkCenterCode, parameters.MachineCode) is { } held)
        {
            Add("Holds", new EvidenceNode("Holds", held.Outcome, held.Detail));
        }

        var safety = composition.Evaluate(snapshot, parameters, asOf);
        Add("Safety", safety.Evidence);

        foreach (var dimension in NotYetEvaluated)
        {
            nodes.Add(new EvidenceNode(dimension, EvidenceOutcome.Info, $"Not evaluated: {dimension.ToLowerInvariant()} data is not in Forge yet."));
        }

        var failed = dimensions.Where(d => d.Outcome is EvidenceOutcome.Fail or EvidenceOutcome.Indeterminate).Select(d => d.Name).ToList();
        var primary = Precedence.FirstOrDefault(failed.Contains);
        var executable = failed.Count == 0;
        var root = new EvidenceNode(
            "OperationReadiness",
            EvidenceNode.Combine(nodes),
            executable
                ? $"{parameters.Describe()} is executable on the evaluated dimensions ({string.Join(", ", dimensions.Select(d => d.Name))})."
                : $"{parameters.Describe()} is not executable. Primary constraint: {primary}.")
        {
            Children = nodes,
        };

        return new EngineEvaluation<ReadinessResult>(
            new ReadinessResult(parameters, executable, primary, dimensions, NotYetEvaluated, safety.Output),
            root);
    }

    private static EvidenceNode WorkforceCheck(SiteSnapshot snapshot, WorkItem work, IReadOnlyList<string> operatorQualifications)
    {
        var onMachine = snapshot.People
            .Where(p => p.CountsAsPresent && p.Assignment is { } a && a.MachineCode == work.MachineCode && a.ActivityCode == work.ActivityCode)
            .OrderBy(p => p.PersonId)
            .ToList();
        var qualified = onMachine.Where(p => p.Assignment!.AsTrainee == false && operatorQualifications.Any(p.Holds)).ToList();
        var inputs = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["assigned"] = string.Join(",", onMachine.Select(p => p.Name)),
            ["qualified"] = string.Join(",", qualified.Select(p => p.Name)),
        };

        if (qualified.Count > 0)
        {
            return new EvidenceNode("Workforce", EvidenceOutcome.Pass, $"Qualified operator present and assigned: {string.Join(", ", qualified.Select(p => p.Name))}.") { Inputs = inputs };
        }

        var reasons = onMachine.Select(p => p.Assignment!.AsTrainee ? $"{p.Name} is assigned as a trainee" : $"{p.Name} holds none of {string.Join(", ", operatorQualifications)}");
        var candidates = snapshot.PresentInArea(work.SafetyAreaCode)
            .Where(p => !onMachine.Contains(p) && p.Assignment?.AsTrainee != true && operatorQualifications.Any(p.Holds))
            .Select(p => p.Name)
            .ToList();
        var detail = onMachine.Count == 0
            ? $"No one present is assigned to {work.Describe()}."
            : $"No independently qualified operator assigned: {string.Join("; ", reasons)}.";
        if (candidates.Count > 0)
        {
            detail += $" Qualified and present in {work.SafetyAreaCode}: {string.Join(", ", candidates)}.";
        }

        return new EvidenceNode("Workforce", EvidenceOutcome.Fail, detail) { Inputs = inputs };
    }
}
