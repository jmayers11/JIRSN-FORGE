using Forge.Kernel.Engines;
using Forge.Workforce.Contracts;

namespace Forge.Planning.Engines;

public sealed record WorkforceRecoveryMove(Guid PersonId, string PersonName, WorkAssignment Assignment);

public sealed record WorkforceRecoveryRequest(WorkItem Target, IReadOnlySet<Guid>? CandidatePersonIds = null);

public sealed record WorkforceRecoveryCandidate(
    int Rank,
    Guid PersonId,
    string PersonName,
    bool RequiresSourceBackfill,
    IReadOnlyList<WorkforceRecoveryMove> Moves,
    IReadOnlyList<Impact> SourceImpacts,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> NotEvaluated);

public sealed record WorkforceRecoveryResult(
    DateTimeOffset AsOf,
    WorkItem Target,
    bool TargetReadyBefore,
    string? CurrentConstraint,
    IReadOnlyList<ReadinessDimension> CurrentDimensions,
    int EvaluatedCandidates,
    IReadOnlyList<WorkforceRecoveryCandidate> Candidates,
    IReadOnlyList<string> Limitations);

/// <summary>
/// Finds a small deterministic set of immediately safe, source-aware workforce moves.
/// All options are read-only what-if plans; the supervisor still stages and commits a plan.
/// </summary>
public sealed class WorkforceRecoveryEngine : IEngine<SiteSnapshot, WorkforceRecoveryRequest, WorkforceRecoveryResult>
{
    private const int MaximumCandidates = 100;
    private const int MaximumBackfillCandidatesPerSource = 30;
    private const int MaximumResults = 3;
    private static readonly string[] Limitations = [
        "This preview checks current presence, qualifications, machine authorization, equipment/tooling, and safety composition.",
        "Material, order due-date, cost, and future-shift effects are not evaluated by this endpoint.",
        "No assignment is written; review the proposed moves and run the current plan preview before committing.",
    ];

    private readonly OperationReadinessEngine readiness = new();
    private readonly ReallocationImpactEngine impact = new();
    private readonly ReallocationPlanEngine plan = new();

    public string Name => "workforce-recovery";

    public string Version => "1";

    public EngineEvaluation<WorkforceRecoveryResult> Evaluate(SiteSnapshot snapshot, WorkforceRecoveryRequest request, DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Target);
        var target = request.Target;

        var targetBefore = readiness.Evaluate(snapshot, target, asOf).Output;
        var available = snapshot.People
            .Where(person => person.Active && person.CountsAsPresent && person.Presence != PresenceKind.Projected && person.Unavailability is null
                && (request.CandidatePersonIds is null || request.CandidatePersonIds.Contains(person.PersonId)))
            .OrderBy(person => person.Name, StringComparer.Ordinal)
            .ThenBy(person => person.PersonId)
            .Take(MaximumCandidates + 1)
            .ToArray();
        if (available.Length > MaximumCandidates)
            throw new ArgumentException($"Recovery ranking is limited to {MaximumCandidates} visible, currently present workers. Narrow the supervisor scope.", nameof(snapshot));

        var candidates = new List<WorkforceRecoveryCandidate>();
        foreach (var mover in available)
        {
            if (mover.Assignment is { } existing && ToWork(existing) == target)
                continue;

            var proposed = AssignmentFor(target);
            var preview = impact.Evaluate(snapshot, new ReallocationRequest(mover.PersonId, proposed), asOf).Output;
            var targetAfter = readiness.Evaluate(snapshot.WithAssignment(mover.PersonId, proposed), target, asOf).Output;
            if (!targetAfter.Executable)
                continue;

            var sourceImpacts = preview.Impacts
                .Where(item => item.Kind is ImpactKind.WouldStop or ImpactKind.WouldBeUnstaffed)
                .GroupBy(item => item.Work)
                .Select(group => group.First())
                .OrderBy(item => item.Work.WorkCenterCode ?? item.Work.SafetyAreaCode, StringComparer.Ordinal)
                .ThenBy(item => item.Work.MachineCode ?? string.Empty, StringComparer.Ordinal)
                .ToArray();

            if (sourceImpacts.Length == 0)
            {
                candidates.Add(new WorkforceRecoveryCandidate(
                    0,
                    mover.PersonId,
                    mover.Name,
                    false,
                    [new WorkforceRecoveryMove(mover.PersonId, mover.Name, proposed)],
                    [],
                    [$"{mover.Name} can cover {target.Describe()} with current verified qualifications and any required machine authorization.", "No currently executable source-center work is stopped."],
                    targetAfter.NotEvaluated));
                continue;
            }

            if (sourceImpacts.Length != 1)
                continue;

            var stopped = sourceImpacts[0].Work;
            var backfillAssignment = AssignmentFor(stopped);
            foreach (var replacement in available
                .Where(person => person.PersonId != mover.PersonId && person.ActiveOperation is null)
                .OrderBy(person => person.Name, StringComparer.Ordinal)
                .ThenBy(person => person.PersonId)
                .Take(MaximumBackfillCandidatesPerSource))
            {
                var sequence = new[]
                {
                    new ReallocationPlanMove(replacement.PersonId, backfillAssignment),
                    new ReallocationPlanMove(mover.PersonId, proposed),
                };
                var trial = plan.Evaluate(snapshot, sequence, asOf).Output;
                if (!SafePlan(snapshot, target, trial, asOf))
                    continue;

                candidates.Add(new WorkforceRecoveryCandidate(
                    0,
                    mover.PersonId,
                    mover.Name,
                    true,
                    [
                        new WorkforceRecoveryMove(replacement.PersonId, replacement.Name, backfillAssignment),
                        new WorkforceRecoveryMove(mover.PersonId, mover.Name, proposed),
                    ],
                    sourceImpacts,
                    [$"Move {replacement.Name} to {stopped.Describe()} first; then move {mover.Name} to {target.Describe()}.", "The ordered two-move preview keeps the source and target work executable at each step."],
                    targetAfter.NotEvaluated));
                break;
            }
        }

        var ranked = candidates
            .OrderBy(candidate => candidate.RequiresSourceBackfill ? 1 : 0)
            .ThenBy(candidate => candidate.Moves.Count)
            .ThenBy(candidate => candidate.PersonName, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.PersonId)
            .Take(MaximumResults)
            .Select((candidate, index) => candidate with { Rank = index + 1 })
            .ToArray();

        var result = new WorkforceRecoveryResult(
            asOf,
            target,
            targetBefore.Executable,
            targetBefore.PrimaryConstraint,
            targetBefore.Dimensions,
            available.Length,
            ranked,
            Limitations);
        var evidence = new EvidenceNode(
            "WorkforceRecovery",
            ranked.Length > 0 ? EvidenceOutcome.Pass : EvidenceOutcome.Info,
            ranked.Length > 0
                ? $"Found {ranked.Length} safe, source-aware workforce option(s) for {target.Describe()}; no move was committed."
                : $"No safe, source-aware currently present replacement was found for {target.Describe()}.")
        {
            Children = ranked.Select(candidate => new EvidenceNode(
                $"Rank {candidate.Rank}", EvidenceOutcome.Pass,
                string.Join(" ", candidate.Evidence))).ToArray(),
        };
        return new EngineEvaluation<WorkforceRecoveryResult>(result, evidence);
    }

    private bool SafePlan(SiteSnapshot original, WorkItem target, ReallocationPlanResult trial, DateTimeOffset asOf)
    {
        foreach (var step in trial.Steps)
        {
            if (step.Preview.Impacts.Any(item => item.Kind is ImpactKind.WouldStop or ImpactKind.WouldBeUnstaffed))
                return false;
            if (step.Preview.Impacts.Any(item => item.Kind == ImpactKind.NewWork && item.Constraint is not null))
                return false;
        }

        var before = impact.EvaluateAll(original, asOf).Where(result => result.Executable).Select(result => result.Work).ToHashSet();
        var final = trial.FinalActiveWork.ToDictionary(result => result.Work);
        if (!final.TryGetValue(target, out var targetResult) || !targetResult.Executable)
            return false;
        if (before.Any(work => !final.TryGetValue(work, out var result) || !result.Executable))
            return false;
        return true;
    }

    private static WorkItem ToWork(WorkAssignment assignment) =>
        new(assignment.SafetyAreaCode, assignment.WorkCenterCode, assignment.MachineCode, assignment.ActivityCode);

    private static WorkAssignment AssignmentFor(WorkItem work) =>
        new(work.SafetyAreaCode, work.WorkCenterCode, work.MachineCode, work.ActivityCode, false);
}
