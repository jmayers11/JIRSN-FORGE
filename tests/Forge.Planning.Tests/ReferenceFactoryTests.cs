using Forge.Planning.Engines;
using Forge.SafetyRules.Contracts;
using static Forge.Planning.Tests.TestFactory;

namespace Forge.Planning.Tests;

/// <summary>Architecture section 30: Factory A raises nothing, Factory C (strange but valid) raises nothing.</summary>
public class ReferenceFactoryTests
{
    private readonly ReallocationImpactEngine engine = new();

    [Fact]
    public void Factory_A_healthy_everything_active_is_executable()
    {
        var f = Slitting().SlittingAreaRule();
        f.Add("Sarah", OnSlitter("SLITTER-A"), true, null, SlitII, FirstAid);
        f.Add("Michael", OnSlitter("SLITTER-B"), true, null, SlitII);
        f.Add("Priya", InSlitting(StageRoll), true, null, FirstAid);
        f.Add("Jennifer", InPaint(), true, null, PaintQual);
        f.Add("Dana", InAssembly(), true, null, AssemblyQual);

        var results = engine.EvaluateAll(f.Build(), Now);

        Assert.Equal(5, results.Count);
        Assert.All(results, r => Assert.True(r.Executable, r.Work.Describe() + ": " + r.PrimaryConstraint));
    }

    [Fact]
    public void Factory_C_unusual_but_valid_configurations_raise_nothing()
    {
        var f = Slitting()
            .SlittingAreaRule()
            // A one-person rule on the paint booth, satisfied by a person holding several qualifications.
            .Rule("PAINT-SOLO", RuleScopeKind.Machine, "PAINT-BOOTH", [], 1, new RoleSlot("Painter", 1, [PaintQual], false));

        // Two qualified operators, no dedicated responder: allowed because the responder slot accepts SLIT-II.
        f.Add("Sarah", OnSlitter("SLITTER-A"), true, null, SlitII);
        f.Add("Michael", OnSlitter("SLITTER-A"), true, null, SlitII);

        // One person kitting alone in Assembly with no rule, and a multi-skilled painter working alone.
        f.Add("Night kitter", new("ASSEMBLY", "ASSY-WC", null, Kitting, false), true, null);
        f.Add("Jennifer", InPaint(), true, null, PaintQual, SlitII, FirstAid, AssemblyQual);

        // Someone registered but not present at all, and an assigned trainee in Assembly.
        f.Add("Off shift", OnSlitter("SLITTER-B"), false, null, SlitII);
        f.Add("Alex", new("ASSEMBLY", "ASSY-WC", null, Assemble, true), true, null);

        var results = engine.EvaluateAll(f.Build(), Now);

        Assert.DoesNotContain(results, r => r.Work.MachineCode == "SLITTER-B"); // absent people create no work
        Assert.All(results, r => Assert.True(r.Executable, r.Work.Describe() + ": " + r.PrimaryConstraint));
    }
}
