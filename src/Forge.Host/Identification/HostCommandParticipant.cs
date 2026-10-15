using Forge.Engineering;
using Forge.Engineering.Contracts;
using Forge.Host.Commercial;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;

namespace Forge.Host.Identification;

/// <summary>Composition resolves owner dependencies only while executing an already-constructed command service.</summary>
public sealed class HostCommandParticipantFactory(IServiceProvider services) : ICommandTransactionParticipantFactory
{
    public ICommandTransactionParticipant Create() => services.GetRequiredService<HostCommandParticipant>();
}

public sealed class HostCommandParticipant(ScanCommandParticipant scans, IGeneratedContentOwnerAccess files,
    CommercialCommandGuard commercial) : ICommandTransactionParticipant
{
    private sealed record CombinedEvidence(CommandTransactionEvidence? Scan, CommandTransactionEvidence? Commercial);

    public async Task<CommandTransactionEvidence?> ValidateAsync(IKernelUnitOfWork uow, CommandSpec spec, object command,
        CommandContext context, CommandAuthority authority, DateTimeOffset now, CancellationToken ct)
    {
        var sha = command switch
        {
            AddDocumentRevision revision => revision.FileSha256,
            UpdateDocumentDraft draft => draft.FileSha256,
            AttachFile attachment => attachment.FileSha256,
            StoreDocumentFile upload => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(upload.Content)).ToLowerInvariant(),
            _ => spec.SourceContext == "Engineering" && spec.CommandType == nameof(StoreDocumentFile) ? spec.Resource.Id : null,
        };
        if (!string.IsNullOrWhiteSpace(sha)) await RequireFileAsync(uow, context.Principal, sha, ct);
        var scanEvidence = await scans.ValidateAsync(uow, spec, command, context, authority, now, ct);
        var commercialEvidence = await commercial.ValidateAsync(uow, spec, command, context, authority, now, ct);
        if (scanEvidence is null) return commercialEvidence;
        if (commercialEvidence is null) return scanEvidence;
        return new("HostCommandEvidence", CanonicalJson.Fingerprint(new { ScanFingerprint = scanEvidence.Fingerprint, CommercialFingerprint = commercialEvidence.Fingerprint }),
            new CombinedEvidence(scanEvidence, commercialEvidence));
    }

    public Task RecordAcceptedAsync(IKernelUnitOfWork uow, CommandSpec spec, CommandContext context, CommandOutcome outcome,
        Guid? auditId, CommandTransactionEvidence evidence, DateTimeOffset now, CancellationToken ct) =>
        evidence.Data switch
        {
            CombinedEvidence combined => RecordCombinedAsync(uow, spec, context, outcome, auditId, combined, evidence.Fingerprint, now, ct),
            _ when evidence.Kind == "IdentificationScanSession" => scans.RecordAcceptedAsync(uow, spec, context, outcome, auditId, evidence, now, ct),
            _ => commercial.RecordAcceptedAsync(uow, spec, context, outcome, auditId, evidence, evidence.Fingerprint, now, ct),
        };

    public Task RecordRejectedAsync(IKernelUnitOfWork uow, CommandSpec spec, object command, CommandContext context,
        CommandAuthority authority, string outcome, string validationCode, Guid auditId, DateTimeOffset now,
        CancellationToken ct) => scans.RecordRejectedAsync(uow, spec, command, context, authority,
            outcome, validationCode, auditId, now, ct);

    private async Task RecordCombinedAsync(IKernelUnitOfWork uow, CommandSpec spec, CommandContext context,
        CommandOutcome outcome, Guid? auditId, CombinedEvidence evidence, string contextFingerprint, DateTimeOffset now, CancellationToken ct)
    {
        if (evidence.Scan is not null) await scans.RecordAcceptedAsync(uow, spec, context, outcome, auditId, evidence.Scan, now, ct);
        if (evidence.Commercial is not null) await commercial.RecordAcceptedAsync(uow, spec, context, outcome, auditId,
            evidence.Commercial, contextFingerprint, now, ct);
    }

    private async Task RequireFileAsync(IKernelUnitOfWork uow, ForgePrincipal principal, string sha, CancellationToken ct)
    {
        principal = await uow.People.LoadPrincipalAsync(principal.PersonId, ct) is { IsActive: true } current ? current
            : throw new ForgeAuthorizationException(new(false, ["A current registered principal is required."]));
        var access = await files.CheckAsync(uow, principal, sha.Trim().ToLowerInvariant(), ct);
        if (access.KnownBinding && !access.IsPermitted)
            throw new ForgeAuthorizationException(access.Denial ?? new(false, ["The generated content's current owning sources are inaccessible."]), access.DeniedResource);
    }
}
