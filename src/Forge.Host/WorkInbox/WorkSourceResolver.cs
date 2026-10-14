using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Holds;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.WorkAssignments;
using Forge.Maintenance.Contracts;
using Forge.Planning;
using Forge.Procurement.Contracts;
using Forge.Projects;
using Forge.Projects.Contracts;
using Forge.Quality.Contracts;
using Forge.Quality.Qms;
using Forge.Host.History;
using Forge.Resources.Contracts;

namespace Forge.Host.WorkInbox;

/// <summary>Inbox context comes from the same owning records as the eventual domain command.</summary>
public sealed class WorkSourceResolver(TimeProvider time, IHoldTargetResolver holdTargets, IDocumentTargetResolver documentTargets,
    QmsOptions? qmsOptions = null, OwnedSourceAccess? ownedSources = null) : IWorkSourceResolver, ICommandSourceResolver, IOwnedSourceResolver
{
    public Task<bool> CanReadOwnedSourceAsync(IKernelUnitOfWork uow, ForgePrincipal principal, string type, string id, CancellationToken ct) =>
        ownedSources is null ? Task.FromResult(false) : ownedSources.CanReadAsync(uow, principal, type, id, ct);

    public async Task<CommandAuthority> ResolveAsync(IKernelUnitOfWork uow, CommandSpec spec, CommandContext context, DateTimeOffset now, CancellationToken ct)
    {
        var source = spec.Resource.Id is { } id ? await ResolveAsync(uow, spec.Resource.Type, id, ct) : null;
        if (source is null || source.Resources.Count == 0) return new CommandAuthority(spec.Resource);
        ResourceRef Merge(ResourceRef current) => current with
        {
            Type = spec.Resource.Type, Id = spec.Resource.Id,
            Program = current.Program ?? spec.Resource.Program,
            Markings = current.Markings | spec.Resource.Markings,
            FacilityId = current.FacilityId ?? spec.Resource.FacilityId,
            WorkCenterId = current.WorkCenterId ?? spec.Resource.WorkCenterId,
            ContractId = current.ContractId ?? spec.Resource.ContractId,
            CustomerId = current.CustomerId ?? spec.Resource.CustomerId,
            ShiftId = current.ShiftId ?? spec.Resource.ShiftId,
            Amount = spec.Resource.Amount ?? current.Amount,
            Currency = spec.Resource.Currency ?? current.Currency,
        };
        // A command can carry a prospective source (for example a new B requisition on an
        // existing A purchase order). Current records cannot replace that required scope.
        // A wholly generic spec adds no authority dimension and would incorrectly deny a
        // scoped caller after the actual record has supplied the missing context.
        var explicitContext = spec.Resource.Program is not null || spec.Resource.Markings != DataMarking.None
            || spec.Resource.FacilityId is not null || spec.Resource.WorkCenterId is not null
            || spec.Resource.ContractId is not null || spec.Resource.CustomerId is not null
            || spec.Resource.ShiftId is not null || spec.Resource.Amount is not null || spec.Resource.Currency is not null;
        var resources = source.Resources.Select(Merge)
            .Concat(explicitContext ? [spec.Resource] : Array.Empty<ResourceRef>()).Distinct().ToArray();
        return new CommandAuthority(resources[0], AdditionalResources: resources.Skip(1).ToArray());
    }

    public async Task<WorkSource?> ResolveAsync(IKernelUnitOfWork uow, string type, string id, CancellationToken ct)
    {
        if (OwnedSourceFamilies.Contains(type))
            return ownedSources is null ? null : await ownedSources.ResolveAsync(uow, type, id, ct);
        var number = long.TryParse(id.Split('/')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        switch (type)
        {
            case "QmsRecord":
            case "QmsRevision":
                var qmsSubject = QmsSourceAccess.Subject(type, id);
                if (qmsSubject is null || qmsOptions?.Configured != true) return null;
                var qmsRecord = await uow.Store<IQmsLookup>().FindAsync(qmsSubject.Value.Code, ct);
                if (qmsRecord is null || qmsRecord.OrganizationCode != qmsOptions.OrganizationCode
                    || (qmsSubject.Value.Revision is { } qmsRevision && !qmsRecord.Revisions.Any(row => row.Revision == qmsRevision))) return null;
                return new(Permissions.QmsRead, [QmsService.Resource(qmsRecord) with { Type = type, Id = id }]);
            case "Order":
            case "ProductionOrder":
            case "OrderOperation":
                var orderKey = id.Split('/');
                var sourceOrder = await uow.Store<IExecutionLookup>().FindOrderAsync(number, ct);
                if (sourceOrder is null) return null;
                int? operationSequence = orderKey.Length == 2 && int.TryParse(orderKey[1], NumberStyles.None, CultureInfo.InvariantCulture, out var orderSequence) ? orderSequence : null;
                return new(Permissions.OrdersRead, [await OrderResourceAsync(uow, type, id, number, operationSequence, ct)]);
            case "Ncr":
            case "NCR":
                var ncr = await uow.Store<IQualityLookup>().FindNcrAsync(number, ct);
                return ncr is null ? null : new(Permissions.QualityRead,
                    [await Forge.Quality.QualityService.ResourceForNcrAsync(uow, ncr, ct)], ncr.Status != NcrStatus.Closed, DecisionAvailable: ncr.Status == NcrStatus.AwaitingApproval);
            case "Capa":
            case "CAPA":
                var capa = await uow.Store<Forge.Quality.IQualityStore>().FindCapaAsync(number, ct);
                return capa is null ? null : new(Permissions.QualityRead,
                    [await Forge.Quality.QualityService.ResourceForCapaAsync(uow, capa, ct)], capa.Status != CapaStatus.Verified);
            case "Hold":
                var hold = (await uow.Store<IHoldLookup>().ListHoldsAsync(true, ct)).FirstOrDefault(row => row.HoldId == number);
                if (hold is null) return null;
                var target = await holdTargets.ResolveAsync(uow, hold.TargetType, hold.TargetId, hold.TargetPart, ct);
                if (target is null) return null;
                var heldSource = await ResolveAsync(uow, hold.TargetType.ToString(), hold.TargetId, ct);
                return new(Permissions.HoldsRead, heldSource?.Resources.Select(resource => resource with { Type = "Hold", Id = id }).ToArray()
                    ?? [new ResourceRef("Hold", id, Program(target.ProgramId))], hold.Active);
            case "Inspection":
                var inspection = await uow.Store<IQualityLookup>().FindInspectionAsync(number, ct);
                return inspection is null ? null : new(Permissions.QualityRead, [await OrderResourceAsync(uow, "Inspection", id, inspection.OrderNumber, inspection.Sequence, ct)]);
            case "PurchaseOrder":
                var po = await uow.Store<IProcurementLookup>().FindPurchaseOrderAsync(number, ct);
                return po is null ? null : new(Permissions.PurchasingRead, await Forge.Procurement.PurchasingService.ResourcesForPurchaseOrderAsync(uow, po, ct), po.Status == PurchaseOrderStatus.Draft);
            case "Project":
            case "ProjectReview":
                var project = await uow.Store<IProjectLookup>().FindProjectAsync(id, ct);
                if (project is not null) return Project(project.Budget, type, id, project.Status == ProjectStatus.Draft);
                if (type == "Project") return null;
                goto case "ProjectChange";
            case "ProjectChange":
                var change = await uow.Store<IProjectsStore>().FindChangeAsync(number, ct);
                var owner = change is null ? null : await uow.Store<IProjectLookup>().FindProjectAsync(change.ProjectCode, ct);
                return owner is null || change is null ? null : Project(change.Kind == ProjectChangeKind.Header ? change.Budget : owner.Budget, type, id, change.Status == ProjectChangeStatus.Pending);
            case "RFQ":
            case "RFQRelease":
            case "SourceSelection":
                var rfq = await uow.Store<ISourcingLookup>().FindRfqAsync(number, ct);
                var need = rfq is null ? null : await uow.Store<IRequisitionLookup>().FindRequisitionAsync(rfq.RequisitionId, ct);
                if (need is null || rfq is null) return null;
                if (type == "SourceSelection")
                {
                    var quoteKey = id.Split('/');
                    if (quoteKey.Length != 2 || !long.TryParse(quoteKey[1], NumberStyles.None, CultureInfo.InvariantCulture, out var quoteId)) return null;
                    var quote = (await uow.Store<ISourcingLookup>().ListQuotesAsync(rfq.RfqId, ct)).FirstOrDefault(row => row.QuoteId == quoteId);
                    var original = quote is null ? null : await uow.Store<IDocumentLookup>().FindDocumentAsync(quote.OriginalDocumentNumber, ct);
                    return quote is null || original is null ? null : new(Permissions.PurchasingRead,
                        [await Forge.Procurement.SourcingService.ResourceForRfqAsync(uow, rfq, quote, ct)],
                        rfq.Status == RfqStatus.Released && quote.ValidThrough >= DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime)
                            && quote.Quantity >= need.Quantity && (quote.ProposedAlternatePart is null || quote.ProposedAlternatePart == need.PartNumber));
                }
                return new(Permissions.PurchasingRead, [await Forge.Procurement.SourcingService.ResourceForRfqAsync(uow, rfq, null, ct)], rfq.Status is RfqStatus.Draft or RfqStatus.Released,
                    DecisionAvailable: type != "RFQRelease" || rfq.Status == RfqStatus.Draft);
            case "WorkOrder":
                var workOrder = await uow.Store<IMaintenanceLookup>().FindWorkOrderAsync(number, ct);
                return workOrder is null ? null : new(Permissions.MaintenanceRead, [await MachineResourceAsync(uow, "WorkOrder", id, workOrder.MachineCode, ct)],
                    workOrder.Status is WorkOrderStatus.Open or WorkOrderStatus.Scheduled or WorkOrderStatus.InProgress or WorkOrderStatus.Completed);
            case "Outage":
                var outage = await uow.Store<IMaintenanceLookup>().FindOutageAsync(number, ct);
                return outage is null ? null : new(Permissions.MaintenanceRead, [await MachineResourceAsync(uow, "Outage", id, outage.MachineCode, ct)], outage.Status == OutageStatus.Proposed);
            case "MaterialTask":
            case "MaterialRequest":
                var task = await uow.Store<IInventoryLookup>().FindMaterialTaskAsync(number, ct);
                if (task is null) return null;
                var destination = (await uow.Store<IInventoryLookup>().ListLocationsAsync(ct)).FirstOrDefault(location => location.Code == task.ToLocation);
                var taskResource = await OrderResourceAsync(uow, "MaterialTask", id, task.OrderNumber, null, ct);
                return new(Permissions.InventoryRead, [taskResource with { FacilityId = destination?.SiteCode, WorkCenterId = destination?.WorkCenterCode }],
                    task.Status is MaterialTaskStatus.Requested or MaterialTaskStatus.Allocated or MaterialTaskStatus.Picked);
            case "Schedule":
                var schedule = await uow.Store<IPlanningStore>().FindVersionAsync(number, true, ct);
                if (schedule is null) return null;
                var scheduleResources = (schedule.Result?.Operations ?? []).Select(operation => new ResourceRef("Schedule", id, Program(operation.ProgramId), FacilityId: operation.SiteCode, WorkCenterId: operation.WorkCenterCode)).Distinct().ToList();
                return new(Permissions.ScheduleRead, scheduleResources.Count == 0 ? [ResourceRef.Of("Schedule", id)] : scheduleResources, schedule.Status == ScheduleStatus.Proposed);
            case "Document":
            case "DocumentRevision":
                string[] split = type == "Document" ? [id] : id.Contains(" rev ", StringComparison.Ordinal) ? id.Split(" rev ", 2, StringSplitOptions.None) : id.Split('/', 2);
                var document = await uow.Store<IDocumentLookup>().FindDocumentAsync(split[0], ct);
                var documentRevision = split.Length == 2 ? document?.Revisions.FirstOrDefault(row => row.Revision == split[1]) : null;
                return document is null ? null : new(Permissions.EngineeringRead,
                    [new ResourceRef(type, id, Program(document.ProgramId), document.Markings)], type == "Document" || documentRevision?.Status == DocumentRevisionStatus.InReview);
            case "DocumentLink":
                var documents = uow.Store<IDocumentLookup>();
                if (long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var linkId))
                {
                    var link = await documents.FindLinkAsync(linkId, ct);
                    var linkedDocument = link is null ? null : await documents.FindDocumentAsync(link.DocumentNumber, ct);
                    if (link is null || linkedDocument is null) return null;
                    var linkedSource = await ResolveAsync(uow, link.TargetType.ToString(), link.TargetId, ct);
                    return new(linkedSource?.ReadPermission ?? DocumentAccess.ReadPermission(link.TargetType), linkedSource?.Resources.Select(resource => resource with
                    { Type = type, Id = id, Markings = resource.Markings | linkedDocument.Markings | link.TargetMarkings }).ToArray()
                        ?? [new ResourceRef(type, id, Program(link.ProgramId), linkedDocument.Markings | link.TargetMarkings)]);
                }
                var attachment = id.Split(':', 2);
                if (attachment.Length != 2 || !Enum.TryParse<DocumentTargetType>(attachment[0], out var attachmentType)) return null;
                var attachmentTarget = await documentTargets.ResolveAsync(uow, attachmentType, attachment[1], ct);
                if (attachmentTarget is null) return null;
                var attachmentSource = await ResolveAsync(uow, attachmentType.ToString(), attachment[1], ct);
                return new(attachmentTarget.ReadPermission, attachmentSource?.Resources.Select(resource => resource with
                    { Type = type, Id = id, Markings = resource.Markings | attachmentTarget.Markings }).ToArray()
                    ?? [new ResourceRef(type, id, Program(attachmentTarget.ProgramId), attachmentTarget.Markings)]);
            case "EngineeringReview":
            case "EngineeringChange":
                var changeNumber = type == "EngineeringReview" ? id.Split('/')[0] : id;
                var engineering = await uow.Store<IChangeLookup>().FindChangeAsync(changeNumber, ct);
                if (engineering is null) return null;
                var engineeringResources = new List<ResourceRef> { new("EngineeringChange", changeNumber, Program(engineering.ProgramId), CustomerId: engineering.Customer) };
                var revisionAuthors = new HashSet<string>(StringComparer.Ordinal) { engineering.Originator };
                foreach (var line in engineering.ActiveLines)
                {
                    if (line.Kind == ChangeLineKind.DocumentRevision)
                    {
                        var releasedDocument = await uow.Store<IDocumentLookup>().FindDocumentAsync(line.DocumentNumber!, ct);
                        if (releasedDocument is null) return null;
                        engineeringResources.Add(new ResourceRef("EngineeringChange", changeNumber, Program(releasedDocument.ProgramId), releasedDocument.Markings, CustomerId: engineering.Customer));
                        if (releasedDocument.Revisions.FirstOrDefault(row => row.Revision == line.ToRevision) is { } authoredRevision) revisionAuthors.Add(authoredRevision.Author);
                    }
                    else revisionAuthors.UnionWith(await uow.Store<Forge.Engineering.IEngineeringStore>().GetContributorsAsync(line.PartNumber!, line.ToRevision, ct));
                }
                var reviewKey = id.Split('/');
                var reviewPending = type == "EngineeringReview" && reviewKey.Length == 3 && int.TryParse(reviewKey[1], NumberStyles.None, CultureInfo.InvariantCulture, out var round)
                    && round == engineering.ReviewRound && engineering.CurrentReviews.Any(row => row.Kind.ToString() == reviewKey[2] && row.Decision is null);
                var active = engineering.Status == ChangeStatus.InReview && (type == "EngineeringReview" ? reviewPending : engineering.CurrentReviews.All(row => row.Decision is not null));
                return new(Permissions.EngineeringRead, engineeringResources, active, type == "EngineeringChange" ? revisionAuthors.ToList() : [engineering.Originator]);
            case "ManufacturerPart":
                var manufacturerKey = id.Split('/', 3);
                if (manufacturerKey.Length != 3) return null;
                var manufacturerPart = (await uow.Store<IEngineeringLookup>().ListManufacturerPartsAsync(manufacturerKey[0], ct))
                    .FirstOrDefault(row => row.Manufacturer == manufacturerKey[1] && row.Mpn == manufacturerKey[2]);
                return manufacturerPart is null ? null : new(Permissions.EngineeringRead, [ResourceRef.Of(type, id)], manufacturerPart.Status == ManufacturerPartStatus.Proposed);
            case "Alternate":
                var alternate = await uow.Store<IAlternateLookup>().FindAlternateAsync(number, ct);
                if (alternate is null) return null;
                var alternateMachine = await MachineResourceAsync(uow, "Alternate", id, alternate.MachineCode, ct);
                return new(Permissions.EngineeringRead, [alternateMachine with { Program = Program(alternate.ProgramId), CustomerId = alternate.Customer }],
                    alternate.Status == AlternateStatus.Proposed && (alternate.ValidTo is null || alternate.ValidTo >= DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime)));
            case "SafetyRule":
                var ruleId = id.Split('/');
                if (ruleId.Length != 2 || !int.TryParse(ruleId[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ruleVersion)) return null;
                var rule = (await uow.Store<Forge.SafetyRules.ISafetyRuleStore>().VersionsAsync(ruleId[0], ct)).FirstOrDefault(row => row.Version == ruleVersion);
                return rule is null ? null : new(Permissions.SafetyRulesRead, [ResourceRef.Of("SafetyRule", id)], rule.Status == Forge.SafetyRules.Contracts.RuleStatus.Draft);
            case "ShelfLifeExtension":
                var extension = (await uow.Store<IShelfLifeLookup>().ListExtensionsAsync(null, ct)).FirstOrDefault(row => row.ExtensionId == number);
                return extension is null ? null : new(Permissions.InventoryRead, [ResourceRef.Of("ShelfLifeExtension", id)], extension.Approved is null);
            case "SupplierUseAsIs":
                var rejection = (await uow.Store<IReceivingLookup>().ListRejectionsAsync(false, ct)).FirstOrDefault(row => row.RejectionId == number);
                if (rejection is null) return null;
                var rejectedInspection = (await uow.Store<IReceivingLookup>().ListReceivingInspectionsAsync(null, ct)).FirstOrDefault(row => row.InspectionId == rejection.InspectionId);
                var receipt = rejectedInspection is null ? null : await uow.Store<IReceivingLookup>().FindTransactionAsync(rejectedInspection.ReceiptTransactionId, ct);
                var rejectionResource = await OrderResourceAsync(uow, "SupplierRejection", id, receipt?.OrderNumber, null, ct);
                return new(Permissions.QualityRead, [rejectionResource], rejection.Disposition == SupplierDisposition.UseAsIs && rejection.ApprovedBy is null);
            case "Adjustment":
                var adjustment = (await uow.Store<IInventoryLookup>().ListRequestsAsync(false, ct)).FirstOrDefault(row => row.RequestId == number);
                var location = adjustment is null ? null : (await uow.Store<IInventoryLookup>().ListLocationsAsync(ct)).FirstOrDefault(row => row.Code == adjustment.LocationCode);
                return adjustment is null ? null : new(Permissions.InventoryRead, [new ResourceRef("AdjustmentRequest", id, FacilityId: location?.SiteCode)], adjustment.State == AdjustmentState.Pending);
            case "RootCause":
                var rootCause = (await uow.Store<IProductionActualsLookup>().ListRootCausesAsync(null, ct)).FirstOrDefault(row => row.RootCauseId == number);
                var stoppage = rootCause is null ? null : (await uow.Store<IProductionActualsLookup>().ListStoppagesAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, false, ct)).FirstOrDefault(row => row.StoppageId == rootCause.StoppageId);
                if (rootCause is null || stoppage is null) return null;
                var rootResource = await OrderResourceAsync(uow, "RootCause", id, stoppage.OrderNumber, stoppage.Sequence, ct);
                var stoppedMachine = await MachineResourceAsync(uow, "RootCause", id, stoppage.MachineCode, ct);
                return new(Permissions.ShopFloorRead, [rootResource with { WorkCenterId = stoppage.WorkCenterCode, FacilityId = stoppedMachine.FacilityId }], rootCause.ConfirmedAt is null);
            case "Delegation":
                if (!Guid.TryParse(id, out var delegationId)) return null;
                var delegation = await uow.Store<Forge.Kernel.Access.IDelegationStore>().FindAsync(delegationId, ct);
                return delegation is null ? null : new(Permissions.AccessManage, [Forge.Kernel.Access.DelegationAuthority.Resource(delegation.Scopes, id)],
                    delegation.Status == Forge.Kernel.Access.DelegationStatus.PendingApproval && delegation.ExpiresAt > time.GetUtcNow());
            case "Machine":
                return await uow.Store<IResourcesLookup>().FindMachineAsync(id, ct) is null ? null
                    : new(Permissions.ResourcesRead, [await MachineResourceAsync(uow, type, id, id, ct)]);
            case "WorkCenter":
                var sourceCenter = await uow.Store<IResourcesLookup>().FindWorkCenterAsync(id, ct);
                var sourceArea = sourceCenter is null ? null : await uow.Store<IResourcesLookup>().FindSafetyAreaAsync(sourceCenter.SafetyAreaCode, ct);
                return sourceCenter is null ? null : new(Permissions.ResourcesRead, [new ResourceRef(type, id, FacilityId: sourceArea?.SiteCode, WorkCenterId: sourceCenter.Code)]);
            default:
                return null;
        }
    }

    private static WorkSource Project(decimal? budget, string type, string id, bool active) =>
        new(Permissions.ProjectsRead, [new ResourceRef(type, id, Amount: budget, Currency: "USD")], active);
    private static ProgramId? Program(Guid? value) => value is { } id ? new ProgramId(id) : null;
    private static async Task<ResourceRef> OrderResourceAsync(IKernelUnitOfWork uow, string type, string id, long? orderNumber, int? sequence, CancellationToken ct)
    {
        var order = orderNumber is { } number ? await uow.Store<IExecutionLookup>().FindOrderAsync(number, ct) : null;
        var operation = sequence is { } seq ? order?.Operations.FirstOrDefault(row => row.Sequence == seq) : null;
        var center = operation is null ? null : await uow.Store<IResourcesLookup>().FindWorkCenterAsync(operation.WorkCenterCode, ct);
        var area = center is null ? null : await uow.Store<IResourcesLookup>().FindSafetyAreaAsync(center.SafetyAreaCode, ct);
        return new ResourceRef(type, id, Program(order?.ProgramId), FacilityId: area?.SiteCode, WorkCenterId: operation?.WorkCenterCode, ContractId: order?.ContractId?.ToString(CultureInfo.InvariantCulture));
    }
    private static async Task<ResourceRef> MachineResourceAsync(IKernelUnitOfWork uow, string type, string id, string? code, CancellationToken ct)
    {
        var machine = code is null ? null : await uow.Store<IResourcesLookup>().FindMachineAsync(code, ct);
        var center = machine is null ? null : await uow.Store<IResourcesLookup>().FindWorkCenterAsync(machine.WorkCenterCode, ct);
        var area = center is null ? null : await uow.Store<IResourcesLookup>().FindSafetyAreaAsync(center.SafetyAreaCode, ct);
        return new ResourceRef(type, id, FacilityId: area?.SiteCode, WorkCenterId: center?.Code);
    }
}
