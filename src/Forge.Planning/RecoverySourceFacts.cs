using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Maintenance.Contracts;
using Forge.Planning.Engines;
using Forge.Procurement.Contracts;

namespace Forge.Planning;

/// <summary>Read-only source evidence captured in the scenario's existing consistent snapshot.</summary>
internal sealed record RecoverySourceFacts(
    MaterialSnapshot Material,
    IReadOnlyList<StockPosition> Positions,
    IReadOnlyList<LocationInfo> Locations,
    IReadOnlyList<LotInfo> Lots,
    IReadOnlyList<MaterialTask> Tasks,
    IReadOnlyList<TransformationInfo> Transformations,
    IReadOnlyList<AlternateInfo> Alternates,
    IReadOnlyList<PurchaseOrderInfo> PurchaseOrders,
    IReadOnlyList<WorkOrderInfo> MaintenanceOrders,
    IReadOnlyDictionary<long, RecoveryOrderChecks> OrderChecks,
    IReadOnlyDictionary<string, RecoverySourceCheck> SourceChecks,
    MaterialSourceVisibility? Visibility = null)
{
    /// <summary>Retain the original private scenario's material changes before any independent recovery trial.</summary>
    public static MaterialSnapshot ApplyScenarioMaterial(ScenarioChange change, IReadOnlyList<OrderInfo> proposedOrders,
        IReadOnlyList<OrderMaterialStatus> currentMaterial, MaterialSnapshot source)
    {
        var priorities = proposedOrders.ToDictionary(o => o.OrderNumber, o => o.Priority);
        var proposed = source with
        {
            Orders = source.Orders.Select(o => priorities.TryGetValue(o.OrderNumber, out var priority) ? o with { Priority = priority } : o).ToArray(),
        };
        if (change.Kind != ScenarioChangeKind.SupplierDelay) return proposed;
        var delayedKeys = currentMaterial.Where(m => m.OrderNumber == change.OrderNumber).SelectMany(m => m.Lines)
            .SelectMany(l => l.Allocations).Where(a => a.DueDate is not null).Select(a => a.Source).ToHashSet(StringComparer.Ordinal);
        return proposed with
        {
            Supplies = proposed.Supplies.Select(s => delayedKeys.Contains(s.Key) ? s with { DueDate = s.DueDate.AddDays(change.DelayDays!.Value) } : s).ToArray(),
        };
    }

    public static async Task<RecoverySourceFacts> LoadAsync(IKernelUnitOfWork uow,
        ForgePrincipal principal, ScenarioChange change, SiteSnapshot snapshot, IReadOnlyList<OrderInfo> orders, MaterialSnapshot material,
        IReadOnlyList<StockPosition> sourcePositions, IReadOnlyList<LocationInfo> sourceLocations,
        IInventoryRecoveryReadiness inventoryReadiness, CancellationToken ct, MaterialSourceVisibility? sourceVisibility = null)
    {
        var inventory = uow.Store<IInventoryLookup>();
        var selected = change.RecoverySelections ?? [];
        var asOf = snapshot.AsOf;
        // The allocation engine still sees the full demand queue. Its internal records are never emitted.
        var canInventory = principal.HasPermission(Permissions.InventoryRead);
        var needsStock = selected.Any(s => s.OptionId is "approved-alternate" or "existing-wip" or "remnant-transformation" or "expedited-material-movement");
        var positions = canInventory && needsStock ? sourcePositions : [];
        var locations = canInventory && needsStock ? sourceLocations : [];
        IReadOnlyList<LotInfo> lots = []; // Shelf-life-validated positions already carry the selected lot's usable state.
        var tasks = canInventory && needsStock ? await inventory.ListMaterialTasksAsync(true, null, ct) : [];
        IReadOnlyList<TransformationInfo> transformations = [];
        if (canInventory && selected.Any(s => s.OptionId == "remnant-transformation"))
        {
            var visibleOrders = orders.Select(o => o.OrderNumber).ToHashSet();
            transformations = (await inventory.ListTransformationsAsync(null, null, null, ct))
                .Where(t => visibleOrders.Contains(t.OrderNumber)).ToArray();
        }
        var alternates = selected.Any(s => s.OptionId == "approved-alternate") && principal.HasPermission(Permissions.EngineeringRead)
            ? await uow.Store<IAlternateLookup>().ListAlternatesAsync(ct) : [];
        sourceVisibility ??= await MaterialSourceVisibility.LoadAsync(uow, principal, ct, authorizer: new ForgeAuthorizer(new SnapshotTime(asOf)));
        var purchaseNumbers = selected.Where(s => s.OptionId == "expedited-procurement" && s.PurchaseOrder is not null).Select(s => s.PurchaseOrder!.Value).ToHashSet();
        var purchases = sourceVisibility.PurchaseOrders.Where(p => purchaseNumbers.Contains(p.PoNumber) && sourceVisibility.CanSeePurchaseOrder(p.PoNumber)).ToArray();
        var maintenance = selected.Any(s => s.OptionId == "maintenance-timing") && principal.HasPermission(Permissions.MaintenanceRead)
            ? await uow.Store<IMaintenanceLookup>().ListWorkOrdersAsync(false, ct) : [];
        var pointOfUse = new OrderPointOfUse(sourcePositions.Where(p => p.PointOfUseWorkCenter is not null).ToArray(),
            sourceLocations.Where(l => l.Active && l.PointOfUse).Select(l => l.WorkCenterCode!).ToHashSet(StringComparer.Ordinal));
        var sourceOrders = RecoveryOptionEvaluator.SourceOrders(change, snapshot, orders);
        var continuation = await WipContinuationFacts.LoadAsync(uow, sourceOrders, asOf, ct);
        var checks = new Dictionary<long, RecoveryOrderChecks>();
        var revisions = new Dictionary<string, IReadOnlyList<RevisionInfo>>(StringComparer.Ordinal);
        foreach (var order in sourceOrders)
        {
            if (!revisions.TryGetValue(order.PartNumber, out var partRevisions))
                revisions.Add(order.PartNumber, partRevisions = await uow.Store<IEngineeringLookup>().ListRevisionsAsync(order.PartNumber, ct));
            var revision = partRevisions.SingleOrDefault(r => r.Revision == order.Revision);
            var documentation = await DocumentationService.ForOrderAsync(uow, order, asOf, principal, ct);
            checks[order.OrderNumber] = new(revision, documentation, await ReadinessService.TrainingAsync(uow, documentation, ct), pointOfUse,
                order, continuation.GetValueOrDefault(order.OrderNumber));
        }

        var sourceChecks = new Dictionary<string, RecoverySourceCheck>(StringComparer.Ordinal);
        foreach (var selection in selected)
        {
            if (selection.OptionId == "expedited-material-movement" && canInventory
                && selection.PartNumber is { } movePart && selection.Quantity is { } moveQuantity
                && selection.FromLocation is { } moveFrom && selection.ToLocation is { } moveTo)
            {
                try
                {
                    await inventoryReadiness.RequireTransferAsync(uow,
                        new StockTransferReadinessRequest(movePart, moveQuantity, moveFrom, moveTo, selection.LotNumber, StockStatus.Released), ct);
                    sourceChecks[selection.OptionId] = new(true, [], ["The real TransferStock source preparation passed without moving ledger quantity."]);
                }
                catch (CommandRejectedException ex) { sourceChecks[selection.OptionId] = new(false, [$"{ex.Code}: {ex.Message}"], []); }
            }
            if ((selection.OptionId is "approved-alternate" or "existing-wip" or "remnant-transformation") && canInventory
                && selection.OrderNumber is { } issueOrder && orders.Any(o => o.OrderNumber == issueOrder)
                && selection.PartNumber is { } issuePart && selection.Quantity is { } issueQuantity && selection.FromLocation is { } issueLocation)
            {
                try
                {
                    await inventoryReadiness.RequireIssueAsync(uow,
                        new StockIssueReadinessRequest(issueOrder, issuePart, issueQuantity, issueLocation, selection.LotNumber,
                            selection.OptionId == "approved-alternate" ? selection.PrimaryPartNumber : null), principal, asOf, ct);
                    sourceChecks[selection.OptionId] = new(true, [], ["The real IssueToOrder source preparation passed without issuing stock or creating genealogy."]);
                }
                catch (CommandRejectedException ex) { sourceChecks[selection.OptionId] = new(false, [$"{ex.Code}: {ex.Message}"], []); }
            }
            if (selection.OptionId == "outside-processing" && canInventory && principal.HasPermission(Permissions.PurchasingRead)
                && selection.PartNumber is { } part && selection.LotNumber is { } lot && selection.Quantity is { } quantity
                && selection.FromLocation is { } from && selection.ToLocation is { } to && selection.PurchaseOrder is { } po && selection.PurchaseOrderLine is { } line)
            {
                try
                {
                    await inventoryReadiness.RequireOutsideDispatchAsync(uow,
                        new OutsideDispatchReadinessRequest(part, quantity, from, to, lot, po, line,
                            SpecificationDocument: selection.SpecificationDocument, SpecificationRevision: selection.SpecificationRevision,
                            ExpectedReturnDate: selection.ProposedDate), principal, asOf, ct);
                    sourceChecks[selection.OptionId] = new(true, [], [$"Source dispatch gate passed for {part}/{lot}, PO {po}/{line}, {from} → {to}. A return awaits inspection and its required processor certificate."]);
                }
                catch (CommandRejectedException ex) { sourceChecks[selection.OptionId] = new(false, [$"{ex.Code}: {ex.Message}"], []); }
            }
            if (selection.OptionId == "partial-shipment" && canInventory && principal.HasPermission(Permissions.QualityRead)
                && selection.ShipmentId is { } shipmentId)
            {
                var shipment = await inventory.FindShipmentAsync(shipmentId, ct);
                var demand = shipment?.DemandId is { } demandId ? await uow.Store<IDemandStore>().FindAsync(demandId, ct) : null;
                var visible = demand is not null && (demand.ProgramId is not { } program || principal.Programs.Contains(new ProgramId(program)));
                if (shipment is null || !visible)
                {
                    sourceChecks[selection.OptionId] = new(false, ["The selected shipment and delivery demand are unavailable in this actor's scope."], []);
                    continue;
                }
                var shipped = (await inventory.ListShippedByDemandAsync(ct)).Where(x => x.DemandId == demand!.DemandId && x.PartNumber == demand.PartNumber).Sum(x => x.Shipped);
                var partialQuantity = shipment.Lines.Where(x => x.PartNumber == demand!.PartNumber).Sum(x => x.Quantity);
                try
                {
                    if (demand!.Status != DemandStatus.Open || partialQuantity <= 0 || partialQuantity >= demand.Quantity - shipped)
                        throw new CommandRejectedException("not_partial_delivery", "The selected shipment must deliver a positive quantity below the open delivery demand's outstanding quantity.");
                    await inventoryReadiness.RequireShipmentReleaseAsync(uow, shipment, "person:" + principal.PersonId, asOf, ct);
                    sourceChecks[selection.OptionId] = new(true, [], [$"Packed shipment {shipmentId} can release {partialQuantity} {demand.PartNumber}; {demand.Quantity - shipped - partialQuantity} remains on demand {demand.DemandId}. Source stock, shelf life, packaging, NCR, holds and separation-of-duties gates passed."]);
                }
                catch (CommandRejectedException ex) { sourceChecks[selection.OptionId] = new(false, [$"{ex.Code}: {ex.Message}"], []); }
            }
        }
        return new(material, positions, locations, lots, tasks, transformations, alternates, purchases, maintenance, checks, sourceChecks,
            sourceVisibility);
    }

    private sealed class SnapshotTime(DateTimeOffset at) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => at;
    }
}

internal sealed record RecoveryOrderChecks(RevisionInfo? Revision, OrderDocumentation Documentation, OrderTraining Training, OrderPointOfUse PointOfUse,
    OrderInfo? Order = null, WipContinuationEvidence? Continuation = null);
internal sealed record RecoverySourceCheck(bool Passed, IReadOnlyList<string> Constraints, IReadOnlyList<string> Evidence);
