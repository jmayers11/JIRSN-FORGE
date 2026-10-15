using Forge.Kernel.Access;
using Forge.Kernel.Security;

namespace Forge.Quality.Contracts;

/// <summary>Actual failed inspection/NCR and existing customer ownership, resolved in the consuming transaction.</summary>
public sealed record CommercialCustomerContext(CustomerConcessionScope Scope, string FacilityCode,
    IReadOnlyList<CommercialReference> CustomerSources, IReadOnlyList<CommercialReadSource> Sources,
    IReadOnlyList<ResourceRef> Ownership, string Fingerprint);
public sealed record CommercialCustomerApproval(bool Required, CustomerConcessionScope Scope,
    IReadOnlyList<CommercialRevision> Requirements, CommercialRevision? Communication,
    CommercialObservation? ReceivedApproval, IReadOnlyList<CommercialReadSource> Sources,
    IReadOnlyList<ResourceRef> Ownership, string Fingerprint);

/// <summary>External customer consent is distinct from internal communication approval and Quality product release.</summary>
public interface ICommercialCustomerApproval
{
    Task<CommercialCustomerApproval?> RequireAsync(IKernelUnitOfWork uow, ForgePrincipal principal,
        AcceptanceSubjectKey subject, string revision, long ncrId, decimal quantity, Disposition disposition,
        DateTimeOffset at, CancellationToken ct);
}
