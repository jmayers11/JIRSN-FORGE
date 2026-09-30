using Forge.Kernel.Engines;
using Forge.Planning.Engines;
using Forge.Workforce.Contracts;
using static Forge.Planning.Tests.TestFactory;

namespace Forge.Planning.Tests;

public class ReadinessTests
{
    private readonly OperationReadinessEngine engine = new();

    [Fact]
    public void Staffed_slitter_is_executable_and_unmodeled_dimensions_are_listed_not_passed()
    {
        var f = Slitting().SlittingAreaRule();
        f.Add("Sarah", OnSlitter("SLITTER-A"), true, null, SlitII);
        f.Add("Michael", InSlitting(StageRoll), true, null, FirstAid);

        var run = EngineRunner.Run(engine, f.Build(), SlitterWork("SLITTER-A"), Now);

        Assert.True(run.Output.Executable);
        Assert.Null(run.Output.PrimaryConstraint);
        Assert.Equal(["Equipment", "Material", "Engineering", "Tooling", "Quality"], run.Output.NotEvaluated);
        Assert.DoesNotContain(run.Output.Dimensions, d => run.Output.NotEvaluated.Contains(d.Name));
    }

    [Fact]
    public void Spec_39_machine_with_one_person_fails_on_safety_staffing()
    {
        var f = Slitting().SlittingAreaRule();
        f.Add("Sarah", OnSlitter("SLITTER-A"), true, null, SlitII, FirstAid);

        var result = engine.Evaluate(f.Build(), SlitterWork("SLITTER-A"), Now).Output;

        Assert.False(result.Executable);
        Assert.Equal("Safety", result.PrimaryConstraint);
        Assert.Contains(result.Dimensions, d => d.Name == "Workforce" && d.Outcome == EvidenceOutcome.Pass);
    }

    [Fact]
    public void Trainee_alone_is_not_a_qualified_operator()
    {
        var f = Slitting().SlittingAreaRule(traineeMayBeResponder: true);
        f.Add("Alex", OnSlitter("SLITTER-A", trainee: true), true, null, SlitII, FirstAid);
        f.Add("Michael", InSlitting(StageRoll), true, null, SlitII);

        var result = engine.Evaluate(f.Build(), SlitterWork("SLITTER-A"), Now).Output;

        Assert.False(result.Executable);
        Assert.Equal("Workforce", result.PrimaryConstraint);
        var workforce = result.Dimensions.Single(d => d.Name == "Workforce");
        Assert.Contains("Alex is assigned as a trainee", workforce.Detail, StringComparison.Ordinal);
        Assert.Contains("Michael", workforce.Detail, StringComparison.Ordinal); // qualified and present: a candidate
    }

    [Fact]
    public void Spec_78_absence_stops_the_slitter_and_says_why()
    {
        var f = Slitting().SlittingAreaRule();
        f.Add("Michael", OnSlitter("SLITTER-B"), true, null, SlitII);
        f.Add("Sarah", InSlitting(StageRoll), true, UnavailabilityKind.Unavailable, FirstAid);

        var result = engine.Evaluate(f.Build(), SlitterWork("SLITTER-B"), Now).Output;

        Assert.False(result.Executable);
        Assert.Equal("Safety", result.PrimaryConstraint);
        Assert.Contains(result.Safety.Rules[0].Shortfalls, s => s.Contains("1 present", StringComparison.Ordinal));
    }
}
