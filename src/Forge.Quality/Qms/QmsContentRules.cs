using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Quality.Contracts;
using System.Text.RegularExpressions;

namespace Forge.Quality.Qms;

/// <summary>Structural requirements for explicit human QMS definitions, without manufacturing an organization policy.</summary>
public static partial class QmsContentRules
{
    public static bool RequiresLicensedVerification(QmsContent content) =>
        content.Requirement is { } r && r.StandardReferences.Any(s => s.Verification == QmsStandardVerification.PendingAuthorizedVerification);

    public static void Validate(QmsRecordKind kind, QmsContent content)
    {
        if (content is null) Reject("qms_content_required", "Supply the typed QMS content.");
        if (!Enum.IsDefined(kind) || new object?[] { content.Profile, content.Process, content.Policy, content.Requirement }.Count(x => x is not null) != 1
            || (kind == QmsRecordKind.Profile) != (content.Profile is not null)
            || (kind == QmsRecordKind.Process) != (content.Process is not null)
            || (kind == QmsRecordKind.Policy) != (content.Policy is not null)
            || (kind == QmsRecordKind.Requirement) != (content.Requirement is not null))
            Reject("content_kind", "Supply exactly the content section that agrees with the record kind.");
        if (content.Documents?.Any(d => d is null) == true || content.Documents?.Count > 200) Reject("qms_content_required", "Supply bounded non-null document references.");
        foreach (var d in content.Documents ?? []) Document(d);
        if ((content.Documents ?? []).GroupBy(d => (DocumentNumber(d.DocumentNumber), DocumentRevision(d.Revision))).Any(g => g.Count() > 1)) Reject("duplicate_source", "Document references must be unique.");
        if (content.Profile is { } p)
        {
            Text(p.OrganizationName); Text(p.Scope); Strings(p.Products); Strings(p.Services);
            if (p.Products.Count + p.Services.Count == 0) Reject("profile_scope", "Define the products or services within the scope.");
            Explicit(p.ClimateRelevant); Text(p.ClimateBasis);
            foreach (var i in Required(p.Issues)) { Text(i.Kind); Text(i.Description); Text(i.Basis); }
            foreach (var i in Required(p.InterestedParties)) { Text(i.Name); Text(i.Requirements); Text(i.Basis); }
            Strings(p.CustomerRequirements); Strings(p.StatutoryRequirements);
            foreach (var n in Required(p.NonApplicability)) { BusinessCode.Require(n.RequirementCode, "Requirement"); Positive(n.Revision); Text(n.Basis); }
            References(p.Processes); References(p.Requirements);
        }
        if (content.Process is { } process)
        {
            Text(process.Name); Owner(process.OwnerPersonId); Strings(process.Inputs, true); Strings(process.Outputs, true);
            References(process.Dependencies); Strings(process.Resources, true); Strings(process.RiskConsiderations);
            foreach (var m in Required(process.Monitoring, true)) { Text(m.Name); Text(m.Method); Text(m.DataSource); Text(m.ReviewInterval); }
        }
        if (content.Policy is { } policy)
        {
            if (!Enum.IsDefined(policy.PolicyKind)) Reject("policy_kind", "Select a recognized policy kind.");
            Text(policy.Statement); Owner(policy.OwnerPersonId); Text(policy.CommunicationMethod); Text(policy.ReviewInterval); Explicit(policy.AcknowledgmentRequired);
            var rules = policy.Rules;
            var count = rules is null ? 0 : new object?[] { rules.Execution, rules.ProductRelease, rules.LabelCopy, rules.AttemptRetention }.Count(x => x is not null);
            var operational = policy.PolicyKind is QmsPolicyKind.Execution or QmsPolicyKind.ProductRelease or QmsPolicyKind.LabelCopy or QmsPolicyKind.AttemptRetention;
            if (count != (operational ? 1 : 0) || (policy.PolicyKind == QmsPolicyKind.Execution) != (rules?.Execution is not null)
                || (policy.PolicyKind == QmsPolicyKind.ProductRelease) != (rules?.ProductRelease is not null)
                || (policy.PolicyKind == QmsPolicyKind.LabelCopy) != (rules?.LabelCopy is not null)
                || (policy.PolicyKind == QmsPolicyKind.AttemptRetention) != (rules?.AttemptRetention is not null))
                Reject("policy_rules", "Supply exactly the typed rules for this operational policy kind.");
            if (rules?.Execution is { } e) { Explicit(e.RequireQualification); Explicit(e.RequirePresentOperator); Explicit(e.RequireReleasedDocuments); Explicit(e.RevalidateAtCommit); }
            if (rules?.ProductRelease is { } r) { Explicit(r.RequireIndependentDecision); Explicit(r.RequireRequiredInspectionEvidence); Explicit(r.RequireDispositionedNonconformities); Text(r.ReleasePermission); }
            if (rules?.LabelCopy is { } l) { Explicit(l.RequireCopyAuthorization); Explicit(l.RetainCopyReason); Range(l.MaximumCopiesPerRequest, 1, 10000); }
            if (rules?.AttemptRetention is { } a)
            {
                Range(a.MinimumRetentionDays, 1, 365000); Range(a.MaximumRawAttemptAgeDays, 1, 365000); Range(a.MaximumStoredRawAttempts, 1, 10000000);
                if (a.MaximumRawAttemptAgeDays < a.MinimumRetentionDays) Reject("policy_range", "Maximum raw-attempt age cannot be less than the minimum retention period.");
                Explicit(a.PreserveDeniedAttempts); Explicit(a.PreserveSourceLink); Explicit(a.NeverRewriteAuditChain);
                if (a.PreserveDeniedAttempts != true || a.PreserveSourceLink != true || a.NeverRewriteAuditChain != true)
                    Reject("audit_invariant", "A recorded retention policy cannot weaken denied-attempt, source-link or immutable audit-chain invariants.");
            }
        }
        if (content.Requirement is { } req)
        {
            Text(req.Title); Text(req.RequirementText); Text(req.ApplicabilityBasis); Owner(req.OwnerPersonId);
            if (!Enum.IsDefined(req.Classification) || !Enum.IsDefined(req.Applicability)) Reject("requirement_kind", "Select a recognized classification and applicability determination.");
            if (req.Classification == QmsRequirementClass.ISO && Required(req.StandardReferences).Count == 0)
                Reject("standard_reference_required", "An ISO requirement needs its edition and clause reference and an explicit verification determination.");
            foreach (var s in Required(req.StandardReferences))
            {
                Text(s.Standard); Text(s.Edition); Text(s.Clause); Text(s.VerificationBasis);
                if (!Enum.IsDefined(s.Verification)) Reject("verification_required", "Record the authorized-copy verification determination.");
                if (s.Verification == QmsStandardVerification.VerifiedAuthorizedCopy && s.VerificationDocument is null)
                    Reject("verification_document_required", "Verified authorized-copy status needs an exact controlled-document revision and a human basis.");
                if (s.VerificationDocument is { } d) Document(d);
            }
        }
    }

    public static IEnumerable<(QmsRevisionReference Reference, QmsRecordKind Kind)> ReferencesFor(QmsContent content)
    {
        foreach (var r in content.Profile?.Processes ?? []) yield return (r, QmsRecordKind.Process);
        foreach (var r in content.Profile?.Requirements ?? []) yield return (r, QmsRecordKind.Requirement);
        foreach (var r in content.Profile?.NonApplicability ?? []) yield return (new(r.RequirementCode, r.Revision), QmsRecordKind.Requirement);
        foreach (var r in content.Process?.Dependencies ?? []) yield return (r, QmsRecordKind.Process);
    }
    public static IEnumerable<QmsDocumentReference> DocumentsFor(QmsContent content) =>
        (content.Documents ?? []).Concat((content.Requirement?.StandardReferences ?? []).Where(s => s.VerificationDocument is not null).Select(s => s.VerificationDocument!)).Distinct();

    private static void References(IReadOnlyList<QmsRevisionReference>? refs) { foreach (var r in Required(refs)) { BusinessCode.Require(r.Code, "QMS record"); Positive(r.Revision); } }
    // Engineering's owning document key domain is 60/20 characters and permits periods; generic business codes do not.
    public static string DocumentNumber(string? value) => DocumentKey(value, DocumentNumberPattern(), "invalid_document_number");
    public static string DocumentRevision(string? value) => DocumentKey(value, DocumentRevisionPattern(), "invalid_revision");
    private static void Document(QmsDocumentReference d) { DocumentNumber(d.DocumentNumber); DocumentRevision(d.Revision); }
    private static string DocumentKey(string? value, Regex pattern, string code)
    {
        var key = (value ?? string.Empty).Trim().ToUpperInvariant();
        if (!pattern.IsMatch(key)) Reject(code, "Use an exact Engineering document number and revision in the owning key domain.");
        return key;
    }
    [GeneratedRegex("^[A-Z0-9][A-Z0-9._-]{0,59}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex DocumentNumberPattern();
    [GeneratedRegex("^[A-Z0-9][A-Z0-9._-]{0,19}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex DocumentRevisionPattern();
    private static void Owner(Guid id) { if (id == Guid.Empty) Reject("owner_required", "Select an actual active responsible person."); }
    private static void Positive(int n) { if (n <= 0) Reject("invalid_revision", "Reference a positive revision number."); }
    private static void Explicit(bool? value) { if (value is null) Reject("determination_required", "Supply each policy or applicability determination explicitly."); }
    private static void Range(int? n, int min, int max) { if (n is null || n < min || n > max) Reject("policy_range", $"The configured value must be between {min} and {max}."); }
    private static void Text(string? value, int max = 4000) { if (string.IsNullOrWhiteSpace(value) || value.Length > max) Reject("qms_content_required", $"Supply a human definition or basis of at most {max} characters."); }
    private static IReadOnlyList<T> Required<T>(IReadOnlyList<T>? values, bool nonempty = false) { if (values is null || values.Count > 200 || nonempty && values.Count == 0 || values.Any(v => v is null)) Reject("qms_content_required", "Supply an explicit bounded list of non-null QMS definitions."); return values; }
    private static void Strings(IReadOnlyList<string>? values, bool nonempty = false) { foreach (var v in Required(values, nonempty)) Text(v); }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Reject(string code, string message) => throw new CommandRejectedException(code, message);
}
