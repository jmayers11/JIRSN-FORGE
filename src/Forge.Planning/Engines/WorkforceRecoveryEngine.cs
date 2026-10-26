using Forge.Kernel.Engines;
using Forge.Workforce.Contracts;

namespace Forge.Planning.Engines;

public sealed record WorkforceRecoveryMove(Guid PersonId, string PersonName, WorkAssignment Assignment);

/// <param name="MoverPersonIds">When set, only these people are offered to move to the target (for example, those holding an
/// operation's required qualifications); anyone available may still backfill the work they leave.</param>
public sealed record WorkforceRecoveryRequest(WorkItem Target, IReadOnlySet<Guid>? CandidatePersonIds = null, bool ProjectedShift = false, IReadOnlySet<Guid>? MoverPersonIds = null);

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
    IReadOnlyList<string> Limitations,
    bool ProjectedShift);

/// <summary>
/// Finds a small deterministic set of qualified workforce options for current presence or an explicitly
/// projected shift. All options are read-only what-if plans; the supervisor still stages and commits a plan.
/// </summary>
public sealed class WorkforceRecoveryEngine : IEngine<SiteSnapshot, WorkforceRecoveryRequest, WorkforceRecoveryResult>
{
    private const int MaximumCandidates = 100;
    private const int MaximumBackfillCandidatesPerSource = 30;
    private const int MaximumResults = 3;
    private static readonly string[] CurrentLimitations = [
        "This preview checks current presence, qualifications, machine authorization, equipment/tooling, and safety composition.",
        "Material, order due-date, cost, and future-shift effects are not evaluated by this current-presence preview.",
        "No assignment is written; review the proposed moves and run the current plan preview before committing.",
    ];
    private static readonly string[] ProjectedShiftLimitations = [
        "Candidate presence is projected from the selected shift calendar and recorded unavailability; it is not proof of clock-in or attendance.",
        "Qualification expiry, machine authorization, equipment/tooling, and safety rules effective at the selected shift start are checked.",
        "Currently open source operations are checked with current assignments carried forward to the selected shift. Future dispatch, source workload, material, due-date, and cost effects are not forecasted.",
        "No assignment is written; confirm actual attendance and review source-center coverage before committing a plan.",
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
            .Where(person => person.Active
                && (request.ProjectedShift
                    ? person.Presence == PresenceKind.Projected && person.ActiveOperation is null
                    : person.CountsAsPresent && person.Presence != PresenceKind.Projected)
                && person.Unavailability is null
                && (request.CandidatePersonIds is null || request.CandidatePersonIds.Contains(person.PersonId)))
            .OrderBy(person => person.Name, StringComparer.Ordinal)
            .ThenBy(person => person.PersonId)
            .Take(MaximumCandidates + 1)
            .ToArray();
        if (available.Length > MaximumCandidates)
            throw new ArgumentException(request.ProjectedShift
                ? $"Recovery ranking is limited to {MaximumCandidates} visible, scheduled workers. Narrow the supervisor scope."
                : $"Recovery ranking is limited to {MaximumCandidates} visible, currently present workers. Narrow the supervisor scope.", nameof(snapshot));

        var candidates = new List<WorkforceRecoveryCandidate>();
        foreach (var mover in available)
        {
            if (mover.Assignment is { } existing && ToWork(existing) == target)
                continue;
            if (request.MoverPersonIds is { } movers && !movers.Contains(mover.PersonId))
                continue;

            var proposed = AssignmentFor(target);
            var preview = impact.Evaluate(snapshot, new ReallocationRequest(mover.PersonId, proposed), asOf).Output;
            var targetAfter = readiness.Evaluate(snapshot.WithAssignment(mover.PersonId, proposed), target, asOf).Output;
            if (!targetAfter.Executable)
                continue;

            var sourceImpacts = (preview?.Impacts ?? [])
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
                    request.ProjectedShift
                        ? [$"{mover.Name} is scheduled at the selected shift start and meets the target qualification, machine-authorization, equipment/tooling, and safety checks.", "No currently open source operation is made unready using current assignments carried forward; future source dispatch and workload remain unknown."]
                        : [$"{mover.Name} can cover {target.Describe()} with current verified qualifications and any required machine authorization.", "No currently executable source-center work is stopped."],
                    request.ProjectedShift
                        ? [.. targetAfter.NotEvaluated, "Future source operation assignments and dispatch load"]
                        : targetAfter.NotEvaluated));
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
                    request.ProjectedShift
                        ? [.. targetAfter.NotEvaluated, "Future source operation assignments and dispatch load"]
                        : targetAfter.NotEvaluated));
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
            request.ProjectedShift ? ProjectedShiftLimitations : CurrentLimitations,
            request.ProjectedShift);
        var evidence = new EvidenceNode(
            "WorkforceRecovery",
            ranked.Length > 0 ? EvidenceOutcome.Pass : EvidenceOutcome.Info,
            ranked.Length > 0
                ? request.ProjectedShift
                    ? $"Found {ranked.Length} qualification- and safety-checked candidate(s) scheduled for the selected shift for {target.Describe()}; no move was committed."
                    : $"Found {ranked.Length} safe, source-aware workforce option(s) for {target.Describe()}; no move was committed."
                : request.ProjectedShift
                    ? $"No qualified and safety-checked worker is scheduled at the selected shift start for {target.Describe()}."
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
