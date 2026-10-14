using Forge.Engineering;
using Forge.Engineering.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;

namespace Forge.Host.Identification;

/// <summary>Composition resolves owner dependencies only while executing an already-constructed command service.</summary>
public sealed class HostCommandParticipantFactory(IServiceProvider services) : ICommandTransactionParticipantFactory
{
    public ICommandTransactionParticipant Create() => services.GetRequiredService<HostCommandParticipant>();
}

public sealed class HostCommandParticipant(ScanCommandParticipant scans, IGeneratedContentOwnerAccess files) : ICommandTransactionParticipant
{
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
        return await scans.ValidateAsync(uow, spec, command, context, authority, now, ct);
    }

    public Task RecordAcceptedAsync(IKernelUnitOfWork uow, CommandSpec spec, CommandContext context, CommandOutcome outcome,
        Guid? auditId, CommandTransactionEvidence evidence, DateTimeOffset now, CancellationToken ct) =>
        scans.RecordAcceptedAsync(uow, spec, context, outcome, auditId, evidence, now, ct);

    private async Task RequireFileAsync(IKernelUnitOfWork uow, ForgePrincipal principal, string sha, CancellationToken ct)
    {
        principal = await uow.People.LoadPrincipalAsync(principal.PersonId, ct) is { IsActive: true } current ? current
            : throw new ForgeAuthorizationException(new(false, ["A current registered principal is required."]));
        var access = await files.CheckAsync(uow, principal, sha.Trim().ToLowerInvariant(), ct);
        if (access.KnownBinding && !access.IsPermitted)
            throw new ForgeAuthorizationException(access.Denial ?? new(false, ["The generated content's current owning sources are inaccessible."]), access.DeniedResource);
    }
}
