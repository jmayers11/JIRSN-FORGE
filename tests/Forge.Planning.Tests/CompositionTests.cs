using Forge.Kernel.Engines;
using Forge.Planning.Engines;
using Forge.SafetyRules.Contracts;
using Forge.Workforce.Contracts;
using static Forge.Planning.Tests.TestFactory;

namespace Forge.Planning.Tests;

public class CompositionTests
{
    private readonly PersonnelCompositionEngine engine = new();

    private CompositionResult Evaluate(TestFactory factory, WorkItem work) => engine.Evaluate(factory.Build(), work, Now).Output;

    [Fact]
    public void Two_people_on_different_machines_in_the_same_area_satisfy_area_coverage()
    {
        // Spec §31: Sarah on Machine A and Michael on Machine B together satisfy the Slitting area rule.
        var f = Slitting().SlittingAreaRule();
        f.Add("Sarah", OnSlitter("SLITTER-A"), true, null, SlitII);
        f.Add("Michael", OnSlitter("SLITTER-B"), true, null, SlitII);

        var result = Evaluate(f, SlitterWork("SLITTER-A"));

        Assert.Equal(CompositionStatus.Satisfied, result.Status);
        var fills = Assert.Single(result.Rules).Fills;
        Assert.Equal(2, fills.Select(x => x.PersonId).Distinct().Count());
    }

    [Fact]
    public void One_person_with_every_qualification_never_counts_as_two()
    {
        var f = Slitting().SlittingAreaRule();
        f.Add("Sarah", OnSlitter("SLITTER-A"), true, null, SlitII, FirstAid);

        var rule = Assert.Single(Evaluate(f, SlitterWork("SLITTER-A")).Rules);

        Assert.False(rule.Satisfied);
        Assert.Single(rule.Fills);
        Assert.Contains(rule.Shortfalls, s => s.Contains("2 physical people", StringComparison.Ordinal));
    }

    [Fact]
    public void Matching_finds_the_assignment_a_greedy_pass_would_miss()
    {
        // Sarah holds both; Michael holds only SLIT-II. Greedy "first eligible" would put Sarah on
        // Operator and leave the responder slot empty. Matching must give Operator to Michael.
        var f = Slitting().Rule(
            "STRICT",
            RuleScopeKind.SafetyArea,
            "SLITTING",
            [RunSlitter],
            2,
            new RoleSlot("Operator", 1, [SlitII], false),
            new RoleSlot("Responder", 1, [FirstAid], false));
        f.Add("Sarah", OnSlitter("SLITTER-A"), true, null, SlitII, FirstAid);
        f.Add("Michael", OnSlitter("SLITTER-B"), true, null, SlitII);

        var rule = Assert.Single(Evaluate(f, SlitterWork("SLITTER-A")).Rules);

        Assert.True(rule.Satisfied);
        Assert.Equal("Michael", rule.Fills.Single(x => x.Slot == "Operator").PersonName);
        Assert.Equal("Sarah", rule.Fills.Single(x => x.Slot == "Responder").PersonName);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void A_trainee_fills_a_safety_slot_only_when_the_rule_allows_it(bool ruleAllows, bool expected)
    {
        var f = Slitting().SlittingAreaRule(traineeMayBeResponder: ruleAllows);
        f.Add("Michael", OnSlitter("SLITTER-A"), true, null, SlitII);
        f.Add("Alex", OnSlitter("SLITTER-A", trainee: true), true, null, FirstAid);

        var result = Evaluate(f, SlitterWork("SLITTER-A"));

        Assert.Equal(expected, result.Status == CompositionStatus.Satisfied);
        if (!expected)
        {
            Assert.Contains(result.Rules[0].Shortfalls, s => s.Contains("Alex", StringComparison.Ordinal) && s.Contains("trainee", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Scheduled_but_not_present_and_unavailable_people_do_not_count()
    {
        var f = Slitting().SlittingAreaRule();
        f.Add("Michael", OnSlitter("SLITTER-B"), true, null, SlitII);
        f.Add("Sarah", InSlitting(StageRoll), false, null, SlitII, FirstAid); // assigned, no presence evidence
        f.Add("Jennifer", InSlitting(StageRoll), true, UnavailabilityKind.CallOut, FirstAid);

        var rule = Assert.Single(Evaluate(f, SlitterWork("SLITTER-B")).Rules);

        Assert.False(rule.Satisfied);
        Assert.Equal(1, rule.PresentPeople);
    }

    [Fact]
    public void Activities_without_a_rule_are_not_blocked()
    {
        // Spec §33: kitting is a permitted one-person activity even when the slitters cannot run.
        var f = Slitting().SlittingAreaRule();
        f.Add("Michael", InSlitting(Kitting), true, null, SlitII);

        var result = engine.Evaluate(f.Build(), new WorkItem("SLITTING", "SLIT-WC", null, Kitting), Now);

        Assert.Equal(CompositionStatus.NoRuleApplies, result.Output.Status);
        Assert.Equal(EvidenceOutcome.Pass, result.Evidence.Outcome);
    }

    [Fact]
    public void Every_applicable_rule_must_pass()
    {
        var f = Slitting().SlittingAreaRule().Rule(
            "SLITTER-B-SPECIAL",
            RuleScopeKind.Machine,
            "SLITTER-B",
            [],
            1,
            new RoleSlot("Lead", 1, [FirstAid], false));
        f.Add("Sarah", OnSlitter("SLITTER-A"), true, null, SlitII);
        f.Add("Michael", OnSlitter("SLITTER-B"), true, null, SlitII);

        var onA = Evaluate(f, SlitterWork("SLITTER-A"));
        var onB = Evaluate(f, SlitterWork("SLITTER-B"));

        Assert.Equal(CompositionStatus.Satisfied, onA.Status); // machine rule does not apply to A
        Assert.Equal(CompositionStatus.NotSatisfied, onB.Status);
        Assert.Equal(2, onB.Rules.Count);
    }

    [Fact]
    public void Rules_not_yet_in_force_or_retired_are_ignored()
    {
        var f = Slitting();
        f.Add("Sarah", OnSlitter("SLITTER-A"), true, null, SlitII);
        var snapshot = f.Build() with
        {
            Rules =
            [
                new SafetyRuleDefinition("FUTURE", 1, "Future", RuleScopeKind.SafetyArea, "SLITTING", [], 5, false, [], RuleStatus.Published, "a", "b", Now.AddDays(1), null),
                new SafetyRuleDefinition("OLD", 1, "Old", RuleScopeKind.SafetyArea, "SLITTING", [], 5, false, [], RuleStatus.Retired, "a", "b", Now.AddDays(-9), Now.AddDays(-1)),
            ],
        };

        Assert.Equal(CompositionStatus.NoRuleApplies, engine.Evaluate(snapshot, SlitterWork("SLITTER-A"), Now).Output.Status);
    }
}
