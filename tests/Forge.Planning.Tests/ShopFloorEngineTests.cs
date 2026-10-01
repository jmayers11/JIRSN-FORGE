using Forge.Competency.Contracts;
using Forge.Execution.Contracts;
using Forge.Execution.Engines;
using Forge.Kernel.Engines;
using Forge.Planning.Engines;

namespace Forge.Planning.Tests;

/// <summary>Operator authorization (spec §23-25, §80), attainment, and remaining work on started operations.</summary>
public class ShopFloorEngineTests
{
    private static readonly DateTimeOffset Now = TestFactory.Now;
    private static readonly Guid Alex = new("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid Michael = new("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid Sarah = new("00000000-0000-0000-0000-00000000000c");
    private readonly OperatorAuthorizationEngine engine = new();

    private static readonly QualificationTypeInfo[] Types =
    [
        new("SLIT-II", "Slitting Level II", QualificationKind.Machine, 365, []),
        new("TRAIN-SLIT", "Slitting trainer", QualificationKind.Trainer, 365, ["SLIT-II"]),
    ];

    private static PersonQualification Q(Guid person, string code, QualificationStatus status = QualificationStatus.Qualified, DateTimeOffset? expires = null) =>
        new(person, code, status, status == QualificationStatus.Qualified ? Now.AddDays(-30) : null, expires);

    private static OperatorSnapshot Floor(params FloorPerson[] people) => new(Types, people);

    private OperatorCheck Check(OperatorSnapshot floor, Guid op, Guid? trainer = null) =>
        engine.Evaluate(floor, new OperatorRequest(op, ["SLIT-II"], trainer), Now).Output;

    [Fact]
    public void A_qualified_present_operator_may_start()
    {
        var check = Check(Floor(new FloorPerson(Sarah, "Sarah", true, [Q(Sarah, "SLIT-II")])), Sarah);
        Assert.Equal(OperatorDecision.Authorized, check.Decision);
    }

    [Fact]
    public void Scheduled_is_not_present_clock_in_first()
    {
        var check = Check(Floor(new FloorPerson(Sarah, "Sarah", false, [Q(Sarah, "SLIT-II")])), Sarah);
        Assert.Equal(OperatorDecision.NotPresent, check.Decision);
    }

    [Fact]
    public void Expired_qualification_does_not_count()
    {
        var check = Check(Floor(new FloorPerson(Sarah, "Sarah", true, [Q(Sarah, "SLIT-II", expires: Now.AddDays(-1))])), Sarah);
        Assert.Equal(OperatorDecision.NotEligible, check.Decision);
        Assert.Contains("SLIT-II", check.Missing);
    }

    [Fact]
    public void Not_in_training_means_not_eligible_and_says_whom_to_contact()
    {
        var check = Check(Floor(new FloorPerson(Alex, "Alex", true, [])), Alex);
        Assert.Equal(OperatorDecision.NotEligible, check.Decision);
        Assert.Contains("training coordinator", check.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Trainee_needs_a_trainer_and_is_told_who_is_available()
    {
        var floor = Floor(
            new FloorPerson(Alex, "Alex", true, [Q(Alex, "SLIT-II", QualificationStatus.InTraining)]),
            new FloorPerson(Michael, "Michael", true, [Q(Michael, "SLIT-II"), Q(Michael, "TRAIN-SLIT")]),
            new FloorPerson(Sarah, "Sarah", true, [Q(Sarah, "SLIT-II")]));
        var check = Check(floor, Alex);
        Assert.Equal(OperatorDecision.TrainerRequired, check.Decision);
        Assert.Equal(new[] { "Michael" }, check.AvailableTrainers); // Sarah operates but is not a trainer
    }

    [Fact]
    public void Spec_80_valid_trainer_starts_supervised_training_and_grants_nothing()
    {
        var floor = Floor(
            new FloorPerson(Alex, "Alex", true, [Q(Alex, "SLIT-II", QualificationStatus.InTraining)]),
            new FloorPerson(Michael, "Michael", true, [Q(Michael, "SLIT-II"), Q(Michael, "TRAIN-SLIT")]));
        var run = EngineRunner.Run(engine, floor, new OperatorRequest(Alex, ["SLIT-II"], Michael), Now);
        Assert.Equal(OperatorDecision.SupervisedTraining, run.Output.Decision);
        Assert.Contains("does not grant", run.Output.Explanation, StringComparison.Ordinal);
        Assert.Contains(run.Evidence.Children, c => c.Check == "Trainer" && c.Outcome == EvidenceOutcome.Pass);
    }

    [Fact]
    public void Qualified_operator_is_not_a_trainer()
    {
        var floor = Floor(
            new FloorPerson(Alex, "Alex", true, [Q(Alex, "SLIT-II", QualificationStatus.InTraining)]),
            new FloorPerson(Sarah, "Sarah", true, [Q(Sarah, "SLIT-II")]));
        var check = Check(floor, Alex, Sarah);
        Assert.Equal(OperatorDecision.TrainerRequired, check.Decision);
        Assert.Contains("not an authorized trainer", check.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Trainer_must_be_present_and_cannot_be_the_trainee()
    {
        var floor = Floor(
            new FloorPerson(Alex, "Alex", true, [Q(Alex, "SLIT-II", QualificationStatus.InTraining), Q(Alex, "TRAIN-SLIT")]),
            new FloorPerson(Michael, "Michael", false, [Q(Michael, "SLIT-II"), Q(Michael, "TRAIN-SLIT")]));
        Assert.Contains("no current clock-in", Check(floor, Alex, Michael).Explanation, StringComparison.Ordinal);
        Assert.Contains("cannot supervise themselves", Check(floor, Alex, Alex).Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Same_inputs_give_the_same_fingerprint()
    {
        var floor = Floor(new FloorPerson(Sarah, "Sarah", true, [Q(Sarah, "SLIT-II")]));
        var a = EngineRunner.Run(engine, floor, new OperatorRequest(Sarah, ["SLIT-II"], null), Now);
        var b = EngineRunner.Run(engine, floor, new OperatorRequest(Sarah, ["SLIT-II"], null), Now);
        Assert.Equal(a.OutputFingerprint, b.OutputFingerprint);
    }

    private static OrderInfo Order(long number, decimal quantity, params OrderOperationInfo[] ops) =>
        new(number, "ROLL-100", "A", quantity, DateOnly.FromDateTime(Now.UtcDateTime.AddDays(5)), 3, OrderStatus.Released, null, null, Now.AddDays(-1), ops, []);

    private static OrderOperationInfo Op(int seq, OperationStatus status, decimal good = 0, DateTimeOffset? completed = null) =>
        new(seq, "Slit", "SLIT-WC", "RUN_SLITTER", ["SLITTER-A"], [], 120m, 1, status, good, 0, null, completed);

    [Fact]
    public void Started_operation_schedules_only_the_work_left()
    {
        var order = Order(1, 100m, Op(10, OperationStatus.InProgress, good: 25m));
        Assert.Equal(90m, SchedulingEngine.RemainingMinutes(order, order.Operations[0]));
        var pending = Order(2, 100m, Op(10, OperationStatus.Pending));
        Assert.Equal(120m, SchedulingEngine.RemainingMinutes(pending, pending.Operations[0]));
    }

    private static ScheduledOperation Planned(long order, int seq, DateTimeOffset end, decimal minutes) =>
        new(order, seq, "Slit", "SLIT-WC", "SLITTER-A", "RUN_SLITTER", true, end.AddMinutes(-(double)minutes), end, minutes, "Now", "", [], [], []);

    [Fact]
    public void Attainment_weights_planned_minutes_and_ignores_cancelled_orders()
    {
        var from = Now.AddHours(-24);
        var plan = new ScheduleResult(from, Now, [], [
            Planned(1, 10, Now.AddHours(-20), 60m),   // done in time
            Planned(2, 10, Now.AddHours(-10), 180m),  // still open
            Planned(3, 10, Now.AddHours(-5), 60m),    // cancelled
            Planned(4, 10, Now.AddHours(5), 60m),     // due after the window: not counted
        ]);
        var orders = new[]
        {
            Order(1, 10m, Op(10, OperationStatus.Complete, 10m, Now.AddHours(-19))),
            Order(2, 10m, Op(10, OperationStatus.InProgress, 4m)),
            Order(3, 10m, Op(10, OperationStatus.Pending)) with { Status = OrderStatus.Cancelled },
        };
        var run = EngineRunner.Run(new AttainmentEngine(), new AttainmentSnapshot(7, from.AddHours(-1), plan, orders), new AttainmentRequest(from, Now), Now);
        Assert.Equal(2, run.Output.PlannedOperations);
        Assert.Equal(1, run.Output.CompletedOperations);
        Assert.Equal(25.0m, run.Output.Percent);
        Assert.Contains(run.Output.Lines, l => l.OrderNumber == 3 && l.Status == AttainmentLineStatus.Cancelled);
    }

    [Fact]
    public void No_approved_plan_means_no_attainment_figure()
    {
        var run = EngineRunner.Run(new AttainmentEngine(), new AttainmentSnapshot(null, null, null, []), new AttainmentRequest(Now.AddHours(-24), Now), Now);
        Assert.Null(run.Output.Percent);
        Assert.Equal(EvidenceOutcome.Indeterminate, run.Evidence.Outcome);
    }
}
