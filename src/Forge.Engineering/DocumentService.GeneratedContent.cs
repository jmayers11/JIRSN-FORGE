using Forge.Kernel.Access;
using Forge.Kernel.Security;

namespace Forge.Engineering;

public sealed partial class DocumentService
{
    private async Task RequireGeneratedContentAsync(IKernelUnitOfWork uow, ForgePrincipal principal, string sha, CancellationToken ct)
    {
        if (generatedContent is null) return;
        var current = await uow.People.LoadPrincipalAsync(principal.PersonId, ct);
        if (current is not { IsActive: true })
            throw new ForgeAuthorizationException(new(false, ["A current registered principal is required to use generated content."]));
        var access = await generatedContent.CheckAsync(uow, current, sha, ct);
        if (access.KnownBinding && !access.IsPermitted)
            throw new ForgeAuthorizationException(access.Denial ?? new(false, ["The generated content's current owning sources are inaccessible."]), access.DeniedResource);
    }
}
