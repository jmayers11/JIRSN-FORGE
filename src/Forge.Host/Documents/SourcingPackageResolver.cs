using Forge.Engineering.Contracts;
using Forge.Kernel.Access;
using Forge.Planning;
using Forge.Procurement.Contracts;

namespace Forge.Host.Documents;

public sealed class SourcingPackageResolver : ISourcingPackageResolver
{
    public async Task<SourcingPackage> ResolveAsync(IKernelUnitOfWork uow, long rfqId, RequisitionInfo need, DateTimeOffset now, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(need);
        var applicable = await DocumentationService.ForSourcingAsync(uow, rfqId, need, now, ct);
        var documents = new List<PackageDocument>();
        foreach (var result in applicable.Documents)
        {
            var document = await uow.Store<IDocumentLookup>().FindDocumentAsync(result.DocumentNumber, ct);
            var revision = document?.Revisions.FirstOrDefault(r => r.Revision == result.Applicable);
            documents.Add(new PackageDocument(result.DocumentNumber, result.Title, result.Applicable, result.State.ToString(), result.Source, revision?.FileSha256, revision?.FileName));
        }

        return new SourcingPackage(rfqId, need.PartNumber, applicable.ItemRevision, applicable.Summary, now, actor, documents);
    }
}
