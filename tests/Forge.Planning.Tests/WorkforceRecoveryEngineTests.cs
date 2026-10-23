using Forge.Planning.Engines;
using static Forge.Planning.Tests.TestFactory;

namespace Forge.Planning.Tests;

public sealed class WorkforceRecoveryEngineTests
{
    [Fact]
    public void Ranking_returns_only_currently_qualified_workers_and_is_stable()
    {
        var factory = Slitting();
        var zara = factory.Add("Zara", null, true, null, SlitII);
        var alina = factory.Add("Alina", null, true, null, SlitII);
        var unqualified = factory.Add("Morgan", null, true, null, FirstAid);
        var target = SlitterWork("SLITTER-A");
        var engine = new WorkforceRecoveryEngine();

        var result = engine.Evaluate(factory.Build(), new WorkforceRecoveryRequest(target,
            new HashSet<Guid> { zara, alina, unqualified }), Now).Output;

        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal(new[] { "Alina", "Zara" }, result.Candidates.Select(candidate => candidate.PersonName));
        Assert.Equal(new[] { 1, 2 }, result.Candidates.Select(candidate => candidate.Rank));
        Assert.All(result.Candidates, candidate => Assert.Single(candidate.Moves));
        Assert.DoesNotContain(result.Candidates, candidate => candidate.PersonId == unqualified);
    }

    [Fact]
    public void Machine_authorization_is_a_hard_gate_for_recovery_candidates()
    {
        var factory = Slitting();
        var worker = factory.Add("Operator", null, true, null, SlitII);
        var snapshot = factory.Build();
        var gated = snapshot with
        {
            MachineActivities = snapshot.MachineActivities.Select(profile => profile.MachineCode == "SLITTER-A"
                ? profile with { RequiresMachineAuthorization = true }
                : profile).ToArray(),
        };

        var result = new WorkforceRecoveryEngine().Evaluate(gated,
            new WorkforceRecoveryRequest(SlitterWork("SLITTER-A"), new HashSet<Guid> { worker }), Now).Output;

        Assert.Empty(result.Candidates);
        Assert.Equal("Workforce", result.CurrentConstraint);
    }

    [Fact]
    public void Ranked_two_step_option_backfills_the_source_work_before_moving_the_operator()
    {
        var factory = Slitting().SlittingAreaRule();
        var incumbent = factory.Add("Mika", OnSlitter("SLITTER-A"), true, null, SlitII);
        var operatorToMove = factory.Add("Anna", InPaint(), true, null, SlitII, FirstAid, PaintQual);
        var sourceBackfill = factory.Add("Ben", null, true, null, PaintQual);
        var snapshot = factory.Build();
        var target = SlitterWork("SLITTER-A");

        var result = new WorkforceRecoveryEngine().Evaluate(snapshot,
            new WorkforceRecoveryRequest(target, new HashSet<Guid> { incumbent, operatorToMove, sourceBackfill }), Now).Output;

        var recommendation = Assert.Single(result.Candidates);
        Assert.Equal(operatorToMove, recommendation.PersonId);
        Assert.True(recommendation.RequiresSourceBackfill);
        Assert.Equal(new[] { sourceBackfill, operatorToMove }, recommendation.Moves.Select(move => move.PersonId));
        Assert.Equal(new[] { "PAINT-BOOTH", "SLITTER-A" }, recommendation.Moves.Select(move => move.Assignment.MachineCode));
        Assert.Contains("first", string.Join(" ", recommendation.Evidence), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(InPaint(), snapshot.Person(operatorToMove)!.Assignment);
        Assert.Equal(OnSlitter("SLITTER-A"), snapshot.Person(incumbent)!.Assignment);
    }
}
