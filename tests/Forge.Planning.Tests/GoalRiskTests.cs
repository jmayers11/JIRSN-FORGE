using Forge.Execution.Contracts;
using Forge.Planning.Engines;
using static Forge.Planning.Tests.TestFactory;

namespace Forge.Planning.Tests;

public sealed class GoalRiskTests
{
    private static ScheduledOperation Op(long order, int sequence, DateTimeOffset start, DateTimeOffset end, decimal minutes = 60) => new(
        order,
        sequence,
        sequence == 10 ? "Run" : "Pack",
        "SLIT-WC",
        "SLITTER-A",
        RunSlitter,
        true,
        start,
        end,
        minutes,
        sequence == 10 ? "Now" : "Predecessor",
        sequence == 10 ? "Starts at the earliest possible time." : "Previous operation complete.",
        [new ScheduledSegment(start, end, [])],
        [],
        []);

    private static ScheduledOrder Order(long order, DateTimeOffset due, DateTimeOffset? projected, DeliveryStatus status = DeliveryStatus.OnTime) => new(
        order,
        "ROLL-100",
        3,
        DateOnly.FromDateTime(due.UtcDateTime),
        due,
        projected,
        status,
        projected is null ? "No completion." : $"Projected completion {projected:u}, due {due:u}.");

    private static GoalRiskResult Run(ScheduleResult schedule, params GoalActualOperation[] actuals) =>
        new GoalRiskEngine().Evaluate(new GoalRiskSnapshot(schedule, actuals), new GoalRiskRequest(Now), Now).Output;

    [Fact]
    public void Behind_plan_is_not_goal_at_risk_when_recovery_slack_remains()
    {
        var due = Now.AddDays(2);
        var schedule = new ScheduleResult(
            Now.AddDays(-1),
            Now.AddDays(3),
            [Order(10001, due, due.AddHours(-16))],
            [Op(10001, 10, Now.AddHours(-2), Now.AddHours(-1)), Op(10001, 20, Now.AddHours(4), Now.AddHours(5))]);

        var line = Assert.Single(Run(schedule).Lines);

        Assert.Equal(GoalRiskState.BehindPlanRecoverable, line.State);
        Assert.True(line.BehindPlan);
        Assert.False(line.AtRisk);
        Assert.Contains("operation 10", Assert.Single(line.MissedOperations), StringComparison.Ordinal);
        Assert.Contains("recovery slack", line.Evidence[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void Goal_at_risk_is_distinct_from_ordinary_behind_plan()
    {
        var due = Now.AddHours(9);
        var schedule = new ScheduleResult(
            Now.AddDays(-1),
            Now.AddDays(2),
            [Order(10002, due, due.AddHours(-2), DeliveryStatus.AtRisk)],
            [Op(10002, 10, Now.AddHours(-2), Now.AddHours(-1)), Op(10002, 20, Now.AddHours(6), Now.AddHours(7))]);

        var line = Assert.Single(Run(schedule).Lines);

        Assert.Equal(GoalRiskState.GoalAtRisk, line.State);
        Assert.True(line.BehindPlan);
        Assert.True(line.AtRisk);
        Assert.Contains("recovery margin", line.Evidence[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void Completed_operations_remove_the_behind_plan_signal()
    {
        var due = Now.AddDays(1);
        var schedule = new ScheduleResult(
            Now.AddDays(-1),
            Now.AddDays(2),
            [Order(10003, due, Now.AddHours(3))],
            [Op(10003, 10, Now.AddHours(-2), Now.AddHours(-1)), Op(10003, 20, Now.AddHours(2), Now.AddHours(3))]);

        var line = Assert.Single(Run(schedule, new GoalActualOperation(10003, 10, OperationStatus.Complete, Now.AddMinutes(-30))).Lines);

        Assert.Equal(GoalRiskState.OnTrack, line.State);
        Assert.False(line.BehindPlan);
        Assert.False(line.AtRisk);
        Assert.Equal(60, line.PlannedMinutesComplete);
    }

    [Fact]
    public void Late_and_unschedulable_goals_are_at_risk_even_without_intermediate_plan_misses()
    {
        var due = Now.AddHours(4);
        var schedule = new ScheduleResult(
            Now,
            Now.AddDays(2),
            [Order(10004, due, due.AddHours(1), DeliveryStatus.Late), Order(10005, due, null, DeliveryStatus.Unschedulable)],
            [Op(10004, 10, Now.AddHours(1), Now.AddHours(2)), Op(10005, 10, Now.AddHours(1), Now.AddHours(2))]);

        var result = Run(schedule);

        Assert.Equal(2, result.AtRisk);
        Assert.Equal(1, result.Late);
        Assert.Equal(1, result.Unschedulable);
        Assert.Contains(result.Lines, l => l.OrderNumber == 10004 && l.State == GoalRiskState.Late);
        Assert.Contains(result.Lines, l => l.OrderNumber == 10005 && l.State == GoalRiskState.Unschedulable);
    }
}
