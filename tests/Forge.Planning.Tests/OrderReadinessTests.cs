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
    public void Superseded_wip_continuation_requires_exact_released_source_order_part_revision_and_complete_old_decision()
    {
        var order = WipOrder();
        var old = RevisionA with { EffectiveTo = Now.AddMinutes(-10) };
        var continuation = Continuation(old);
        Assert.True(continuation.Permits(order, old, Now));
        var allowed = engine.Evaluate(StaffedSlitting().Build(), new OrderReadinessRequest(order, old, Continuation: continuation), Now).Output.Operations[0];
        Assert.True(allowed.Executable);
        Assert.Contains(allowed.Dimensions, dimension => dimension.Name == "Engineering"
            && dimension.Outcome == EvidenceOutcome.Pass && dimension.Detail.Contains("WIP disposition 41", StringComparison.Ordinal));

        var wrongSources = new[]
        {
            continuation with { Action = continuation.Action with { Status = ChangeActionStatus.Open } },
            continuation with { Action = continuation.Action with { Resolution = nameof(WipResolution.ReworkToNewRevision) } },
            continuation with { Action = continuation.Action with { Kind = ChangeActionKind.PurchaseReview } },
            continuation with { Action = continuation.Action with { Target = "99999" } },
            continuation with { Action = continuation.Action with { PartNumber = "OTHER-PART" } },
            continuation with { Action = continuation.Action with { ProgramId = Guid.NewGuid() } },
            continuation with { Action = continuation.Action with { ChangeNumber = "OTHER-CHANGE" } },
            continuation with { Action = continuation.Action with { Revision = "C" } },
            continuation with { Action = continuation.Action with { ResolvedBy = null } },
            continuation with { Action = continuation.Action with { ResolvedAt = Now.AddMinutes(1) } },
            continuation with { Action = continuation.Action with { Notes = null } },
            continuation with { Change = continuation.Change with { Status = ChangeStatus.Approved } },
            continuation with { Change = continuation.Change with { Lines = [continuation.Change.Lines[0] with { FromRevision = "X" }] } },
            continuation with { Change = continuation.Change with { Lines = [continuation.Change.Lines[0] with { RemovedAt = Now }] } },
            continuation with { SupersedingRevision = continuation.SupersedingRevision with { EffectiveFrom = Now.AddMinutes(-11) } },
        };
        foreach (var wrong in wrongSources)
        {
            Assert.False(wrong.Permits(order, old, Now));
            var blocked = engine.Evaluate(StaffedSlitting().Build(), new OrderReadinessRequest(order, old, Continuation: wrong), Now).Output.Operations[0];
            Assert.Equal("Engineering", blocked.PrimaryConstraint);
        }
        Assert.False(continuation.Permits(Order(), old, Now)); // a future order is not the recorded WIP
        Assert.False(continuation.Permits(order with { Revision = "X" }, old, Now));
    }

    [Fact]
    public void Authorized_old_revision_continuation_does_not_bypass_order_or_safety_gates()
    {
        var old = RevisionA with { EffectiveTo = Now.AddMinutes(-10) };
        var continuation = Continuation(old);
        var held = WipOrder() with { Status = OrderStatus.Held };
        var blocked = engine.Evaluate(StaffedSlitting().Build(), new OrderReadinessRequest(held, old, Continuation: continuation), Now).Output.Operations[0];
        Assert.Equal("Order", blocked.PrimaryConstraint);
        var absent = Slitting().SlittingAreaRule();
        absent.Add("Sarah", InSlitting(StageRoll), true, UnavailabilityKind.CallOut, SlitII, FirstAid);
        absent.Add("Michael", InSlitting(Kitting), true, null, SlitII);
        var unsafeOperation = engine.Evaluate(absent.Build(), new OrderReadinessRequest(WipOrder(), old, Continuation: continuation), Now).Output.Operations[0];
        Assert.Equal("Safety", unsafeOperation.PrimaryConstraint);
        Assert.Contains(unsafeOperation.Dimensions, dimension => dimension.Name == "Engineering" && dimension.Outcome == EvidenceOutcome.Pass);
    }

    private static OrderInfo WipOrder()
    {
        var order = Order();
        return order with { Operations = order.Operations.Select(operation => operation.Sequence == 10
            ? operation with { Status = OperationStatus.InProgress } : operation).ToArray() };
    }

    private static WipContinuationEvidence Continuation(RevisionInfo old)
    {
        var line = new ChangeLineInfo("ECN-WIP", 1, ChangeLineKind.ItemRevision, null, "ROLL-100", "A", "B", null,
            "person:author", Now.AddHours(-1), null, null);
        var change = new ChangeInfo(
            Number: "ECN-WIP", Title: "Reviewed WIP transition", Class: ChangeClass.Minor, Type: ChangeType.Design,
            Priority: ChangePriority.Routine, Reason: "Released engineering change", Description: null, ProgramId: null,
            Customer: null, RequestedEffective: null, Status: ChangeStatus.Released, ReviewRound: 1,
            Originator: "person:author", CreatedAt: Now.AddHours(-1), SubmittedAt: Now.AddMinutes(-20),
            ApprovedBy: "person:reviewer", ApprovedAt: Now.AddMinutes(-15), ReleasedBy: "person:releaser",
            ReleasedAt: Now.AddMinutes(-10), EffectiveFrom: DateOnly.FromDateTime(Now.UtcDateTime),
            ClosedBy: null, ClosedAt: null, ClosedReason: null, Lines: [line], Reviews: []);
        var action = new ChangeActionInfo(41, change.Number, ChangeActionKind.WipDisposition, "10001", "ROLL-100", null, "B",
            null, null, "Recorded WIP", ChangeActionStatus.Resolved, nameof(WipResolution.CompleteAtOldRevision),
            "Approved customer deviation DV-WIP", "system:propagation", Now.AddMinutes(-9), "person:quality", Now.AddMinutes(-8));
        return new WipContinuationEvidence(change, action, new RevisionInfo("ROLL-100", "B", RevisionStatus.Released, old.EffectiveTo, null));
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
