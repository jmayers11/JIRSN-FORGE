using System.Globalization;
using Forge.Execution.Contracts;
using Forge.Kernel.Engines;

namespace Forge.Planning.Engines;

/// <summary>Actual shop-floor state for one operation, reduced to the facts the goal engine needs.</summary>
public sealed record GoalActualOperation(long OrderNumber, int Sequence, OperationStatus Status, DateTimeOffset? CompletedAt);

/// <summary>The approved/proposed plan and actual operation facts used to ask whether goals remain achievable.</summary>
/// <param name="Schedule">The plan: operations due by the as-of time say whether an order is behind plan.</param>
/// <param name="Reforecast">
/// A fresh finite-capacity schedule of the remaining work from now (V3.7). When given, projected completion and
/// delivery status come from it rather than from the plan; behind plan is still measured against the plan.
/// </param>
public sealed record GoalRiskSnapshot(ScheduleResult? Schedule, IReadOnlyList<GoalActualOperation> Actuals, ScheduleResult? Reforecast = null);

/// <param name="AsOf">The moment to evaluate against the plan.</param>
/// <param name="RecoveryMargin">
/// How much slack must remain between projected completion and due time before a behind-plan goal is still
/// called recoverable. A goal inside this margin is at risk even if the projected completion is not late.
/// </param>
public sealed record GoalRiskRequest(DateTimeOffset AsOf, TimeSpan? RecoveryMargin = null);

public enum GoalRiskState
{
    OnTrack,
    BehindPlanRecoverable,
    GoalAtRisk,
    Late,
    Unschedulable,
    Complete,
}

public sealed record GoalRiskLine(
    long OrderNumber,
    string PartNumber,
    int Priority,
    DateOnly DueDate,
    DateTimeOffset DueAt,
    DateTimeOffset? ProjectedCompletion,
    GoalRiskState State,
    bool BehindPlan,
    bool AtRisk,
    decimal PlannedMinutesDue,
    decimal PlannedMinutesComplete,
    decimal RecoverySlackHours,
    IReadOnlyList<string> MissedOperations,
    IReadOnlyList<string> Evidence,
    Guid? ProgramId);

public sealed record GoalRiskResult(
    DateTimeOffset AsOf,
    int Goals,
    int BehindPlan,
    int AtRisk,
    int Late,
    int Unschedulable,
    IReadOnlyList<GoalRiskLine> Lines,
    string Explanation);

/// <summary>
/// V3 goal-risk evaluation. Behind plan is measured against operations that the plan expected to be done by
/// the as-of time. Goal-at-risk is measured against recoverability to the required due time. The distinction
/// matters: a goal may be behind an intermediate plan and still recoverable inside executable capacity.
/// </summary>
public sealed class GoalRiskEngine : IEngine<GoalRiskSnapshot, GoalRiskRequest, GoalRiskResult>
{
    public string Name => "goal-risk";

    public string Version => "1";

    public EngineEvaluation<GoalRiskResult> Evaluate(GoalRiskSnapshot snapshot, GoalRiskRequest parameters, DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(parameters);
        var at = parameters.AsOf.ToUniversalTime();
        var margin = parameters.RecoveryMargin ?? SchedulingEngine.AtRiskMargin;
        if (margin < TimeSpan.Zero)
        {
            throw new ArgumentException("Recovery margin cannot be negative.", nameof(parameters));
        }

        var plan = snapshot.Schedule ?? snapshot.Reforecast;
        var current = snapshot.Reforecast ?? plan;
        if (plan is null || current is null)
        {
            var empty = new GoalRiskResult(at, 0, 0, 0, 0, 0, [], "No schedule is available, so Forge cannot assess goal risk.");
            return new(empty, new EvidenceNode("GoalRisk", EvidenceOutcome.Indeterminate, empty.Explanation));
        }

        var actuals = snapshot.Actuals.ToDictionary(a => (a.OrderNumber, a.Sequence));
        var operationsByOrder = plan.Operations.GroupBy(o => o.OrderNumber).ToDictionary(g => g.Key, g => g.OrderBy(o => o.Sequence).ToList());
        var currentOperations = current.Operations.GroupBy(o => o.OrderNumber).ToDictionary(g => g.Key, g => g.OrderBy(o => o.Sequence).ToList());
        var lines = new List<GoalRiskLine>();

        foreach (var order in current.Orders.OrderBy(o => o.Priority).ThenBy(o => o.DueAt).ThenBy(o => o.OrderNumber))
        {
            operationsByOrder.TryGetValue(order.OrderNumber, out var operations);
            operations ??= [];
            var everyOperation = currentOperations.GetValueOrDefault(order.OrderNumber) ?? operations;
            var due = operations.Where(o => o.End is { } end && end <= at).ToList();
            var missed = new List<string>();
            var completeMinutes = 0m;
            foreach (var op in due)
            {
                if (actuals.TryGetValue((op.OrderNumber, op.Sequence), out var actual)
                    && actual.Status == OperationStatus.Complete
                    && actual.CompletedAt is { } done
                    && done.ToUniversalTime() <= at)
                {
                    completeMinutes += op.PlannedMinutes;
                }
                else
                {
                    missed.Add($"operation {op.Sequence} planned by {Utc(op.End!.Value)}");
                }
            }

            var plannedMinutes = due.Sum(o => o.PlannedMinutes);
            var behind = missed.Count > 0;
            var allComplete = everyOperation.Count > 0 && everyOperation.All(o => actuals.TryGetValue((o.OrderNumber, o.Sequence), out var a) && a.Status == OperationStatus.Complete);
            var slack = order.ProjectedCompletion is { } projected ? order.DueAt - projected : TimeSpan.MinValue;
            var state = State(order, allComplete, behind, slack, margin);
            var atRisk = state is GoalRiskState.GoalAtRisk or GoalRiskState.Late or GoalRiskState.Unschedulable;
            var evidence = new List<string>
            {
                order.ProjectedCompletion is { } completion
                    ? $"Projected completion {Utc(completion)}, due {Utc(order.DueAt)}, slack {Hours(slack)} h."
                    : $"No projected completion; scheduler status is {order.Status}.",
            };
            if (behind)
            {
                evidence.Add($"{missed.Count} operation(s) planned by {Utc(at)} are not complete.");
            }

            evidence.Add(state == GoalRiskState.BehindPlanRecoverable
                ? $"Behind plan, but projected completion still has at least {Hours(margin)} h of recovery slack."
                : state == GoalRiskState.GoalAtRisk
                    ? $"Projected completion is inside the {Hours(margin)} h recovery margin."
                    : Label(state));

            lines.Add(new GoalRiskLine(
                order.OrderNumber,
                order.PartNumber,
                order.Priority,
                order.DueDate,
                order.DueAt,
                order.ProjectedCompletion,
                state,
                behind,
                atRisk,
                plannedMinutes,
                completeMinutes,
                order.ProjectedCompletion is null ? decimal.MinValue : decimal.Round((decimal)slack.TotalHours, 2),
                missed,
                evidence,
                order.ProgramId));
        }

        var result = new GoalRiskResult(
            at,
            lines.Count,
            lines.Count(l => l.BehindPlan),
            lines.Count(l => l.AtRisk),
            lines.Count(l => l.State == GoalRiskState.Late),
            lines.Count(l => l.State == GoalRiskState.Unschedulable),
            lines,
            $"{lines.Count} goal(s) assessed: {lines.Count(l => l.BehindPlan)} behind plan, {lines.Count(l => l.AtRisk)} at risk.");

        var evidenceRoot = new EvidenceNode("GoalRisk", result.AtRisk == 0 ? EvidenceOutcome.Pass : EvidenceOutcome.Fail, result.Explanation)
        {
            Children = lines.Select(l => new EvidenceNode(l.State.ToString(), l.AtRisk ? EvidenceOutcome.Fail : l.BehindPlan ? EvidenceOutcome.Info : EvidenceOutcome.Pass,
                $"Order {l.OrderNumber}: {string.Join(" ", l.Evidence)}")).ToList(),
        };
        return new(result, evidenceRoot);
    }

    private static GoalRiskState State(ScheduledOrder order, bool allComplete, bool behind, TimeSpan slack, TimeSpan margin)
    {
        if (allComplete)
        {
            return GoalRiskState.Complete;
        }

        return order.Status switch
        {
            DeliveryStatus.Unschedulable => GoalRiskState.Unschedulable,
            DeliveryStatus.Late => GoalRiskState.Late,
            _ when order.ProjectedCompletion is null => GoalRiskState.GoalAtRisk,
            _ when slack < TimeSpan.Zero => GoalRiskState.Late,
            _ when slack < margin => GoalRiskState.GoalAtRisk,
            _ when behind => GoalRiskState.BehindPlanRecoverable,
            _ => GoalRiskState.OnTrack,
        };
    }

    private static string Label(GoalRiskState state) => state switch
    {
        GoalRiskState.OnTrack => "On track.",
        GoalRiskState.Complete => "Complete.",
        GoalRiskState.Late => "Projected late.",
        GoalRiskState.Unschedulable => "Unschedulable inside the current horizon.",
        _ => "Goal at risk.",
    };

    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture);

    private static string Hours(TimeSpan value) => decimal.Round((decimal)value.TotalHours, 2).ToString("0.##", CultureInfo.InvariantCulture);
}
