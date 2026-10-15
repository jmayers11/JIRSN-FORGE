using System.Globalization;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Security;
using Forge.Kernel.WorkAssignments;
using Forge.Quality.Acceptance;
using Forge.Quality.Contracts;

namespace Forge.Host.WorkInbox;

/// <summary>Actual Quality owners supply business authority; this lazy bridge only projects scope and rechecks reads.</summary>
public sealed class AcceptanceSourceAccess(IServiceProvider services)
{
    public static bool Contains(string type) => type is "AcceptanceSubject" or "AcceptanceCriteriaRevision" or "AcceptanceInspection" or "ProductReleaseDecision" or "ReturnInspectionCohort";
    public async Task<bool> CanReadAsync(IKernelUnitOfWork uow, ForgePrincipal principal, string type, string id, CancellationToken ct)
    {
        try
        {
            if (type == "ReturnInspectionCohort")
                return long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var cohortId) && cohortId > 0
                    && await services.GetRequiredService<ReturnCohortReadService>().CanReadAsync(uow, principal, cohortId, ct);
            var source = await ResolveAsync(uow, type, id, ct);
            if (source is null || source.Resources.Count == 0) return false;
            var authorizer = services.GetRequiredService<IForgeAuthorizer>();
            if (source.Resources.Any(resource => !authorizer.Authorize(principal, Permissions.QualityRead, resource).IsPermitted)) return false;
            if (type == "AcceptanceCriteriaRevision")
            {
                var key = CriteriaKey(id); if (key is null) return false;
                var criteria = await uow.Store<IAcceptanceLookup>().FindCriteriaAsync(key.Value.Code, key.Value.Revision, ct);
                return criteria is not null && await services.GetRequiredService<AcceptanceCriteriaService>().CanReadAsync(uow, principal, criteria, ct);
            }
            if (type == "AcceptanceSubject")
            {
                if (!AcceptanceSubjectReference.TryParse(id, out var key) || key is null) return false;
                await services.GetRequiredService<IInspectionSubjectResolver>().ResolveAsync(uow, principal, key, false, ct);
                foreach (var decision in await uow.Store<IAcceptanceLookup>().DecisionsForAsync(key, ct))
                    if (!await services.GetRequiredService<ProductAcceptanceService>().CanReadAsync(uow, principal, decision.DecisionId, true, ct)) return false;
                foreach (var inspection in await uow.Store<IAcceptanceLookup>().InspectionsForAsync(key, ct))
                    if (!await services.GetRequiredService<ProductAcceptanceService>().CanReadAsync(uow, principal, inspection.InspectionId, false, ct)) return false;
                return true;
            }
            return long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
                && await services.GetRequiredService<ProductAcceptanceService>().CanReadAsync(uow, principal, number, type == "ProductReleaseDecision", ct);
        }
        catch (ForgeAuthorizationException) { return false; }
        catch (CommandRejectedException) { return false; }
    }
    public async Task<WorkSource?> ResolveAsync(IKernelUnitOfWork uow, string type, string id, CancellationToken ct)
    {
        if (!Contains(type)) return null;
        if (type == "ReturnInspectionCohort")
        {
            if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var cohortId) || cohortId <= 0) return null;
            var cohort = await uow.Store<IReturnAcceptanceLookup>().FindCohortAsync(cohortId, ct); if (cohort is null) return null;
            var rma = await uow.Store<IReceivingLookup>().FindCustomerReturnAsync(cohort.RmaId, ct); if (rma is null) return null;
            var location = await uow.Store<IInventoryLookup>().FindLocationAsync(rma.LocationCode, ct); if (location is null) return null;
            var cohortResources = (await KeyResourcesAsync(uow, cohort.Subject, type, id, ct)).ToList();
            cohortResources.AddRange(await services.GetRequiredService<IReturnAcceptanceSourceResolver>().ResourceContextsAsync(uow, cohort.RmaId, location.SiteCode, ct));
            cohortResources.AddRange(cohort.Sources.Select(source => source.Resource with { Type = type, Id = id }));
            return cohortResources.Count == 0 ? null : new(Permissions.QualityRead, cohortResources.Distinct().ToArray());
        }
        if (type == "AcceptanceCriteriaRevision")
        {
            var key = CriteriaKey(id); if (key is null) return null;
            var criteria = await uow.Store<IAcceptanceLookup>().FindCriteriaAsync(key.Value.Code, key.Value.Revision, ct);
            return criteria is null ? null : new(Permissions.QualityRead, [AcceptanceCriteriaService.Resource(criteria)],
                criteria.State is not (AcceptanceCriteriaState.Withdrawn or AcceptanceCriteriaState.Superseded));
        }
        AcceptanceSubjectKey subject; InspectionSubject? recorded = null; bool active = true;
        if (type == "AcceptanceSubject")
        {
            if (!AcceptanceSubjectReference.TryParse(id, out var key) || key is null) return null;
            subject = key;
        }
        else
        {
            if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0) return null;
            if (type == "AcceptanceInspection")
            {
                var inspection = await uow.Store<IAcceptanceLookup>().FindInspectionAsync(number, ct);
                if (inspection is null) return null; recorded = inspection.Subject; subject = recorded.Key;
            }
            else
            {
                var decision = await uow.Store<IAcceptanceLookup>().FindDecisionAsync(number, ct);
                if (decision is null) return null; subject = decision.Subject;
                active = await uow.Store<IAcceptanceLookup>().FindWithdrawalAsync(number, ct) is null;
                var inspections = new List<AcceptanceInspection>();
                foreach (var inspectionId in decision.InspectionIds)
                {
                    var inspection = await uow.Store<IAcceptanceLookup>().FindInspectionAsync(inspectionId, ct);
                    if (inspection is null) return null; inspections.Add(inspection);
                }
                recorded = inspections.FirstOrDefault()?.Subject;
            }
        }
        var resources = (await KeyResourcesAsync(uow, subject, type, id, ct)).ToList();
        if (recorded is not null)
            resources.AddRange(await services.GetRequiredService<IInspectionSubjectResolver>().ResourceContextsAsync(uow, recorded, type,
                long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var recordId) ? recordId : null, ct));
        return resources.Count == 0 ? null : new(Permissions.QualityRead, resources.Distinct().ToArray(), active);
    }
    /// <summary>Scope derives from uncapped actual physical ancestors and owner source records, with no fabricated system principal.</summary>
    private async Task<IReadOnlyList<ResourceRef>> KeyResourcesAsync(IKernelUnitOfWork uow, AcceptanceSubjectKey subject, string type, string id, CancellationToken ct)
    {
        var lookup = uow.Store<IInventoryLookup>();
        if (await lookup.FindLotAsync(subject.PartNumber, subject.LotNumber, ct) is null) return [];
        var location = await lookup.FindLocationAsync(subject.LocationCode, ct); if (location is null) return [];
        var ancestors = new HashSet<(string Part, string Lot)>(); var queue = new Queue<(string Part, string Lot)>([(subject.PartNumber, subject.LotNumber)]);
        var links = await lookup.ListInstanceLinksAsync(null, null, ct);
        while (queue.TryDequeue(out var point))
            if (ancestors.Add(point)) foreach (var link in links.Where(link => link.ResultPart == point.Part && link.ResultLot == point.Lot)) queue.Enqueue((link.SourcePart, link.SourceLot));
        var origins = (await lookup.ListGenealogyTransactionsAsync(ct)).Where(transaction => transaction.LotNumber is { } lot
            && ancestors.Contains((transaction.PartNumber, lot)) && transaction.Kind is InventoryTransactionKind.Receipt or InventoryTransactionKind.ProductionReceipt
                or InventoryTransactionKind.Remnant or InventoryTransactionKind.Byproduct or InventoryTransactionKind.CustomerReturn).ToArray();
        if (origins.Length == 0) return [];
        var resources = new List<ResourceRef>(); var work = services.GetRequiredService<IWorkSourceResolver>();
        foreach (var origin in origins)
        {
            var ownerType = origin.OrderNumber is not null ? "Order" : origin.PoNumber is not null ? "PurchaseOrder" : null;
            var ownerId = origin.OrderNumber ?? origin.PoNumber; var workCenter = location.WorkCenterCode;
            if (origin.OrderNumber is { } orderNumber)
            {
                var order = await uow.Store<IExecutionLookup>().FindOrderAsync(orderNumber, ct); if (order is null) return [];
                workCenter = order.Operations.OrderBy(operation => operation.Sequence).LastOrDefault()?.WorkCenterCode ?? workCenter;
            }
            if (ownerType is null) { resources.Add(new(type, id, FacilityId: location.SiteCode, WorkCenterId: workCenter)); continue; }
            var source = await work.ResolveAsync(uow, ownerType, ownerId!.Value.ToString(CultureInfo.InvariantCulture), ct);
            if (source is null || source.Resources.Count == 0) return [];
            resources.AddRange(source.Resources.Select(resource => resource with { Type = type, Id = id, FacilityId = location.SiteCode, WorkCenterId = workCenter ?? resource.WorkCenterId }));
        }
        return resources.Distinct().ToArray();
    }
    private static (string Code, int Revision)? CriteriaKey(string id)
    {
        var separator = id.LastIndexOf('/');
        return separator > 0 && int.TryParse(id.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var revision) && revision > 0
            ? (id[..separator], revision) : null;
    }
}
