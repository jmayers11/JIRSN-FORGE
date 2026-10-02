using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Kernel.Engines;
using Forge.Planning.Engines;
using Forge.Quality.Contracts;
using Forge.Workforce.Contracts;
using static Forge.Planning.Tests.TestFactory;

namespace Forge.Planning.Tests;

public class OrderReadinessTests
{
    private readonly OrderReadinessEngine engine = new();

    private static readonly RevisionInfo RevisionA = new("ROLL-100", "A", RevisionStatus.Released, Now.AddDays(-10), null);

    private static OrderInfo Order(OrderStatus status = OrderStatus.Released) => new(
        10001,
        "ROLL-100",
        "A",
        10,
        new DateOnly(2026, 10, 9),
        5,
        status,
        status == OrderStatus.Held ? OrderStatus.Released : null,
        null,
        Now.AddHours(-1),
        [
            new OrderOperationInfo(10, "Slit", "SLIT-WC", RunSlitter, ["SLITTER-A", "SLITTER-B"], [], 45, 1, OperationStatus.Pending),
            new OrderOperationInfo(20, "Kit", "SLIT-WC", Kitting, [], [FirstAid], 20, 1, OperationStatus.Pending),
        ],
        []);

    private static TestFactory StaffedSlitting()
    {
        var f = Slitting().SlittingAreaRule();
        f.Add("Sarah", InSlitting(StageRoll), true, null, SlitII, FirstAid);
        f.Add("Michael", InSlitting(Kitting), true, null, SlitII);
        return f;
    }

    [Fact]
    public void First_operation_can_run_and_later_operations_wait_for_it()
    {
        var run = EngineRunner.Run(engine, StaffedSlitting().Build(), new OrderReadinessRequest(Order(), RevisionA), Now);
        var ops = run.Output.Operations;

        Assert.True(ops[0].Executable);
        Assert.Equal("SLITTER-A", ops[0].BestMachine);
        Assert.Equal(2, ops[0].Candidates.Count);
        Assert.Equal(OrderReadinessEngine.NotYetEvaluated, ops[0].NotEvaluated);

        Assert.False(ops[1].Executable);
        Assert.Equal("Predecessor", ops[1].PrimaryConstraint);
        Assert.Contains("next operation 10 can run", run.Output.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Held_order_is_blocked_by_its_status_first()
    {
        var result = engine.Evaluate(StaffedSlitting().Build(), new OrderReadinessRequest(Order(OrderStatus.Held), RevisionA), Now).Output;
        Assert.Equal("Order", result.Operations[0].PrimaryConstraint);
    }

    [Fact]
    public void Superseded_revision_needs_engineering_confirmation()
    {
        var superseded = RevisionA with { EffectiveTo = Now.AddMinutes(-5) };
        var result = engine.Evaluate(StaffedSlitting().Build(), new OrderReadinessRequest(Order(), superseded), Now).Output;

        Assert.Equal("Engineering", result.Operations[0].PrimaryConstraint);
        Assert.Contains(result.Operations[0].Dimensions, d => d.Name == "Engineering" && d.Detail.Contains("superseded", StringComparison.Ordinal));
    }

    [Fact]
    public void Absence_blocks_every_eligible_machine_on_safety()
    {
        var f = Slitting().SlittingAreaRule();
        f.Add("Sarah", InSlitting(StageRoll), true, UnavailabilityKind.CallOut, SlitII, FirstAid);
        f.Add("Michael", InSlitting(Kitting), true, null, SlitII);

        var op = engine.Evaluate(f.Build(), new OrderReadinessRequest(Order(), RevisionA), Now).Output.Operations[0];

        Assert.False(op.Executable);
        Assert.Equal("Safety", op.PrimaryConstraint);
        Assert.All(op.Candidates, c => Assert.Equal("Safety", c.PrimaryConstraint));
    }

    [Fact]
    public void Unassigned_present_people_are_workforce_candidates_but_not_area_safety_coverage()
    {
        var f = Slitting().SlittingAreaRule();
        f.Add("Michael", InSlitting(Kitting), true, null);
        f.Add("Priya", null, true, null, SlitII); // clocked in, not yet assigned anywhere

        var op = engine.Evaluate(f.Build(), new OrderReadinessRequest(Order(), RevisionA), Now).Output.Operations[0];

        Assert.Contains(op.Dimensions, d => d.Name == "Workforce" && d.Outcome == EvidenceOutcome.Pass && d.Detail.Contains("Priya", StringComparison.Ordinal));
        Assert.Contains(op.Dimensions, d => d.Name == "Safety" && d.Outcome == EvidenceOutcome.Fail);
    }

    [Fact]
    public void Additional_required_qualifications_must_all_be_held()
    {
        var f = Slitting().SlittingAreaRule();
        f.Add("Michael", InSlitting(Kitting), true, null, SlitII);
        var order = Order() with
        {
            Operations = [new OrderOperationInfo(10, "Kit", "SLIT-WC", Kitting, [], [FirstAid], 20, 1, OperationStatus.Pending)],
        };

        var op = engine.Evaluate(f.Build(), new OrderReadinessRequest(order, RevisionA), Now).Output.Operations[0];

        Assert.False(op.Executable);
        Assert.Equal("Workforce", op.PrimaryConstraint);
        Assert.Contains(op.Dimensions, d => d.Name == "Safety" && d.Outcome == EvidenceOutcome.Pass); // kitting has no rule
    }

    private static NcrInfo OpenNcr(long id) => new(
        id, "ROLL-100", 10001, 10, null, 2, "Burr on edge", "Edge burr over limit", NcrStatus.Open, null, null, "insp", Now.AddMinutes(-10), null, null, null, null, null);

    [Fact]
    public void An_operation_waits_for_the_inspection_after_the_one_before_it()
    {
        var order = Order() with
        {
            Operations =
            [
                new OrderOperationInfo(10, "Slit", "SLIT-WC", RunSlitter, ["SLITTER-A"], [], 45, 1, OperationStatus.Complete),
                new OrderOperationInfo(20, "Kit", "SLIT-WC", Kitting, [], [FirstAid], 20, 1, OperationStatus.Pending),
            ],
        };
        var quality = new OrderQuality(10001, [10], [], []);

        var op = engine.Evaluate(StaffedSlitting().Build(), new OrderReadinessRequest(order, RevisionA, null, quality), Now).Output.Operations[1];

        Assert.False(op.Executable);
        Assert.Equal("Quality", op.PrimaryConstraint);
        Assert.Contains(op.Dimensions, d => d.Name == "Quality" && d.Detail.Contains("operation 10 has not passed", StringComparison.Ordinal));
        Assert.Equal(OrderReadinessEngine.NotYetEvaluatedWithQuality, op.NotEvaluated);
    }

    [Fact]
    public void A_passed_inspection_lets_the_next_operation_run()
    {
        var order = Order() with
        {
            Operations =
            [
                new OrderOperationInfo(10, "Slit", "SLIT-WC", RunSlitter, ["SLITTER-A"], [], 45, 1, OperationStatus.Complete),
                new OrderOperationInfo(20, "Kit", "SLIT-WC", Kitting, [], [FirstAid], 20, 1, OperationStatus.Pending),
            ],
        };

        var op = engine.Evaluate(StaffedSlitting().Build(), new OrderReadinessRequest(order, RevisionA, null, new OrderQuality(10001, [10], [10], [])), Now).Output.Operations[1];

        Assert.True(op.Executable);
        Assert.Contains(op.Dimensions, d => d.Name == "Quality" && d.Outcome == EvidenceOutcome.Pass);
    }

    [Fact]
    public void An_undecided_nonconformance_holds_every_unfinished_operation()
    {
        var quality = new OrderQuality(10001, [], [], [OpenNcr(9001)]);
        var result = engine.Evaluate(StaffedSlitting().Build(), new OrderReadinessRequest(Order(), RevisionA, null, quality), Now).Output;

        Assert.All(result.Operations, o => Assert.Contains(o.Dimensions, d => d.Name == "Quality" && d.Outcome == EvidenceOutcome.Fail && d.Detail.Contains("NCR 9001", StringComparison.Ordinal)));
        Assert.Equal("Quality", result.Operations[0].PrimaryConstraint);
        Assert.Contains("blocked by Quality", result.Summary, StringComparison.Ordinal);
    }
}
