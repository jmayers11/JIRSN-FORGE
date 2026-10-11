using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Engines;
using Forge.Planning.Engines;
using Forge.Resources.Contracts;
using static Forge.Planning.Tests.TestFactory;

namespace Forge.Planning.Tests;

/// <summary>V3.9: tooling, holds, material at the point of use, training on controlled documents and presence levels.</summary>
public class ExecutableReadinessTests
{
    private readonly OrderReadinessEngine engine = new();

    private static readonly RevisionInfo RevisionA = new("ROLL-100", "A", RevisionStatus.Released, Now.AddDays(-10), null);

    private static readonly OrderMaterialStatus MaterialReady = new(10001, MaterialState.Ready, null, false, "COIL-AL: 26 needed, all on hand.",
        [new MaterialLineStatus("COIL-AL", 26, 0, 26, 26, 0, null, [])]);

    private static OrderInfo Order() => new(
        10001, "ROLL-100", "A", 10, new DateOnly(2026, 10, 9), 5, OrderStatus.Released, null, null, Now.AddHours(-1),
        [new OrderOperationInfo(10, "Slit", "SLIT-WC", RunSlitter, ["SLITTER-A", "SLITTER-B"], [], 45, 1, OperationStatus.Pending)],
        []);

    private static TestFactory Staffed()
    {
        var f = Slitting().SlittingAreaRule();
        f.Add("Sarah", InSlitting(StageRoll), true, null, SlitII, FirstAid);
        f.Add("Michael", InSlitting(Kitting), true, null, SlitII);
        return f;
    }

    private static SiteSnapshot WithCaliper(SiteSnapshot snapshot, DateOnly due) => snapshot with
    {
        ToolTypes = [new ToolTypeInfo("CALIPER", "Caliper", ToolKind.Gauge, 180, null, null)],
        Tools = [new ToolInfo("CAL-1", "CALIPER", "Caliper", null, "CRIB", ToolCustody.InHouse, null, ToolCondition.Good, [], [], 0, 0, due, "LAB-1", ToolStatus.Available, Now.AddDays(-200), "qe")],
        ToolEvents = [new ToolEvent(1, "CAL-1", ToolEventKind.Registered, Now.AddDays(-200), "qe", DueOn: due)],
        ToolRequirements = [new ToolRequirement(1, RunSlitter, null, "CALIPER", 1, Now.AddDays(-30), "me")],
    };

    private OperationReadiness Op(SiteSnapshot snapshot, OrderReadinessRequest? request = null) =>
        EngineRunner.Run(engine, snapshot, request ?? new OrderReadinessRequest(Order(), RevisionA, MaterialReady), Now).Output.Operations[0];

    [Fact]
    public void Machine_material_and_operator_ready_but_an_expired_gauge_makes_it_not_executable()
    {
        var ready = Op(WithCaliper(Staffed().Build(), new DateOnly(2027, 1, 1)));
        Assert.True(ready.Executable);
        Assert.Contains(ready.Dimensions, d => d.Name == "Tooling" && d.Outcome == EvidenceOutcome.Pass && d.Detail.Contains("CAL-1", StringComparison.Ordinal));
        Assert.DoesNotContain("Tooling", ready.NotEvaluated);

        var expired = Op(WithCaliper(Staffed().Build(), new DateOnly(2026, 9, 30)));
        Assert.False(expired.Executable);
        Assert.Equal("Tooling", expired.PrimaryConstraint);
        Assert.Contains(expired.Dimensions, d => d.Name == "Equipment" && d.Outcome == EvidenceOutcome.Pass);
        Assert.Contains(expired.Dimensions, d => d.Name == "Material" && d.Outcome == EvidenceOutcome.Pass);
        Assert.Contains(expired.Dimensions, d => d.Name == "Workforce" && d.Outcome == EvidenceOutcome.Pass);
        Assert.Contains("was due for calibration 2026-09-30", expired.Dimensions.Single(d => d.Name == "Tooling").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_machine_hold_moves_the_work_to_another_machine_and_a_work_center_hold_stops_it()
    {
        var hold = new ProductionHold(501, HoldScope.Machine, null, null, null, null, "SLITTER-A", "Guard damaged", Now.AddHours(-1), "sup");
        var machineHeld = Op(Staffed().Build() with { ProductionHolds = [hold] });
        Assert.True(machineHeld.Executable);
        Assert.Equal("SLITTER-B", machineHeld.BestMachine);

        var wcHold = hold with { Scope = HoldScope.WorkCenter, MachineCode = null, WorkCenterCode = "SLIT-WC" };
        var held = Op(Staffed().Build() with { ProductionHolds = [wcHold] });
        Assert.False(held.Executable);
        Assert.Equal("Holds", held.PrimaryConstraint);
        Assert.Contains("Hold 501 on work center SLIT-WC: Guard damaged", held.Dimensions.Single(d => d.Name == "Holds").Detail, StringComparison.Ordinal);

        var released = Op(Staffed().Build() with { ProductionHolds = [wcHold with { ReleasedAt = Now, ReleasedBy = "qe", ReleaseReason = "Fixed" }] });
        Assert.True(released.Executable);
    }

    [Fact]
    public void Material_must_be_at_the_point_of_use_where_the_work_center_has_one()
    {
        static StockPosition At(string location, string? workCenter, decimal quantity) =>
            new("COIL-AL", "L-1", location, location, workCenter is null ? LocationKind.Stores : LocationKind.Staging, StockStatus.Released, quantity, Custody.Company, null, true, workCenter, null);
        var snapshot = Staffed().Build();
        OrderReadinessRequest Request(OrderPointOfUse pointOfUse) => new(Order(), RevisionA, MaterialReady, PointOfUse: pointOfUse);

        var none = Op(snapshot, Request(new OrderPointOfUse([], new HashSet<string>(StringComparer.Ordinal))));
        Assert.True(none.Executable);
        Assert.Contains(none.Dimensions, d => d.Name == "PointOfUse" && d.Outcome == EvidenceOutcome.Info);

        var centers = new HashSet<string>(StringComparer.Ordinal) { "SLIT-WC" };
        var notStaged = Op(snapshot, Request(new OrderPointOfUse([At("SLIT-STAGE", "SLIT-WC", 10)], centers)));
        Assert.False(notStaged.Executable);
        Assert.Equal("PointOfUse", notStaged.PrimaryConstraint);
        Assert.Contains("COIL-AL needs 26, 10 there", notStaged.Dimensions.Single(d => d.Name == "PointOfUse").Detail, StringComparison.Ordinal);

        var staged = Op(snapshot, Request(new OrderPointOfUse([At("SLIT-STAGE", "SLIT-WC", 30)], centers)));
        Assert.True(staged.Executable);
    }

    [Fact]
    public void Training_on_controlled_documents_warns_or_stops_by_policy()
    {
        var f = Staffed();
        var snapshot = f.Build();
        var documents = new List<TrainingRequirement> { new("WI-SLIT", "B", null) };
        OrderReadinessRequest Request(TrainingPolicy policy) => new(Order(), RevisionA, MaterialReady, Training: new OrderTraining(policy, documents));

        Assert.DoesNotContain(Op(snapshot, Request(TrainingPolicy.Off)).Dimensions, d => d.Name == "Training");

        var warned = Op(snapshot, Request(TrainingPolicy.Warn));
        Assert.True(warned.Executable);
        Assert.Contains(warned.Dimensions, d => d.Name == "Training" && d.Outcome == EvidenceOutcome.Info && d.Detail.Contains("Michael (WI-SLIT rev B)", StringComparison.Ordinal));

        var enforced = Op(snapshot, Request(TrainingPolicy.Enforce));
        Assert.False(enforced.Executable);
        Assert.Equal("Training", enforced.PrimaryConstraint);

        // Trained on an earlier revision does not count; on the revision in force it does.
        var sarah = snapshot.People.First(p => p.Name == "Sarah");
        var old = snapshot with { People = snapshot.People.Select(p => p == sarah ? p with { TrainedDocuments = new HashSet<string>(StringComparer.Ordinal) { "WI-SLIT@A" } } : p).ToList() };
        Assert.False(Op(old, Request(TrainingPolicy.Enforce)).Executable);
        var trained = snapshot with { People = snapshot.People.Select(p => p == sarah ? p with { TrainedDocuments = new HashSet<string>(StringComparer.Ordinal) { "WI-SLIT@B" } } : p).ToList() };
        Assert.True(Op(trained, Request(TrainingPolicy.Enforce)).Executable);
    }

    [Fact]
    public void A_scheduled_employee_is_never_counted_as_present()
    {
        var f = Staffed();
        var dana = f.AddWorker("Dana", "SLIT-WC", SlitII);
        var lee = f.AddWorker("Lee", "SLIT-WC", SlitII);
        var snapshot = f.Build();
        snapshot = snapshot with
        {
            People = snapshot.People.Select(p => p.PersonId == dana ? p with { Presence = null, PresentUntil = null, Assignment = null }
                : p.PersonId == lee ? p with { ActiveOperation = "10001/10", Assignment = OnSlitter("SLITTER-A") } : p).ToList(),
        };

        var presence = ExecutableChecks.Presence(snapshot);
        var d = presence.Single(p => p.Name == "Dana");
        Assert.Equal((PresenceLevel.Scheduled, false, true), (d.Level, d.CountsAsPresent, d.OnShift));
        Assert.Equal(PresenceLevel.ActiveOnOperation, presence.Single(p => p.Name == "Lee").Level);
        Assert.Equal(PresenceLevel.AssignedToWorkCenter, presence.Single(p => p.Name == "Sarah").Level);
        Assert.Equal("SupervisorConfirmed", presence.Single(p => p.Name == "Sarah").Source);

        // Dana is qualified and scheduled, but readiness never counts her.
        var op = Op(snapshot);
        Assert.DoesNotContain("Dana", op.Dimensions.Single(x => x.Name == "Workforce").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Current_work_readiness_checks_tooling_and_no_longer_lists_it_as_not_evaluated()
    {
        var f = Slitting().SlittingAreaRule();
        f.Add("Sarah", OnSlitter("SLITTER-A"), true, null, SlitII);
        f.Add("Michael", InSlitting(StageRoll), true, null, FirstAid);
        var run = EngineRunner.Run(new OperationReadinessEngine(), WithCaliper(f.Build(), new DateOnly(2026, 9, 1)), SlitterWork("SLITTER-A"), Now);
        Assert.False(run.Output.Executable);
        Assert.Equal("Tooling", run.Output.PrimaryConstraint);
        Assert.DoesNotContain("Tooling", run.Output.NotEvaluated);
    }
}
