using Forge.Engineering.Contracts;
using Forge.Kernel.Engines;
using Forge.Kernel.Security;
using Forge.Planning.Engines;

namespace Forge.Planning.Tests;

/// <summary>V2.3 applicability: date effectivity by default, rules on top, never a guess.</summary>
public class ApplicabilityTests
{
    private static readonly DateTimeOffset Jan10 = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Mar10 = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Falcon = Guid.Parse("01a0f000-0000-7000-8000-000000000001");

    private static DocumentRevisionInfo Rev(string revision, DocumentRevisionStatus status, DateOnly? from = null, DateOnly? to = null) =>
        new("DWG-100", revision, status, "ab", revision + ".pdf", 10, null, null, "person:author", Jan10.AddDays(-30), null, null, null, null,
            from is null ? null : "person:releaser", from is null ? null : Jan10.AddDays(-20), from, to, null, null);

    /// <summary>Rev A in force from Jan 1; rev B released, in force from Feb 1.</summary>
    private static DocumentInfo Drawing(params DocumentRevisionInfo[] extra) => new(
        "DWG-100", "Slit roll", DocumentType.Drawing, DocumentAuthority.Internal, null, null, null, DataMarking.None, "person:author", Jan10.AddDays(-30),
        [Rev("A", DocumentRevisionStatus.Superseded, new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 1)), Rev("B", DocumentRevisionStatus.Released, new DateOnly(2026, 2, 1)), .. extra]);

    private static ApplicabilityFacts Facts(long order = 48172, Guid? program = null, string? customer = null, int dueDay = 15) =>
        new(order, "ROLL-100", "A", program, customer, new DateOnly(2026, 3, dueDay));

    private static ApplicabilityRuleInfo Rule(long id, string revision, ApplicabilityCondition condition, Guid? program = null, string? customer = null, DateOnly? from = null, DateOnly? to = null, long? order = null, DateTimeOffset? created = null, DateTimeOffset? withdrawn = null) =>
        new(id, "DWG-100", revision, condition, program, customer, from, to, order, order is null ? null : "DV-12", null, "person:dc", created ?? Jan10.AddDays(-5), withdrawn is null ? null : "person:dc", withdrawn, withdrawn is null ? null : "done");

    private static DocumentNeed Need(string? pinned = null, DateTimeOffset? added = null) => new("DWG-100", pinned, null, "ROLL-100 rev A", added ?? Jan10.AddDays(-10));

    [Fact]
    public void Date_effectivity_is_the_default_rule()
    {
        Assert.Equal("A", ApplicabilityEngine.Resolve(Drawing(), null, [], Facts(), Jan10).Revision!.Revision);
        var march = ApplicabilityEngine.Resolve(Drawing(), null, [], Facts(), Mar10);
        Assert.Equal("B", march.Revision!.Revision);
        Assert.Equal(ResolutionBasis.DateEffectivity, march.Basis);
        Assert.Equal(ResolutionBasis.None, ApplicabilityEngine.Resolve(Drawing(), null, [], Facts(), new DateTimeOffset(2025, 12, 1, 0, 0, 0, TimeSpan.Zero)).Basis);
    }

    [Fact]
    public void Each_rule_decides_on_its_own_and_withdrawn_rules_do_not()
    {
        var program = ApplicabilityEngine.Resolve(Drawing(), null, [Rule(1, "A", ApplicabilityCondition.Program, program: Falcon)], Facts(program: Falcon), Mar10);
        Assert.Equal(("A", ResolutionBasis.Rule), (program.Revision!.Revision, program.Basis));
        Assert.Equal("B", ApplicabilityEngine.Resolve(Drawing(), null, [Rule(1, "A", ApplicabilityCondition.Program, program: Falcon)], Facts(), Mar10).Revision!.Revision);

        var customer = ApplicabilityEngine.Resolve(Drawing(), null, [Rule(2, "A", ApplicabilityCondition.Customer, customer: "USAF")], Facts(customer: " usaf "), Mar10);
        Assert.Equal("A", customer.Revision!.Revision);

        var due = new[] { Rule(3, "A", ApplicabilityCondition.DueDate, from: new DateOnly(2026, 3, 1), to: new DateOnly(2026, 3, 20)) };
        Assert.Equal("A", ApplicabilityEngine.Resolve(Drawing(), null, due, Facts(dueDay: 20), Mar10).Revision!.Revision);
        Assert.Equal("B", ApplicabilityEngine.Resolve(Drawing(), null, due, Facts(dueDay: 21), Mar10).Revision!.Revision);

        var deviation = ApplicabilityEngine.Resolve(Drawing(), null, [Rule(4, "A", ApplicabilityCondition.Order, order: 48172)], Facts(), Mar10);
        Assert.Equal(("A", ResolutionBasis.Deviation), (deviation.Revision!.Revision, deviation.Basis));
        Assert.Equal("B", ApplicabilityEngine.Resolve(Drawing(), null, [Rule(4, "A", ApplicabilityCondition.Order, order: 1)], Facts(), Mar10).Revision!.Revision);

        var withdrawn = Rule(5, "A", ApplicabilityCondition.Program, program: Falcon, withdrawn: Jan10);
        Assert.Equal("B", ApplicabilityEngine.Resolve(Drawing(), null, [withdrawn], Facts(program: Falcon), Mar10).Revision!.Revision);
        Assert.Equal("A", ApplicabilityEngine.Resolve(Drawing(), null, [withdrawn], Facts(program: Falcon), Jan10.AddDays(-1)).Revision!.Revision);
    }

    [Fact]
    public void Rules_that_disagree_require_review_unless_a_deviation_decides()
    {
        var c = Rev("C", DocumentRevisionStatus.Superseded, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1));
        var rules = new[] { Rule(1, "A", ApplicabilityCondition.Program, program: Falcon), Rule(2, "C", ApplicabilityCondition.Customer, customer: "USAF") };
        var conflict = ApplicabilityEngine.Evaluate(Drawing(c), Need(), rules, Facts(program: Falcon, customer: "USAF"), Mar10, null);
        Assert.Equal(DocumentState.RequiresReview, conflict.State);
        Assert.Equal(EvidenceOutcome.Indeterminate, conflict.Outcome);

        var decided = ApplicabilityEngine.Resolve(Drawing(c), null, [.. rules, Rule(3, "C", ApplicabilityCondition.Order, order: 48172)], Facts(program: Falcon, customer: "USAF"), Mar10);
        Assert.Equal(("C", ResolutionBasis.Deviation), (decided.Revision!.Revision, decided.Basis));
    }

    [Fact]
    public void Released_orders_are_compared_with_what_applied_at_release()
    {
        // Released in January with rev A; rev B took effect in February by date: work in progress continues.
        var legit = ApplicabilityEngine.Evaluate(Drawing(), Need(), [], Facts(), Mar10, Jan10);
        Assert.Equal((DocumentState.LegitimatelySuperseded, "A", "B"), (legit.State, legit.AtRelease, legit.Applicable));
        Assert.Equal(EvidenceOutcome.Pass, legit.Outcome);

        // Released in March with rev B: current.
        Assert.Equal(DocumentState.Current, ApplicabilityEngine.Evaluate(Drawing(), Need(), [], Facts(), Mar10, Mar10.AddHours(-1)).State);

        // A deviation written after release moves the order to another revision: mismatch until reissued.
        var deviation = Rule(9, "A", ApplicabilityCondition.Order, order: 48172, created: Mar10.AddDays(-1));
        var mismatch = ApplicabilityEngine.Evaluate(Drawing(), Need(), [deviation], Facts(), Mar10, Mar10.AddDays(-2));
        Assert.Equal((DocumentState.Mismatch, EvidenceOutcome.Fail), (mismatch.State, mismatch.Outcome));

        // The revision the order was released with was made obsolete.
        var obsolete = new DocumentInfo("DWG-100", "Slit roll", DocumentType.Drawing, DocumentAuthority.Internal, null, null, null, DataMarking.None, "person:author", Jan10,
            [Rev("A", DocumentRevisionStatus.Obsolete, new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 1)), Rev("B", DocumentRevisionStatus.Released, new DateOnly(2026, 2, 1))]);
        Assert.Equal(DocumentState.Mismatch, ApplicabilityEngine.Evaluate(obsolete, Need(), [], Facts(), Mar10, Jan10).State);

        // A requirement added after release is checked against now only.
        Assert.Equal(DocumentState.Current, ApplicabilityEngine.Evaluate(Drawing(), Need(added: Mar10.AddDays(-1)), [], Facts(), Mar10, Jan10).State);
    }

    [Fact]
    public void Missing_unreleased_and_pinned_revisions()
    {
        var draftOnly = new DocumentInfo("DWG-100", "Slit roll", DocumentType.Drawing, DocumentAuthority.Internal, null, null, null, DataMarking.None, "person:author", Jan10, [Rev("A", DocumentRevisionStatus.Draft)]);
        Assert.Equal(DocumentState.Missing, ApplicabilityEngine.Evaluate(draftOnly, Need(), [], Facts(), Mar10, null).State);
        Assert.Equal(DocumentState.NotReleased, ApplicabilityEngine.Evaluate(draftOnly, Need("A"), [], Facts(), Mar10, null).State);

        var pinned = ApplicabilityEngine.Evaluate(Drawing(), Need("A"), [], Facts(), Mar10, Jan10);
        Assert.Equal((DocumentState.Pinned, EvidenceOutcome.Pass), (pinned.State, pinned.Outcome));
        Assert.Equal(DocumentState.Missing, ApplicabilityEngine.Evaluate(Drawing(), Need("Z"), [], Facts(), Mar10, null).State);
    }

    [Fact]
    public void The_documentation_dimension_fails_on_any_failed_document_and_is_indeterminate_on_review()
    {
        DocumentApplicability Doc(int? op, DocumentState state) => new("D" + op, "T", DocumentAuthority.Internal, "src", op, null, null, "A", state, ApplicabilityEngine.Outcome(state), state.ToString());
        var docs = new OrderDocumentation(1, "P", "A", ItemClass.Make, true, [Doc(null, DocumentState.Current), Doc(20, DocumentState.Mismatch), Doc(30, DocumentState.RequiresReview)], "s");
        Assert.Equal(EvidenceOutcome.Pass, docs.DimensionFor(10).Outcome);
        Assert.Equal(EvidenceOutcome.Fail, docs.DimensionFor(20).Outcome);
        Assert.Equal(EvidenceOutcome.Indeterminate, docs.DimensionFor(30).Outcome);
        Assert.Equal(EvidenceOutcome.Pass, (docs with { Documents = [] }).DimensionFor(10).Outcome);
    }
}
