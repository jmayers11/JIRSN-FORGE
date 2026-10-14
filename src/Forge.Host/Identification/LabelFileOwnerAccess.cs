using Forge.Engineering.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Security;

namespace Forge.Host.Identification;

/// <summary>Recognizes label artifacts before every generic file read/use fallback, including document attachments.</summary>
public sealed class LabelFileOwnerAccess(LabelService labels) : IGeneratedContentOwnerAccess
{
    public async Task<GeneratedContentAccess> CheckAsync(IKernelUnitOfWork uow, ForgePrincipal principal, string sha256, CancellationToken ct)
    {
        var store = uow.Store<IIdentificationPresentationStore>();
        var bindings = await store.FindArtifactOwnersAsync(sha256, ct);
        if (bindings.Count == 0) return new(false, false, []);
        ForgeAuthorizationException? denied = null;
        foreach (var binding in bindings)
        {
            try
            {
                var copy = (await store.FindAsync<LabelCopyView>("Copy", PresentationAccess.Key(binding.LabelId), ct))?.Record;
                if (copy is null) continue;
                var subject = await labels.ReadAttemptAsync(uow, principal, binding, ct);
                await labels.RequireArtifactTemplateAsync(uow, principal, copy, ct);
                return new(true, true, subject.Resources);
            }
            catch (ForgeAuthorizationException error) { denied ??= error; }
            catch (Forge.Kernel.Commands.CommandRejectedException) { }
        }
        return new(true, false, [], denied?.Decision, denied?.OwningResource);
    }
}
