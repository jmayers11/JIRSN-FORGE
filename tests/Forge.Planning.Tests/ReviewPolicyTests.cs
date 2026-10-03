using Forge.Engineering.Contracts;

namespace Forge.Planning.Tests;

public sealed class ReviewPolicyTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Falcon = new("11111111-2222-3333-4444-555555555555");

    private static ChangeInfo Change(ChangeClass cls, ChangeType type, Guid? program = null, string? customer = null) => new(
        "ECN-0001", "t", cls, type, ChangePriority.Routine, "r", null, program, customer, null, ChangeStatus.Draft, 0, "person:a", At,
        null, null, null, null, null, null, null, null, null, [], []);

    private static ReviewPolicyInfo Policy(long id, ReviewKind[] kinds, ChangeClass? cls = null, ChangeType? type = null, ItemClass? item = null, Guid? program = null, string? customer = null, bool combine = false, bool withdrawn = false) => new(
        id, $"P{id}", cls, type, item, program, customer, kinds, combine, "person:b", At, withdrawn ? "person:b" : null, withdrawn ? At : null, withdrawn ? "old" : null);

    [Fact]
    public void No_matching_policy_still_requires_a_technical_review()
    {
        var required = ReviewPolicies.For([Policy(1, [ReviewKind.Quality], cls: ChangeClass.Major)], Change(ChangeClass.Minor, ChangeType.Design), []);
        Assert.Equal([ReviewKind.Technical], required.Kinds);
        Assert.False(required.CombineApprovalRelease);
    }

    [Fact]
    public void Every_criterion_must_match_and_matching_policies_add_up()
    {
        var policies = new[]
        {
            Policy(1, [ReviewKind.Technical, ReviewKind.Quality], cls: ChangeClass.Major),
            Policy(2, [ReviewKind.Procurement], item: ItemClass.BuildToPrint),
            Policy(3, [ReviewKind.Program], program: Falcon, customer: "Acme"),
            Policy(4, [ReviewKind.Maintenance], cls: ChangeClass.Major, withdrawn: true),
            Policy(5, [ReviewKind.Manufacturing], cls: ChangeClass.Major, type: ChangeType.Process),
        };

        var required = ReviewPolicies.For(policies, Change(ChangeClass.Major, ChangeType.Design, Falcon, " acme "), [ItemClass.BuildToPrint]);
        Assert.Equal([ReviewKind.Technical, ReviewKind.Quality, ReviewKind.Procurement, ReviewKind.Program], required.Kinds);
        Assert.Equal(3, required.Basis.Count);

        var other = ReviewPolicies.For(policies, Change(ChangeClass.Major, ChangeType.Design, Falcon, "Other"), [ItemClass.Make]);
        Assert.Equal([ReviewKind.Technical, ReviewKind.Quality], other.Kinds);
    }

    [Fact]
    public void Any_matching_policy_can_combine_approval_and_release()
    {
        var required = ReviewPolicies.For([Policy(1, [ReviewKind.Technical], type: ChangeType.Documentation, combine: true), Policy(2, [ReviewKind.Quality])], Change(ChangeClass.Administrative, ChangeType.Documentation), []);
        Assert.True(required.CombineApprovalRelease);
        Assert.Equal([ReviewKind.Technical, ReviewKind.Quality], required.Kinds);
    }
}
