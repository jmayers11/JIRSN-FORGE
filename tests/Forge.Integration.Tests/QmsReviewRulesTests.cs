using Forge.Kernel.Commands;
using Forge.Quality.Contracts;
using Forge.Quality.Review;
using Xunit;

namespace Forge.Integration.Tests;

public sealed class QmsReviewRulesTests
{
    [Fact]
    public void A_populated_link_and_adequacy_field_cannot_supply_missing_licensed_normative_verification()
    {
        var error = Assert.Throws<CommandRejectedException>(() => QmsReviewRules.ValidateMappingAssessment(
            QmsEvidenceAdequacy.Adequate, QmsConformityAssessment.Supported, true,
            "Human interpretation", "Compare actual owner evidence", "Applicable to the recorded process", "", "Evidence linked", "Licensed source still unavailable"));
        Assert.Equal("mapping_licensed_source_pending", error.Code);
        QmsReviewRules.ValidateMappingAssessment(QmsEvidenceAdequacy.NotAssessed,
            QmsConformityAssessment.RequiresLicensedVerification, true, "Provisional interpretation", "Authorized-copy review needed",
            "Applicability not yet established", "", "No conformity conclusion", "Licensed source still unavailable");
    }
    [Theory]
    [InlineData(QmsEvidenceAdequacy.Inadequate)]
    [InlineData(QmsEvidenceAdequacy.Unavailable)]
    [InlineData(QmsEvidenceAdequacy.NotAssessed)]
    public void Supported_conformity_requires_a_separate_adequate_human_evidence_judgment(QmsEvidenceAdequacy adequacy)
    {
        var error = Assert.Throws<CommandRejectedException>(() => QmsReviewRules.ValidateMappingAssessment(
            adequacy, QmsConformityAssessment.Supported, false, "Human interpretation", "Authorized criterion assessment",
            "Applicable process", "", "The link exists", "Actual evidence not adequate"));
        Assert.Equal("mapping_evidence_inadequate", error.Code);
    }
    [Fact]
    public void Partial_frozen_input_cannot_be_changed_to_adequate_by_filling_in_meeting_text()
    {
        var now = DateTimeOffset.UtcNow;
        var input = new QmsReviewFrozenInput("CONFORMITY", "Product conformity", true, [], [], ["Recorded cohort is incomplete"], "actual-input-fingerprint");
        var manifest = new QmsReviewInputManifest(Guid.NewGuid(), Guid.NewGuid(), "REVIEW", 1, 5, "actual-definition-fingerprint",
            now.AddDays(-30), now.AddDays(-1), now, "recorded-capture-actor", now, [input], [], "actual-manifest-fingerprint", "actual-database-snapshot");
        var error = Assert.Throws<CommandRejectedException>(() => QmsReviewRules.ValidateAssessments(manifest,
            [new("CONFORMITY", QmsEvidenceAdequacy.Adequate, "All linked", "Incomplete cohort", "Declare complete", [])]));
        Assert.Equal("review_incomplete_input", error.Code);
        QmsReviewRules.ValidateAssessments(manifest,
            [new("CONFORMITY", QmsEvidenceAdequacy.Inadequate, "Cohort limitation reviewed", "Incomplete cohort", "Investigate the missing coverage", [])]);
    }
    [Fact]
    public void Every_required_frozen_agenda_item_needs_its_own_attributed_assessment()
    {
        var now = DateTimeOffset.UtcNow;
        var manifest = new QmsReviewInputManifest(Guid.NewGuid(), Guid.NewGuid(), "REVIEW", 1, 5, "definition",
            now.AddDays(-30), now.AddDays(-1), now, "capture-actor", now,
            [new("CUSTOMERS", "Customer feedback", true, [], [], [], "customers"), new("RISKS", "Risk actions", true, [], [], [], "risks")], [], "manifest", "snapshot");
        var error = Assert.Throws<CommandRejectedException>(() => QmsReviewRules.ValidateAssessments(manifest,
            [new("CUSTOMERS", QmsEvidenceAdequacy.Adequate, "Feedback assessed", "", "Continue feedback process", [])]));
        Assert.Equal("review_required_assessment", error.Code);
    }
    [Fact]
    public void An_assigned_review_decision_needs_actual_owner_due_date_resources_and_completion_criteria()
    {
        var owner = Guid.NewGuid();
        var error = Assert.Throws<CommandRejectedException>(() => QmsReviewRules.ValidateDecisions(
            [new("ACTION", "Improve training coverage", "Reviewed qualification gaps", "Qualified coverage", "Training capacity", owner, null, "Qualified workers at each governed operation", [])]));
        Assert.Equal("review_action_due", error.Code);
        QmsReviewRules.ValidateDecisions(
            [new("ACTION", "Improve training coverage", "Reviewed qualification gaps", "Qualified coverage", "Training capacity", owner, DateTimeOffset.UtcNow.AddDays(14), "Qualified workers at each governed operation", [])]);
    }
    [Fact]
    public void A_review_period_must_be_closed_and_finite_at_the_actual_snapshot_cutoff()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("review_period", Assert.Throws<CommandRejectedException>(() => QmsReviewRules.Period(now.AddDays(-1), now.AddDays(1), now)).Code);
        Assert.Equal("review_period", Assert.Throws<CommandRejectedException>(() => QmsReviewRules.Period(now, now, now)).Code);
        QmsReviewRules.Period(now.AddDays(-30), now.AddDays(-1), now);
    }
    [Fact]
    public void Actual_content_contributors_cannot_independently_approve_their_own_assessment()
    {
        Assert.Equal("review_independent_assessment_required", Assert.Throws<CommandRejectedException>(() =>
            QmsReviewRules.Independent("person:author", ["person:old-editor", "person:author"])).Code);
        QmsReviewRules.Independent("person:independent-reviewer", ["person:old-editor", "person:author"]);
    }
}
