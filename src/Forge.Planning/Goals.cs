using Forge.Execution.Contracts;
using Forge.Planning.Engines;
using Forge.Resources.Contracts;

namespace Forge.Planning;

/// <summary>Production goals (make it) and delivery goals (get it to the customer) are never mixed (V3.7).</summary>
public enum GoalKind
{
    Production,
    Delivery,
}

public enum GoalStatus
{
    Open,
    Closed,
}

/// <summary>Where a goal stands (V3.7). Behind plan and at risk are different things.</summary>
public enum GoalState
{
    Complete,
    OnTrack,

    /// <summary>Behind the plan's intermediate steps, but the reforecast still makes the required-by date with slack.</summary>
    BehindPlan,

    /// <summary>No projection to judge by: not scheduled, or nothing below it is.</summary>
    NoProjection,

    /// <summary>The reforecast makes the date with too little slack, or the work cannot be scheduled.</summary>
    AtRisk,

    Late,
}

/// <param name="RequiredBy">Its own date; null when it follows its parent by <paramref name="LeadDays"/>.</param>
/// <param name="LeadDays">How many days before its parent's required-by date it is needed.</param>
public sealed record GoalInfo(
    long GoalId,
    GoalKind Kind,
    string Title,
    long? ParentGoalId,
    long? OrderNumber,
    long? DemandId,
    DateOnly? RequiredBy,
    int? LeadDays,
    Guid? ProgramId,
    GoalStatus Status,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset ChangedAt,
    string ChangedBy);

/// <summary>What a reforecast projected for a goal at one moment (V3.7). Never changed.</summary>
public sealed record GoalForecast(long ForecastId, long GoalId, DateTimeOffset At, DateOnly RequiredBy, DateTimeOffset? Projected, GoalState State, bool BehindPlan, decimal Attainment, string RecordedBy);

public interface IGoalStore
{
    Task<long> InsertAsync(GoalInfo goal, CancellationToken ct);

    Task UpdateAsync(GoalInfo goal, CancellationToken ct);

    Task<IReadOnlyList<GoalInfo>> ListAsync(bool includeClosed, CancellationToken ct);

    Task InsertForecastAsync(GoalForecast forecast, CancellationToken ct);

    Task<IReadOnlyList<GoalForecast>> ListForecastsAsync(long? goalId, CancellationToken ct);
}

/// <summary>A demand line as a delivery goal sees it: how much, by when, how much has shipped.</summary>
public sealed record GoalDemand(long DemandId, string PartNumber, decimal Quantity, decimal Shipped, DateOnly NeedDate);

/// <summary>One goal evaluated now (V3.7).</summary>
public sealed record GoalAttainment(
    long GoalId,
    GoalKind Kind,
    string Title,
    long? ParentGoalId,
    long? OrderNumber,
    long? DemandId,
    DateOnly RequiredBy,
    DateTimeOffset? Projected,
    decimal Done,
    decimal Target,
    decimal Attainment,
    GoalState State,
    bool BehindPlan,
    decimal RemainingMinutes,
    IReadOnlyList<string> Evidence,
    Guid? ProgramId);

/// <summary>
/// Goal attainment (V3.7): required-by dates cascade down the breakdown; a production goal follows its order through
/// the plan and a fresh reforecast of the remaining work; a delivery goal follows what has shipped and the
/// production goals below it; a goal with neither rolls up its children. Behind plan never makes a goal at risk by
/// itself: only the reforecast against the required-by date does.
/// </summary>
public static class GoalAttainmentEngine
{
    public static IReadOnlyList<GoalAttainment> Evaluate(
        IReadOnlyList<GoalInfo> goals,
        IReadOnlyDictionary<long, GoalRiskLine> risk,
        IReadOnlyDictionary<long, OrderInfo> orders,
        IReadOnlyDictionary<long, GoalDemand> demand,
        DateOnly today,
        string? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(goals);
        ArgumentNullException.ThrowIfNull(risk);
        ArgumentNullException.ThrowIfNull(orders);
        ArgumentNullException.ThrowIfNull(demand);
        var byId = goals.ToDictionary(g => g.GoalId);
        var children = goals.Where(g => g.ParentGoalId is not null).ToLookup(g => g.ParentGoalId!.Value);
        var required = new Dictionary<long, DateOnly>();
        var done = new Dictionary<long, GoalAttainment>();

        DateOnly RequiredBy(GoalInfo g, int depth)
        {
            if (required.TryGetValue(g.GoalId, out var known))
            {
                return known;
            }

            var date = g.RequiredBy
                ?? (g.ParentGoalId is { } p && byId.TryGetValue(p, out var parent) && depth < 50 ? RequiredBy(parent, depth + 1).AddDays(-(g.LeadDays ?? 0)) : today);
            required[g.GoalId] = date;
            return date;
        }

        GoalAttainment Assess(GoalInfo g, int depth)
        {
            if (done.TryGetValue(g.GoalId, out var known))
            {
                return known;
            }

            var requiredBy = RequiredBy(g, 0);
            var below = depth < 50 ? children[g.GoalId].Select(c => Assess(c, depth + 1)).ToList() : new List<GoalAttainment>();
            var evidence = new List<string>();
            DateTimeOffset? projected = null;
            decimal target, completed, remaining = 0;
            GoalState state;
            var behind = below.Any(b => b.BehindPlan);

            if (g.OrderNumber is { } orderNumber)
            {
                var order = orders.GetValueOrDefault(orderNumber);
                var last = order?.Routed().LastOrDefault();
                target = order?.Quantity ?? 0;
                completed = last?.GoodQuantity ?? 0;
                remaining = order is null ? 0 : order.Operations.Where(o => o.Status != OperationStatus.Complete)
                    .Sum(o => order.Quantity == 0 ? o.PlannedMinutes : o.PlannedMinutes * Math.Max(0, 1 - (o.GoodQuantity / order.Quantity)));
                if (order is null)
                {
                    state = GoalState.NoProjection;
                    evidence.Add($"Order {orderNumber} is not visible.");
                }
                else if (order.Status == OrderStatus.Completed)
                {
                    state = GoalState.Complete;
                    evidence.Add($"Order {orderNumber} completed: {completed:0.###} of {target:0.###} good.");
                }
                else if (risk.TryGetValue(orderNumber, out var line))
                {
                    projected = line.ProjectedCompletion;
                    behind |= line.BehindPlan;
                    state = line.State switch
                    {
                        GoalRiskState.Complete => GoalState.Complete,
                        GoalRiskState.OnTrack => GoalState.OnTrack,
                        GoalRiskState.BehindPlanRecoverable => GoalState.BehindPlan,
                        GoalRiskState.Late => GoalState.Late,
                        _ => GoalState.AtRisk,
                    };
                    evidence.AddRange(line.Evidence);
                }
                else
                {
                    state = GoalState.NoProjection;
                    evidence.Add($"Order {orderNumber} is {order.Status}; it is not in the schedule, so there is no projection.");
                }
            }
            else if (g.DemandId is { } demandId)
            {
                var d = demand.GetValueOrDefault(demandId);
                target = d?.Quantity ?? 0;
                completed = d?.Shipped ?? 0;
                projected = below.Where(b => b.Kind == GoalKind.Production).Select(b => b.Projected).Max();
                remaining = below.Sum(b => b.RemainingMinutes);
                if (d is not null && completed >= target)
                {
                    state = GoalState.Complete;
                    evidence.Add($"Demand {demandId} shipped in full ({completed:0.###}).");
                }
                else
                {
                    state = below.Count == 0 ? GoalState.NoProjection : WorstOf(below.Select(b => b.State == GoalState.Complete ? GoalState.OnTrack : b.State));
                    evidence.Add(d is null ? $"Demand {demandId} is not visible." : $"Demand {demandId}: {completed:0.###} of {target:0.###} shipped.");
                    if (below.Count == 0)
                    {
                        evidence.Add("No production goal below it to project from.");
                    }
                }
            }
            else
            {
                target = below.Count;
                completed = below.Count(b => b.State == GoalState.Complete);
                projected = below.Select(b => b.Projected).Max();
                remaining = below.Sum(b => b.RemainingMinutes);
                state = below.Count == 0 ? GoalState.NoProjection : WorstOf(below.Select(b => b.State));
                evidence.Add(below.Count == 0 ? "Nothing below it yet." : $"{completed:0} of {target:0} goal(s) below it complete.");
            }

            // The goal's own date decides, whatever the order's or demand's dates say.
            if (state != GoalState.Complete)
            {
                if (requiredBy < today)
                {
                    state = GoalState.Late;
                    evidence.Add($"Required by {requiredBy:yyyy-MM-dd}, and it is not complete.");
                }
                else if (projected is { } p)
                {
                    var day = SiteTime.DateAtOrUtc(p, timeZone);
                    if (day > requiredBy)
                    {
                        state = GoalState.Late;
                        evidence.Add($"Reforecast {p:u} is after the required-by date {requiredBy:yyyy-MM-dd}.");
                    }
                    else if (day == requiredBy && state is GoalState.OnTrack or GoalState.BehindPlan)
                    {
                        state = GoalState.AtRisk;
                        evidence.Add($"Reforecast {p:u} lands on the required-by date with no day to spare.");
                    }
                }
            }

            var programId = g.ProgramId ?? (g.OrderNumber is { } n ? orders.GetValueOrDefault(n)?.ProgramId : null);
            var result = new GoalAttainment(g.GoalId, g.Kind, g.Title, g.ParentGoalId, g.OrderNumber, g.DemandId, requiredBy, projected, completed, target,
                target <= 0 ? 0 : Math.Round(Math.Min(1, completed / target), 4), state, behind, Math.Round(remaining, 1), evidence, programId);
            done[g.GoalId] = result;
            return result;
        }

        return goals.Select(g => Assess(g, 0)).OrderBy(g => g.RequiredBy).ThenBy(g => g.GoalId).ToList();
    }

    /// <summary>The states are declared from best to worst.</summary>
    private static GoalState WorstOf(IEnumerable<GoalState> states) => states.Max();
}
