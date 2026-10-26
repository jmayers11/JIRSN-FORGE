using Forge.Planning.Engines;
using Forge.Workforce.Contracts;
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
    public void Movers_can_be_limited_to_people_holding_the_operations_own_qualifications()
    {
        var factory = Slitting();
        var zara = factory.Add("Zara", null, true, null, SlitII);
        var alina = factory.Add("Alina", null, true, null, SlitII, FirstAid);
        var result = new WorkforceRecoveryEngine().Evaluate(factory.Build(), new WorkforceRecoveryRequest(SlitterWork("SLITTER-A"),
            new HashSet<Guid> { zara, alina }, MoverPersonIds: new HashSet<Guid> { alina }), Now).Output;

        Assert.Equal("Alina", Assert.Single(result.Candidates).PersonName);
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
    public void Projected_shift_recovery_uses_scheduled_people_and_discloses_future_source_load_limits()
    {
        var factory = Slitting();
        var scheduledWorker = factory.Add("Scheduled Operator", null, false, null, SlitII);
        var snapshot = factory.Build();
        var projected = snapshot with
        {
            AsOf = Now.AddDays(1),
            People = snapshot.People.Select(person => person.PersonId == scheduledWorker
                ? person with
                {
                    Presence = PresenceKind.Projected,
                }
                : person with { Presence = null, Assignment = null }).ToArray(),
        };

        var result = new WorkforceRecoveryEngine().Evaluate(projected,
            new WorkforceRecoveryRequest(SlitterWork("SLITTER-A"), new HashSet<Guid> { scheduledWorker }, ProjectedShift: true),
            projected.AsOf).Output;

        var candidate = Assert.Single(result.Candidates);
        Assert.True(result.ProjectedShift);
        Assert.Equal(scheduledWorker, candidate.PersonId);
        Assert.Contains("scheduled at the selected shift start", string.Join(" ", candidate.Evidence), StringComparison.Ordinal);
        Assert.Contains("Future source operation assignments and dispatch load", candidate.NotEvaluated);
        Assert.Contains("not proof of clock-in", string.Join(" ", result.Limitations), StringComparison.Ordinal);
    }

    [Fact]
    public void Projected_shift_preview_reports_open_source_work_impact_and_an_ordered_backfill_option()
    {
        var factory = Slitting().SlittingAreaRule();
        var incumbent = factory.Add("Mika", OnSlitter("SLITTER-A"), true, null, SlitII);
        var operatorToMove = factory.Add("Anna", InPaint(), true, null, SlitII, FirstAid, PaintQual);
        var sourceBackfill = factory.Add("Ben", null, true, null, PaintQual);
        var snapshot = factory.Build();
        var targetAt = Now.AddDays(1);
        var projected = snapshot with
        {
            AsOf = targetAt,
            People = snapshot.People.Select(person => person.PersonId == incumbent || person.PersonId == operatorToMove || person.PersonId == sourceBackfill
                ? person with { Presence = PresenceKind.Projected, ActiveOperation = null }
                : person with { Presence = null, Assignment = null, ActiveOperation = null }).ToArray(),
        };

        var result = new WorkforceRecoveryEngine().Evaluate(projected,
            new WorkforceRecoveryRequest(SlitterWork("SLITTER-A"), new HashSet<Guid> { incumbent, operatorToMove, sourceBackfill }, ProjectedShift: true), targetAt).Output;

        var recommendation = Assert.Single(result.Candidates, candidate => candidate.PersonId == operatorToMove);
        Assert.True(recommendation.RequiresSourceBackfill);
        Assert.Equal(new[] { sourceBackfill, operatorToMove }, recommendation.Moves.Select(move => move.PersonId));
        Assert.Contains(recommendation.SourceImpacts, impact => impact.Work.MachineCode == "PAINT-BOOTH");
        Assert.Contains("Future source operation assignments and dispatch load", recommendation.NotEvaluated);
        Assert.Contains("Future dispatch, source workload", string.Join(" ", result.Limitations), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Projected_shift_recovery_rejects_qualifications_that_expire_before_shift_start()
    {
        var factory = Slitting();
        var scheduledWorker = factory.Add("Expiring Operator", null, false, null, SlitII);
        var snapshot = factory.Build();
        var targetAt = Now.AddDays(1);
        var projected = snapshot with
        {
            AsOf = targetAt,
            People = snapshot.People.Select(person => person.PersonId == scheduledWorker
                ? person with
                {
                    Presence = PresenceKind.Projected,
                    Assignment = new WorkAssignment("SLITTING", "SLIT-WC", null, "PROJECTED", false),
                    QualificationExpiry = new Dictionary<string, DateTimeOffset?> { [SlitII] = Now.AddHours(1) },
                }
                : person with { Presence = null, Assignment = null }).ToArray(),
        };

        var result = new WorkforceRecoveryEngine().Evaluate(projected,
            new WorkforceRecoveryRequest(SlitterWork("SLITTER-A"), new HashSet<Guid> { scheduledWorker }, ProjectedShift: true), targetAt).Output;

        Assert.Empty(result.Candidates);
        Assert.Equal(1, result.EvaluatedCandidates);
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
