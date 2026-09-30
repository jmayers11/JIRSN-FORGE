using Forge.Kernel.Engines;

namespace Forge.Kernel.Tests;

/// <summary>A small but real engine used to prove the engine contract: two-person coverage in an area.</summary>
public class EngineContractTests
{
    private sealed record AreaSnapshot(string Area, IReadOnlyList<string> PeoplePresent);

    private sealed record CoverageRule(string RuleRef, int MinimumPeople);

    private sealed class CoverageEngine : IEngine<AreaSnapshot, CoverageRule, bool>
    {
        public string Name => "test-coverage";

        public string Version => "1";

        public EngineEvaluation<bool> Evaluate(AreaSnapshot snapshot, CoverageRule parameters, DateTimeOffset asOf)
        {
            var distinct = snapshot.PeoplePresent.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var check = new EvidenceNode(
                "MinimumPhysicalPeople",
                distinct.Count >= parameters.MinimumPeople ? EvidenceOutcome.Pass : EvidenceOutcome.Fail,
                $"{distinct.Count} of {parameters.MinimumPeople} required people present in {snapshot.Area}.")
            {
                RuleRef = parameters.RuleRef,
                Inputs = new(StringComparer.Ordinal) { ["present"] = string.Join(",", distinct) },
            };
            var root = new EvidenceNode("AreaCoverage", EvidenceNode.Combine([check]), snapshot.Area) { Children = [check] };
            return new EngineEvaluation<bool>(root.Outcome == EvidenceOutcome.Pass, root);
        }
    }

    private static readonly DateTimeOffset AsOf = new(2026, 10, 1, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Same_inputs_give_identical_fingerprints_and_output()
    {
        var engine = new CoverageEngine();
        var rule = new CoverageRule("SafetyRule:SLIT-AREA@v1", 2);
        var first = EngineRunner.Run(engine, new AreaSnapshot("Slitting", ["sarah", "michael"]), rule, AsOf);
        var second = EngineRunner.Run(engine, new AreaSnapshot("Slitting", ["sarah", "michael"]), rule, AsOf);

        Assert.Equal(first.InputFingerprint, second.InputFingerprint);
        Assert.Equal(first.OutputFingerprint, second.OutputFingerprint);
        Assert.True(first.Output);
    }

    [Fact]
    public void As_of_offset_does_not_change_the_fingerprint()
    {
        var engine = new CoverageEngine();
        var rule = new CoverageRule("r", 2);
        var snapshot = new AreaSnapshot("Slitting", ["sarah"]);
        var utc = EngineRunner.Run(engine, snapshot, rule, AsOf);
        var eastern = EngineRunner.Run(engine, snapshot, rule, AsOf.ToOffset(TimeSpan.FromHours(-4)));
        Assert.Equal(utc.InputFingerprint, eastern.InputFingerprint);
    }

    [Fact]
    public void A_person_counts_once_and_failure_is_explained()
    {
        var run = EngineRunner.Run(new CoverageEngine(), new AreaSnapshot("Slitting", ["sarah", "sarah"]), new CoverageRule("SafetyRule:SLIT-AREA@v1", 2), AsOf);

        Assert.False(run.Output);
        var check = Assert.Single(run.Evidence.Children);
        Assert.Equal(EvidenceOutcome.Fail, check.Outcome);
        Assert.Equal("SafetyRule:SLIT-AREA@v1", check.RuleRef);
        Assert.Contains("1 of 2", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Changing_an_input_changes_the_input_fingerprint()
    {
        var engine = new CoverageEngine();
        var rule = new CoverageRule("r", 2);
        var a = EngineRunner.Run(engine, new AreaSnapshot("Slitting", ["sarah", "michael"]), rule, AsOf);
        var b = EngineRunner.Run(engine, new AreaSnapshot("Slitting", ["sarah", "jennifer"]), rule, AsOf);
        Assert.NotEqual(a.InputFingerprint, b.InputFingerprint);
    }

    [Theory]
    [InlineData(new[] { EvidenceOutcome.Pass, EvidenceOutcome.Pass }, EvidenceOutcome.Pass)]
    [InlineData(new[] { EvidenceOutcome.Pass, EvidenceOutcome.Indeterminate }, EvidenceOutcome.Indeterminate)]
    [InlineData(new[] { EvidenceOutcome.Indeterminate, EvidenceOutcome.Fail }, EvidenceOutcome.Fail)]
    [InlineData(new[] { EvidenceOutcome.Info, EvidenceOutcome.Pass }, EvidenceOutcome.Pass)]
    public void Indeterminate_is_never_treated_as_pass(EvidenceOutcome[] children, EvidenceOutcome expected)
    {
        var nodes = children.Select((o, i) => new EvidenceNode("c" + i, o, string.Empty));
        Assert.Equal(expected, EvidenceNode.Combine(nodes));
    }
}
