using Forge.Engineering.Contracts;
using Forge.Kernel.Access;
using Forge.Planning;
using Forge.Procurement.Contracts;

namespace Forge.Host.Documents;

/// <summary>
/// The technical package of each purchase order line at approval (V2.5): the item revision in force and
/// every document it is built to or the order is attached to follow, at the revision that applies, with
/// the file that revision holds. Recorded whole — markings and program access restrict who may read it,
/// never what was sent.
/// </summary>
public sealed class TechnicalPackageResolver : ITechnicalPackageResolver
{
    public async Task<IReadOnlyList<TechnicalPackage>> ResolveAsync(IKernelUnitOfWork uow, PurchaseOrderInfo po, DateTimeOffset now, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(po);
        var documentation = await DocumentationService.ForPurchaseOrderAsync(uow, po, now, null, ct);
        var documents = uow.Store<IDocumentLookup>();
        var packages = new List<TechnicalPackage>();
        foreach (var line in documentation.Lines)
        {
            var sent = new List<PackageDocument>();
            foreach (var d in line.Documents)
            {
                var revision = d.Applicable is { } applicable && await documents.FindDocumentAsync(d.DocumentNumber, ct) is { } document
                    ? document.Revisions.FirstOrDefault(r => r.Revision == applicable)
                    : null;
                sent.Add(new PackageDocument(d.DocumentNumber, d.Title, d.Applicable, d.State.ToString(), d.Source, revision?.FileSha256, revision?.FileName));
            }

            packages.Add(new TechnicalPackage(po.PoNumber, line.LineNumber, line.PartNumber, line.ItemRevision, line.Summary, now, actor, sent));
        }

        return packages;
    }
}
