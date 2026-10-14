using System.Globalization;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Security;
using Forge.Kernel.WorkAssignments;
using Forge.Resources.Contracts;

namespace Forge.Host.Identification;

public sealed record ResolvedIdentification(string Code, string Namespace, string Kind, long? OrderNumber, int? Sequence,
    string? PartNumber, string? Revision, string Status, string? LotNumber = null, string? LocationCode = null, string? IdentityKind = null);

public sealed record IdentificationConfiguration(string Namespace, IReadOnlyList<string> SupportedKinds, int MaxCodeLength, int MaxKeyLength, string Version);

/// <summary>Resolves a presentation key through current owning records and their existing source authorization.</summary>
public sealed class IdentificationResolver(IKernelUnitOfWorkFactory unitOfWork, IWorkSourceResolver sources,
    IForgeAuthorizer authorizer, IConfiguration configuration)
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
        var identification = CanonicalIdentification.Parse(code, configuration[CanonicalIdentification.NamespaceConfigurationKey]);
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        if (identification.Kind == "material")
        {
            var lot = await uow.Store<IInventoryLookup>().FindLotAsync(identification.PartNumber!, identification.LotNumber!, ct);
            if (lot is null || !authorizer.Authorize(principal, Permissions.InventoryRead,
                new ResourceRef("Lot", CanonicalIdentification.EncodeKey(lot.PartNumber) + ":" + CanonicalIdentification.EncodeKey(lot.LotNumber))).IsPermitted)
                throw NotFound();
            // The owner records identity, not one stock-quality state or an originating program.
            return new(identification.Code, identification.Namespace, identification.Kind, null, null,
                lot.PartNumber, lot.PartRevision, "Recorded", LotNumber: lot.LotNumber, IdentityKind: lot.IdentityKind.ToString());
        }
        if (identification.Kind == "location")
        {
            var location = await uow.Store<IInventoryLookup>().FindLocationAsync(identification.LocationCode!, ct);
            if (location is null) throw NotFound();
            var workCenter = location.WorkCenterCode;
            if (workCenter is null && location.MachineCode is { } machineCode)
                workCenter = (await uow.Store<IResourcesLookup>().FindMachineAsync(machineCode, ct))?.WorkCenterCode;
            var resource = new ResourceRef("Location", location.Code, FacilityId: location.SiteCode, WorkCenterId: workCenter);
            if (!authorizer.Authorize(principal, Permissions.InventoryRead, resource).IsPermitted) throw NotFound();
            return new(identification.Code, identification.Namespace, identification.Kind, null, null, null, null,
                location.Active ? "Active" : "Inactive", LocationCode: location.Code);
        }
        var order = identification.OrderNumber is { } orderNumber ? await uow.Store<IExecutionLookup>().FindOrderAsync(orderNumber, ct) : null;
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
