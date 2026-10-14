using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;

namespace Forge.Host.Identification;

public static class ScanSessionRules
{
    public static string CreationFingerprint(CreateScanSession command, CommandContext context) =>
        CanonicalJson.Fingerprint(new { command, context.Actor, context.Source, context.IdempotencyKey });

    public static string Channel(ForgePrincipal principal, CommandAuthenticationEvidence? authentication)
    {
        if (authentication is null || string.IsNullOrWhiteSpace(authentication.Subject) || string.IsNullOrWhiteSpace(authentication.Issuer))
            throw new CommandRejectedException("scan_authenticated_context_required", "The scan session requires validated provider authentication.");
        return !string.IsNullOrWhiteSpace(authentication.WorkstationId)
            ? CanonicalJson.Fingerprint(new { authentication.Issuer, authentication.WorkstationId })
            : AuthenticationBinding(principal, authentication);
    }

    public static string AuthenticationBinding(ForgePrincipal principal, CommandAuthenticationEvidence authentication) =>
        CanonicalJson.Fingerprint(new { principal.PersonId, authentication.Subject, authentication.Issuer,
            authentication.ProviderSessionId, authentication.WorkstationId });

    public static void RequireAuthentication(QmsAdoptedPolicy policy, ForgePrincipal principal, Guid worker,
        CommandAuthenticationEvidence? authentication, DateTimeOffset now)
    {
        var rules = policy.Rules.Execution!.Presentation!;
        _ = Channel(principal, authentication);
        if (authentication!.ValidUntil is { } until && now >= until)
            throw new CommandRejectedException("scan_authentication_expired", "Provider authentication has expired. Sign in again.");
        if (rules.RequireCurrentAuthenticatedOperator == true && worker != principal.PersonId.Value)
            throw new CommandRejectedException("scan_operator_authentication_required", "The selected worker must be the current provider-authenticated person.");
        if ((rules.RequireTrustedWorkstation == true || rules.RequireCurrentAuthenticatedOperator != true || policy.OperationRule?.RequireTrustedTerminalContext == true)
            && (string.IsNullOrWhiteSpace(authentication.ProviderSessionId) || string.IsNullOrWhiteSpace(authentication.WorkstationId)))
            throw new CommandRejectedException("scan_trusted_terminal_required", "The adopted policy requires a validated provider session and trusted workstation claim.");
        if (rules.ProviderAssurance == QmsProviderAssurance.StepUp
            && (authentication.Assurance != "StepUp" || authentication.AuthenticatedAt is null || authentication.ValidUntil is null))
            throw new CommandRejectedException("scan_step_up_required", "The adopted policy requires fresh provider step-up authentication.");
    }

    public static void RequireActive(ScanSessionView session, Guid current, string channel, Guid principal,
        int expectedVersion, DateTimeOffset now)
    {
        if (session.ChannelHash != channel || session.PrincipalPersonId != principal || current != session.Id
            || session.State != ScanSessionState.Active)
            throw new CommandRejectedException("scan_session_inactive", "The current operator generation changed or the session was invalidated.");
        if (session.ExpiresAt <= now)
            throw new CommandRejectedException("scan_session_expired", "This session expired. Open a new session.");
        if (session.Version != expectedVersion)
            throw new CommandRejectedException("scan_session_version_conflict", "The staged context changed. Reload its current version.");
    }

    public static string SourceFingerprint(IdentificationSubject subject) => CanonicalJson.Fingerprint(new
    {
        subject.CanonicalSubjectCode, subject.SourceType, subject.SourceId, subject.LabelId, subject.AliasId,
        subject.Display.PartNumber, subject.Display.Revision, subject.Display.LotNumber, subject.Display.LocationCode,
        subject.Display.SourceFingerprint, subject.PresentationFingerprint, subject.Resources, subject.ReadSources,
    });

    public static bool ManualAllowed(QmsAdoptedPolicy policy) => policy.OperationRule!.AllowManualSelection == true;
    public static IReadOnlyList<QmsRequiredScanKind> RequiredKinds(QmsAdoptedPolicy policy) => policy.OperationRule!.RequiredScans;
    public static int Expiry(QmsAdoptedPolicy policy) => policy.Rules.Execution!.Presentation!.SessionExpirySeconds!.Value;
    public static string StageKind(string kind) => kind switch
    {
        "order" or "traveler" => "Order",
        "operation" or "wip" => "Operation",
        "material" or "batch" or "roll" or "serial" or "remnant" => "Material",
        "location" => "Location", "machine" => "Machine", "workcenter" => "WorkCenter",
        "tool" => "Tool", "gauge" => "Gauge", "instruction" => "ControlledInstruction",
        "container" or "package" => "Container",
        _ => throw new CommandRejectedException("scan_kind_not_executable", "This accessible identification kind is not an execution selection.")
    };

    public static void RequireSelections(ScanSessionView session, QmsAdoptedPolicy policy)
    {
        foreach (var kind in RequiredKinds(policy))
            if (!session.Selections.Any(selection => selection.Kind == kind.ToString()))
                throw new CommandRejectedException("scan_required_selection_missing", "A required current selection is missing: " + kind + ".");
        if (policy.OperationRule!.RequireInstructionRevision == true
            && !session.Selections.Any(selection => selection.Kind == "ControlledInstruction"))
            throw new CommandRejectedException("scan_required_instruction_missing", "The policy requires an exact current controlled instruction revision.");
    }
}
