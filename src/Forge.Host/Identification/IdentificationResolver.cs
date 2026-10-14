using System.Globalization;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Security;
using Forge.Kernel.WorkAssignments;
using Forge.Resources.Contracts;

namespace Forge.Host.Identification;

public sealed record ResolvedIdentification(string Code, string Namespace, string Kind, long? OrderNumber, int? Sequence,
    string? PartNumber, string? Revision, string Status, string? LotNumber = null, string? LocationCode = null, string? IdentityKind = null,
    string? SubjectKey = null, string? DisplayName = null, long? RecordNumber = null, int? LineNumber = null, string? SourceFingerprint = null);

public sealed record IdentificationConfiguration(string Namespace, IReadOnlyList<string> SupportedKinds, int MaxCodeLength, int MaxKeyLength, string Version);

/// <summary>Resolves a presentation key through current owning records and their existing source authorization.</summary>
public sealed partial class IdentificationResolver(IKernelUnitOfWorkFactory unitOfWork, IWorkSourceResolver sources,
    IForgeAuthorizer authorizer, IConfiguration configuration, Forge.Quality.Contracts.IQmsPolicyReader? policyReader = null, TimeProvider? time = null)
{
    public IdentificationConfiguration GetConfiguration()
    {
        var configuredNamespace = configuration[CanonicalIdentification.NamespaceConfigurationKey];
        if (!CanonicalIdentification.IsValidNamespace(configuredNamespace))
            throw new IdentificationException("identification_not_configured", "Identification requires a valid configured namespace.");
        return new(configuredNamespace!, CanonicalIdentification.SupportedKinds, CanonicalIdentification.MaxCodeLength, CanonicalIdentification.MaxKeyLength, "v1");
    }

    public async Task<ResolvedIdentification> ResolveAsync(ForgePrincipal principal, string? code, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        _ = CanonicalIdentification.Parse(code, configuration[CanonicalIdentification.NamespaceConfigurationKey]);
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        return (await ResolveSubjectAsync(uow, principal, code, ct)).Display;
    }

    private static IdentificationException NotFound() => new("identification_not_found", "Identification does not refer to an accessible source.");
}
