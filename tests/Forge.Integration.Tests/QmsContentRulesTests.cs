using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;
using Forge.Quality.Qms;

namespace Forge.Integration.Tests;

public sealed class QmsContentRulesTests
{
    private static readonly Guid Owner = Guid.Parse("d4d9300b-e46f-4cd1-bc0b-72a5f1b6b530");
    private static QmsPolicyContent Policy(QmsPolicyKind kind, QmsPolicyRules? rules) => new(kind,
        "Explicit organization policy", Owner, "Supervisor communication with recorded acknowledgment", "Annual and on change", false, rules);

    private static void Rejected(QmsRecordKind kind, QmsContent content, string code) =>
        Assert.Equal(code, Assert.Throws<CommandRejectedException>(() => QmsContentRules.Validate(kind, content)).Code);

    [Fact]
    public void Operational_policies_require_complete_matching_typed_rules_without_inferred_defaults()
    {
        var valid = new QmsContent(Policy: Policy(QmsPolicyKind.Execution, new(Execution: new(true, true, true, true))));
        QmsContentRules.Validate(QmsRecordKind.Policy, valid);
        Rejected(QmsRecordKind.Policy, valid with { Policy = valid.Policy! with { Rules = null } }, "policy_rules");
        Rejected(QmsRecordKind.Policy, valid with { Policy = valid.Policy! with { Rules = new(Execution: new(true, null, true, true)) } }, "determination_required");
        Rejected(QmsRecordKind.Policy, valid with { Policy = valid.Policy! with { Rules = new(LabelCopy: new(true, 2, true)) } }, "policy_rules");
        Rejected(QmsRecordKind.Policy, valid with { Process = new("Unexpected second section", ["Input"], ["Output"], Owner, [], ["People"], [], [new("Measure", "Compare", "Recorded output", "Monthly")]) }, "content_kind");
    }

    [Fact]
    public void Retention_has_explicit_finite_age_and_count_bounds_and_preserves_existing_audit_invariants()
    {
        QmsContent Content(QmsAttemptRetentionRules rules) => new(Policy: Policy(QmsPolicyKind.AttemptRetention, new(AttemptRetention: rules)));
        var valid = new QmsAttemptRetentionRules(30, 365, 10000, true, true, true);
        QmsContentRules.Validate(QmsRecordKind.Policy, Content(valid));
        Rejected(QmsRecordKind.Policy, Content(valid with { MaximumRawAttemptAgeDays = 20 }), "policy_range");
        Rejected(QmsRecordKind.Policy, Content(valid with { MaximumStoredRawAttempts = null }), "policy_range");
        Rejected(QmsRecordKind.Policy, Content(valid with { MinimumRetentionDays = 0 }), "policy_range");
        Rejected(QmsRecordKind.Policy, Content(valid with { NeverRewriteAuditChain = false }), "audit_invariant");
        Rejected(QmsRecordKind.Policy, Content(valid with { PreserveDeniedAttempts = false }), "audit_invariant");
        Rejected(QmsRecordKind.Policy, Content(valid with { PreserveSourceLink = null }), "determination_required");
    }

    [Fact]
    public void Licensed_verification_is_a_separate_human_source_determination_and_not_inferred_from_local_approval()
    {
        var reference = new QmsStandardReference("ISO 9001", "2015", "4.1", QmsStandardVerification.PendingAuthorizedVerification,
            "An authorized licensed copy has not been verified; local context definition only.", null);
        var content = new QmsContent(Requirement: new("Organization context", QmsRequirementClass.ISO, "Local context determination",
            QmsApplicability.Applicable, "Leadership's recorded applicability basis", Owner, [reference]));
        QmsContentRules.Validate(QmsRecordKind.Requirement, content);
        Assert.True(QmsContentRules.RequiresLicensedVerification(content));
        Rejected(QmsRecordKind.Requirement, content with { Requirement = content.Requirement! with { StandardReferences = [] } }, "standard_reference_required");
        Rejected(QmsRecordKind.Requirement, content with { Requirement = content.Requirement! with { StandardReferences = [reference with { Verification = QmsStandardVerification.VerifiedAuthorizedCopy }] } }, "verification_document_required");
        var verified = content with { Requirement = content.Requirement! with { StandardReferences = [reference with {
            Verification = QmsStandardVerification.VerifiedAuthorizedCopy, VerificationBasis = "Named human verified the authorized copy and exact edition; no conformity claim.",
            VerificationDocument = new("AUTHORIZED-COPY", "2015") }] } };
        QmsContentRules.Validate(QmsRecordKind.Requirement, verified);
        Assert.False(QmsContentRules.RequiresLicensedVerification(verified));
        Assert.Equal(new QmsDocumentReference("AUTHORIZED-COPY", "2015"), Assert.Single(QmsContentRules.DocumentsFor(verified)));
    }

    [Fact]
    public void Profiles_need_a_human_scope_climate_determination_and_non_applicability_basis()
    {
        var profile = new QmsProfileContent("Example organization", "Manufacture controlled assemblies at the named facility",
            ["Assemblies"], [], [new("External", "Supply continuity", "Reviewed supplier records")], [new("Customer", "Delivery and product acceptance", "Actual contracts")],
            ["Recorded contracts"], ["Applicable safety law determined by leadership"], false, "Physical facility review recorded by leadership",
            [new("NOT-APPLICABLE", 2, "Human determination based on the exact requirement revision")], [], [new("NOT-APPLICABLE", 2)]);
        QmsContentRules.Validate(QmsRecordKind.Profile, new(Profile: profile));
        Rejected(QmsRecordKind.Profile, new(Profile: profile with { ClimateRelevant = null }), "determination_required");
        Rejected(QmsRecordKind.Profile, new(Profile: profile with { Scope = " " }), "qms_content_required");
        Rejected(QmsRecordKind.Profile, new(Profile: profile with { NonApplicability = [new("NOT-APPLICABLE", 2, "")] }), "qms_content_required");
    }

    [Fact]
    public void Process_records_require_real_owner_identity_and_meaningful_inputs_outputs_and_monitoring()
    {
        var process = new QmsProcessContent("Production", ["Released requirements"], ["Accepted product"], Owner,
            [new("PURCHASING", 1)], ["Qualified people and maintained equipment"], ["Loss of supply assessed by process owner"],
            [new("Acceptance", "Compare inspection results", "Recorded inspections", "Monthly")]);
        QmsContentRules.Validate(QmsRecordKind.Process, new(Process: process));
        Rejected(QmsRecordKind.Process, new(Process: process with { OwnerPersonId = Guid.Empty }), "owner_required");
        Rejected(QmsRecordKind.Process, new(Process: process with { Outputs = [] }), "qms_content_required");
        Rejected(QmsRecordKind.Process, new(Process: process with { Monitoring = [] }), "qms_content_required");
        QmsContentRules.Validate(QmsRecordKind.Process, new(Process: process, Documents: [new("QP." + new string('A', 57), "A.1")]));
        Assert.Equal("QP.001", QmsContentRules.DocumentNumber(" qp.001 "));
        Assert.Equal("A.1", QmsContentRules.DocumentRevision("a.1"));
    }
}
