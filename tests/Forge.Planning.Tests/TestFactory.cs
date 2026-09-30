using Forge.Planning.Engines;
using Forge.Resources.Contracts;
using Forge.SafetyRules.Contracts;
using Forge.Workforce.Contracts;

namespace Forge.Planning.Tests;

/// <summary>
/// Builds engine snapshots for the reference factories. The layout is the spec's Slitting example:
/// one Slitting safety area with two slitters, an Assembly area and a Paint area.
/// </summary>
internal sealed class TestFactory
{
    public static readonly DateTimeOffset Now = new(2026, 10, 1, 14, 0, 0, TimeSpan.Zero);

    public const string RunSlitter = "RUN_SLITTER";
    public const string StageRoll = "STAGE_ROLL";
    public const string Kitting = "KITTING";
    public const string Assemble = "ASSEMBLE";
    public const string Paint = "PAINT";
    public const string SlitII = "SLIT-II";
    public const string FirstAid = "FIRST-AID";
    public const string AssemblyQual = "ASSY-I";
    public const string PaintQual = "PAINT-I";

    private readonly List<PersonSnapshot> people = [];
    private readonly List<SafetyRuleDefinition> rules = [];

    public static TestFactory Slitting() => new();

    public Guid Add(string name, WorkAssignment? assignment, bool present = true, UnavailabilityKind? unavailable = null, params string[] qualifications)
    {
        // Deterministic ids ordered by insertion: name order and id order differ on purpose in some tests.
        var id = Guid.Parse($"018f0000-0000-7000-8000-{people.Count + 1:D12}");
        people.Add(new PersonSnapshot(
            id,
            name,
            true,
            present ? PresenceKind.SupervisorConfirmed : null,
            present ? Now.AddHours(6) : null,
            unavailable,
            assignment,
            qualifications.Order(StringComparer.Ordinal).ToList()));
        return id;
    }

    public TestFactory Rule(string code, RuleScopeKind scope, string scopeCode, string[] activities, int minimum, params RoleSlot[] slots)
    {
        rules.Add(new SafetyRuleDefinition(code, 1, code + " staffing", scope, scopeCode, activities, minimum, false, slots, RuleStatus.Published, "person:ehs", "person:approver", Now.AddDays(-30), null));
        return this;
    }

    /// <summary>The spec's rule: two people in the Slitting area to run a slitter, one operator and one safety responder.</summary>
    public TestFactory SlittingAreaRule(bool traineeMayBeResponder = false) => Rule(
        "SLIT-AREA",
        RuleScopeKind.SafetyArea,
        "SLITTING",
        [RunSlitter],
        2,
        new RoleSlot("Operator", 1, [SlitII], false),
        new RoleSlot("Safety responder", 1, [FirstAid, SlitII], traineeMayBeResponder));

    public SiteSnapshot Build() => new(
        Now,
        [new SafetyAreaInfo("ASSEMBLY", "PLANT1", "Assembly"), new SafetyAreaInfo("PAINT", "PLANT1", "Paint"), new SafetyAreaInfo("SLITTING", "PLANT1", "Slitting")],
        [new WorkCenterInfo("ASSY-WC", "ASSEMBLY", "Assembly"), new WorkCenterInfo("PAINT-WC", "PAINT", "Paint"), new WorkCenterInfo("SLIT-WC", "SLITTING", "Slitting")],
        [new MachineInfo("PAINT-BOOTH", "PAINT-WC", "PAINT", "Paint booth"), new MachineInfo("SLITTER-A", "SLIT-WC", "SLITTING", "Slitter A"), new MachineInfo("SLITTER-B", "SLIT-WC", "SLITTING", "Slitter B")],
        [
            new MachineActivityInfo("PAINT-BOOTH", Paint, [PaintQual]),
            new MachineActivityInfo("SLITTER-A", RunSlitter, [SlitII]),
            new MachineActivityInfo("SLITTER-B", RunSlitter, [SlitII]),
        ],
        [
            new WorkCenterActivityInfo("ASSY-WC", Assemble),
            new WorkCenterActivityInfo("ASSY-WC", Kitting),
            new WorkCenterActivityInfo("SLIT-WC", Kitting),
            new WorkCenterActivityInfo("SLIT-WC", StageRoll),
        ],
        [
            new ActivityTypeInfo(Assemble, "Assemble", null),
            new ActivityTypeInfo(Kitting, "Slitted roll kitting", null),
            new ActivityTypeInfo(Paint, "Paint", null),
            new ActivityTypeInfo(RunSlitter, "Run slitter", null),
            new ActivityTypeInfo(StageRoll, "Stage next roll", null),
        ],
        people.OrderBy(p => p.PersonId).ToList(),
        rules.OrderBy(r => r.Code, StringComparer.Ordinal).ToList());

    public static WorkAssignment OnSlitter(string machine, bool trainee = false) => new("SLITTING", "SLIT-WC", machine, RunSlitter, trainee);

    public static WorkAssignment InSlitting(string activity, bool trainee = false) => new("SLITTING", "SLIT-WC", null, activity, trainee);

    public static WorkAssignment InAssembly() => new("ASSEMBLY", "ASSY-WC", null, Assemble, false);

    public static WorkAssignment InPaint() => new("PAINT", "PAINT-WC", "PAINT-BOOTH", Paint, false);

    public static WorkItem SlitterWork(string machine) => new("SLITTING", "SLIT-WC", machine, RunSlitter);
}
