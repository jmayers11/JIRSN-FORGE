using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Quality.Contracts;

namespace Forge.Quality.Qms;

/// <summary>Explicit operational controls. Missing extensions remain valid historic definitions but cannot authorize dependent features.</summary>
public static class QmsPolicyRuleValidation
{
    public static void ValidateExtensions(QmsPolicyRules rules)
    {
        if (rules.Execution?.Presentation is { } execution) Execution(execution);
        if (rules.LabelCopy?.Lifecycle is { } labels) Labels(labels);
        if (rules.AttemptRetention?.Telemetry is { } telemetry) Telemetry(telemetry);
    }

    public static void RequireConsumable(QmsPolicyPurpose purpose, QmsContent content)
    {
        QmsContentRules.Validate(QmsRecordKind.Policy, content);
        var policy = content.Policy!;
        if (policy.PolicyKind != Kind(purpose)) Reject("policy_purpose", "The exact policy revision has the wrong operational purpose.");
        var rules = policy.Rules!;
        if (purpose == QmsPolicyPurpose.ProductRelease && rules.ProductRelease?.ReleasePermission != Permissions.QualityProductRelease)
            Reject("release_permission", "Product release always requires the canonical independent Quality decision permission.");
        ValidateExtensions(rules);
        if (purpose == QmsPolicyPurpose.Execution && rules.Execution?.Presentation is null
            || purpose == QmsPolicyPurpose.LabelCopy && rules.LabelCopy?.Lifecycle is null
            || purpose == QmsPolicyPurpose.AttemptRetention && rules.AttemptRetention?.Telemetry is null)
            Reject("policy_rules_incomplete", "Adopt a new approved policy revision with every dependent operational determination explicitly recorded.");
    }

    public static QmsPolicyKind Kind(QmsPolicyPurpose purpose) => purpose switch
    {
        QmsPolicyPurpose.Execution => QmsPolicyKind.Execution,
        QmsPolicyPurpose.ProductRelease => QmsPolicyKind.ProductRelease,
        QmsPolicyPurpose.LabelCopy => QmsPolicyKind.LabelCopy,
        QmsPolicyPurpose.AttemptRetention => QmsPolicyKind.AttemptRetention,
        _ => throw new CommandRejectedException("policy_purpose", "Unknown governed policy purpose.")
    };

    public static void ValidateAdoption(QmsPolicyAdoptionContent content)
    {
        if (content is null || content.Selector is null) Reject("policy_content", "Supply a policy binding and actual operation selector.");
        Kind(content.Purpose); BusinessCode.Require(content.PolicyCode, "Policy"); Positive(content.PolicyRevision);
        Selector(content.Selector); Text(content.Basis);
        if (content.Process is { } process) { BusinessCode.Require(process.Code, "Process"); Positive(process.Revision); }
        if (content.RiskClassCode is not null) { BusinessCode.Require(content.RiskClassCode, "Risk classification"); Text(content.RiskBasis); }
        else if (!string.IsNullOrWhiteSpace(content.RiskBasis)) Reject("risk_binding", "A risk basis must name its human-defined classification.");
        if (content.Purpose != QmsPolicyPurpose.Execution && (content.Process is not null || content.RiskClassCode is not null))
            Reject("policy_selector", "Process/risk operation bindings apply to Execution policies.");
    }

    public static bool Matches(QmsOperationSelector selector, QmsPolicyContext context) =>
        Match(selector.WorkCenterCode, context.WorkCenterCode) && Match(selector.ActivityCode, context.ActivityCode)
        && Match(selector.MachineCode, context.MachineCode) && (selector.OperationSequence is null || selector.OperationSequence == context.OperationSequence)
        && Match(selector.PartNumber, context.PartNumber) && Match(selector.PartRevision, context.PartRevision);

    public static QmsOperationScanRule MatchOperationRule(QmsExecutionPresentationRules rules, QmsPolicyAdoptionContent binding, QmsPolicyContext context)
    {
        var matching = rules.OperationRules.Where(r => Matches(r.Selector, context)
            && (r.Process is null || binding.Process is {} process && r.Process.Revision == process.Revision && Match(r.Process.Code, process.Code))
            && Match(r.RiskClassCode, binding.RiskClassCode)).ToArray();
        if (matching.Length == 0) Reject("operation_policy_not_defined", "The adopted policy has no complete rule for this actual operation/process/risk binding.");
        if (matching.Length != 1) Reject("operation_policy_ambiguous", "Multiple operation rules match. Correct the approved definitions instead of selecting a looser rule.");
        return matching[0];
    }

    public static bool ScopeMatches(QmsPolicyAdoptionView adoption, QmsPolicyAdoptionContent binding, QmsPolicyPurpose purpose, QmsPolicyContext context) =>
        binding.Purpose == purpose && Match(adoption.FacilityCode, context.FacilityCode)
        && (adoption.ProgramId is null || adoption.ProgramId == context.ProgramId) && Matches(binding.Selector, context);

    public static bool ScopeMayMatch(QmsPolicyAdoptionView adoption, QmsPolicyAdoptionContent binding, QmsPolicyPurpose purpose, QmsPolicyContext context) =>
        binding.Purpose == purpose && Match(adoption.FacilityCode, context.FacilityCode)
        && (adoption.ProgramId is null || context.ProgramId is null || adoption.ProgramId == context.ProgramId)
        && Potential(binding.Selector.WorkCenterCode, context.WorkCenterCode) && Potential(binding.Selector.ActivityCode, context.ActivityCode)
        && Potential(binding.Selector.MachineCode, context.MachineCode) && (binding.Selector.OperationSequence is null || context.OperationSequence is null || binding.Selector.OperationSequence == context.OperationSequence)
        && Potential(binding.Selector.PartNumber, context.PartNumber) && Potential(binding.Selector.PartRevision, context.PartRevision);

    private static void Execution(QmsExecutionPresentationRules rules)
    {
        Range(rules.SessionExpirySeconds, 1, 3600); Explicit(rules.RequireServerSession); Explicit(rules.RequireCurrentAuthenticatedOperator);
        Explicit(rules.RequireTrustedWorkstation); Defined(rules.ProviderAssurance);
        if (rules.RequireServerSession != true
            || rules.RequireCurrentAuthenticatedOperator != true && rules.RequireTrustedWorkstation != true)
            Reject("session_invariant", "Consequential staging requires a server session and either the authenticated operating person or an explicitly governed trusted terminal with a separately identified worker.");
        foreach (var rule in List(rules.OperationRules, true))
        {
            BusinessCode.Require(rule.Code, "Operation rule"); Selector(rule.Selector);
            if (rule.Process is { } process) { BusinessCode.Require(process.Code, "Process"); Positive(process.Revision); }
            if (rule.RiskClassCode is not null) BusinessCode.Require(rule.RiskClassCode, "Risk classification");
            foreach (var scan in List(rule.RequiredScans)) if (!Enum.IsDefined(scan)) Reject("scan_kind", "Select a supported typed identification requirement.");
            if (rule.RequiredScans.Distinct().Count() != rule.RequiredScans.Count) Reject("duplicate_scan", "A required identification kind must be unique within a rule.");
            Explicit(rule.AllowManualSelection); Explicit(rule.RequireInstructionRevision); Explicit(rule.RequireTrustedTerminalContext);
        }
        if (rules.OperationRules.Select(r => BusinessCode.Require(r.Code, "Operation rule")).Distinct(StringComparer.Ordinal).Count() != rules.OperationRules.Count)
            Reject("duplicate_rule", "Operation rule codes must be unique.");
    }
    private static void Labels(QmsLabelLifecycleRules rules)
    {
        Defined(rules.ActiveCopyMode); Defined(rules.RequiredConfirmation); Range(rules.MaximumActiveCopies, 1, 10000);
        if (rules.ActiveCopyMode == QmsActiveCopyMode.SingleActive && rules.MaximumActiveCopies != 1)
            Reject("copy_limit", "A single-active-copy policy must explicitly set its active-copy maximum to one.");
        Explicit(rules.AllowReprint); Explicit(rules.AllowReplacement); Explicit(rules.AllowVoid); Explicit(rules.ReplacementVoidsPriorCopy);
        Explicit(rules.RequireIndependentCopyAuthorization); Explicit(rules.AllowAliasCreate); Explicit(rules.AllowAliasReplacement); Explicit(rules.AllowAliasVoid);
        if (rules.ActiveCopyMode == QmsActiveCopyMode.SingleActive && rules.AllowReplacement == true && rules.ReplacementVoidsPriorCopy != true)
            Reject("copy_limit", "Single-active replacement must retire its prior logical copy atomically.");
        var allowedKinds = List(rules.AllowedSubjectKinds, true);
        var allowedTemplates = List(rules.AllowedTemplates, true);
        var allowedSymbologies = List(rules.AllowedSymbologies, true);
        foreach (var kind in allowedKinds)
            if (string.IsNullOrWhiteSpace(kind) || kind.Length > 40 || kind.Any(c => !(c is >= 'a' and <= 'z' or '-')))
                Reject("label_kind", "Use exact lowercase canonical primitive-kind names.");
        foreach (var template in allowedTemplates) { BusinessCode.Require(template.Code, "Label template"); Positive(template.Version); }
        foreach (var symbology in allowedSymbologies) if (!Enum.IsDefined(symbology)) Reject("label_symbology", "Select a recognized symbology; the consumer must separately support it.");
        if (allowedKinds.Distinct(StringComparer.Ordinal).Count() != allowedKinds.Count
            || allowedTemplates.Select(t => (BusinessCode.Require(t.Code, "Label template"), t.Version)).Distinct().Count() != allowedTemplates.Count
            || allowedSymbologies.Distinct().Count() != allowedSymbologies.Count) Reject("duplicate_label_choice", "Allowed lifecycle choices must be unique.");
    }
    private static void Telemetry(QmsTelemetryRules rules)
    {
        Defined(rules.PayloadMode); Range(rules.MaximumCapturedReferenceCharacters, 1, 1024);
        Explicit(rules.RetainDeclaredAdapter); Explicit(rules.RetainDeclaredDevice); Explicit(rules.RetainCaptureMethod);
        // Unknown raw input and credential/provider-claim payloads are never retained in either mode.
    }
    private static void Selector(QmsOperationSelector selector)
    {
        if (selector is null) Reject("policy_selector", "Supply an explicit selector object; an empty object is a deliberate facility-wide choice.");
        foreach (var code in new[] { selector.WorkCenterCode, selector.ActivityCode, selector.MachineCode, selector.PartNumber })
            if (code is not null) BusinessCode.Require(code, "Selector");
        if (selector.OperationSequence is { } sequence && sequence is < 1 or > 9999) Reject("policy_selector", "Operation sequence must be between 1 and 9999.");
        if (selector.PartRevision is not null) { if (selector.PartNumber is null) Reject("policy_selector", "A revision selector must name its actual part."); QmsContentRules.DocumentRevision(selector.PartRevision); }
    }
    private static bool Match(string? expected, string? actual) => expected is null || string.Equals(expected.Trim(), actual?.Trim(), StringComparison.OrdinalIgnoreCase);
    private static bool Potential(string? expected, string? actual) => actual is null || Match(expected, actual);
    private static void Positive(int value) { if (value <= 0) Reject("policy_revision", "Reference a positive immutable revision/version."); }
    private static void Text(string? value) { if (string.IsNullOrWhiteSpace(value) || value.Length > 4000) Reject("policy_basis", "Record the explicit human scope/applicability basis."); }
    private static void Explicit(bool? value) { if (value is null) Reject("policy_rules_incomplete", "Supply every governance determination explicitly."); }
    private static void Range(int? value, int minimum, int maximum) { if (value is null || value < minimum || value > maximum) Reject("policy_range", "Supply an explicit finite operational bound."); }
    private static void Defined<T>(T? value) where T : struct, Enum { if (value is null || !Enum.IsDefined(value.Value)) Reject("policy_rules_incomplete", "Supply the explicit recognized governance choice."); }
    private static IReadOnlyList<T> List<T>(IReadOnlyList<T>? values, bool nonempty = false)
    {
        if (values is null || values.Count > 200 || nonempty && values.Count == 0 || values.Any(v => v is null)) Reject("policy_rules_incomplete", "Supply a bounded, explicit list of governance choices.");
        return values;
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Reject(string code, string message) => throw new CommandRejectedException(code, message);
}
