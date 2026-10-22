using Forge.Kernel.Engines;
using Forge.Planning.Engines;
using static Forge.Planning.Tests.TestFactory;

namespace Forge.Planning.Tests;

public class ReallocationTests
{
    private readonly ReallocationImpactEngine engine = new();
    private readonly ReallocationPlanEngine planEngine = new();

    // Spec §79: Sarah on Slitting support work, Michael running Slitter B, area rule satisfied.
    private static (TestFactory Factory, Guid Sarah, Guid Michael, Guid Jennifer, Guid Dana) Spec79()
    {
        var f = Slitting().SlittingAreaRule();
        var sarah = f.Add("Sarah", InSlitting(StageRoll), true, null, SlitII, FirstAid);
        var michael = f.Add("Michael", OnSlitter("SLITTER-B"), true, null, SlitII);
        var jennifer = f.Add("Jennifer", InPaint(), true, null, PaintQual, FirstAid);
        var dana = f.Add("Dana", InAssembly(), true, null, AssemblyQual);
        return (f, sarah, michael, jennifer, dana);
    }

    [Fact]
    public void Spec_79_moving_sarah_shows_the_stopped_slitter_before_commit()
    {
        var (f, sarah, _, _, _) = Spec79();

        var preview = engine.Evaluate(f.Build(), new ReallocationRequest(sarah, InAssembly()), Now).Output;

        var stopped = Assert.Single(preview.Impacts, i => i.Kind == ImpactKind.WouldStop);
        Assert.Equal(SlitterWork("SLITTER-B"), stopped.Work);
        Assert.Equal("Safety", stopped.Constraint);
        Assert.Contains(preview.Impacts, i => i.Kind == ImpactKind.NewWork && i.Work.ActivityCode == Assemble);
    }

    [Fact]
    public void Spec_79_options_cover_replacement_one_person_work_moving_michael_and_the_schedule()
    {
        var (f, sarah, _, _, _) = Spec79();

        var options = engine.Evaluate(f.Build(), new ReallocationRequest(sarah, InAssembly()), Now).Output.Options;

        // A. Jennifer holds FIRST-AID, but moving her would stop the paint booth: offered with that cost.
        var jennifer = Assert.Single(options, o => o.Kind == OptionKind.ReplacePerson && o.Description.StartsWith("Move Jennifer", StringComparison.Ordinal));
        Assert.Contains(jennifer.Consequences, c => c.Contains("PAINT on PAINT-BOOTH", StringComparison.Ordinal));

        // Dana is present but holds nothing that satisfies the rule: not offered.
        Assert.DoesNotContain(options, o => o.Description.Contains("Dana", StringComparison.Ordinal));

        // C. Michael can switch to one-person Slitting work (no rule restricts kitting or staging).
        Assert.Contains(options, o => o.Kind == OptionKind.SwitchToOtherActivity && o.Description.Contains($"Michael to {Kitting}", StringComparison.Ordinal));
        Assert.Contains(options, o => o.Kind == OptionKind.SwitchToOtherActivity && o.Description.Contains($"Michael to {StageRoll}", StringComparison.Ordinal));

        // B, D, E.
        Assert.Contains(options, o => o.Kind == OptionKind.MoveOperatorElsewhere);
        Assert.Contains(options, o => o.Kind == OptionKind.AdjustSchedule);
        Assert.Equal(OptionKind.ReviewManually, options[^1].Kind);
    }

    [Fact]
    public void A_replacement_that_breaks_nothing_is_listed_first()
    {
        var (f, sarah, _, _, _) = Spec79();
        f.Add("Priya", InAssembly(), true, null, FirstAid);

        var replacements = engine.Evaluate(f.Build(), new ReallocationRequest(sarah, InAssembly()), Now).Output.Options
            .Where(o => o.Kind == OptionKind.ReplacePerson)
            .ToList();

        Assert.StartsWith("Move Priya", replacements[0].Description, StringComparison.Ordinal);
        Assert.Contains("Stops no other active work.", replacements[0].Consequences);
    }

    [Fact]
    public void A_move_that_breaks_nothing_offers_no_options()
    {
        var (f, sarah, _, _, _) = Spec79();
        f.Add("Priya", InSlitting(Kitting), true, null, FirstAid);

        var preview = engine.Evaluate(f.Build(), new ReallocationRequest(sarah, InAssembly()), Now).Output;

        Assert.DoesNotContain(preview.Impacts, i => i.Kind == ImpactKind.WouldStop);
        Assert.Empty(preview.Options);
    }

    [Fact]
    public void The_preview_never_changes_the_snapshot()
    {
        var (f, sarah, _, _, _) = Spec79();
        var snapshot = f.Build();
        var before = snapshot.Person(sarah)!.Assignment;

        engine.Evaluate(snapshot, new ReallocationRequest(sarah, InAssembly()), Now);

        Assert.Equal(before, snapshot.Person(sarah)!.Assignment);
    }

    [Fact]
    public void Same_state_gives_the_same_answer_regardless_of_input_order()
    {
        var (f, sarah, _, _, _) = Spec79();
        var snapshot = f.Build();
        var shuffled = snapshot with
        {
            People = [.. snapshot.People.Reverse()],
            Machines = [.. snapshot.Machines.Reverse()],
            Activities = [.. snapshot.Activities.Reverse()],
        };

        var a = EngineRunner.Run(engine, snapshot, new ReallocationRequest(sarah, InAssembly()), Now);
        var b = EngineRunner.Run(engine, snapshot, new ReallocationRequest(sarah, InAssembly()), Now);
        var c = EngineRunner.Run(engine, shuffled, new ReallocationRequest(sarah, InAssembly()), Now);

        Assert.Equal(a.InputFingerprint, b.InputFingerprint);
        Assert.Equal(a.OutputFingerprint, b.OutputFingerprint);
        Assert.Equal(a.OutputFingerprint, c.OutputFingerprint);
    }

    [Fact]
    public void A_cascading_plan_rechecks_the_next_move_against_the_previous_move_and_does_not_change_source()
    {
        var (f, sarah, michael, _, _) = Spec79();
        var snapshot = f.Build();

        var preview = planEngine.Evaluate(snapshot,
        [
            new ReallocationPlanMove(sarah, InAssembly()),
            new ReallocationPlanMove(michael, null),
        ], Now).Output;

        Assert.Equal(2, preview.Steps.Count);
        Assert.Contains(preview.Steps[0].Preview.Impacts, impact => impact.Kind == ImpactKind.WouldStop && impact.Work == SlitterWork("SLITTER-B"));
        var secondMove = preview.Steps[1].Preview;
        Assert.Equal(OnSlitter("SLITTER-B"), secondMove.Current);
        Assert.Contains(secondMove.Before, result => result.Work == SlitterWork("SLITTER-B") && !result.Executable);
        Assert.DoesNotContain(preview.FinalActiveWork, result => result.Work == SlitterWork("SLITTER-B"));
        Assert.Equal(InSlitting(StageRoll), snapshot.Person(sarah)!.Assignment);
        Assert.Equal(OnSlitter("SLITTER-B"), snapshot.Person(michael)!.Assignment);
    }

    [Fact]
    public void A_cascading_plan_rejects_duplicate_people()
    {
        var (f, sarah, _, _, _) = Spec79();
        var move = new ReallocationPlanMove(sarah, InAssembly());

        Assert.Throws<ArgumentException>(() => planEngine.Evaluate(f.Build(), [move, move], Now));
    }
}
