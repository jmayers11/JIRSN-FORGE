using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Planning.Engines;
using Forge.Procurement.Contracts;
using Forge.Quality.Contracts;

namespace Forge.Planning;

/// <summary>Loads one snapshot and runs the change impact engine for an engineering change (V2.4).</summary>
public sealed class ChangeImpactService(IKernelUnitOfWorkFactory unitOfWork, IForgeAuthorizer authorizer, TimeProvider time)
{
    public async Task<ChangeImpact> ImpactAsync(ForgePrincipal principal, string changeNumber, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var decision = authorizer.Authorize(principal, Permissions.EngineeringRead, ResourceRef.Of("ChangeImpact"));
        if (!decision.IsPermitted)
        {
            throw new ForgeAuthorizationException(decision);
        }

        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var number = (changeNumber ?? string.Empty).Trim().ToUpperInvariant();
        var change = await uow.Store<IChangeLookup>().FindChangeAsync(number, ct);
        if (change is null || (change.ProgramId is { } p && !principal.Programs.Contains(new ProgramId(p))))
        {
            throw new CommandRejectedException("change_not_found", $"Engineering change {number} does not exist.");
        }

        return ChangeImpactEngine.Evaluate(await LoadAsync(uow, change, principal, time.GetUtcNow(), ct));
    }

    private static async Task<ChangeImpactInput> LoadAsync(IKernelUnitOfWork uow, ChangeInfo change, ForgePrincipal principal, DateTimeOffset now, CancellationToken ct)
    {
        var documents = uow.Store<IDocumentLookup>();
        var requirements = await documents.ListRequirementsAsync(null, null, false, ct);
        var engineering = uow.Store<IEngineeringLookup>();
        var parts = new List<ImpactedPart>();
        foreach (var line in change.ActiveLines.Where(l => l.Kind == ChangeLineKind.ItemRevision))
        {
            parts.Add(new ImpactedPart(line.PartNumber!, line.FromRevision, line.ToRevision, $"{line.PartNumber} rev {line.FromRevision ?? "(none)"} → rev {line.ToRevision}"));
        }

        foreach (var line in change.ActiveLines.Where(l => l.Kind == ChangeLineKind.DocumentRevision))
        {
            var current = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var r in requirements.Where(r => r.DocumentNumber == line.DocumentNumber))
            {
                if (parts.Any(x => x.PartNumber == r.PartNumber) || current.ContainsKey(r.PartNumber))
                {
                    continue;
                }

                // A part counts when the revision in force now (or the requirement's revision, if none is in force) is built to the document.
                var inForce = (await engineering.ListRevisionsAsync(r.PartNumber, ct)).FirstOrDefault(x => x.IsEffectiveAt(now))?.Revision;
                if (inForce == r.Revision || inForce is null)
                {
                    current[r.PartNumber] = r.Revision;
                }
            }

            parts.AddRange(current.Select(c => new ImpactedPart(c.Key, c.Value, null, $"rev {c.Value} is built to {line.DocumentNumber} (rev {line.FromRevision ?? "(none)"} → rev {line.ToRevision})")));
        }

        // The order list carries no operations; orders of the parts reached are read whole to tell started from not started.
        var execution = uow.Store<IExecutionLookup>();
        var reached = parts.Select(p => p.PartNumber).ToHashSet(StringComparer.Ordinal);
        var orders = new List<OrderInfo>();
        foreach (var order in (await execution.ListOrdersAsync(ct)).Where(o => reached.Contains(o.PartNumber)))
        {
            orders.Add(await execution.FindOrderAsync(order.OrderNumber, ct) ?? order);
        }

        return new ChangeImpactInput(
            change,
            parts,
            orders,
            await uow.Store<IProcurementLookup>().ListPurchaseOrdersAsync(ct),
            await uow.Store<IInventoryLookup>().ListBalancesAsync(null, ct),
            await uow.Store<IInventoryLookup>().ListLotsAsync(null, ct),
            await engineering.ListReleasedStructuresAsync(ct),
            await uow.Store<IQualityLookup>().ListPlansAsync(null, ct),
            requirements,
            program => program is not { } id || principal.Programs.Contains(new ProgramId(id)));
    }
}
