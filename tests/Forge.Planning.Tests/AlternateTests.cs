using Forge.Engineering.Contracts;
using Forge.Planning.Engines;
using static Forge.Planning.Tests.TestFactory;

namespace Forge.Planning.Tests;

/// <summary>V2.6: alternates are decided from data; MRP substitutes only approved equivalents on lines that allow it, and says why.</summary>
public sealed class AlternateTests
{
    private static readonly DateOnly Today = new(2026, 10, 1);
    private static readonly Guid ProgramA = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid ProgramB = new("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset At = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    private static AlternateInfo Rel(long id, string primary, string alternate, AlternateKind kind = AlternateKind.Equivalent, AlternateStatus status = AlternateStatus.Approved,
        decimal factor = 1, Guid? program = null, string? parent = null, DateOnly? to = null) =>
        new(id, primary, alternate, kind, status, factor, program, null, parent, null, null, null, null, null, to, null, null, null, "person:p", At,
            status == AlternateStatus.Proposed ? null : "person:a", status == AlternateStatus.Proposed ? null : At, null, null, null, null);

    [Fact]
    public void Proposed_alternates_are_never_usable_and_are_listed_apart()
    {
        var candidates = Alternates.Candidates([Rel(1, "BRG", "BRG-ALT", status: AlternateStatus.Proposed), Rel(2, "BRG", "BRG-EQ")], "BRG", new AlternateContext(Today));
        Assert.Equal("BRG-EQ", Assert.Single(candidates.Approved).OfferedPart);
        var proposed = Assert.Single(candidates.RequiresApproval);
        Assert.Equal("BRG-ALT", proposed.OfferedPart);
        Assert.Contains("Proposed", proposed.Reasons[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_program_condition_is_usable_for_that_program_only_and_unknown_is_never_usable()
    {
        var r = Rel(1, "CHIP", "CHIP2", AlternateKind.Conditional, program: ProgramA);
        Assert.Equal(AlternateUsability.Usable, Alternates.Evaluate(r, new AlternateContext(Today, ProgramId: ProgramA)).Usability);
        Assert.Equal(AlternateUsability.RequiresApproval, Alternates.Evaluate(r, new AlternateContext(Today, ProgramId: ProgramB)).Usability);
        Assert.Equal(AlternateUsability.RequiresApproval, Alternates.Evaluate(r, new AlternateContext(Today)).Usability);
    }

    [Fact]
    public void Expired_and_ended_alternates_stop_and_maintenance_only_stays_out_of_production()
    {
        Assert.Equal(AlternateUsability.NotUsable, Alternates.Evaluate(Rel(1, "A", "B", status: AlternateStatus.Expired), new AlternateContext(Today)).Usability);
        Assert.Equal(AlternateUsability.NotUsable, Alternates.Evaluate(Rel(2, "A", "B", AlternateKind.Temporary, to: Today.AddDays(-1)), new AlternateContext(Today)).Usability);
        Assert.Equal(AlternateUsability.Usable, Alternates.Evaluate(Rel(3, "A", "B", AlternateKind.Temporary, to: Today), new AlternateContext(Today)).Usability);
        var maintenance = Rel(4, "BRG", "BRG2", AlternateKind.MaintenanceOnly);
        Assert.Equal(AlternateUsability.NotUsable, Alternates.Evaluate(maintenance, new AlternateContext(Today)).Usability);
        Assert.Equal(AlternateUsability.Usable, Alternates.Evaluate(maintenance, new AlternateContext(Today, Maintenance: true)).Usability);
    }

    [Fact]
    public void Interchangeable_works_both_ways_at_the_inverse_conversion()
    {
        var r = Rel(1, "BOLT-M8", "BOLT-PACK", AlternateKind.Interchangeable, factor: 0.25m);
        Assert.Equal(0.25m, Assert.Single(Alternates.For([r], "BOLT-M8")).Factor);
        var back = Assert.Single(Alternates.For([r], "BOLT-PACK"));
        Assert.Equal(("BOLT-M8", 4m), (back.OfferedPart, back.Factor));
    }

    private static MrpSnapshot Plant(SubstitutionPolicy policy, AlternateInfo[] alternates, decimal alternateStock = 20)
    {
        MrpItem Buy(string part) => new(part, part, "EA", Engineering.Contracts.MakeBuy.Buy, 10, 0, LotPolicy.LotForLot, 0, 0);
        var line = new MrpLineContext("ROLL", 1, policy);
        return new MrpSnapshot(
            Today,
            [Buy("COIL"), Buy("COIL-EQ")],
            [],
            [new MrpStock("COIL", 8, 0, 0), new MrpStock("COIL-EQ", alternateStock, 0, 0)],
            [new MrpRequirement("WO1/1", "COIL", 20, Today.AddDays(5), 3, "OrderMaterial", "Order 1 line 1", line)],
            [],
            alternates);
    }

    private static MrpResult Run(MrpSnapshot s) => new MrpEngine().Evaluate(s, new MrpRequest(), Now).Output;

    [Fact]
    public void Scenario_2_automatic_line_substitutes_an_approved_equivalent_up_to_the_shortage_and_records_why()
    {
        var result = Run(Plant(SubstitutionPolicy.Automatic, [Rel(7, "COIL", "COIL-EQ")]));
        var sub = Assert.Single(result.Substitutions!);
        Assert.Equal(("COIL", "COIL-EQ", 7L, 12m, 20m, 12m, "Automatic"), (sub.PrimaryPart, sub.AlternatePart, sub.RelationshipId, sub.Shortage, sub.AlternateAvailable, sub.AlternateQuantity, sub.Policy));
        Assert.Empty(result.PlannedOrders);
        Assert.Contains("allows automatic substitution", sub.Explanation, StringComparison.Ordinal);
        var alternate = result.Items.Single(i => i.PartNumber == "COIL-EQ");
        Assert.Contains(alternate.Requirements, r => r.Kind == "Substitution" && r.Quantity == 12);
        Assert.Equal(12m, result.Items.Single(i => i.PartNumber == "COIL").Buckets.Sum(b => b.Substituted));
    }

    [Fact]
    public void Only_what_is_free_is_substituted_and_the_rest_is_planned()
    {
        var result = Run(Plant(SubstitutionPolicy.Automatic, [Rel(7, "COIL", "COIL-EQ", factor: 2)], alternateStock: 10));
        var sub = Assert.Single(result.Substitutions!);
        Assert.Equal((5m, 10m), (sub.PrimaryQuantity, sub.AlternateQuantity));
        var planned = Assert.Single(result.PlannedOrders);
        Assert.Equal(("COIL", 7m), (planned.PartNumber, planned.Quantity));
        Assert.Contains("covered 5 of the shortage", planned.Explanation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SubstitutionPolicy.Suggest, AlternateStatus.Approved, AlternateKind.Equivalent, true)]
    [InlineData(SubstitutionPolicy.Automatic, AlternateStatus.Approved, AlternateKind.Conditional, true)]
    [InlineData(SubstitutionPolicy.Never, AlternateStatus.Approved, AlternateKind.Equivalent, false)]
    [InlineData(SubstitutionPolicy.Automatic, AlternateStatus.Proposed, AlternateKind.Equivalent, false)]
    [InlineData(SubstitutionPolicy.Automatic, AlternateStatus.Expired, AlternateKind.Equivalent, false)]
    public void Otherwise_nothing_is_substituted(SubstitutionPolicy policy, AlternateStatus status, AlternateKind kind, bool suggested)
    {
        var result = Run(Plant(policy, [Rel(7, "COIL", "COIL-EQ", kind, status)]));
        Assert.Empty(result.Substitutions!);
        Assert.Equal(12m, Assert.Single(result.PlannedOrders).Quantity);
        Assert.Equal(suggested, result.Exceptions.Any(e => e.Kind == "AlternateSuggested"));
    }

    [Fact]
    public void A_line_restricted_to_another_assembly_is_not_substituted()
    {
        var result = Run(Plant(SubstitutionPolicy.Automatic, [Rel(7, "COIL", "COIL-EQ", parent: "OTHER")]));
        Assert.Empty(result.Substitutions!);
    }
}
