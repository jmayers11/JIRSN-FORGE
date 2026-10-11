using Forge.Execution.Contracts;
using Forge.Planning.Engines;

namespace Forge.Planning.Tests;

/// <summary>V3.7: dates cascade down the breakdown; behind plan is not at risk; delivery and production goals are kept apart.</summary>
public class GoalAttainmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 1);

    private static GoalInfo Goal(long id, GoalKind kind, long? parent = null, long? order = null, long? demand = null, DateOnly? requiredBy = null, int? lead = null) =>
        new(id, kind, $"Goal {id}", parent, order, demand, requiredBy, lead, null, GoalStatus.Open, Now, "person:1", Now, "person:1");

    private static OrderInfo Order(long n, decimal good, OrderStatus status = OrderStatus.Released) =>
        new(n, "ROLL", "A", 10, Today.AddDays(10), 3, status, null, null, Now,
            [new OrderOperationInfo(10, "Slit", "SLIT-WC", "RUN", [], [], 60, 1, good >= 10 ? OperationStatus.Complete : OperationStatus.InProgress, good, 0)], []);

    private static GoalRiskLine Risk(long n, GoalRiskState state, DateTimeOffset? projected, bool behind = false) =>
        new(n, "ROLL", 3, Today.AddDays(10), Now.AddDays(10), projected, state, behind, state is GoalRiskState.GoalAtRisk or GoalRiskState.Late or GoalRiskState.Unschedulable,
            60, 0, 10, [], ["evidence"], null);

    private static Dictionary<long, GoalAttainment> Run(GoalInfo[] goals, GoalRiskLine[]? risk = null, OrderInfo[]? orders = null, GoalDemand[]? demand = null) =>
        GoalAttainmentEngine.Evaluate(goals, (risk ?? []).ToDictionary(r => r.OrderNumber), (orders ?? []).ToDictionary(o => o.OrderNumber), (demand ?? []).ToDictionary(d => d.DemandId), Today)
            .ToDictionary(g => g.GoalId);

    [Fact]
    public void Required_by_dates_cascade_down_the_breakdown()
    {
        var r = Run([Goal(1, GoalKind.Delivery, requiredBy: Today.AddDays(20)), Goal(2, GoalKind.Production, 1, lead: 3), Goal(3, GoalKind.Production, 2, lead: 2), Goal(4, GoalKind.Production, 2, requiredBy: Today.AddDays(5))]);
        Assert.Equal((Today.AddDays(17), Today.AddDays(15), Today.AddDays(5)), (r[2].RequiredBy, r[3].RequiredBy, r[4].RequiredBy));
    }

    [Fact]
    public void Behind_plan_is_kept_apart_from_at_risk_and_the_goal_date_decides()
    {
        var goals = new[] { Goal(1, GoalKind.Production, order: 7, requiredBy: Today.AddDays(10)) };
        var behind = Run(goals, [Risk(7, GoalRiskState.BehindPlanRecoverable, Now.AddDays(5), behind: true)], [Order(7, 4)]);
        Assert.Equal((GoalState.BehindPlan, true, 0.4m), (behind[1].State, behind[1].BehindPlan, behind[1].Attainment));
        Assert.Equal(36m, behind[1].RemainingMinutes); // 60 min × 60 % of the quantity left

        // The order is on time for its own due date, but the goal needs it sooner.
        var sooner = Run([Goal(1, GoalKind.Production, order: 7, requiredBy: Today.AddDays(3))], [Risk(7, GoalRiskState.OnTrack, Now.AddDays(5))], [Order(7, 4)]);
        Assert.Equal(GoalState.Late, sooner[1].State);
        var onTheDay = Run([Goal(1, GoalKind.Production, order: 7, requiredBy: Today.AddDays(5))], [Risk(7, GoalRiskState.OnTrack, Now.AddDays(5))], [Order(7, 4)]);
        Assert.Equal(GoalState.AtRisk, onTheDay[1].State);
        var unschedulable = Run(goals, [Risk(7, GoalRiskState.Unschedulable, null)], [Order(7, 0)]);
        Assert.Equal(GoalState.AtRisk, unschedulable[1].State);
        var complete = Run([Goal(1, GoalKind.Production, order: 7, requiredBy: Today.AddDays(-5))], orders: [Order(7, 10, OrderStatus.Completed)]);
        Assert.Equal((GoalState.Complete, 1m), (complete[1].State, complete[1].Attainment));
    }

    [Fact]
    public void A_delivery_goal_follows_shipments_and_the_production_below_it()
    {
        var goals = new[] { Goal(1, GoalKind.Delivery, demand: 50, requiredBy: Today.AddDays(12)), Goal(2, GoalKind.Production, 1, order: 7, lead: 2) };
        var r = Run(goals, [Risk(7, GoalRiskState.GoalAtRisk, Now.AddDays(9))], [Order(7, 2)], [new GoalDemand(50, "ROLL", 10, 4, Today.AddDays(12))]);
        Assert.Equal((GoalState.AtRisk, 4m, 10m, Now.AddDays(9)), (r[1].State, r[1].Done, r[1].Target, r[1].Projected));
        var shipped = Run(goals, [Risk(7, GoalRiskState.GoalAtRisk, Now.AddDays(9))], [Order(7, 2)], [new GoalDemand(50, "ROLL", 10, 10, Today.AddDays(12))]);
        Assert.Equal(GoalState.Complete, shipped[1].State);
        var nothingBelow = Run([Goal(1, GoalKind.Delivery, demand: 50, requiredBy: Today.AddDays(12))], demand: [new GoalDemand(50, "ROLL", 10, 0, Today.AddDays(12))]);
        Assert.Equal(GoalState.NoProjection, nothingBelow[1].State);
        var overdue = Run([Goal(1, GoalKind.Delivery, demand: 50, requiredBy: Today.AddDays(-1))], demand: [new GoalDemand(50, "ROLL", 10, 0, Today.AddDays(-1))]);
        Assert.Equal(GoalState.Late, overdue[1].State);
    }

    [Fact]
    public void A_goal_with_neither_order_nor_demand_rolls_up_its_children()
    {
        var goals = new[] { Goal(1, GoalKind.Production, requiredBy: Today.AddDays(30)), Goal(2, GoalKind.Production, 1, order: 7, lead: 0), Goal(3, GoalKind.Production, 1, order: 8, lead: 0) };
        var r = Run(goals, [Risk(8, GoalRiskState.BehindPlanRecoverable, Now.AddDays(4), behind: true)], [Order(7, 10, OrderStatus.Completed), Order(8, 5)]);
        Assert.Equal((GoalState.BehindPlan, true, 1m, 2m, 0.5m), (r[1].State, r[1].BehindPlan, r[1].Done, r[1].Target, r[1].Attainment));
    }
}
