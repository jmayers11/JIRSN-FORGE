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
        Assert.Equal(["Material", "Engineering", "Quality"], run.Output.NotEvaluated);
        Assert.Contains(run.Output.Dimensions, d => d.Name == "Tooling" && d.Outcome == EvidenceOutcome.Pass);
        Assert.Contains(run.Output.Dimensions, d => d.Name == "Equipment" && d.Outcome == EvidenceOutcome.Pass);
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

    [Fact]
    public void Machine_specific_practical_authorization_is_required_in_addition_to_the_qualification()
    {
        var f = Slitting().SlittingAreaRule();
        var operatorId = f.Add("Sarah", OnSlitter("SLITTER-A"), true, null, SlitII);
        f.Add("Michael", InSlitting(StageRoll), true, null, FirstAid);
        var snapshot = f.Build() with
        {
            MachineActivities = f.Build().MachineActivities.Select(profile => profile with { RequiresMachineAuthorization = true }).ToList(),
        };

        var blocked = engine.Evaluate(snapshot, SlitterWork("SLITTER-A"), Now).Output;
        Assert.False(blocked.Executable);
        Assert.Contains("lacks current authorization", blocked.Dimensions.Single(dimension => dimension.Name == "Workforce").Detail, StringComparison.Ordinal);

        var authorizedPerson = snapshot.Person(operatorId)! with
        {
            MachineAuthorizationExpiry = new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal)
            {
                ["SLITTER-A|RUN_SLITTER|SLIT-II"] = Now.AddHours(1),
            },
        };
        var authorized = snapshot with { People = snapshot.People.Select(person => person.PersonId == operatorId ? authorizedPerson : person).ToList() };
        Assert.True(engine.Evaluate(authorized, SlitterWork("SLITTER-A"), Now).Output.Executable);

        var capacityRequirement = new WorkRequirement("SLIT-WC", "SLITTER-A", RunSlitter, [SlitII], [], 1, RequiresMachineAuthorization: true);
        Assert.False(capacityRequirement.Qualifies(snapshot.Person(operatorId)!, Now));
        Assert.True(capacityRequirement.Qualifies(authorizedPerson, Now));
        Assert.False((capacityRequirement with { MachineCode = "SLITTER-B" }).Qualifies(authorizedPerson, Now), "Authorization on one machine does not authorize its sibling.");
        Assert.False(capacityRequirement.Qualifies(authorizedPerson, Now.AddHours(2)), "Authorization expiry blocks future projected capacity.");
    }
}
