using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Quality.Contracts;
using Forge.Resources.Contracts;

namespace Forge.Host.Identification;

public sealed partial class IdentificationResolver
{
    public async Task<IdentificationSubject> ResolveSubjectAsync(IKernelUnitOfWork uow, ForgePrincipal principal, string? code, CancellationToken ct)
    {
        var parsed = CanonicalIdentification.Parse(code, configuration[CanonicalIdentification.NamespaceConfigurationKey]);
        try
        {
            return await ResolveAuthorityPresentationAsync(uow, principal, parsed.Code, ct);
        }
        catch (ForgeAuthorizationException) { throw NotFound(); }
        catch (CommandRejectedException) { throw NotFound(); }
    }

    public async Task<IdentificationSubject> ResolveAuthorityPresentationAsync(IKernelUnitOfWork uow, ForgePrincipal principal, string? code, CancellationToken ct)
    {
        CanonicalIdentification parsed;
        try { parsed = CanonicalIdentification.Parse(code, configuration[CanonicalIdentification.NamespaceConfigurationKey]); }
        catch (IdentificationException error) { throw new CommandRejectedException(error.Code, error.Message); }
        if(parsed.Kind is "label" or "alias")return await ResolvePresentationAsync(uow, principal, parsed, ct);
        var subject=await ResolveAuthoritySubjectAsync(uow,principal,parsed.Code,ct);
        await RequireActivePrimitiveAsync(uow,subject,ct);
        return subject;
    }

    public async Task RequireActivePrimitiveAsync(IKernelUnitOfWork uow, IdentificationSubject subject, CancellationToken ct)
    {
        if((subject.PresentedKind ?? subject.Display.Kind)!="instruction")return;
        var document=await uow.Store<IDocumentLookup>().FindDocumentAsync(subject.Display.SubjectKey!,ct) ?? throw MissingSubject();
        var revision=document.Current;
        var at=(time ?? throw new CommandRejectedException("identification_clock_unavailable","Instruction staging requires the configured host clock.")).GetUtcNow();
        var day=(await uow.SiteDateAsync(at, ct));
        if(document.Authority==DocumentAuthority.Reference || revision is null || revision.Revision!=subject.Display.Revision
            || revision.Status is not(DocumentRevisionStatus.Released or DocumentRevisionStatus.Applicable)
            || revision.EffectiveFrom is null || revision.EffectiveFrom>day || revision.EffectiveTo is{} end && end<=day
            || revision.FileSha256 is null || await uow.Store<IDocumentLookup>().FindFileAsync(revision.FileSha256,ct) is null)
            throw new CommandRejectedException("instruction_not_current","Select the exact current controlled instruction revision and retained file.");
    }

    /// <summary>Consequential consumers retain the actual owning denied resource for audit, instead of HTTP read concealment.</summary>
    public async Task<IdentificationSubject> ResolveAuthoritySubjectAsync(IKernelUnitOfWork uow, ForgePrincipal principal, string? code, CancellationToken ct)
    {
        IdentificationSubject source;
        try { source = await ResolveSubjectFactsAsync(uow, code, ct); }
        catch (IdentificationException error) { throw new CommandRejectedException(error.Code, error.Message); }
        var current = await uow.People.LoadPrincipalAsync(principal.PersonId, ct);
        if (current is null) throw new ForgeAuthorizationException(new(false, ["The current manufacturing person is inactive or unavailable."]), source.Resources[0]);
        PermitSource(current, source.ReadPermission, source.Resources);
        if (Forge.Host.WorkInbox.OwnedSourceFamilies.Contains(source.SourceType)
            && !await Forge.Host.WorkInbox.OwnedSourceFamilies.CanReadAsync(uow, sources, current, source.SourceType, source.SourceId, ct))
            throw new ForgeAuthorizationException(new(false, ["The actual captured and current owner sources are inaccessible."]), source.Resources[0]);
        return source;
    }

    /// <summary>Same-UOW primitive lookup. This method grants no access and never resolves through a label/alias.</summary>
    public async Task<IdentificationSubject> ResolveSubjectFactsAsync(IKernelUnitOfWork uow, string? code, CancellationToken ct)
    {
        var parsed = CanonicalIdentification.Parse(code, configuration[CanonicalIdentification.NamespaceConfigurationKey]);
        var prefix = "forge:v1:" + parsed.Namespace + ":";
        IdentificationSubject Subject(ResolvedIdentification view, string canonical, string type, string id, string permission, IReadOnlyList<ResourceRef> resources,
            string? activity = null, string? machine = null) => new(view, canonical, type, id, permission, resources,
                PresentationCode: parsed.Code, PresentedKind: parsed.Kind, ActivityCode: activity, MachineCode: machine);

        if (parsed.Kind is "label" or "alias")
            throw new CommandRejectedException("primitive_subject_required", "A presentation mapping must reference an existing primitive subject directly.");
        if (parsed.Kind == "instruction")
        {
            var document = await uow.Store<IDocumentLookup>().FindDocumentAsync(parsed.Key!, ct) ?? throw MissingSubject();
            var revision = document.Revisions.FirstOrDefault(row => row.Revision == parsed.Revision) ?? throw MissingSubject();
            var resource = new ResourceRef("DocumentRevision", document.Number + "/" + revision.Revision,
                document.ProgramId is { } program ? new ProgramId(program) : null, document.Markings);
            // The exact owner status is exposed; the execution document gate determines required applicability/training.
            // References remain attachment identities and cannot become an instruction presentation.
            if (document.Authority == DocumentAuthority.Reference) throw MissingSubject();
            return Subject(new(parsed.Code, parsed.Namespace, parsed.Kind, null, null, null, revision.Revision, revision.Status.ToString(),
                SubjectKey: document.Number, SourceFingerprint: CanonicalJson.Fingerprint(new { document.Number, document.Authority, revision.Revision, revision.FileSha256 })),
                parsed.Code, "DocumentRevision", resource.Id!, Permissions.EngineeringRead, [resource]);
        }
        if (parsed.Kind is "order" or "operation" or "traveler" or "wip")
        {
            var order = await uow.Store<IExecutionLookup>().FindOrderAsync(parsed.OrderNumber!.Value, ct) ?? throw MissingSubject();
            var operation = parsed.Sequence is { } sequence ? order.Operations.FirstOrDefault(row => row.Sequence == sequence) : null;
            if (parsed.Sequence is not null && operation is null) throw MissingSubject();
            var type = operation is null ? "Order" : "OrderOperation";
            var id = Number(order.OrderNumber) + (operation is null ? "" : "/" + Number(operation.Sequence));
            var source = await sources.ResolveAsync(uow, type, id, ct);
            if (source is null || source.Resources.Count == 0) throw MissingSubject();
            // Travelers and WIP work references present the recorded order/routing; they create no additional physical unit identity.
            var canonical = prefix + (operation is null ? "order:" + Number(order.OrderNumber) : "operation:" + Number(order.OrderNumber) + ":" + Number(operation.Sequence));
            var fingerprint = parsed.Kind == "traveler" ? CanonicalJson.Fingerprint(new { order.OrderNumber, order.PartNumber, order.Revision, order.ReleasedAt,
                Routing = order.Operations.Select(row => new { row.Sequence, row.Description, row.WorkCenterCode, row.ActivityCode, row.EligibleMachines, row.RequiredQualifications, row.PlannedMinutes, row.CrewSize }), order.Materials }) : null;
            return Subject(new(parsed.Code, parsed.Namespace, parsed.Kind, order.OrderNumber, parsed.Sequence, order.PartNumber, order.Revision,
                operation?.Status.ToString() ?? order.Status.ToString(), SourceFingerprint: fingerprint), canonical, type, id, source.ReadPermission, source.Resources, operation?.ActivityCode);
        }
        if (parsed.Kind is "material" or "batch" or "roll" or "serial" or "remnant")
        {
            var inventory = uow.Store<IInventoryLookup>();
            var lot = await inventory.FindLotAsync(parsed.PartNumber!, parsed.LotNumber!, ct) ?? throw MissingSubject();
            if (parsed.Kind == "batch" && lot.IdentityKind != IdentityKind.Batch
                || parsed.Kind == "roll" && lot.IdentityKind != IdentityKind.Roll
                || parsed.Kind == "serial" && lot.IdentityKind != IdentityKind.Serial
                || parsed.Kind == "remnant" && !await inventory.IsRecordedRemnantAsync(lot.PartNumber, lot.LotNumber, ct)) throw MissingSubject();
            var tuple = CanonicalIdentification.EncodeKey(lot.PartNumber) + ":" + CanonicalIdentification.EncodeKey(lot.LotNumber);
            return Subject(new(parsed.Code, parsed.Namespace, parsed.Kind, null, null, lot.PartNumber, lot.PartRevision, "Recorded",
                LotNumber: lot.LotNumber, IdentityKind: lot.IdentityKind.ToString()), prefix + "material:" + tuple, "Lot", tuple,
                Permissions.InventoryRead, [new("Lot", tuple)]);
        }
        if (parsed.Kind == "location")
        {
            var location = await uow.Store<IInventoryLookup>().FindLocationAsync(parsed.LocationCode!, ct) ?? throw MissingSubject();
            var resource = await LocationResourceAsync(uow, location, ct);
            return Subject(new(parsed.Code, parsed.Namespace, parsed.Kind, null, null, null, null, location.Active ? "Active" : "Inactive",
                LocationCode: location.Code), parsed.Code, "Location", location.Code, Permissions.InventoryRead, [resource]);
        }
        if (parsed.Kind is "machine" or "workcenter")
        {
            var lookup = uow.Store<IResourcesLookup>();
            var machine = parsed.Kind == "machine" ? await lookup.FindRecordedMachineAsync(parsed.Key!, ct) ?? throw MissingSubject() : null;
            var center = await lookup.FindRecordedWorkCenterAsync(machine?.WorkCenterCode ?? parsed.Key!, ct) ?? throw MissingSubject();
            var area = await lookup.FindRecordedSafetyAreaAsync(machine?.SafetyAreaCode ?? center.SafetyAreaCode, ct) ?? throw MissingSubject();
            var type = machine is null ? "WorkCenter" : "Machine";
            var key = machine?.Code ?? center.Code;
            return Subject(new(parsed.Code, parsed.Namespace, parsed.Kind, null, null, null, null,
                (machine?.Active ?? center.Active) ? "Active" : "Inactive", SubjectKey: key, DisplayName: machine?.Name ?? center.Name), parsed.Code, type, key,
                Permissions.ResourcesRead, [new(type, key, FacilityId: area.SiteCode, WorkCenterId: center.Code)], machine: machine?.Code);
        }
        if (parsed.Kind is "tool" or "gauge")
        {
            var lookup = uow.Store<IToolLookup>();
            var tool = await lookup.FindToolAsync(parsed.Key!, ct) ?? throw MissingSubject();
            var type = await lookup.FindToolTypeAsync(tool.TypeCode, ct) ?? throw MissingSubject();
            if (parsed.Kind == "gauge" && type.Kind != ToolKind.Gauge) throw MissingSubject();
            // Type/gauge variants share one actual tool identity for copy limits.
            return Subject(new(parsed.Code, parsed.Namespace, parsed.Kind, null, null, null, null, tool.Status.ToString(),
                SubjectKey: tool.Code, IdentityKind: type.Kind.ToString()), prefix + "tool:" + CanonicalIdentification.EncodeKey(tool.Code), "Tool", tool.Code,
                Permissions.ResourcesRead, [new("Tool", tool.Code)]);
        }
        if (parsed.Kind == "person")
        {
            var person = await uow.People.FindAsync(new PersonId(parsed.Id!.Value), ct);
            if (person is null || !person.IsActive) throw MissingSubject();
            // Person.Markings is a clearance. This minimal directory presentation is not classified by that clearance.
            return Subject(new(parsed.Code, parsed.Namespace, parsed.Kind, null, null, null, null, "Active",
                SubjectKey: person.Id.ToString(), DisplayName: person.DisplayName), parsed.Code, "Person", person.Id.ToString(),
                Permissions.IdentificationRead, [new("Person", person.Id.ToString())]);
        }
        if (parsed.Kind == "inspection" && parsed.InspectionNamespace == "acceptance")
        {
            var inspection = await uow.Store<IAcceptanceLookup>().FindInspectionAsync(parsed.RecordNumber!.Value, ct) ?? throw MissingSubject();
            var owner = await sources.ResolveAsync(uow, "AcceptanceInspection", Number(inspection.InspectionId), ct) ?? throw MissingSubject();
            return Subject(new(parsed.Code, parsed.Namespace, parsed.Kind, null, null, inspection.Subject.Key.PartNumber,
                inspection.Subject.Revision, inspection.Passed ? "Pass" : "Fail", LotNumber: inspection.Subject.Key.LotNumber,
                LocationCode: inspection.Subject.Key.LocationCode, IdentityKind: "AcceptanceInspection", RecordNumber: inspection.InspectionId,
                SourceFingerprint: CanonicalJson.Fingerprint(inspection)), parsed.Code, "AcceptanceInspection", Number(inspection.InspectionId), Permissions.QualityRead, owner.Resources);
        }
        if (parsed.Kind == "product-release")
        {
            var decision = await uow.Store<IAcceptanceLookup>().FindDecisionAsync(parsed.RecordNumber!.Value, ct) ?? throw MissingSubject();
            var withdrawal = await uow.Store<IAcceptanceLookup>().FindWithdrawalAsync(decision.DecisionId, ct);
            var owner = await sources.ResolveAsync(uow, "ProductReleaseDecision", Number(decision.DecisionId), ct) ?? throw MissingSubject();
            return Subject(new(parsed.Code, parsed.Namespace, parsed.Kind, null, null, decision.Subject.PartNumber, decision.Revision,
                withdrawal is null ? "Approved" : "Withdrawn", LotNumber: decision.Subject.LotNumber, LocationCode: decision.Subject.LocationCode,
                IdentityKind: "ProductReleaseDecision", RecordNumber: decision.DecisionId, SourceFingerprint: CanonicalJson.Fingerprint(new { decision, withdrawal })),
                parsed.Code, "ProductReleaseDecision", Number(decision.DecisionId), Permissions.QualityRead, owner.Resources);
        }
        if (parsed.Kind == "inspection")
        {
            var inspection = await uow.Store<IQualityLookup>().FindInspectionAsync(parsed.RecordNumber!.Value, ct) ?? throw MissingSubject();
            var owner = await ResolveSubjectFactsAsync(uow, prefix + "operation:" + Number(inspection.OrderNumber) + ":" + Number(inspection.Sequence), ct);
            var resources = owner.Resources.Select(resource => resource with { Type = "Inspection", Id = Number(inspection.InspectionId) }).ToArray();
            return Subject(new(parsed.Code, parsed.Namespace, parsed.Kind, inspection.OrderNumber, inspection.Sequence, inspection.PartNumber,
                owner.Display.Revision, inspection.Result.ToString(), RecordNumber: inspection.InspectionId), parsed.Code, "Inspection", Number(inspection.InspectionId),
                Permissions.QualityRead, resources, owner.ActivityCode);
        }
        if (parsed.Kind is "container" or "package" or "shipment")
        {
            var inventory = uow.Store<IInventoryLookup>();
            ContainerInfo? container = null; ShipmentInfo? shipment = null;
            if (parsed.Kind == "shipment") shipment = await inventory.FindShipmentAsync(parsed.RecordNumber!.Value, ct) ?? throw MissingSubject();
            else container = await inventory.FindContainerAsync(parsed.Key!, ct) ?? throw MissingSubject();
            var type = container is null ? "Shipment" : "Container";
            var id = container?.Code ?? Number(shipment!.ShipmentId);
            var resources = new List<ResourceRef>();
            var locationCodes = container is null ? shipment!.Lines.Select(row => row.LocationCode) : container.Contents.Select(row => row.LocationCode);
            foreach (var locationCode in locationCodes.Distinct(StringComparer.Ordinal))
            {
                var location = await inventory.FindLocationAsync(locationCode, ct) ?? throw MissingSubject();
                resources.Add((await LocationResourceAsync(uow, location, ct)) with { Type = type, Id = id });
            }
            if(resources.Count==0)resources.Add(new(type,id));
            return Subject(new(parsed.Code, parsed.Namespace, parsed.Kind, null, null, null, null, container?.Status.ToString() ?? shipment!.Status.ToString(),
                SubjectKey: id, RecordNumber: shipment?.ShipmentId, IdentityKind: container?.Kind.ToString()),
                container is null ? parsed.Code : prefix + "container:" + CanonicalIdentification.EncodeKey(container.Code), type, id, Permissions.InventoryRead, resources.Distinct().ToArray());
        }
        if (parsed.Kind == "output")
        {
            var output = await uow.Store<IInventoryLookup>().FindTransformationOutputAsync(parsed.RecordNumber!.Value, parsed.LineNumber!.Value, ct) ?? throw MissingSubject();
            if (output.Line.Role is not (TransformationRole.Remnant or TransformationRole.Byproduct)) throw MissingSubject();
            var workCode = prefix + (output.OperationSequence is { } operation ? "operation:" + Number(output.OrderNumber) + ":" + Number(operation) : "order:" + Number(output.OrderNumber));
            var work = await ResolveSubjectFactsAsync(uow, workCode, ct);
            var resources = work.Resources.Select(resource => resource with { Type = "TransformationOutput", Id = Number(output.TransformationId) + "/" + Number(output.Line.LineNumber) }).ToList();
            if (output.Line.LocationCode is { } locationCode)
            {
                var location = await uow.Store<IInventoryLookup>().FindLocationAsync(locationCode, ct) ?? throw MissingSubject();
                resources.Add(await LocationResourceAsync(uow, location, ct));
            }
            // Tracked outputs share their material lock. Untracked output lines remain explicit source references, never invented lots.
            var canonical = output.Line.LotNumber is { } lot ? prefix + "material:" + CanonicalIdentification.EncodeKey(output.Line.PartNumber) + ":" + CanonicalIdentification.EncodeKey(lot) : parsed.Code;
            return Subject(new(parsed.Code, parsed.Namespace, parsed.Kind, output.OrderNumber, output.OperationSequence, output.Line.PartNumber, null, "Recorded",
                LotNumber: output.Line.LotNumber, IdentityKind: output.Line.LotNumber is null ? "UntrackedOutput" : output.Line.Role.ToString(),
                RecordNumber: output.TransformationId, LineNumber: output.Line.LineNumber), canonical, "TransformationOutput", Number(output.TransformationId) + "/" + Number(output.Line.LineNumber),
                Permissions.InventoryRead, resources, work.ActivityCode);
        }
        throw MissingSubject();
    }

    private void PermitSource(ForgePrincipal principal, string permission, IEnumerable<ResourceRef> resources)
    {
        foreach (var resource in resources)
        {
            var decision = authorizer.Authorize(principal, permission, resource);
            if (!decision.IsPermitted) throw new ForgeAuthorizationException(decision, resource);
        }
    }

    private static async Task<ResourceRef> LocationResourceAsync(IKernelUnitOfWork uow, LocationInfo location, CancellationToken ct)
    {
        var workCenter = location.WorkCenterCode;
        if (workCenter is null && location.MachineCode is { } machineCode)
            workCenter = (await uow.Store<IResourcesLookup>().FindRecordedMachineAsync(machineCode, ct))?.WorkCenterCode;
        return new("Location", location.Code, FacilityId: location.SiteCode, WorkCenterId: workCenter);
    }
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static CommandRejectedException MissingSubject() => new("identification_not_found", "Identification does not refer to an accessible source.");
}
