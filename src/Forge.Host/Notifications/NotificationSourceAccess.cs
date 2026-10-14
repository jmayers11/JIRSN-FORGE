using Forge.Kernel.Access;
using Forge.Kernel.Security;
using Forge.Kernel.WorkAssignments;
using Forge.Host.History;
using Forge.Quality.Qms;

namespace Forge.Host.Notifications;

/// <summary>Inbox and external delivery share the owning source's current read authority.</summary>
internal static class NotificationSourceAccess
{
    public static async Task<bool> CanSeeAsync(IKernelUnitOfWork uow, IWorkSourceResolver sources,
        IForgeAuthorizer authorizer, QmsService qms, ForgePrincipal principal, string type, string id, CancellationToken ct)
    {
        if (QmsSourceAccess.IsQms(type)) return await QmsSourceAccess.CanSeeAsync(uow, qms, principal, type, id, ct);
        var source = await sources.ResolveAsync(uow, type, id, ct);
        if (source is null) return type is not ("Order" or "ProductionOrder" or "OrderOperation" or "Ncr" or "NCR" or "Capa" or "CAPA" or "Document" or "DocumentRevision" or "DocumentLink" or "Hold" or "Machine" or "WorkCenter");
        return source.Resources.All(resource => authorizer.Authorize(principal, source.ReadPermission, resource).IsPermitted);
    }
}
