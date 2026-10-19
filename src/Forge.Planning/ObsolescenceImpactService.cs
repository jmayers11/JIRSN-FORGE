using Forge.Engineering.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Maintenance.Contracts;
using Forge.Planning.Engines;
using Forge.Procurement.Contracts;
using Forge.Resources.Contracts;

namespace Forge.Planning;

/// <summary>Gathers what an obsolete item touches from each owner and runs the obsolescence impact engine (V2.8).</summary>
public sealed class ObsolescenceImpactService(IKernelUnitOfWorkFactory unitOfWork, IForgeAuthorizer authorizer, TimeProvider time)
{
    public async Task<ObsolescenceImpact> ImpactAsync(ForgePrincipal principal, long recordId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var decision = authorizer.Authorize(principal, Permissions.EngineeringRead, ResourceRef.Of("ObsolescenceImpact"));
        if (!decision.IsPermitted)
        {
            throw new ForgeAuthorizationException(decision);
        }

        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var record = await uow.Store<IObsolescenceLookup>().FindObsolescenceAsync(recordId, ct)
            ?? throw new CommandRejectedException("obsolescence_not_found", $"Obsolescence record {recordId} does not exist.");
        return ObsolescenceImpactEngine.Evaluate(await LoadAsync(uow, record, principal, time.GetUtcNow(), ct));
    }

    public static async Task<ObsolescenceSnapshot> LoadAsync(IKernelUnitOfWork uow, ObsolescenceInfo record, ForgePrincipal principal, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(principal);
        var part = record.PartNumber;
        var engineering = uow.Store<IEngineeringLookup>();
        var item = await engineering.FindItemAsync(part, ct);

        var assetLookup = uow.Store<IAssetLookup>();
        var assets = (await assetLookup.ListAssetsAsync(ct)).ToDictionary(a => a.AssetCode, StringComparer.Ordinal);
        var installed = (await assetLookup.ListComponentsAsync(null, part, false, ct))
            .Select(c =>
            {
                var a = assets[c.AssetCode];
                return new ObsolescenceInstall(c.ComponentId, a.AssetCode, a.Name, a.Status, a.WorkCenterCode, a.MachineCode, c.Position, c.Quantity, c.InstalledByWorkOrder);
            })
            .ToList();

        var inventory = uow.Store<IInventoryLookup>();
        var balances = await inventory.ListBalancesAsync(part, ct);
        var available = balances.Where(b => b.Status == StockStatus.Released).Sum(b => b.Quantity);
        var held = balances.Where(b => b.Status != StockStatus.Released).Sum(b => b.Quantity);

        var received = (await inventory.ListPurchaseReceiptsAsync(ct)).ToDictionary(r => (r.PoNumber, r.LineNumber), r => r.Received);
        var purchases = (await uow.Store<IProcurementLookup>().ListPurchaseOrdersAsync(ct))
            .Where(po => po.Status == PurchaseOrderStatus.Approved)
            .SelectMany(po => po.Lines.Where(l => l.PartNumber == part && l.Status == PurchaseLineStatus.Open)
                .Select(l => new ObsolescenceOpenPurchase(po.PoNumber, l.LineNumber, Math.Max(0, l.Quantity - received.GetValueOrDefault((po.PoNumber, l.LineNumber))), l.PromisedDate)))
            .Where(p => p.OpenQuantity > 0)
            .ToList();

        bool Visible(Guid? program) => program is not { } p || principal.Programs.Contains(new ProgramId(p));
        var alternates = (await uow.Store<IAlternateLookup>().ListAlternatesAsync(ct)).Where(a => a.PrimaryPart == part && Visible(a.ProgramId)).ToList();

        var whereUsed = (await engineering.ListReleasedStructuresAsync(ct))
            .Where(s => s.Revision.IsEffectiveAt(now))
            .SelectMany(s => s.Bom.Where(l => l.ComponentPartNumber == part).Select(l => new ObsolescenceWhereUsed(s.Revision.PartNumber, s.Revision.Revision, l.LineNumber, l.QuantityPer)))
            .ToList();

        var openDemand = (await uow.Store<IDemandStore>().ListAsync(false, ct))
            .Where(d => d.PartNumber == part && d.Status == DemandStatus.Open && Visible(d.ProgramId))
            .ToList();
        var demandIds = openDemand.Select(d => d.DemandId).ToHashSet();

        // A requisition raised for a demand listed here is the same need; only the others are added.
        var demand = openDemand
            .Select(d => new ObsolescenceDemand(d.SourceKey ?? $"demand:{d.DemandId}", d.Kind.ToString(), d.Quantity, d.NeedDate))
            .Concat((await uow.Store<IRequisitionLookup>().ListRequisitionsAsync(true, ct))
                .Where(r => r.PartNumber == part && Visible(r.ProgramId) && !(r.DemandId is { } id && demandIds.Contains(id)))
                .Select(r => new ObsolescenceDemand($"requisition {r.RequisitionId} ({r.SourceKey})", r.SourceKind.ToString(), r.Quantity, r.NeedDate)))
            .ToList();

        return new ObsolescenceSnapshot(record, item?.UnitOfMeasure ?? "EA", installed, available, held, purchases, alternates, whereUsed, demand, await uow.SiteDateAsync(now, ct));
    }
}
