using System.Globalization;
using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Security;
using Forge.Kernel.WorkAssignments;

namespace Forge.Host.Identification;

public sealed record ResolvedIdentification(string Code, string Namespace, string Kind, long OrderNumber, int? Sequence,
    string PartNumber, string Revision, string Status);

/// <summary>Resolves a presentation key through current owning records and their existing source authorization.</summary>
public sealed class IdentificationResolver(IKernelUnitOfWorkFactory unitOfWork, IWorkSourceResolver sources,
    IForgeAuthorizer authorizer, IConfiguration configuration)
{
    public async Task<ResolvedIdentification> ResolveAsync(ForgePrincipal principal, string? code, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var identification = CanonicalIdentification.Parse(code, configuration[CanonicalIdentification.NamespaceConfigurationKey]);
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var order = await uow.Store<IExecutionLookup>().FindOrderAsync(identification.OrderNumber, ct);
        if (order is null) throw NotFound();

        var operation = identification.Sequence is { } sequence ? order.Operations.FirstOrDefault(row => row.Sequence == sequence) : null;
        if (identification.Sequence is not null && operation is null) throw NotFound();

        var sourceType = identification.Sequence is null ? "Order" : "OrderOperation";
        var sourceId = order.OrderNumber.ToString(CultureInfo.InvariantCulture)
            + (identification.Sequence is { } sourceSequence ? "/" + sourceSequence.ToString(CultureInfo.InvariantCulture) : string.Empty);
        var source = await sources.ResolveAsync(uow, sourceType, sourceId, ct);
        if (source is null || source.Resources.Count == 0
            || source.Resources.Any(resource => !authorizer.Authorize(principal, source.ReadPermission, resource).IsPermitted))
            throw NotFound();

        return new ResolvedIdentification(identification.Code, identification.Namespace, identification.Kind, order.OrderNumber,
            identification.Sequence, order.PartNumber, order.Revision, operation?.Status.ToString() ?? order.Status.ToString());
    }

    private static IdentificationException NotFound() => new("identification_not_found", "Identification does not refer to an accessible source.");
}
