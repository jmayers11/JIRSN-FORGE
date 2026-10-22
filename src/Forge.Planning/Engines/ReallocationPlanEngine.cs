using Forge.Kernel.Engines;
using Forge.Workforce.Contracts;

namespace Forge.Planning.Engines;

/// <summary>One proposed assignment in a scenario-only cascade.</summary>
public sealed record ReallocationPlanMove(Guid PersonId, WorkAssignment? Proposed);

public sealed record ReallocationPlanStep(int Number, ReallocationPreview Preview);

public sealed record ReallocationPlanResult(
    DateTimeOffset AsOf,
    IReadOnlyList<ReallocationPlanStep> Steps,
    IReadOnlyList<ReadinessResult> FinalActiveWork);

/// <summary>
/// Evaluates a sequence of proposed moves against successive copies of one snapshot. No assignment is written.
/// This makes a later move see the safety effects of every earlier move in its draft.
/// </summary>
public sealed class ReallocationPlanEngine : IEngine<SiteSnapshot, IReadOnlyList<ReallocationPlanMove>, ReallocationPlanResult>
{
    private const int MaximumMoves = 100;
    private readonly ReallocationImpactEngine reallocation = new();

    public string Name => "reallocation-plan";

    public string Version => "1";

    public EngineEvaluation<ReallocationPlanResult> Evaluate(SiteSnapshot snapshot, IReadOnlyList<ReallocationPlanMove> moves, DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(moves);
        if (moves.Count is < 1 or > MaximumMoves)
            throw new ArgumentException($"A scenario must contain between 1 and {MaximumMoves} moves.", nameof(moves));
        if (moves.Select(move => move.PersonId).Distinct().Count() != moves.Count)
            throw new ArgumentException("A worker can appear only once in a scenario move list.", nameof(moves));

        var working = snapshot;
        var steps = new List<ReallocationPlanStep>(moves.Count);
        var impactedWork = new HashSet<WorkItem>();
        for (var i = 0; i < moves.Count; i++)
        {
            var move = moves[i];
            if (working.Person(move.PersonId) is null)
                throw new ArgumentException($"Worker {move.PersonId:D} is not in the planning snapshot.", nameof(moves));

            var preview = reallocation.Evaluate(working, new ReallocationRequest(move.PersonId, move.Proposed), asOf).Output;
            impactedWork.UnionWith(preview.Impacts.Select(impact => impact.Work));
            preview = preview with
            {
                Before = preview.Before.Where(result => impactedWork.Contains(result.Work)).ToArray(),
                After = preview.After.Where(result => impactedWork.Contains(result.Work)).ToArray(),
            };
            steps.Add(new ReallocationPlanStep(i + 1, preview));
            working = working.WithAssignment(move.PersonId, move.Proposed);
        }

        var result = new ReallocationPlanResult(asOf, steps, reallocation.EvaluateAll(working, asOf));
        var blockers = steps.SelectMany(step => step.Preview.Impacts)
            .Where(impact => impact.Kind is ImpactKind.WouldStop or ImpactKind.WouldBeUnstaffed)
            .Select(impact => new EvidenceNode(impact.Kind.ToString(), EvidenceOutcome.Fail, impact.Detail))
            .ToList();
        var evidence = new EvidenceNode(
            "ReallocationPlan",
            blockers.Count == 0 ? EvidenceOutcome.Pass : EvidenceOutcome.Fail,
            blockers.Count == 0 ? $"All {steps.Count} proposed move(s) were evaluated on successive copies of the same snapshot." : $"The proposed move sequence affects safe active work in {blockers.Count} finding(s).")
        {
            Children = blockers,
        };
        return new EngineEvaluation<ReallocationPlanResult>(result, evidence);
    }
}
