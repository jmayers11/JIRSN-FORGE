using Forge.Kernel.Access;
using Forge.Kernel.Security;
using Forge.Quality.Commercial;

namespace Forge.Quality.Contracts;

public sealed record CommercialAcceptedUse(Guid UseId, Guid AuditId, string ContextFingerprint, string ComponentFingerprint,
    string EntityType, string EntityId, string Actor, DateTimeOffset At, CommercialUseEvidence Evidence);
public interface ICommercialUseLookup
{
    Task<CommercialAcceptedUse?> FindAsync(Guid useId, CancellationToken ct);
    Task<CommercialAcceptedUse?> FindForAuditAsync(Guid auditId, CancellationToken ct);
    Task<CommercialAcceptedUse?> LatestForEntityAsync(string type, string id, string activity, CancellationToken ct);
}
internal interface ICommercialUseStore : ICommercialUseLookup
{
    Task InsertAsync(CommercialAcceptedUse use, CancellationToken ct);
}
public interface ICommercialUseAccess
{
    Task<bool> CanReadAuditAsync(IKernelUnitOfWork uow, ForgePrincipal principal, Guid auditId, CancellationToken ct);
    Task<CommercialUseEvidence?> RequireDesignAsync(IKernelUnitOfWork uow, ForgePrincipal principal,
        string part, string revision, DateTimeOffset at, CancellationToken ct);
}
