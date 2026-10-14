using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Kernel.WorkAssignments;
using Forge.Quality.Contracts;
using Forge.Quality.Qms;
using Forge.Resources.Contracts;

namespace Forge.Host.Identification;

public sealed record ScanOperationFacts(OrderInfo Order, OrderOperationInfo Operation, QmsPolicyContext PolicyContext,
    ResourceRef Resource, string Fingerprint, string Code, IReadOnlyList<ScanReadSource> Sources);

/// <summary>Same-transaction facts and mixed owner access, shared by commands and the Kernel participant.</summary>
public sealed class ScanSessionAuthority(IdentificationResolver identification, IQmsPolicyReader policies,
    IForgeAuthorizer authorizer, QmsService qms, QmsPolicyAdoptionService adoptions,
    IConfiguration configuration, ScannerOptions scanners, IWorkSourceResolver sources)
{
    public async Task<ScanOperationFacts> OperationAsync(IKernelUnitOfWork uow, ForgePrincipal principal,
        string? code, long? orderNumber, int? sequence, bool locked, bool requireRead, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(code))
        {
            var source = await identification.ResolveAuthorityPresentationAsync(uow, principal, code, ct);
            if (source.Display.OrderNumber is not { } resolvedOrder || source.Display.Sequence is not { } resolvedSequence)
                throw new CommandRejectedException("scan_operation_required", "Select an actual order operation.");
            if (orderNumber is { } suppliedOrder && suppliedOrder != resolvedOrder || sequence is { } suppliedSequence && suppliedSequence != resolvedSequence)
                throw new CommandRejectedException("scan_operation_mismatch", "The operation reference differs from the actual order and sequence.");
            orderNumber = resolvedOrder; sequence = resolvedSequence;
        }
        if (orderNumber is not > 0 || sequence is not > 0)
            throw new CommandRejectedException("scan_operation_required", "Supply a positive actual order number and operation sequence.");
        if (locked) await uow.Store<IOrderCommitmentStore>().LockAsync(orderNumber.Value, ct);
        var order = await uow.Store<IExecutionLookup>().FindOrderAsync(orderNumber.Value, ct) ?? throw Unavailable();
        var operation = order.Operations.FirstOrDefault(row => row.Sequence == sequence.Value) ?? throw Unavailable();
        var resources = uow.Store<IResourcesLookup>();
        var center = await resources.FindWorkCenterAsync(operation.WorkCenterCode, ct) ?? throw Unavailable();
        var area = await resources.FindSafetyAreaAsync(center.SafetyAreaCode, ct) ?? throw Unavailable();
        var resource = new ResourceRef("OrderOperation", order.OrderNumber.ToString(CultureInfo.InvariantCulture) + "/" + operation.Sequence.ToString(CultureInfo.InvariantCulture),
            order.ProgramId is { } program ? new ProgramId(program) : null, FacilityId: area.SiteCode,
            WorkCenterId: operation.WorkCenterCode, ContractId: order.ContractId?.ToString(CultureInfo.InvariantCulture));
        var readSources = new List<ScanReadSource> { new(Permissions.OrdersRead, resource) };
        if (requireRead)
        {
            var currentSource = await sources.ResolveAsync(uow, "OrderOperation", resource.Id!, ct) ?? throw Unavailable();
            if (currentSource.Resources.Count == 0) throw Unavailable();
            foreach (var currentResource in currentSource.Resources) Require(principal, currentSource.ReadPermission, currentResource);
            readSources.AddRange(currentSource.Resources.Select(currentResource => new ScanReadSource(currentSource.ReadPermission, currentResource)));
            resource = resource with { Markings = currentSource.Resources.Aggregate(resource.Markings, (markings, currentResource) => markings | currentResource.Markings) };
        }
        var documents = uow.Store<IDocumentLookup>();
        var requiredDocuments = (await documents.ListRequirementsAsync(order.PartNumber, order.Revision, false, ct))
            .Where(requirement => requirement.OperationSequence is null || requirement.OperationSequence == operation.Sequence)
            .Select(requirement => (Number: requirement.DocumentNumber, Revision: requirement.DocumentRevision))
            .Concat((await documents.ListLinksAsync(DocumentTargetType.ProductionOrder, order.OrderNumber.ToString(CultureInfo.InvariantCulture), null, false, ct))
                .Where(link => link.Purpose == DocumentPurpose.Follow).Select(link => (Number: link.DocumentNumber, Revision: link.Revision))).Distinct();
        var documentConfiguration = new List<object>();
        foreach (var required in requiredDocuments)
        {
            var document = await documents.FindDocumentAsync(required.Number, ct) ?? throw Unavailable();
            var revision = required.Revision is { } pinned ? document.Revisions.FirstOrDefault(row => row.Revision == pinned) : document.Current;
            if (revision is null) throw Unavailable();
            var documentResource = new ResourceRef("DocumentRevision", document.Number + "/" + revision.Revision,
                document.ProgramId is { } documentProgram ? new ProgramId(documentProgram) : null, document.Markings);
            if (requireRead) Require(principal, Permissions.EngineeringRead, documentResource);
            readSources.Add(new(Permissions.EngineeringRead, documentResource));
            documentConfiguration.Add(new { document.Number, document.Authority, revision.Revision, revision.FileSha256 });
        }
        resource = resource with { Markings = readSources.Aggregate(resource.Markings, (marks, source) => marks | source.Resource.Markings) };
        var context = new QmsPolicyContext(area.SiteCode, order.ProgramId, operation.WorkCenterCode, operation.ActivityCode,
            OperationSequence: operation.Sequence, PartNumber: order.PartNumber, PartRevision: order.Revision, SubjectKind: "operation");
        var fingerprint = CanonicalJson.Fingerprint(new { order.OrderNumber, order.PartNumber, order.Revision, order.ProgramId, order.ReleasedAt,
            operation.Sequence, operation.Description, operation.WorkCenterCode, operation.ActivityCode, operation.EligibleMachines,
            operation.RequiredQualifications, operation.PlannedMinutes, operation.CrewSize, order.Materials, RequiredDocuments = documentConfiguration });
        var ns = configuration[CanonicalIdentification.NamespaceConfigurationKey];
        var canonical = CanonicalIdentification.IsValidNamespace(ns)
            ? "forge:v1:" + ns + ":operation:" + order.OrderNumber.ToString(CultureInfo.InvariantCulture) + ":" + operation.Sequence.ToString(CultureInfo.InvariantCulture)
            : "order:" + order.OrderNumber.ToString(CultureInfo.InvariantCulture) + "/" + operation.Sequence.ToString(CultureInfo.InvariantCulture);
        return new(order, operation, context, resource, fingerprint, canonical, readSources.Distinct().ToArray());
    }

    public async Task<CommandAuthority> SessionAuthorityAsync(IKernelUnitOfWork uow, ForgePrincipal principal, Guid id, CancellationToken ct)
    {
        var session = await FindAsync(uow, id, ct);
        if (session.PrincipalPersonId != principal.PersonId.Value) throw Unavailable();
        var facts = await OperationAsync(uow, principal, null, session.OrderNumber, session.Sequence, true, true, ct);
        foreach (var source in session.Sources.Concat(session.Selections.SelectMany(selection => selection.Sources)))
            Require(principal, source.Permission, source.Resource);
        return new(facts.Resource with { Type = "ScanSession", Id = id.ToString("D"), Markings = session.Markings },
            "operator:" + session.OperatorPersonId.ToString("D"));
    }

    public async Task<(ScanSessionView Session, QmsAdoptedPolicy Policy, ScanOperationFacts Facts)> RequireActiveAsync(
        IKernelUnitOfWork uow, CommandContext context, Guid id, int version, DateTimeOffset now, CancellationToken ct)
    {
        var principal = await uow.People.LoadPrincipalAsync(context.Principal.PersonId, ct) is { IsActive: true } current ? current : throw Unavailable();
        var session = await FindAsync(uow, id, ct);
        var channel = ScanSessionRules.Channel(principal, context.Authentication);
        var store = uow.Store<IScanSessionStore>();
        var facts = await OperationAsync(uow, principal, null, session.OrderNumber, session.Sequence, false, true, ct);
        var machine = await MachineAsync(uow, principal, facts, session.SelectedMachineCode, ct);
        var worker = await uow.People.LoadPrincipalAsync(new PersonId(session.OperatorPersonId), ct);
        if (worker is not { IsActive: true } || facts.Order.ProgramId is { } workerProgram
            && !worker.Programs.Contains(new ProgramId(workerProgram))) throw Unavailable();
        var policy = await policies.RequireAsync(uow, principal, QmsPolicyPurpose.Execution, facts.PolicyContext with { MachineCode = machine }, now, ct);
        ScanSessionRules.RequireAuthentication(policy, principal, session.OperatorPersonId, context.Authentication, now);
        await store.LockChannelAsync(channel, ct);
        session = await FindAsync(uow, id, ct);
        ScanSessionRules.RequireActive(session, await store.CurrentAsync(channel, ct) ?? Guid.Empty, channel, principal.PersonId.Value, version, now);
        if (session.AuthenticationBindingHash != ScanSessionRules.AuthenticationBinding(principal, context.Authentication!))
            throw new CommandRejectedException("scan_authenticated_session_changed", "The validated provider session or authenticated operator changed.");
        if (session.Policy.Fingerprint != policy.Fingerprint)
            throw new CommandRejectedException("scan_context_changed", "The exact operation or approved policy changed. Open a new session.");
        foreach (var recorded in session.Sources) Require(principal, recorded.Permission, recorded.Resource);
        foreach (var selection in session.Selections)
        {
            scanners.Require(selection.InputMode, selection.AdapterId, selection.Symbology, ScanSessionRules.ManualAllowed(policy));
            foreach (var recorded in selection.Sources) Require(principal, recorded.Permission, recorded.Resource);
            var source = await identification.ResolveAuthorityPresentationAsync(uow, principal, selection.Code, ct);
            if (ScanSessionRules.SourceFingerprint(source) != selection.SourceFingerprint)
                throw new CommandRejectedException("scan_selection_changed", "An exact selected source changed. Open a new session.");
            RequireMatchingSource(selection.Kind, source, facts);
            if (selection.Kind == "ControlledInstruction") await RequireInstructionForOperationAsync(uow, source, facts, ct);
            if (selection.Kind == "Material") await RequireMaterialForOperationAsync(uow, source, facts, machine, ct);
        }
        if (session.OperationFingerprint != facts.Fingerprint)
            throw new CommandRejectedException("scan_context_changed", "The actual operation or its required controlled configuration changed. Open a new session.");
        return (session, policy, facts);
    }

    public async Task<string?> MachineAsync(IKernelUnitOfWork uow, ForgePrincipal principal, ScanOperationFacts facts,
        string? requested, CancellationToken ct)
    {
        var eligible = facts.Operation.EligibleMachines;
        if (eligible.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(requested)) throw new CommandRejectedException("scan_machine_mismatch", "This actual operation has no machine.");
            return null;
        }
        var machine = string.IsNullOrWhiteSpace(requested) ? eligible.Count == 1 ? eligible[0]
            : throw new CommandRejectedException("scan_machine_required", "Select the actual eligible machine before evaluating its governed policy.")
            : BusinessCode.Require(requested, "Machine");
        if (!eligible.Contains(machine, StringComparer.Ordinal)
            || await uow.Store<IResourcesLookup>().FindMachineAsync(machine, ct) is not { Active: true } actual
            || actual.WorkCenterCode != facts.Operation.WorkCenterCode)
            throw new CommandRejectedException("scan_machine_mismatch", "The selected machine is unavailable or not eligible for this operation.");
        Require(principal, Permissions.ResourcesRead, facts.Resource with { Type = "Machine", Id = machine });
        return machine;
    }

    public async Task<bool> CanReadAsync(IKernelUnitOfWork uow, ForgePrincipal principal, ScanSessionView session, CancellationToken ct)
    {
        try
        {
            var facts = await OperationAsync(uow, principal, null, session.OrderNumber, session.Sequence, false, true, ct);
            if (principal.PersonId.Value != session.PrincipalPersonId)
            {
                foreach (var owner in session.Sources.Where(source => source.Permission == Permissions.OrdersRead)
                    .Select(source => source.Resource).Append(facts.Resource).Distinct())
                    Require(principal, Permissions.AuditRead, owner with { Type = "ScanSession", Id = session.Id.ToString("D"),
                        Markings = owner.Markings | session.Markings });
            }
            foreach (var source in session.Sources.Concat(session.Selections.SelectMany(selection => selection.Sources)))
            {
                Require(principal, source.Permission, source.Resource);
                if (Forge.Host.WorkInbox.OwnedSourceFamilies.Contains(source.Resource.Type)
                    && !await Forge.Host.WorkInbox.OwnedSourceFamilies.CanReadAsync(uow, sources, principal,
                        source.Resource.Type, source.Resource.Id!, ct)) return false;
                if (source.Resource.Type is "QmsRecord" or "QmsRevision")
                {
                    var split = source.Resource.Id!.LastIndexOf('/');
                    var code = source.Resource.Type == "QmsRevision" ? source.Resource.Id[..split] : source.Resource.Id;
                    var revision = source.Resource.Type == "QmsRevision" ? int.Parse(source.Resource.Id.AsSpan(split + 1), CultureInfo.InvariantCulture) : (int?)null;
                    var record = await uow.Store<IQmsLookup>().FindAsync(code, ct);
                    if (record is null || !await qms.CanReadAsync(uow, principal, record, ct, revision)) return false;
                }
                if (source.Resource.Type is "QmsPolicyAdoption" or "QmsPolicyAdoptionRevision")
                {
                    var split = source.Resource.Id!.LastIndexOf('/');
                    var code = source.Resource.Type == "QmsPolicyAdoptionRevision" ? source.Resource.Id[..split] : source.Resource.Id;
                    var revision = source.Resource.Type == "QmsPolicyAdoptionRevision" ? int.Parse(source.Resource.Id.AsSpan(split + 1), CultureInfo.InvariantCulture) : (int?)null;
                    var adoption = await uow.Store<IQmsPolicyAdoptionLookup>().FindAsync(code, ct);
                    if (adoption is null || !await adoptions.CanReadAsync(uow, principal, adoption, ct, revision)) return false;
                }
            }
            foreach (var selection in session.Selections)
                _ = await identification.ResolveAuthoritySubjectAsync(uow, principal, selection.SubjectCode, ct);
            return true;
        }
        catch (ForgeAuthorizationException) { return false; }
        catch (CommandRejectedException) { return false; }
        catch (IdentificationException) { return false; }
    }

    public static void RequireMatchingSource(string kind, IdentificationSubject source, ScanOperationFacts facts)
    {
        if (kind is "Order" or "Operation" && (source.Display.OrderNumber != facts.Order.OrderNumber
            || kind == "Operation" && source.Display.Sequence != facts.Operation.Sequence))
            throw new CommandRejectedException("scan_operation_mismatch", "This selection belongs to a different actual order or operation.");
        if (kind == "WorkCenter" && source.SourceId != facts.Operation.WorkCenterCode)
            throw new CommandRejectedException("scan_work_center_mismatch", "The selected work center does not own this operation.");
        if (kind == "Machine" && !facts.Operation.EligibleMachines.Contains(source.MachineCode ?? source.SourceId, StringComparer.Ordinal))
            throw new CommandRejectedException("scan_machine_mismatch", "The selected machine is not eligible for this actual operation.");
        if (kind == "Material" && source.Display.PartNumber != facts.Order.PartNumber
            && !facts.Order.Materials.Any(material => material.ComponentPartNumber == source.Display.PartNumber))
            throw new CommandRejectedException("scan_material_mismatch", "The material is not an actual input or output part for this order.");
    }

    public static async Task RequireInstructionForOperationAsync(IKernelUnitOfWork uow, IdentificationSubject source,
        ScanOperationFacts facts, CancellationToken ct)
    {
        var documents = uow.Store<IDocumentLookup>();
        var requirements = (await documents.ListRequirementsAsync(facts.Order.PartNumber, facts.Order.Revision, false, ct))
            .Where(requirement => requirement.OperationSequence is null || requirement.OperationSequence == facts.Operation.Sequence)
            .Select(requirement => (Number: requirement.DocumentNumber, Revision: requirement.DocumentRevision))
            .Concat((await documents.ListLinksAsync(DocumentTargetType.ProductionOrder,
                facts.Order.OrderNumber.ToString(CultureInfo.InvariantCulture), null, false, ct))
                .Where(link => link.Purpose == DocumentPurpose.Follow).Select(link => (Number: link.DocumentNumber, Revision: link.Revision)));
        if (!requirements.Any(requirement => source.Display.SubjectKey == requirement.Number
            && (requirement.Revision is null || requirement.Revision == source.Display.Revision)))
            throw new CommandRejectedException("scan_instruction_mismatch", "The instruction is not a required controlled document for this actual operation.");
    }

    public static async Task RequireAllInstructionsAsync(IKernelUnitOfWork uow, ScanSessionView session,
        QmsAdoptedPolicy policy, ScanOperationFacts facts, CancellationToken ct)
    {
        if (policy.OperationRule?.RequireInstructionRevision != true
            && !ScanSessionRules.RequiredKinds(policy).Contains(QmsRequiredScanKind.ControlledInstruction)) return;
        var lookup = uow.Store<IDocumentLookup>();
        var requirements = (await lookup.ListRequirementsAsync(facts.Order.PartNumber, facts.Order.Revision, false, ct))
            .Where(requirement => requirement.OperationSequence is null || requirement.OperationSequence == facts.Operation.Sequence)
            .Select(requirement => (Number: requirement.DocumentNumber, Revision: requirement.DocumentRevision))
            .Concat((await lookup.ListLinksAsync(DocumentTargetType.ProductionOrder,
                facts.Order.OrderNumber.ToString(CultureInfo.InvariantCulture), null, false, ct))
                .Where(link => link.Purpose == DocumentPurpose.Follow).Select(link => (Number: link.DocumentNumber, Revision: link.Revision))).Distinct();
        foreach (var requirement in requirements)
        {
            var current = (await lookup.FindDocumentAsync(requirement.Number, ct))?.Current;
            var expectedRevision = requirement.Revision ?? current?.Revision;
            if (expectedRevision is null || !session.Selections.Where(selection => selection.Kind == "ControlledInstruction")
                .Select(selection => CanonicalIdentification.Parse(selection.SubjectCode, selection.SubjectCode.Split(':')[2]))
                .Any(parsed => parsed.Key == requirement.Number && parsed.Revision == expectedRevision))
                throw new CommandRejectedException("scan_required_instruction_missing", "Stage every exact controlled instruction required by the actual order operation.");
        }
    }

    public static async Task RequireMaterialForOperationAsync(IKernelUnitOfWork uow, IdentificationSubject source,
        ScanOperationFacts facts, string? machine, CancellationToken ct)
    {
        var part = source.Display.PartNumber; var lot = source.Display.LotNumber;
        if (part is null || lot is null) throw new CommandRejectedException("scan_material_mismatch", "Stage an exact recorded input material identity.");
        var lookup = uow.Store<IInventoryLookup>();
        async Task<bool> AtOperationAsync(string? code)
        {
            if (code is null || await lookup.FindLocationAsync(code, ct) is not { Active: true, PointOfUse: true } location) return false;
            return location.WorkCenterCode == facts.Operation.WorkCenterCode && (location.MachineCode is null || location.MachineCode == machine);
        }
        foreach (var task in (await lookup.ListMaterialTasksAsync(false, facts.Order.OrderNumber, ct))
            .Where(task => task.PartNumber == part && task.LotNumber == lot && task.Status == MaterialTaskStatus.Delivered
                && task.TransactionId is not null && (task.PickedQuantity ?? task.Quantity) > 0))
        {
            if (!await AtOperationAsync(task.ToLocation)) continue;
            var delivered = await uow.Store<IReceivingLookup>().FindTransactionAsync(task.TransactionId!.Value, ct);
            if (delivered is { Kind: InventoryTransactionKind.Transfer } && delivered.PartNumber == part && delivered.LotNumber == lot
                && delivered.ToLocation == task.ToLocation && delivered.Quantity > 0) return;
        }
        var lines = await uow.Store<IAsBuiltRecord>().ListAsync(facts.Order.OrderNumber, part, lot, ct);
        var atOperation = new List<AsBuiltLine>();
        foreach (var line in lines)
        {
            var transaction = await uow.Store<IReceivingLookup>().FindTransactionAsync(line.TransactionId, ct);
            if (transaction is not null && await AtOperationAsync(line.Kind == AsBuiltKind.Issued ? transaction.FromLocation : transaction.ToLocation))
                atOperation.Add(line);
        }
        if (atOperation.Sum(line => line.Kind == AsBuiltKind.Issued ? line.Quantity : -line.Quantity) > 0) return;
        throw new CommandRejectedException("scan_material_not_delivered", "This exact lot has no recorded delivered or net issued input at the actual operation's point of use.");
    }

    private void Require(ForgePrincipal principal, string permission, ResourceRef resource)
    {
        var decision = authorizer.Authorize(principal, permission, resource);
        if (!decision.IsPermitted) throw new ForgeAuthorizationException(decision, resource);
    }
    private static async Task<ScanSessionView> FindAsync(IKernelUnitOfWork uow, Guid id, CancellationToken ct) =>
        await uow.Store<IScanSessionStore>().FindAsync(id, ct) ?? throw Unavailable();
    private static CommandRejectedException Unavailable() => new("scan_session_unavailable", "The current source or registered operator context is unavailable.");
}
