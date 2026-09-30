using Forge.Kernel.Engines;
using Forge.Workforce.Contracts;

namespace Forge.Planning.Engines;

/// <summary>A proposed move: the person's new assignment, or null to take them off work.</summary>
public sealed record ReallocationRequest(Guid PersonId, WorkAssignment? Proposed);

public enum ImpactKind
{
    /// <summary>Executable now; would not be after the move.</summary>
    WouldStop,

    /// <summary>Not executable now; would be after the move.</summary>
    WouldStart,

    /// <summary>The mover's own new work, and whether it could run.</summary>
    NewWork,

    /// <summary>The mover's current work would have nobody assigned to it.</summary>
    WouldBeUnstaffed,
}

public sealed record Impact(ImpactKind Kind, WorkItem Work, string? Constraint, string Detail);

public enum OptionKind
{
    ReplacePerson,
    MoveOperatorElsewhere,
    SwitchToOtherActivity,
    AdjustSchedule,
    ReviewManually,
}

/// <summary>A possible response to a stopped piece of work. Forge lists options; the supervisor chooses.</summary>
public sealed record ReallocationOption(OptionKind Kind, string Description, IReadOnlyList<string> Consequences, WorkItem? For);

public sealed record ReallocationPreview(
    Guid PersonId,
    string PersonName,
    WorkAssignment? Current,
    WorkAssignment? Proposed,
    IReadOnlyList<Impact> Impacts,
    IReadOnlyList<ReallocationOption> Options,
    IReadOnlyList<ReadinessResult> Before,
    IReadOnlyList<ReadinessResult> After);

/// <summary>
/// "What happens if I move this person?" Evaluates readiness of all active work before and after
/// the move on an in-memory copy of the snapshot, then lists concrete options for anything that
/// would stop. Never commits anything and never picks an option.
/// </summary>
public sealed class ReallocationImpactEngine : IEngine<SiteSnapshot, ReallocationRequest, ReallocationPreview>
{
    private const int MaxReplacementOptions = 5;
    private readonly OperationReadinessEngine readiness = new();
    private readonly PersonnelCompositionEngine composition = new();

    public string Name => "reallocation-impact";

    public string Version => "1";

    public EngineEvaluation<ReallocationPreview> Evaluate(SiteSnapshot snapshot, ReallocationRequest parameters, DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(parameters);

        var mover = snapshot.Person(parameters.PersonId)
            ?? throw new ArgumentException("The person is not in the snapshot.", nameof(parameters));
        var afterSnapshot = snapshot.WithAssignment(mover.PersonId, parameters.Proposed);
        var before = EvaluateAll(snapshot, asOf);
        var after = EvaluateAll(afterSnapshot, asOf);

        var impacts = new List<Impact>();
        foreach (var b in before.Where(b => b.Executable))
        {
            var a = after.FirstOrDefault(x => x.Work == b.Work);
            if (a is null)
            {
                // Only the mover's assignment changes, so this is the work they leave with nobody else on it.
                impacts.Add(new Impact(ImpactKind.WouldBeUnstaffed, b.Work, null, $"{mover.Name} leaves {b.Work.Describe()}; nobody else is assigned to it."));
            }
            else if (!a.Executable)
            {
                impacts.Add(new Impact(ImpactKind.WouldStop, a.Work, a.PrimaryConstraint, Explain(a)));
            }
        }

        foreach (var a in after.Where(a => a.Executable))
        {
            var b = before.FirstOrDefault(x => x.Work == a.Work);
            if (b is { Executable: false })
            {
                impacts.Add(new Impact(ImpactKind.WouldStart, a.Work, null, $"{a.Work.Describe()} becomes executable."));
            }
        }

        if (parameters.Proposed is { } proposed && mover.CountsAsPresent)
        {
            var newWork = new WorkItem(proposed.SafetyAreaCode, proposed.WorkCenterCode, proposed.MachineCode, proposed.ActivityCode);
            var result = after.First(x => x.Work == newWork);
            impacts.Add(new Impact(ImpactKind.NewWork, newWork, result.PrimaryConstraint, result.Executable ? $"{mover.Name} can start {newWork.Describe()}." : Explain(result)));
        }

        var options = new List<ReallocationOption>();
        foreach (var stopped in impacts.Where(i => i.Kind == ImpactKind.WouldStop))
        {
            options.AddRange(Replacements(afterSnapshot, after, mover, stopped.Work, asOf));
            options.AddRange(OperatorOptions(afterSnapshot, stopped.Work, asOf));
            options.Add(new ReallocationOption(
                OptionKind.AdjustSchedule,
                $"Reschedule {stopped.Work.Describe()} to a time with enough qualified coverage.",
                ["Forge's scheduler is not built yet (Phase 4); review the schedule manually."],
                stopped.Work));
        }

        if (options.Count > 0)
        {
            options.Add(new ReallocationOption(OptionKind.ReviewManually, "Review manually before deciding.", [], null));
        }

        var preview = new ReallocationPreview(mover.PersonId, mover.Name, mover.Assignment, parameters.Proposed, impacts, options, before, after);
        var evidence = new EvidenceNode(
            "ReallocationImpact",
            impacts.Any(i => i.Kind == ImpactKind.WouldStop) ? EvidenceOutcome.Fail : EvidenceOutcome.Pass,
            impacts.Any(i => i.Kind == ImpactKind.WouldStop)
                ? $"Moving {mover.Name} would stop: {string.Join("; ", impacts.Where(i => i.Kind == ImpactKind.WouldStop).Select(i => i.Work.Describe()))}."
                : $"Moving {mover.Name} stops no active work.")
        {
            Children = impacts.Select(i => new EvidenceNode(i.Kind.ToString(), i.Kind == ImpactKind.WouldStop ? EvidenceOutcome.Fail : EvidenceOutcome.Info, i.Detail)).ToList(),
        };
        return new EngineEvaluation<ReallocationPreview>(preview, evidence);
    }

    /// <summary>Readiness of every piece of active work in the snapshot.</summary>
    public IReadOnlyList<ReadinessResult> EvaluateAll(SiteSnapshot snapshot, DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.ActiveWork().Select(w => readiness.Evaluate(snapshot, w, asOf).Output).ToList();
    }

    private IEnumerable<ReallocationOption> Replacements(SiteSnapshot after, IReadOnlyList<ReadinessResult> afterResults, PersonSnapshot mover, WorkItem stopped, DateTimeOffset asOf)
    {
        // A replacement takes over the mover's previous duty (independently, not as a trainee).
        var duty = mover.Assignment is { } previous && previous.SafetyAreaCode == stopped.SafetyAreaCode
            ? previous with { AsTrainee = false }
            : new WorkAssignment(stopped.SafetyAreaCode, stopped.WorkCenterCode, null, stopped.ActivityCode, false);

        var clean = new List<ReallocationOption>();
        var costly = new List<ReallocationOption>();
        foreach (var candidate in after.People.Where(p => p.CountsAsPresent && p.PersonId != mover.PersonId && p.Assignment?.SafetyAreaCode != stopped.SafetyAreaCode).OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.PersonId))
        {
            var trial = after.WithAssignment(candidate.PersonId, duty);
            var trialResults = EvaluateAll(trial, asOf);
            if (trialResults.FirstOrDefault(r => r.Work == stopped) is not { Executable: true })
            {
                continue;
            }

            var broken = afterResults
                .Where(r => r.Executable)
                .Select(r => (r.Work, Trial: trialResults.FirstOrDefault(t => t.Work == r.Work)))
                .Where(x => x.Trial is not { Executable: true })
                .Select(x => x.Trial is null ? $"Would leave {x.Work.Describe()} unstaffed." : $"Would stop {x.Work.Describe()}.")
                .ToList();
            var from = candidate.Assignment is null ? "no current assignment" : $"{candidate.Assignment.ActivityCode} in {candidate.Assignment.SafetyAreaCode}";
            var option = new ReallocationOption(
                OptionKind.ReplacePerson,
                $"Move {candidate.Name} ({from}) to {duty.ActivityCode} in {duty.SafetyAreaCode}.",
                broken.Count == 0 ? [$"Restores {stopped.Describe()}.", "Stops no other active work."] : [$"Restores {stopped.Describe()}.", .. broken],
                stopped);
            (broken.Count == 0 ? clean : costly).Add(option);
        }

        return clean.Concat(costly).Take(MaxReplacementOptions);
    }

    private IEnumerable<ReallocationOption> OperatorOptions(SiteSnapshot after, WorkItem stopped, DateTimeOffset asOf)
    {
        var operators = after.People
            .Where(p => p.CountsAsPresent && p.Assignment is { } a && a.SafetyAreaCode == stopped.SafetyAreaCode && a.MachineCode == stopped.MachineCode && a.ActivityCode == stopped.ActivityCode && a.WorkCenterCode == stopped.WorkCenterCode)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

        // One-person alternatives: activities allowed at the same work center without a machine.
        var supportActivities = after.WorkCenterActivities
            .Where(a => a.WorkCenterCode == stopped.WorkCenterCode)
            .Select(a => a.ActivityCode)
            .Order(StringComparer.Ordinal)
            .ToList();

        foreach (var op in operators)
        {
            foreach (var activity in supportActivities)
            {
                var switched = new WorkAssignment(stopped.SafetyAreaCode, stopped.WorkCenterCode, null, activity, op.Assignment!.AsTrainee);
                var trial = after.WithAssignment(op.PersonId, switched);
                var work = new WorkItem(switched.SafetyAreaCode, switched.WorkCenterCode, null, activity);
                var result = composition.Evaluate(trial, work, asOf).Output;
                if (result.Status != CompositionStatus.NotSatisfied)
                {
                    yield return new ReallocationOption(
                        OptionKind.SwitchToOtherActivity,
                        $"Switch {op.Name} to {activity} in {work.WorkCenterCode ?? work.SafetyAreaCode}.",
                        [$"{stopped.Describe()} stops.", result.Status == CompositionStatus.NoRuleApplies ? $"No safety rule restricts {activity}." : $"Safety rules for {activity} are satisfied."],
                        stopped);
                }
            }

            yield return new ReallocationOption(
                OptionKind.MoveOperatorElsewhere,
                $"Stop {stopped.Describe()} and reassign {op.Name} to another area.",
                [$"{stopped.Describe()} stops."],
                stopped);
        }
    }

    private static string Explain(ReadinessResult result)
    {
        var failures = result.Dimensions.Where(d => d.Outcome is EvidenceOutcome.Fail or EvidenceOutcome.Indeterminate).Select(d => d.Detail);
        var shortfalls = result.Safety.Rules.SelectMany(r => r.Shortfalls);
        return $"{result.Work.Describe()} would not be executable: {string.Join(" ", failures.Concat(shortfalls).Distinct())}";
    }
}
